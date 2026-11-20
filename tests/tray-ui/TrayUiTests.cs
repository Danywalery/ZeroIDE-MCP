using System.Drawing.Imaging;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ZeroIDE.Mcp;

// v0.5 tray UI checks after returning to the original design: palette, logo, panel banner,
// approval cards and Settings → Changes. PNG renders go to the folder given as the first argument.
// Nothing here touches the installed tray: the TrayContext is a field-only stand-in (no icon,
// no timers, no HTTP), and every file lives under a per-run temp folder.
internal static partial class TrayUiTests
{
    static readonly List<string> names = []; static string captures = "";
    static void Check(bool value, string name) { if (!value) throw new Exception(name); names.Add(name); Console.WriteLine("PASS  " + name); }
    static void Save(Bitmap bmp, string name) { if (captures.Length > 0) bmp.Save(Path.Combine(captures, name + ".png"), ImageFormat.Png); }
    static bool Near(Color a, Color b, int tol = 40) => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    [STAThread] static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var temp = Path.Combine(Path.GetTempPath(), "zeroide-tray-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        if (args.Length > 0) { captures = Path.GetFullPath(args[0]); Directory.CreateDirectory(captures); }
        string lang = I18n.CurrentLanguage; I18n.CurrentLanguage = "ru";
        try { OldDesign(); Logo(); Panel(temp); GuardianCards(); ChangesFlow(temp); SettingsChanges(temp); UpdaterFlow(temp); Console.WriteLine("TOTAL TRAY UI: " + names.Count + " passed"); return 0; }
        catch (Exception e) { Console.Error.WriteLine("FAIL  " + e); return 1; }
        finally { I18n.CurrentLanguage = lang; try { Directory.Delete(temp, true); } catch (IOException) { } }
    }

    /// <summary>A TrayContext with fields only: lists and strings are empty, config points into the temp folder.</summary>
    internal static TrayContext FakeTray(string root)
    {
        var ctx = (TrayContext)RuntimeHelpers.GetUninitializedObject(typeof(TrayContext));
        foreach (var f in typeof(TrayContext).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (f.FieldType.IsValueType || f.GetValue(ctx) != null) continue;
            var t = f.FieldType;
            if (t == typeof(string)) f.SetValue(ctx, "");
            else if (t.IsGenericType && t.GetGenericTypeDefinition() is var d && (d == typeof(List<>) || d == typeof(Dictionary<,>) || d == typeof(HashSet<>))) f.SetValue(ctx, Activator.CreateInstance(t));
        }
        Set(ctx, "_cfg", new TrayConfig { LogDir = Path.Combine(root, "logs"), ProjectDir = Path.Combine(root, "proj"), Port = 1, ControlPort = 1 });
        Set(ctx, "_state", "off"); Set(ctx, "_stateText", "Off");
        return ctx;
    }
    internal static void Set(object o, string field, object? value) => o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    static void OldDesign()
    {
        // The original ZeroIDE palette (graphite + indigo accent), exactly as before the Studio experiment.
        Check(Theme.Accent.ToArgb() == Color.FromArgb(0x5B, 0x6C, 0xFF).ToArgb() && Theme.Bg0.ToArgb() == Color.FromArgb(0x0D, 0x0F, 0x14).ToArgb()
            && Theme.Surface.ToArgb() == Color.FromArgb(0x18, 0x1C, 0x24).ToArgb() && Theme.TextPrimary.ToArgb() == Color.FromArgb(0xF2, 0xF4, 0xF7).ToArgb(), "original palette is back");
        var types = typeof(Theme).Assembly.GetTypes().Select(t => t.Name).ToHashSet();
        Check(!types.Contains("StudioForm") && !types.Contains("DeskChanges") && !types.Contains("DeskAudit") && !types.Contains("StudioHost"), "the Studio window is no longer part of the tray");
    }

