using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

/// <summary>Provider step of the connect wizard: ngrok, Tailscale or Cloudflare Tunnel. Each runs the shipped
/// tools/Setup-*.ps1 helper (the same one agents use); key entry and launch consent are the app's own windows.</summary>
internal sealed partial class SetupWizardForm
{
    private sealed record ProviderSpec(string Script, string Switch, string TitleEn, string TitleRu, string SubEn, string SubRu, string LinkEn, string LinkRu, string LinkUrl, (string En, string Ru)[] Steps, string StartEn, string StartRu);

    private ProviderSpec Provider(string port) => _provider switch
    {
        "tailscale" => new("Setup-Tailscale.ps1", "-OwnerChoseTailscale", "Setting up Tailscale", "Настраиваем Tailscale",
            "Your computers see each other inside your private Tailscale network; no public address is created.", "Твои компьютеры видят друг друга внутри частной сети Tailscale; публичный адрес не создаётся.",
            "", "", "",
            [("Press Start. If Tailscale is missing, the app downloads the official installer and runs it: confirm the administrator prompt", "Нажми «Начать». Если Tailscale ещё нет, приложение скачает официальный установщик и запустит его: подтверди запрос администратора"),
             ("Sign in to Tailscale in the browser page that opens (skipped if you are already signed in)", "Войди в Tailscale на странице, которая откроется в браузере (пропускается, если уже вошёл)"),
             ("HTTPS certificates must be enabled in your tailnet: the wizard tells you if they are not", "В твоей сети Tailscale должны быть включены HTTPS-сертификаты: мастер подскажет, если это не так"),
             ("Approve the launch of Tailscale Serve in the app's window; the app starts and stops it with MCP", "Разреши запуск Tailscale Serve в окне приложения; приложение включает и выключает его вместе с MCP")],
            "Start", "Начать"),
        "cloudflare" => new("Setup-Cloudflare.ps1", "-OwnerChoseCloudflare", "Cloudflare Tunnel on your domain", "Cloudflare Tunnel на своём домене",
            "The tunnel is created in your Cloudflare account; this PC runs cloudflared for it.", "Туннель создаётся в твоём аккаунте Cloudflare; на этом ПК для него работает cloudflared.",
            "Open Cloudflare Zero Trust", "Открыть Cloudflare Zero Trust", "https://one.dash.cloudflare.com/",
            [("In Cloudflare Zero Trust open Networks → Tunnels, create a tunnel (Cloudflared) and copy its token", "В Cloudflare Zero Trust открой Networks → Tunnels, создай туннель (Cloudflared) и скопируй его токен"),
             ("Add a Public hostname on your domain (for example mcp.example.com) that points to http://localhost:" + port, "Добавь Public hostname на своём домене (например, mcp.example.com), ведущий на http://localhost:" + port),
             ("Enter that hostname below", "Укажи этот hostname ниже"),
             ("Press Start: the app downloads cloudflared, asks you to paste the token into a protected window and asks permission to launch it", "Нажми «Начать»: приложение скачает cloudflared, попросит вставить токен в защищённое окно и спросит разрешение на запуск")],
            "Start", "Начать"),
        _ => new("Setup-Ngrok.ps1", "-OwnerChoseNgrok", "Setting up ngrok", "Настраиваем ngrok",
            "Three steps. The ngrok key goes only into this app's window, never anywhere else.", "Три шага. Ключ ngrok вводится только в окно приложения и больше никуда.",
            "Open the ngrok key page", "Открыть страницу с ключом ngrok", "https://dashboard.ngrok.com/get-started/your-authtoken",
            [("Create a free ngrok account (or sign in) and keep the page with your Authtoken open", "Создай бесплатный аккаунт ngrok (или войди) и оставь открытой страницу с Authtoken"),
             ("Press Start: the app downloads ngrok from the official site, asks you to paste the key into a protected window and asks permission to launch it", "Нажми «Начать»: приложение скачает ngrok с официального сайта, попросит вставить ключ в защищённое окно и спросит разрешение на запуск")],
            "Start", "Начать"),
    };

