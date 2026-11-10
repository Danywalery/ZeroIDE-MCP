using System.Drawing;
using System.Windows.Forms;
namespace ZeroIDE.Mcp;

internal sealed partial class SettingsForm
{
    private bool _connectionHelp, _connectionTaskCopied;
    private string _connectionGoal = "url";
    private static SizeF ConnectionMeasure(Graphics g, string text, System.Drawing.Font font, float width)
    {
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags &= ~StringFormatFlags.NoWrap;
        return g.MeasureString(text, font, new SizeF(Math.Max(1, width), 10000), format);
    }
    private static float ConnectionButtonWidth(Graphics g, string label) => Math.Max(76, Theme.MeasureText(g, label, PanelTypography.Get(12.5f, true)).Width + 30);
    private float ConnectionText(Graphics g, string text, System.Drawing.Font font, Color color, float x, float y, float width)
    {
        float height = Math.Max(font.Height + 3, ConnectionMeasure(g, text, font, width).Height + 6);
        Theme.DrawText(g, text, font, color, new RectangleF(x, y, width, height), vertical: StringAlignment.Near, wrap: true);
        return height;
    }
    private float ConnectionButton(Graphics g, float x, float y, string label, string id, Action action, bool primary = false, float maxWidth = 10000)
    {
        float width = Math.Min(maxWidth, ConnectionButtonWidth(g, label));
        var rect = new RectangleF(x, y, width, 32);
        Theme.DrawButton(g, rect, label, primary ? ButtonKind.Primary : ButtonKind.Secondary, rect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal, PanelTypography.Get(12.5f, true));
        _hitZones.Add(new HitZone(rect, id, action));
        return width;
    }
    private async void ConnectionPanelAction(string id)
    {
        switch (id)
        {
            case "power_on": _ctx.StartServerFromUi(null); break;
            case "copy": if (!string.IsNullOrEmpty(_ctx.ConnectionUrl)) Clipboard.SetText(_ctx.ConnectionUrl); break;
            case "check": await _ctx.CheckConnectionFromUi(); break;
            case "restart": _ctx.RestartTunnelFromUi(); break;
            case "activate": await _ctx.ActivatePendingFromUi(); break;
            case "cancel": _ctx.RestoreConnectionFromUi(); break;
            case "retire": _ctx.RetirePreviousConnectionFromUi(); break;
            case "restore": _ctx.RestoreConnectionFromUi(); break;
            case "guide_hide": _ctx.HideConnectionProgress(); break;
            default:
                if (id.StartsWith("agent_")) { _connectionGoal = id[6..]; _ctx.CopyConnectionAgentTask(_connectionGoal); _connectionTaskCopied = true; }
                break;
        }
        Invalidate();
    }
    private float DrawConnectionContent(Graphics g, float x, float w, float y)
    {
        var status = _ctx.ConnectionStatus();
        Color tone = status.Tone switch { ConnectionTone.Good => ColGreen, ConnectionTone.Error => ColRed, ConnectionTone.Warning or ConnectionTone.Waiting => ColOrange, ConnectionTone.Neutral => ColTextSecondary, _ => ColBlue };
        float inner = w - 40;
        float titleH = Math.Max(28, ConnectionMeasure(g, status.Title, UiBodyStrong, inner - 24).Height + 6);
        float detailH = Math.Max(22, ConnectionMeasure(g, status.Detail, UiSmall, inner).Height + 6);
        float stepsH = status.Steps.Sum(step => Math.Max(26, ConnectionMeasure(g, step.Text, UiSmall, inner - 88).Height + 8));
        int actionRows = 0; float actionUsed = 0;
        foreach (var action in status.Actions) { float aw = Math.Min(inner, ConnectionButtonWidth(g, action.Label)); if (actionRows == 0 || actionUsed + aw > inner) { actionRows++; actionUsed = 0; } actionUsed += aw + 10; }
        float buttonsH = actionRows * 42;
        float bannerH = 36 + titleH + detailH + stepsH + buttonsH;
        DrawCardSurface(g, new RectangleF(x, y, w, bannerH));
        Theme.StrokeRound(g, new RectangleF(x, y, w, bannerH), 16, Theme.WithAlpha(tone, 100), 1);
        Theme.DrawStatusDot(g, new PointF(x + 24, y + 28), 4, tone, false);
        ConnectionText(g, status.Title, UiBodyStrong, ColTextPrimary, x + 40, y + 16, inner - 24);
        float cursor = y + 16 + titleH;
        ConnectionText(g, status.Detail, UiSmall, ColTextSecondary, x + 20, cursor, inner); cursor += detailH + 4;
        foreach (var step in status.Steps)
        {
            Color color = step.State == "done" ? ColGreen : step.State == "current" ? ColBlue : ColTextSecondary;
            Theme.DrawText(g, step.State == "done" ? "✓" : step.State == "current" ? "●" : "○", UiSmallStrong, color, new RectangleF(x + 20, cursor, 22, 24));
            float h = ConnectionText(g, step.Text, UiSmall, step.State == "todo" ? ColTextSecondary : ColTextPrimary, x + 46, cursor + 2, inner - 88);
            if (step.Owner) { Theme.FillRound(g, new RectangleF(x + w - 64, cursor, 44, 24), 8, Theme.WithAlpha(ColOrange, 30)); Theme.DrawText(g, I18n.T("YOU", "ТЫ"), UiCaption, Theme.TextVariant(ColOrange), new RectangleF(x + w - 64, cursor, 44, 24), StringAlignment.Center); }
            cursor += Math.Max(26, h + 2);
        }
        if (status.Actions.Length > 0)
        {
            cursor += 6; float bx = x + 20;
            foreach (var action in status.Actions)
            {
                float aw = Math.Min(inner, ConnectionButtonWidth(g, action.Label));
                if (bx > x + 20 && bx + aw > x + w - 20) { bx = x + 20; cursor += 42; }
                bx += ConnectionButton(g, bx, cursor, action.Label, "connection_status_" + action.Id, () => ConnectionPanelAction(action.Id), action.Primary, inner) + 10;
            }
        }
        y += bannerH + 20;
        y = DrawGroupTitle(g, x, y, I18n.T("CONNECTION TYPE", "ТИП ПОДКЛЮЧЕНИЯ"));
        float kindTitleH = Math.Max(26, ConnectionMeasure(g, _ctx.ConnectionKindTitle, UiBodyStrong, inner).Height + 6);
        float kindH = 34 + kindTitleH + Math.Max(26, ConnectionMeasure(g, _ctx.ConnectionKindDescription, UiSmall, inner).Height + 8);
        DrawCardSurface(g, new RectangleF(x, y, w, kindH));
        ConnectionText(g, _ctx.ConnectionKindTitle, UiBodyStrong, ColTextPrimary, x + 20, y + 14, inner);
        ConnectionText(g, _ctx.ConnectionKindDescription, UiSmall, ColTextSecondary, x + 20, y + 20 + kindTitleH, inner); y += kindH + 20;
        y = DrawGroupTitle(g, x, y, I18n.T("MCP CONNECTION URL", "ССЫЛКА MCP ДЛЯ ПОДКЛЮЧЕНИЯ"));
        string url = _ctx.ConnectionUrl;
        float urlH = Math.Max(32, ConnectionMeasure(g, url, UiBody, inner).Height + 8);
        float checkH = Math.Max(20, ConnectionMeasure(g, _ctx.ConnectionCheck, UiCaption, inner).Height + 6);
        float linkH = 28 + urlH + 46 + checkH;
        DrawCardSurface(g, new RectangleF(x, y, w, linkH));
        ConnectionText(g, url.Length > 0 ? url : I18n.T("Not configured yet", "Пока не настроена"), UiBody, ColCyan, x + 20, y + 14, inner);
        float ax = x + 20, ay = y + 14 + urlH;
        ax += ConnectionButton(g, ax, ay, I18n.T("Copy", "Копировать"), "connection_copy", () => ConnectionPanelAction("copy"), true) + 10;
        ax += ConnectionButton(g, ax, ay, I18n.T("Check", "Проверить"), "connection_check", () => ConnectionPanelAction("check")) + 10;
        ConnectionButton(g, ax, ay, I18n.T("Change URL", "Изменить ссылку"), "connection_change", async () => { await _ctx.ChangeConnectionAddressFromUi(this); Invalidate(); });
        ConnectionText(g, _ctx.ConnectionCheck, UiCaption, ColTextSecondary, x + 20, ay + 42, inner); y += linkH + 20;
        y = DrawGroupTitle(g, x, y, I18n.T("CHANGE WITH YOUR AGENT", "ПОМЕНЯТЬ ЧЕРЕЗ АГЕНТА"));
        string[] goals = ["url", "type", "fix", "local"];
        string[] labels = [I18n.T("Different URL", "Другая ссылка"), I18n.T("Different type", "Другой тип"), I18n.T("Fix connection", "Не работает — починить"), I18n.T("Only this PC", "Только этот ПК")];
        float chipX = x + 20, chipY = y + 56;
        float[] widths = labels.Select(label => ConnectionButtonWidth(g, label)).ToArray();
        int rows = 1; float used = 0;
        foreach (float cw in widths) { if (used > 0 && used + cw > inner) { rows++; used = 0; } used += cw + 8; }
        string hint = _connectionTaskCopied ? I18n.T("Task copied — paste it into your agent on this PC.", "Задание скопировано — вставь его агенту на этом ПК.") : I18n.T("The button copies a task, it does not launch an agent. Paste it into your agent's chat.", "Кнопка копирует задание, а не запускает агента. Вставь его в чат своего агента.");
        float hintH = Math.Max(34, ConnectionMeasure(g, hint, UiCaption, inner).Height + 6);
        float agentH = 56 + rows * 40 + 46 + hintH + 14;
        DrawCardSurface(g, new RectangleF(x, y, w, agentH));
        ConnectionText(g, I18n.T("What would you like to change?", "Что нужно сделать?"), UiBodyStrong, ColTextPrimary, x + 20, y + 16, inner);
        for (int i = 0; i < goals.Length; i++)
        {
            if (chipX > x + 20 && chipX + widths[i] > x + w - 20) { chipX = x + 20; chipY += 40; }
            string goal = goals[i]; ConnectionButton(g, chipX, chipY, labels[i], "connection_goal_" + goal, () => { _connectionGoal = goal; _connectionTaskCopied = false; Invalidate(); }, _connectionGoal == goal); chipX += widths[i] + 8;
        }
        float copyY = y + 56 + rows * 40;
        ConnectionButton(g, x + 20, copyY, I18n.T("Copy task for agent", "Скопировать задание для агента"), "connection_agent_copy", () => { _ctx.CopyConnectionAgentTask(_connectionGoal); _connectionTaskCopied = true; Invalidate(); }, true);
        ConnectionText(g, hint, UiCaption, _connectionTaskCopied ? ColGreen : ColTextSecondary, x + 20, copyY + 42, inner); y += agentH + 16;
        ConnectionButton(g, x, y, (_connectionHelp ? "− " : "+ ") + I18n.T("How this works for you", "Как это устроено у тебя"), "connection_help", () => { _connectionHelp = !_connectionHelp; Invalidate(); }); y += 44;
        if (_connectionHelp)
        {
            string note = _ctx.ConnectionGuideNote;
            string help = (note.Length > 0 ? note + "\n\n" : "") + I18n.T("A URL does not grant access. Only an AI with an MCP token or an approved sign-in can enter. Sensitive actions require confirmation; actions are journaled. Exiting the tray disconnects AI from this PC. Provider keys are not MCP tokens.\nA local check does not prove that an AI in the cloud can connect.", "Ссылка не даёт прав. Войти может только AI с токеном MCP или разрешённым тобой входом. Опасные действия подтверждаются; действия пишутся в журнал. Выход из трея отключает AI от ПК. Ключ провайдера — не токен MCP.\nПроверка с этого ПК ещё не подтверждает доступ AI из облака.");
            y += ConnectionText(g, help, UiSmall, ColTextSecondary, x + 4, y, w - 8) + 12;
            ConnectionButton(g, x, y, I18n.T("Advanced…", "Дополнительно…"), "connection_advanced", () => { _ctx.ShowConnectionControls(this); Invalidate(); }); y += 46;
        }
        string footer = I18n.T("Connected clients and how to disconnect them — in Access →", "Кто подключён и как отключить клиента — в разделе «Доступ» →");
        float footerH = ConnectionText(g, footer, UiSmall, ColCyan, x + 4, y, w - 8);
        _hitZones.Add(new HitZone(new RectangleF(x, y, w, footerH), "connection_access", () => NavigateToSection("access")));
        return y + footerH + 24;
    }
}