    static void Logo()
    {
        Check(Theme.LogoIcon.Width >= 16 && ReferenceEquals(Theme.LogoIcon, Theme.LogoIcon), "logo icon is loaded from the embedded asset and cached");
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Black); Theme.DrawLogo(g, new RectangleF(0, 0, 64, 64)); }
        Check(Near(bmp.GetPixel(8, 32), Color.FromArgb(0x10, 0x25, 0x23), 12) && Near(bmp.GetPixel(32, 19), Color.FromArgb(0x82, 0xEB, 0xC8), 12) && Near(bmp.GetPixel(49, 48), Color.FromArgb(0xB2, 0xCE, 0xF5), 16), "vector logo matches the SVG: tile, mint Z, blue dot");
        Save(bmp, "logo-64");
        using var form = new Form(); Theme.ApplyLogo(form);
        Check(ReferenceEquals(form.Icon, Theme.LogoIcon), "windows get the logo as their taskbar icon");
        using var trayIcon = Theme.MakeTrayIcon(Theme.Success);
        using var trayBmp = trayIcon.ToBitmap();
        Save(trayBmp, "tray-icon-on-32");
        Check(Near(trayBmp.GetPixel(4, 16), Color.FromArgb(0x10, 0x25, 0x23), 14)
            && Near(trayBmp.GetPixel(16, 9), Color.FromArgb(0x82, 0xEB, 0xC8), 16)
            && Near(trayBmp.GetPixel(24, 24), Theme.Success, 20), "tray icon uses the new logo with a readable state dot");
        Check(new TrayConfig().TrayMenuMode == "panel", "default tray menu mode is panel");
        var localCtx = FakeTray(Path.Combine(Path.GetTempPath(), "zeroide-unused"));
        Set(localCtx, "_state", "on");
        Set(localCtx, "_cfg", new TrayConfig { Port = 39011, TunnelEnabled = false, ConnectionMode = "local" });
        var localStatus = localCtx.ConnectionStatus();
        Check(localStatus.Key == "local" && localStatus.Actions.Any(a => a.Id == "wizard_internet"), "local connection banner opens the browser setup wizard");
    }

    static void Panel(string temp)
    {
        var ctx = FakeTray(Path.Combine(temp, "panel"));
        ctx.StandbyToolEnabledOverride = false;
        Set(ctx, "_userMessageText", "Готово: лендинг собран, отчёт сохранён.");
        Set(ctx, "_userMessageKind", "status");
        Set(ctx, "_userMessageAgent", "Claude · Лендинг");
        Set(ctx, "_userMessageLink", MessageLink.TryCreate("file", @"C:\Projects\landing\report.html", false));
        Check(ctx.UserMessageLink is { ButtonText: "Открыть" } && ctx.UserMessageAgent == "Claude · Лендинг", "banner keeps the sender and its one-click link");
        using var panel = new TrayPopupForm(ctx, "panel");
        string file = Path.Combine(temp, "panel.png");
        panel.RenderToBitmapFile(file);
        using var bmp = new Bitmap(file);
        float s = bmp.Width / 480f;
        Check(Near(bmp.GetPixel((int)(31 * s), (int)(20 * s)), Color.FromArgb(0x82, 0xEB, 0xC8)), "panel header shows the logo in place of the hexagon");
        Save(bmp, "tray-panel-message-ru");
        Check(PermissionsConfig.CreateDefaultTools()["await_owner"].Mode == "deny", "await_owner (5 min standby) is disabled by default");
        Check(PermissionsStore.DetectActivePreset(new PermissionsConfig()) == "balanced", "default permissions still match balanced preset with await_owner disabled");
        Check(PermissionsStore.AllTools.Count == 19
            && PermissionsStore.AllTools.Any(t => t.Name == "read_image")
            && PermissionsStore.AllTools.Any(t => t.Name == "file_ops")
            && PermissionsStore.AllTools.Any(t => t.Name == "system_info")
            && PermissionsStore.AllTools.Any(t => t.Name == "task_status"), "all 19 server tools including read_image, file_ops and system_info appear in Settings");
        Check(PermissionsConfig.CreateDefaultTools()["read_image"].Mode == "ask", "read_image defaults to ask in tray permissions");
        Check(PermissionsStore.AllTools.Any(t => t.Name == "await_owner" && t.RawDescriptionRu.Contains("не разрывать соединение", StringComparison.OrdinalIgnoreCase)), "await_owner description explains keeping MCP connected");
        Check(!panel.CurrentTipsForTest.Any(t => t.Tip.Contains("Удержание связи", StringComparison.OrdinalIgnoreCase)), "5-min pill and its tooltip are absent when await_owner is disabled");

        // Long banner & task card + collapsed chat hides chat feed bubbles completely
        var tasks = new List<AgentTaskState>
        {
            new()
            {
                AgentId = "monday",
                AgentName = "monday agent",
                Title = "Hill Rider: лунная карта с низкой гравитацией",
                Current = "Прогоняю симуляцию лунной трассы и сравниваю высоту прыжков",
                Status = "running",
                Done = ["Создана ZIP-копия проекта", "Настроена гравитация по трассам"],
                Next = ["Подобрать рельеф кратеров", "Запустить автотесты физики"],
                StartedAt = DateTime.Now.AddMinutes(-3),
            }
        };
        Set(ctx, "_agentTasks", tasks);
        Set(ctx, "_selectedAgentId", "monday");
        string longReply = "Принял. Делаю лунную карту SS 04: сначала ZIP-копия, затем гравитация по трассам, трасса, пейзаж и тесты. Буду обновлять task_status на каждом этапе и пришлю готовый отчёт со скриншотами после прогона симуляции.";
        var chatList = new List<ChatHistoryItem>
        {
            new("m1", "owner", "monday agent", "не забывай вести таск статус и писать мне сюда сообщения через юзер мессадж", "delivered", DateTimeOffset.UtcNow.AddMinutes(-2).ToString("o")),
            new("m2", "agent", "monday agent", longReply, "delivered", DateTimeOffset.UtcNow.AddSeconds(-20).ToString("o")),
        };
        Set(ctx, "_chatHistory", chatList);
        Set(ctx, "_userMessageText", longReply);
        Set(ctx, "_userMessageAgent", "monday agent");
        Set(ctx, "_userMessageLink", null);

        int collapsedBannerHeight;
        using (var collapsedPanel = new TrayPopupForm(ctx, "panel"))
        {
            string cFile = Path.Combine(temp, "panel-task-collapsed.png");
            collapsedPanel.RenderToBitmapFile(cFile);
            collapsedBannerHeight = collapsedPanel.UnclampedPanelHeightForTest;
            Check(!collapsedPanel.CurrentTipsForTest.Any(t => t.Tip.StartsWith("monday agent: Принял", StringComparison.Ordinal)), "collapsed chat hides dialogue bubbles completely");
            using var cBmp = new Bitmap(cFile);
            Save(cBmp, "tray-panel-task-collapsed-ru");
        }

        // Expanded long banner + expanded task card
        using (var expandedPanel = new TrayPopupForm(ctx, "panel"))
        {
            expandedPanel.SetMessageExpandedForPreview(true);
            expandedPanel.SetTaskExpandedForPreview(true);
            string eFile = Path.Combine(temp, "panel-task-expanded.png");
            expandedPanel.RenderToBitmapFile(eFile);
            Check(expandedPanel.UnclampedPanelHeightForTest > collapsedBannerHeight + 60, "expanding long message banner and task card shows full content");
            using var eBmp = new Bitmap(eFile);
            Save(eBmp, "tray-panel-task-expanded-ru");
        }

        // Enabled await_owner shows the 5-min pill with explanatory hover tooltip
        ctx.StandbyToolEnabledOverride = true;
        Set(ctx, "_userMessageText", "");
        Set(ctx, "_agentTasks", new List<AgentTaskState>());
        using (var standbyPanel = new TrayPopupForm(ctx, "panel"))
        {
            string sbFile = Path.Combine(temp, "panel-standby.png");
            standbyPanel.RenderToBitmapFile(sbFile);
            Check(standbyPanel.CurrentTipsForTest.Any(t => t.Tip.Contains("не разрывает соединение", StringComparison.OrdinalIgnoreCase)), "5-min pill registers explanatory hover tooltip when await_owner is enabled");
            using var sbBmp = new Bitmap(sbFile);
            Save(sbBmp, "tray-panel-chat-standby-ru");
        }

        // Expanded chat with expandable long model reply
        Set(ctx, "_holdAfterTask", true);
        int chatHeightCollapsedBubble;
        using (var chatPanel = new TrayPopupForm(ctx, "panel"))
        {
            chatPanel.SetComposerForPreview(true, "После проверки сохрани скриншот в папку отчётов");
            string chatFile = Path.Combine(temp, "panel-chat.png");
            chatPanel.RenderToBitmapFile(chatFile);
            chatHeightCollapsedBubble = chatPanel.UnclampedPanelHeightForTest;
            Check(chatPanel.CurrentTipsForTest.Any(t => t.Tip.Contains("не разрывает соединение", StringComparison.OrdinalIgnoreCase)), "expanded chat keeps explanatory tooltip on the 5-min button");
            using var chatBmp = new Bitmap(chatFile);
            Save(chatBmp, "tray-panel-chat-open-ru");
        }
        using (var fullBubblePanel = new TrayPopupForm(ctx, "panel"))
        {
            fullBubblePanel.SetComposerForPreview(true, "После проверки сохрани скриншот в папку отчётов");
            fullBubblePanel.SetChatBubbleExpandedForPreview("m2", true);
            string fullFile = Path.Combine(temp, "panel-chat-full.png");
            fullBubblePanel.RenderToBitmapFile(fullFile);
            Check(fullBubblePanel.UnclampedPanelHeightForTest > chatHeightCollapsedBubble, "clicking a long chat bubble expands the full model answer");
            using var fullBmp = new Bitmap(fullFile);
            Save(fullBmp, "tray-panel-chat-expanded-ru");
        }
        Check(ctx.UserMessageLink == null, "a cleared banner has no button");

        // Secret dialog has its own "Open key page" button so TopMost never hides it
        Check(DesktopLauncher.IsSafeHttpUrl("https://dashboard.ngrok.com/get-started/your-authtoken", out _) && !DesktopLauncher.IsSafeHttpUrl("file:///C:/Windows/win.ini", out _), "OpenUrl accepts only http(s) links");
        using (var secDlg = new ConnectionSecretDialog("ngrok", "Вставь Authtoken ngrok в это окно, не в чат. Это ключ провайдера, а не пароль MCP."))
        {
            Check(secDlg.ProviderKeyUrl == "https://dashboard.ngrok.com/get-started/your-authtoken", "ngrok secret dialog knows its key page URL");
            bool hasOpenBtn = secDlg.Controls.Cast<Control>().SelectMany(FlattenControls).OfType<Button>().Any(b => b.Text.Contains("Открыть страницу с ключом", StringComparison.OrdinalIgnoreCase));
            Check(hasOpenBtn, "ngrok secret dialog includes an Open key page button");
        }

        // Telegram one-way alerts: validation, secret redaction, updates parsing, formatting, config persistence, and Settings -> Notifications render
        Check(TelegramNotifier.IsValidBotToken("123456789:AAFakeBotTokenForTest_1234567890") && !TelegramNotifier.IsValidBotToken("not-a-token"), "Telegram bot token validation works");
        Check(TelegramNotifier.IsValidChatId("987654321") && TelegramNotifier.IsValidChatId("-1001234567890") && !TelegramNotifier.IsValidChatId("@username"), "Telegram chat ID requires numeric ID");
        string redacted = TelegramNotifier.RedactSecrets("token=supersecret123456 and 123456789:AAFakeBotTokenForTest_1234567890");
        Check(!redacted.Contains("supersecret123456") && !redacted.Contains("AAFakeBotToken"), "Telegram notifier redacts secrets before sending");
        var parsedChat = TelegramNotifier.ParseLatestChatFromUpdates("{\"ok\":true,\"result\":[{\"update_id\":1,\"message\":{\"chat\":{\"id\":777888999,\"username\":\"danya_dev\",\"first_name\":\"Danya\"}}}]}");
        Check(parsedChat.ChatId == "777888999" && parsedChat.Label == "@danya_dev", "Telegram getUpdates parser extracts chat_id and @username");
        string doneHtml = TelegramNotifier.FormatTaskDone("monday agent", "Hill Rider <lunar>", "All <b>tests</b> passed", 4);
        Check(doneHtml.Contains("&lt;lunar&gt;") && doneHtml.Contains("Задача выполнена") && doneHtml.Contains("4"), "Telegram task-done alert escapes HTML and formats summary");
        string aprHtml = TelegramNotifier.FormatApproval(new ApprovalRequestDto { Id = "a1", Tool = "run_command", Agent = "claude", TimeoutSec = 60, Summary = new ApprovalSummaryDto { Target = "rm -rf ./tmp" }, Assessment = new ApprovalAssessmentDto { Level = "high", Headline = "Deletes folders" } });
        Check(aprHtml.Contains("Высокий риск") && aprHtml.Contains("run_command"), "Telegram approval alert includes risk level and tool");

        string tgCfgPath = Path.Combine(temp, "telegram.json");
        using var tgNotifier = new TelegramNotifier(tgCfgPath);
        tgNotifier.UpdateConfig(new TelegramConfig { Enabled = true, BotToken = "123456789:AAFakeBotTokenForTest_1234567890", ChatId = "777888999", ChatLabel = "@danya_dev", NotifyTaskDone = true, NotifyAgentMessages = true, NotifyApprovals = true });
        var reloadedTg = TelegramNotifier.LoadFrom(tgCfgPath);
        Check(reloadedTg.Enabled && reloadedTg.IsConfigured && reloadedTg.ChatId == "777888999" && reloadedTg.ChatLabel == "@danya_dev", "Telegram config saves and reloads cleanly");
        Set(ctx, "_telegram", tgNotifier);
        using (var sf = new SettingsForm(ctx, "notifications"))
        {
            string notifFile = Path.Combine(temp, "settings-notifications-ru.png");
            sf.RenderPreviewToFile(notifFile, "notifications");
            using var nb = new Bitmap(notifFile);
            Save(nb, "settings-notifications-ru");
            Check(nb.Width >= 800 && nb.Height >= 600, "Settings -> Notifications renders with Telegram alerts section");
        }
        using (var tgDlg = new TelegramSettingsDialog(tgNotifier))
        {
            string dlgFile = Path.Combine(temp, "telegram-dialog-ru.png");
            tgDlg.RenderPreviewToFile(dlgFile);
            using var db = new Bitmap(dlgFile);
            Save(db, "telegram-dialog-ru");
            Check(db.Width == 600 && db.Height == 520, "Telegram settings dialog renders in the ZeroIDE app style");
        }
        RiskToggleFlow(temp);
    }

    static void RiskToggleFlow(string temp)
    {
        string permsPath = PermissionsStore.GetPermissionsPath();
        bool hadPerms = File.Exists(permsPath);
        string? permsBackup = hadPerms ? File.ReadAllText(permsPath) : null;
        try
        {
            var cfg = new PermissionsConfig();
            Check(PermissionsStore.IsHighRiskFilterEnabled(cfg) && PermissionsStore.IsCriticalGuardEnabled(cfg), "default risk toggles keep both High/Red-Flag and Critical protection enabled");

            PermissionsStore.ApplyRiskToggles(cfg, highOn: false, criticalOn: true, save: false);
            Check(!PermissionsStore.IsHighRiskFilterEnabled(cfg) && PermissionsStore.IsCriticalGuardEnabled(cfg)
                && !cfg.RedFlags.Enabled && !cfg.RedFlags.ElevateToAsk
                && cfg.RiskGate.Enabled && cfg.RiskGate.AskAtOrAbove == "critical" && cfg.RiskGate.GrantsNeverCover == "critical" && !cfg.RiskGate.SensitiveReads,
                "disabling High filter only keeps Critical escalation active");

            PermissionsStore.ApplyRiskToggles(cfg, highOn: false, criticalOn: false, save: false);
            Check(!PermissionsStore.IsHighRiskFilterEnabled(cfg) && !PermissionsStore.IsCriticalGuardEnabled(cfg)
                && !cfg.RiskGate.Enabled && cfg.RiskGate.AskAtOrAbove == "none" && cfg.RiskGate.GrantsNeverCover == "none",
                "disabling both High and Critical configures full autonomy");

            PermissionsStore.ApplyRiskToggles(cfg, highOn: true, criticalOn: false, save: false);
            Check(PermissionsStore.IsHighRiskFilterEnabled(cfg) && !PermissionsStore.IsCriticalGuardEnabled(cfg)
                && cfg.RedFlags.Enabled && cfg.RiskGate.Enabled && cfg.RiskGate.AskAtOrAbove == "high" && cfg.RiskGate.GrantsNeverCover == "none",
                "High-only mode keeps Red-Flag filter active while letting grants cover critical calls");

            PermissionsStore.ApplyRiskToggles(new PermissionsConfig(), highOn: true, criticalOn: true, save: true);
            using var toolsForm = new SettingsForm(FakeTray(Path.Combine(temp, "tools-risk")), "tools");
            toolsForm.PaintForTests(450);
            Check(toolsForm.ZoneIdsForTests.Contains("toggle_redflags") && toolsForm.ZoneIdsForTests.Contains("toggle_critical_guard"),
                "Settings -> Tools -> Execution renders both High/Red-Flag and Critical toggles");

            I18n.CurrentLanguage = "ru";
            string execPng = Path.Combine(temp, "settings-tools-exec-ru.png");
            toolsForm.RenderPreviewToFile(execPng, "tools", scrollY: 450);
            using (var eb = new Bitmap(execPng)) Save(eb, "settings-tools-exec-ru");

            // Also confirm small runner screens keep Execution toggles reachable when scrolled
            toolsForm.Size = new Size(1000, 680);
            toolsForm.PaintForTests(480);
            Check(toolsForm.ZoneIdsForTests.Contains("toggle_redflags") && toolsForm.ZoneIdsForTests.Contains("toggle_critical_guard"),
                "can inspect High/Red-Flag and Critical toggles on compact screens too");

            Check(toolsForm.InvokeZoneForTests("toggle_redflags"), "can click High/Red-Flag toggle in Settings");
            var afterHighOff = PermissionsStore.Load();
            Check(!PermissionsStore.IsHighRiskFilterEnabled(afterHighOff) && PermissionsStore.IsCriticalGuardEnabled(afterHighOff),
                "clicking High toggle persists High=off while preserving Critical=on");

            toolsForm.ConfirmDisableCriticalForTests = () => false;
            toolsForm.PaintForTests();
            toolsForm.InvokeZoneForTests("toggle_critical_guard");
            Check(PermissionsStore.IsCriticalGuardEnabled(PermissionsStore.Load()),
                "cancelling Critical protection warning keeps Critical guard ON");

            toolsForm.ConfirmDisableCriticalForTests = () => true;
            toolsForm.PaintForTests();
            toolsForm.InvokeZoneForTests("toggle_critical_guard");
            var fullAuto = PermissionsStore.Load();
            Check(!PermissionsStore.IsHighRiskFilterEnabled(fullAuto) && !PermissionsStore.IsCriticalGuardEnabled(fullAuto) && !fullAuto.RiskGate.Enabled,
                "confirming Critical disable with High=off writes full autonomy permissions");

            toolsForm.ConfirmDisableCriticalForTests = () => throw new InvalidOperationException("re-enabling Critical should not prompt");
            toolsForm.PaintForTests();
            toolsForm.InvokeZoneForTests("toggle_critical_guard");
            Check(PermissionsStore.IsCriticalGuardEnabled(PermissionsStore.Load()) && !PermissionsStore.IsHighRiskFilterEnabled(PermissionsStore.Load()),
                "re-enabling Critical works immediately without confirmation and leaves High=off untouched");
        }
        finally
        {
            if (hadPerms && permsBackup != null)
            {
                File.WriteAllText(permsPath, permsBackup);
                PermissionsStore.Load();
            }
            else
            {
                PermissionsStore.Save(new PermissionsConfig());
                try { if (File.Exists(permsPath)) File.Delete(permsPath); } catch { }
            }
        }
    }

    sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    static void UpdaterFlow(string temp)
    {
        var defCfg = new TrayConfig();
        Check(defCfg.CheckUpdatesEnabled && !defCfg.AutoUpdateEnabled, "update defaults: auto-check is ON, auto-install is OFF");
        Check(AppUpdater.RepoOwner == "Danywalery" && AppUpdater.RepoName == "ZeroIDE-MCP", "updater targets Danywalery/ZeroIDE-MCP");

        // SemVer comparison
        Check(AppUpdater.IsNewerVersion("v0.5.1", "0.5.0")
            && AppUpdater.IsNewerVersion("0.6.0", "0.5.0")
            && AppUpdater.IsNewerVersion("0.5.0", "0.5.0-rc.1")
            && !AppUpdater.IsNewerVersion("0.5.0", "0.5.0")
            && !AppUpdater.IsNewerVersion("0.4.9", "0.5.0")
            && !AppUpdater.IsNewerVersion("0.5.0-rc.2", "0.5.0"),
            "SemVer comparator handles tags, patch/minor bumps and prereleases");

        // Download URL allow-list
        Check(AppUpdater.IsTrustedDownloadUrl("https://github.com/Danywalery/ZeroIDE-MCP/releases/download/v0.6.0/zeroide-mcp.zip")
            && AppUpdater.IsTrustedDownloadUrl("https://objects.githubusercontent.com/github-production-release-asset/123")
            && !AppUpdater.IsTrustedDownloadUrl("http://github.com/Danywalery/ZeroIDE-MCP/releases/download/v0.6.0/zeroide-mcp.zip")
            && !AppUpdater.IsTrustedDownloadUrl("https://github.com.evil.example/zeroide-mcp.zip")
            && !AppUpdater.IsTrustedDownloadUrl("https://evil.example/zeroide-mcp.zip"),
            "only HTTPS GitHub release domains are trusted for update downloads");

        // GitHub release JSON parsing
        string relJson = """
        {
          "tag_name": "v0.6.0",
          "name": "ZeroIDE MCP v0.6.0",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/Danywalery/ZeroIDE-MCP/releases/tag/v0.6.0",
          "published_at": "2026-11-20T00:00:00Z",
          "body": "## What's new\n- Redesigned diff viewer and one-click updates\n",
          "assets": [
            { "name": "zeroide-mcp.zip", "browser_download_url": "https://github.com/Danywalery/ZeroIDE-MCP/releases/download/v0.6.0/zeroide-mcp.zip" },
            { "name": "zeroide-mcp.zip.sha256", "browser_download_url": "https://github.com/Danywalery/ZeroIDE-MCP/releases/download/v0.6.0/zeroide-mcp.zip.sha256" }
          ]
        }
        """;
        var parsed = AppUpdater.ParseReleaseJson(relJson);
        Check(parsed != null && parsed.Tag == "v0.6.0" && parsed.Version == "0.6.0"
            && parsed.ZipUrl.EndsWith("/zeroide-mcp.zip") && parsed.Sha256Url.EndsWith(".sha256")
            && parsed.NotesSummary.Contains("Redesigned diff viewer") && parsed.ReleaseNotes.Contains("What's new"),
            "GitHub release JSON parses tag, assets, release notes and prefers non-heading summary");
        Check(AppUpdater.ParseReleaseJson("{\"tag_name\":\"v0.6.0\",\"draft\":true}") == null
            && AppUpdater.ParseReleaseJson("{\"tag_name\":\"v0.6.0-rc.1\",\"prerelease\":true}") == null,
            "draft and prerelease payloads are ignored");

        I18n.CurrentLanguage = "ru";
        var demoSnap = AppUpdater.CreateDemoUpdateSnapshot();
        Check(demoSnap.HasUpdate && demoSnap.Latest is { IsDemo: true } && !string.IsNullOrWhiteSpace(demoSnap.Latest.ReleaseNotes),
            "demo update snapshot provides release notes and triggers the banner");
        using (var notesDlg = new UpdateNotesDialog(demoSnap.Latest!, demoSnap.CurrentVersion, showInstallButton: true))
        {
            string notesPng = Path.Combine(temp, "update-notes-dialog-ru.png");
            notesDlg.RenderPreviewToFile(notesPng);
            using var nb = new Bitmap(notesPng);
            Check(Near(nb.GetPixel(32, 22), Color.FromArgb(0x82, 0xEB, 0xC8)), "What's new dialog header shows the ZeroIDE logo");
            Save(nb, "update-notes-dialog-ru");
        }

        // Package zip extraction: valid package passes; Zip Slip and machine-local secrets are rejected
        string updDir = Path.Combine(temp, "updater");
        Directory.CreateDirectory(updDir);
        string goodZip = Path.Combine(updDir, "good.zip");
        using (var z = ZipFile.Open(goodZip, ZipArchiveMode.Create))
        {
            static void AddText(ZipArchive a, string name, string body)
            {
                var e = a.CreateEntry(name);
                using var sw = new StreamWriter(e.Open(), new UTF8Encoding(false));
                sw.Write(body);
            }
            AddText(z, "zeroide-mcp/ZeroIDE-MCP.exe", "MZ-stub");
            AddText(z, "zeroide-mcp/server/server.mjs", "// server");
            AddText(z, "zeroide-mcp/installer/install.ps1", "param([switch]$Update,[string]$InstallDir='')");
        }
        string goodSha = AppUpdater.ComputeFileSha256(goodZip);
        Check(AppUpdater.ParseExpectedSha256($"{goodSha}  zeroide-mcp.zip\n") == goodSha, "SHA-256 parser extracts exact 64-hex hash");
        string unpacked = AppUpdater.SafeExtractAndValidatePackage(goodZip, Path.Combine(updDir, "out-good"));
        Check(File.Exists(Path.Combine(unpacked, "ZeroIDE-MCP.exe")), "valid release package unpacks and validates cleanly");

        string slipZip = Path.Combine(updDir, "slip.zip");
        using (var z = ZipFile.Open(slipZip, ZipArchiveMode.Create))
            using (var sw = new StreamWriter(z.CreateEntry("../escape.txt").Open())) sw.Write("bad");
        bool slipRejected = false;
        try { AppUpdater.SafeExtractAndValidatePackage(slipZip, Path.Combine(updDir, "out-slip")); }
        catch (InvalidOperationException) { slipRejected = true; }
        Check(slipRejected, "Zip Slip traversal entry is rejected");

        string secretZip = Path.Combine(updDir, "secret.zip");
        using (var z = ZipFile.Open(secretZip, ZipArchiveMode.Create))
            using (var sw = new StreamWriter(z.CreateEntry("zeroide-mcp/server/config.env").Open())) sw.Write("MCP_TOKEN=x");
        bool secretRejected = false;
        try { AppUpdater.SafeExtractAndValidatePackage(secretZip, Path.Combine(updDir, "out-secret")); }
        catch (InvalidOperationException) { secretRejected = true; }
        Check(secretRejected, "archive carrying machine-local config.env is rejected");

        // Auto-update respects active MCP server: stages verified update and waits instead of interrupting agent
        byte[] goodZipBytes = File.ReadAllBytes(goodZip);
        bool serverOn = true;
        bool exitCalled = false;
        string mockInstall = Path.Combine(updDir, "install");
        Directory.CreateDirectory(mockInstall);
        using (var updater = new AppUpdater(
            mockInstall,
            () => Path.Combine(mockInstall, "logs"),
            () => serverOn,
            _ => { },
            () => exitCalled = true,
            new StubHttpHandler(req =>
            {
                string u = req.RequestUri!.AbsoluteUri;
                if (u.EndsWith("/releases/latest")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(relJson, Encoding.UTF8, "application/json") };
                if (u.EndsWith(".sha256")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(goodSha + "  zeroide-mcp.zip", Encoding.UTF8, "text/plain") };
                if (u.EndsWith(".zip")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(goodZipBytes) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })))
        {
            var st = Task.Run(() => updater.CheckAsync(autoUpdateEnabled: true, userInitiated: false)).GetAwaiter().GetResult();
            Check(st.Stage == UpdateStage.WaitingForServerOff && !exitCalled && Directory.Exists(st.PreparedPackageDir),
                "auto-update downloads & verifies SHA-256, then waits while MCP server is active");
        }

        // Apply script quotes paths safely and preserves ZEROIDE_INSTANCE_ID
        string script = AppUpdater.BuildApplyScript(1234, unpacked, @"C:\Users\Даня\AppData\Local\ZeroIDE-MCP", Path.Combine(updDir, "staging"), Path.Combine(updDir, "update.log"), "v05-sandbox");
        Check(script.Contains("-Update -InstallDir $installDir") && script.Contains("ZEROIDE_INSTANCE_ID") && script.Contains("C:\\Users\\Даня\\AppData\\Local\\ZeroIDE-MCP"),
            "apply-update.ps1 preserves instance id and handles Cyrillic install paths");

        // Settings UI: global update banner on Home + About updates card + dismiss behavior
        I18n.CurrentLanguage = "ru";
        var ctx = FakeTray(Path.Combine(temp, "upd-ui"));
        Set(ctx, "_state", "on");
        ctx.Updater.SetStateForPreview(new UpdateStateSnapshot(
            UpdateStage.Available,
            AppUpdater.CurrentVersion,
            new ReleaseAssetInfo(
                "v0.6.0",
                "0.6.0",
                "ZeroIDE MCP v0.6.0",
                "https://github.com/Danywalery/ZeroIDE-MCP/releases/tag/v0.6.0",
                "https://github.com/Danywalery/ZeroIDE-MCP/releases/download/v0.6.0/zeroide-mcp.zip",
                "https://github.com/Danywalery/ZeroIDE-MCP/releases/download/v0.6.0/zeroide-mcp.zip.sha256",
                "2026-11-20T00:00:00Z",
                "Новое окно сравнения файлов, уведомления в Telegram и автопроверка обновлений",
                demoSnap.Latest!.ReleaseNotes),
            0,
            "Доступна версия 0.6.0.",
            DateTimeOffset.Now,
            "",
            false));

        using (var settingsHome = new SettingsForm(ctx, "home"))
        {
            settingsHome.PaintForTests();
            var zones = settingsHome.ZoneIdsForTests;
            Check(zones.Contains("upd_banner_install") && zones.Contains("upd_banner_notes") && zones.Contains("upd_banner_dismiss"),
                "Settings displays the update available banner with Update, What's new and Dismiss actions");

            string homePng = Path.Combine(temp, "settings-update-banner-ru.png");
            settingsHome.RenderPreviewToFile(homePng, "home");
            using (var hb = new Bitmap(homePng)) Save(hb, "settings-update-banner-ru");

            string aboutPng = Path.Combine(temp, "settings-about-update-ru.png");
            settingsHome.RenderPreviewToFile(aboutPng, "about");
            using (var ab = new Bitmap(aboutPng)) Save(ab, "settings-about-update-ru");

            settingsHome.PaintForTests();
            var aboutZones = settingsHome.ZoneIdsForTests;
            if (!aboutZones.Contains("abt_upd_auto_toggle"))
            {
                settingsHome.PaintForTests(120);
                aboutZones = settingsHome.ZoneIdsForTests;
            }
            Check(aboutZones.Contains("abt_upd_install") && aboutZones.Contains("abt_upd_check") && aboutZones.Contains("abt_upd_notes") && aboutZones.Contains("abt_upd_check_toggle") && aboutZones.Contains("abt_upd_auto_toggle"),
                "Settings -> About shows update status, Install/Check/What's new buttons and both update toggles");
            settingsHome.Size = new Size(1000, 680);
            settingsHome.PaintForTests(240);
            Check(settingsHome.ZoneIdsForTests.Contains("abt_contact_tg"), "Settings -> About shows Telegram @danywalery contact link");

            settingsHome.NavigateToSection("home");
            settingsHome.PaintForTests();
            Check(settingsHome.InvokeZoneForTests("upd_banner_dismiss"), "dismissing update banner hides it for the session");
            settingsHome.PaintForTests();
            Check(!settingsHome.ZoneIdsForTests.Contains("upd_banner_install"), "dismissed update banner no longer renders on tabs");
        }
    }

    static IEnumerable<Control> FlattenControls(Control root)
    {
        yield return root;
        foreach (Control c in root.Controls)
            foreach (var sub in FlattenControls(c)) yield return sub;
    }
}
