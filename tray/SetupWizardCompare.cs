using System.Drawing;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

/// <summary>Card with a title, a tagline and a label/value grid: one connection option in the comparison screen.</summary>
internal sealed class WizardFacts : Control
{
    private readonly string _title, _tag, _badge; private readonly (string Label, string Value)[] _rows; private readonly Color _tone;
    private const float LabelW = 150;
    public WizardFacts(string title, string tag, string badge, (string Label, string Value)[] rows, Color tone, int width)
    {
        _title = title; _tag = tag; _badge = badge; _rows = rows; _tone = tone; Width = width; Margin = new Padding(0, 0, 0, 12);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        using var g = CreateGraphics();
        float h = 16 + 26 + WizardText.Height(g, tag, Theme.Small, width - 40) + 12;
        foreach (var r in rows) h += RowHeight(g, r);
        Height = (int)h + 14;
    }
    private float RowHeight(Graphics g, (string Label, string Value) r) => Math.Max(26, WizardText.Height(g, r.Value, Theme.Small, Width - 40 - LabelW - 12) + 10);
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg1); Theme.PrepareGraphics(g);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        Theme.FillRound(g, r, Theme.RadiusCard, Theme.Surface); Theme.StrokeRound(g, r, Theme.RadiusCard, Theme.WithAlpha(_tone, 110), 1.2f);
        Theme.FillRound(g, new RectangleF(r.X + 14, r.Y + 18, 4, 20), 2, _tone);
        Theme.DrawText(g, _title, Theme.BodyStrong, Theme.TextPrimary, new RectangleF(r.X + 30, r.Y + 14, r.Width - 60, 28));
        if (_badge.Length > 0)
        {
            float bw = Theme.MeasureText(g, _badge, Theme.Caption).Width + 20; var badge = new RectangleF(r.Right - bw - 16, r.Y + 17, bw, 22);
            Theme.FillRound(g, badge, 8, Theme.WithAlpha(_tone, 34)); Theme.DrawText(g, _badge, Theme.Caption, Theme.TextVariant(_tone), badge, StringAlignment.Center);
        }
        float y = r.Y + 44;
        float tagH = WizardText.Height(g, _tag, Theme.Small, r.Width - 40);
        Theme.DrawText(g, _tag, Theme.Small, Theme.TextSecondary, new RectangleF(r.X + 20, y, r.Width - 40, tagH + 4), StringAlignment.Near, StringAlignment.Near, false, true);
        y += tagH + 12;
        using var pen = new Pen(Theme.Divider);
        foreach (var row in _rows)
        {
            float h = RowHeight(g, row);
            g.DrawLine(pen, r.X + 20, y, r.Right - 20, y);
            Theme.DrawText(g, row.Label, Theme.SmallStrong, Theme.TextTertiary, new RectangleF(r.X + 20, y + 5, LabelW, h - 6), StringAlignment.Near, StringAlignment.Near);
            Theme.DrawText(g, row.Value, Theme.Small, Theme.TextPrimary, new RectangleF(r.X + 20 + LabelW + 12, y + 5, r.Width - 40 - LabelW - 12, h - 6), StringAlignment.Near, StringAlignment.Near, false, true);
            y += h;
        }
    }
}

internal sealed partial class SetupWizardForm
{
    private string _compareBack = "client";
    private void Facts(string tEn, string tRu, string tagEn, string tagRu, string badgeEn, string badgeRu, Color tone, params (string LEn, string LRu, string VEn, string VRu)[] rows)
        => _body.Controls.Add(new WizardFacts(I18n.T(tEn, tRu), I18n.T(tagEn, tagRu), I18n.T(badgeEn, badgeRu), rows.Select(r => (I18n.T(r.LEn, r.LRu), I18n.T(r.VEn, r.VRu))).ToArray(), tone, BodyWidth));

