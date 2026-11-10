using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ZeroIDE.Mcp;
using ZeroIDE.Mcp.ConnectionsV2;

internal static class RuntimeTests
{
    static int passed;
    static readonly string Temp = Path.Combine(Path.GetTempPath(), "zeroide-runtime-" + Guid.NewGuid().ToString("N"));
    static void Pass(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    static void Set(object o, string field, object? value) => o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);
    static object? Call(object o, string method, params object[] args) => o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);
    static int Port() { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    static TrayContext Context(string directory)
    {
        Directory.CreateDirectory(directory);
        var cfg = new TrayConfig { Port = 39081, ServerDir = directory, LogDir = directory, ProjectDir = directory, TunnelEnabled = true, ConnectionMode = "ngrok", NgrokUrl = "https://example.test", Language = "ru" };
        var ctx = (TrayContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayContext));
        Set(ctx, "_cfg", cfg); Set(ctx, "_cfgPath", Path.Combine(directory, "tray-config.json"));
        foreach (string f in new[] { "_connectionsError", "_connectionCheck", "_networkNotice" }) Set(ctx, f, "");
        Set(ctx, "_state", "on"); Set(ctx, "_stateText", "on"); Set(ctx, "_networkState", "running"); Set(ctx, "_http", new HttpClient());
        return ctx;
    }
    [STAThread] static int Main(string[] args)
    {
        Directory.CreateDirectory(Temp);
        Application.SetHighDpiMode(HighDpiMode.SystemAware); Application.EnableVisualStyles();
        try
        {
            NetworkTests().GetAwaiter().GetResult(); ControlApiTests().GetAwaiter().GetResult(); CoordinatorTests().GetAwaiter().GetResult(); GuideTests().GetAwaiter().GetResult(); StoreAndLifecycleTests(); Render(args.Length > 0 ? args[0] : Path.Combine(Temp, "renders"));
            Console.WriteLine("TOTAL RUNTIME: " + passed + " passed"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        finally { try { Directory.Delete(Temp, true); } catch { } }
    }
    static async Task NetworkTests()
    {
        foreach (string mode in new[] { "ok-json", "ok-sse", "wrong-proof", "redirect", "html", "oversize", "bad-mcp" })
        {
            using var listener = new HttpListener(); int port = Port(); string origin = $"http://127.0.0.1:{port}"; listener.Prefixes.Add(origin + "/"); listener.Start();
            int authorized = 0, closed = 0, localProof = 0;
            var loop = Task.Run(async () => {
                while (listener.IsListening)
                {
                    HttpListenerContext context; try { context = await listener.GetContextAsync(); } catch { break; }
                    var req = context.Request; var res = context.Response; string body;
                    if (req.Url!.AbsolutePath == "/local/connection-proof") { localProof++; Pass(req.Headers["X-ZeroIDE-Control"] == "local-control", mode + " localhost control authorization"); body = "{\"proof\":\"" + new string('a', 64) + "\"}"; }
                    else if (req.HttpMethod == "GET")
                    {
                        Pass(req.Headers["Authorization"] == null, mode + " no credential before identity proof");
                        body = mode switch { "wrong-proof" => "{\"proof\":\"" + new string('b',64) + "\"}", "html" => "<html>not MCP</html>", "oversize" => new string('x', 70000), _ => "{\"proof\":\"" + new string('a',64) + "\"}" };
                        if (mode == "redirect") { res.StatusCode = 302; res.RedirectLocation = origin + "/stolen"; }
                    }
                    else if (req.HttpMethod == "POST")
                    {
                        authorized++; Pass(req.Headers["Authorization"] == "Bearer fixture-token", mode + " authenticated initialize");
                        Pass(req.Url.AbsolutePath == "/gateway/Custom/", mode + " full path preserved");
                        res.Headers.Add("Mcp-Session-Id", "fixture-session");
                        body = mode == "bad-mcp" ? "{\"jsonrpc\":\"2.0\",\"id\":\"connection-check\",\"error\":{\"code\":-1}}" : "{\"jsonrpc\":\"2.0\",\"id\":\"connection-check\",\"result\":{\"protocolVersion\":\"2025-03-26\",\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"}}}";
                        if (mode == "ok-sse") { res.ContentType = "text/event-stream"; body = "event: message\ndata: " + body + "\n\n"; }
                    }
                    else { closed++; body = "{}"; }
                    var bytes = Encoding.UTF8.GetBytes(body); res.ContentLength64 = bytes.Length;
                    try { await res.OutputStream.WriteAsync(bytes); } catch { } res.Close();
                }
            });
            var p = new ConnectionProfile { Id = "test", Name = "test", Scope = AccessScope.Local, Ownership = ConnectionOwnership.Local, Endpoint = origin + "/gateway/Custom/" };
            bool success; try { var proof = await ConnectionVerifier.CheckAsync(p, "operation", new Uri(origin), "local-control", "fixture-token"); success = proof.ServerIdentityMatched && proof.McpInitialized && proof.VantagePoint == "this-pc"; } catch { success = false; }
            Pass(success == mode.StartsWith("ok"), mode + " verification result");
            Pass(authorized == (mode.StartsWith("ok") || mode == "bad-mcp" ? 1 : 0), mode + " token disclosure guard");
            if (authorized > 0) Pass(closed == 1, mode + " verification session cleaned up");
            listener.Stop(); await loop;
        }
    }
    static async Task ControlApiTests()
    {
        int port = Port(), calls = 0;
        using var server = new ApprovalControlServer(port, () => "control-secret", new SynchronizationContext(), (_, _) => Task.FromResult(new ApprovalResponseDto()), _ => { }, onConnection: (method, path, body) => { calls++; return Task.FromResult((200, "{}")); });
        server.Start(); using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        foreach (string mode in new[] { "missing", "wrong", "origin", "ok" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/connections/state");
            if (mode != "missing") request.Headers.Add("X-ZeroIDE-Control", mode == "wrong" ? "bad" : "control-secret");
            if (mode == "origin") request.Headers.Add("Origin", "https://untrusted.test");
            using var response = await client.SendAsync(request);
            Pass((int)response.StatusCode == (mode == "ok" ? 200 : 403), "local API " + mode);
        }
        Pass(calls == 1, "unauthorized API requests never invoke controller");
    }
    static async Task CoordinatorTests()
    {
        var ctx = Context(Path.Combine(Temp, "coordinator"));
        var response = await ctx.HandleConnectionApi("GET", "/connections/state", "");
        Pass(response.Item1 == 200 && !response.Item2.Contains("ControlSecret"), "controller state is credential-free");
        using var state = JsonDocument.Parse(response.Item2); long revision = state.RootElement.GetProperty("revision").GetInt64();
        var profile = new ConnectionProfile { Id = "independent", Name = "Independent", Scope = AccessScope.Public, Ownership = ConnectionOwnership.External, Endpoint = "https://independent.test/Custom/" };
        response = await ctx.HandleConnectionApi("POST", "/connections/prepare", JsonSerializer.Serialize(new { revision, profile }));
        Pass(response.Item1 == 200 && ctx.ConnectionUrl == "https://example.test/mcp", "API prepare preserves legacy route");
        using var prepared = JsonDocument.Parse(response.Item2); revision = prepared.RootElement.GetProperty("revision").GetInt64(); string operationId = prepared.RootElement.GetProperty("pending").GetProperty("OperationId").GetString()!;
        var stale = await ctx.HandleConnectionApi("POST", "/connections/cancel", JsonSerializer.Serialize(new { revision = revision - 1, operationId }));
        Pass(stale.Item1 == 409, "API stale revision rejected");
        var unverified = await ctx.HandleConnectionApi("POST", "/connections/activate", JsonSerializer.Serialize(new { revision, operationId, approved = true, verification = true }));
        Pass(unverified.Item1 == 409 && !ctx.UsesUniversalConnection, "agent cannot forge approval or verification");
        var wrongOperation = await ctx.HandleConnectionApi("POST", "/connections/cancel", JsonSerializer.Serialize(new { revision, operationId = "wrong" }));
        Pass(wrongOperation.Item1 == 409, "API operation binding enforced");
        response = await ctx.HandleConnectionApi("POST", "/connections/cancel", JsonSerializer.Serialize(new { revision, operationId }));
        Pass(response.Item1 == 200 && ctx.ConnectionUrl == "https://example.test/mcp", "API cancellation keeps current route");
    }
    static async Task GuideTests()
    {
        var ctx = Context(Path.Combine(Temp, "guide-tests"));
        Pass(ctx.ConnectionStatus().Key == "ready", "no client success inferred from a running process");
        Set(ctx, "_connectionCheckResult", "ok");
        Pass(ctx.ConnectionStatus().Key == "ready", "local check does not claim client onboarding");
        Set(ctx, "_state", "starting"); Pass(ctx.ConnectionStatus().Key == "server-starting", "starting MCP never claims readiness"); Set(ctx, "_state", "on");
        var payload = new { taskId = "test-guide", title = "Setting up", waitingFor = "Enter key on PC", steps = new[] { new { text = "Install\u202e provider", state = "done", owner = false }, new { text = "Enter key", state = "current", owner = true } } };
        var response = await ctx.HandleConnectionApi("POST", "/connections/progress", JsonSerializer.Serialize(payload));
        Pass(response.Item1 == 200 && ctx.ConnectionStatus().Steps.Length == 2, "progress works without registry revision");
        Pass(!ctx.ConnectionStatus().Steps[0].Text.Contains('\u202e') && ctx.ConnectionStatus().Steps[1].Owner, "progress is inert text and owner steps are identified");
        var conflict = await ctx.HandleConnectionApi("POST", "/connections/progress", "{\"taskId\":\"another\"}");
        Pass(conflict.Item1 == 409, "another progress task cannot overwrite active task");
        var longTitle = await ctx.HandleConnectionApi("POST", "/connections/progress", JsonSerializer.Serialize(new { taskId = "test-guide", title = new string('a',81) }));
        Pass(longTitle.Item1 == 409 && ctx.ConnectionStatus().Steps.Length == 2, "oversize guide payload is rejected atomically");
        var steps = Enumerable.Range(0,9).Select(i => new { text = "step", state = "todo", owner = false }).ToArray();
        Pass((await ctx.HandleConnectionApi("POST", "/connections/progress", JsonSerializer.Serialize(new { taskId = "test-guide", steps }))).Item1 == 409, "progress limited to eight steps");
        Set(ctx, "_state", "off"); Pass(ctx.ConnectionStatus().Key == "off", "MCP off takes priority over guide"); Set(ctx, "_state", "on");
        response = await ctx.HandleConnectionApi("POST", "/connections/progress", "{\"taskId\":\"test-guide\",\"clear\":true}");
        Pass(response.Item1 == 200 && ctx.ConnectionStatus().Key == "ready", "guide can be cleared without changing connection");
        Pass((await ctx.HandleConnectionApi("POST", "/connections/note", "{\"text\":\"Protected provider setup\"}")).Item1 == 200 && ctx.ConnectionGuideNote == "Protected provider setup", "agent note persisted separately from registry");
        Pass((await ctx.HandleConnectionApi("POST", "/connections/secret-request", "{\"kind\":\"unsupported\"}")).Item1 == 409, "unknown secret kind rejected before any dialog");
        Pass((await ctx.HandleConnectionApi("POST", "/connections/secret-request", "{\"kind\":\"provider-token\",\"name\":\"../escape\"}")).Item1 == 409, "secret file traversal rejected");
        string marker = Path.Combine(Temp, "guide-tests", "connection-client.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { host = "example.test", path = "/mcp", at = DateTimeOffset.UtcNow }));
        Set(ctx, "_clientSeenReadAt", default(DateTimeOffset)); Pass(ctx.ConnectionStatus().Key == "working", "server client evidence matches exact active address");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { host = "another.test", path = "/mcp", at = DateTimeOffset.UtcNow }));
        Set(ctx, "_clientSeenReadAt", default(DateTimeOffset)); Pass(ctx.ConnectionStatus().Key == "ready", "another host cannot prove this connection works");
        Set(ctx, "_connectionCheckResult", "fail"); Pass(ctx.ConnectionStatus().Key == "check-failed", "failed check takes priority over historical client evidence");
        Call(ctx, "ResetConnectionState"); Pass(ctx.ConnectionStatus().Key == "ready", "restart clears obsolete verification result");
        var noAddress = (TrayConfig)typeof(TrayContext).GetField("_cfg", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx)!;
        noAddress.NgrokUrl = ""; Pass(ctx.ConnectionStatus().Key == "not-configured", "missing URL never claims address ready"); noAddress.NgrokUrl = "https://example.test";
        Pass((await ctx.HandleConnectionApi("POST", "/connections/progress", "{\"taskId\":\"\u202e\"}")).Item1 == 409, "invisible-only task ID rejected");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { host = "example.test", path = "/mcp", at = DateTimeOffset.UtcNow.AddMinutes(-5) }));
        var active = new ConnectionProfile { Id = "active", Name = "Active", Scope = AccessScope.Public, Ownership = ConnectionOwnership.External, Endpoint = "https://example.test/mcp" };
        Set(ctx, "_connections", new ConnectionCatalog { RuntimeEnabled = true, ActiveProfileId = active.Id, Profiles = [active], History = [new ConnectionHistory { Profile = active, ReplacedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, new ConnectionHistory { Profile = active, ReplacedAt = DateTimeOffset.UtcNow.AddDays(-1) }] });
        Set(ctx, "_clientSeenReadAt", default(DateTimeOffset)); Pass(ctx.ConnectionStatus().Key == "ready", "only client evidence after the latest switch counts");
        Set(ctx, "_connections", null);
        string task = ctx.BuildAgentTask("fix");
        Pass(task.Contains("secret-request") && task.Contains("AGENT_CONNECTIONS.md") && !task.Contains("ControlSecret"), "agent handoff includes safety contract without credentials");
    }
    static void StoreAndLifecycleTests()
    {
        var ctx = Context(Path.Combine(Temp, "lifecycle"));
        var local = new ConnectionProfile { Id = "local", Name = "Local", Scope = AccessScope.Local, Ownership = ConnectionOwnership.Local, Adapter = "local", Endpoint = "http://127.0.0.1:39081/mcp" };
        var store = new ConnectionStore(Path.Combine(Temp, "lifecycle", "connections-v2.json"));
        var c = store.Initialize(new ConnectionCatalog { ActiveProfileId = local.Id, Profiles = [local] });
        var external = new ConnectionProfile { Id = "provider-any", Name = "Any provider", Scope = AccessScope.Public, Ownership = ConnectionOwnership.External, Endpoint = "https://any-provider.test/Exact/Path/" };
        c = store.Prepare(external, c.Revision, DateTimeOffset.UtcNow.AddSeconds(-1));
        c = store.UpdatePendingEndpoint(c.Pending!.OperationId, c.Revision, "https://other.test/Custom/");
        Pass(c.Pending.Profile.Endpoint == "https://other.test/Custom/" && c.ActiveProfileId == "local", "pending URL update preserves active route");
        var proof = new ConnectionVerification(c.Pending.OperationId, c.Pending.Profile.Fingerprint(), DateTimeOffset.UtcNow, true, true, "fixture");
        c = store.CommitSelection(c.Pending.OperationId, c.Revision, proof, DateTimeOffset.UtcNow);
        Pass(c.RuntimeEnabled, "verified commit enables universal runtime");
        Set(ctx, "_connections", c);
        Pass(ctx.NetworkState == "external", "external ownership never inherits a managed running status");
        Call(ctx, "StartUniversalConnection"); Pass(ctx.NetworkState == "external", "external route does not spawn a process");
        Call(ctx, "StopUniversalConnections"); Pass(ctx.ConnectionUrl == "https://other.test/Custom/", "stop preserves saved full URL");
        c = store.RestorePreviousSelection(c.Revision); Pass(!c.RuntimeEnabled && c.ActiveProfileId == "local", "rollback restores legacy runtime ownership");
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var managed = external with { Id = "managed-fixture", Ownership = ConnectionOwnership.Managed, Launch = new ConnectionLaunch { Executable = cmd, Arguments = ["/d", "/c", "ping -n 61 127.0.0.1 >nul"], WorkingDirectory = Temp } };
        bool denied = false; try { Call(ctx, "StartApprovedConnection", managed); } catch { denied = true; } Pass(denied, "managed launch denied without owner approval");
        string fingerprint = (string)Call(ctx, "ApprovalFingerprint", managed)!;
        string approval = (string)Call(ctx, "ApprovalPath", managed)!; Directory.CreateDirectory(Path.GetDirectoryName(approval)!); File.WriteAllText(approval, fingerprint);
        using (var route = (ManagedConnectionProcess)Call(ctx, "StartApprovedConnection", managed)!) { Pass(route.Running, "approved fixture process is supervised"); route.Dispose(); Pass(!route.Running, "managed stop kills only owned fixture"); }
        var changed = managed with { Launch = managed.Launch! with { Arguments = ["/d", "/c", "echo changed"] } };
        Pass(!(bool)Call(ctx, "IsLaunchApproved", changed)!, "launch parameter change invalidates approval");
    }
    static void Render(string directory)
    {
        Directory.CreateDirectory(directory); var ctx = Context(Path.Combine(Temp, "render"));
        foreach (string language in new[] { "ru", "en" })
        {
            I18n.CurrentLanguage = language;
            foreach (string state in new[] { "managed", "off", "external", "error", "local", "working", "guide", "pending", "pending-ready", "guide-long", "handover", "check-failed" })
            {
                Set(ctx, "_state", state == "off" ? "off" : "on"); Set(ctx, "_connectionsError", state == "error" ? I18n.T("Connection check failed. The previous route was kept.", "Проверка связи не пройдена. Прежний маршрут сохранён.") : "");
                Set(ctx, "_connections", state == "local" ? new ConnectionCatalog { RuntimeEnabled = true, ActiveProfileId = "local", Profiles = [new ConnectionProfile { Id = "local", Name = I18n.T("On this PC", "На этом ПК"), Scope = AccessScope.Local, Ownership = ConnectionOwnership.Local, Endpoint = "http://127.0.0.1:39081/mcp" }] } : state == "external" ? new ConnectionCatalog { RuntimeEnabled = true, ActiveProfileId = "external", Profiles = [new ConnectionProfile { Id = "external", Name = "Custom provider", Scope = AccessScope.Public, Ownership = ConnectionOwnership.External, Endpoint = "https://external.test/Full/Custom/Route/" }] } : null);
                Set(ctx, "_clientSeenPreview", state == "working"); Set(ctx, "_guideTask", ""); Set(ctx, "_previousLegacyConnection", state == "handover"); Set(ctx, "_connectionCheckResult", state == "check-failed" ? "fail" : "ok");
                if (state is "guide" or "guide-long") { Set(ctx, "_guideTask", "render"); Set(ctx, "_guideUpdated", DateTimeOffset.UtcNow); Set(ctx, "_guideTitle", I18n.T("Internet address via ngrok", "Адрес в интернете через ngrok")); Set(ctx, "_guideWaiting", I18n.T("Paste the key into the window on your PC", "Вставь ключ в окно на этом ПК")); Set(ctx, "_guideSteps", new[] { new ConnectionStep(I18n.T("Install ngrok", "Установить ngrok"), "done", false), new ConnectionStep(I18n.T("Enter provider key", "Ввести ключ провайдера"), "current", true), new ConnectionStep(I18n.T("Check address and connect AI", "Проверить ссылку и подключить AI"), "todo", false) }); }
                if (state is "pending" or "pending-ready") { var active = new ConnectionProfile { Id = "current", Name = "ngrok", Scope = AccessScope.Public, Ownership = ConnectionOwnership.External, Endpoint = "https://example.test/mcp" }; Set(ctx, "_connections", new ConnectionCatalog { RuntimeEnabled = true, ActiveProfileId = active.Id, Profiles = [active], Pending = new PreparedConnection { OperationId = "render", Profile = active with { Id = "new", Endpoint = "https://new.example.test/mcp" }, CreatedAt = DateTimeOffset.UtcNow } }); }
                Set(ctx, "_connectionCheck", state == "check-failed" ? I18n.T("The URL did not respond. Your current connection is unchanged.", "Ссылка не ответила. Текущее подключение не изменено.") : I18n.T("So far, only checked from this PC.", "Пока проверено только с этого ПК."));
                Set(ctx, "_connectionProof", null);
                if (state == "pending-ready") { var c = (ConnectionCatalog)typeof(TrayContext).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx)!; Set(ctx, "_connectionProof", new ConnectionVerification("render", c.Pending!.Profile.Fingerprint(), DateTimeOffset.UtcNow, true, true, "this-pc")); }
                if (state == "guide-long") Set(ctx, "_guideSteps", Enumerable.Range(0,8).Select(i => new ConnectionStep(I18n.T("Check provider settings and keep the existing connection working while preparing the new URL", "Проверить настройки сервиса и сохранить рабочее подключение, пока готовится новая ссылка"), i == 0 ? "done" : i == 1 ? "current" : "todo", i == 1)).ToArray());
                using var form = new SettingsForm(ctx); form.Size = new Size(1180, 800); form.RenderPreviewToFile(Path.Combine(directory, language + "-" + state + ".png"), "connection");
            }
            Set(ctx, "_connectionsError", ""); Set(ctx, "_connectionProof", null); Set(ctx, "_connections", null); Set(ctx, "_state", "on"); Set(ctx, "_guideTask", ""); Set(ctx, "_previousLegacyConnection", false); Set(ctx, "_clientSeenPreview", false); Set(ctx, "_connectionCheckResult", "ok");
            Set(ctx, "_connectionCheck", I18n.T("So far, only checked from this PC.", "Пока проверено только с этого ПК."));
            using (var form = new SettingsForm(ctx)) { form.Size = new Size(1000, 680); form.RenderPreviewToFile(Path.Combine(directory, language + "-compact.png"), "connection"); }
            using (var form = new SettingsForm(ctx)) { form.Size = new Size(1180, 1500); Set(form, "_connectionHelp", true); form.RenderPreviewToFile(Path.Combine(directory, language + "-help.png"), "connection"); }
            using (var dialog = new ConnectionSecretDialog("ngrok", I18n.T("For the internet address", "Для адреса в интернете"))) Snapshot(dialog, Path.Combine(directory, language + "-secret.png"));
            using (var dialog = new ConnectionControlDialog(ctx)) Snapshot(dialog, Path.Combine(directory, language + "-control.png"));
            using (var dialog = new ConnectionAddressDialog("https://provider.test/Custom/mcp")) Snapshot(dialog, Path.Combine(directory, language + "-address.png"));
            using (var dialog = new ConnectionLaunchConsentDialog(new ConnectionProfile { Id = "fixture", Name = "Example transport", Ownership = ConnectionOwnership.Managed, Scope = AccessScope.Public, Launch = new ConnectionLaunch { Executable = "C:\\Program Files\\Transport\\tunnel.exe", Arguments = ["run", "--config", "C:\\Protected config\\transport.yml"], ConfigurationFile = "C:\\Protected config\\transport.yml" } })) Snapshot(dialog, Path.Combine(directory, language + "-consent.png"));
        }
        Pass(Directory.GetFiles(directory, "*.png").Length == 36, "native UI states rendered (visual inspection required)");
    }
    static void Snapshot(Form form, string path) { form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-4000, -4000); form.Show(); Application.DoEvents(); form.PerformLayout(); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path); form.Hide(); }
}
