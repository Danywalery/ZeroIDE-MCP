using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

/// <summary>Connect-mode steps of the setup wizard: run inside the installed tray and use its connection coordinator.</summary>
internal sealed partial class SetupWizardForm
{
    private string LocalUrl => $"http://127.0.0.1:{_ctx!.Config.Port}/mcp";
    private bool ClientConnected => _ctx!.ConnectionStatus().Key == "working";

    private void StepClient()
    {
        Head("Which AI do you want to connect?", "Какой AI подключаем?",
            "This decides whether the PC needs an internet address. You can change it later.", "От этого зависит, нужен ли компьютеру адрес в интернете. Потом можно поменять.");
        Space(6);
        Choice("An agent on this computer", "Агент на этом компьютере",
            "Claude Code, Cursor, Codex, Windsurf or another agent installed here. No internet address needed.",
            "Claude Code, Cursor, Codex, Windsurf или другой агент, установленный здесь. Адрес в интернете не нужен.",
            () => { _client = "local"; Show("local"); });
        Choice("Web chats with agents", "Веб-чаты с агентами",
            "ChatGPT online, Claude online, Notion and many other AI sites that let you add a custom MCP connection. They live in the cloud, so this PC needs an internet address.",
            "ChatGPT онлайн, Claude онлайн, Notion и многие другие сайты с ИИ, где можно добавить своё (custom) MCP-подключение. Они живут в облаке, поэтому этому ПК нужен адрес в интернете.",
            () => { _client = "web"; Show("internet"); });
        Choice("An agent on another computer of mine", "Агент на другом моём компьютере", "A private network between your computers (Tailscale) or an internet address will do.", "Подойдёт частная сеть между твоими компьютерами (Tailscale) или адрес в интернете.",
            () => { _client = "network"; Show("internet"); });
        Small("Not sure yet? Access stays limited to this PC. You can open this wizard again under Connection in the app.", "Пока не решил? Доступ останется только с этого ПК. Мастер можно открыть снова в разделе «Подключение».");
        Footer(Btn("Decide later", "Решу позже", Close, ButtonKind.Subtle), Btn("About the connection options", "Подробнее о вариантах подключения", () => { _compareBack = "client"; Show("compare"); }));
    }

    private void StepLocal()
    {
        Head("Connect on this PC", "Подключение на этом ПК", "An agent on this computer needs no internet address.", "Агенту на этом компьютере адрес в интернете не нужен.");
        Space(6);
        var list = Checklist();
        WizardButton? enable = null;
        enable = Btn("Enable MCP", "Включить MCP", () => { _ctx!.StartServerFromUi(null); enable!.Enabled = false; }, ButtonKind.Primary);
        Row(enable);
        Para("URL for the agent", "Ссылка для агента", true, bottom: 4);
        CodeBox(LocalUrl);
        Para("In your agent's settings add an MCP server of the HTTP type with this URL. On first use the agent asks to sign in: approve the access card that appears in ZeroIDE MCP. If the agent needs a token instead of sign-in, create one in Access and paste it into the agent.",
            "В настройках агента добавь MCP-сервер типа HTTP с этой ссылкой. При первом обращении агент попросит войти: подтверди карточку доступа, которая появится в ZeroIDE MCP. Если агенту нужен токен вместо входа, создай его в разделе «Доступ» и вставь в агент.");
        Row(Btn("Create a token", "Создать токен", () => _ctx!.OpenSettingsWindow("access")));
        void Refresh()
        {
            bool on = _ctx!.McpEnabled; bool seen = ClientConnected;
            enable!.Enabled = !on;
            list.SetItems([
                new(on ? I18n.T("MCP is on", "MCP включён") : I18n.T("Enable MCP: the server runs only while you keep it on", "Включи MCP: сервер работает только пока ты его не выключил"), on ? "done" : "current", true),
                new(I18n.T("Add the MCP server in your agent using the URL below", "Добавь MCP-сервер в своём агенте по ссылке ниже"), !on ? "todo" : seen ? "done" : "current", true),
                new(seen ? I18n.T("Your agent has connected", "Агент подключился") : I18n.T("Waiting for the first connection… this page updates by itself", "Ждём первое подключение… страница обновится сама"), seen ? "done" : "todo")]);
        }
        Refresh(); _tick = Refresh;
        Footer(Btn("Back", "Назад", () => Show("client"), ButtonKind.Subtle), Btn("Done", "Готово", () => Show("done"), ButtonKind.Primary));
    }

