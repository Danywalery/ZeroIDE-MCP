using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using ZeroIDE.Mcp.ConnectionsV2;

namespace ZeroIDE.Mcp;

internal sealed partial class TrayContext
{
    private ConnectionCatalog? _connections;
    private string _connectionsError = "";
    private ConnectionVerification? _connectionProof;
    private string _connectionCheck = "";
    private bool _connectionAction;
    private ManagedConnectionProcess? _activeConnectionProcess, _candidateConnectionProcess, _previousConnectionProcess;
    private bool _previousLegacyConnection;
    private string ConnectionStorePath => Path.Combine(Path.GetDirectoryName(_cfgPath)!, "connections-v2.json");
    private ConnectionStore ConnectionRegistry => new(ConnectionStorePath);
    private bool UniversalRuntime => _connections?.RuntimeEnabled == true;
    internal string ConnectionUrl => UniversalRuntime ? _connections!.Active.Endpoint : ConnectionMode == "local" ? $"http://127.0.0.1:{_cfg.Port}/mcp" : PublicMcpUrl;
    internal string ConnectionLabel => UniversalRuntime ? _connections!.Active.Name : ConnectionMode == "local" ? I18n.T("On this PC", "На этом ПК") : ConnectionMode == "ssh" ? I18n.T("Existing connection", "Существующее подключение") : ConnectionMode;
    internal string ConnectionControl => UniversalRuntime ? _connections!.Active.Ownership switch {
        ConnectionOwnership.Local => I18n.T("Local access. No tunnel required.", "Локальный доступ. Туннель не нужен."),
        ConnectionOwnership.External => I18n.T("External service. Its launch and account are controlled outside this app.", "Внешний сервис. Его запуск и аккаунт управляются вне приложения."),
        _ => I18n.T("App-managed. Starts and stops together with MCP.", "Под управлением приложения. Запускается и выключается вместе с MCP.")
    } : ConnectionMode == "local" ? I18n.T("Available only to applications on this PC.", "Доступно только приложениям на этом ПК.") : I18n.T("Managed tunnel · starts and stops with MCP.", "Управляемый туннель · включается и выключается вместе с MCP.");
    internal string ConnectionCheck => !string.IsNullOrEmpty(_connectionsError) ? _connectionsError : !string.IsNullOrEmpty(_connectionCheck) ? _connectionCheck : I18n.T("Not verified. A running process does not prove that a client can connect.", "Не проверено. Запущенный процесс ещё не означает, что клиент может подключиться.");
    internal string ConnectionTransportStatus => !McpEnabled ? I18n.T("Transport off with MCP", "Транспорт выключен вместе с MCP") : NetworkState switch {
        "local" => I18n.T("Local access · no transport process", "Локальный доступ · без процесса туннеля"),
        "external" => I18n.T("External service · process not controlled here", "Внешний сервис · процесс не контролируется здесь"),
        "running" or "ready" => I18n.T("Transport process running", "Процесс транспорта запущен"),
        "error" => I18n.T("Transport error · check settings and log", "Ошибка транспорта · проверь настройки и журнал"),
        _ => I18n.T("Transport starting", "Транспорт запускается") };
    internal bool CanRestartConnection => UniversalRuntime ? _connections!.Active.Ownership == ConnectionOwnership.Managed : ConnectionMode != "local";
    internal bool HasPreviousConnection => _previousLegacyConnection || _previousConnectionProcess != null;

