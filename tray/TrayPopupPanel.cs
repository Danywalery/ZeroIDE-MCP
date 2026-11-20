using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

internal sealed class TrayPopupForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly TrayContext _ctx;
    private readonly string _mode;
    private PointF _mousePos = new(-1, -1);
    private int _hoveredHitIdx = -1;
    private int _keyboardHitIdx = -1;
    private readonly List<(RectangleF Rect, Action OnClick)> _hits = new();
    private readonly List<(RectangleF Rect, string Tip)> _tips = new();
    private readonly ToolTip _toolTip = new();
    private string? _lastTip;
    private bool _recentExpanded;
    private bool _statusExpanded;
    private bool _taskExpanded;
    private bool _messageExpanded;
    private string _lastBannerText = "";
    private readonly HashSet<string> _expandedChatIds = new(StringComparer.Ordinal);
    private bool _composerOpen;
    private string _composerText = "";
    private bool _composerTargetAll;
    private string _composerFeedback = "";
    private DateTime _composerFeedbackUntil = DateTime.MinValue;
    private float _scrollY;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 900 };
    private readonly UiFrameTimer _motionTimer;
    private readonly UiAnimationClock _motion = new();

    private static readonly Color BgSurface = Theme.Bg1;
    private static readonly Color TextPri = Theme.TextPrimary;
    private static readonly Color TextSec = Theme.TextSecondary;
    private float ScaleFactor => _mode == "mini" ? 1f : Math.Max(1f, DeviceDpi / 96f);
    private float PanelWidth => _recentExpanded ? 580f : 480f;
    private float LogicalHeight => ClientSize.Height / ScaleFactor;
    private bool TaskBlockVisible => _ctx.ShowTaskStatus && !string.IsNullOrWhiteSpace(_ctx.TaskTitle);
    private IReadOnlyList<McpToolEvent> RecentCalls => _ctx.RecentEvents ?? Array.Empty<McpToolEvent>();

    public TrayPopupForm(TrayContext ctx, string mode)
    {
        _motionTimer = new UiFrameTimer(this);
        _ctx = ctx;
        _mode = mode == "mini" ? "mini" : "panel";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = BgSurface;
        DoubleBuffered = true;
        KeyPreview = true;
        AccessibleName = Theme.ProductName;
        _toolTip.InitialDelay = 220;
        _toolTip.ReshowDelay = 80;
        _toolTip.AutoPopDelay = 12000;
        _toolTip.ShowAlways = true;
        Size = _mode == "mini" ? new Size(340, 62) : new Size((int)(PanelWidth * ScaleFactor), ComputePanelHeight());
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Clamp(Cursor.Position.X - Width / 2, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - Width - 12)), wa.Bottom - Height - 10);
        Deactivate += (_, _) => { try { Close(); } catch { } };
        _refreshTimer.Tick += (_, _) => { if (_mode == "panel") ReflowPanel(); Invalidate(); };
        _refreshTimer.Start();
        if (_mode == "panel") {
            _motionTimer.Tick += (_, _) => {
                bool moving = Visible && ((TaskBlockVisible && _ctx.TaskStatus == "running") || _ctx.LastEvent?.Status is "running" or "pending");
                _motion.SetActive(moving);
                if (moving) Invalidate();
            };
            _motionTimer.Start();
        }
        FormClosed += (_, _) => { _refreshTimer.Stop(); _motionTimer.Stop(); _motionTimer.Dispose(); _motion.Dispose(); _toolTip.SetToolTip(this, null); };
    }

    private PointF BodyMousePos => new(_mousePos.X, _mousePos.Y + _scrollY);
    private float MessageHeight
    {
        get
        {
            var lines = MessageLines(out bool expandable);
            return 38f + lines.Count * 18f + (expandable ? 22f : 0f) + (_ctx.UserMessageLink != null ? 36f : 0f);
        }
    }
    private float AgentTabsHeight => _ctx.ActiveAgentTasks.Count > 1 ? 36f : 0f;
    private float TaskCardBaseHeight => (TaskBlockVisible ? 94f + AgentTabsHeight + (_taskExpanded ? TaskDetailsHeight : 0f) : 176f);
    private float ChatFeedHeight => MeasureChatFeedHeight();
    private float StandbyBarHeight => _ctx.StandbyRemainingSec > 0 ? 38f : 0f;
    private float ComposerCardHeight => (_composerOpen ? 154f : 58f) + StandbyBarHeight + ChatFeedHeight;

    /// <summary>Preview/test helpers to render expanded chat, message banner, task card, and full bubble states directly.</summary>
    internal void SetComposerForPreview(bool open, string draft = "")
    {
        _composerOpen = open;
        _composerText = draft ?? "";
    }

    internal void SetMessageExpandedForPreview(bool expanded)
    {
        _lastBannerText = _ctx.UserMessageText ?? "";
        _messageExpanded = expanded;
    }

    internal void SetTaskExpandedForPreview(bool expanded) => _taskExpanded = expanded;

    internal void SetChatBubbleExpandedForPreview(string id, bool expanded = true)
    {
        if (expanded) _expandedChatIds.Add(id); else _expandedChatIds.Remove(id);
    }

    internal IReadOnlyList<(RectangleF Rect, string Tip)> CurrentTipsForTest => _tips;
    private float TaskDetailsHeight
    {
        get
        {
            float h = 14f;
            if (!string.IsNullOrWhiteSpace(_ctx.TaskCurrent)) h += 74f;
            if (_ctx.TaskDone?.Count > 0) h += 24f + Math.Min(_ctx.TaskDone.Count, 4) * 22f + 6f;
            if (_ctx.TaskNext?.Count > 0) h += 24f + Math.Min(_ctx.TaskNext.Count, 4) * 22f + 6f;
            if (_ctx.TaskDone?.Count > 4 || _ctx.TaskNext?.Count > 4) h += 22f;
            return Math.Max(46f, h + 6f);
        }
    }
    private float BodyHeight()
    {
        float h = 70f + (_statusExpanded ? 52f : 0f);
        if (!string.IsNullOrEmpty(_ctx.UserMessageText)) h += MessageHeight + 12f;
        if (_ctx.ConfinementEnabled) h += 42f;
        if (_recentExpanded) return h + 12f + 38f + 12f + Math.Max(100f, Math.Min(RecentCalls.Count, 9) * 70f);
        h += 12f + TaskCardBaseHeight;
        h += 12f + ComposerCardHeight;
        if (TaskBlockVisible) h += 12f + 120f;
        return h;
    }

    private int ComputePanelHeight()
    {
        if (_mode == "mini") return 62;
        int max = Math.Max(180, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 24);
        return Math.Min((int)Math.Ceiling((132f + BodyHeight()) * ScaleFactor), max);
    }

    internal int UnclampedPanelHeightForTest => (int)Math.Ceiling((132f + BodyHeight()) * ScaleFactor);

    private void ReflowPanel()
    {
        if (_mode == "mini") return;
        var wa = Screen.FromControl(this).WorkingArea;
        int w = (int)Math.Round(PanelWidth * ScaleFactor), h = ComputePanelHeight();
        if (Width == w && Height == h) return;
        int right = Right, bottom = Bottom;
        Size = new Size(w, h);
        Location = new Point(Math.Clamp(right - w, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - w - 8)), Math.Max(wa.Top + 8, Math.Min(bottom - h, wa.Bottom - h - 8)));
    }

    private void ChangeView(Action change)
    {
        change(); _scrollY = 0; _keyboardHitIdx = -1; _hoveredHitIdx = -1;
        ReflowPanel(); Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int pref = 2; DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int)); } catch { }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_mode != "panel" || Width <= 0 || Height <= 0) return;
        // DWM's corner preference is only a hint and is ignored on some Windows
        // configurations. Give the actual HWND an explicit rounded region too.
        using var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), Theme.RadiusWindow * ScaleFactor);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_mode == "panel") { ReflowPanel(); Invalidate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _refreshTimer.Stop(); _refreshTimer.Dispose(); _toolTip.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mousePos = new PointF(e.X / ScaleFactor, e.Y / ScaleFactor);
        int idx = _hits.FindIndex(h => h.Rect.Contains(_mousePos));
        Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
        if (idx != _hoveredHitIdx) { _hoveredHitIdx = idx; Invalidate(); }
        string? tip = _tips.FirstOrDefault(t => t.Rect.Contains(_mousePos)).Tip;
        if (tip != _lastTip) { _lastTip = tip; _toolTip.SetToolTip(this, tip); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e); _mousePos = new(-1, -1); _hoveredHitIdx = -1; _lastTip = null;
        _toolTip.SetToolTip(this, null); Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var p = new PointF(e.X / ScaleFactor, e.Y / ScaleFactor);
        var hit = _hits.FirstOrDefault(h => h.Rect.Contains(p));
        hit.OnClick?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_mode == "mini") return;
        float max = Math.Max(0, BodyHeight() - Math.Max(1, LogicalHeight - 132f));
        _scrollY = Math.Clamp(_scrollY - e.Delta / 120f * 44f, 0, max);
        _keyboardHitIdx = -1; Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (_composerOpen && _composerText.Length > 0) { _composerText = ""; Invalidate(); return true; }
            if (_composerOpen) { ChangeView(() => _composerOpen = false); return true; }
            Close(); return true;
        }
        if (_mode == "panel" && _composerOpen)
        {
            if (keyData == Keys.Back)
            {
                if (_composerText.Length > 0) { _composerText = _composerText[..^1]; Invalidate(); }
                return true;
            }
            if (keyData == (Keys.Control | Keys.V))
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string clip = Clipboard.GetText().Replace("\r", " ").Replace("\n", " ");
                        _composerText = (_composerText + clip);
                        if (_composerText.Length > 500) _composerText = _composerText[..500];
                        Invalidate();
                    }
                }
                catch { }
                return true;
            }
            if (keyData == Keys.Enter)
            {
                SubmitComposerMessage(stopCommand: false);
                return true;
            }
        }
        if (_mode == "panel" && (keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab)))
        {
            if (_hits.Count > 0)
            {
                _keyboardHitIdx = _keyboardHitIdx < 0 ? (keyData == Keys.Tab ? 0 : _hits.Count - 1)
                    : (_keyboardHitIdx + (keyData == Keys.Tab ? 1 : _hits.Count - 1)) % _hits.Count;
                Invalidate();
            }
            return true;
        }
        if (_mode == "panel" && (keyData == Keys.Enter || keyData == Keys.Space) && _keyboardHitIdx >= 0 && _keyboardHitIdx < _hits.Count)
        { _hits[_keyboardHitIdx].OnClick(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_mode != "panel") return;
        if (!char.IsControl(e.KeyChar))
        {
            if (!_composerOpen) ChangeView(() => _composerOpen = true);
            if (_composerText.Length < 500)
            {
                _composerText += e.KeyChar;
                Invalidate();
            }
            e.Handled = true;
        }
    }

    private void SubmitComposerMessage(bool stopCommand)
    {
        string text = _composerText.Trim();
        if (string.IsNullOrEmpty(text)) return;
        string target = _composerTargetAll || string.IsNullOrWhiteSpace(_ctx.TaskAgent) ? "*" : _ctx.TaskAgent;
        _composerText = "";
        _composerFeedback = stopCommand
            ? I18n.T("Stopping command & sending…", "Остановка команды и отправка…")
            : I18n.T("Queued for instant delivery…", "Отправлено в мгновенный шлюз…");
        _composerFeedbackUntil = DateTime.Now.AddSeconds(4);
        _ = Task.Run(async () =>
        {
            await _ctx.SendOwnerMessageAsync(target, text, stopCommand);
            try { BeginInvoke(new Action(Invalidate)); } catch { }
        });
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _hits.Clear(); _tips.Clear();
        var g = e.Graphics;
        Theme.PrepareGraphics(g);
        if (_mode == "mini")
        {
            using (var bg = new SolidBrush(BgSurface)) g.FillRectangle(bg, 0, 0, Width, Height);
            DrawMiniMode(g);
            using var border = new Pen(Theme.BorderStrong, 1f);
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            return;
        }
        // Paint the design once at 96 DPI, then scale the whole surface. This keeps
        // point fonts, geometry and hit targets aligned on 125/150/200% monitors.
        using var canvas = new Bitmap((int)PanelWidth, (int)Math.Ceiling(LogicalHeight));
        canvas.SetResolution(96, 96);
        using (var logical = Graphics.FromImage(canvas))
        {
            Theme.PrepareGraphics(logical, layered: true);
            logical.Clear(Color.Transparent);
            Theme.DrawWindowBackground(logical, new RectangleF(0, 0, PanelWidth, LogicalHeight), Theme.RadiusWindow);
            var savedClip = logical.Save();
            using (var roundedClip = Theme.RoundRect(new RectangleF(0, 0, PanelWidth, LogicalHeight), Theme.RadiusWindow))
                logical.SetClip(roundedClip);
            DrawPanelMode(logical);
            logical.Restore(savedClip);
            Theme.StrokeRound(logical, new RectangleF(.5f, .5f, PanelWidth - 1, LogicalHeight - 1), Theme.RadiusWindow, Theme.BorderStrong);
            if (_keyboardHitIdx >= 0 && _keyboardHitIdx < _hits.Count)
                Theme.StrokeRound(logical, RectangleF.Inflate(_hits[_keyboardHitIdx].Rect, 2, 2), 6, Theme.AccentHover, 1.5f);
        }
        g.DrawImage(canvas, new Rectangle(0, 0, Width, Height), new Rectangle(0, 0, canvas.Width, canvas.Height), GraphicsUnit.Pixel);
    }

    private Color StateColor() => _ctx.CurrentState == "on" ? Theme.Success : _ctx.CurrentState == "starting" ? Theme.Warning : Theme.TextTertiary;

    private void DrawMiniMode(Graphics g)
    {
        bool isOn = _ctx.CurrentState == "on" || _ctx.CurrentState == "starting";
        Color dotCol = StateColor();

        Theme.DrawStatusDot(g, new PointF(22f, 31f), 5f, dotCol, halo: isOn);

        float textW = Width - 132f - 38f - 8f;
        Theme.DrawText(g, Theme.ProductName, Theme.BodyStrong, TextPri, new RectangleF(38f, 10f, textW, 21f));
        string sub = _ctx.CurrentState == "on" ? I18n.T("Online", "Онлайн") : (_ctx.CurrentState == "starting" ? I18n.T("Starting…", "Запуск…") : I18n.T("Off", "Выключено"));
        if (_ctx.MissedCount > 0)
            sub += I18n.Format(" · missed: {0}", " · пропущено: {0}", _ctx.MissedCount);
        else if (_ctx.ConfinementEnabled)
            sub += I18n.T(" · 📁 1 folder", " · 📁 1 папка");
        else if (_ctx.SessionDeadline.HasValue)
            sub += I18n.Format(" · until {0:HH:mm}", " · до {0:HH:mm}", _ctx.SessionDeadline.Value);
        Theme.DrawText(g, sub, Theme.Small, TextSec, new RectangleF(38f, 31f, textW, 19f));

        // Toggle switch
        DrawToggle(g, Width - 132f, 19f, isOn, () =>
        {
            if (isOn) _ctx.StopServerFromUi("disabled from mini panel");
            else _ctx.StartServerFromUi(null);
            Invalidate();
        });

        // Settings button
        var gearRect = new RectangleF(Width - 76f, 15f, 32f, 32f);
        DrawIconCircleButton(g, gearRect, IconKind.Settings, false, () =>
        {
            Close();
            _ctx.OpenSettingsWindow("general");
        });

        // Exit button
        var exitRect = new RectangleF(Width - 38f, 15f, 28f, 32f);
        DrawIconCircleButton(g, exitRect, IconKind.Close, true, () =>
        {
            Close();
            _ctx.ExitFromUi();
        });
    }

    private void DrawPanelMode(Graphics g)
    {
        DrawHeader(g);
        float available = Math.Max(1, LogicalHeight - 132f);
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, BodyHeight() - available));
        var bodyClip = new RectangleF(10, 52, PanelWidth - 20, available);
        var saved = g.Save();
        g.SetClip(bodyClip);
        g.TranslateTransform(0, -_scrollY);
        float y = 52f;
        int bodyHits = _hits.Count, bodyTips = _tips.Count;
        if (!string.IsNullOrEmpty(_ctx.UserMessageText))
        {
            DrawMessageBanner(g, new RectangleF(14, y, PanelWidth - 28, MessageHeight));
            y += MessageHeight + 12;
        }
        DrawStatusCard(g, new RectangleF(14, y, PanelWidth - 28, 70 + (_statusExpanded ? 52 : 0)));
        y += 82 + (_statusExpanded ? 52 : 0);
        if (_ctx.ConfinementEnabled)
        {
            var r = new RectangleF(14, y, PanelWidth - 28, 30);
            Theme.FillRound(g, r, 9, Theme.SuccessSubtle);
            Theme.StrokeRound(g, r, 9, Theme.WithAlpha(Theme.Success, 56));
            Theme.DrawIcon(g, IconKind.Folder, new RectangleF(26, y + 7, 16, 16), Theme.Success);
            DrawFitted(g, I18n.Format("Single folder: {0} · terminal off", "Одна папка: {0} · терминал откл.", EventHumanizer.GetLeafName(_ctx.ConfinementRoot)), PanelTypography.Get(12.5f), Theme.SuccessText, new RectangleF(50, y, PanelWidth - 98, 30));
            Theme.DrawIcon(g, IconKind.ChevronRight, new RectangleF(PanelWidth - 42, y + 7, 16, 16), Theme.SuccessText);
            _hits.Add((r, () => { Close(); _ctx.OpenSettingsWindow("access"); }));
            y += 42;
        }
        if (_recentExpanded) DrawHistory(g, y);
        else
        {
            DrawTaskCard(g, new RectangleF(14, y, PanelWidth - 28, TaskCardBaseHeight));
            y += TaskCardBaseHeight + 12;
            DrawComposerCard(g, new RectangleF(14, y, PanelWidth - 28, ComposerCardHeight));
            y += ComposerCardHeight + 12;
            if (TaskBlockVisible) DrawLatestCard(g, new RectangleF(14, y, PanelWidth - 28, 120));
        }
        g.Restore(saved);
        // Hit targets use final viewport coordinates, never off-screen card coordinates.
        for (int i = _hits.Count - 1; i >= bodyHits; i--)
        {
            var h = _hits[i]; var r = h.Rect; r.Offset(0, -_scrollY);
            r = RectangleF.Intersect(r, bodyClip);
            if (r.Width <= 0 || r.Height <= 0) _hits.RemoveAt(i); else _hits[i] = (r, h.OnClick);
        }
        for (int i = _tips.Count - 1; i >= bodyTips; i--)
        {
            var t = _tips[i]; var r = t.Rect; r.Offset(0, -_scrollY);
            r = RectangleF.Intersect(r, bodyClip);
            if (r.Width <= 0 || r.Height <= 0) _tips.RemoveAt(i); else _tips[i] = (r, t.Tip);
        }
        if (BodyHeight() > available)
        {
            float track = available - 8, thumb = Math.Max(24, track * available / BodyHeight());
            Theme.FillRound(g, new RectangleF(PanelWidth - 7, 56 + (track - thumb) * _scrollY / (BodyHeight() - available), 3, thumb), 1.5f, Theme.BorderStrong);
        }
        DrawFooter(g);
    }

    private void DrawHeader(Graphics g)
    {
        // The ZeroIDE logo (assets/zeroide-logo.svg) in place of the earlier hexagon mark.
        Theme.DrawLogo(g, new RectangleF(17, 12, 28, 28));
        Theme.DrawText(g, Theme.ProductName, PanelTypography.Get(14.5f, true), TextPri, new RectangleF(53, 14, PanelWidth - 182, 24));
        bool isOn = _ctx.CurrentState is "on" or "starting";
        var toggle = new RectangleF(PanelWidth - 106, 15, 44, 24);
        Theme.DrawToggle(g, new RectangleF(toggle.X + 3, toggle.Y + 1, 38, 22), isOn, toggle.Contains(_mousePos), false, Theme.Accent);
        _tips.Add((toggle, isOn ? I18n.T("Turn MCP off", "Выключить MCP") : I18n.T("Turn MCP on", "Включить MCP")));
        _hits.Add((toggle, () => { if (isOn) _ctx.StopServerFromUi("disabled from panel"); else _ctx.StartServerFromUi(null); Invalidate(); }));
        var close = new RectangleF(PanelWidth - 48, 10, 34, 32);
        if (close.Contains(_mousePos)) Theme.FillRound(g, close, 8, Theme.SurfaceHover);
        Theme.DrawIcon(g, IconKind.Close, new RectangleF(close.X + 9, close.Y + 8, 16, 16), TextSec);
        _tips.Add((close, I18n.T("Close panel · Esc", "Закрыть панель · Esc")));
        _hits.Add((close, Close));
    }

    private void DrawStatusCard(Graphics g, RectangleF r)
    {
        Color c = StateColor();
        Surface(g, r);
        Wash(g, r, c, 26);
        Theme.DrawStatusDot(g, new PointF(r.X + 26, r.Y + 35), 6, c, halo: _ctx.CurrentState != "off");
        string title = _ctx.CurrentState == "on" ? I18n.T("MCP online", "MCP онлайн") : _ctx.CurrentState == "starting" ? I18n.T("Starting…", "Запуск…") : I18n.T("Server off", "Сервер выключен");
        float titleW = r.Width - 100;
        if (_ctx.MissedCount > 0)
        {
            string text = I18n.Format("{0} Missed", "{0} Пропущено", _ctx.MissedCount);
            float w = Theme.MeasureText(g, text, PanelTypography.Get(12.5f, true)).Width + 38;
            var badge = new RectangleF(r.Right - 16 - w, r.Y + 11, w, 24);
            Theme.FillRound(g, badge, 12, Theme.WarningSubtle);
            Theme.StrokeRound(g, badge, 12, Theme.WithAlpha(Theme.Warning, 72));
            Theme.DrawIcon(g, IconKind.Warning, new RectangleF(badge.X + 10, badge.Y + 5, 14, 14), Theme.Warning);
            Theme.DrawText(g, text, PanelTypography.Get(12.5f, true), Theme.Warning, new RectangleF(badge.X + 30, badge.Y, w - 36, 24));
            _hits.Add((badge, () => { Close(); _ctx.OpenMissedRequestsWindow(); }));
            titleW -= w;
        }
        DrawFitted(g, title, PanelTypography.Get(14.5f, true), TextPri, new RectangleF(r.X + 50, r.Y + 12, titleW, 23));
        string sub = _ctx.SessionDeadline.HasValue && (_ctx.CurrentState is "on" or "starting")
            ? I18n.Format("Session until {0:HH:mm} · {1} min left", "Сессия до {0:HH:mm} · осталось {1} мин", _ctx.SessionDeadline.Value, Math.Max(0, (int)Math.Ceiling((_ctx.SessionDeadline.Value - DateTime.Now).TotalMinutes)))
            : _ctx.CurrentState == "on" ? I18n.Format("Port 127.0.0.1:{0} · Tunnel active", "Порт 127.0.0.1:{0} · Туннель активен", _ctx.Config.Port)
            : _ctx.CurrentState == "starting" ? I18n.T("Connecting to the server…", "Подключение к серверу…") : I18n.T("Turn on MCP using the toggle above", "Включите MCP тумблером вверху");
        DrawFitted(g, sub, PanelTypography.Get(12.5f), TextSec, new RectangleF(r.X + 50, r.Y + 35, r.Width - 88, 21));
        Theme.DrawIcon(g, _statusExpanded ? IconKind.ChevronUp : IconKind.ChevronDown, new RectangleF(r.Right - 29, r.Y + 38, 14, 14), Theme.TextTertiary);
        _tips.Add((new RectangleF(r.X, r.Y, r.Width, 70), I18n.T("Session timers and active grants", "Таймер сессии и активные гранты")));
        if (_statusExpanded)
        {
            using var p = new Pen(Theme.Divider);
            g.DrawLine(p, r.X + 16, r.Y + 68, r.Right - 16, r.Y + 68);
            float w = (r.Width - 48) / 3;
            SessionButton(g, new RectangleF(r.X + 16, r.Y + 80, w, 30), I18n.T("For 30 min", "На 30 мин"), () => _ctx.StartServerFromUi(30));
            SessionButton(g, new RectangleF(r.X + 24 + w, r.Y + 80, w, 30), I18n.T("For 1 hour", "На 1 час"), () => _ctx.StartServerFromUi(60));
            SessionButton(g, new RectangleF(r.X + 32 + 2 * w, r.Y + 80, w, 30), I18n.Format("Grants: {0}", "Гранты: {0}", _ctx.ActiveGrantsCount), () => { Close(); _ctx.OpenSettingsWindow("approvals"); });
        }
        _hits.Add((new RectangleF(r.X, r.Y, r.Width, 70), () => ChangeView(() => _statusExpanded = !_statusExpanded)));
    }

    private void DrawTaskCard(Graphics g, RectangleF r)
    {
        Surface(g, r);
        if (!TaskBlockVisible)
        {
            Wash(g, r, Theme.Accent, 20);
            Theme.DrawIconTile(g, new RectangleF(r.X + (r.Width - 52) / 2, r.Y + 22, 52, 52), IconKind.Clock, Theme.Accent, 26, 16);
            Theme.DrawText(g, I18n.T("No current task", "Нет текущей задачи"), PanelTypography.Get(14.5f, true), TextPri, new RectangleF(r.X + 16, r.Y + 86, r.Width - 32, 23), StringAlignment.Center);
            Theme.DrawText(g, I18n.T("Waiting for requests from the AI agent", "Ожидаем запросы от AI-агента"), PanelTypography.Get(13.5f), TextSec, new RectangleF(r.X + 16, r.Y + 110, r.Width - 32, 22), StringAlignment.Center);
            var link = new RectangleF(r.X + (r.Width - 180) / 2, r.Y + 140, 180, 22);
            Theme.DrawText(g, I18n.T("Call history", "История вызовов"), PanelTypography.Get(12.5f, true), Theme.AccentText, link, StringAlignment.Center);
            _hits.Add((link, () => ChangeView(() => _recentExpanded = true)));
            return;
        }
        float topOffset = 0f;
        var tasks = _ctx.ActiveAgentTasks;
        if (tasks.Count > 1)
        {
            float tx = r.X + 14f;
            float ty = r.Y + 10f;
            float availTabW = (r.Width - 28f - (tasks.Count - 1) * 6f) / tasks.Count;
            availTabW = Math.Clamp(availTabW, 74f, 180f);
            foreach (var t in tasks)
            {
                bool sel = t.AgentId == _ctx.SelectedAgentId;
                var pill = new RectangleF(tx, ty, availTabW, 24f);
                Theme.FillRound(g, pill, 8f, sel ? Theme.AccentSubtle : Theme.SurfaceRaised);
                Theme.StrokeRound(g, pill, 8f, sel ? Theme.Accent : Theme.Border);
                Theme.DrawStatusDot(g, new PointF(pill.X + 12f, pill.Y + 12f), 3.5f, sel ? Theme.Success : Theme.TextTertiary, halo: sel);
                DrawFitted(g, t.AgentName, PanelTypography.Get(11.5f, sel), sel ? TextPri : TextSec, new RectangleF(pill.X + 22f, pill.Y + 2f, pill.Width - 28f, 20f));
                string capturedId = t.AgentId;
                _hits.Add((pill, () => { _ctx.SelectedAgentId = capturedId; Invalidate(); }));
                _tips.Add((pill, $"{t.AgentName}: {t.Title}"));
                tx += availTabW + 6f;
            }
            topOffset = 36f;
        }
        float headerY = r.Y + topOffset;
        PointF mp = BodyMousePos;
        bool isTaskDone = _ctx.TaskStatus is "done" or "waiting";
        Color taskAccent = isTaskDone ? Theme.Success : Theme.Accent;
        Wash(g, r, taskAccent, isTaskDone ? 22 : 18);

        int doneCount = _ctx.TaskDone?.Count ?? 0;
        int nextCount = _ctx.TaskNext?.Count ?? 0;
        int totalSteps = doneCount + nextCount + (!isTaskDone && !string.IsNullOrWhiteSpace(_ctx.TaskCurrent) ? 1 : 0);

        Theme.DrawIconTile(g, new RectangleF(r.X + 14, headerY + 14, 36, 36), isTaskDone ? IconKind.Check : IconKind.Play, taskAccent, 16);

        // Top meta row: Status pill + Agent name + Elapsed time + Expand/Close buttons
        string statusBadge = isTaskDone
            ? I18n.T("Done", "Готово")
            : (totalSteps > 1 ? I18n.Format("Running · {0}/{1}", "В работе · {0}/{1}", doneCount, totalSteps) : I18n.T("Running", "В работе"));
        var badgeFont = PanelTypography.Get(10.5f, true);
        float badgeW = Theme.MeasureText(g, statusBadge, badgeFont).Width + 24f;
        var badgeRect = new RectangleF(r.X + 60, headerY + 12, badgeW, 20f);
        Theme.FillRound(g, badgeRect, 10f, isTaskDone ? Theme.SuccessSubtle : Theme.AccentSubtle);
        Theme.StrokeRound(g, badgeRect, 10f, Theme.WithAlpha(taskAccent, 95));
        Theme.DrawStatusDot(g, new PointF(badgeRect.X + 10f, badgeRect.Y + 10f), 3f, taskAccent, halo: !isTaskDone);
        Theme.DrawText(g, statusBadge, badgeFont, isTaskDone ? Theme.SuccessText : Theme.AccentText, new RectangleF(badgeRect.X + 17f, badgeRect.Y + 2f, badgeRect.Width - 20f, 16f));

        string elapsed = _ctx.TaskStartedAt.HasValue
            ? FormatElapsed(_ctx.TaskStartedAt.Value, isTaskDone ? _ctx.TaskCompletedAt : null)
            : "";
        string agentLabel = string.IsNullOrWhiteSpace(_ctx.TaskAgent) ? "" : _ctx.TaskAgent;
        string metaRight = string.Join(" · ", new[] { agentLabel, elapsed }.Where(s => !string.IsNullOrWhiteSpace(s)));
        float metaX = badgeRect.Right + 8f;
        float metaMaxW = Math.Max(40f, r.Right - 66f - metaX);
        if (!string.IsNullOrEmpty(metaRight))
            DrawFitted(g, metaRight, PanelTypography.Get(11f), Theme.TextTertiary, new RectangleF(metaX, headerY + 14, metaMaxW, 17f));

        // Vector close & expand buttons (registered before card header hit)
        var closeBtn = new RectangleF(r.Right - 36f, headerY + 10f, 24f, 24f);
        bool closeHov = closeBtn.Contains(mp);
        if (closeHov) Theme.FillRound(g, closeBtn, 6f, Theme.SurfaceHover);
        Theme.DrawIcon(g, IconKind.Close, new RectangleF(closeBtn.X + 5f, closeBtn.Y + 5f, 14f, 14f), closeHov ? TextPri : Theme.TextTertiary, 1.7f);
        _hits.Add((closeBtn, () => { _ctx.ClearTaskStatus(); ReflowPanel(); Invalidate(); }));
        _tips.Add((closeBtn, I18n.T("Close this agent's task", "Закрыть задачу этого агента")));

        var arrowBtn = new RectangleF(r.Right - 62f, headerY + 10f, 24f, 24f);
        bool arrowHov = arrowBtn.Contains(mp);
        if (arrowHov) Theme.FillRound(g, arrowBtn, 6f, Theme.SurfaceHover);
        Theme.DrawIcon(g, _taskExpanded ? IconKind.ChevronUp : IconKind.ChevronDown, new RectangleF(arrowBtn.X + 5f, arrowBtn.Y + 5f, 14f, 14f), arrowHov ? TextPri : Theme.TextTertiary, 1.7f);

        // Task title (primary line) + live current step (secondary line)
        DrawFitted(g, _ctx.TaskTitle, PanelTypography.Get(13.5f, true), TextPri, new RectangleF(r.X + 60, headerY + 35, r.Width - 76, 21f));
        string subLine = !string.IsNullOrWhiteSpace(_ctx.TaskCurrent) && !string.Equals(_ctx.TaskCurrent, _ctx.TaskTitle, StringComparison.Ordinal)
            ? _ctx.TaskCurrent
            : (isTaskDone
                ? I18n.T("Completed — click to inspect steps", "Выполнено — нажмите, чтобы посмотреть этапы")
                : I18n.T("Click to view progress steps", "Нажмите, чтобы посмотреть этапы задачи"));
        DrawFitted(g, subLine, PanelTypography.Get(11.5f), TextSec, new RectangleF(r.X + 60, headerY + 57, r.Width - 76, 18f));

        // Clean progress track separated from text
        var barRect = new RectangleF(r.X + 14f, headerY + 82f, r.Width - 28f, 3.5f);
        Theme.FillRound(g, barRect, 1.75f, Theme.Trough);
        if (isTaskDone)
        {
            Theme.FillRound(g, barRect, 1.75f, Theme.Success);
        }
        else
        {
            if (totalSteps > 0 && doneCount > 0)
            {
                float pct = Math.Clamp((float)doneCount / totalSteps, 0.06f, 0.96f);
                Theme.FillRound(g, new RectangleF(barRect.X, barRect.Y, barRect.Width * pct, barRect.Height), 1.75f, Theme.Accent);
            }
            UiAnimationClock.DrawMarquee(g, new RectangleF(barRect.X, barRect.Y, barRect.Width, 3f), Theme.AccentHover, _motion.Seconds);
        }

        _hits.Add((new RectangleF(r.X, headerY, r.Width, 94f), () => ChangeView(() => _taskExpanded = !_taskExpanded)));
        if (!_taskExpanded) return;

        float y = headerY + 96f;
        using (var div = new Pen(Theme.Divider)) g.DrawLine(div, r.X + 14f, y, r.Right - 14f, y);
        y += 10f;

        if (!string.IsNullOrWhiteSpace(_ctx.TaskCurrent))
        {
            var curBox = new RectangleF(r.X + 14f, y, r.Width - 28f, 66f);
            Theme.FillRound(g, curBox, 9f, Theme.SurfaceRaised);
            Theme.StrokeRound(g, curBox, 9f, Theme.Border);
            Theme.DrawStatusDot(g, new PointF(curBox.X + 12f, curBox.Y + 13f), 3f, taskAccent, halo: !isTaskDone);
            Theme.DrawText(g, isTaskDone ? I18n.T("RESULT", "ИТОГ") : I18n.T("NOW WORKING ON", "СЕЙЧАС ДЕЛАЕТ"), PanelTypography.Get(10f, true), Theme.TextTertiary, new RectangleF(curBox.X + 22f, curBox.Y + 5f, curBox.Width - 32f, 15f));
            var curLines = WrapLines(g, _ctx.TaskCurrent, PanelTypography.Get(12f), curBox.Width - 24f, 2);
            float cy = curBox.Y + 23f;
            foreach (string cl in curLines)
            {
                Theme.DrawText(g, cl, PanelTypography.Get(12f), TextPri, new RectangleF(curBox.X + 12f, cy, curBox.Width - 24f, 18f));
                cy += 18f;
            }
            _tips.Add((curBox, WrapTooltipText(_ctx.TaskCurrent)));
            y += 74f;
        }

        void Rows(IReadOnlyList<string>? rows, bool done)
        {
            if (rows == null || rows.Count == 0) return;
            _tips.Add((new RectangleF(r.X + 14, y, r.Width - 28, 22), string.Join("\n", rows)));
            string hdr = done
                ? I18n.Format("DONE ({0})", "ГОТОВО ({0})", rows.Count)
                : I18n.Format("NEXT ({0})", "ДАЛЕЕ ({0})", rows.Count);
            Theme.DrawText(g, hdr, PanelTypography.Get(10.5f, true), done ? Theme.SuccessText : Theme.TextTertiary, new RectangleF(r.X + 16, y, r.Width - 32, 20));
            y += 22f;
            foreach (string t in rows.Take(4))
            {
                Theme.DrawIcon(g, done ? IconKind.Check : IconKind.ChevronRight, new RectangleF(r.X + 16, y + 3, 14, 14), done ? Theme.Success : Theme.TextTertiary, 1.7f);
                DrawFitted(g, t, PanelTypography.Get(12f), done ? TextSec : TextPri, new RectangleF(r.X + 36, y, r.Width - 52, 20));
                y += 22f;
            }
            y += 6f;
        }
        Rows(_ctx.TaskDone, true);
        Rows(_ctx.TaskNext, false);
        if (_ctx.TaskDone?.Count > 4 || _ctx.TaskNext?.Count > 4)
            DrawFitted(g, I18n.Format("+{0} more steps", "Ещё шагов: {0}", Math.Max(0, (_ctx.TaskDone?.Count ?? 0) - 4) + Math.Max(0, (_ctx.TaskNext?.Count ?? 0) - 4)), PanelTypography.Get(11.5f), Theme.TextTertiary, new RectangleF(r.X + 16, y, r.Width - 32, 20));
    }

    private static string HoldStandbyTooltip() => I18n.T(
        "Keep-alive standby: keeps the agent's MCP connection open for 5 min\nafter finishing its work so it does not disconnect and can receive\nfollow-up instructions from the tray.\n\nClick to toggle.",
        "Удержание связи: не разрывает соединение агента с MCP ещё 5 минут\nпосле завершения его работы, чтобы можно было отправить новые\nуказания прямо из трея.\n\nНажмите, чтобы включить или выключить.");

    private static List<string> WrapLines(Graphics g, string text, Font font, float maxWidth, int maxLines, bool showTail = false)
        => WrapLinesWithTruncation(g, text, font, maxWidth, maxLines, out _, showTail);

    private static List<string> WrapLinesWithTruncation(Graphics g, string text, Font font, float maxWidth, int maxLines, out bool truncated, bool showTail = false, bool preserveNewlines = false)
    {
        truncated = false;
        string normalized = (text ?? "").Replace("\r", "");
        if (!preserveNewlines) normalized = normalized.Replace("\n", " ");
        if (string.IsNullOrWhiteSpace(normalized)) return new List<string> { "" };

        var all = new List<string>();
        string[] paragraphs = preserveNewlines ? normalized.Split('\n') : new[] { normalized };
        foreach (string para in paragraphs)
        {
            string clean = para.Trim();
            if (clean.Length == 0)
            {
                if (preserveNewlines && all.Count > 0) all.Add("");
                continue;
            }
            string cur = "";
            foreach (string rawWord in clean.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = cur.Length == 0 ? rawWord : cur + " " + rawWord;
                if (Theme.MeasureText(g, candidate, font).Width <= maxWidth)
                {
                    cur = candidate;
                    continue;
                }
                if (cur.Length > 0) { all.Add(cur); cur = ""; }
                if (Theme.MeasureText(g, rawWord, font).Width <= maxWidth)
                {
                    cur = rawWord;
                    continue;
                }
                // Break long unbroken token (e.g. file path or URL)
                foreach (char ch in rawWord)
                {
                    string next = cur + ch;
                    if (cur.Length > 0 && Theme.MeasureText(g, next, font).Width > maxWidth)
                    {
                        all.Add(cur);
                        cur = ch.ToString();
                    }
                    else cur = next;
                }
            }
            if (cur.Length > 0) all.Add(cur);
        }
        if (all.Count == 0) all.Add("");
        if (all.Count <= maxLines) return all;
        truncated = true;
        if (showTail)
        {
            var tail = all.Skip(all.Count - maxLines).ToList();
            if (tail.Count > 0 && !tail[0].StartsWith("…")) tail[0] = "…" + tail[0];
            return tail;
        }
        var head = all.Take(maxLines).ToList();
        string last = head[^1];
        while (last.Length > 0 && Theme.MeasureText(g, last + "…", font).Width > maxWidth) last = last[..^1];
        head[^1] = last.TrimEnd() + "…";
        return head;
    }

    private IReadOnlyList<ChatHistoryItem> VisibleChatItems()
    {
        // When the chat composer is collapsed, hide the dialogue feed completely so the user can collapse chat out of sight.
        if (!_composerOpen || _ctx.ChatHistory.Count == 0) return Array.Empty<ChatHistoryItem>();
        return _ctx.ChatHistory.TakeLast(5).ToList();
    }

    private (List<string> Lines, bool Expandable, bool Expanded) LayoutChatBubble(Graphics g, ChatHistoryItem item, Font textFont, float maxTextW)
    {
        bool expanded = _expandedChatIds.Contains(item.Id);
        var preview = WrapLinesWithTruncation(g, item.Text, textFont, maxTextW, 3, out bool exceedsCollapsed, preserveNewlines: true);
        if (!expanded || !exceedsCollapsed)
            return (preview, exceedsCollapsed, false);
        var full = WrapLinesWithTruncation(g, item.Text, textFont, maxTextW, 16, out _, preserveNewlines: true);
        return (full, true, true);
    }

    private float MeasureChatFeedHeight()
    {
        var items = VisibleChatItems();
        if (items.Count == 0) return 0f;
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        float cardW = PanelWidth - 28f;
        float maxTextW = cardW - 96f;
        var textFont = PanelTypography.Get(12f);
        float total = 10f; // top padding
        for (int i = 0; i < items.Count; i++)
        {
            var (lines, expandable, _) = LayoutChatBubble(g, items[i], textFont, maxTextW);
            bool isOwner = items[i].Role == "owner";
            float expandH = expandable ? 16f : 0f;
            float bubbleH = isOwner
                ? 7f + lines.Count * 17f + 15f + 6f
                : 7f + 15f + lines.Count * 17f + expandH + 7f;
            total += bubbleH;
            if (i < items.Count - 1) total += 7f;
        }
        return total + 10f; // bottom spacing + divider
    }

    private float DrawChatFeed(Graphics g, RectangleF r, float curY)
    {
        var items = VisibleChatItems();
        if (items.Count == 0) return curY;

        float maxBubbleW = r.Width - 74f;
        float maxTextW = maxBubbleW - 22f;
        var textFont = PanelTypography.Get(12f);
        var metaFont = PanelTypography.Get(10.5f, true);
        var timeFont = PanelTypography.Get(10f);
        var linkFont = PanelTypography.Get(10.5f, true);
        PointF mp = BodyMousePos;

        curY += 10f;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool isOwner = item.Role == "owner";
            var (lines, expandable, expanded) = LayoutChatBubble(g, item, textFont, maxTextW);

            float textMaxLineW = 0f;
            foreach (string ln in lines)
                textMaxLineW = Math.Max(textMaxLineW, Theme.MeasureText(g, ln, textFont).Width);

            string toggleText = expanded ? I18n.T("Collapse ▴", "Свернуть ▴") : I18n.T("Show more ▾", "Показать полностью ▾");
            float toggleW = expandable ? Theme.MeasureText(g, toggleText, linkFont).Width + 8f : 0f;

            if (isOwner)
            {
                string statusText = item.Status == "queued"
                    ? I18n.T("Queued", "В очереди")
                    : I18n.T("✓ Delivered", "✓ Доставлено");
                string timeText = RelativeTime(item.Ts);
                string foot = string.IsNullOrEmpty(timeText) ? statusText : statusText + " · " + timeText;
                float footW = Theme.MeasureText(g, foot, timeFont).Width;
                float bottomNeedW = footW + (expandable ? toggleW + 14f : 0f);
                float bubbleW = Math.Clamp(Math.Max(textMaxLineW, bottomNeedW) + 24f, 118f, maxBubbleW);
                float bubbleH = 7f + lines.Count * 17f + 15f + 6f;
                var bubble = new RectangleF(r.Right - 12f - bubbleW, curY, bubbleW, bubbleH);
                bool bHov = expandable && bubble.Contains(mp);

                Theme.FillRound(g, bubble, 11f, Theme.AccentSubtle);
                Theme.StrokeRound(g, bubble, 11f, Theme.WithAlpha(Theme.Accent, bHov ? 175 : 95));

                float ty = bubble.Y + 7f;
                foreach (string ln in lines)
                {
                    Theme.DrawText(g, ln, textFont, TextPri, new RectangleF(bubble.X + 11f, ty, bubble.Width - 22f, 17f));
                    ty += 17f;
                }
                if (expandable)
                    Theme.DrawText(g, toggleText, linkFont, Theme.AccentText, new RectangleF(bubble.X + 11f, ty + 1f, toggleW, 14f));
                Color statusCol = item.Status == "queued" ? Theme.WarningText : Theme.SuccessText;
                Theme.DrawText(g, foot, timeFont, statusCol, new RectangleF(bubble.X + 11f, ty + 1f, bubble.Width - 22f, 14f), StringAlignment.Far);
                if (expandable)
                {
                    string cid = item.Id;
                    _hits.Add((bubble, () => { if (!_expandedChatIds.Add(cid)) _expandedChatIds.Remove(cid); ReflowPanel(); Invalidate(); }));
                }
                _tips.Add((bubble, WrapTooltipText(I18n.T("You: ", "Вы: ") + item.Text)));
                curY += bubbleH;
            }
            else
            {
                string sender = string.IsNullOrWhiteSpace(item.Agent) ? I18n.T("Agent", "Агент") : item.Agent;
                string timeText = RelativeTime(item.Ts);
                float headerW = Theme.MeasureText(g, sender, metaFont).Width + (string.IsNullOrEmpty(timeText) ? 0f : Theme.MeasureText(g, timeText, timeFont).Width + 14f);
                float bubbleW = Math.Clamp(Math.Max(Math.Max(textMaxLineW, headerW), toggleW) + 24f, 128f, maxBubbleW);
                float expandH = expandable ? 16f : 0f;
                float bubbleH = 7f + 15f + lines.Count * 17f + expandH + 7f;
                var bubble = new RectangleF(r.X + 12f, curY, bubbleW, bubbleH);
                bool bHov = expandable && bubble.Contains(mp);

                Theme.FillRound(g, bubble, 11f, bHov ? Theme.SurfaceHover : Theme.SurfaceRaised);
                Theme.StrokeRound(g, bubble, 11f, bHov ? Theme.BorderStrong : Theme.Border);

                float timeW = string.IsNullOrEmpty(timeText) ? 0f : Theme.MeasureText(g, timeText, timeFont).Width + 6f;
                Theme.DrawText(g, sender, metaFont, Theme.SuccessText, new RectangleF(bubble.X + 11f, bubble.Y + 6f, bubble.Width - 22f - timeW, 15f));
                if (!string.IsNullOrEmpty(timeText))
                    Theme.DrawText(g, timeText, timeFont, Theme.TextTertiary, new RectangleF(bubble.Right - 11f - timeW, bubble.Y + 6f, timeW, 15f), StringAlignment.Far);

                float ty = bubble.Y + 22f;
                foreach (string ln in lines)
                {
                    Theme.DrawText(g, ln, textFont, TextPri, new RectangleF(bubble.X + 11f, ty, bubble.Width - 22f, 17f));
                    ty += 17f;
                }
                if (expandable)
                {
                    Theme.DrawText(g, toggleText, linkFont, Theme.AccentText, new RectangleF(bubble.X + 11f, ty + 2f, bubble.Width - 22f, 15f));
                    string cid = item.Id;
                    _hits.Add((bubble, () => { if (!_expandedChatIds.Add(cid)) _expandedChatIds.Remove(cid); ReflowPanel(); Invalidate(); }));
                }
                _tips.Add((bubble, WrapTooltipText(sender + ": " + item.Text)));
                curY += bubbleH;
            }

            if (i < items.Count - 1) curY += 7f;
        }

        curY += 8f;
        using var divPen = new Pen(Theme.Divider);
        g.DrawLine(divPen, r.X + 14f, curY, r.Right - 14f, curY);
        return curY + 2f;
    }

    private void DrawComposerCard(Graphics g, RectangleF r)
    {
        Surface(g, r);
        var lastMsg = _ctx.OwnerMessages.LastOrDefault();
        bool hasQueued = lastMsg != null && lastMsg.Status == "queued";
        int standbySec = _ctx.StandbyRemainingSec;
        bool standbyEnabled = _ctx.StandbyToolEnabled;
        Color tint = standbySec > 0 ? Theme.Success : hasQueued ? Theme.Warning : Theme.Accent;
        Wash(g, r, tint, 18);

        PointF mp = BodyMousePos;
        float curY = r.Y;

        // Expanded header (title + recipient chip + collapse button)
        if (_composerOpen)
        {
            Theme.DrawIcon(g, IconKind.Edit, new RectangleF(r.X + 14, curY + 11, 15, 15), Theme.AccentText, 1.7f);
            Theme.DrawText(g, I18n.T("Agent chat", "Чат с агентом"), PanelTypography.Get(13f, true), TextPri, new RectangleF(r.X + 35, curY + 7, 132, 22));

            string targetName = _composerTargetAll || string.IsNullOrWhiteSpace(_ctx.TaskAgent)
                ? I18n.T("All agents", "Всем агентам")
                : _ctx.TaskAgent;
            var targetFont = PanelTypography.Get(11f, true);
            float targetW = Math.Clamp(Theme.MeasureText(g, targetName, targetFont).Width + 32f, 82f, 170f);
            var targetRect = new RectangleF(r.Right - 44f - targetW, curY + 6f, targetW, 24f);
            bool targetHov = targetRect.Contains(mp);
            Theme.FillRound(g, targetRect, 7f, targetHov ? Theme.SurfaceHover : Theme.SurfaceRaised);
            Theme.StrokeRound(g, targetRect, 7f, targetHov ? Theme.BorderStrong : Theme.Border);
            Theme.DrawIcon(g, IconKind.User, new RectangleF(targetRect.X + 8f, targetRect.Y + 6f, 12f, 12f), Theme.AccentText, 1.6f);
            Theme.DrawText(g, targetName, targetFont, TextPri, new RectangleF(targetRect.X + 24f, targetRect.Y + 3f, targetRect.Width - 30f, 18f));
            _hits.Add((targetRect, () => { _composerTargetAll = !_composerTargetAll; Invalidate(); }));
            _tips.Add((targetRect, I18n.T("Recipient: click to switch between current agent and all agents", "Получатель: нажмите, чтобы переключить — текущему агенту или всем")));

            var collapseRect = new RectangleF(r.Right - 36f, curY + 5f, 26f, 26f);
            bool colHov = collapseRect.Contains(mp);
            if (colHov) Theme.FillRound(g, collapseRect, 7f, Theme.SurfaceHover);
            Theme.DrawIcon(g, IconKind.ChevronUp, new RectangleF(collapseRect.X + 6f, collapseRect.Y + 6f, 14f, 14f), colHov ? TextPri : TextSec, 1.8f);
            _hits.Add((collapseRect, () => ChangeView(() => _composerOpen = false)));
            _tips.Add((collapseRect, I18n.T("Collapse chat · Esc", "Свернуть чат · Esc")));

            using (var hp = new Pen(Theme.Divider)) g.DrawLine(hp, r.X + 12f, curY + 35f, r.Right - 12f, curY + 35f);
            curY += 36f;
        }

        // 1. Active 5-minute standby banner (shown when an agent is actively holding the line)
        if (standbySec > 0)
        {
            string mmss = $"{standbySec / 60:00}:{standbySec % 60:00}";
            var sbBar = new RectangleF(r.X + 12, curY + 6, r.Width - 24, 28);
            Theme.FillRound(g, sbBar, 8, Theme.SuccessSubtle);
            Theme.StrokeRound(g, sbBar, 8, Theme.WithAlpha(Theme.Success, 86));
            Theme.DrawStatusDot(g, new PointF(sbBar.X + 12, sbBar.Y + 14), 3.8f, Theme.Success, halo: true);
            string sbText = I18n.Format("Waiting for instructions · {0}", "Ожидает указаний · {0}", mmss);
            DrawFitted(g, sbText, PanelTypography.Get(11.5f, true), Theme.SuccessText, new RectangleF(sbBar.X + 24, sbBar.Y + 4, sbBar.Width - 118, 20));

            var relBtn = new RectangleF(sbBar.Right - 88, sbBar.Y + 3, 84, 22);
            bool relHov = relBtn.Contains(mp);
            Theme.FillRound(g, relBtn, 6, relHov ? Theme.SurfaceHover : Theme.SurfaceRaised);
            Theme.StrokeRound(g, relBtn, 6, relHov ? Theme.BorderStrong : Theme.Border);
            Theme.DrawText(g, I18n.T("Release", "Отпустить"), PanelTypography.Get(11f, true), TextPri, relBtn, StringAlignment.Center);
            _hits.Add((relBtn, () => { _ = _ctx.ReleaseStandbyAsync(); Invalidate(); }));
            _tips.Add((relBtn, I18n.T("End the 5-minute standby now and let the agent disconnect", "Завершить 5-минутное ожидание сейчас и отпустить агента")));
            curY += 38f;
        }

        // 2. Chat dialogue bubbles
        curY = DrawChatFeed(g, r, curY);

        // 3. Collapsed row
        if (!_composerOpen)
        {
            bool holdOn = _ctx.HoldAfterTask;
            float holdW = standbyEnabled ? (holdOn ? 96f : 72f) : 0f;
            int chatCount = _ctx.ChatHistory.Count;
            float countBadgeW = chatCount > 0 ? 28f : 0f;
            float rightReserve = 20f;
            if (standbyEnabled) rightReserve += holdW + 8f;
            else rightReserve += 20f; // chevron space
            if (chatCount > 0) rightReserve += countBadgeW + 6f;
            if (hasQueued) rightReserve += 32f;

            var rowHit = new RectangleF(r.X, curY, r.Width - rightReserve + 8f, 58f);
            bool rowHov = rowHit.Contains(mp);
            if (rowHov)
            {
                var rowBg = new RectangleF(r.X + 4f, curY + 4f, r.Width - 8f, 50f);
                Theme.FillRound(g, rowBg, 10f, Theme.HoverOverlay);
            }

            Theme.DrawIconTile(g, new RectangleF(r.X + 14, curY + 12, 34, 34), IconKind.Edit, tint, 16);

            string titleText;
            string subText;
            Color titleCol = TextPri;
            Color subCol = TextSec;
            if (DateTime.Now < _composerFeedbackUntil && !string.IsNullOrEmpty(_composerFeedback))
            {
                titleText = _composerFeedback;
                titleCol = Theme.SuccessText;
                subText = I18n.T("Click to open chat", "Нажмите, чтобы открыть чат");
            }
            else if (hasQueued && lastMsg != null)
            {
                titleText = I18n.T("Message queued", "Сообщение в очереди");
                titleCol = Theme.WarningText;
                subText = $"«{lastMsg.Text}»";
            }
            else if (standbySec > 0)
            {
                titleText = I18n.T("Send next instruction", "Отправить следующее указание");
                titleCol = Theme.SuccessText;
                subText = I18n.T("Agent is waiting on the line — click to write", "Агент ждёт на связи — нажмите, чтобы написать");
            }
            else if (_ctx.TaskStatus == "done" && _ctx.ActiveCalls == 0)
            {
                titleText = I18n.T("Message agent", "Написать агенту");
                subText = I18n.T("Turn finished — message will be queued", "Ход завершён — сообщение встанет в очередь");
                subCol = Theme.TextTertiary;
            }
            else if (lastMsg != null && lastMsg.Status == "delivered")
            {
                titleText = I18n.T("Message agent", "Написать агенту");
                subText = I18n.T("Last message delivered · click to write more", "Доставлено · нажмите, чтобы написать ещё");
                subCol = Theme.SuccessText;
            }
            else
            {
                titleText = !string.IsNullOrWhiteSpace(_ctx.TaskAgent)
                    ? I18n.Format("Message · {0}", "Написать · {0}", _ctx.TaskAgent)
                    : I18n.T("Message agent", "Написать агенту");
                subText = I18n.T("Send an instruction to the active turn", "Передать указание в текущий ход");
            }

            float textW = Math.Max(80f, r.Width - 58f - rightReserve);
            DrawFitted(g, titleText, PanelTypography.Get(13f, true), titleCol, new RectangleF(r.X + 58, curY + 11, textW, 20));
            DrawFitted(g, subText, PanelTypography.Get(11.5f), subCol, new RectangleF(r.X + 58, curY + 31, textW, 18));

            float rightCursor = r.Right - 12f;
            if (standbyEnabled)
            {
                var holdRect = new RectangleF(rightCursor - holdW, curY + 15f, holdW, 28f);
                bool holdHov = holdRect.Contains(mp);
                Theme.FillRound(g, holdRect, 8f, holdOn ? Theme.SuccessSubtle : (holdHov ? Theme.SurfaceHover : Theme.SurfaceRaised));
                Theme.StrokeRound(g, holdRect, 8f, holdOn ? Theme.Success : (holdHov ? Theme.BorderStrong : Theme.Border));
                Theme.DrawIcon(g, IconKind.Clock, new RectangleF(holdRect.X + 8f, holdRect.Y + 7.5f, 13f, 13f), holdOn ? Theme.Success : TextSec, 1.7f);
                string pillText = holdOn ? I18n.T("5m · ON", "5м · ВКЛ") : I18n.T("5 min", "5 мин");
                Theme.DrawText(g, pillText, PanelTypography.Get(11f, holdOn), holdOn ? Theme.SuccessText : TextSec, new RectangleF(holdRect.X + 24f, holdRect.Y + 4f, holdRect.Width - 28f, 20f));
                _hits.Add((holdRect, () => { _ = _ctx.SetHoldAfterTaskAsync(!holdOn); Invalidate(); }));
                _tips.Add((holdRect, HoldStandbyTooltip()));
                rightCursor = holdRect.X - 6f;
            }
            else
            {
                Theme.DrawIcon(g, IconKind.ChevronRight, new RectangleF(rightCursor - 18f, curY + 21f, 16f, 16f), rowHov ? TextPri : Theme.TextTertiary, 1.8f);
                rightCursor -= 22f;
            }

            if (chatCount > 0)
            {
                var badgeRect = new RectangleF(rightCursor - countBadgeW, curY + 18f, countBadgeW, 22f);
                Theme.FillRound(g, badgeRect, 11f, Theme.AccentSubtle);
                Theme.StrokeRound(g, badgeRect, 11f, Theme.WithAlpha(Theme.Accent, 95));
                Theme.DrawText(g, chatCount > 99 ? "99+" : chatCount.ToString(), PanelTypography.Get(10.5f, true), Theme.AccentText, badgeRect, StringAlignment.Center);
                _tips.Add((badgeRect, I18n.Format("Chat history: {0} messages · click to open", "История чата: сообщений {0} · нажмите, чтобы открыть", chatCount)));
                rightCursor = badgeRect.X - 6f;
            }

            if (hasQueued && lastMsg != null)
            {
                var cancelRect = new RectangleF(rightCursor - 26f, curY + 16f, 26f, 26f);
                bool cancelHov = cancelRect.Contains(mp);
                if (cancelHov) Theme.FillRound(g, cancelRect, 7f, Theme.WarningSubtle);
                Theme.DrawIcon(g, IconKind.Close, new RectangleF(cancelRect.X + 6f, cancelRect.Y + 6f, 14f, 14f), Theme.WarningText, 1.8f);
                string cancelId = lastMsg.Id;
                _hits.Add((cancelRect, () => { _ = _ctx.CancelOwnerMessageAsync(cancelId); Invalidate(); }));
                _tips.Add((cancelRect, I18n.T("Cancel queued message", "Отменить сообщение в очереди")));
            }

            _hits.Add((rowHit, () => ChangeView(() => _composerOpen = true)));
            return;
        }

        // 4. Expanded inline composer (multiline input box + clean action bar)
        var inputBox = new RectangleF(r.X + 12f, curY + 8f, r.Width - 24f, 62f);
        Theme.FillRound(g, inputBox, 10f, Theme.Bg0);
        Theme.StrokeRound(g, inputBox, 10f, Theme.Accent, 1.3f);

        var inputFont = PanelTypography.Get(12.5f);
        if (string.IsNullOrEmpty(_composerText))
        {
            Theme.DrawText(g, I18n.T("Write an instruction for the agent…", "Напишите указание агенту…"), inputFont, Theme.TextTertiary, new RectangleF(inputBox.X + 12f, inputBox.Y + 10f, inputBox.Width - 24f, 20f));
            Theme.DrawText(g, I18n.T("Enter to send · Ctrl+V to paste", "Enter — отправить · Ctrl+V — вставить"), PanelTypography.Get(10f), Theme.TextDisabled, new RectangleF(inputBox.X + 12f, inputBox.Bottom - 20f, inputBox.Width - 24f, 15f), StringAlignment.Far);
        }
        else
        {
            var inputLines = WrapLines(g, _composerText, inputFont, inputBox.Width - 28f, 3, showTail: true);
            float iy = inputBox.Y + 7f;
            float lastLineW = 0f;
            for (int i = 0; i < inputLines.Count; i++)
            {
                string ln = inputLines[i];
                Theme.DrawText(g, ln, inputFont, TextPri, new RectangleF(inputBox.X + 12f, iy, inputBox.Width - 24f, 17f));
                if (i == inputLines.Count - 1) lastLineW = Theme.MeasureText(g, ln, inputFont).Width;
                else iy += 16f;
            }
            float caretX = Math.Min(inputBox.Right - 12f, inputBox.X + 10f + lastLineW);
            using var caretPen = new Pen(Theme.AccentHover, 1.6f);
            g.DrawLine(caretPen, caretX, iy + 2f, caretX, iy + 15f);
        }

        // Bottom controls row
        float btnY = inputBox.Bottom + 8f;
        bool hasText = !string.IsNullOrWhiteSpace(_composerText);

        if (standbyEnabled)
        {
            bool expHoldOn = _ctx.HoldAfterTask;
            string holdBtnText = expHoldOn ? I18n.T("Hold 5m: ON", "Ждать 5м: ВКЛ") : I18n.T("Hold 5 min", "Ждать 5 мин");
            var holdFont = PanelTypography.Get(11.5f, expHoldOn);
            float holdBtnW = Math.Clamp(Theme.MeasureText(g, holdBtnText, holdFont).Width + 36f, 102f, 150f);
            var holdBtn = new RectangleF(r.X + 12f, btnY, holdBtnW, 32f);
            bool holdHov = holdBtn.Contains(mp);
            Theme.FillRound(g, holdBtn, 8f, expHoldOn ? Theme.SuccessSubtle : (holdHov ? Theme.SurfaceHover : Theme.SurfaceRaised));
            Theme.StrokeRound(g, holdBtn, 8f, expHoldOn ? Theme.Success : (holdHov ? Theme.BorderStrong : Theme.Border));
            Theme.DrawIcon(g, IconKind.Clock, new RectangleF(holdBtn.X + 10f, holdBtn.Y + 9f, 14f, 14f), expHoldOn ? Theme.Success : TextSec, 1.7f);
            Theme.DrawText(g, holdBtnText, holdFont, expHoldOn ? Theme.SuccessText : TextSec, new RectangleF(holdBtn.X + 28f, holdBtn.Y + 6f, holdBtn.Width - 34f, 20f));
            _hits.Add((holdBtn, () => { _ = _ctx.SetHoldAfterTaskAsync(!expHoldOn); Invalidate(); }));
            _tips.Add((holdBtn, HoldStandbyTooltip()));
        }

        var sendRect = new RectangleF(r.Right - 12f - 114f, btnY, 114f, 32f);
        var stopSendRect = new RectangleF(sendRect.X - 8f - 122f, btnY, 122f, 32f);

        Theme.DrawButton(
            g,
            stopSendRect,
            I18n.T("Stop & send", "Стоп и отпр."),
            ButtonKind.Danger,
            hasText ? (stopSendRect.Contains(mp) ? ButtonState.Hover : ButtonState.Normal) : ButtonState.Disabled,
            PanelTypography.Get(11.5f, true),
            8f,
            icon: IconKind.Stop);
        _hits.Add((stopSendRect, () => SubmitComposerMessage(stopCommand: true)));
        _tips.Add((stopSendRect, I18n.T("Stop the currently running command and deliver this message immediately", "Остановить текущую команду и сразу передать это сообщение")));

        Theme.DrawButton(
            g,
            sendRect,
            I18n.T("Send", "Отправить"),
            ButtonKind.Primary,
            hasText ? (sendRect.Contains(mp) ? ButtonState.Hover : ButtonState.Normal) : ButtonState.Disabled,
            PanelTypography.Get(11.5f, true),
            8f,
            icon: IconKind.ChevronRight);
        _hits.Add((sendRect, () => SubmitComposerMessage(stopCommand: false)));
        _tips.Add((sendRect, I18n.T("Deliver this message right away (any running command moves to background)", "Сразу передать сообщение агенту (если идёт команда, она уйдёт в фон)")));
    }

    private void DrawLatestCard(Graphics g, RectangleF r)
    {
        Surface(g, r);
        Theme.DrawText(g, I18n.T("Latest event", "Последнее событие"), PanelTypography.Get(13.5f, true), TextPri, new RectangleF(r.X + 16, r.Y + 10, r.Width - 150, 24));
        var history = new RectangleF(r.Right - 120, r.Y + 10, 104, 24);
        Theme.DrawText(g, I18n.T("Show all", "Показать все"), PanelTypography.Get(12.5f, true), Theme.AccentText, history, StringAlignment.Far);
        _hits.Add((history, () => ChangeView(() => _recentExpanded = true)));
        var ev = _ctx.LastEvent;
        if (ev == null)
        {
            Theme.DrawText(g, I18n.T("No calls in this session yet", "Пока нет вызовов в этой сессии"), PanelTypography.Get(13.5f), TextSec, new RectangleF(r.X + 16, r.Y + 48, r.Width - 32, 24)); return;
        }
        var (_, headline, _) = EventHumanizer.Format(ev);
        if (string.IsNullOrWhiteSpace(ev.Note)) headline = ToolTitle(ev.Tool);
        DrawEventStatus(g, new RectangleF(r.X + 16, r.Y + 52, 20, 20), ev.Status);
        DrawFitted(g, headline, PanelTypography.Get(13.5f), TextPri, new RectangleF(r.X + 48, r.Y + 41, r.Width - 64, 24));
        string original = ev.Tool + " · " + (!string.IsNullOrWhiteSpace(ev.Cmd) ? ev.Cmd : !string.IsNullOrWhiteSpace(ev.Path) ? ev.Path : ev.Detail);
        DrawFitted(g, original.TrimEnd(' ', '·'), PanelTypography.Get(11.5f), TextSec, new RectangleF(r.X + 48, r.Y + 65, r.Width - 64, 20));
        _tips.Add((r, original));
        if (ev.Status is "running" or "pending") UiAnimationClock.DrawMarquee(g, new RectangleF(r.X + 16, r.Bottom - 6, r.Width - 32, 2), Theme.AccentHover, _motion.Seconds);
        string meta = RelativeTime(ev.Ts);
        if (!string.IsNullOrEmpty(meta)) meta += " · ";
        Theme.DrawText(g, meta, PanelTypography.Get(12.5f), Theme.TextTertiary, new RectangleF(r.X + 48, r.Y + 88, r.Width - 64, 20));
        float x = r.X + 48 + Theme.MeasureText(g, meta, PanelTypography.Get(12.5f)).Width;
        string duration = TrayToastForm.FormatRealDuration(ev.DurationMs, ev.StartedAtMs, ev.Status);
        string status = StatusText(ev.Status) + (string.IsNullOrWhiteSpace(duration) ? "" : " · " + duration);
        DrawFitted(g, status, PanelTypography.Get(12.5f), StatusColor(ev.Status), new RectangleF(x, r.Y + 88, r.Right - 16 - x, 20));
    }

    private void DrawHistory(Graphics g, float y)
    {
        var back = new RectangleF(18, y, 32, 34);
        Theme.DrawIcon(g, IconKind.ChevronLeft, new RectangleF(24, y + 9, 16, 16), TextSec);
        _hits.Add((back, () => ChangeView(() => _recentExpanded = false)));
        Theme.DrawText(g, I18n.T("Call history", "История вызовов"), PanelTypography.Get(13.5f, true), TextPri, new RectangleF(60, y, 200, 34));
        using (var p = new Pen(Theme.Divider)) g.DrawLine(p, 18, y + 37, PanelWidth - 18, y + 37);
        using (var p = new Pen(Theme.Accent, 2)) g.DrawLine(p, 60, y + 37, 204, y + 37);
        if (_ctx.MissedCount > 0)
        {
            var missed = new RectangleF(PanelWidth - 206, y, 188, 34);
            Theme.DrawText(g, I18n.Format("Missed · {0}", "Пропущенные · {0}", _ctx.MissedCount), PanelTypography.Get(12.5f, true), Theme.Warning, missed, StringAlignment.Far);
            _hits.Add((missed, () => { Close(); _ctx.OpenMissedRequestsWindow(); }));
        }
        y += 50;
        var events = RecentCalls.Reverse().Take(9).ToList();
        var card = new RectangleF(14, y, PanelWidth - 28, Math.Max(100, events.Count * 70));
        Surface(g, card);
        if (events.Count == 0)
        {
            Theme.DrawText(g, I18n.T("No calls in this session yet", "Пока нет вызовов в этой сессии"), PanelTypography.Get(13.5f), TextSec, card, StringAlignment.Center); return;
        }
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i]; float ry = y + i * 70;
            if (i > 0) { using var p = new Pen(Theme.Divider); g.DrawLine(p, 30, ry, PanelWidth - 30, ry); }
            var (icon, tint) = ToolVisual(ev.Tool);
            Theme.DrawIconTile(g, new RectangleF(30, ry + 17, 36, 36), icon, tint);
            DrawFitted(g, ToolTitle(ev.Tool), PanelTypography.Get(13.5f, true), TextPri, new RectangleF(80, ry + 12, PanelWidth - 298, 23));
            string detail = !string.IsNullOrWhiteSpace(ev.Note) ? ev.Note : !string.IsNullOrWhiteSpace(ev.Path) ? ev.Path : !string.IsNullOrWhiteSpace(ev.Cmd) ? ev.Cmd : ev.Detail;
            DrawFitted(g, detail, PanelTypography.Get(12.5f), TextSec, new RectangleF(80, ry + 36, PanelWidth - 298, 21));
            Theme.DrawText(g, RelativeTime(ev.Ts), PanelTypography.Get(12.5f), Theme.TextTertiary, new RectangleF(PanelWidth - 208, ry + 12, 100, 23), StringAlignment.Far);
            Theme.DrawText(g, StatusText(ev.Status), PanelTypography.Get(12.5f), StatusColor(ev.Status), new RectangleF(PanelWidth - 208, ry + 36, 100, 21), StringAlignment.Far);
            DrawEventStatus(g, new RectangleF(PanelWidth - 80, ry + 25, 20, 20), ev.Status);
        }
        if (RecentCalls.Count > 9) _tips.Add((card, I18n.T("Latest 9 calls. Full history is available in the journal.", "Последние 9 вызовов. Полная история доступна в журнале.")));
    }

    private void DrawFooter(Graphics g)
    {
        float y = LogicalHeight - 54;
        float w = (PanelWidth - 48) / 3;
        void Button(float x, string text, ButtonKind kind, IconKind icon, Action action)
        {
            var r = new RectangleF(x, y, w, 40);
            Theme.DrawButton(g, r, text, kind, r.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal, PanelTypography.Get(13.5f, true), icon: icon);
            _hits.Add((r, action));
        }
        Button(14, I18n.T("Settings", "Настройки"), ButtonKind.Primary, IconKind.Settings, () => { Close(); _ctx.OpenSettingsWindow("general"); });
        Button(24 + w, I18n.T("Journal", "Журнал"), ButtonKind.Secondary, IconKind.File, () => { Close(); _ctx.OpenLogsFolderFromUi(); });
        Button(34 + w * 2, I18n.T("Exit", "Выход"), ButtonKind.Danger, IconKind.Power, () => { Close(); _ctx.ExitFromUi(); });
    }

    private List<string> MessageLines(out bool expandable)
    {
        string raw = _ctx.UserMessageText ?? "";
        if (!string.Equals(raw, _lastBannerText, StringComparison.Ordinal))
        {
            _lastBannerText = raw;
            _messageExpanded = false;
        }
        using var b = new Bitmap(1, 1);
        using var g = Graphics.FromImage(b);
        float max = PanelWidth - 128;
        var font = PanelTypography.Get(12.5f);
        var preview = WrapLinesWithTruncation(g, raw, font, max, 2, out bool truncatedAt2, preserveNewlines: true);
        expandable = truncatedAt2;
        if (!_messageExpanded || !truncatedAt2) return preview;
        return WrapLinesWithTruncation(g, raw, font, max, 14, out _, preserveNewlines: true);
    }

    private void DrawMessageBanner(Graphics g, RectangleF r)
    {
        Color c = _ctx.UserMessageKind == "warning" ? Theme.Warning : _ctx.UserMessageKind == "request" ? Theme.Accent : Theme.Success;
        Surface(g, r); Wash(g, r, c, 38);
        Theme.StrokeRound(g, r, 13, Theme.WithAlpha(c, 76));
        Theme.DrawIconTile(g, new RectangleF(r.X + 14, r.Y + 12, 32, 32), _ctx.UserMessageKind == "warning" ? IconKind.Warning : _ctx.UserMessageKind == "request" ? IconKind.Info : IconKind.Check, c, 18);
        string title = _ctx.UserMessageKind == "warning" ? I18n.T("Attention", "Обратите внимание") : _ctx.UserMessageKind == "request" ? I18n.T("Agent request", "Запрос от агента") : I18n.T("Agent message", "Сообщение от агента");
        if (!string.IsNullOrWhiteSpace(_ctx.UserMessageAgent)) title += " · " + _ctx.UserMessageAgent;
        Theme.DrawText(g, title, PanelTypography.Get(13.5f, true), TextPri, new RectangleF(r.X + 58, r.Y + 7, r.Width - 100, 21));

        var lines = MessageLines(out bool expandable);
        float y = r.Y + 29;
        foreach (string line in lines) { Theme.DrawText(g, line, PanelTypography.Get(12.5f), TextSec, new RectangleF(r.X + 58, y, r.Width - 100, 18)); y += 18; }

        // Register Close and Open link hits BEFORE the expandable banner hit so they always win clicks.
        var close = new RectangleF(r.Right - 34, r.Y + 8, 28, 28);
        Theme.DrawIcon(g, IconKind.Close, new RectangleF(close.X + 6, close.Y + 6, 16, 16), TextSec);
        _hits.Add((close, () => { _messageExpanded = false; _ctx.ClearUserMessage(); ReflowPanel(); Invalidate(); }));
        _tips.Add((close, I18n.T("Dismiss message", "Скрыть сообщение")));

        var link = _ctx.UserMessageLink;
        if (link != null)
        {
            // v0.5: one click to the result the agent produced (page, report, folder).
            var font = PanelTypography.Get(11.5f, true);
            float bw;
            using (var mb = new Bitmap(1, 1)) using (var mg = Graphics.FromImage(mb)) bw = Theme.MeasureText(mg, link.ButtonText, font).Width + 58;
            var btn = new RectangleF(r.X + 58, r.Bottom - 38, Math.Min(bw, r.Width - 100), 28);
            IconKind icon = link.Kind == "url" ? IconKind.Globe : link.Kind == "folder" || link.Reveal ? IconKind.Folder : IconKind.File;
            Theme.DrawButton(g, btn, link.ButtonText, ButtonKind.Outline, btn.Contains(BodyMousePos) ? ButtonState.Hover : ButtonState.Normal, font, 8, icon: icon);
            _hits.Add((btn, () => { Close(); _ctx.OpenUserMessageLink(); }));
            _tips.Add((btn, WrapTooltipText(link.Target)));
            string hint = link.Kind == "url" ? new Uri(link.Target).Host : System.IO.Path.GetFileName(link.Target.TrimEnd('\\', '/'));
            Theme.DrawText(g, hint, PanelTypography.Get(11f), Theme.TextTertiary, new RectangleF(btn.Right + 10, btn.Y + 5, Math.Max(0, r.Right - btn.Right - 54), 18));
        }

        if (expandable)
        {
            string toggleLabel = _messageExpanded ? I18n.T("Collapse ▴", "Свернуть ▴") : I18n.T("Show full message ▾", "Показать полностью ▾");
            var toggleRect = new RectangleF(r.X + 58, y + 2, r.Width - 100, 18);
            Theme.DrawText(g, toggleLabel, PanelTypography.Get(11.5f, true), Theme.AccentText, toggleRect);
            _hits.Add((r, () => { _messageExpanded = !_messageExpanded; ReflowPanel(); Invalidate(); }));
        }
        _tips.Add((r, WrapTooltipText(_ctx.UserMessageText ?? "")));
    }

    private static void Surface(Graphics g, RectangleF r)
    {
        Theme.FillRound(g, r, 13, Theme.Surface);
        Theme.StrokeRound(g, r, 13, Theme.Border);
        // Hairline border keeps the low-contrast reference surface, without a heavy shadow.
    }

    private static void Wash(Graphics g, RectangleF r, Color c, int alpha)
    {
        var saved = g.Save(); using var clip = Theme.RoundRect(r, 13); g.SetClip(clip, CombineMode.Intersect);
        using var b = new LinearGradientBrush(r, Theme.WithAlpha(c, alpha), Theme.WithAlpha(c, 0), LinearGradientMode.Horizontal);
        g.FillRectangle(b, r); g.Restore(saved);
    }

    private void DrawFitted(Graphics g, string? text, Font font, Color color, RectangleF r)
    {
        string clean = (text ?? "").Replace("\r", " ").Replace("\n", " ");
        Theme.DrawText(g, clean, font, color, r);
        if (Theme.MeasureText(g, clean, font).Width > r.Width) _tips.Add((r, WrapTooltipText(clean)));
    }

    private void SessionButton(Graphics g, RectangleF r, string text, Action action)
    {
        var p = new PointF(_mousePos.X, _mousePos.Y + _scrollY);
        Theme.DrawButton(g, r, text, ButtonKind.Secondary, r.Contains(p) ? ButtonState.Hover : ButtonState.Normal, PanelTypography.Get(12.5f, true), 9);
        _hits.Add((r, () => { action(); Invalidate(); }));
    }

    private static Color StatusColor(string s) => s == "done" ? Theme.Success : s is "running" or "pending" ? Theme.Warning : s is "error" or "failed" or "timeout" ? Theme.Danger : Theme.TextSecondary;
    private static string StatusText(string s) => s == "done" ? I18n.T("Success", "Успешно") : s == "running" ? I18n.T("Running", "Выполняется") : s == "pending" ? I18n.T("Pending", "Ожидание") : s == "timeout" ? I18n.T("Timeout", "Таймаут") : s is "error" or "failed" ? I18n.T("Error", "Ошибка") : s == "denied" ? I18n.T("Denied", "Отклонено") : s;
    private static void DrawEventStatus(Graphics g, RectangleF r, string status)
    {
        Color c = StatusColor(status); Theme.FillRound(g, r, r.Width / 2, Theme.WithAlpha(c, 28));
        Theme.DrawIcon(g, status == "done" ? IconKind.Check : status is "running" or "pending" ? IconKind.Clock : status is "error" or "failed" or "timeout" ? IconKind.Warning : IconKind.Minus, RectangleF.Inflate(r, -4, -4), c, 1.8f);
    }

    private static string ToolTitle(string tool) => tool switch
    {
        "read_file" => I18n.T("Read file", "Чтение файла"),
        "read_image" => I18n.T("Read image", "Просмотр изображения"),
        "write_file" => I18n.T("Write file", "Запись файла"),
        "edit_file" => I18n.T("Edit file", "Изменение файла"),
        "file_ops" => I18n.T("File operation", "Операция с файлами"),
        "system_info" => I18n.T("System info", "Сведения о системе"),
        "run_command" => I18n.T("Run command", "Выполнение команды"),
        "take_screenshot" => I18n.T("Screenshot", "Скриншот экрана"),
        "list_directory" => I18n.T("Folder contents", "Содержимое папки"),
        "search_files" => I18n.T("Search files", "Поиск по файлам"),
        "journal_entry" => I18n.T("Write journal entry", "Запись в журнал"),
        "journal_log" => I18n.T("Read journal", "Чтение журнала"),
        "audit_log" => I18n.T("Audit log", "Журнал аудита"),
        "job_status" => I18n.T("Job status", "Статус задачи"),
        "job_output" => I18n.T("Job output", "Результат задачи"),
        "user_message" => I18n.T("Agent message", "Сообщение от агента"),
        "task_status" => I18n.T("Task status", "Статус задачи"),
        "agent_message" => I18n.T("Agent message", "Мессенджер агентов"),
        "await_owner" => I18n.T("Keep connected (5m)", "Ожидание владельца (5м)"),
        _ => tool,
    };
    private static (IconKind Icon, Color Tint) ToolVisual(string tool) => tool switch
    {
        "run_command" => (IconKind.Terminal, Theme.Violet),
        "take_screenshot" or "read_image" => (IconKind.Eye, Theme.Info),
        "list_directory" or "file_ops" => (IconKind.Folder, Theme.Success),
        "system_info" => (IconKind.Info, Theme.Info),
        "write_file" or "edit_file" => (IconKind.Edit, Theme.Warning),
        "search_files" or "audit_log" => (IconKind.Search, Theme.Info),
        _ => (IconKind.File, Theme.Accent),
    };

    private static string FormatElapsed(DateTime startedAt, DateTime? completedAt = null)
    {
        DateTime end = completedAt ?? DateTime.Now;
        int minutes = Math.Max(0, (int)(end - startedAt).TotalMinutes);
        if (minutes == 0)
        {
            int secs = Math.Max(1, (int)(end - startedAt).TotalSeconds);
            return I18n.Format("{0} s", "{0} с", secs);
        }
        return minutes < 60 ? I18n.Format("{0} min", "{0} мин", minutes) : I18n.Format("{0} h {1} min", "{0} ч {1} мин", minutes / 60, minutes % 60);
    }

    private static string RelativeTime(string ts)
    {
        if (!DateTimeOffset.TryParse(ts, out var t)) return "";
        var elapsed = DateTimeOffset.Now - t;
        if (elapsed.TotalSeconds < 60) return I18n.T("Just now", "Только что");
        if (elapsed.TotalMinutes < 60) return I18n.Format("{0} min ago", "{0} мин назад", (int)elapsed.TotalMinutes);
        if (elapsed.TotalHours < 24) return I18n.Format("{0} h ago", "{0} ч назад", (int)elapsed.TotalHours);
        return t.ToLocalTime().ToString("dd.MM HH:mm");
    }

    private static string WrapTooltipText(string text)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Split('\n'))
            for (int i = 0; i < paragraph.Length; i += 90) lines.Add(paragraph.Substring(i, Math.Min(90, paragraph.Length - i)));
        return string.Join("\n", lines);
    }

    public void RenderToBitmapFile(string filePath)
    {
        if (_mode == "panel") Size = new Size((int)(PanelWidth * ScaleFactor), ComputePanelHeight());
        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetResolution(96, 96); // Logical canvas already applies DeviceDpi explicitly.
        using var g = Graphics.FromImage(bmp);
        using var pe = new PaintEventArgs(g, new Rectangle(Point.Empty, Size));
        OnPaint(pe); bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void DrawToggle(Graphics g, float x, float y, bool isOn, Action onClick)
    {
        var rect = new RectangleF(x, y, 44f, 24f);   // hit zone (unchanged)
        bool hov = rect.Contains(_mousePos);
        var vis = new RectangleF(x + 2f, y + 2f, Theme.ToggleWidth, Theme.ToggleHeight);
        Theme.DrawToggle(g, vis, isOn, hov, false, Theme.Success);
        _hits.Add((rect, onClick));
    }

    private void DrawIconCircleButton(Graphics g, RectangleF rect, IconKind icon, bool danger, Action onClick)
    {
        bool hov = rect.Contains(_mousePos);
        Color fill = hov ? (danger ? Theme.DangerSubtle : Theme.SurfaceHover) : Theme.SurfaceRaised;
        Theme.FillRound(g, rect, Theme.RadiusControl, fill);
        Theme.StrokeRound(g, rect, Theme.RadiusControl, hov && danger ? Theme.WithAlpha(Theme.Danger, 110) : Theme.Border);
        var ib = new RectangleF(rect.X + (rect.Width - 16f) / 2f, rect.Y + (rect.Height - 16f) / 2f, 16f, 16f);
        Theme.DrawIcon(g, icon, ib, danger ? Theme.DangerText : Theme.TextPrimary);
        _hits.Add((rect, onClick));
    }

    private void DrawSmallPill(Graphics g, RectangleF rect, string text, Action onClick)
    {
        bool hov = rect.Contains(_mousePos);
        Theme.FillRound(g, rect, Theme.RadiusSmall, hov ? Theme.SurfaceHover : Theme.SurfaceRaised);
        Theme.StrokeRound(g, rect, Theme.RadiusSmall, hov ? Theme.BorderStrong : Theme.Border);
        Theme.DrawText(g, text, Theme.SmallStrong, TextPri, RectangleF.Inflate(rect, -4f, 0f), StringAlignment.Center);
        _hits.Add((rect, onClick));
    }

    private void DrawActionPill(Graphics g, RectangleF rect, string text, ButtonKind kind, Action onClick)
    {
        bool hov = rect.Contains(_mousePos);
        if (kind == ButtonKind.Danger)
        {
            Theme.FillRound(g, rect, Theme.RadiusControl, Theme.WithAlpha(Theme.Danger, hov ? 72 : 41));
            Theme.StrokeRound(g, rect, Theme.RadiusControl, Theme.WithAlpha(Theme.Danger, hov ? 140 : 90));
            Theme.DrawText(g, text, Theme.BodyStrong, Theme.DangerText, rect, StringAlignment.Center);
        }
        else
        {
            Theme.DrawButton(g, rect, text, kind, hov ? ButtonState.Hover : ButtonState.Normal);
        }
        _hits.Add((rect, onClick));
    }

    private static void DrawRoundCard(Graphics g, RectangleF rect, Color bg)
    {
        Theme.DrawCard(g, rect, bg, Theme.RadiusCard);
    }
}