    private void StepInternet()
    {
        Head("Access from the internet", "Доступ из интернета",
            "A web chat lives in the cloud, so it needs an HTTPS address that leads to this PC.", "Веб-чат живёт в облаке, поэтому ему нужен HTTPS-адрес, который ведёт на этот ПК.");
        Space(6);
        if (_ctx!.ConnectionIsPublic)
        {
            Note("Already set up", "Уже настроено", "This PC already has an internet address (" + _ctx.ConnectionKindTitle + "):\n" + _ctx.ConnectionUrl, "У этого ПК уже есть адрес в интернете (" + _ctx.ConnectionKindTitle + "):\n" + _ctx.ConnectionUrl, Theme.Success);
            Choice("Continue to the AI client", "Перейти к подключению клиента", "Use the existing address.", "Использовать существующий адрес.", () => Show("handoff"), true);
            Choice("Change the connection", "Поменять подключение", "Enter a different address or hand the change to an agent. The working address stays until the new one is checked.", "Указать другой адрес или поручить замену агенту. Рабочий адрес сохраняется, пока новый не проверен.", () => { _provider = "external"; Show("other"); });
            Footer(Btn("Back", "Назад", () => Show("client"), ButtonKind.Subtle));
            return;
        }
        Note("Before you continue", "Прежде чем продолжить",
            "An AI that uses this address can control this PC and change files. Only a client with a token or a sign-in you approved can get in. Sensitive actions wait for your confirmation and everything is logged. Switch MCP off or exit the tray, and access through this app is closed.",
            "AI по этому адресу сможет управлять этим ПК и менять файлы. Войти сможет только клиент с токеном или с подтверждённым тобой входом. Важные действия ждут твоего подтверждения, всё записывается в журнал. Выключил MCP или вышел из трея — доступ через приложение закрыт.", Theme.Warning);
        Para("Recommended: ngrok", "Рекомендуем: ngrok", true, bottom: 6);
        Para("No domain of your own is needed and the app does the technical part. You need a free ngrok account; the free plan has limits, but they are fine for personal use.",
            "Свой домен не нужен, техническую часть делает приложение. Понадобится бесплатный аккаунт ngrok; у бесплатного тарифа есть ограничения, для личного использования их хватает.", bottom: 14);
        Choice("Let's go with ngrok", "Поехали с ngrok", "About two minutes: sign in to ngrok, paste the key into the app window, approve the launch.", "Около двух минут: войти в ngrok, вставить ключ в окно приложения, разрешить запуск.", () => { _provider = "ngrok"; Show("provider"); }, true);
        if (_client == "network")
            Choice("Tailscale: a private network between my computers", "Tailscale: частная сеть между моими компьютерами", "No public address: the other computer must be in the same Tailscale network.", "Без публичного адреса: другой компьютер должен быть в той же сети Tailscale.", () => { _provider = "tailscale"; Show("provider"); });
        Choice("I have my own domain (Cloudflare Tunnel)", "У меня свой домен (Cloudflare Tunnel)", "A named tunnel on your domain. You set it up in Cloudflare and enter the address here.", "Именованный туннель на твоём домене. Настраивается в Cloudflare, адрес указывается здесь.", () => { _provider = "cloudflare"; Show("provider"); });
        Choice("I already have an address", "У меня уже есть адрес", "Another service or your own server already gives an HTTPS address that leads to this PC.", "Другой сервис или свой сервер уже даёт HTTPS-адрес, ведущий на этот ПК.", () => { _provider = "external"; Show("other"); });
        Footer(Btn("Back", "Назад", () => Show("client"), ButtonKind.Subtle), Btn("Compare the options", "Сравнить варианты", () => { _compareBack = "internet"; Show("compare"); }));
    }