    private void LoadConnectionRegistry()
    {
        if (!File.Exists(ConnectionStorePath)) return;
        try { _connections = ConnectionRegistry.Read(); }
        catch { _connectionsError = I18n.T("Connection registry is invalid. Restore its backup; no new transport will start.", "Настройки подключений повреждены. Восстанови резервную копию; новое подключение не запустится."); }
    }
    private ConnectionCatalog EnsureConnectionRegistry()
    {
        if (_connectionsError.Length > 0) throw new InvalidOperationException(_connectionsError);
        if (_connections != null)
        {
            _connections = ConnectionRegistry.Read();
            if (!_connections.RuntimeEnabled && _connections.Pending == null)
            {
                using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(_cfg));
                var inventory = LegacyConnectionImport.Read(snapshot.RootElement);
                if (_connections.Active.Fingerprint() != inventory.Active.Fingerprint())
                    _connections = ConnectionRegistry.RefreshLegacyInventory(inventory, _connections.Revision);
            }
            return _connections;
        }
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(_cfg));
        return _connections = ConnectionRegistry.Initialize(LegacyConnectionImport.Read(legacy.RootElement));
    }
    private ConnectionProfile CurrentConnectionProfile() => UniversalRuntime ? _connections!.Active : new ConnectionProfile {
        Id = "current-check", Name = ConnectionLabel, Scope = ConnectionMode == "local" ? AccessScope.Local : AccessScope.Public,
        Ownership = ConnectionMode == "local" ? ConnectionOwnership.Local : ConnectionOwnership.External,
        Endpoint = ConnectionUrl, Adapter = "external"
    };
    private string ReadMcpCredential()
    {
        // Values stay in memory; never sent back by the local API or written into profiles.
        string token = Environment.GetEnvironmentVariable("MCP_TOKEN") ?? "";
        if (token.Length > 0) return token;
        string file = Path.Combine(_cfg.ServerDir, "config.env");
        foreach (string line in File.ReadLines(file))
        {
            string value = line.Trim();
            if (!value.StartsWith("MCP_TOKEN=", StringComparison.Ordinal)) continue;
            return value[10..].Trim().Trim('"', '\'');
        }
        throw new InvalidOperationException(I18n.T("An MCP credential is required for the authenticated check.", "Для проверки авторизации нужен действующий токен MCP."));
    }
    private async Task<ConnectionVerification> CheckConnectionProfile(ConnectionProfile profile, string operation)
    {
        if (!McpEnabled) throw new InvalidOperationException(I18n.T("Enable MCP first.", "Сначала включи MCP."));
        int epoch = _networkEpoch;
        var proof = await ConnectionVerifier.CheckAsync(profile, operation, new Uri($"http://127.0.0.1:{_cfg.Port}"), _cfg.ControlSecret, ReadMcpCredential());
        if (epoch != _networkEpoch || !McpEnabled) throw new InvalidOperationException("Server state changed during verification.");
        return proof;
    }
    internal async Task CheckConnectionFromUi()
    {
        if (_connectionAction) return;
        if (!McpEnabled) { _connectionCheck = I18n.T("Enable MCP before checking the connection.", "Перед проверкой связи включи MCP."); return; }
        _connectionAction = true; _connectionCheckResult = "";
        string fingerprint = CurrentConnectionProfile().Fingerprint();
        _connectionCheck = I18n.T("Checking server identity and MCP authorization…", "Проверяю принадлежность сервера и авторизацию MCP…");
        try
        {
            var proof = await CheckConnectionProfile(CurrentConnectionProfile(), "current");
            if (fingerprint != CurrentConnectionProfile().Fingerprint()) return;
            _connectionCheckResult = "ok";
            _connectionCheck = I18n.T("MCP check passed from this PC at ", "MCP проверен с этого ПК в ") + proof.CheckedAt.ToLocalTime().ToString("HH:mm") + I18n.T(". Remote client access still needs a test in that client.", ". Доступ удалённого клиента проверь в самом клиенте.");
        }
        catch { _connectionCheckResult = "fail"; _connectionCheck = I18n.T("Check failed: confirm the URL, tunnel, MCP credential and server version. The current connection is unchanged.", "Проверка не пройдена: проверь ссылку, туннель, токен MCP и версию сервера. Текущее подключение не изменено."); }
        finally { _connectionAction = false; }
    }
    private string ApprovalFingerprint(ConnectionProfile profile)
    {
        var launch = profile.Launch ?? throw new InvalidOperationException("Managed launch is missing.");
        string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(launch.Fingerprint() + HashFile(launch.Executable) + (launch.ConfigurationFile.Length > 0 ? HashFile(launch.ConfigurationFile) : ""))));
    }
    private string ApprovalPath(ConnectionProfile profile) => Path.Combine(Path.GetDirectoryName(_cfgPath)!, "connection-approvals", profile.Id + ".approval");
    private bool IsLaunchApproved(ConnectionProfile profile)
    {
        try { return File.ReadAllText(ApprovalPath(profile)) == ApprovalFingerprint(profile); } catch { return false; }
    }
    private void ApproveLaunch(ConnectionProfile profile)
    {
        string fingerprint = ApprovalFingerprint(profile);
        using var consent = new ConnectionLaunchConsentDialog(profile);
        if (consent.ShowDialog() != DialogResult.Yes) throw new InvalidOperationException("Owner declined transport launch.");
        if (fingerprint != ApprovalFingerprint(profile)) throw new InvalidOperationException("Transport changed while approval was pending.");
        TunnelProfiles.WritePrivate(ApprovalPath(profile), fingerprint);
    }
    private ManagedConnectionProcess StartApprovedConnection(ConnectionProfile profile)
    {
        profile.Validate();
        if (!IsLaunchApproved(profile)) throw new InvalidOperationException("Transport launch needs owner approval; executable or config may have changed.");
        var launch = profile.Launch!;
        var psi = new ProcessStartInfo(launch.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = launch.WorkingDirectory.Length > 0 ? launch.WorkingDirectory : Path.GetDirectoryName(launch.Executable)! };
        foreach (string value in launch.Arguments) psi.ArgumentList.Add(value);
        return new ManagedConnectionProcess(psi);
    }
    private void StartUniversalConnection()
    {
        _tunnelLastStart = DateTime.Now;
        var profile = _connections!.Active;
        if (profile.Ownership != ConnectionOwnership.Managed) { _networkState = profile.Ownership == ConnectionOwnership.Local ? "local" : "external"; return; }
        try { _activeConnectionProcess?.Dispose(); _activeConnectionProcess = StartApprovedConnection(profile); _networkState = "running"; }
        catch { _networkState = "error"; _connectionCheck = I18n.T("The connection could not start. Check settings and owner approval.", "Подключение не запустилось. Проверь настройки и разрешение владельца."); }
    }
    private void TickUniversalConnection()
    {
        if (!McpEnabled || _connections!.Active.Ownership != ConnectionOwnership.Managed) return;
        if (_activeConnectionProcess?.Running == true) return;
        _networkState = "error";
        if ((DateTime.Now - _tunnelLastStart).TotalSeconds >= Math.Min(60, 5 + _tunnelRestarts * 5)) { _tunnelRestarts++; StartUniversalConnection(); }
    }
    private void StopUniversalConnections()
    {
        _candidateConnectionProcess?.Dispose(); _candidateConnectionProcess = null;
        _activeConnectionProcess?.Dispose(); _activeConnectionProcess = null;
        _previousConnectionProcess?.Dispose(); _previousConnectionProcess = null;
        _previousLegacyConnection = false;
        _connectionProof = null;
    }
    internal void RetirePreviousConnectionFromUi()
    {
        if (!HasPreviousConnection) return;
        if (MessageBox.Show(I18n.T("Has your client connected using the new URL? Stop the old managed transport now?", "AI уже подключился по новой ссылке? Отключить прежнее подключение?"), Theme.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (_previousLegacyConnection) { KillTree(_tunnel); _tunnel = null; _previousLegacyConnection = false; }
        _previousConnectionProcess?.Dispose(); _previousConnectionProcess = null;
    }
    private object ConnectionApiState()
    {
        var c = EnsureConnectionRegistry();
        object Summary(ConnectionProfile p) => new { p.Id, p.Name, p.Scope, p.Ownership, p.Adapter, p.Endpoint, p.AddressKind, launchFingerprint = p.Launch?.Fingerprint() };
        return new { schemaVersion = 1, revision = c.Revision, runtimeEnabled = c.RuntimeEnabled, active = Summary(c.Active), currentUrl = ConnectionUrl,
            serverEnabled = McpEnabled, processState = NetworkState, verification = ConnectionCheck, verificationVantage = "this-pc", remoteClientVerified = ClientSeen(),
            retainedPrevious = HasPreviousConnection, candidateVerifiedAt = _connectionProof?.CheckedAt, profiles = c.Profiles.Select(Summary), pending = c.Pending == null ? null : new { c.Pending.OperationId, profile = Summary(c.Pending.Profile) },
            guide = new { taskId = _guideTask, title = _guideTitle, waitingFor = _guideWaiting, updatedAt = _guideUpdated, steps = _guideSteps ?? [] },
            actions = new[] { "prepare", "start", "address", "check", "activate", "cancel", "progress", "note", "secret-request" },
            warning = "Activation and managed launch require owner approval. Credentials are never accepted in a profile. No remote-client success is inferred from a local check." };
    }
    internal async Task<(int, string)> HandleConnectionApi(string method, string path, string body)
    {
        if (_connectionAction) return (409, "{\"error\":\"connection action busy\"}");
        _connectionAction = true;
        try
        {
            if (method == "GET" && path == "/connections/state") return (200, JsonSerializer.Serialize(ConnectionApiState()));
            if (method != "POST") return (405, "{\"error\":\"method not allowed\"}");
            using var d = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = d.RootElement;
            if (path is "/connections/progress" or "/connections/note" or "/connections/secret-request")
                return (200, JsonSerializer.Serialize(HandleConnectionGuide(path, root)));
            long revision = root.GetProperty("revision").GetInt64();
            var c = EnsureConnectionRegistry();
            if (c.Revision != revision) throw new InvalidOperationException("Revision conflict. Reload state.");
            string operation = root.TryGetProperty("operationId", out var op) ? op.GetString() ?? "" : "";
            var pending = c.Pending;
            if (path != "/connections/prepare" && (pending == null || pending.OperationId != operation)) throw new InvalidOperationException("Pending operation mismatch.");
            switch (path)
            {
                case "/connections/prepare":
                    var profile = JsonSerializer.Deserialize<ConnectionProfile>(root.GetProperty("profile").GetRawText(), new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })!.Validate();
                    if (profile.Id.StartsWith("legacy-", StringComparison.OrdinalIgnoreCase) || profile.Adapter.StartsWith("legacy-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Reserved legacy profile. Create a new independent route.");
                    _connections = ConnectionRegistry.Prepare(profile, revision, DateTimeOffset.UtcNow); _connectionProof = null; break;
                case "/connections/start":
                    if (!McpEnabled || pending!.Profile.Ownership != ConnectionOwnership.Managed) throw new InvalidOperationException("Managed candidate and enabled MCP required.");
                    if (_candidateConnectionProcess != null) throw new InvalidOperationException("Candidate already started.");
                    ApproveLaunch(pending.Profile); _launchApprovedOperation = !ConnectionIsPublic ? operation : "";
                    _candidateConnectionProcess = StartApprovedConnection(pending.Profile); break;
                case "/connections/address":
                    _connections = ConnectionRegistry.UpdatePendingEndpoint(operation, revision, root.GetProperty("endpoint").GetString() ?? ""); _connectionProof = null; break;
                case "/connections/check":
                    _connectionProof = await CheckConnectionProfile(pending!.Profile, operation); break;
                case "/connections/activate":
                    if (HasPreviousConnection) throw new InvalidOperationException("Retire or restore the previous route before another handover.");
                    if (_connectionProof == null) throw new InvalidOperationException("Check the candidate first.");
                    if (pending!.Profile.Ownership == ConnectionOwnership.Managed && _candidateConnectionProcess?.Running != true) throw new InvalidOperationException("Start the managed candidate first.");
                    if (pending.Profile.Ownership == ConnectionOwnership.Managed && !IsLaunchApproved(pending.Profile)) throw new InvalidOperationException("The approved program or configuration changed. Cancel and prepare a fresh candidate with owner approval.");
                    if (!UniversalRuntime && ConnectionMode != "local" && pending.Profile.Ownership == ConnectionOwnership.External && pending.Profile.Endpoint == ConnectionUrl) throw new InvalidOperationException("This URL depends on the current app-managed tunnel. Do not relabel it as external.");
                    if (!ConnectionIsPublic)
                    {
                        // First launch consent also approves activation. Bind it to this operation
                        // and re-check the executable/config fingerprint before consuming it.
                        if (_launchApprovedOperation != operation || pending.Profile.Ownership != ConnectionOwnership.Managed || !IsLaunchApproved(pending.Profile))
                        {
                            using var consent = new ConnectionLaunchConsentDialog(pending.Profile);
                            if (consent.ShowDialog() != DialogResult.Yes) throw new InvalidOperationException("Owner declined activation.");
                        }
                    }
                    else if (MessageBox.Show(I18n.T("Switch the MCP URL?\n\nFrom: ", "Переключить ссылку MCP?\n\nБыло: ") + ConnectionUrl + I18n.T("\nTo: ", "\nСтанет: ") + pending.Profile.Endpoint + I18n.T("\n\nUpdate the URL in your AI client. The previous app-managed connection keeps working until you stop it.", "\n\nОбнови ссылку в AI-клиенте. Прежнее подключение под управлением приложения работает, пока ты его не отключишь."), Theme.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) throw new InvalidOperationException("Owner declined activation.");
                    bool wasUniversal = UniversalRuntime;
                    _connections = ConnectionRegistry.CommitSelection(operation, revision, _connectionProof, DateTimeOffset.UtcNow);
                    _previousConnectionProcess = _activeConnectionProcess;
                    _previousLegacyConnection = !wasUniversal && _tunnel != null && !_tunnel.HasExited;
                    _activeConnectionProcess = _candidateConnectionProcess; _candidateConnectionProcess = null;
                    _connectionProof = null; _launchApprovedOperation = ""; ResetConnectionState(); _connectionCheckResult = "ok";
                    _networkState = _connections.Active.Ownership == ConnectionOwnership.Managed ? "running" : _connections.Active.Ownership == ConnectionOwnership.Local ? "local" : "external";
                    _connectionCheck = I18n.T("New URL verified from this PC. Connect your client, then stop the previous route.", "Новая ссылка проверена с этого ПК. Подключи AI, затем отключи прежнее подключение."); break;
                case "/connections/cancel":
                    _connections = ConnectionRegistry.Cancel(operation, revision); _candidateConnectionProcess?.Dispose(); _candidateConnectionProcess = null; _connectionProof = null; break;
                default: return (404, "{\"error\":\"unknown connection action\"}");
            }
            return (200, JsonSerializer.Serialize(ConnectionApiState()));
        }
        catch (Exception ex)
        {
            Log("connection action failed: " + ex.GetType().Name);
            // Do not serialize process errors, response bodies, paths or credentials.
            return (409, JsonSerializer.Serialize(new { error = "Action failed or was declined. Reload state; check revision, pending operation, URL, owner approval and transport configuration. The active route was not silently replaced." }));
        }
        finally { _connectionAction = false; }
    }
    internal async Task ChangeConnectionAddressFromUi(IWin32Window owner)
    {
        if (_connectionAction) return;
        using var dialog = new ConnectionAddressDialog(ConnectionUrl);
        var choice = dialog.ShowDialog(owner);
        if (choice == DialogResult.Retry) { CopyConnectionAgentTask("url"); MessageBox.Show(I18n.T("Task copied. Paste it into your agent on this PC.", "Задание скопировано. Вставь его агенту на этом ПК."), Theme.ProductName); return; }
        if (choice != DialogResult.OK) return;
        _connectionAction = true;
        try
        {
            var c = EnsureConnectionRegistry();
            var profile = UniversalRuntime ? c.Active with { Endpoint = dialog.Endpoint } : new ConnectionProfile { Id = "external-" + Guid.NewGuid().ToString("N")[..8], Name = dialog.ConnectionName, Endpoint = dialog.Endpoint, Scope = AccessScope.Public, Ownership = ConnectionOwnership.External, Adapter = "external" };
            // Legacy provider URL edit keeps its existing launch ownership and credentials.
            if (!UniversalRuntime && ConnectionMode is "ngrok" or "cloudflare" or "ssh")
            {
                profile.Validate(requireEndpoint: true);
                // Verify first while the old config and live process remain untouched.
                if (new Uri(dialog.Endpoint).AbsolutePath != "/mcp") throw new InvalidOperationException("A custom path needs agent setup.");
                await CheckConnectionProfile(profile, "legacy-url-change");
                string origin = TunnelProfiles.Origin(dialog.Endpoint);
                if (MessageBox.Show(I18n.T("The new URL was checked from this PC. Save it? Update it in your AI afterwards. This does not create a provider domain or restart the working connection.\n\n", "Новая ссылка проверена с этого ПК. Сохранить её? Затем обнови ссылку в AI. Это не создаёт домен и не перезапускает действующее подключение.\n\n") + dialog.Endpoint, Theme.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var updated = JsonSerializer.Deserialize<TrayConfig>(JsonSerializer.Serialize(_cfg))!;
                if (ConnectionMode == "ngrok") updated.NgrokUrl = origin; else if (ConnectionMode == "cloudflare") updated.CloudflareUrl = origin; else updated.SshPublicUrl = origin;
                TunnelProfiles.WritePrivate(_cfgPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
                _cfg.NgrokUrl = updated.NgrokUrl; _cfg.CloudflareUrl = updated.CloudflareUrl; _cfg.SshPublicUrl = updated.SshPublicUrl; _connectionCheckResult = "ok"; _clientSeenReadAt = default; _connectionCheck = I18n.T("URL checked and saved. Update it in your AI client.", "Ссылка проверена и сохранена. Обнови её в AI-клиенте."); return;
            }
            if (HasPreviousConnection) throw new InvalidOperationException("Finish the previous handover first.");
            profile.Validate(requireEndpoint: true);
            _connections = ConnectionRegistry.Prepare(profile, c.Revision, DateTimeOffset.UtcNow);
            _connectionProof = await CheckConnectionProfile(profile, _connections.Pending!.OperationId);
            bool accepted;
            if (!ConnectionIsPublic)
            {
                using var consent = new ConnectionLaunchConsentDialog(profile);
                accepted = consent.ShowDialog(owner) == DialogResult.Yes;
            }
            else accepted = MessageBox.Show(owner, I18n.T("Checked from this PC. Switch the URL?\n\nFrom: ", "Проверено с этого ПК. Переключить ссылку?\n\nБыло: ") + ConnectionUrl + I18n.T("\nTo: ", "\nСтанет: ") + profile.Endpoint + I18n.T("\n\nUpdate the URL in your AI client afterwards.", "\n\nЗатем обнови ссылку в своём AI-клиенте."), Theme.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            if (!accepted) { _connections = ConnectionRegistry.Cancel(_connections.Pending.OperationId, _connections.Revision); return; }
            _connections = ConnectionRegistry.CommitSelection(_connections.Pending.OperationId, _connections.Revision, _connectionProof, DateTimeOffset.UtcNow);
            _connectionCheckResult = "ok"; _clientSeenReadAt = default; _connectionCheck = I18n.T("URL checked from this PC. Update it in your AI client.", "Ссылка проверена с этого ПК. Обнови её в AI-клиенте.");
            _connectionProof = null;
        }
        catch { MessageBox.Show(I18n.T("The URL could not be saved or verified. The active route is unchanged. Cancel any pending change before trying another route.", "Не удалось сохранить или проверить ссылку. Действующее подключение не изменено. Перед новой попыткой отмени незавершённое изменение."), Theme.ProductName); }
        finally { _connectionAction = false; }
    }
    internal void ShowConnectionControls(IWin32Window owner)
    {
        using var f = new ConnectionControlDialog(this);
        f.ShowDialog(owner);
    }
    internal void RestoreConnectionFromUi()
    {
        try
        {
            var c = EnsureConnectionRegistry();
            if (c.Pending != null) { _connections = ConnectionRegistry.Cancel(c.Pending.OperationId, c.Revision); _candidateConnectionProcess?.Dispose(); _candidateConnectionProcess = null; _connectionProof = null; return; }
            if (c.History.Length == 0) return;
            if (MessageBox.Show(I18n.T("Restore the previous connection? Its transport may need restarting.", "Вернуть предыдущее подключение? Возможно, потребуется перезапуск сервиса."), Theme.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _connections = ConnectionRegistry.RestorePreviousSelection(c.Revision);
            _activeConnectionProcess?.Dispose(); _activeConnectionProcess = _previousConnectionProcess; _previousConnectionProcess = null;
            if (!_connections.RuntimeEnabled) { if (!_previousLegacyConnection && McpEnabled) StartTunnel(); _previousLegacyConnection = false; }
            else if (McpEnabled && _activeConnectionProcess?.Running != true) StartUniversalConnection();
            _connectionCheck = ""; _connectionCheckResult = ""; _clientSeenReadAt = default;
        }
        catch { MessageBox.Show(I18n.T("Could not restore. Reload the connection registry.", "Не удалось восстановить подключение. Проверь реестр."), Theme.ProductName); }
    }
}

internal sealed class ManagedConnectionProcess : IDisposable
{
    private readonly Process _process;
    private readonly SafeFileHandle _job;
    public bool Running { get { try { return !_process.HasExited; } catch { return false; } } }
    public ManagedConnectionProcess(ProcessStartInfo start)
    {
        _process = Process.Start(start) ?? throw new InvalidOperationException("Transport did not start.");
        IntPtr job = IntPtr.Zero;
        if (!Program.TryAssignToKillJob(_process, ref job)) { try { _process.Kill(true); } catch { } _process.Dispose(); throw new InvalidOperationException("Cannot supervise the transport safely."); }
        _job = new SafeFileHandle(job, true);
    }
    public void Dispose() { try { if (!_process.HasExited) _process.Kill(true); } catch { } _job.Dispose(); _process.Dispose(); }
}