    private void StepCompare()
    {
        string port = _ctx != null ? _ctx.Config.Port.ToString() : "39011";
        Head("Connection options", "Варианты подключения", "How your AI reaches this PC. One is active at a time; you can switch later under Connection.", "Как твой AI достаёт до этого ПК. Одновременно активен один вариант; поменять можно потом в разделе «Подключение».");
        Note("The simple rule", "Простое правило",
            "An agent on this PC → only this computer. A web chat (ChatGPT, Claude, Notion) → ngrok. Your other computers → Tailscale. Your own domain → Cloudflare Tunnel.",
            "Агент на этом ПК → только этот компьютер. Веб-чат (ChatGPT, Claude, Notion) → ngrok. Другие твои компьютеры → Tailscale. Свой домен → Cloudflare Tunnel.", Theme.Accent);
        Facts("Only this computer", "Только этот компьютер", "The default. The server listens on 127.0.0.1 only; nothing leaves the PC.", "Вариант по умолчанию. Сервер слушает только 127.0.0.1; ничего не выходит за пределы ПК.", "Default", "По умолчанию", Theme.Success,
            ("Who can reach it", "Кто достучится", "Programs running on this PC: Claude Code, Cursor, Codex, Windsurf and other local agents.", "Программы на этом ПК: Claude Code, Cursor, Codex, Windsurf и другие локальные агенты."),
            ("Good for", "Для чего", "Working with a coding agent on the same machine.", "Работа с агентом для разработки на той же машине."),
            ("Address", "Адрес", "http://127.0.0.1:" + port + "/mcp, permanent.", "http://127.0.0.1:" + port + "/mcp, постоянный."),
            ("You need", "Что потребуется", "Nothing extra. No account, no internet.", "Ничего дополнительно. Ни аккаунта, ни интернета."));
        Facts("ngrok", "ngrok", "A public HTTPS address on the internet that forwards to this PC through ngrok's servers.", "Публичный HTTPS-адрес в интернете, который ведёт на этот ПК через серверы ngrok.", "Web chats", "Для веб-чатов", Theme.Warning,
            ("Who can reach it", "Кто достучится", "Anyone on the internet who knows the address. Entry still requires MCP authorization (OAuth you approve, or a token), and every action is logged.", "Любой в интернете, кто знает адрес. Войти всё равно можно только с авторизацией MCP (OAuth, который ты подтверждаешь, или токен); все действия в журнале."),
            ("Good for", "Для чего", "ChatGPT, Claude in the browser, Notion and other cloud services that support custom MCP connections.", "ChatGPT, Claude в браузере, Notion и другие облачные сервисы с поддержкой своих MCP-подключений."),
            ("Address and limits", "Адрес и лимиты", "A stable domain from your ngrok account. The free plan has request and traffic limits and shows a warning page on first sign-in (press Visit Site).", "Постоянный домен из твоего аккаунта ngrok. На бесплатном тарифе есть лимиты запросов и трафика, при первом входе показывается страница-предупреждение (нажми Visit Site)."),
            ("You need", "Что потребуется", "A free ngrok account: sign in, paste the key into the app's window, approve the launch. The app installs and runs ngrok itself.", "Бесплатный аккаунт ngrok: войти, вставить ключ в окно приложения, разрешить запуск. ngrok приложение ставит и запускает само."));
        Facts("Tailscale", "Tailscale", "A private network between your own devices. The address exists only inside it; the internet cannot see it.", "Частная сеть между твоими устройствами. Адрес существует только внутри неё; из интернета его не видно.", "Your other computers", "Для своих компьютеров", Theme.Info,
            ("Who can reach it", "Кто достучится", "Only devices signed in to your Tailscale account (up to 100 on the free plan).", "Только устройства, вошедшие в твой аккаунт Tailscale (до 100 на бесплатном тарифе)."),
            ("Good for", "Для чего", "An agent on your laptop or another PC controlling this one; several of your computers driven from one place. Not for ChatGPT or Claude in the browser: their servers are outside your network.", "Агент на ноутбуке или другом ПК управляет этим; несколько твоих компьютеров из одного места. Не для ChatGPT или Claude в браузере: их серверы вне твоей сети."),
            ("Address and limits", "Адрес и лимиты", "https://this-pc.your-net.ts.net/mcp, permanent, with a real HTTPS certificate. Direct device-to-device traffic, no limits.", "https://этот-пк.твоя-сеть.ts.net/mcp, постоянный, с настоящим HTTPS-сертификатом. Трафик напрямую между устройствами, без лимитов."),
            ("You need", "Что потребуется", "The Tailscale app on each device, signed in to the same account (the wizard installs it here). HTTPS certificates enabled once in the Tailscale console.", "Приложение Tailscale на каждом устройстве с входом в один аккаунт (здесь мастер поставит его сам). Один раз включить HTTPS-сертификаты в консоли Tailscale."));
        Facts("Cloudflare Tunnel", "Cloudflare Tunnel", "A public address on your own domain, served through Cloudflare.", "Публичный адрес на твоём домене, через Cloudflare.", "Own domain", "Свой домен", Theme.Accent,
            ("Who can reach it", "Кто достучится", "Anyone on the internet, like ngrok; MCP authorization is still required.", "Любой в интернете, как с ngrok; авторизация MCP обязательна."),
            ("Good for", "Для чего", "Web chats when you already have a domain in Cloudflare and want a nice permanent address without ngrok's limits.", "Веб-чаты, когда у тебя уже есть домен в Cloudflare и хочется красивый постоянный адрес без лимитов ngrok."),
            ("Address and limits", "Адрес и лимиты", "https://mcp.your-domain.com/mcp. Free on Cloudflare; no traffic limits for this use.", "https://mcp.твой-домен.com/mcp. Бесплатно в Cloudflare, лимитов трафика для такого использования нет."),
            ("You need", "Что потребуется", "A domain in Cloudflare. You create the tunnel and its public hostname in Zero Trust and copy the token; the app installs and runs cloudflared.", "Домен в Cloudflare. Туннель и public hostname создаёшь в Zero Trust и копируешь токен; cloudflared приложение ставит и запускает само."));
        Facts("Your own address", "Свой адрес", "Any other HTTPS address that already leads to this PC: your VPS with a reverse tunnel, another provider, a corporate proxy.", "Любой другой HTTPS-адрес, который уже ведёт на этот ПК: свой VPS с обратным туннелем, другой провайдер, корпоративный прокси.", "Advanced", "Для продвинутых", Theme.TextTertiary,
            ("Who can reach it", "Кто достучится", "Depends on your setup.", "Зависит от твоей схемы."),
            ("Good for", "Для чего", "When you run the transport yourself and only want the app to use and check the address.", "Когда транспорт ты держишь сам, а приложение должно лишь использовать и проверять адрес."),
            ("You need", "Что потребуется", "Enter the address; the app checks that it really points to this server before switching.", "Указать адрес; приложение проверит, что он ведёт именно на этот сервер, прежде чем переключиться."));
        Footer(Btn("Back", "Назад", () => Show(_compareBack), ButtonKind.Subtle));
    }
}