    private void StepOther()
    {
        string port = _ctx!.Config.Port.ToString();
        switch (_provider)
        {
            case "tailscale":
                Head("Private network with Tailscale", "Частная сеть Tailscale", "The other computer reaches this PC inside your Tailscale network, without a public address.", "Другой компьютер достаёт до этого ПК внутри твоей сети Tailscale, без публичного адреса.");
                Para("Install Tailscale on both computers and sign in to the same network. The MCP server listens only on this PC (127.0.0.1:" + port + "), so publish it inside the network with Tailscale Serve pointing to http://127.0.0.1:" + port + ". When the address works, enter it here: the app checks it before switching.",
                    "Установи Tailscale на обоих компьютерах и войди в одну сеть. MCP-сервер слушает только этот ПК (127.0.0.1:" + port + "), поэтому опубликуй его внутри сети через Tailscale Serve, направив на http://127.0.0.1:" + port + ". Когда адрес заработает, укажи его здесь: приложение проверит его перед переключением.");
                break;
            case "cloudflare":
                Head("Cloudflare Tunnel on your domain", "Cloudflare Tunnel на своём домене", "You create the tunnel in Cloudflare; the app checks the address and uses it.", "Туннель создаёшь в Cloudflare, приложение проверяет адрес и использует его.");
                Para("In Cloudflare Zero Trust create a named tunnel and point its public hostname to http://127.0.0.1:" + port + ". When the hostname works, enter it here: the app checks it before switching.",
                    "В Cloudflare Zero Trust создай именованный туннель и направь его публичный hostname на http://127.0.0.1:" + port + ". Когда адрес заработает, укажи его здесь: приложение проверит его перед переключением.");
                break;
            default:
                Head("Your own address", "Свой адрес", "Another service or server already leads to this PC.", "Другой сервис или сервер уже ведёт на этот ПК.");
                Para("Enter the HTTPS address that reaches http://127.0.0.1:" + port + " on this PC. The app checks it before switching; the current connection keeps working until then.",
                    "Укажи HTTPS-адрес, который ведёт на http://127.0.0.1:" + port + " этого ПК. Приложение проверит его перед переключением; до этого текущее подключение продолжает работать.");
                break;
        }
        Para("Prefer not to do it by hand? An agent on this PC can set the provider up for you: copy the task and paste it into its chat.",
            "Не хочется делать это руками? Агент на этом ПК может настроить провайдера за тебя: скопируй задание и вставь в его чат.", color: Theme.TextSecondary);
        var copied = Small("", "", Theme.SuccessText, 6);
        var statusNote = Small("", "", Theme.SuccessText, 6);
        Row(Btn("Enter the address", "Указать адрес", async () => { await _ctx.ChangeConnectionAddressFromUi(this); }, ButtonKind.Primary),
            Btn("Copy a task for an agent", "Скопировать задание агенту", () => { try { Clipboard.SetText(_ctx.BuildAgentTask("type")); copied.Text = I18n.T("Task copied. Paste it into an agent running on this PC.", "Задание скопировано. Вставь его агенту, запущенному на этом ПК."); } catch { } }));
        Small("Details for agents: docs\\AGENT_CONNECTIONS.md in the installation folder.", "Подробности для агентов: docs\\AGENT_CONNECTIONS.md в папке установки.");
        var next = Btn("Next", "Дальше", () => Show("handoff"), ButtonKind.Primary);
        void Refresh()
        {
            var st = _ctx.ConnectionStatus();
            bool ready = _ctx.ConnectionIsPublic && st.Key is not ("pending" or "pending-ready");
            next.Enabled = ready;
            statusNote.Text = st.Key is "pending" or "pending-ready" or "handover" ? st.Title + (st.Detail.Length > 0 ? " · " + st.Detail.Replace("\n", " ") : "") : ready ? I18n.T("Address in use: ", "Используется адрес: ") + _ctx.ConnectionUrl : "";
            statusNote.ForeColor = ready ? Theme.SuccessText : Theme.WarningText;
        }
        Refresh(); _tick = Refresh;
        Footer(Btn("Back", "Назад", () => Show("internet"), ButtonKind.Subtle), next, Btn("Open Connection", "Открыть «Подключение»", () => _ctx.OpenSettingsWindow("connection")));
    }

