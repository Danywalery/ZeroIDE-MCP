using System.Diagnostics;
using System.Drawing;
using System.Management;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

internal static class TunnelProfiles
{
    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroIDE-MCP", "tools");
    public static string Mode(TrayConfig c) => !c.TunnelEnabled ? "local" : c.ConnectionMode is "ngrok" or "cloudflare" or "ssh" ? c.ConnectionMode : "local";
    public static string DefaultNgrokExe => Path.Combine(Home, "ngrok", "ngrok.exe");
    public static string DefaultNgrokConfig => Path.Combine(Home, "ngrok", "ngrok.yml");
    public static string NgrokExe(TrayConfig c) => string.IsNullOrWhiteSpace(c.NgrokExePath) ? DefaultNgrokExe : c.NgrokExePath;
    public static string NgrokConfig(TrayConfig c) => string.IsNullOrWhiteSpace(c.NgrokConfigPath) ? DefaultNgrokConfig : c.NgrokConfigPath;
    public static string CloudflareExe(TrayConfig c) => string.IsNullOrWhiteSpace(c.CloudflareExePath) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cloudflared", "cloudflared.exe") : c.CloudflareExePath;
    public static string Origin(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u) || u.Scheme != "https" ||
            !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) ||
            (u.AbsolutePath != "/" && u.AbsolutePath != "/mcp") || u.IsLoopback)
            throw new ArgumentException(I18n.T("Enter an HTTPS origin or /mcp URL, without credentials or query parameters.", "Введи HTTPS-адрес или ссылку /mcp без пароля и параметров."));
        return u.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
    public static string PublicOrigin(TrayConfig c) => Mode(c) switch { "ngrok" => c.NgrokUrl, "cloudflare" => c.CloudflareUrl, "ssh" => c.SshPublicUrl, _ => "" };
    // Host of the public address for status lines; "" when the PC is local-only.
    public static string PublicHost(TrayConfig c) => Uri.TryCreate(PublicOrigin(c) ?? "", UriKind.Absolute, out var u) ? u.Host : "";
    public static ProcessStartInfo Build(TrayConfig c)
    {
        string mode = Mode(c);
        var p = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        void Add(params string[] values) { foreach (var v in values) p.ArgumentList.Add(v); }
        if (mode == "ngrok")
        {
            p.FileName = NgrokExe(c);
            if (!File.Exists(p.FileName) || !File.Exists(NgrokConfig(c))) throw new InvalidOperationException("ngrok: executable / config missing");
            string dir = Path.GetDirectoryName(NgrokConfig(c))!;
            Add("http", $"http://127.0.0.1:{c.Port}", "--config", NgrokConfig(c), "--inspect=false", "--log", Path.Combine(dir, "agent.log"), "--log-format=json");
            string origin = Origin(c.NgrokUrl);
            if (origin != "") Add("--url", origin);
        }
        else if (mode == "cloudflare")
        {
            p.FileName = CloudflareExe(c);
            if (!File.Exists(p.FileName)) throw new InvalidOperationException("cloudflared: executable missing");
            if (Origin(c.CloudflareUrl) == "") throw new InvalidOperationException("Cloudflare: public hostname missing");
            Directory.CreateDirectory(Path.Combine(Home, "cloudflare"));
            Add("tunnel", "--no-autoupdate", "--logfile", Path.Combine(Home, "cloudflare", "agent.log"));
            if (!string.IsNullOrWhiteSpace(c.CloudflareConfigPath))
            {
                if (!File.Exists(c.CloudflareConfigPath) || string.IsNullOrWhiteSpace(c.CloudflareTunnelId)) throw new InvalidOperationException("Cloudflare: config / tunnel ID missing");
                Add("--config", c.CloudflareConfigPath, "run", c.CloudflareTunnelId);
            }
            else
            {
                if (!File.Exists(c.CloudflareTokenFile)) throw new InvalidOperationException("Cloudflare: token file missing");
                Add("run", "--token-file", c.CloudflareTokenFile);
            }
        }
        else throw new InvalidOperationException("Not a managed web tunnel");
        return p;
    }
    // No account credentials in tray-config.json, command arguments or the journal.
    public static void WritePrivate(string path, string text)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, "", new UTF8Encoding(false));
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            var owner = WindowsIdentity.GetCurrent().User!;
            acl.SetOwner(owner);
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(temp).SetAccessControl(acl);
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed partial class TrayContext
{
    private string _networkState = "off";
    private bool _statusPollBusy;
    private DateTime _statusFailureAt = DateTime.MinValue;
    private async Task PollAsync()
    {
        if (_statusPollBusy) return;
        _statusPollBusy = true;
        try { await PollCoreAsync(); } finally { _statusPollBusy = false; }
    }
    private bool _networkBusy;
    private int _networkEpoch;
    private DateTime _networkLastRefresh = DateTime.MinValue;
    private string _networkNotice = "";
    public string ConnectionMode => UniversalRuntime ? _connections!.Active.Ownership == ZeroIDE.Mcp.ConnectionsV2.ConnectionOwnership.Local ? "local" : "universal" : TunnelProfiles.Mode(_cfg);
    public string PublicMcpUrl => UniversalRuntime ? _connections!.Active.Endpoint : TunnelProfiles.PublicOrigin(_cfg) is string u && u.Length > 0 ? u.TrimEnd('/') + "/mcp" : "";
    public bool McpEnabled => _state is "on" or "starting";
    public string NetworkState => !McpEnabled ? "off" : ConnectionMode == "local" ? "local" : UniversalRuntime && _connections!.Active.Ownership == ZeroIDE.Mcp.ConnectionsV2.ConnectionOwnership.External ? "external" : _networkState;
    public string NetworkDescription => !McpEnabled ? I18n.T("Off with MCP · saved address stays the same", "Выключен вместе с MCP · адрес сохранён") : NetworkState switch
    {
        "local" => I18n.T("Local access only", "Только локальный доступ"),
        "ready" => I18n.T("Health check passed from this PC", "Проверка с этого ПК пройдена"),
        "running" => I18n.T("Tunnel process running · public access not verified", "Туннель запущен · доступ снаружи не проверен"),
        "error" => I18n.T("Connection error · check setup and tunnel log", "Ошибка подключения · проверь настройки и журнал туннеля"),
        _ => I18n.T("Starting tunnel…", "Запуск туннеля…")
    };
    public string NetworkNotice => _networkNotice;
    private void ResetConnectionState()
    {
        _networkEpoch++;
        _connectionProof = null;
        _connectionCheck = ""; _connectionCheckResult = ""; _clientSeenReadAt = default;
        _networkState = "off";
        _networkNotice = "";
        _networkLastRefresh = DateTime.MinValue;
    }
    private void StartTunnel()
    {
        if (UniversalRuntime) { if (McpEnabled) StartUniversalConnection(); return; }
        if (!McpEnabled) return;
        _tunnelLastStart = DateTime.Now; // also throttle failures before Process.Start
        _networkState = "starting";
        if (ConnectionMode == "local") { _networkState = "local"; return; }
        if (ConnectionMode == "ssh") { StartSshTunnel(); return; }
        try
        {
            var psi = TunnelProfiles.Build(_cfg);
            _tunnel = FindWebTunnel(psi) ?? Process.Start(psi);
            if (_tunnel == null) throw new InvalidOperationException("No process");
            _tunnel.EnableRaisingEvents = true;
            _tunnel.Exited += (_, _) => OnTunnelExited();
            if (!Program.TryAssignToKillJob(_tunnel, ref _tunnelJob))
                Log("web tunnel: kill-on-close unavailable; explicit stop remains enabled");
            _networkState = "running";
            Log("web tunnel started/adopted: " + ConnectionMode + " pid=" + _tunnel.Id);
        }
        catch (Exception ex)
        {
            _networkState = "error";
            Log("web tunnel start failed: " + ConnectionMode + " (" + ex.GetType().Name + ")");
        }
    }
    private Process? FindWebTunnel(ProcessStartInfo psi)
    {
        // Adopt only this exact executable, config and local upstream. Never another user's tunnel.
        string exe = Path.GetFileName(psi.FileName);
        if (exe is not ("ngrok.exe" or "cloudflared.exe")) return null;
        try
        {
            using var search = new ManagementObjectSearcher($"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '{exe}'");
            foreach (ManagementObject mo in search.Get())
            {
                string cmd = mo["CommandLine"] as string ?? "";
                string path = mo["ExecutablePath"] as string ?? "";
                if (!path.Equals(Path.GetFullPath(psi.FileName), StringComparison.OrdinalIgnoreCase)) continue;
                bool match = ConnectionMode == "ngrok"
                    ? cmd.Contains(TunnelProfiles.NgrokConfig(_cfg), StringComparison.OrdinalIgnoreCase) && cmd.Contains($"http://127.0.0.1:{_cfg.Port}", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrEmpty(_cfg.NgrokUrl) || !cmd.Contains("--url") || cmd.Contains(_cfg.NgrokUrl, StringComparison.OrdinalIgnoreCase))
                    : (!string.IsNullOrWhiteSpace(_cfg.CloudflareConfigPath) ? cmd.Contains(_cfg.CloudflareConfigPath, StringComparison.OrdinalIgnoreCase) && cmd.Contains(_cfg.CloudflareTunnelId, StringComparison.OrdinalIgnoreCase) : !string.IsNullOrWhiteSpace(_cfg.CloudflareTokenFile) && cmd.Contains(_cfg.CloudflareTokenFile, StringComparison.OrdinalIgnoreCase));
                if (match) return Process.GetProcessById(Convert.ToInt32(mo["ProcessId"]));
            }
        }
        catch { /* A failed scan must not authorize adoption of an unrelated process. */ }
        return null;
    }
    private void ConnectionTick()
    {
        if (UniversalRuntime) { TickUniversalConnection(); return; }
        if (!McpEnabled) return;
        if (ConnectionMode == "local") { _networkState = "local"; return; }
        if (_tunnel == null || _tunnel.HasExited)
        {
            _networkState = "error";
            int delay = Math.Min(60, 3 + _tunnelRestarts * 3);
            if ((DateTime.Now - _tunnelLastStart).TotalSeconds >= delay)
            {
                _tunnelRestarts++;
                _tunnel?.Dispose(); _tunnel = null;
                StartTunnel();
            }
            return;
        }
        if (_networkState is "starting" or "off") _networkState = "running";
        // Only localhost API polling. No continuous billable public health requests.
        if (ConnectionMode == "ngrok" && (DateTime.Now - _networkLastRefresh).TotalSeconds >= 5)
            _ = RefreshConnectionAsync(false);
    }
    public async Task RefreshConnectionAsync(bool checkPublic = true)
    {
        if (UniversalRuntime) { if (checkPublic) await CheckConnectionFromUi(); return; }
        if (_networkBusy) return;
        _networkBusy = true;
        int epoch = _networkEpoch;
        string mode = ConnectionMode;
        _networkLastRefresh = DateTime.Now;
        try
        {
            if (!McpEnabled) { _networkNotice = I18n.T("Enable MCP to refresh the tunnel. The saved address is unchanged.", "Включи MCP для проверки туннеля. Сохранённый адрес не изменён."); return; }
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            if (mode == "ngrok")
            {
                string body = await client.GetStringAsync($"http://127.0.0.1:{_cfg.NgrokApiPort}/api/tunnels");
                if (epoch != _networkEpoch || !McpEnabled) return;
                using var doc = JsonDocument.Parse(body);
                string discovered = "";
                foreach (var t in doc.RootElement.GetProperty("tunnels").EnumerateArray())
                {
                    string upstream = t.GetProperty("config").GetProperty("addr").GetString() ?? "";
                    if (!Uri.TryCreate(upstream, UriKind.Absolute, out var target) || target.Host != "127.0.0.1" || target.Port != _cfg.Port) continue;
                    string u = t.GetProperty("public_url").GetString() ?? "";
                    if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { discovered = TunnelProfiles.Origin(u); break; }
                }
                if (discovered == "") throw new InvalidOperationException("No matching HTTPS endpoint");
                if (_cfg.NgrokUrl != discovered)
                {
                    _cfg.NgrokUrl = discovered; SaveConfig();
                    _networkNotice = I18n.T("Address synced. If it changed, update the URL in Notion too.", "Адрес получен. Если он изменился, обнови ссылку и в Notion.");
                }
                if (_networkState == "error") _networkState = "running";
            }
            string origin = TunnelProfiles.PublicOrigin(_cfg);
            if (checkPublic && origin != "" && mode != "local")
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, TunnelProfiles.Origin(origin) + "/health");
                req.Headers.Add("ngrok-skip-browser-warning", "1");
                using var response = await client.SendAsync(req);
                string body = await response.Content.ReadAsStringAsync();
                if (epoch != _networkEpoch || !McpEnabled) return;
                using var health = JsonDocument.Parse(body);
                if (!response.IsSuccessStatusCode || !health.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) throw new InvalidOperationException("Health check failed");
                _networkState = "ready";
                _networkNotice = I18n.T("Health check passed from this PC; MCP authorization is still required.", "Проверка с этого ПК пройдена; для MCP всё равно нужна авторизация.");
            }
        }
        catch
        {
            if (epoch == _networkEpoch && McpEnabled)
            {
                if (checkPublic) _networkState = "error";
                _networkNotice = I18n.T("Could not verify the address. Check the account, local agent API and tunnel log.", "Не удалось проверить адрес. Проверь аккаунт, локальный API агента и журнал туннеля.");
            }
        }
        finally { _networkBusy = false; }
    }
    public void ChooseConnectionFromUi(string mode)
    {
        if (mode == ConnectionMode) return;
        if (McpEnabled && MessageBox.Show(I18n.T("Switch the active connection? The current remote link will disconnect.", "Сменить активное подключение? Текущий удалённый канал отключится."), Theme.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        ResetConnectionState(); KillTree(_tunnel); _tunnel = null;
        _cfg.ConnectionMode = mode; _cfg.TunnelEnabled = mode != "local";
        _tunnelRestarts = 0; SaveConfig();
        if (McpEnabled) StartTunnel();
    }
    public void ConfigureConnectionFromUi(IWin32Window owner)
    {
        using var dialog = new ConnectionSetupForm(_cfg, ConnectionMode);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        SaveConfig();
        RestartTunnelFromUi();
    }
}

internal sealed class ConnectionSetupForm : Form
{
    public ConnectionSetupForm(TrayConfig c, string mode)
    {
        Text = I18n.T("Connection settings", "Настройки подключения");
        BackColor = Theme.Bg1; ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 10f); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(700, 650); AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 1, AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(layout);
        void LabelText(string s) => layout.Controls.Add(new Label { Text = s, AutoSize = true, MaximumSize = new Size(630, 0), Margin = new Padding(0, 0, 0, 8) });
        TextBox Field(string label, string value, bool secret = false)
        {
            LabelText(label);
            var b = new TextBox { Text = value, UseSystemPasswordChar = secret, Dock = DockStyle.Top, BackColor = Theme.SurfaceRaised, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 16) };
            layout.Controls.Add(b); return b;
        }
        void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
        var link = new LinkLabel { Text = mode == "ngrok" ? I18n.T("Open ngrok account / domains", "Открыть аккаунт ngrok / домены") : I18n.T("Open Cloudflare dashboard", "Открыть панель Cloudflare"), AutoSize = true, LinkColor = Theme.Info, Margin = new Padding(0, 0, 0, 16) };
        link.LinkClicked += (_, _) => Open(mode == "ngrok" ? "https://dashboard.ngrok.com/" : "https://dash.cloudflare.com/");
        if (mode is "ngrok" or "cloudflare") layout.Controls.Add(link);
        LabelText(mode switch {
            "ngrok" => I18n.T("Keep the account's assigned domain, or paste another domain available in that account. Refresh syncs the running agent; it does not generate a domain.", "Сохрани выданный аккаунту домен или вставь другой доступный в аккаунте. Обновление получает адрес агента, а не создаёт новый домен."),
            "cloudflare" => I18n.T("Named tunnel only. Route this hostname to http://127.0.0.1:" + c.Port + ". Token-file mode requires cloudflared 2025.4.0+. Leave token blank to keep it.", "Только именованный туннель. Направь hostname на http://127.0.0.1:" + c.Port + ". Режим token-file: cloudflared 2025.4.0+. Пустое поле сохраняет токен."),
            _ => I18n.T("Existing SSH reverse tunnel. Enter its public HTTPS origin.", "Существующий обратный SSH-туннель. Укажи его публичный HTTPS-адрес.") });
        var url = Field(mode == "ngrok" ? I18n.T("Public HTTPS address (optional until ngrok starts)", "Публичный HTTPS-адрес (ngrok может получить сам)") : I18n.T("Public HTTPS address", "Публичный HTTPS-адрес"), TunnelProfiles.PublicOrigin(c));
        TextBox? exe = null, config = null, token = null, id = null;
        if (mode == "ngrok")
        {
            exe = Field("ngrok.exe", TunnelProfiles.NgrokExe(c));
            config = Field(I18n.T("ngrok config file · local API must be 127.0.0.1:" + c.NgrokApiPort, "Конфиг ngrok · локальный API: 127.0.0.1:" + c.NgrokApiPort), TunnelProfiles.NgrokConfig(c));
            token = Field(I18n.T("New ngrok account authtoken (optional, not the MCP Bearer token)", "Новый authtoken аккаунта ngrok (необязательно, это не Bearer MCP)"), "", true);
            LabelText(I18n.T("A new account token replaces this ngrok config. Blank keeps it. In the OAuth browser window click Visit Site if the ngrok warning appears.", "Новый токен заменит этот конфиг ngrok. Пустое поле сохраняет его. В окне OAuth нажми Visit Site, если появится предупреждение ngrok."));
        }
        else if (mode == "cloudflare")
        {
            exe = Field("cloudflared.exe", TunnelProfiles.CloudflareExe(c));
            config = Field(I18n.T("Local config file (optional; takes priority over token)", "Локальный конфиг (необязательно; приоритетнее токена)"), c.CloudflareConfigPath);
            id = Field(I18n.T("Tunnel ID / name (only with local config)", "ID / имя туннеля (только для локального конфига)"), c.CloudflareTunnelId);
            token = Field(I18n.T("New named-tunnel token (optional; stored privately on this PC)", "Новый токен именованного туннеля (хранится только на ПК)"), "", true);
        }
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 8, 0, 0) };
        var save = new Button { Text = I18n.T("Save", "Сохранить"), AutoSize = true, Height = 36 };
        var cancel = new Button { Text = I18n.T("Cancel", "Отмена"), AutoSize = true, DialogResult = DialogResult.Cancel, Height = 36 };
        footer.Controls.Add(save); footer.Controls.Add(cancel); layout.Controls.Add(footer); AcceptButton = save; CancelButton = cancel;
        save.Click += (_, _) =>
        {
            try
            {
                string origin = TunnelProfiles.Origin(url.Text);
                if (mode == "cloudflare" && origin == "") throw new ArgumentException(I18n.T("Cloudflare needs a public hostname.", "Для Cloudflare нужен публичный hostname."));
                if (exe != null && !File.Exists(exe.Text.Trim())) throw new ArgumentException(I18n.T("Executable not found. Install the tunnel client first.", "Программа не найдена. Сначала установи клиент туннеля."));
                if (mode == "ngrok")
                {
                    string path = Path.GetFullPath(config!.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(token!.Text))
                    {
                        string secret = token.Text.Trim();
                        if (secret.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-')) throw new ArgumentException("Invalid authtoken format");
                        if (File.Exists(path) && MessageBox.Show(I18n.T("Replace this ngrok config with the new account token? Custom options will be removed.", "Заменить конфиг ngrok новым токеном аккаунта? Дополнительные параметры будут удалены."), Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        TunnelProfiles.WritePrivate(path, $"version: '2'\nauthtoken: '{secret}'\nweb_addr: 127.0.0.1:{c.NgrokApiPort}\n");
                    }
                    if (!File.Exists(path)) throw new ArgumentException(I18n.T("Config missing. Enter the account authtoken or select a config.", "Нет конфига. Введи authtoken аккаунта или укажи конфиг."));
                    c.NgrokUrl = origin; c.NgrokExePath = exe!.Text.Trim(); c.NgrokConfigPath = path;
                }
                else if (mode == "cloudflare")
                {
                    string cp = config!.Text.Trim();
                    if (cp != "" && (!File.Exists(cp) || string.IsNullOrWhiteSpace(id!.Text))) throw new ArgumentException("Config / tunnel ID missing");
                    string tokenFile = string.IsNullOrWhiteSpace(c.CloudflareTokenFile) ? Path.Combine(TunnelProfiles.Home, "cloudflare", "tunnel.token") : c.CloudflareTokenFile;
                    if (!string.IsNullOrWhiteSpace(token!.Text))
                    {
                        string secret = token.Text.Trim();
                        if (secret.Any(char.IsWhiteSpace)) throw new ArgumentException("Invalid tunnel token format");
                        TunnelProfiles.WritePrivate(tokenFile, secret);
                    }
                    if (cp == "" && !File.Exists(tokenFile)) throw new ArgumentException(I18n.T("Enter a tunnel token or local config.", "Введи токен туннеля или укажи локальный конфиг."));
                    c.CloudflareUrl = origin; c.CloudflareExePath = exe!.Text.Trim(); c.CloudflareConfigPath = cp; c.CloudflareTunnelId = id!.Text.Trim(); c.CloudflareTokenFile = tokenFile;
                }
                else c.SshPublicUrl = origin;
                if (token != null) token.Clear(); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(ex is ArgumentException ? ex.Message : I18n.T("Could not save settings. Check the file path and permissions.", "Не удалось сохранить настройки. Проверь путь и права на файл."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
}
