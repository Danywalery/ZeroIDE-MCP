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
        Size = _mode == "mini" ? new Size(340, 62) : new Size((int)(PanelWidth * ScaleFactor), ComputePanelHeight());
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Clamp(Cursor.Position.X - Width / 2, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - Width - 12)), wa.Bottom - Height - 10);
        Deactivate += (_, _) => { try { Close(); } catch { } };
        _refreshTimer.Tick += (_, _) => { if (_mode == "panel") ReflowPanel(); Invalidate(); };
        _refreshTimer.Start();
        if (_mode == "panel") {
            _motionTimer.Tick += (_, _) => {
                bool moving = Visible && (TaskBlockVisible || _ctx.LastEvent?.Status is "running" or "pending");
                _motion.SetActive(moving);
                if (moving) Invalidate();
            };
            _motionTimer.Start();
        }
        FormClosed += (_, _) => { _refreshTimer.Stop(); _motionTimer.Stop(); _motionTimer.Dispose(); _motion.Dispose(); _toolTip.SetToolTip(this, null); };
    }

    private float MessageHeight => 56f + (MessageLines().Count - 1) * 18f;
    private float TaskDetailsHeight => 62f + (_ctx.TaskDone?.Count > 0 ? Math.Min(_ctx.TaskDone.Count, 3) * 22f + 24f : 0f)
        + (_ctx.TaskNext?.Count > 0 ? Math.Min(_ctx.TaskNext.Count, 3) * 22f + 24f : 0f)
        + ((_ctx.TaskDone?.Count > 3 || _ctx.TaskNext?.Count > 3) ? 22f : 0f);
    private float BodyHeight()
    {
        float h = 70f + (_statusExpanded ? 52f : 0f);
        if (!string.IsNullOrEmpty(_ctx.UserMessageText)) h += MessageHeight + 12f;
        if (_ctx.ConfinementEnabled) h += 42f;
        if (_recentExpanded) return h + 12f + 38f + 12f + Math.Max(100f, Math.Min(RecentCalls.Count, 9) * 70f);
        h += 12f + (TaskBlockVisible ? 70f + (_taskExpanded ? TaskDetailsHeight : 0f) : 176f);
        if (TaskBlockVisible) h += 12f + 120f;
        return h;
    }

    private int ComputePanelHeight()
    {
        if (_mode == "mini") return 62;
        int max = Math.Max(180, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 24);
        return Math.Min((int)Math.Ceiling((132f + BodyHeight()) * ScaleFactor), max);
    }

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
        if (keyData == Keys.Escape) { Close(); return true; }
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
            DrawTaskCard(g, new RectangleF(14, y, PanelWidth - 28, TaskBlockVisible ? 70 + (_taskExpanded ? TaskDetailsHeight : 0) : 176));
            if (TaskBlockVisible) DrawLatestCard(g, new RectangleF(14, y + 82 + (_taskExpanded ? TaskDetailsHeight : 0), PanelWidth - 28, 120));
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
        // The same geometric mark as the references: violet hexagon + white inner ring.
        PointF[] Hex(float cx, float cy, float r) => Enumerable.Range(0, 6).Select(i => {
            double a = (i * 60 - 90) * Math.PI / 180; return new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
        }).ToArray();
        using (var b = new LinearGradientBrush(new Rectangle(21, 16, 20, 22), Theme.AccentHover, Theme.Accent, 90f)) g.FillPolygon(b, Hex(31, 26, 11));
        using (var p = new Pen(Color.White, 2)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, Hex(31, 26, 5.2f)); }
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
        Theme.DrawIconTile(g, new RectangleF(r.X + 16, r.Y + 17, 36, 36), IconKind.File, Theme.Accent);
        DrawFitted(g, I18n.T("Current task", "Текущая задача"), PanelTypography.Get(13.5f, true), TextPri, new RectangleF(r.X + 66, r.Y + 12, r.Width - 134, 23));
        DrawFitted(g, _ctx.TaskTitle, PanelTypography.Get(12.5f), TextSec, new RectangleF(r.X + 66, r.Y + 35, r.Width - 134, 22));
        var arrow = new RectangleF(r.Right - 32, r.Y + 26, 16, 16);
        Theme.DrawIcon(g, _taskExpanded ? IconKind.ChevronUp : IconKind.ChevronRight, arrow, Theme.TextTertiary);
        var closeBtn = new RectangleF(r.Right - 60, r.Y + 22, 24, 24);
        Theme.DrawText(g, "×", PanelTypography.Get(15f, true), Theme.TextTertiary, closeBtn, StringAlignment.Center);
        _hits.Add((closeBtn, () => { _ctx.ClearTaskStatus(); Invalidate(); }));
        _tips.Add((closeBtn, I18n.T("Close task", "Закрыть задачу")));
        _hits.Add((new RectangleF(r.X, r.Y, r.Width, 70), () => ChangeView(() => _taskExpanded = !_taskExpanded)));
        UiAnimationClock.DrawMarquee(g, new RectangleF(r.X + 66, r.Y + 61, r.Width - 110, 2), Theme.AccentHover, _motion.Seconds);
        if (!_taskExpanded) return;
        // TaskCurrent may carry detail not present in TaskTitle: keep it in a native tooltip.
        float y = r.Y + 70;
        DrawFitted(g, string.IsNullOrWhiteSpace(_ctx.TaskCurrent) ? _ctx.TaskTitle : _ctx.TaskCurrent, PanelTypography.Get(12.5f), TextPri, new RectangleF(r.X + 16, y, r.Width - 32, 24));
        string elapsed = _ctx.TaskStartedAt.HasValue ? FormatElapsed(_ctx.TaskStartedAt.Value) : "";
        string agent = string.IsNullOrWhiteSpace(_ctx.TaskAgent) ? I18n.T("AI agent", "AI-агент") : _ctx.TaskAgent;
        DrawFitted(g, agent + (elapsed.Length > 0 ? " · " + elapsed : ""), PanelTypography.Get(12.5f), Theme.TextTertiary, new RectangleF(r.X + 16, y + 24, r.Width - 32, 22));
        y += 50;
        void Rows(IReadOnlyList<string>? rows, bool done)
        {
            if (rows == null || rows.Count == 0) return;
            _tips.Add((new RectangleF(r.X + 16, y, r.Width - 32, 24), string.Join("\n", rows)));
            Theme.DrawText(g, done ? I18n.T("DONE", "ГОТОВО") : I18n.T("NEXT", "ДАЛЕЕ"), PanelTypography.Get(12.5f, true), Theme.TextTertiary, new RectangleF(r.X + 16, y, r.Width - 32, 24)); y += 24;
            foreach (string t in rows.Take(3))
            {
                Theme.DrawIcon(g, done ? IconKind.Check : IconKind.ChevronRight, new RectangleF(r.X + 17, y + 4, 14, 14), done ? Theme.Success : Theme.TextTertiary);
                DrawFitted(g, t, PanelTypography.Get(12.5f), TextSec, new RectangleF(r.X + 40, y, r.Width - 56, 22)); y += 22;
            }
        }
        Rows(_ctx.TaskDone, true); Rows(_ctx.TaskNext, false);
        if (_ctx.TaskDone?.Count > 3 || _ctx.TaskNext?.Count > 3)
            DrawFitted(g, I18n.Format("{0} more steps", "Ещё шагов: {0}", Math.Max(0, (_ctx.TaskDone?.Count ?? 0) - 3) + Math.Max(0, (_ctx.TaskNext?.Count ?? 0) - 3)), PanelTypography.Get(12.5f), Theme.TextTertiary, new RectangleF(r.X + 16, y, r.Width - 32, 22));
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

    private List<string> MessageLines()
    {
        // Pixel-accurate word wrap, including unbroken paths. Never let message text hit Close.
        using var b = new Bitmap(1, 1); using var g = Graphics.FromImage(b);
        float max = PanelWidth - 128;
        var lines = new List<string>(); string line = "";
        foreach (char ch in (_ctx.UserMessageText ?? "").Replace("\r", ""))
        {
            if (ch == '\n') { lines.Add(line); line = ""; }
            else
            {
                string next = line + ch;
                if (line.Length > 0 && Theme.MeasureText(g, next, PanelTypography.Get(12.5f)).Width > max)
                { lines.Add(line.TrimEnd()); line = ch.ToString().TrimStart(); }
                else line = next;
            }
            if (lines.Count == 2) break;
        }
        if (lines.Count < 2 && line.Length > 0) lines.Add(line);
        if (lines.Count == 0) lines.Add("");
        if (lines.Count == 2 && lines.Sum(x => x.Length) < (_ctx.UserMessageText ?? "").Replace("\r", "").Replace("\n", "").Length)
        {
            string last = lines[1];
            while (last.Length > 0 && Theme.MeasureText(g, last + "…", PanelTypography.Get(12.5f)).Width > max) last = last[..^1];
            lines[1] = last.TrimEnd() + "…";
        }
        return lines;
    }

    private void DrawMessageBanner(Graphics g, RectangleF r)
    {
        Color c = _ctx.UserMessageKind == "warning" ? Theme.Warning : _ctx.UserMessageKind == "request" ? Theme.Accent : Theme.Success;
        Surface(g, r); Wash(g, r, c, 38);
        Theme.StrokeRound(g, r, 13, Theme.WithAlpha(c, 76));
        Theme.DrawIconTile(g, new RectangleF(r.X + 14, r.Y + 12, 32, 32), _ctx.UserMessageKind == "warning" ? IconKind.Warning : _ctx.UserMessageKind == "request" ? IconKind.Info : IconKind.Check, c, 18);
        string title = _ctx.UserMessageKind == "warning" ? I18n.T("Attention", "Обратите внимание") : _ctx.UserMessageKind == "request" ? I18n.T("Agent request", "Запрос от агента") : I18n.T("Agent message", "Сообщение от агента");
        Theme.DrawText(g, title, PanelTypography.Get(13.5f, true), TextPri, new RectangleF(r.X + 58, r.Y + 7, r.Width - 100, 21));
        float y = r.Y + 29;
        foreach (string line in MessageLines()) { Theme.DrawText(g, line, PanelTypography.Get(12.5f), TextSec, new RectangleF(r.X + 58, y, r.Width - 100, 18)); y += 18; }
        _tips.Add((r, WrapTooltipText(_ctx.UserMessageText ?? "")));
        var close = new RectangleF(r.Right - 34, r.Y + 8, 28, 28);
        Theme.DrawIcon(g, IconKind.Close, new RectangleF(close.X + 6, close.Y + 6, 16, 16), TextSec);
        _hits.Add((close, () => { _ctx.ClearUserMessage(); ReflowPanel(); Invalidate(); }));
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
        "write_file" => I18n.T("Write file", "Запись файла"),
        "edit_file" => I18n.T("Edit file", "Изменение файла"),
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
        _ => tool,
    };
    private static (IconKind Icon, Color Tint) ToolVisual(string tool) => tool switch
    {
        "run_command" => (IconKind.Terminal, Theme.Violet),
        "take_screenshot" => (IconKind.Eye, Theme.Info),
        "list_directory" => (IconKind.Folder, Theme.Success),
        "write_file" or "edit_file" => (IconKind.Edit, Theme.Warning),
        "search_files" or "audit_log" => (IconKind.Search, Theme.Info),
        _ => (IconKind.File, Theme.Accent),
    };

    private static string FormatElapsed(DateTime startedAt)
    {
        int minutes = Math.Max(0, (int)(DateTime.Now - startedAt).TotalMinutes);
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