    private void StepProvider()
    {
        var spec = Provider(_ctx!.Config.Port.ToString());
        Head(spec.TitleEn, spec.TitleRu, spec.SubEn, spec.SubRu);
        Space(6);
        var list = Checklist();
        TextBox? host = null;
        if (_provider == "cloudflare")
        {
            Para("Public hostname from Cloudflare", "Public hostname из Cloudflare", true, bottom: 4);
            host = new TextBox { Width = BodyWidth, Height = 34, Font = Theme.FontMono(10), BackColor = Theme.SurfaceRaised, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 10, 12), PlaceholderText = "mcp.example.com" };
            _body.Controls.Add(host);
        }
        WizardButton? start = null;
        bool running = false, finished = false;
        Label result = null!, progressTitle = null!; WizardChecklist progress = null!; FlowLayoutPanel actions = null!;
        start = Btn(spec.StartEn, spec.StartRu, () => _ = RunAsync(), ButtonKind.Primary);
        if (spec.LinkUrl.Length > 0)
        {
            Row(start, Btn(spec.LinkEn, spec.LinkRu, () =>
            {
                if (OpenUrl(spec.LinkUrl))
                {
                    if (!running) { result.ForeColor = Theme.SuccessText; result.Text = I18n.T("Opened in your browser: ", "Открыто в браузере: ") + spec.LinkUrl; }
                }
                else
                {
                    try { Clipboard.SetText(spec.LinkUrl); } catch { }
                    result.ForeColor = Theme.WarningText;
                    result.Text = I18n.T("Could not launch browser; the link was copied to the clipboard: ", "Не удалось открыть браузер — ссылка скопирована в буфер: ") + spec.LinkUrl;
                }
            }));
        }
        else Row(start);
        Small("Start switches MCP on if it is off. Nothing launches without your approval in the app's window.", "«Начать» включит MCP, если он выключен. Ничего не запустится без твоего разрешения в окне приложения.");
        result = Small("", "", Theme.TextPrimary, 8);
        progressTitle = Para("", "", true, bottom: 6); progressTitle.Visible = false;
        progress = new WizardChecklist(BodyWidth) { Visible = false }; _body.Controls.Add(progress);
        actions = Row();
        void Refresh()
        {
            bool on = _ctx.McpEnabled; var st = _ctx.ConnectionStatus();
            start!.Enabled = !running;
            var items = new List<WizardStep> { new(on ? I18n.T("MCP is on", "MCP включён") : I18n.T("MCP is switched on when you press Start", "MCP включится, когда нажмёшь «Начать»"), on ? "done" : "current", false) };
            for (int i = 0; i < spec.Steps.Length; i++)
            {
                bool last = i == spec.Steps.Length - 1;
                string state = finished ? "done" : running ? (last ? "current" : "done") : !on ? "todo" : last ? "todo" : "current";
                items.Add(new(I18n.T(spec.Steps[i].En, spec.Steps[i].Ru), state, true));
            }
            list.SetItems(items);
            if (st.Key == "guide")
            {
                progressTitle.Visible = progress.Visible = true;
                progressTitle.Text = st.Title + (st.Detail.Length > 0 ? "\n" + st.Detail : "");
                progress.SetItems(st.Steps.Select(s => new WizardStep(s.Text, s.State, s.Owner)));
            }
        }
        async Task RunAsync()
        {
            string args = spec.Switch + " -InstallDir '" + AppContext.BaseDirectory.TrimEnd('\\', '/').Replace("'", "''") + "'";
            if (host != null)
            {
                string h = host.Text.Trim();
                if (h.Length == 0 || h.Any(char.IsWhiteSpace) || h.Contains('\'')) { result.Text = I18n.T("Enter the public hostname first, for example mcp.example.com.", "Сначала укажи public hostname, например mcp.example.com."); result.ForeColor = Theme.WarningText; return; }
                args += " -Hostname '" + h + "'";
            }
            running = true; _busy = true; result.ForeColor = Theme.TextPrimary;
            result.Text = I18n.T("Working… watch for the app's windows and the browser.", "Работаем… следи за окнами приложения и браузером.");
            actions.Controls.Clear(); Refresh();
            // A previous attempt from this wizard may have left its progress card and an unfinished draft; the owner is retrying deliberately.
            _ctx.HideConnectionProgress();
            if (_ctx.ConnectionStatus().Key is "pending" or "pending-ready") _ctx.RestoreConnectionFromUi();
            if (!_ctx.McpEnabled) _ctx.StartServerFromUi(null);
            for (int i = 0; i < 40 && (!_ctx.McpEnabled || _ctx.ConnectionStatus().Key == "server-starting"); i++) await Task.Delay(500);
            var errors = new StringBuilder(); string stdout = "";
            try
            {
                var p = SetupLaunch.StartScript(Path.Combine(AppContext.BaseDirectory, "tools", spec.Script), args, AppContext.BaseDirectory);
                var readOut = p.StandardOutput.ReadToEndAsync(); var readErr = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(); stdout = await readOut; errors.Append(await readErr);
            }
            catch (Exception ex) { errors.AppendLine(ex.Message); }
            running = false; _busy = false;
            if (IsDisposed) return;
            string status = "", code = "", message = "", url = "";
            try
            {
                int a = stdout.IndexOf('{'), b = stdout.LastIndexOf('}');
                if (a >= 0 && b > a)
                {
                    using var doc = JsonDocument.Parse(stdout[a..(b + 1)]); var r = doc.RootElement;
                    string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    status = S("status"); code = S("code"); url = S("url"); message = S("ownerAction").Length > 0 ? S("ownerAction") : S("message");
                }
            }
            catch { }
            HandleResult(status, code, message, url);
            Refresh();
        }
        void HandleResult(string status, string code, string message, string url)
        {
            switch (status)
            {
                case "awaiting_client": finished = true; Show("handoff"); return;
                case "cancelled": result.Text = I18n.T("Cancelled. Nothing changed; you can start again.", "Отменено. Ничего не изменилось; можно начать снова."); result.ForeColor = Theme.WarningText; break;
                case "waiting_for_owner":
                    result.ForeColor = Theme.WarningText;
                    result.Text = code switch
                    {
                        "ENABLE_MCP" => I18n.T("Enable MCP, wait for it to start, then press Start again.", "Включи MCP, дождись запуска и нажми «Начать» ещё раз."),
                        "ENABLE_HTTPS" => I18n.T("HTTPS certificates are not enabled in your Tailscale network yet. Open the admin console, go to DNS → HTTPS Certificates → Enable, then press Start again.", "В твоей сети Tailscale ещё не включены HTTPS-сертификаты. Открой консоль, раздел DNS → HTTPS Certificates → Enable, затем нажми «Начать» ещё раз."),
                        "INSTALL_TAILSCALE" => I18n.T("Tailscale was not installed. Finish the installer, then press Start again.", "Tailscale не установился. Заверши установщик и нажми «Начать» ещё раз."),
                        "LOGIN_TAILSCALE" => I18n.T("Sign-in to Tailscale did not finish. Sign in (browser page or the Tailscale tray icon), then press Start again.", "Вход в Tailscale не завершился. Войди (страница в браузере или значок Tailscale в трее) и нажми «Начать» ещё раз."),
                        "HOSTNAME_REQUIRED" => I18n.T("Enter the public hostname configured in Cloudflare, for example mcp.example.com.", "Укажи public hostname, настроенный в Cloudflare, например mcp.example.com."),
                        _ => message,
                    };
                    if (url.Length > 0) actions.Controls.Add(Btn("Open the page", "Открыть страницу", () => OpenUrl(url), ButtonKind.Primary));
                    break;
                case "needs_attention":
                    result.Text = I18n.T("Setup stopped at step “" + code + "”. Nothing was rolled back and your current connection is unchanged. Open Connection to see the state, or hand the fix to an agent.", "Настройка остановилась на шаге «" + code + "». Ничего не откатывалось, текущее подключение не изменилось. Открой «Подключение», чтобы посмотреть состояние, или поручи починку агенту.");
                    result.ForeColor = Theme.DangerText;
                    actions.Controls.Add(Btn("Open Connection", "Открыть «Подключение»", () => _ctx.OpenSettingsWindow("connection")));
                    actions.Controls.Add(Btn("Copy a task for an agent", "Скопировать задание агенту", () => { try { Clipboard.SetText(_ctx.BuildAgentTask("fix")); } catch { } }));
                    break;
                default:
                    result.Text = I18n.T("The helper did not finish. Check that the installation is complete, then try again.", "Помощник не завершил работу. Проверь, что установка целая, и попробуй снова.");
                    result.ForeColor = Theme.DangerText; break;
            }
        }
        Refresh(); _tick = Refresh;
        Footer(Btn("Back", "Назад", () => { if (!running) Show("internet"); }, ButtonKind.Subtle));
    }
}