    private void StepHandoff()
    {
        bool local = !_ctx!.ConnectionIsPublic;
        Head("Connect your AI", "Подключи свой AI", "Add this PC as an MCP server in your AI client. The page updates by itself when it connects.", "Добавь этот ПК как MCP-сервер в своём AI-клиенте. Страница сама обновится, когда он подключится.");
        Space(6);
        var list = Checklist();
        WizardButton? enable = null;
        enable = Btn("Enable MCP", "Включить MCP", () => { _ctx.StartServerFromUi(null); enable!.Enabled = false; });
        Para("MCP URL", "Ссылка MCP", true, bottom: 4);
        CodeBox(_ctx.ConnectionUrl);
        Bullets(("1.  In your AI's settings open connectors, integrations or MCP servers and choose to add a custom MCP server.", "1.  В настройках своего AI открой коннекторы, интеграции или MCP-серверы и выбери «добавить свой MCP-сервер»."),
            ("2.  Paste the URL above. Any name will do, for example “My PC”.", "2.  Вставь ссылку выше. Название любое, например «Мой ПК»."),
            ("3.  Choose sign-in (OAuth) if it is offered: a sign-in page opens and an access card appears in ZeroIDE MCP. Approve it. If the client asks for a token instead, create one below and paste it into the client.", "3.  Выбери вход (OAuth), если он предлагается: откроется страница входа, а в ZeroIDE MCP появится карточка доступа. Подтверди её. Если клиент просит токен, создай его кнопкой ниже и вставь в клиент."));
        if (_ctx.ConnectionProviderName.Contains("ngrok", StringComparison.OrdinalIgnoreCase))
            Small("If the browser shows an ngrok warning page during sign-in, press Visit Site.", "Если при входе браузер покажет страницу-предупреждение ngrok, нажми Visit Site.");
        Row(enable, Btn("Create a token", "Создать токен", () => _ctx.OpenSettingsWindow("access")));
        var live = Para("", "", true, bottom: 6);
        void Refresh()
        {
            bool on = _ctx.McpEnabled; bool seen = ClientConnected;
            enable!.Enabled = !on;
            list.SetItems([
                new(on ? I18n.T("MCP is on", "MCP включён") : I18n.T("Enable MCP", "Включи MCP"), on ? "done" : "current", true),
                new(I18n.T("Add the MCP server in your AI client", "Добавь MCP-сервер в своём AI-клиенте"), !on ? "todo" : seen ? "done" : "current", true),
                new(seen ? I18n.T("Your AI has connected", "AI подключился") : I18n.T("Waiting for the first connection…", "Ждём первое подключение…"), seen ? "done" : "todo")]);
            live.Text = seen ? I18n.T("✓ Your AI has connected to this PC.", "✓ AI подключился к этому ПК.") : I18n.T("Waiting for the first connection…", "Ждём первое подключение…");
            live.ForeColor = seen ? Theme.SuccessText : Theme.TextSecondary;
            if (seen) _ctx.HideConnectionProgress();
        }
        Refresh(); _tick = Refresh;
        Footer(Btn("Back", "Назад", () => Show(local ? "client" : "internet"), ButtonKind.Subtle), Btn("Done", "Готово", () => Show("done"), ButtonKind.Primary));
    }

    private void StepDone()
    {
        Head("All set", "Всё готово", "", "");
        Space(6);
        Note("", "", I18n.T("AI access to this PC: ", "Доступ AI к этому ПК: ") + _ctx!.ConnectionKindTitle + "\n" + _ctx.ConnectionUrl, I18n.T("AI access to this PC: ", "Доступ AI к этому ПК: ") + _ctx.ConnectionKindTitle + "\n" + _ctx.ConnectionUrl, Theme.Success);
        Para("Good to know", "Полезно знать", true, bottom: 8);
        Bullets(("Switch MCP off in the tray when the AI should not reach this PC; exiting the tray closes access too.", "Выключай MCP в трее, когда AI не должен доставать до этого ПК; выход из трея тоже закрывает доступ."),
            ("Confirmations of commands and file changes appear in the tray app; the action log is there as well.", "Подтверждения команд и изменений файлов появляются в приложении в трее; там же журнал действий."),
            ("Connection shows the address and its check. Access shows connected clients, tokens and revocation.", "«Подключение» показывает адрес и его проверку. «Доступ» — подключённые клиенты, токены и отзыв доступа."),
            ("This wizard is always available again under Connection.", "Этот мастер всегда можно открыть снова в разделе «Подключение»."));
        _ctx.HideConnectionProgress();
        Footer(null, Btn("Close", "Закрыть", Close, ButtonKind.Primary));
    }
}
