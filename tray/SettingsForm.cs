using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

internal sealed partial class SettingsForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    private readonly TrayContext _ctx;
    private PermissionsConfig _perms;

    private string _activeSection = "tools";
    private string _auditFilter = "all";
    private int _scrollY;
    private int _maxScrollY;
    private Point _mousePos = new(-1, -1);
    private string _hoveredZoneId = "";
    private readonly ToolTip _toolTip = new();
    private string? _lastHoveredTip;

    private List<ActiveGrantItem> _activeGrants = new();
    private List<AuditRowItem> _auditItems = new();
    private readonly List<HitZone> _hitZones = new();
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 1500 };

    // ZeroIDE palette (mapped onto Theme tokens)
    private static readonly Color ColSidebarBg = Theme.Bg0;
    private static readonly Color ColContentBg = Theme.Bg1;
    private static readonly Color ColCardBg = Theme.Surface;
    private static readonly Color ColCardBorder = Theme.Border;
    private static readonly Color ColDivider = Theme.Divider;
    private static readonly Color ColTrough = Theme.SurfaceRaised;
    private static readonly Color ColPillIdle = Theme.SurfaceRaised;
    private static readonly Color ColPillHover = Theme.SurfaceHover;
    private static readonly Color ColTextPrimary = Theme.TextPrimary;
    private static readonly Color ColTextSecondary = Theme.TextSecondary;
    private static readonly Color ColBlue = Theme.Accent;
    private static readonly Color ColGreen = Theme.Success;
    private static readonly Color ColOrange = Theme.Warning;
    private static readonly Color ColRed = Theme.Danger;
    private static readonly Color ColPurple = Theme.Accent;
    private static readonly Color ColCyan = Theme.Info;

    private const int SidebarWidth = 236;
    private const float CaptionHeight = 54f;
    private const float RowH = 64f;
    // Local to settings: the classic menu and mini panel retain their own typography.
    private static Font UiFont(float points, bool strong = false) => PanelTypography.Get(points * 4f / 3f, strong);
    private static Font UiBody => PanelTypography.Get(13.5f);
    private static Font UiBodyStrong => PanelTypography.Get(13.5f, true);
    private static Font UiSmall => PanelTypography.Get(12f);
    private static Font UiSmallStrong => PanelTypography.Get(12f, true);
    private static Font UiCaption => PanelTypography.Get(11.5f);

    private sealed record HitZone(RectangleF Rect, string Id, Action OnClick, string? Tooltip = null);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _liveTimer.Stop(); _liveTimer.Dispose(); _toolTip.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record NavItem(string Id, string TitleEn, string TitleRu, Color BadgeColor, string IconKind)
    {
        public string Title => I18n.IsRu ? TitleRu : TitleEn;
    }

    private static readonly NavItem[] NavItems =
    {
        new("home", "Home", "Главная", Theme.Accent, "home"),
        new("general", "General", "Основные", Theme.TextTertiary, "gear"),
        new("tools", "Tools and permissions", "Инструменты и права", Theme.Accent, "sliders"),
        new("access", "Access", "Доступ", Theme.Success, "user"),
        new("approvals", "Approvals", "Подтверждения", Theme.Warning, "shieldcheck"),
        new("notifications", "Notifications", "Уведомления", Theme.Danger, "bell"),
        new("connection", "Connection", "Подключение", Theme.Info, "link"),
        new("audit", "Journal and audit", "Журнал и аудит", Theme.Accent, "list"),
        new("screenshots", "Screenshots", "Скриншоты", Theme.Info, "image"),
        new("about", "About", "О программе", Theme.TextTertiary, "info"),
    };

    public SettingsForm(TrayContext ctx, string initialSection = "tools")
    {
        _ctx = ctx;
        _perms = PermissionsStore.Load();
        _activeSection = string.IsNullOrWhiteSpace(initialSection) ? "tools" : initialSection;

        Text = I18n.Format("{0} — Settings", "{0} — Настройки", Theme.ProductName);
        FormBorderStyle = FormBorderStyle.None;
        Size = new Size(1180, 800);
        MinimumSize = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ColContentBg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        MaximumSize = Screen.FromPoint(Cursor.Position).WorkingArea.Size;
        LoadAuditItems();
        _ = RefreshGrantsAsync();

        _liveTimer.Tick += async (_, _) =>
        {
            if (_activeSection == "approvals")
            {
                await RefreshGrantsAsync();
            }
            else if (_activeSection == "audit")
            {
                LoadAuditItems();
                Invalidate();
            }
            else if (_activeSection == "access" || _activeSection == "general" || _activeSection == "home" || _activeSection == "connection")
            {
                Invalidate();
            }
        };
        _liveTimer.Start();
        FormClosed += (_, _) => _liveTimer.Stop();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    public void NavigateToSection(string section)
    {
        _perms = PermissionsStore.Load();
        _activeSection = section;
        _scrollY = 0;
        if (section == "audit") LoadAuditItems();
        else if (section == "approvals") _ = RefreshGrantsAsync();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        Invalidate();
    }

    private async Task RefreshGrantsAsync()
    {
        _activeGrants = await _ctx.FetchActiveGrantsAsync();
        Invalidate();
    }

    private void LoadAuditItems()
    {
        _auditItems.Clear();
        string auditPath = Path.Combine(_ctx.GetLogDirPath(), "audit.jsonl");
        if (!File.Exists(auditPath)) return;
        try
        {
            var lines = File.ReadAllLines(auditPath);
            for (int i = lines.Length - 1; i >= 0 && _auditItems.Count < 40; i--)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    string ts = r.TryGetProperty("ts", out var tsEl) ? (tsEl.GetString() ?? "") : "";
                    string tool = r.TryGetProperty("tool", out var tEl) ? (tEl.GetString() ?? "") : "";
                    string ev = r.TryGetProperty("event", out var evEl) ? (evEl.GetString() ?? "") : "";
                    bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                    string detail = "";
                    if (r.TryGetProperty("command", out var cEl)) detail = "$ " + cEl.GetString();
                    else if (r.TryGetProperty("path", out var pEl)) detail = pEl.GetString() ?? "";
                    else if (r.TryGetProperty("note", out var nEl)) detail = nEl.GetString() ?? "";
                    else if (r.TryGetProperty("reason", out var rEl)) detail = rEl.GetString() ?? "";

                    if (_auditFilter == "errors" && ok && !ev.Contains("deny") && !ev.Contains("timeout"))
                        continue;
                    if (_auditFilter == "approvals" && string.IsNullOrEmpty(ev))
                        continue;

                    string timeFormatted = DateTime.TryParse(ts, out var dt)
                        ? dt.ToLocalTime().ToString("HH:mm:ss")
                        : ts;
                    string badge = !string.IsNullOrEmpty(ev) ? ev : tool;
                    _auditItems.Add(new AuditRowItem(timeFormatted, badge, detail ?? "", ok));
                }
                catch { }
            }
        }
        catch { }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_maxScrollY <= 0) return;
        _scrollY = Math.Clamp(_scrollY - e.Delta / 2, 0, _maxScrollY);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mousePos = e.Location;
        var hit = _hitZones.FirstOrDefault(z => z.Rect.Contains(e.Location));
        string newId = hit?.Id ?? "";
        Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        if (newId != _hoveredZoneId)
        {
            _hoveredZoneId = newId;
            Invalidate();
        }
        string? tip = hit?.Tooltip;
        if (tip != _lastHoveredTip)
        {
            _lastHoveredTip = tip;
            _toolTip.SetToolTip(this, tip);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mousePos = new Point(-1, -1);
        Cursor = Cursors.Default;
        if (_lastHoveredTip != null)
        {
            _lastHoveredTip = null;
            _toolTip.SetToolTip(this, null);
        }
        if (!string.IsNullOrEmpty(_hoveredZoneId))
        {
            _hoveredZoneId = "";
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        foreach (var zone in _hitZones.ToList())
        {
            if (zone.Rect.Contains(e.Location))
            {
                zone.OnClick();
                return;
            }
        }

        // Top 54px of window is the draggable title bar (Windows style);
        // double-click toggles maximize, like a standard caption bar.
        if (e.Y <= 54)
        {
            if (e.Clicks == 2)
            {
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                return;
            }
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        var previous = Region;
        if (WindowState == FormWindowState.Maximized) Region = null;
        else { using var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), 16f); Region = new Region(path); }
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _hitZones.Clear();
        var g = e.Graphics;
        Theme.PrepareGraphics(g);
        var saved = g.Save();
        bool maximized = WindowState == FormWindowState.Maximized;
        using var windowPath = Theme.RoundRect(new RectangleF(.5f, .5f, Width - 1f, Height - 1f), maximized ? 0f : 16f);
        g.SetClip(windowPath);
        using (var bg = new LinearGradientBrush(ClientRectangle, Theme.BgGradTop, Theme.BgGradBottom, 90f))
            g.FillRectangle(bg, ClientRectangle);
        using (var sidebar = new SolidBrush(ColSidebarBg))
            g.FillRectangle(sidebar, 0, CaptionHeight, SidebarWidth, Height - CaptionHeight);
        Theme.DrawDivider(g, 0, Width, CaptionHeight);
        using (var pen = new Pen(ColDivider)) g.DrawLine(pen, SidebarWidth, CaptionHeight, SidebarWidth, Height);
        DrawSidebar(g);
        DrawContentPane(g);
        using (var border = new Pen(Theme.BorderStrong)) g.DrawPath(border, windowPath);
        g.Restore(saved);
    }

    // ============================================================ SIDEBAR (ZeroIDE / Fluent)
    private void DrawSidebar(Graphics g)
    {
        bool online = _ctx.CurrentState == "on";
        bool starting = _ctx.CurrentState == "starting";
        Color stateColor = online ? ColGreen : (starting ? ColOrange : Theme.TextTertiary);
        // The same hexagonal brand mark as the approved reference.
        PointF[] outer = { new(28, 14), new(39, 20.5f), new(39, 33.5f), new(28, 40), new(17, 33.5f), new(17, 20.5f) };
        using (var fill = new LinearGradientBrush(new Rectangle(17, 14, 22, 26), Theme.AccentHover, Theme.AccentPressed, 45f)) g.FillPolygon(fill, outer);
        PointF[] inner = { new(28, 21), new(33, 24), new(33, 30), new(28, 33), new(23, 30), new(23, 24) };
        using (var pen = new Pen(Color.White, 1.8f) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, inner);
        Theme.DrawText(g, Theme.ProductName, UiFont(10.5f, true), ColTextPrimary, new RectangleF(50, 15, 150, 25));
        string connection = online ? I18n.T("Connected", "Подключено") : (starting ? I18n.T("Starting", "Запуск") : I18n.T("Offline", "Отключено"));
        float chipW = Theme.MeasureChipOutlinedWidth(g, connection, true, UiSmallStrong);
        float brandW = Theme.MeasureText(g, Theme.ProductName, UiFont(10.5f, true)).Width;
        Theme.DrawChipOutlined(g, new RectangleF(Math.Max(156f, 50f + brandW + 14f), 15, chipW, 25), connection, stateColor, Theme.WithAlpha(stateColor, 20), dot: true, font: UiSmallStrong);
        var min = new RectangleF(Width - 138, 5, 42, 42);
        var max = new RectangleF(Width - 92, 5, 42, 42);
        var close = new RectangleF(Width - 46, 5, 42, 42);
        DrawWinCaptionButton(g, min, 1, false); DrawWinCaptionButton(g, max, 2, WindowState == FormWindowState.Maximized); DrawWinCaptionButton(g, close, 0, false);
        _hitZones.Add(new(min, "win_min", () => WindowState = FormWindowState.Minimized));
        _hitZones.Add(new(max, "win_max", () => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized));
        _hitZones.Add(new(close, "win_close", Close));
        float y = 70f;
        foreach (var item in NavItems)
        {
            var row = new RectangleF(12, y, SidebarWidth - 24, 40);
            bool selected = _activeSection == item.Id;
            bool hover = row.Contains(_mousePos);
            if (selected)
            {
                using var path = Theme.RoundRect(row, 10);
                using var fill = new LinearGradientBrush(row, Theme.WithAlpha(ColBlue, 56), Theme.WithAlpha(ColBlue, 20), 0f);
                g.FillPath(fill, path);
                Theme.FillRound(g, new RectangleF(0, y + 10, 3, 20), 1.5f, ColBlue);
            }
            else if (hover) Theme.FillRound(g, row, 10, Theme.HoverOverlay);
            Theme.DrawIcon(g, GlyphToIcon(item.IconKind), new RectangleF(24, y + 11, 18, 18), selected ? Theme.AccentText : Theme.TextTertiary, 1.7f);
            Theme.DrawText(g, item.Title, UiFont(10.125f, selected), selected ? ColTextPrimary : ColTextSecondary, new RectangleF(54, y, SidebarWidth - 72, 40));
            string id = item.Id;
            _hitZones.Add(new(row, "nav_" + id, () => NavigateToSection(id)));
            y += 44f;
        }
        Theme.DrawDivider(g, 24, SidebarWidth - 24, Height - 78);
        Theme.DrawStatusDot(g, new PointF(29, Height - 54), 3.5f, stateColor, halo: false);
        Theme.DrawText(g, online ? I18n.T("MCP server active", "MCP-сервер активен") : (starting ? I18n.T("Starting server…", "Запуск сервера…") : I18n.T("Server off", "Сервер выключен")), UiSmall, ColTextSecondary, new RectangleF(41, Height - 65, SidebarWidth - 54, 22));
        Theme.DrawText(g, AppVersion, UiCaption, Theme.TextTertiary, new RectangleF(24, Height - 37, SidebarWidth - 40, 20));
    }

    /// <summary>Windows-style caption button (kind: 0 = close, 1 = minimize, 2 = maximize/restore).</summary>
    private void DrawWinCaptionButton(Graphics g, RectangleF zone, int kind, bool isMaximized)
    {
        bool hov = zone.Contains(_mousePos);
        if (hov)
        {
            using var fill = new SolidBrush(kind == 0 ? Theme.Danger : Theme.HoverOverlay);
            g.FillRectangle(fill, zone);
        }
        Color c = hov ? (kind == 0 ? Color.White : ColTextPrimary) : ColTextSecondary;
        float cx = zone.X + zone.Width / 2f;
        float cy = zone.Y + zone.Height / 2f;
        if (kind == 0) Theme.DrawIcon(g, IconKind.Close, new RectangleF(cx - 6f, cy - 6f, 12f, 12f), c, 1.9f);
        else if (kind == 1) Theme.DrawIcon(g, IconKind.Minus, new RectangleF(cx - 6f, cy - 6f, 12f, 12f), c, 1.9f);
        else
        {
            using var pen = new Pen(c, 1.2f);
            if (isMaximized)
            {
                // Restore glyph: two overlapping squares.
                g.DrawRectangle(pen, cx - 5f, cy - 3f, 8f, 8f);
                g.DrawRectangle(pen, cx - 1f, cy - 7f, 8f, 8f);
            }
            else
            {
                g.DrawRectangle(pen, cx - 5f, cy - 5f, 10f, 10f);
            }
        }
    }

    private static IconKind GlyphToIcon(string kind) => kind switch
    {
        "gear" => IconKind.Settings,
        "home" => IconKind.Home,
        "shield" => IconKind.Tools,
        "key" => IconKind.Access,
        "timer" => IconKind.Approvals,
        "bell" => IconKind.Notifications,
        "globe" => IconKind.Connection,
        "list" => IconKind.Audit,
        "camera" => IconKind.Eye,
        "info" => IconKind.Info,
        "sliders" => IconKind.Sliders,
        "user" => IconKind.User,
        "shieldcheck" => IconKind.ShieldCheck,
        "link" => IconKind.Link,
        "image" => IconKind.Image,
        _ => IconKind.About,
    };

    private static void DrawNavGlyph(Graphics g, RectangleF r, string kind) => DrawNavGlyph(g, r, kind, Color.White);

    private static void DrawNavGlyph(Graphics g, RectangleF r, string kind, Color color)
    {
        float size = Math.Max(12f, Math.Min(r.Width, r.Height) * 0.6f);
        var box = new RectangleF(r.X + (r.Width - size) / 2f, r.Y + (r.Height - size) / 2f, size, size);
        // Content rows use more literal glyphs than the sidebar navigation.
        IconKind icon = kind switch
        {
            "shield" => IconKind.Lock,
            "timer" => IconKind.Clock,
            _ => GlyphToIcon(kind),
        };
        Theme.DrawIcon(g, icon, box, color);
    }

    // ============================================================ CONTENT PANE
    private void DrawContentPane(Graphics g)
    {
        float contentX = SidebarWidth + 32f;
        float contentW = Width - SidebarWidth - 64f;

        // Top Header Bar (fixed above scroll)
        var navItem = NavItems.FirstOrDefault(n => n.Id == _activeSection) ?? NavItems[0];
        string? subtitle = SectionSubtitle(_activeSection);
        Theme.DrawText(g, navItem.Title, PanelTypography.Get(23f, true), ColTextPrimary, new RectangleF(contentX - 1f, subtitle == null ? 74f : 66f, contentW, 34f), StringAlignment.Near, StringAlignment.Center);
        if (subtitle != null)
            Theme.DrawText(g, subtitle, PanelTypography.Get(13f), ColTextSecondary, new RectangleF(contentX, 102f, contentW, 22f), StringAlignment.Near, StringAlignment.Center);
        float clipTop = subtitle == null ? 124f : 136f;

        // Clip scrollable region below header
        var clipRect = new RectangleF(SidebarWidth + 1f, clipTop, Width - SidebarWidth - 2f, Height - clipTop - 2f);
        var prevClip = g.Clip;
        g.SetClip(clipRect);

        int firstContentZone = _hitZones.Count;
        float y = clipTop + 6f - _scrollY;

        switch (_activeSection)
        {
            case "home":
                y = DrawHomeContent(g, contentX, contentW, y);
                break;
            case "general":
                y = DrawGeneralContent(g, contentX, contentW, y);
                break;
            case "tools":
                y = DrawToolsContent(g, contentX, contentW, y);
                break;
            case "access":
                y = DrawAccessContent(g, contentX, contentW, y);
                break;
            case "approvals":
                y = DrawApprovalsContent(g, contentX, contentW, y);
                break;
            case "notifications":
                y = DrawNotificationsContent(g, contentX, contentW, y);
                break;
            case "connection":
                y = DrawConnectionContent(g, contentX, contentW, y);
                break;
            case "audit":
                y = DrawAuditContent(g, contentX, contentW, y);
                break;
            case "screenshots":
                y = DrawScreenshotsContent(g, contentX, contentW, y);
                break;
            case "about":
                y = DrawAboutContent(g, contentX, contentW, y);
                break;
        }

        float totalHeight = (y + _scrollY) - clipTop + 24f;
        _maxScrollY = Math.Max(0, (int)(totalHeight - clipRect.Height));
        if (_scrollY > _maxScrollY) _scrollY = _maxScrollY;

        // Custom minimal scrollbar thumb on the right if needed
        if (_maxScrollY > 0)
        {
            float trackH = clipRect.Height - 16f;
            float thumbH = Math.Max(36f, trackH * (clipRect.Height / totalHeight));
            float thumbY = clipRect.Top + 8f + (trackH - thumbH) * (_scrollY / (float)_maxScrollY);
            var thumbRect = new RectangleF(Width - 9f, thumbY, 4f, thumbH);
            using var thumbPath = CreateRoundRect(thumbRect, 2f);
            using var thumbBrush = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            g.FillPath(thumbBrush, thumbPath);
        }

        // Hidden scrolled rows must not intercept clicks on the fixed heading/nav.
        for (int i = _hitZones.Count - 1; i >= firstContentZone; i--)
        {
            var zone = _hitZones[i];
            var visible = RectangleF.Intersect(zone.Rect, clipRect);
            if (visible.Width <= 0 || visible.Height <= 0) _hitZones.RemoveAt(i);
            else _hitZones[i] = zone with { Rect = visible };
        }
        g.Clip = prevClip;
        prevClip.Dispose();
    }

    // ============================================================ 1. GENERAL
    // ============================================================ HOME DASHBOARD
    private float DrawHomeContent(Graphics g, float x, float w, float y)
    {
        bool online = _ctx.CurrentState == "on";
        bool starting = _ctx.CurrentState == "starting";
        Color state = online ? ColGreen : (starting ? ColOrange : Theme.TextTertiary);

        // ---- Hero: server status, actions and summary chips
        const float heroH = 132f;
        var hero = new RectangleF(x, y, w, heroH);
        DrawCardSurface(g, hero);
        using (var heroPath = Theme.RoundRect(hero, Theme.RadiusCard))
        {
            var st = g.Save();
            g.SetClip(heroPath, CombineMode.Intersect);
            DrawRadialWash(g, new PointF(hero.X, hero.Y), w * 0.5f, heroH * 1.7f, Theme.WithAlpha(state, 38));
            DrawRadialWash(g, new PointF(hero.Right, hero.Y), w * 0.45f, heroH * 1.7f, Theme.WithAlpha(ColBlue, 42));
            g.Restore(st);
        }
        Theme.StrokeRound(g, hero, Theme.RadiusCard, ColCardBorder);
        var heroTile = new RectangleF(x + 22f, y + (heroH - 56f) / 2f, 56f, 56f);
        DrawHeroTile(g, heroTile, online || starting ? IconKind.ShieldCheck : IconKind.Power, state, online || starting);

        float right = hero.Right - 20f;
        float btnY = y + 22f;
        float buttonsLeft;
        if (online)
        {
            right = DrawPillButtonRight(g, right, btnY, I18n.T("Disable", "Выключить"), ColRed, Color.White, "home_stop", () => { _ctx.StopServerFromUi("disabled from home"); Invalidate(); }) - 8f;
            right = DrawPillButtonRight(g, right, btnY, I18n.T("+1 hour", "+1 час"), ColPillIdle, ColTextPrimary, "home_60", () => { _ctx.StartServerFromUi(60); Invalidate(); }) - 8f;
            buttonsLeft = DrawPillButtonRight(g, right, btnY, I18n.T("+30 min", "+30 мин"), ColPillIdle, ColTextPrimary, "home_30", () => { _ctx.StartServerFromUi(30); Invalidate(); });
        }
        else
        {
            right = DrawPillButtonRight(g, right, btnY, I18n.T("For 1 hour", "На 1 час"), ColPillIdle, ColTextPrimary, "home_60", () => { _ctx.StartServerFromUi(60); Invalidate(); }) - 8f;
            right = DrawPillButtonRight(g, right, btnY, I18n.T("For 30 min", "На 30 мин"), ColPillIdle, ColTextPrimary, "home_30", () => { _ctx.StartServerFromUi(30); Invalidate(); }) - 8f;
            buttonsLeft = DrawPillButtonRight(g, right, btnY, I18n.T("Enable", "Включить"), ColBlue, Color.White, "home_start", () => { _ctx.StartServerFromUi(null); Invalidate(); });
        }

        float tx = heroTile.Right + 20f;
        string title = online ? I18n.T("MCP online", "MCP онлайн") : (starting ? I18n.T("Starting server…", "Запуск сервера…") : I18n.T("Server off", "Сервер выключен"));
        var titleFont = PanelTypography.Get(20f, true);
        float titleMax = Math.Max(80f, buttonsLeft - 16f - tx);
        float titleW = Math.Min(titleMax, Theme.MeasureText(g, title, titleFont).Width + 2f);
        Theme.DrawText(g, title, titleFont, ColTextPrimary, new RectangleF(tx, y + 20f, titleW, 32f));
        string conn = online ? I18n.T("Connected", "Подключено") : (starting ? I18n.T("Starting", "Запуск") : I18n.T("Offline", "Отключено"));
        float connW = Theme.MeasureChipOutlinedWidth(g, conn, true, UiSmallStrong);
        if (tx + titleW + 12f + connW <= buttonsLeft - 12f)
            Theme.DrawChipOutlined(g, new RectangleF(tx + titleW + 12f, y + 24f, connW, 24f), conn, state, Theme.WithAlpha(state, 28), dot: true, font: UiSmallStrong);

        string sub = online
            ? I18n.Format("Port 127.0.0.1:{0}", "Порт 127.0.0.1:{0}", _ctx.Config.Port) + (_ctx.Config.TunnelEnabled ? I18n.T(" · Tunnel active", " · Туннель активен") : "")
            : (starting ? I18n.T("Starting the server and tunnel…", "Запускаются сервер и туннель…") : I18n.T("Enable the server to connect an agent", "Включите сервер для подключения агента"));
        Theme.DrawText(g, sub, PanelTypography.Get(13f), ColTextSecondary, new RectangleF(tx, y + 56f, hero.Right - 20f - tx, 22f));

        int tokens = 0;
        try { tokens = AccessStore.LoadTokens().Tokens.Count(t => !t.Revoked); } catch { }
        float chipY = y + 90f;
        float chipsRight = hero.Right - 20f;
        string missedText = I18n.Format("Missed: {0}", "Пропущено: {0}", _ctx.MissedCount);
        if (_ctx.MissedCount > 0)
        {
            float mw = ChipWidth(g, missedText, true);
            float mx = hero.Right - 20f - mw;
            DrawInfoChip(g, ref mx, hero.Right, chipY, IconKind.Clock, missedText, "home_stat_missed", () => _ctx.OpenMissedRequestsWindow(), ColOrange);
            chipsRight = hero.Right - 20f - mw - 10f;
        }
        float cx = tx;
        DrawInfoChip(g, ref cx, chipsRight, chipY, IconKind.Server, starting ? I18n.T("Starting…", "Запуск…") : w < 800f ? (online ? I18n.T("Active", "Активен") : I18n.T("Off", "Выключен")) : (online ? I18n.T("MCP server active", "MCP-сервер активен") : I18n.T("MCP server off", "MCP-сервер выключен")), null, null);
        DrawInfoChip(g, ref cx, chipsRight, chipY, null, AppVersion, null, null);
        DrawInfoChip(g, ref cx, chipsRight, chipY, IconKind.Access, I18n.Format("Tokens: {0}", "Токенов: {0}", tokens), "home_stat_tokens", () => NavigateToSection("access"));
        DrawInfoChip(g, ref cx, chipsRight, chipY, IconKind.Clock, I18n.Format("Active grants: {0}", "Активных грантов: {0}", _ctx.ActiveGrantsCount), "home_stat_grants", () => NavigateToSection("approvals"));
        if (_ctx.MissedCount == 0)
            DrawInfoChip(g, ref cx, chipsRight, chipY, null, missedText, "home_stat_missed", () => _ctx.OpenMissedRequestsWindow());
        y += heroH + 24f;

        // ---- Key parameters
        y = DrawGroupTitle(g, x, y, I18n.T("KEY PARAMETERS", "КЛЮЧЕВЫЕ ПАРАМЕТРЫ"));
        string preset = PermissionsStore.DetectActivePreset(_perms);
        string presetName = preset switch { "cautious" => I18n.T("Cautious", "Осторожный"), "balanced" => I18n.T("Balanced", "Сбалансированный"), "trusting" => I18n.T("Trusting", "Доверчивый"), _ => I18n.T("Custom", "Индивидуальный") };
        Color presetColor = preset switch { "cautious" => ColOrange, "balanced" => ColBlue, "trusting" => ColGreen, _ => Theme.Violet };
        int allowed = PermissionsStore.AllTools.Count(t => PermissionsStore.GetToolMode(_perms, t.Name) != "deny");
        bool confined = _perms.Confinement?.Enabled == true;
        var parameters = new (string Label, string Value, IconKind Icon, Color Tint, string Section, Color? Chip)[] {
            (I18n.T("Tools", "Инструменты"), I18n.Format("{0} of {1}", "{0} из {1}", allowed, PermissionsStore.AllTools.Count()), IconKind.Tools, ColBlue, "tools", null),
            (I18n.T("Permissions", "Права"), confined ? I18n.T("Restricted", "Ограничены") : I18n.T("Per tool", "По инструментам"), IconKind.User, ColOrange, "access", confined ? ColOrange : ColBlue),
            (w < 800f ? I18n.T("Profile", "Профиль") : I18n.T("Security profile", "Профиль безопасности"), presetName, IconKind.Gauge, Theme.Violet, "tools", presetColor),
            (I18n.T("Response timeout", "Таймаут ответа"), I18n.Format("{0} seconds", "{0} секунд", _perms.ApprovalTimeoutSec), IconKind.Clock, Theme.Info, "approvals", null) };
        const float kGap = 14f, kH = 100f;
        float kW = (w - kGap * 3f) / 4f;
        for (int i = 0; i < parameters.Length; i++)
        {
            var (label, value, icon, tint, section, chip) = parameters[i];
            var tile = new RectangleF(x + i * (kW + kGap), y, kW, kH);
            bool hov = tile.Contains(_mousePos);
            DrawCardSurface(g, tile);
            if (hov) Theme.StrokeRound(g, tile, Theme.RadiusCard, Theme.BorderStrong);
            var ic = new RectangleF(tile.X + 16f, tile.Y + 16f, 30f, 30f);
            DrawTintTile(g, ic, icon, tint, 8f, 15f);
            float lx = ic.Right + 10f, lw = tile.Right - 14f - lx;
            Theme.DrawText(g, label, FitFont(g, label, 12.5f, 11f, lw, false), ColTextSecondary, new RectangleF(lx, ic.Y, lw, 30f));
            if (chip is Color cc)
            {
                var cf = FitFont(g, value, 13f, 11f, kW - 54f, true);
                float pw = Math.Min(kW - 32f, Theme.MeasureText(g, value, cf).Width + 24f);
                var pr = new RectangleF(tile.X + 16f, tile.Y + 60f, pw, 26f);
                Theme.FillRound(g, pr, 13f, Theme.WithAlpha(cc, 36));
                Theme.StrokeRound(g, pr, 13f, Theme.WithAlpha(cc, 80));
                Theme.DrawText(g, value, cf, Theme.TextVariant(cc), RectangleF.Inflate(pr, -8f, 0f), StringAlignment.Center);
            }
            else
            {
                Theme.DrawText(g, value, FitFont(g, value, 20f, 14f, kW - 32f, true), ColTextPrimary, new RectangleF(tile.X + 16f, tile.Y + 57f, kW - 32f, 32f));
            }
            _hitZones.Add(new(tile, "home_parameter_" + i, () => NavigateToSection(section), label + ": " + value));
        }
        y += kH + 24f;

        // ---- Popular settings (real toggles) + current task / recent calls
        float gap = 20f;
        float rw = Math.Clamp(w * 0.41f, 290f, 380f);
        float lwCol = w - rw - gap;
        float rx = x + lwCol + gap;
        float popY = DrawGroupTitle(g, x, y, I18n.T("POPULAR SETTINGS", "ПОПУЛЯРНЫЕ НАСТРОЙКИ"));
        DrawGroupTitle(g, rx, y, I18n.T("CURRENT TASK", "ТЕКУЩАЯ ЗАДАЧА"));

        bool rfOn = _perms.RedFlags?.Enabled != false && _perms.RedFlags?.ElevateToAsk != false;
        bool notifOn = _ctx.Config.NotificationsEnabled;
        bool shotsOn = _ctx.Config.SaveScreenshots;
        bool autostart = _ctx.GetAutostartFromUi();
        var popular = new (IconKind Icon, Color Tint, string Title, string Sub, bool On, string Id, Action Toggle, string Section)[]
        {
            (IconKind.ShieldCheck, ColBlue, I18n.T("Dangerous command filter (Red-Flag)", "Фильтр опасных команд (Red-Flag)"), I18n.T("Always ask before destructive commands", "Всегда спрашивать перед деструктивными командами"), rfOn, "home_toggle_redflags", ToggleRedFlags, "tools"),
            (IconKind.FolderLock, ColBlue, I18n.T("Restrict to files in one folder", "Ограничить файлами одной папки"), I18n.T("The agent sees only files inside the chosen folder", "Агент видит только файлы внутри выбранной папки"), confined, "home_toggle_conf", ToggleConfinement, "access"),
            (IconKind.Notifications, Theme.Violet, I18n.T("Action pop-up cards", "Всплывающие карточки действий"), I18n.T("Cards on MCP tool calls (always silent)", "Карточки при вызовах инструментов MCP (без звука)"), notifOn, "home_toggle_notif", () => _ctx.SetNotificationsEnabledFromUi(!notifOn), "notifications"),
            (IconKind.Image, Theme.Info, I18n.T("Save approved screenshots", "Сохранять одобренные скриншоты"), I18n.T("Only approved shots, with a caption bar", "Только одобренные кадры, с полосой-подписью"), shotsOn, "home_toggle_shots", () => { _ctx.Config.SaveScreenshots = !shotsOn; _ctx.SaveConfigFromUi(); }, "screenshots"),
            (IconKind.Power, Theme.TextTertiary, I18n.T("Run with Windows", "Запускать вместе с Windows"), I18n.T("Add the tray icon at logon", "Добавлять иконку в трей при входе в систему"), autostart, "home_toggle_autostart", () => _ctx.SetAutostartFromUi(!autostart), "general"),
        };
        const float pRow = 62f;
        DrawCardSurface(g, new RectangleF(x, popY, lwCol, pRow * popular.Length));
        for (int i = 0; i < popular.Length; i++)
        {
            var it = popular[i];
            float ry = popY + i * pRow;
            if (i > 0) Theme.DrawDivider(g, x + 1f, x + lwCol - 1f, ry);
            var rowRect = new RectangleF(x, ry, lwCol - 72f, pRow);
            if (rowRect.Contains(_mousePos)) Theme.FillRound(g, new RectangleF(x + 4f, ry + 4f, lwCol - 8f, pRow - 8f), 10f, Theme.WithAlpha(Color.White, 8));
            var ic = new RectangleF(x + 16f, ry + (pRow - 30f) / 2f, 30f, 30f);
            DrawTintTile(g, ic, it.Icon, it.Tint, 8f, 15f);
            var toggle = it.Toggle;
            DrawIosToggle(g, x + lwCol - 16f - 44f, ry + (pRow - 24f) / 2f, it.On, it.Id, () => { toggle(); Invalidate(); });
            float ttx = ic.Right + 14f, tw = x + lwCol - 16f - 44f - 12f - ttx;
            Theme.DrawText(g, it.Title, UiBodyStrong, ColTextPrimary, new RectangleF(ttx, ry + 10f, tw, 22f));
            Theme.DrawText(g, it.Sub, UiSmall, ColTextSecondary, new RectangleF(ttx, ry + 32f, tw, 20f));
            string section = it.Section;
            _hitZones.Add(new(rowRect, "home_link_" + section + "_" + i, () => NavigateToSection(section), it.Title + "\n" + it.Sub));
        }
        float leftBottom = popY + pRow * popular.Length;

        // Current task card
        float ry2 = popY;
        string task = _ctx.TaskTitle ?? "";
        bool hasTask = task.Length > 0;
        int done = _ctx.TaskDone?.Count ?? 0, next = _ctx.TaskNext?.Count ?? 0;
        bool hasCur = !string.IsNullOrWhiteSpace(_ctx.TaskCurrent);
        int total = done + next + (hasCur ? 1 : 0);
        bool showProgress = hasTask && total > 0;
        float taskH = showProgress ? 98f : 76f;
        var taskBox = new RectangleF(rx, ry2, rw, taskH);
        Theme.FillRound(g, taskBox, Theme.RadiusCard, ColCardBg);
        Theme.FillRoundGradient(g, taskBox, Theme.RadiusCard, Theme.WithAlpha(ColBlue, hasTask ? 32 : 12), Theme.WithAlpha(ColBlue, hasTask ? 8 : 3));
        Theme.StrokeRound(g, taskBox, Theme.RadiusCard, hasTask ? Theme.WithAlpha(ColBlue, 66) : ColCardBorder);
        Theme.DrawStatusDot(g, new PointF(rx + 26f, ry2 + 30f), 5.5f, hasTask ? ColGreen : Theme.TextTertiary, halo: hasTask);
        string pctText = showProgress ? (int)Math.Round(done * 100.0 / total) + "%" : "";
        float pctW = showProgress ? Theme.MeasureText(g, pctText, PanelTypography.Get(15f, true)).Width + 4f : 0f;
        float taskTx = rx + 46f, taskTw = rw - 46f - 16f - (pctW > 0 ? pctW + 8f : 0f);
        if (hasTask)
        {
            Theme.DrawText(g, task, UiBodyStrong, ColTextPrimary, new RectangleF(taskTx, ry2 + 12f, taskTw, 22f));
            Theme.DrawText(g, _ctx.TaskCurrent ?? "", PanelTypography.Get(12.5f), ColTextSecondary, new RectangleF(taskTx, ry2 + 34f, taskTw, 20f));
        }
        else
        {
            Theme.DrawText(g, I18n.T("No current task", "Нет текущей задачи"), UiBodyStrong, ColTextSecondary, new RectangleF(taskTx, ry2 + 19f, taskTw, 22f));
        }
        if (showProgress)
        {
            Theme.DrawText(g, pctText, PanelTypography.Get(15f, true), ColTextPrimary, new RectangleF(rx + rw - 16f - pctW, ry2 + 12f, pctW, 22f), StringAlignment.Far);
            var bar = new RectangleF(rx + 16f, ry2 + taskH - 26f, rw - 32f, 6f);
            Theme.FillRound(g, bar, 3f, Theme.WithAlpha(Color.White, 18));
            float frac = Math.Clamp(done / (float)total, 0f, 1f);
            if (frac > 0f)
            {
                var fill = new RectangleF(bar.X, bar.Y, Math.Max(6f, bar.Width * frac), bar.Height);
                Theme.DrawGlow(g, fill, 3f, Theme.WithAlpha(ColBlue, 70), 5);
                using var fp = Theme.RoundRect(fill, 3f);
                using var fb = new LinearGradientBrush(new RectangleF(fill.X - 1f, fill.Y, fill.Width + 2f, fill.Height), ColBlue, Color.FromArgb(0x8D, 0x99, 0xFF), LinearGradientMode.Horizontal);
                g.FillPath(fb, fp);
            }
        }
        string taskTip = hasTask ? task + "\n" + (_ctx.TaskCurrent ?? "") + (showProgress ? "\n" + I18n.Format("Steps done: {0} of {1}", "Выполнено шагов: {0} из {1}", done, total) : "") : I18n.T("No current task", "Нет текущей задачи");
        _hitZones.Add(new(taskBox, "home_current_task", () => { }, taskTip));
        ry2 += taskH + 14f;

        // Recent calls card
        var recent = (_ctx.RecentEvents ?? Array.Empty<McpToolEvent>()).Reverse().Take(4).ToList();
        if (recent.Count == 0 && _ctx.LastEvent != null) recent.Add(_ctx.LastEvent);
        const float headH = 46f, itemH = 50f;
        float listH = recent.Count == 0 ? 52f : recent.Count * itemH;
        var rc = new RectangleF(rx, ry2, rw, headH + listH + 4f);
        DrawCardSurface(g, rc);
        Theme.DrawText(g, I18n.T("Recent calls", "Последние вызовы"), UiBodyStrong, ColTextPrimary, new RectangleF(rx + 16f, ry2 + 12f, rw - 140f, 22f));
        string showAll = I18n.T("Show all", "Показать все");
        var linkFont = PanelTypography.Get(12.5f, true);
        float linkW = Theme.MeasureText(g, showAll, linkFont).Width + 4f;
        var linkRect = new RectangleF(rx + rw - 16f - linkW, ry2 + 12f, linkW, 22f);
        bool linkHov = linkRect.Contains(_mousePos);
        Theme.DrawText(g, showAll, linkFont, linkHov ? Theme.AccentHover : Color.FromArgb(0x8F, 0x9B, 0xFF), linkRect, StringAlignment.Far);
        _hitZones.Add(new(RectangleF.Inflate(linkRect, 4f, 4f), "home_audit_all", () => NavigateToSection("audit")));
        for (int i = 0; i < recent.Count; i++)
        {
            var ev = recent[i];
            var (kind, headline, meta) = EventHumanizer.Format(ev);
            float iy = ry2 + headH + i * itemH;
            Theme.DrawDivider(g, rx + 1f, rx + rw - 1f, iy);
            var ic = new RectangleF(rx + 16f, iy + (itemH - 30f) / 2f, 30f, 30f);
            DrawTintTile(g, ic, EventIcon(kind, ev.Tool), Theme.TextTertiary, 8f, 15f);
            string ago = TimeAgo(ev.Ts);
            var capFont = PanelTypography.Get(12f);
            float agoW = string.IsNullOrEmpty(ago) ? 0f : Theme.MeasureText(g, ago, capFont).Width + 4f;
            var stRect = new RectangleF(rx + rw - 16f - 20f, iy + (itemH - 20f) / 2f, 20f, 20f);
            DrawCallStatus(g, stRect, ev.Status);
            if (agoW > 0) Theme.DrawText(g, ago, capFont, Theme.TextTertiary, new RectangleF(stRect.X - 10f - agoW, iy, agoW, itemH), StringAlignment.Far);
            float htx = ic.Right + 12f, htw = stRect.X - 10f - (agoW > 0 ? agoW + 10f : 0f) - htx;
            Theme.DrawText(g, headline, PanelTypography.Get(13f, true), ColTextPrimary, new RectangleF(htx, iy + 7f, htw, 20f));
            Theme.DrawText(g, ev.Tool, Theme.FontMono(8.25f), Theme.TextTertiary, new RectangleF(htx, iy + 27f, htw, 16f));
            string duration = TrayToastForm.FormatRealDuration(ev.DurationMs, null, ev.Status);
            _hitZones.Add(new(new RectangleF(rx + 8f, iy, rw - 16f, itemH), "home_recent_" + i, () => NavigateToSection("audit"), headline + "\n" + meta + "\n" + duration));
        }
        if (recent.Count == 0)
            Theme.DrawText(g, I18n.T("No calls in this session yet", "Пока нет вызовов в этой сессии"), UiSmall, ColTextSecondary, new RectangleF(rx + 16f, ry2 + headH, rw - 32f, 40f));
        float rightBottom = rc.Bottom;

        // ---- Parameters (kept from the previous home page)
        y = Math.Max(leftBottom, rightBottom) + 24f;
        y = DrawGroupTitle(g, x, y, I18n.T("PARAMETERS", "ПАРАМЕТРЫ"));
        string menu = (_ctx.Config.TrayMenuMode ?? "").ToLowerInvariant() switch { "panel" => I18n.T("Panel", "Панель"), "mini" => I18n.T("Mini", "Мини"), _ => I18n.T("Classic", "Классика") };
        var links = new (string Label, string Value, IconKind Icon, Color Tint, string Section, bool Mono)[] {
            (I18n.T("Project working folder", "Рабочая папка проекта"), _ctx.Config.ProjectDir ?? "", IconKind.Folder, ColBlue, "general", true),
            (I18n.T("MCP port", "Порт MCP"), $"127.0.0.1:{_ctx.Config.Port}", IconKind.Connection, Theme.Info, "connection", true),
            ("Language / Язык", (_ctx.Config.Language ?? "en") == "ru" ? "Русский" : "English", IconKind.Globe, ColGreen, "general", false),
            (I18n.T("Tray menu style", "Вид меню трея"), menu, IconKind.Menu, Theme.Violet, "general", false) };
        int cols = w >= 820f ? 4 : 2;
        const float pGap = 14f, pH = 74f;
        float pW = (w - pGap * (cols - 1)) / cols;
        for (int i = 0; i < links.Length; i++)
        {
            var (label, value, icon, tint, section, mono) = links[i];
            var box = new RectangleF(x + (i % cols) * (pW + pGap), y + (i / cols) * (pH + pGap), pW, pH);
            DrawCardSurface(g, box);
            if (box.Contains(_mousePos)) Theme.StrokeRound(g, box, Theme.RadiusCard, Theme.BorderStrong);
            var ic = new RectangleF(box.X + 16f, box.Y + (pH - 30f) / 2f, 30f, 30f);
            DrawTintTile(g, ic, icon, tint, 8f, 15f);
            float ltx = ic.Right + 12f, ltw = box.Right - 14f - ltx;
            Theme.DrawText(g, label, PanelTypography.Get(12f), ColTextSecondary, new RectangleF(ltx, box.Y + 15f, ltw, 20f));
            Theme.DrawText(g, value, mono ? Theme.FontMono(9.25f) : UiBodyStrong, ColTextPrimary, new RectangleF(ltx, box.Y + 37f, ltw, 22f));
            _hitZones.Add(new(box, "home_parameter_link_" + i, () => NavigateToSection(section), label + "\n" + value));
        }
        int rows = (links.Length + cols - 1) / cols;
        return y + rows * pH + (rows - 1) * pGap;
    }

    private float DrawGeneralContent(Graphics g, float x, float w, float y)
    {
        // Hero Status Card (macOS Sonoma style)
        var heroRect = new RectangleF(x, y, w, 86f);
        DrawCardSurface(g, heroRect);

        bool isOn = _ctx.CurrentState == "on";
        DrawHeroTile(g, new RectangleF(x + 18f, y + 19f, 48f, 48f), IconKind.Server, isOn ? ColGreen : Theme.TextTertiary, isOn);

        var titleFont = UiFont(12.0f, true);
        var subFont = UiFont(9.5f);
        using var pBrush = new SolidBrush(ColTextPrimary);
        using var sBrush = new SolidBrush(ColTextSecondary);

        string stTitle = isOn
            ? I18n.T("MCP server and tunnel are running", "MCP-сервер и туннель работают")
            : (_ctx.CurrentState == "starting" ? I18n.T("Starting server…", "Запуск сервера…") : I18n.T("Server off", "Сервер выключен"));
        string publicHost = TunnelProfiles.PublicHost(_ctx.Config);
        string stSub = isOn
            ? (publicHost.Length > 0
                ? I18n.Format("Online · 127.0.0.1:{0} · {1}/mcp", "Онлайн · 127.0.0.1:{0} · {1}/mcp", _ctx.Config.Port, publicHost)
                : I18n.Format("Online · 127.0.0.1:{0}", "Онлайн · 127.0.0.1:{0}", _ctx.Config.Port))
            : I18n.T("Press “Enable” to start the server and tunnel", "Нажмите «Включить» для запуска сервера и туннеля");
        var heroFmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(stTitle, titleFont, pBrush, new RectangleF(x + 82f, y + 21f, w - 380f, 24f), heroFmt);
        g.DrawString(stSub, subFont, sBrush, new RectangleF(x + 82f, y + 46f, w - 380f, 20f), heroFmt);

        // Pill action buttons on right of Hero Card
        float btnRight = heroRect.Right - 16f;
        if (isOn)
        {
            btnRight = DrawPillButtonRight(g, btnRight, y + 28f, I18n.T("Disable", "Выключить"), ColRed, Color.White, "gen_stop", () => { _ctx.StopServerFromUi("disabled from settings"); Invalidate(); }) - 8f;
            btnRight = DrawPillButtonRight(g, btnRight, y + 28f, I18n.T("+1 hour", "+1 час"), ColPillIdle, ColTextPrimary, "gen_60", () => { _ctx.StartServerFromUi(60); Invalidate(); }) - 8f;
            DrawPillButtonRight(g, btnRight, y + 28f, I18n.T("+30 min", "+30 мин"), ColPillIdle, ColTextPrimary, "gen_30", () => { _ctx.StartServerFromUi(30); Invalidate(); });
        }
        else
        {
            btnRight = DrawPillButtonRight(g, btnRight, y + 28f, I18n.T("For 1 hour", "На 1 час"), ColPillIdle, ColTextPrimary, "gen_60", () => { _ctx.StartServerFromUi(60); Invalidate(); }) - 8f;
            btnRight = DrawPillButtonRight(g, btnRight, y + 28f, I18n.T("For 30 min", "На 30 мин"), ColPillIdle, ColTextPrimary, "gen_30", () => { _ctx.StartServerFromUi(30); Invalidate(); }) - 8f;
            DrawPillButtonRight(g, btnRight, y + 28f, I18n.T("Enable", "Включить"), ColGreen, Color.Black, "gen_start", () => { _ctx.StartServerFromUi(null); Invalidate(); });
        }

        y += 104f;
        y = DrawGroupTitle(g, x, y, I18n.T("WORKSPACE, TRAY AND AUTOSTART", "РАБОЧАЯ СРЕДА, ТРЕЙ И АВТОЗАПУСК"));

        // 5-row grouped card (Language, Menu mode, Project folder, Idle off, Autostart)
        float rowH = RowH;
        var cardRect = new RectangleF(x, y, w, rowH * 5f);
        DrawCardSurface(g, cardRect);

        // Row 1: Language / Язык
        string curLang = (string.IsNullOrEmpty(_ctx.Config.Language) ? "en" : _ctx.Config.Language.ToLowerInvariant()) == "ru" ? "ru" : "en";
        DrawSettingRow(g, x, y, w - 240f, rowH, ColBlue, "globe", "Language / Язык", I18n.T("Interface language. Server response language applies on next start.", "Язык интерфейса. Язык ответов сервера применится при следующем запуске."), drawDivider: true);
        DrawSegmentedBar(
            g,
            new RectangleF(x + w - 230f, y + 16f, 214f, 32f),
            new (string Key, string Title, Color ActiveCol)[]
            {
                ("en", "English", ColBlue),
                ("ru", "Русский", ColBlue),
            },
            curLang,
            "gen_lang",
            picked =>
            {
                _ctx.ApplyLanguage(picked);
                Invalidate();
            });

        // Row 2: Tray Menu Mode ("classic" | "panel" | "mini")
        y += rowH;
        string curMenuMode = string.IsNullOrEmpty(_ctx.Config.TrayMenuMode) ? "classic" : _ctx.Config.TrayMenuMode.ToLowerInvariant();
        DrawSettingRow(g, x, y, w - 310f, rowH, ColPurple, "list", I18n.T("Tray menu style", "Вид меню трея"), I18n.T("Classic Windows menu, control panel, or compact strip", "Классическое меню Windows, панель управления или компактная полоска"), drawDivider: true);
        DrawSegmentedBar(
            g,
            new RectangleF(x + w - 306f, y + 16f, 290f, 32f),
            new (string Key, string Title, Color ActiveCol)[]
            {
                ("classic", I18n.T("Classic", "Классика"), ColBlue),
                ("panel", I18n.T("Panel", "Панель"), ColPurple),
                ("mini", I18n.T("Mini", "Мини"), ColGreen),
            },
            curMenuMode,
            "gen_tray_mode",
            picked =>
            {
                _ctx.ApplyTrayMenuMode(picked);
                Invalidate();
            });

        // Row 3: Project folder
        y += rowH;
        DrawSettingRow(g, x, y, w, rowH, ColBlue, "folder", I18n.T("Project working folder", "Рабочая папка проекта"), _ctx.Config.ProjectDir, drawDivider: true);
        float rx = x + w - 16f;
        rx = DrawPillButtonRight(g, rx, y + 16f, I18n.T("Open", "Открыть"), ColBlue, Color.White, "gen_open_proj", () => _ctx.OpenProjectFolderFromUi()) - 8f;
        DrawPillButtonRight(g, rx, y + 16f, I18n.T("Browse…", "Обзор…"), ColPillIdle, ColTextPrimary, "gen_browse_proj", () =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _ctx.Config.ProjectDir };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _ctx.Config.ProjectDir = dlg.SelectedPath;
                _ctx.SaveConfigFromUi();
                Invalidate();
            }
        });

        // Row 4: Idle auto-off
        y += rowH;
        string idleLabel = _ctx.Config.IdleMinutes switch
        {
            15 => I18n.T("In 15 minutes ▾", "Через 15 минут ▾"),
            30 => I18n.T("In 30 minutes ▾", "Через 30 минут ▾"),
            60 => I18n.T("In 1 hour ▾", "Через 1 час ▾"),
            120 => I18n.T("In 2 hours ▾", "Через 2 часа ▾"),
            _ => I18n.T("Never ▾", "Никогда ▾"),
        };
        DrawSettingRow(g, x, y, w, rowH, ColOrange, "timer", I18n.T("Auto shutdown when idle", "Автовыключение при простое"), I18n.T("Stop the server and tunnel if there are no MCP calls", "Останавливать сервер и туннель, если нет вызовов MCP"), drawDivider: true);
        DrawPillButtonRight(g, x + w - 16f, y + 16f, idleLabel, ColPillIdle, ColTextPrimary, "gen_idle_menu", () =>
        {
            ShowPopupOptions(new (string Label, bool Checked, Action Pick)[]
            {
                (I18n.T("Never", "Никогда"), _ctx.Config.IdleMinutes == 0, () => { _ctx.Config.IdleMinutes = 0; _ctx.SaveConfigFromUi(); Invalidate(); }),
                (I18n.T("In 15 minutes", "Через 15 минут"), _ctx.Config.IdleMinutes == 15, () => { _ctx.Config.IdleMinutes = 15; _ctx.SaveConfigFromUi(); Invalidate(); }),
                (I18n.T("In 30 minutes", "Через 30 минут"), _ctx.Config.IdleMinutes == 30, () => { _ctx.Config.IdleMinutes = 30; _ctx.SaveConfigFromUi(); Invalidate(); }),
                (I18n.T("In 1 hour", "Через 1 час"), _ctx.Config.IdleMinutes == 60, () => { _ctx.Config.IdleMinutes = 60; _ctx.SaveConfigFromUi(); Invalidate(); }),
                (I18n.T("In 2 hours", "Через 2 часа"), _ctx.Config.IdleMinutes == 120, () => { _ctx.Config.IdleMinutes = 120; _ctx.SaveConfigFromUi(); Invalidate(); }),
            });
        });

        // Row 5: Autostart
        y += rowH;
        bool autostart = _ctx.GetAutostartFromUi();
        DrawSettingRow(g, x, y, w, rowH, ColGreen, "gear", I18n.T("Run with Windows", "Запускать вместе с Windows"), I18n.Format("Automatically add the {0} icon to the tray at logon", "Автоматически добавлять иконку {0} в трей при входе в систему", Theme.ProductName), drawDivider: false);
        DrawIosToggle(g, x + w - 62f, y + 20f, autostart, "gen_autostart", () =>
        {
            _ctx.SetAutostartFromUi(!autostart);
            Invalidate();
        });

        return y + rowH + 16f;
    }

    // ============================================================ SCREENSHOT ARCHIVE
    private float DrawScreenshotsContent(Graphics g, float x, float w, float y)
    {
        y = DrawGroupTitle(g, x, y, I18n.T("SCREENSHOT ARCHIVE", "АРХИВ СКРИНШОТОВ"));

        float rowH = RowH;
        var cardRect = new RectangleF(x, y, w, rowH * 3f);
        DrawCardSurface(g, cardRect);

        bool enabled = _ctx.Config.SaveScreenshots;
        string dir = string.IsNullOrWhiteSpace(_ctx.Config.ScreenshotsDir)
            ? ScreenshotArchiveConfig.Dir
            : _ctx.Config.ScreenshotsDir;

        DrawSettingRow(
            g, x, y, w - 80f, rowH,
            enabled ? ColGreen : Theme.TextTertiary,
            "image",
            I18n.T("Save approved screenshots", "Сохранять одобренные скриншоты"),
            I18n.T("Only approved shots, with a caption bar. Off by default.", "Только одобренные кадры, с полосой-подписью. По умолчанию выключено."),
            drawDivider: true);
        DrawIosToggle(g, x + w - 62f, y + 20f, enabled, "shot_save_toggle", () =>
        {
            _ctx.Config.SaveScreenshots = !enabled;
            _ctx.SaveConfigFromUi();
            Invalidate();
        });

        y += rowH;
        DrawSettingRow(
            g, x, y, w - 220f, rowH,
            ColBlue,
            "folder",
            I18n.T("Screenshots folder", "Папка для скриншотов"),
            dir,
            drawDivider: true);
        float rx = x + w - 16f;
        rx = DrawPillButtonRight(g, rx, y + 16f, I18n.T("Open", "Открыть"), ColBlue, Color.White, "shot_open", () =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                DesktopLauncher.OpenFolder(dir);
            }
            catch { }
        }) - 8f;
        DrawPillButtonRight(g, rx, y + 16f, I18n.T("Choose folder…", "Выбрать папку…"), ColPillIdle, ColTextPrimary, "shot_browse", () =>
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(dir) ? dir : _ctx.Config.ProjectDir,
                Description = I18n.T("Choose the folder for saved screenshots", "Выберите папку для сохранённых скриншотов")
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _ctx.Config.ScreenshotsDir = dlg.SelectedPath;
                _ctx.SaveConfigFromUi();
                Invalidate();
            }
        });

        y += rowH;
        DrawSettingRow(
            g, x, y, w, rowH,
            Theme.TextTertiary,
            "file",
            I18n.T("File name", "Имя файла"),
            I18n.T("date_time_token-label, e.g. 2026-10-25_20-45-12_Claude123.jpg. The model's description is burned into a caption bar below the photo.", "дата_время_метка токена, например 2026-10-25_20-45-12_Claude123.jpg. Описание модели вшивается полосой-подписью снизу кадра."),
            drawDivider: false);

        return y + rowH + 16f;
    }

    // ============================================================ CONFINEMENT CARD (shared by Access & Tools)
    private float DrawConfinementCard(Graphics g, float x, float w, float y, string idPrefix)
    {
        y = DrawGroupTitle(g, x, y, I18n.T("SINGLE-FOLDER CONFINEMENT", "ОГРАНИЧЕНИЕ ОДНОЙ ПАПКОЙ (SINGLE-FOLDER CONFINEMENT)"));

        float rowH = RowH;
        var cardRect = new RectangleF(x, y, w, rowH * 2f);
        DrawCardSurface(g, cardRect);

        bool confEnabled = _perms.Confinement?.Enabled == true;
        string confRoot = !string.IsNullOrWhiteSpace(_perms.Confinement?.Root)
            ? _perms.Confinement!.Root!
            : _ctx.Config.ProjectDir;

        DrawSettingRow(
            g,
            x,
            y,
            w - 80f,
            rowH,
            confEnabled ? ColGreen : Theme.TextTertiary,
            "folderlock",
            I18n.T("Restrict to files in one folder", "Ограничить файлами одной папки"),
            I18n.T("The terminal is disabled in this mode. The agent sees only files inside the chosen folder", "Терминал в этом режиме отключён. Агент видит только файлы внутри выбранной папки"),
            drawDivider: true);

        DrawIosToggle(g, x + w - 62f, y + 20f, confEnabled, idPrefix + "_conf_toggle", () =>
        { ToggleConfinement(); Invalidate(); });

        y += rowH;
        string rootSubtitle = string.IsNullOrWhiteSpace(confRoot) ? I18n.T("No folder selected", "Папка не выбрана") : confRoot;
        if (confEnabled)
            rootSubtitle += I18n.T("  ·  📁 single-folder mode active (run_command blocked)", "  ·  📁 активен режим одной папки (run_command заблокирован)");

        DrawSettingRow(
            g,
            x,
            y,
            w - 220f,
            rowH,
            ColBlue,
            "folder",
            I18n.T("Allowed folder", "Разрешённая папка"),
            rootSubtitle,
            drawDivider: false);

        float rx = x + w - 16f;
        rx = DrawPillButtonRight(g, rx, y + 16f, I18n.T("Open", "Открыть"), ColPillIdle, ColTextPrimary, idPrefix + "_conf_open", () =>
        {
            if (!string.IsNullOrWhiteSpace(confRoot))
                DesktopLauncher.OpenFolder(confRoot);
        }) - 8f;

        DrawPillButtonRight(g, rx, y + 16f, I18n.T("Choose folder…", "Выбрать папку…"), ColBlue, Color.White, idPrefix + "_conf_browse", () =>
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(confRoot) ? confRoot : _ctx.Config.ProjectDir,
                Description = I18n.T("Choose the single folder the agent will be allowed to access", "Выберите единственную папку, к которой будет разрешён доступ агенту")
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _perms.Confinement ??= new ConfinementConfig();
                _perms.Confinement.Root = dlg.SelectedPath;
                PermissionsStore.Save(_perms);
                _ctx.RefreshConfinementFromPermissions();
                Invalidate();
            }
        });

        return y + rowH + 16f;
    }

    // ============================================================ 2A. ACCESS (BEARER TOKENS AND OAUTH 2.1 CONNECTIONS)
    private float DrawAccessContent(Graphics g, float x, float w, float y)
    {
        _perms = PermissionsStore.Load();
        y = DrawConfinementCard(g, x, w, y, "acc");

        // ---------------- SECTION A: BEARER TOKENS (tokens.json) ----------------
        var tokReg = AccessStore.LoadTokens();
        y = DrawGroupTitle(g, x, y, I18n.Format("BEARER ACCESS TOKENS ({0} IN REGISTRY + LEGACY)", "ТОКЕНЫ ДОСТУПА BEARER ({0} В РЕЕСТРЕ + LEGACY)", tokReg.Tokens.Count));

        float headerRowH = RowH;
        float itemRowH = 62f;
        int totalTokenRows = 1 + tokReg.Tokens.Count; // +1 for legacy (config.env) row
        var tokCardRect = new RectangleF(x, y, w, headerRowH + totalTokenRows * itemRowH);
        DrawCardSurface(g, tokCardRect);

        DrawSettingRow(
            g,
            x,
            y,
            w - 200f,
            headerRowH,
            ColGreen,
            "key",
            I18n.T("Personal access tokens (tokens.json)", "Персональные токены доступа (tokens.json)"),
            I18n.T("Only SHA-256 hashes are stored (apw_…). The plain token is shown once at creation", "Хранятся только SHA-256 хеши (apw_…). Открытый токен показывается один раз при создании"),
            drawDivider: true);

        DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("+ Create token…", "+ Создать токен…"), ColBlue, Color.White, "tok_create", () =>
        {
            using var dlg = new CreateTokenDialog(_ctx.Config.ProjectDir);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                var (entry, plain) = AccessStore.CreateToken(
                    dlg.TokenLabel,
                    dlg.SelectedScope,
                    dlg.TtlDays,
                    dlg.MaxRequests,
                    dlg.RootFolder);
                Invalidate();
                using var once = new ShowTokenOnceDialog(I18n.T("New access token created", "Новый токен доступа создан"), entry, plain);
                once.ShowDialog(this);
            }
        });

        y += headerRowH;

        var rowTitleFont = UiFont(10.0f, true);
        var rowSubFont = UiFont(9.25f);
        var badgeFont = UiFont(9.0f, true);
        using var priBrush = new SolidBrush(ColTextPrimary);
        using var secBrush = new SolidBrush(ColTextSecondary);
        var noWrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        for (int i = 0; i < tokReg.Tokens.Count; i++)
        {
            var t = tokReg.Tokens[i];
            var (statusText, statusCol, _) = AccessStore.GetTokenStatus(t);
            string scopeShort = AccessStore.FormatScopeShortRu(t.Scope);
            string rootDesc = string.IsNullOrWhiteSpace(t.Root) ? I18n.T("no restriction", "без ограничения") : EventHumanizer.ShortenPath(t.Root);
            string expDesc = string.IsNullOrWhiteSpace(t.ExpiresAt)
                ? I18n.T("never expires", "бессрочно")
                : (DateTime.TryParse(t.ExpiresAt, out var edt) ? I18n.Format("until {0}", "до {0}", edt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")) : t.ExpiresAt);
            string budgetDesc = t.MaxRequests.HasValue ? $"{t.UsedRequests}/{t.MaxRequests.Value}" : $"{t.UsedRequests}/∞";
            string lastUsedDesc = string.IsNullOrWhiteSpace(t.LastUsedAt)
                ? I18n.T("not used", "не использовался")
                : (DateTime.TryParse(t.LastUsedAt, out var udt) ? udt.ToLocalTime().ToString("dd.MM HH:mm") : t.LastUsedAt);
            string srcTag = t.Source == "oauth" ? " · OAuth" : "";

            // Status dot
            using (var db = new SolidBrush(statusCol))
                g.FillEllipse(db, x + 20f, y + 18f, 9f, 9f);

            string titleLine = $"{t.Label}{srcTag}";
            g.DrawString(titleLine, rowTitleFont, priBrush, new RectangleF(x + 38f, y + 9f, w - 530f, 19f), noWrap);

            // Status pill next to title
            float pillX = x + 42f + Math.Min(w - 535f, g.MeasureString(titleLine, rowTitleFont).Width + 6f);
            DrawInlineTag(g, pillX, y + 10f, statusText, statusCol);

            string subLine = I18n.Format("{0} · Folder: {1} · Term: {2} · Requests: {3} · Last: {4}", "{0} · Папка: {1} · Срок: {2} · Запросы: {3} · Был: {4}", t.Id, rootDesc, expDesc, budgetDesc, lastUsedDesc);
            g.DrawString(subLine, rowSubFont, secBrush, new RectangleF(x + 38f, y + 33f, w - 455f, 18f), noWrap);

            string capturedId = t.Id;
            string capturedScope = t.Scope;
            float brx = x + w - 16f;

            brx = DrawPillButtonRight(g, brx, y + 16f, I18n.T("Delete", "Удалить"), Theme.WithAlpha(Theme.Danger, 40), ColRed, $"tok_del_{i}", () =>
            {
                AccessStore.DeleteToken(capturedId);
                Invalidate();
            }) - 6f;

            if (!t.Revoked)
            {
                brx = DrawPillButtonRight(g, brx, y + 16f, I18n.T("Revoke", "Отозвать"), ColPillIdle, ColOrange, $"tok_rev_{i}", () =>
                {
                    AccessStore.RevokeToken(capturedId);
                    Invalidate();
                }) - 6f;

                if (t.Source != "oauth")
                {
                    brx = DrawPillButtonRight(g, brx, y + 16f, I18n.T("Rotate", "Ротировать"), ColPillIdle, ColTextPrimary, $"tok_rot_{i}", () =>
                    {
                        var (newEntry, newPlain) = AccessStore.RotateToken(capturedId);
                        Invalidate();
                        if (newEntry != null && !string.IsNullOrEmpty(newPlain))
                        {
                            using var once = new ShowTokenOnceDialog(I18n.T("Token rotated (old one works for another 10 min)", "Токен ротирован (старый действует ещё 10 минут)"), newEntry, newPlain);
                            once.ShowDialog(this);
                        }
                    }) - 6f;
                }
            }
            else
            {
                brx = DrawPillButtonRight(g, brx, y + 16f, I18n.T("Enable", "Включить"), ColGreen, Color.Black, $"tok_en_{i}", () =>
                {
                    AccessStore.EnableToken(capturedId);
                    Invalidate();
                }) - 6f;
            }

            DrawPillButtonRight(g, brx, y + 16f, I18n.Format("Rights: {0} ▾", "Права: {0} ▾", scopeShort), ColPillIdle, ColTextPrimary, $"tok_scope_{i}", () =>
            {
                ShowPopupOptions(new (string Label, bool Checked, Action Pick)[]
                {
                    (I18n.T("Read-only (read)", "Только чтение (read)"), capturedScope == "read", () => { AccessStore.UpdateTokenScope(capturedId, "read"); Invalidate(); }),
                    (I18n.T("Read and write, no commands (readwrite)", "Чтение и запись без команд (readwrite)"), capturedScope == "readwrite", () => { AccessStore.UpdateTokenScope(capturedId, "readwrite"); Invalidate(); }),
                    (I18n.T("Full access, including terminal (full)", "Полный доступ, включая терминал (full)"), capturedScope == "full", () => { AccessStore.UpdateTokenScope(capturedId, "full"); Invalidate(); }),
                });
            });

            using (var divPen = new Pen(ColDivider, 1f))
                g.DrawLine(divPen, x + 38f, y + itemRowH, x + w - 16f, y + itemRowH);

            y += itemRowH;
        }

        // Inactive Legacy token row (config.env)
        using (var legDot = new SolidBrush(Theme.TextTertiary))
            g.FillEllipse(legDot, x + 20f, y + 18f, 9f, 9f);
        using (var legBrush = new SolidBrush(Theme.TextSecondary))
            g.DrawString(I18n.T("legacy (config.env) — full access", "legacy (config.env) — полный доступ"), rowTitleFont, legBrush, new RectangleF(x + 38f, y + 9f, w - 180f, 19f), noWrap);
        g.DrawString(I18n.T("Base token from config.env · no folder restriction · never expires · cannot be revoked from the UI", "Базовый токен из файла config.env · без ограничения папки · бессрочно · не отзывается из UI"), rowSubFont, secBrush, new RectangleF(x + 38f, y + 33f, w - 60f, 18f), noWrap);
        DrawInlineTag(g, x + w - 116f, y + 20f, I18n.T("Permanent", "Постоянный"), Theme.TextTertiary);

        y += itemRowH + 18f;

        // ---------------- SECTION B: OAUTH 2.1 CONNECTIONS (oauth.json) ----------------
        var oauthStore = AccessStore.LoadOAuth();
        y = DrawGroupTitle(g, x, y, I18n.Format("OAUTH 2.1 CONNECTIONS ({0})", "ПОДКЛЮЧЕНИЯ OAUTH 2.1 ({0})", oauthStore.Families.Count));

        int famRows = Math.Max(1, oauthStore.Families.Count);
        var oauthCardRect = new RectangleF(x, y, w, headerRowH + famRows * itemRowH);
        DrawCardSurface(g, oauthCardRect);

        DrawSettingRow(
            g,
            x,
            y,
            w - 230f,
            headerRowH,
            ColCyan,
            "globe",
            I18n.T("Connected OAuth clients (oauth.json)", "Подключённые клиенты OAuth (oauth.json)"),
            I18n.T("Disconnecting a session instantly revokes the refresh family and all access tokens issued to it", "Отключение сессии мгновенно отзывает семейство refresh и все выданные access-токены"),
            drawDivider: true);

        DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("OAuth card test", "Тест OAuth-карточки"), ColPillIdle, ColTextPrimary, "oauth_demo_card", async () =>
        {
            var demoConsent = new ApprovalRequestDto
            {
                Id = "demo_oauth_" + Guid.NewGuid().ToString("N")[..5],
                Kind = "oauth_consent",
                ClientName = "Claude Desktop / Web",
                ClientId = "client_demo",
                RedirectHost = "claude.ai",
                Scope = "readwrite",
                ScopeHuman = "Read and write files (no PowerShell commands)",
                Risk = "known",
                Warning = null,
                TimeoutSec = 120,
                CreatedAt = DateTime.UtcNow.ToString("o")
            };
            await _ctx.HandleApprovalRequestFromUiAsync(demoConsent, null, isDemo: true);
        });

        y += headerRowH;

        if (oauthStore.Families.Count == 0)
        {
            DrawSettingRow(
                g,
                x,
                y,
                w,
                itemRowH,
                Theme.TextTertiary,
                "globe",
                I18n.T("No active or past OAuth connections", "Нет активных или прошлых подключений OAuth"),
                I18n.T("When a service (e.g. ChatGPT or Claude) requests access via /oauth/authorize, it will appear here", "Когда сервис (например, ChatGPT или Claude) запросит доступ через /oauth/authorize, он появится здесь"),
                drawDivider: false);
            y += itemRowH;
        }
        else
        {
            for (int i = 0; i < oauthStore.Families.Count; i++)
            {
                var fam = oauthStore.Families[i];
                var client = oauthStore.Clients.FirstOrDefault(c => c.ClientId == fam.ClientId);
                string clientName = client != null && !string.IsNullOrWhiteSpace(client.ClientName)
                    ? client.ClientName
                    : fam.ClientId;

                var (fStatus, fCol, _) = AccessStore.GetFamilyStatus(fam);
                string scopeShort = AccessStore.FormatScopeShortRu(fam.Scope);
                string createdStr = DateTime.TryParse(fam.CreatedAt, out var cdt)
                    ? cdt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                    : fam.CreatedAt;
                string expStr = string.IsNullOrWhiteSpace(fam.ExpiresAt)
                    ? I18n.T("never expires", "бессрочно")
                    : (DateTime.TryParse(fam.ExpiresAt, out var fdt) ? I18n.Format("until {0}", "до {0}", fdt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")) : fam.ExpiresAt);

                using (var db = new SolidBrush(fCol))
                    g.FillEllipse(db, x + 20f, y + 18f, 9f, 9f);

                string fTitle = clientName;
                g.DrawString(fTitle, rowTitleFont, priBrush, new RectangleF(x + 38f, y + 9f, w - 470f, 19f), noWrap);

                float fpX = x + 42f + Math.Min(w - 475f, g.MeasureString(fTitle, rowTitleFont).Width + 6f);
                DrawInlineTag(g, fpX, y + 10f, fStatus, fCol);

                string fSub = I18n.Format("{0} · {1} · Until: {2}", "{0} · {1} · До: {2}", fam.Id, AccessStore.FormatScope(fam.Scope), expStr);
                g.DrawString(fSub, rowSubFont, secBrush, new RectangleF(x + 38f, y + 33f, w - 395f, 18f), noWrap);

                string capturedFamId = fam.Id;
                string capturedFamScope = fam.Scope;
                float frx = x + w - 16f;

                frx = DrawPillButtonRight(g, frx, y + 16f, I18n.T("Delete", "Удалить"), Theme.WithAlpha(Theme.Danger, 40), ColRed, $"fam_del_{i}", () =>
                {
                    AccessStore.DeleteOAuthFamily(capturedFamId);
                    Invalidate();
                }) - 6f;

                if (!fam.Revoked)
                {
                    frx = DrawPillButtonRight(g, frx, y + 16f, I18n.T("Disconnect", "Отключить"), ColPillIdle, ColOrange, $"fam_disc_{i}", () =>
                    {
                        AccessStore.DisconnectOAuthFamily(capturedFamId);
                        Invalidate();
                    }) - 6f;
                }
                else
                {
                    frx = DrawPillButtonRight(g, frx, y + 16f, I18n.T("Enable", "Включить"), ColGreen, Color.Black, $"fam_en_{i}", () =>
                    {
                        AccessStore.EnableOAuthFamily(capturedFamId);
                        Invalidate();
                    }) - 6f;
                }

                DrawPillButtonRight(g, frx, y + 16f, I18n.Format("Rights: {0} ▾", "Права: {0} ▾", scopeShort), ColPillIdle, ColTextPrimary, $"fam_scope_{i}", () =>
                {
                    ShowPopupOptions(new (string Label, bool Checked, Action Pick)[]
                    {
                        (I18n.T("Read-only (read)", "Только чтение (read)"), capturedFamScope == "read", () => { AccessStore.UpdateOAuthFamilyScope(capturedFamId, "read"); Invalidate(); }),
                        (I18n.T("Read and write, no commands (readwrite)", "Чтение и запись без команд (readwrite)"), capturedFamScope == "readwrite", () => { AccessStore.UpdateOAuthFamilyScope(capturedFamId, "readwrite"); Invalidate(); }),
                        (I18n.T("Full access, including terminal (full)", "Полный доступ, включая терминал (full)"), capturedFamScope == "full", () => { AccessStore.UpdateOAuthFamilyScope(capturedFamId, "full"); Invalidate(); }),
                    });
                });

                if (i < oauthStore.Families.Count - 1)
                {
                    using var divPen = new Pen(ColDivider, 1f);
                    g.DrawLine(divPen, x + 38f, y + itemRowH, x + w - 16f, y + itemRowH);
                }
                y += itemRowH;
            }
        }

        return y + 16f;
    }

    private static void DrawInlineTag(Graphics g, float x, float y, string text, Color accent)
    {
        var f = UiFont(9.0f, true);
        var sz = g.MeasureString(text, f);
        var r = new RectangleF(x, y, sz.Width + 12f, 18f);
        using var path = CreateRoundRect(r, 9f);
        using var bg = new SolidBrush(Color.FromArgb(40, accent));
        using var fg = new SolidBrush(accent);
        g.FillPath(bg, path);
        var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, f, fg, r, center);
    }

    // ============================================================ 2. TOOLS AND PERMISSIONS
    private float DrawToolsContent(Graphics g, float x, float w, float y)
    {
        int totalTools = PermissionsStore.AllTools.Count;
        int allowedTools = PermissionsStore.AllTools.Count(t => PermissionsStore.GetToolMode(_perms, t.Name) != "deny");
        int askTools = PermissionsStore.AllTools.Count(t => PermissionsStore.GetToolMode(_perms, t.Name) == "ask");
        bool rfTop = _perms.RedFlags?.Enabled != false && _perms.RedFlags?.ElevateToAsk != false;
        string insightPreset = PermissionsStore.DetectActivePreset(_perms) switch
        {
            "cautious" => I18n.T("Cautious", "Осторожный"),
            "balanced" => I18n.T("Balanced", "Сбалансированный"),
            "trusting" => I18n.T("Trusting", "Доверчивый"),
            _ => I18n.T("Custom", "Свой набор"),
        };
        y = DrawInsightRow(g, x, w, y,
            new InsightCard(IconKind.ShieldCheck, rfTop ? ColGreen : ColOrange, I18n.T("Security", "Безопасность"), I18n.Format("{0} with approval", "{0} с подтверждением", askTools), rfTop ? I18n.T("Red-Flag filter is on", "Фильтр Red-Flag включён") : I18n.T("Red-Flag filter is off", "Фильтр Red-Flag выключен")),
            new InsightCard(IconKind.User, ColBlue, I18n.T("Available to agents", "Агентам доступно"), I18n.Format("{0} of {1} tools", "{0} из {1} инструментов", allowedTools, totalTools), I18n.Format("Profile: {0}", "Профиль: {0}", insightPreset)),
            new InsightCard(IconKind.Lightbulb, ColOrange, I18n.T("Tip", "Совет"), I18n.T("Grant only the minimum permissions needed — it keeps the computer safer.", "Используйте минимально необходимые права для большей безопасности."), null, Tip: true));
        y = DrawConfinementCard(g, x, w, y, "tools");
        // Security profile: radio option cards (reference «Режим доступа») + info banner
        string activePreset = PermissionsStore.DetectActivePreset(_perms);
        string presetDesc = activePreset switch
        {
            "cautious" => I18n.T("“Cautious” profile: reading — at once; file writes, commands and journal_entry — with your approval.", "Профиль «Осторожный»: чтение — сразу; запись файлов, команды и journal_entry — с подтверждением."),
            "trusting" => I18n.T("“Trusting” profile: reading and editing files — at once; approval is asked only for run_command.", "Профиль «Доверчивый»: чтение и изменение файлов — сразу; подтверждение требуется только для run_command."),
            "balanced" => I18n.T("“Balanced” profile: reading and journal — at once; file writes and commands — with approval (60 s).", "Профиль «Сбалансированный»: чтение и журнал — сразу; запись файлов и команды — с подтверждением (60 с)."),
            _ => I18n.T("Custom permission set: individual tool modes were changed manually.", "Свой набор прав: режимы отдельных инструментов изменены вручную."),
        };
        var options = new (string Key, string Title, string Desc, Color Tint)[]
        {
            ("cautious", I18n.T("Cautious", "Осторожный"), I18n.T("Reads at once, the rest with approval", "Чтение — сразу, остальное — с подтверждением"), ColOrange),
            ("balanced", I18n.T("Balanced", "Сбалансированный"), I18n.T("Writes and commands with approval", "Запись и команды — с подтверждением"), ColBlue),
            ("trusting", I18n.T("Trusting", "Доверчивый"), I18n.T("Approval only for run_command", "Подтверждение только для run_command"), ColGreen),
        };
        float presetTop = y;
        float optY = y + 70f, optH = 76f, optGap = 12f;
        float optW = (w - 32f - optGap * 2f) / 3f;
        float bannerY = optY + optH + 14f;
        float bannerTextH = Theme.MeasureText(g, presetDesc, PanelTypography.Get(12.5f), w - 32f - 54f).Height;
        float bannerH = Math.Max(40f, bannerTextH + 20f);
        var presetCard = new RectangleF(x, y, w, bannerY - y + bannerH + 18f);
        DrawCardSurface(g, presetCard);
        Theme.DrawText(g, I18n.T("Security profile", "Профиль безопасности"), PanelTypography.Get(14f, true), ColTextPrimary, new RectangleF(x + 18f, y + 16f, w - 36f, 22f));
        Theme.DrawText(g, I18n.T("Defines which actions are available to agents.", "Определяет, какие действия доступны агентам."), PanelTypography.Get(12.5f), ColTextSecondary, new RectangleF(x + 18f, y + 39f, w - 36f, 20f));
        for (int i = 0; i < options.Length; i++)
        {
            var (key, otitle, odesc, tint) = options[i];
            var r = new RectangleF(x + 16f + i * (optW + optGap), optY, optW, optH);
            bool sel = activePreset == key;
            bool hov = r.Contains(_mousePos);
            if (sel)
            {
                Theme.DrawGlow(g, r, 11f, Theme.WithAlpha(ColBlue, 40), 6);
                Theme.FillRound(g, r, 11f, Theme.Composite(Theme.WithAlpha(ColBlue, 34), ColCardBg));
                Theme.StrokeRound(g, r, 11f, Theme.WithAlpha(ColBlue, 170), 1.3f);
            }
            else
            {
                Theme.FillRound(g, r, 11f, Color.FromArgb(0x1C, 0x21, 0x2B));
                Theme.StrokeRound(g, r, 11f, hov ? Color.FromArgb(46, 255, 255, 255) : Theme.BorderStrong);
            }
            var dot = new RectangleF(r.X + 16f, r.Y + 15f, 16f, 16f);
            var oldSm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (sel)
            {
                using (var b = new SolidBrush(ColBlue)) g.FillEllipse(b, dot);
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, dot.X + 5f, dot.Y + 5f, 6f, 6f);
            }
            else
            {
                using var pen = new Pen(Color.FromArgb(72, 255, 255, 255), 1.5f);
                g.DrawEllipse(pen, RectangleF.Inflate(dot, -0.75f, -0.75f));
            }
            g.SmoothingMode = oldSm;
            float otx = dot.Right + 12f, otw = r.Right - 12f - otx;
            Theme.DrawText(g, otitle, FitFont(g, otitle, 13.5f, 12f, otw, true), ColTextPrimary, new RectangleF(otx, r.Y + 12f, otw, 20f));
            Theme.DrawText(g, odesc, PanelTypography.Get(11.5f), ColTextSecondary, new RectangleF(otx, r.Y + 33f, otw, 34f), vertical: StringAlignment.Near, ellipsis: true, wrap: true);
            string picked = key;
            _hitZones.Add(new HitZone(r, "preset_" + key, () => { PermissionsStore.ApplyPreset(_perms, picked); Invalidate(); }, otitle + "\n" + odesc));
        }
        DrawInfoBanner(g, x + 16f, w - 32f, bannerY, presetDesc);
        y = presetCard.Bottom + 22f;

        var categories = new (string CatKey, string CatTitle, Color BadgeCol, string Glyph)[]
        {
            ("Execution", I18n.T("Execution", "Выполнение"), Theme.Violet, "terminal"),
            ("File writes", I18n.T("File writes", "Запись файлов"), ColBlue, "edit"),
            ("Read", I18n.T("Read", "Чтение"), ColGreen, "eye"),
            ("Journal", I18n.T("Journal", "Журнал"), ColOrange, "list"),
        };

        foreach (var (catKey, catTitle, badgeCol, glyph) in categories)
        {
            var tools = PermissionsStore.AllTools.Where(t => t.CategoryEn == catKey).ToList();
            if (tools.Count == 0) continue;

            y = DrawGroupTitle(g, x, y, catTitle.ToUpperInvariant());

            int extraRows = catKey == "Execution" ? 1 : 0; // Red-flag toggle row inside Execution
            float rowH = RowH;
            var groupRect = new RectangleF(x, y, w, (tools.Count + extraRows) * rowH);
            DrawCardSurface(g, groupRect);

            for (int i = 0; i < tools.Count; i++)
            {
                var tool = tools[i];
                bool hasNext = (i < tools.Count - 1) || extraRows > 0;
                DrawSettingRow(g, x, y, w - 310f, rowH, badgeCol, glyph, $"{tool.Title}  ({tool.Name})", tool.Description, drawDivider: hasNext);

                string curMode = PermissionsStore.GetToolMode(_perms, tool.Name);
                DrawSegmentedBar(
                    g,
                    new RectangleF(x + w - 310f, y + 16f, 294f, 32f),
                    new (string Key, string Title, Color ActiveCol)[]
                    {
                        ("allow", I18n.T("Allowed", "Разрешён"), ColGreen),
                        ("ask", I18n.T("With approval", "С подтвержд."), ColOrange),
                        ("deny", I18n.T("Denied", "Отключён"), ColRed),
                    },
                    curMode,
                    "tool_" + tool.Name,
                    pickedMode =>
                    {
                        if (tool.Name == "run_command" && pickedMode == "allow" && curMode != "allow")
                        {
                            var res = MessageBox.Show(
                                this,
                                I18n.T("Warning: in “Allowed” mode the agent will be able to run any PowerShell commands without your approval.\n\nIt is recommended to keep “With approval” and issue temporary grants for 10–30 minutes from the card.\n\nAllow run_command without asking?", "Внимание: в режиме «Разрешено» агент сможет выполнять любые команды PowerShell без вашего подтверждения.\n\nРекомендуется оставить «Спрашивать» и выдавать временные разрешения на 10–30 минут с карточки.\n\nРазрешить run_command без спроса?"),
                                I18n.T("Confirmation — run_command", "Подтверждение — run_command"),
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning,
                                MessageBoxDefaultButton.Button2);
                            if (res != DialogResult.Yes) return;
                        }
                        PermissionsStore.SetToolMode(_perms, tool.Name, pickedMode);
                        Invalidate();
                    });

                y += rowH;
            }

            if (catKey == "Execution")
            {
                bool rfOn = _perms.RedFlags?.Enabled != false && _perms.RedFlags?.ElevateToAsk != false;
                DrawSettingRow(
                    g,
                    x,
                    y,
                    w - 80f,
                    rowH,
                    ColRed,
                    "warning",
                    I18n.T("Dangerous command filter (Red-Flag)", "Фильтр опасных команд (Red-Flag)"),
                    I18n.T("Always ask for approval for destructive commands (even if run_command is allowed)", "Всегда спрашивать подтверждение для деструктивных команд (даже если run_command разрешён)"),
                    drawDivider: false);

                DrawIosToggle(g, x + w - 62f, y + 20f, rfOn, "toggle_redflags", () =>
                { ToggleRedFlags(); Invalidate(); });
                y += rowH;
            }

            y += 14f;
        }

        return y;
    }

    // ============================================================ 3. APPROVALS
    private float DrawApprovalsContent(Graphics g, float x, float w, float y)
    {
        y = DrawInsightRow(g, x, w, y,
            new InsightCard(IconKind.Clock, ColCyan, I18n.T("Response timeout", "Таймаут ответа"), I18n.Format("{0} seconds", "{0} секунд", _perms.ApprovalTimeoutSec), I18n.T("Then the card closes", "Затем карточка закроется")),
            new InsightCard(IconKind.ShieldCheck, ColBlue, I18n.T("Temporary grants", "Временные разрешения"), _activeGrants.Count.ToString(), I18n.Format("Default duration: {0} min", "По умолчанию: {0} мин", _ctx.Config.DefaultGrantMinutes)),
            new InsightCard(IconKind.Lightbulb, ColOrange, I18n.T("Tip", "Совет"), I18n.T("You can set different rules for individual tools in “Tools and permissions”.", "Вы можете настроить разные правила для отдельных инструментов в разделе «Инструменты и права»."), null, Tip: true, HitId: "apr_tip_tools", OnClick: () => NavigateToSection("tools")));
        y = DrawGroupTitle(g, x, y, I18n.T("APPROVAL CARD, SOUND AND 60-SECOND TIMER", "КАРТОЧКА ПОДТВЕРЖДЕНИЯ, ЗВУК И ТАЙМЕР 60 СЕКУНД"));

        float rowH = RowH;
        var cardRect = new RectangleF(x, y, w, rowH * 5f);
        DrawCardSurface(g, cardRect);

        // Row 1: Timeout (60s default)
        DrawSettingRow(g, x, y, w - 200f, rowH, ColOrange, "timer", I18n.T("Wait timer in the card corner", "Таймер ожидания в углу плашки"), I18n.T("When time runs out, the card closes automatically (Dismiss)", "По истечении времени карточка закрывается автоматически (Dismiss)"), drawDivider: true);
        string tLabel = I18n.Format("{0} seconds ▾", "{0} секунд ▾", _perms.ApprovalTimeoutSec);
        DrawPillButtonRight(g, x + w - 16f, y + 16f, tLabel, ColPillIdle, ColTextPrimary, "apr_timeout", () =>
        {
            ShowPopupOptions(new (string Label, bool Checked, Action Pick)[]
            {
                (I18n.T("30 seconds", "30 секунд"), _perms.ApprovalTimeoutSec == 30, () => { _perms.ApprovalTimeoutSec = 30; PermissionsStore.Save(_perms); Invalidate(); }),
                (I18n.T("60 seconds (MCP timeout standard)", "60 секунд (стандарт таймаута MCP)"), _perms.ApprovalTimeoutSec == 60, () => { _perms.ApprovalTimeoutSec = 60; PermissionsStore.Save(_perms); Invalidate(); }),
                (I18n.T("120 seconds", "120 секунд"), _perms.ApprovalTimeoutSec == 120, () => { _perms.ApprovalTimeoutSec = 120; PermissionsStore.Save(_perms); Invalidate(); }),
                (I18n.T("300 seconds (5 minutes)", "300 секунд (5 минут)"), _perms.ApprovalTimeoutSec == 300, () => { _perms.ApprovalTimeoutSec = 300; PermissionsStore.Save(_perms); Invalidate(); }),
            });
        });

        // Row 2: Default grant duration
        y += rowH;
        DrawSettingRow(g, x, y, w - 200f, rowH, ColBlue, "timer", I18n.T("Default “Allow for …” button", "Кнопка «Разрешить на …» по умолчанию"), I18n.T("Duration of the temporary grant when pressing the main part of the button", "Длительность временного разрешения при нажатии основной части кнопки"), drawDivider: true);
        string dLabel = _ctx.Config.DefaultGrantMinutes >= 60
            ? I18n.Format("For {0} h ▾", "На {0} ч ▾", _ctx.Config.DefaultGrantMinutes / 60)
            : I18n.Format("For {0} minutes ▾", "На {0} минут ▾", _ctx.Config.DefaultGrantMinutes);
        DrawPillButtonRight(g, x + w - 16f, y + 16f, dLabel, ColPillIdle, ColTextPrimary, "apr_dur", () =>
        {
            ShowPopupOptions(new (string Label, bool Checked, Action Pick)[]
            {
                (I18n.T("For 10 minutes", "На 10 минут"), _ctx.Config.DefaultGrantMinutes == 10, () => { _ctx.Config.DefaultGrantMinutes = 10; _ctx.SaveConfigFromUi(); Invalidate(); }),
                (I18n.T("For 30 minutes", "На 30 минут"), _ctx.Config.DefaultGrantMinutes == 30, () => { _ctx.Config.DefaultGrantMinutes = 30; _ctx.SaveConfigFromUi(); Invalidate(); }),
                (I18n.T("For 1 hour (60 min)", "На 1 час (60 мин)"), _ctx.Config.DefaultGrantMinutes == 60, () => { _ctx.Config.DefaultGrantMinutes = 60; _ctx.SaveConfigFromUi(); Invalidate(); }),
                (I18n.T("For 3 hours (180 min)", "На 3 часа (180 мин)"), _ctx.Config.DefaultGrantMinutes == 180, () => { _ctx.Config.DefaultGrantMinutes = 180; _ctx.SaveConfigFromUi(); Invalidate(); }),
            });
        });

        // Row 3: Default grant scope
        y += rowH;
        DrawSettingRow(g, x, y, w - 300f, rowH, ColPurple, "shieldcheck", I18n.T("Temporary grant scope", "Область временного разрешения"), I18n.T("Grant permission for the whole tool or only for a specific command/file", "Разрешать инструмент целиком или только конкретную команду/файл"), drawDivider: true);
        DrawSegmentedBar(
            g,
            new RectangleF(x + w - 296f, y + 16f, 280f, 32f),
            new (string Key, string Title, Color ActiveCol)[]
            {
                ("tool", I18n.T("Whole tool", "Весь инструмент"), ColBlue),
                ("exact", I18n.T("Exact call", "Точный вызов"), ColPurple),
            },
            _ctx.Config.DefaultGrantScope == "exact" ? "exact" : "tool",
            "apr_scope",
            picked =>
            {
                _ctx.Config.DefaultGrantScope = picked;
                _ctx.SaveConfigFromUi();
                Invalidate();
            });

        // Row 4: Sound only on approval requests (REQ-4)
        y += rowH;
        string curSound = string.IsNullOrEmpty(_ctx.Config.ApprovalSound) ? "chime" : _ctx.Config.ApprovalSound.ToLowerInvariant();
        DrawSettingRow(g, x, y, w - 397f, rowH, ColRed, "bell", I18n.T("Approval request sound", "Звук запроса подтверждения"), I18n.T("Regular notifications are always silent — sound plays only on an approval request", "Обычные уведомления всегда без звука — звук играет только при запросе подтверждения"), drawDivider: true);
        DrawSegmentedBar(
            g,
            new RectangleF(x + w - 388f, y + 16f, 372f, 32f),
            new (string Key, string Title, Color ActiveCol)[]
            {
                ("chime", I18n.T("Chord", "Аккорд"), ColBlue),
                ("ping", I18n.T("Ping", "Пинг"), ColGreen),
                ("bell", I18n.T("Bell", "Колокольчик"), ColPurple),
                ("none", I18n.T("No sound", "Без звука"), Theme.TextTertiary),
            },
            curSound,
            "apr_sound",
            picked =>
            {
                _ctx.Config.ApprovalSound = picked;
                _ctx.SaveConfigFromUi();
                ApprovalSoundPlayer.Play(picked);
                Invalidate();
            });

        // Row 5: Live test button
        y += rowH;
        DrawSettingRow(g, x, y, w - 220f, rowH, ColGreen, "bell", I18n.T("Test the approval card", "Проверить карточку подтверждения"), I18n.T("Show an interactive card with a 60-second timer in the corner", "Показать интерактивную карточку с 60-секундным таймером в углу"), drawDivider: false);
        DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("Show card (60 s)", "Показать карточку (60 с)"), ColBlue, Color.White, "apr_test_card", async () =>
        {
            var req = new ApprovalRequestDto
            {
                Id = "test_apr_" + Guid.NewGuid().ToString("N")[..4],
                Tool = "run_command",
                Note = "Check the list of running PowerShell processes (card test)",
                Summary = new ApprovalSummaryDto
                {
                    Title = "PowerShell command",
                    Target = "Get-Process powershell, pwsh -ErrorAction SilentlyContinue",
                    Detail = $"cwd: {_ctx.Config.ProjectDir}",
                },
                TimeoutSec = _perms.ApprovalTimeoutSec > 0 ? _perms.ApprovalTimeoutSec : 60,
                CreatedAt = DateTime.UtcNow.ToString("o"),
            };
            await _ctx.HandleApprovalRequestFromUiAsync(req, null, isDemo: true);
        });

        y += rowH + 18f;
        y = DrawGroupTitle(g, x, y, I18n.Format("ACTIVE TEMPORARY GRANTS ({0})", "АКТИВНЫЕ ВРЕМЕННЫЕ РАЗРЕШЕНИЯ ({0})", _activeGrants.Count));

        int grantRows = Math.Max(1, _activeGrants.Count);
        var grantsCard = new RectangleF(x, y, w, (grantRows + (_activeGrants.Count > 0 ? 1 : 0)) * rowH);
        DrawCardSurface(g, grantsCard);

        if (_activeGrants.Count == 0)
        {
            DrawSettingRow(g, x, y, w, rowH, Theme.TextTertiary, "timer", I18n.T("No active temporary grants", "Нет активных временных разрешений"), I18n.T("When you press “Allow for 10 min” on a card, the grant will appear here", "При нажатии «Разрешить на 10 мин» в карточке разрешение появится здесь"), drawDivider: false);
            y += rowH;
        }
        else
        {
            DrawSettingRow(g, x, y, w - 160f, rowH, ColRed, "shield", I18n.T("Managing issued grants", "Управление выданными разрешениями"), I18n.T("Grants are stored only in server memory and reset on shutdown", "Разрешения хранятся только в памяти сервера и сбрасываются при выключении"), drawDivider: true);
            DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("Revoke all", "Отозвать все"), ColRed, Color.White, "grant_rev_all", async () =>
            {
                await _ctx.RevokeGrantAsync(null, all: true);
                await RefreshGrantsAsync();
            });
            y += rowH;

            for (int i = 0; i < _activeGrants.Count; i++)
            {
                var gr = _activeGrants[i];
                int m = gr.RemainingSec / 60, s = gr.RemainingSec % 60;
                string scopeTitle = gr.Scope == "exact" ? I18n.Format("Exact call: {0}", "Точный вызов: {0}", gr.Target) : I18n.T("Whole tool allowed", "Разрешён весь инструмент");
                DrawSettingRow(g, x, y, w - 140f, rowH, ColGreen, "timer", I18n.Format("{0}  ·  left {1:00}:{2:00}", "{0}  ·  осталось {1:00}:{2:00}", gr.Tool, m, s), scopeTitle, drawDivider: i < _activeGrants.Count - 1);
                string capturedKey = gr.Key;
                DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("Revoke", "Отозвать"), ColPillIdle, ColRed, "grant_rev_" + i, async () =>
                {
                    await _ctx.RevokeGrantAsync(capturedKey, all: false);
                    await RefreshGrantsAsync();
                });
                y += rowH;
            }
        }

        return y + 16f;
    }

    // ============================================================ 4. NOTIFICATIONS
    private float DrawNotificationsContent(Graphics g, float x, float w, float y)
    {
        y = DrawGroupTitle(g, x, y, I18n.T("CARD STYLE (INFORMATION AND APPROVALS)", "СТИЛЬ КАРТОЧЕК (ИНФОРМАЦИОННЫЕ И ПОДТВЕРЖДЕНИЯ)"));

        var styleCard = new RectangleF(x, y, w, 198f);
        DrawCardSurface(g, styleCard);

        DrawSettingRow(g, x, y, w - 190f, 56f, ColPurple, "sliders", I18n.T("Notification card style", "Стиль карточек уведомлений"), I18n.T("Instantly changes the theme of info toasts and approval cards", "Мгновенно меняет тему информационных тостов и карточек подтверждения"), drawDivider: true);
        DrawPillButtonRight(g, x + w - 16f, y + 12f, I18n.T("Show example", "Показать пример"), ColBlue, Color.White, "notif_show_demo", () =>
        {
            _ctx.ShowDemoCardsFromUi();
        });

        string curStyle = string.IsNullOrEmpty(_ctx.Config.CardStyle) ? "glass" : _ctx.Config.CardStyle.ToLowerInvariant();
        var styles = new (string Key, string Title, string Sub)[]
        {
            ("glass", I18n.T("Glass", "Стекло"), I18n.T("Dark with a colored stripe", "Тёмная с цветной полосой")),
            ("light", I18n.T("Apple Light", "Светлая Apple"), I18n.T("Light and airy", "Светлая и воздушная")),
            ("compact", I18n.T("Compact", "Компактная"), I18n.T("Narrow, single line", "Узкая в одну строку")),
            ("neon", I18n.T("Neon", "Неон"), I18n.T("Black with a lime-neon frame", "Чёрная с лайм-неоновой рамкой")),
        };

        float tileGap = 12f;
        float tileW = (w - 32f - tileGap * 3f) / 4f;
        float tileH = 118f;
        float tileY = y + 68f;

        for (int i = 0; i < styles.Length; i++)
        {
            var st = styles[i];
            float tx = x + 16f + i * (tileW + tileGap);
            var tileRect = new RectangleF(tx, tileY, tileW, tileH);
            bool isSel = curStyle == st.Key;
            bool isHov = tileRect.Contains(_mousePos);

            using (var tPath = CreateRoundRect(tileRect, 10f))
            {
                using var bgBrush = new SolidBrush(isSel
                    ? Theme.AccentSubtle
                    : (isHov ? Theme.SurfaceHover : Theme.SurfaceRaised));
                g.FillPath(bgBrush, tPath);

                using var borderPen = new Pen(isSel ? ColBlue : Theme.Border, isSel ? 1.6f : 1f);
                g.DrawPath(borderPen, tPath);
            }

            // Mini visual card preview inside tile
            var miniRect = new RectangleF(tx + 10f, tileY + 10f, tileW - 20f, st.Key == "compact" ? 24f : 38f);
            if (st.Key == "compact") miniRect.Y += 7f;
            using (var mPath = CreateRoundRect(miniRect, st.Key == "compact" ? 12f : 8f))
            {
                var stPal = Theme.GetCardPalette(st.Key);
                Color mBg = stPal.Surface;
                using var mBrush = new SolidBrush(mBg);
                g.FillPath(mBrush, mPath);

                Color mRim = st.Key == "neon" ? Theme.WithAlpha(stPal.Accent, 220) : stPal.Border;
                using var mPen = new Pen(mRim, st.Key == "neon" ? 1.3f : 1f);
                g.DrawPath(mPen, mPath);
            }

            if (st.Key == "glass")
            {
                using var barB = new SolidBrush(ColBlue);
                g.FillRectangle(barB, miniRect.X + 3f, miniRect.Y + 6f, 2.5f, miniRect.Height - 12f);
            }

            // Mini orb + lines inside preview
            float dotSz = st.Key == "compact" ? 12f : 16f;
            var dotRect = new RectangleF(miniRect.X + 8f, miniRect.Y + (miniRect.Height - dotSz) / 2f, dotSz, dotSz);
            var stPal2 = Theme.GetCardPalette(st.Key);
            Color dotCol = st.Key == "glass" ? ColGreen : stPal2.Accent;
            using (var dBrush = new SolidBrush(dotCol))
                g.FillEllipse(dBrush, dotRect);

            Color line1Col = stPal2.TextPrimary;
            Color line2Col = st.Key == "neon" ? stPal2.Accent : stPal2.TextSecondary;
            using (var l1Pen = new Pen(line1Col, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float ly = st.Key == "compact" ? miniRect.Y + miniRect.Height / 2f : miniRect.Y + 13f;
                g.DrawLine(l1Pen, dotRect.Right + 6f, ly, miniRect.Right - 22f, ly);
            }
            if (st.Key != "compact")
            {
                using var l2Pen = new Pen(line2Col, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(l2Pen, dotRect.Right + 6f, miniRect.Y + 24f, miniRect.Right - 36f, miniRect.Y + 24f);
            }

            // Tile label & subtitle
            var tFont = UiFont(9.5f, true);
            var sFont = UiFont(9.0f);
            using var tBrush = new SolidBrush(ColTextPrimary);
            using var sBrush = new SolidBrush(ColTextSecondary);
            g.DrawString(st.Title, tFont, tBrush, new PointF(tx + 10f, tileY + 56f));
            Theme.DrawText(g, st.Sub, sFont, ColTextSecondary, new RectangleF(tx + 10, tileY + 75, tileW - 20, 38), vertical: StringAlignment.Near, ellipsis: false, wrap: true);

            if (isSel)
            {
                using var ckBrush = new SolidBrush(ColBlue);
                g.FillEllipse(ckBrush, tileRect.Right - 22f, tileY + 56f, 14f, 14f);
                using var ckPen = new Pen(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(ckPen, new[]
                {
                    new PointF(tileRect.Right - 18.5f, tileY + 63f),
                    new PointF(tileRect.Right - 16f, tileY + 65.5f),
                    new PointF(tileRect.Right - 11.5f, tileY + 60.5f),
                });
            }

            string capturedStyle = st.Key;
            _hitZones.Add(new HitZone(tileRect, "style_" + st.Key, () =>
            {
                _ctx.ApplyCardStyle(capturedStyle);
                Invalidate();
            }));
        }

        y += 216f;
        y = DrawGroupTitle(g, x, y, I18n.T("TRAY BEHAVIOR AND INDICATION", "ПОВЕДЕНИЕ И ИНДИКАЦИЯ В ТРЕЕ"));

        float rowH = RowH;
        var card = new RectangleF(x, y, w, rowH * 2f);
        DrawCardSurface(g, card);

        bool notifOn = _ctx.Config.NotificationsEnabled;
        DrawSettingRow(g, x, y, w - 80f, rowH, ColRed, "bell", I18n.T("Action pop-up cards", "Всплывающие карточки действий"), I18n.T("Show cards on MCP tool calls (always silent)", "Показывать карточки при вызовах инструментов MCP (всегда без звука)"), drawDivider: true);
        DrawIosToggle(g, x + w - 62f, y + 20f, notifOn, "notif_toggle", () =>
        {
            _ctx.SetNotificationsEnabledFromUi(!notifOn);
            Invalidate();
        });

        y += rowH;
        bool blinkOn = _ctx.Config.BlinkIconEnabled;
        DrawSettingRow(g, x, y, w - 80f, rowH, ColOrange, "timer", I18n.T("Blink the tray icon", "Моргать иконкой в трее"), I18n.T("Highlight the icon while commands run and approval is awaited", "Подсвечивать иконку при выполнении команд и ожидании подтверждения"), drawDivider: false);
        DrawIosToggle(g, x + w - 62f, y + 20f, blinkOn, "blink_toggle", () =>
        {
            _ctx.Config.BlinkIconEnabled = !blinkOn;
            _ctx.SaveConfigFromUi();
            Invalidate();
        });

        return y + rowH + 16f;
    }

    // ============================================================ 5. CONNECTION
    // ============================================================ 6. JOURNAL AND AUDIT
    private float DrawAuditContent(Graphics g, float x, float w, float y)
    {
        // Top Filter + Folder Buttons Bar: stack actions in compact windows.
        bool compactToolbar = w < 800f;
        float actionsY = y + (compactToolbar ? 44f : 1f);
        DrawSegmentedBar(
            g,
            new RectangleF(x, y, 400f, 32f),
            new (string Key, string Title, Color ActiveCol)[]
            {
                ("all", I18n.T("All entries", "Все записи"), ColBlue),
                ("errors", I18n.T("Denials and errors", "Отказы и ошибки"), ColRed),
                ("approvals", I18n.T("Approvals", "Подтверждения"), ColOrange),
            },
            _auditFilter,
            "audit_flt",
            picked =>
            {
                _auditFilter = picked;
                LoadAuditItems();
                Invalidate();
            });

        float rx = x + w;
        rx = DrawPillButtonRight(g, rx, actionsY, I18n.T("Open logs folder", "Открыть папку логов"), ColGreen, Color.Black, "aud_open_logs", () => _ctx.OpenLogsFolderFromUi()) - 8f;
        DrawPillButtonRight(g, rx, actionsY, I18n.T("Open project folder", "Открыть папку проекта"), ColPillIdle, ColTextPrimary, "aud_open_proj", () => _ctx.OpenProjectFolderFromUi());

        y += compactToolbar ? 90f : 46f;

        float rowH = 48f;
        int count = Math.Max(1, _auditItems.Count);
        var card = new RectangleF(x, y, w, count * rowH);
        DrawCardSurface(g, card);

        if (_auditItems.Count == 0)
        {
            DrawSettingRow(g, x, y, w, rowH, Theme.TextTertiary, "list", I18n.T("No entries for the selected filter", "Нет записей для выбранного фильтра"), I18n.T("Events will appear here as the agent calls tools", "События будут появляться здесь по мере вызова инструментов агентом"), drawDivider: false);
            return y + rowH + 16f;
        }

        var timeFont = Theme.FontMono(9.5f);
        var badgeFont = UiFont(9.5f, true);
        var detailFont = Theme.FontMono(9.5f);
        using var secBrush = new SolidBrush(ColTextSecondary);
        using var priBrush = new SolidBrush(ColTextPrimary);
        var noWrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };

        for (int i = 0; i < _auditItems.Count; i++)
        {
            var item = _auditItems[i];
            Color dotCol = item.Ok ? ColGreen : ColRed;
            using (var db = new SolidBrush(dotCol))
                g.FillEllipse(db, x + 16f, y + 20f, 8f, 8f);

            g.DrawString(item.TimeStr, timeFont, secBrush, new RectangleF(x + 32f, y, 68f, rowH), noWrap);
            g.DrawString(item.Badge, badgeFont, priBrush, new RectangleF(x + 104f, y, 140f, rowH), noWrap);
            g.DrawString(item.Detail, detailFont, secBrush, new RectangleF(x + 248f, y, w - 264f, rowH), noWrap);

            if (i < _auditItems.Count - 1)
            {
                using var divPen = new Pen(ColDivider, 1f);
                g.DrawLine(divPen, x + 32f, y + rowH, x + w - 16f, y + rowH);
            }
            y += rowH;
        }

        return y + 16f;
    }

    // ============================================================ 7. ABOUT (macOS "About This Mac" Style — Reference 4)
    private float DrawAboutContent(Graphics g, float x, float w, float y)
    {
        // Hero Card (centered icon + title + version like Mac Studio in Reference 4)
        var heroRect = new RectangleF(x, y, w, 132f);
        DrawCardSurface(g, heroRect);

        var iconRect = new RectangleF(x + (w - 52f) / 2f, y + 16f, 52f, 52f);
        using (var grad = new LinearGradientBrush(iconRect, Theme.AccentHover, Theme.AccentPressed, 90f))
        using (var path = CreateRoundRect(iconRect, 14f))
        {
            g.FillPath(grad, path);
        }
        Theme.StrokeRound(g, RectangleF.Inflate(iconRect, -0.5f, -0.5f), 13.5f, Theme.WithAlpha(Color.White, 40));
        Theme.DrawText(g, "Z", UiFont(21f, true), Color.White, new RectangleF(iconRect.X, iconRect.Y - 1f, iconRect.Width, iconRect.Height), StringAlignment.Center, StringAlignment.Center, ellipsis: false);

        var hFont = UiFont(15.0f, true);
        var sFont = UiFont(9.75f);
        using var pBrush = new SolidBrush(ColTextPrimary);
        using var sBrush = new SolidBrush(ColTextSecondary);
        var centerFmt = new StringFormat { Alignment = StringAlignment.Center };

        g.DrawString(Theme.ProductName, hFont, pBrush, new RectangleF(x, y + 74f, w, 26f), centerFmt);
        g.DrawString(I18n.T("Version 0.5 · OAuth 2.1, Access Registry & Confinement Edition", "Версия 0.5 · OAuth 2.1, Access Registry & Confinement Edition"), sFont, sBrush, new RectangleF(x, y + 100f, w, 20f), centerFmt);

        y += 148f;
        y = DrawGroupTitle(g, x, y, I18n.T("FILE AND FOLDER LOCATIONS", "РАСПОЛОЖЕНИЕ ФАЙЛОВ И ПАПОК"));

        float rowH = RowH;
        var card = new RectangleF(x, y, w, rowH * 4f);
        DrawCardSurface(g, card);

        DrawSettingRow(g, x, y, w - 140f, rowH, ColBlue, "folder", I18n.T("Project folder", "Папка проекта"), _ctx.Config.ProjectDir, drawDivider: true);
        DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("Open…", "Открыть…"), ColPillIdle, ColTextPrimary, "abt_proj", () => _ctx.OpenProjectFolderFromUi());

        y += rowH;
        DrawSettingRow(g, x, y, w - 140f, rowH, ColGreen, "folder", I18n.T("Logs folder (audit.jsonl, tray.log)", "Папка логов (audit.jsonl, tray.log)"), _ctx.GetLogDirPath(), drawDivider: true);
        DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("Open…", "Открыть…"), ColPillIdle, ColTextPrimary, "abt_logs", () => _ctx.OpenLogsFolderFromUi());

        y += rowH;
        DrawSettingRow(g, x, y, w - 140f, rowH, ColOrange, "shield", I18n.T("Permissions file (permissions.json)", "Файл прав (permissions.json)"), PermissionsStore.GetPermissionsPath(), drawDivider: true);
        DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("Open…", "Открыть…"), ColPillIdle, ColTextPrimary, "abt_perms", () => DesktopLauncher.OpenFile(PermissionsStore.GetPermissionsPath()));

        y += rowH;
        DrawSettingRow(g, x, y, w - 140f, rowH, ColPurple, "folder", I18n.Format("{0} install folder", "Папка установки {0}", Theme.ProductName), AppContext.BaseDirectory, drawDivider: false);
        DrawPillButtonRight(g, x + w - 16f, y + 16f, I18n.T("Open…", "Открыть…"), ColPillIdle, ColTextPrimary, "abt_inst", () => DesktopLauncher.OpenFolder(AppContext.BaseDirectory));

        return y + rowH + 16f;
    }

    // ============================================================ REDESIGN HELPERS (reference design system)
    private const string AppVersion = "v0.5";

    private static string? SectionSubtitle(string section) => section switch
    {
        "general" => I18n.T("Server, language, tray menu style and project folder.", "Сервер, язык, вид меню трея и рабочая папка проекта."),
        "tools" => I18n.T("Control AI agents' access to tools, commands and resources.", "Управление доступом AI-агентов к инструментам, командам и ресурсам."),
        "access" => I18n.T("Single-folder confinement, access tokens and OAuth connections.", "Ограничение одной папкой, токены доступа и подключения OAuth."),
        "approvals" => I18n.T("Show requests for dangerous actions and file operations.", "Показывать запросы на опасные действия и работу с файлами."),
        "notifications" => I18n.T("Card style and notification behaviour in the tray.", "Стиль карточек и поведение уведомлений в трее."),
        "connection" => I18n.T("How AI connects to this PC and which URL to use.", "Как AI подключается к этому ПК и какую ссылку использовать."),
        "audit" => I18n.T("History of tool calls and approval decisions.", "История вызовов инструментов и решений по подтверждениям."),
        "screenshots" => I18n.T("Archive of approved screenshots and its folder.", "Архив одобренных скриншотов и папка для их хранения."),
        "about" => I18n.T("App version and file locations.", "Версия приложения и расположение файлов."),
        _ => null,
    };

    private static Font FitFont(Graphics g, string text, float maxPx, float minPx, float width, bool strong)
    {
        for (float px = maxPx; px > minPx; px -= 0.5f)
        {
            var f = PanelTypography.Get(px, strong);
            if (Theme.MeasureText(g, text, f).Width <= width) return f;
        }
        return PanelTypography.Get(minPx, strong);
    }

    private static void DrawRadialWash(Graphics g, PointF c, float rx, float ry, Color color)
    {
        if (rx <= 1f || ry <= 1f) return;
        using var path = new GraphicsPath();
        path.AddEllipse(c.X - rx, c.Y - ry, rx * 2f, ry * 2f);
        using var brush = new PathGradientBrush(path) { CenterPoint = c, CenterColor = color, SurroundColors = new[] { Color.FromArgb(0, color) } };
        g.FillPath(brush, path);
    }

    private static void DrawHeroTile(Graphics g, RectangleF r, IconKind icon, Color tint, bool glow)
    {
        if (glow) Theme.DrawGlow(g, r, 16f, Theme.WithAlpha(tint, 54), 10);
        Theme.FillRound(g, r, 16f, Theme.Composite(Theme.WithAlpha(tint, 34), ColCardBg));
        Theme.StrokeRound(g, r, 16f, Theme.WithAlpha(tint, 84));
        float s = r.Width * 0.5f;
        Theme.DrawIcon(g, icon, new RectangleF(r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s), tint, 1.8f);
    }

    /// <summary>Tinted icon tile as in the references (.ico): soft tint wash + tint hairline; neutral tint = raised tile.</summary>
    private static void DrawTintTile(Graphics g, RectangleF r, IconKind icon, Color tint, float radius, float iconSize)
    {
        bool neutral = tint.ToArgb() == Theme.TextTertiary.ToArgb();
        Theme.FillRound(g, r, radius, neutral ? Theme.SurfaceRaised : Theme.WithAlpha(tint, 34));
        Theme.StrokeRound(g, r, radius, neutral ? Theme.Border : Theme.WithAlpha(tint, 68));
        Color fg = neutral ? Color.FromArgb(0xC5, 0xCB, 0xD8) : Theme.TextVariant(tint);
        Theme.DrawIcon(g, icon, new RectangleF(r.X + (r.Width - iconSize) / 2f, r.Y + (r.Height - iconSize) / 2f, iconSize, iconSize), fg, 1.7f);
    }

    private static float ChipWidth(Graphics g, string text, bool hasIcon) =>
        Theme.MeasureText(g, text, PanelTypography.Get(12f, true)).Width + 22f + (hasIcon ? 19f : 0f);

    private bool DrawInfoChip(Graphics g, ref float cx, float maxRight, float y, IconKind? icon, string text, string? hitId, Action? onClick, Color? tint = null)
    {
        var font = PanelTypography.Get(12f, true);
        float w = ChipWidth(g, text, icon != null);
        if (cx + w > maxRight) return false;
        var r = new RectangleF(cx, y, w, 24f);
        bool hov = hitId != null && r.Contains(_mousePos);
        Color fg = tint.HasValue ? Theme.TextVariant(tint.Value) : ColTextSecondary;
        Theme.FillRound(g, r, 12f, tint.HasValue ? Theme.WithAlpha(tint.Value, hov ? 46 : 32) : Color.FromArgb(hov ? 26 : 16, 255, 255, 255));
        if (tint.HasValue) Theme.StrokeRound(g, r, 12f, Theme.WithAlpha(tint.Value, 60));
        float ix = r.X + 11f;
        if (icon != null) { Theme.DrawIcon(g, icon.Value, new RectangleF(ix, y + 5.5f, 13f, 13f), fg, 1.7f); ix += 19f; }
        Theme.DrawText(g, text, font, fg, new RectangleF(ix, y, r.Right - ix - 6f, 24f));
        if (hitId != null && onClick != null) _hitZones.Add(new(r, hitId, onClick, text));
        cx += w + 8f;
        return true;
    }

    private static IconKind EventIcon(string kind, string tool) => kind switch
    {
        "folder" => IconKind.Folder,
        "file" => IconKind.File,
        "edit" => IconKind.Edit,
        "search" => IconKind.Search,
        "job" => IconKind.Clock,
        "terminal" => IconKind.Terminal,
        _ => tool switch { "take_screenshot" => IconKind.Image, "run_command" => IconKind.Terminal, "user_message" or "task_status" => IconKind.Notifications, _ => IconKind.Tools },
    };

    private static string TimeAgo(string ts)
    {
        if (!DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) return "";
        var local = dt.Kind == DateTimeKind.Unspecified ? dt : dt.ToLocalTime();
        var d = DateTime.Now - local;
        if (d.TotalSeconds < 60) return I18n.T("just now", "только что");
        if (d.TotalMinutes < 60) return I18n.Format("{0} min ago", "{0} мин назад", (int)d.TotalMinutes);
        if (d.TotalHours < 24) return I18n.Format("{0} h ago", "{0} ч назад", (int)d.TotalHours);
        return local.ToString("dd.MM HH:mm");
    }

    private static void DrawCallStatus(Graphics g, RectangleF r, string status)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (status == "running")
        {
            using var pen = new Pen(Color.FromArgb(64, 255, 255, 255), 1.5f) { DashPattern = new[] { 2.2f, 2.2f } };
            g.DrawEllipse(pen, RectangleF.Inflate(r, -1f, -1f));
            using var b = new SolidBrush(ColBlue);
            g.FillEllipse(b, r.X + r.Width / 2f - 3f, r.Y + r.Height / 2f - 3f, 6f, 6f);
        }
        else
        {
            bool ok = status == "done" || status == "ok" || status == "allowed";
            Color c = ok ? ColGreen : ColRed;
            using (var b = new SolidBrush(Theme.WithAlpha(c, 36))) g.FillEllipse(b, r);
            Theme.DrawIcon(g, ok ? IconKind.Check : IconKind.Close, RectangleF.Inflate(r, -5f, -5f), c, 2.6f);
        }
        g.SmoothingMode = old;
    }

    private static bool LooksLikePath(string? s) =>
        !string.IsNullOrEmpty(s) && s.Length > 2 && ((char.IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) || s.StartsWith("\\\\") || s.StartsWith("~/") || s.StartsWith("/"));

    private static IconKind RowIcon(string glyph) => glyph switch
    {
        "shield" => IconKind.Lock,
        "timer" => IconKind.Clock,
        "folder" => IconKind.Folder,
        "folderlock" => IconKind.FolderLock,
        "image" => IconKind.Image,
        "server" => IconKind.Server,
        "terminal" => IconKind.Terminal,
        "edit" => IconKind.Edit,
        "file" => IconKind.File,
        "eye" => IconKind.Eye,
        "sliders" => IconKind.Sliders,
        "shieldcheck" => IconKind.ShieldCheck,
        "user" => IconKind.User,
        "link" => IconKind.Link,
        "bulb" => IconKind.Lightbulb,
        "warning" => IconKind.Warning,
        "power" => IconKind.Power,
        _ => GlyphToIcon(glyph),
    };

    private void ToggleRedFlags()
    {
        bool rfOn = _perms.RedFlags?.Enabled != false && _perms.RedFlags?.ElevateToAsk != false;
        _perms.RedFlags ??= new RedFlagsConfig();
        _perms.RedFlags.Enabled = !rfOn;
        _perms.RedFlags.ElevateToAsk = !rfOn;
        PermissionsStore.Save(_perms);
    }

    private void ToggleConfinement()
    {
        bool confEnabled = _perms.Confinement?.Enabled == true;
        _perms.Confinement ??= new ConfinementConfig();
        if (!confEnabled)
        {
            string chosenRoot = !string.IsNullOrWhiteSpace(_perms.Confinement.Root) && Directory.Exists(_perms.Confinement.Root)
                ? _perms.Confinement.Root
                : _ctx.Config.ProjectDir;
            if (string.IsNullOrWhiteSpace(chosenRoot) || !Directory.Exists(chosenRoot))
            {
                using var dlg = new FolderBrowserDialog { Description = I18n.T("Choose a folder for single-folder mode", "Выберите папку для режима одной папки") };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                chosenRoot = dlg.SelectedPath;
            }
            _perms.Confinement.Root = chosenRoot;
            _perms.Confinement.Enabled = true;
        }
        else
        {
            _perms.Confinement.Enabled = false;
        }
        PermissionsStore.Save(_perms);
        _ctx.RefreshConfinementFromPermissions();
    }

    private sealed record InsightCard(IconKind Icon, Color Tint, string Label, string Value, string? Caption, bool Tip = false, string? HitId = null, Action? OnClick = null);

    /// <summary>Row of summary cards (reference right-column cards: «Безопасность», «Агентам доступно», «Совет»).</summary>
    private float DrawInsightRow(Graphics g, float x, float w, float y, params InsightCard[] cards)
    {
        const float gap = 14f, h = 104f, tipUnits = 1.3f;
        bool stackTip = w < 860f && cards.Any(c => c.Tip);
        var rowCards = stackTip ? cards.Where(c => !c.Tip).ToArray() : cards;
        float units = rowCards.Sum(c => c.Tip ? tipUnits : 1f);
        float unit = (w - gap * (rowCards.Length - 1)) / units;
        float cx = x;
        foreach (var c in rowCards)
        {
            float cw = unit * (c.Tip ? tipUnits : 1f);
            DrawInsightCard(g, new RectangleF(cx, y, cw, h), c, false);
            cx += cw + gap;
        }
        float bottom = y + h;
        if (stackTip)
        {
            foreach (var c in cards.Where(c => c.Tip))
            {
                var r = new RectangleF(x, bottom + 12f, w, 58f);
                DrawInsightCard(g, r, c, true);
                bottom = r.Bottom;
            }
        }
        return bottom + 22f;
    }

    private void DrawInsightCard(Graphics g, RectangleF r, InsightCard c, bool inline)
    {
        float cw = r.Width, h = r.Height;
        bool hov = c.HitId != null && r.Contains(_mousePos);
        if (c.Tip)
        {
            Theme.FillRound(g, r, Theme.RadiusCard, ColCardBg);
            Theme.FillRoundGradient(g, r, Theme.RadiusCard, Theme.WithAlpha(ColOrange, 18), Theme.WithAlpha(ColOrange, 5));
            Theme.StrokeRound(g, r, Theme.RadiusCard, Theme.WithAlpha(ColOrange, hov ? 70 : 42));
            if (inline)
            {
                var ic = new RectangleF(r.X + 16f, r.Y + (h - 30f) / 2f, 30f, 30f);
                DrawTintTile(g, ic, IconKind.Lightbulb, ColOrange, 8f, 15f);
                var lf = PanelTypography.Get(14f, true);
                float lw = Theme.MeasureText(g, c.Label, lf).Width + 4f;
                Theme.DrawText(g, c.Label, lf, ColTextPrimary, new RectangleF(ic.Right + 12f, r.Y, lw, h));
                float tx = ic.Right + 12f + lw + 10f;
                Theme.DrawText(g, c.Value, PanelTypography.Get(12.5f), ColTextSecondary, new RectangleF(tx, r.Y + 4f, r.Right - 16f - tx, h - 8f), vertical: StringAlignment.Center, ellipsis: true, wrap: true);
            }
            else
            {
                var ic = new RectangleF(r.X + 16f, r.Y + 14f, 30f, 30f);
                DrawTintTile(g, ic, IconKind.Lightbulb, ColOrange, 8f, 15f);
                Theme.DrawText(g, c.Label, PanelTypography.Get(14f, true), ColTextPrimary, new RectangleF(ic.Right + 12f, ic.Y, cw - 74f, 30f));
                Theme.DrawText(g, c.Value, PanelTypography.Get(12.5f), ColTextSecondary, new RectangleF(r.X + 16f, r.Y + 52f, cw - 32f, h - 58f), vertical: StringAlignment.Near, ellipsis: true, wrap: true);
            }
        }
        else
        {
            DrawCardSurface(g, r);
            using (var cp = Theme.RoundRect(r, Theme.RadiusCard))
            {
                var st = g.Save();
                g.SetClip(cp, CombineMode.Intersect);
                DrawRadialWash(g, new PointF(r.X, r.Y), cw * 0.9f, h * 1.3f, Theme.WithAlpha(c.Tint, 30));
                g.Restore(st);
            }
            Theme.StrokeRound(g, r, Theme.RadiusCard, hov ? Theme.BorderStrong : Theme.WithAlpha(c.Tint, 46));
            var ic = new RectangleF(r.X + 16f, r.Y + (h - 42f) / 2f, 42f, 42f);
            DrawTintTile(g, ic, c.Icon, c.Tint, 12f, 20f);
            float tx = ic.Right + 13f, tw = r.Right - 14f - tx;
            float top = r.Y + (c.Caption == null ? 30f : 20f);
            Theme.DrawText(g, c.Label, FitFont(g, c.Label, 12.5f, 10.5f, tw, false), ColTextSecondary, new RectangleF(tx, top, tw, 20f));
            Theme.DrawText(g, c.Value, FitFont(g, c.Value, 15.5f, 12f, tw, true), ColTextPrimary, new RectangleF(tx, top + 20f, tw, 24f));
            if (c.Caption != null) Theme.DrawText(g, c.Caption, FitFont(g, c.Caption, 12f, 10.5f, tw, false), Theme.TextTertiary, new RectangleF(tx, top + 44f, tw, 20f));
        }
        if (c.HitId != null && c.OnClick != null) _hitZones.Add(new(r, c.HitId, c.OnClick, c.Label + "\n" + c.Value + (c.Caption != null ? "\n" + c.Caption : "")));
    }

    /// <summary>Accent info banner (reference .info).</summary>
    private static float DrawInfoBanner(Graphics g, float x, float w, float y, string text)
    {
        var font = PanelTypography.Get(12.5f);
        float textW = w - 54f;
        float textH = Theme.MeasureText(g, text, font, textW).Height;
        float h = Math.Max(40f, textH + 20f);
        var r = new RectangleF(x, y, w, h);
        Theme.FillRound(g, r, 11f, Theme.WithAlpha(ColBlue, 36));
        Theme.StrokeRound(g, r, 11f, Theme.WithAlpha(ColBlue, 76));
        Theme.DrawIcon(g, IconKind.Info, new RectangleF(x + 14f, y + (Math.Min(h, 40f) - 16f) / 2f, 16f, 16f), Color.FromArgb(0x9A, 0xA6, 0xFF), 1.7f);
        Theme.DrawText(g, text, font, Color.FromArgb(0xC7, 0xCD, 0xFF), new RectangleF(x + 40f, y, textW, h), vertical: StringAlignment.Center, ellipsis: false, wrap: true);
        return y + h;
    }

    private static void DrawTracked(Graphics g, string text, Font font, Color color, float x, float y, float tracking)
    {
        using var brush = new SolidBrush(color);
        var fmt = StringFormat.GenericTypographic;
        float cx = x;
        foreach (char ch in text)
        {
            string s = ch.ToString();
            if (ch == ' ') { cx += font.Size * 0.3f + tracking; continue; }
            g.DrawString(s, font, brush, cx, y, fmt);
            cx += g.MeasureString(s, font, PointF.Empty, fmt).Width + tracking;
        }
    }

    // ============================================================ UI DRAWING PRIMITIVES (Theme-based)
    private static void DrawCardSurface(Graphics g, RectangleF rect)
    {
        Theme.DrawCard(g, rect, ColCardBg, Theme.RadiusCard, ColCardBorder);
    }

    private static float DrawGroupTitle(Graphics g, float x, float y, string title)
    {
        DrawTracked(g, title, PanelTypography.Get(11.5f, true), Color.FromArgb(0x8A, 0x92, 0xA3), x + 4f, y, 0.7f);
        return y + 26f;
    }

    private void DrawSettingRow(
        Graphics g,
        float x,
        float y,
        float textAreaWidth,
        float rowH,
        Color badgeColor,
        string glyph,
        string title,
        string subtitle,
        bool drawDivider)
    {
        float tileS = Math.Min(36f, rowH - 16f);
        var iconRect = new RectangleF(x + 16f, y + (rowH - tileS) / 2f, tileS, tileS);
        DrawTintTile(g, iconRect, RowIcon(glyph), badgeColor, 10f, tileS * 0.5f);

        float tx = iconRect.Right + 14f;
        float maxW = Math.Max(100f, textAreaWidth - (tx - x) - 12f);
        bool pathSub = LooksLikePath(subtitle);
        var subFont = pathSub ? Theme.FontMono(9f) : PanelTypography.Get(12f);

        Theme.DrawText(g, title, UiBodyStrong, ColTextPrimary, new RectangleF(tx, y + rowH / 2 - 22, maxW, 23f));
        float subW = maxW;
        bool subTruncated = (!string.IsNullOrEmpty(subtitle) && Theme.MeasureText(g, subtitle, subFont).Width > maxW) || Theme.MeasureText(g, title, UiBodyStrong).Width > maxW;
        if (subTruncated) subW = Math.Max(100f, maxW - 22f);
        Theme.DrawText(g, subtitle, subFont, ColTextSecondary, new RectangleF(tx, y + rowH / 2 + 1, subW, 20f));
        if (subTruncated)
        {
            var infoRect = new RectangleF(tx + maxW - 18f, y + rowH / 2 + 4, 16f, 16f);
            bool infoHov = infoRect.Contains(_mousePos);
            Theme.DrawIcon(g, IconKind.Info, infoRect, infoHov ? ColBlue : ColTextSecondary, 1.6f);
            string tip = WrapTooltipText(title + "\n" + subtitle);
            _hitZones.Add(new HitZone(infoRect, "info_tip", () => _toolTip.Show(tip, this, new Point((int)infoRect.Right + 8, (int)infoRect.Y), 5000), tip));
        }

        if (drawDivider)
        {
            float cardRight = Width - 32f - 16f;
            Theme.DrawDivider(g, x + 1f, cardRight, y + rowH - 0.5f);
        }
    }

    /// <summary>Word-wraps a tooltip body so long subtitles show as readable lines, not one endless strip.</summary>
    private static string WrapTooltipText(string text, int maxLen = 90)
    {
        if (text.Length <= maxLen) return text;
        var words = text.Split(' ');
        var sb = new System.Text.StringBuilder();
        int lineLen = 0;
        foreach (var word in words)
        {
            if (lineLen > 0 && lineLen + 1 + word.Length > maxLen)
            {
                sb.Append('\n');
                lineLen = 0;
            }
            else if (lineLen > 0)
            {
                sb.Append(' ');
                lineLen++;
            }
            if (word.Length > maxLen)
            {
                for (int i = 0; i < word.Length; i += maxLen)
                {
                    if (i > 0) { sb.Append('\n'); lineLen = 0; }
                    string part = word.Substring(i, Math.Min(maxLen, word.Length - i));
                    sb.Append(part);
                    lineLen = part.Length;
                }
            }
            else
            {
                sb.Append(word);
                lineLen += word.Length;
            }
        }
        return sb.ToString();
    }

    private float DrawPillButtonRight(Graphics g, float rightEdge, float y, string text, Color bg, Color fg, string hitId, Action onClick)
    {
        var font = PanelTypography.Get(12.5f, true);
        var sz = Theme.MeasureText(g, text, font);
        float w = Math.Max(76f, sz.Width + 30f);
        float h = 32f;
        var rect = new RectangleF(rightEdge - w, y, w, h);

        bool hov = rect.Contains(_mousePos);
        var state = hov ? ButtonState.Hover : ButtonState.Normal;

        if (bg.ToArgb() == ColBlue.ToArgb())
        {
            Theme.DrawButton(g, rect, text, ButtonKind.Primary, state, font);
        }
        else if (bg.ToArgb() == ColPillIdle.ToArgb())
        {
            if (fg.ToArgb() == ColTextPrimary.ToArgb())
            {
                Theme.DrawButton(g, rect, text, ButtonKind.Secondary, state, font);
            }
            else
            {
                // Secondary button with semantic text (e.g. Revoke / Disconnect)
                var (back, _, border) = Theme.ButtonColors(ButtonKind.Secondary, state);
                Theme.FillRound(g, rect, Theme.RadiusControl, back);
                Theme.StrokeRound(g, rect, Theme.RadiusControl, border);
                Theme.DrawText(g, text, font, Theme.TextVariant(fg), RectangleF.Inflate(rect, -8f, 0), StringAlignment.Center, StringAlignment.Center);
            }
        }
        else
        {
            // Semantic tinted button (success / danger / warning)
            Color sem = Color.FromArgb(255, bg);
            if (bg.A < 255 && fg.A == 255 && fg.ToArgb() != Color.Black.ToArgb()) sem = fg;
            Theme.FillRound(g, rect, Theme.RadiusControl, Theme.WithAlpha(sem, hov ? 64 : 44));
            Theme.StrokeRound(g, rect, Theme.RadiusControl, Theme.WithAlpha(sem, hov ? 150 : 110));
            Theme.DrawText(g, text, font, Theme.TextVariant(sem), RectangleF.Inflate(rect, -8f, 0), StringAlignment.Center, StringAlignment.Center);
        }

        _hitZones.Add(new HitZone(rect, hitId, onClick));
        return rect.Left;
    }

    private void DrawIosToggle(Graphics g, float x, float y, bool isOn, string hitId, Action onClick)
    {
        var rect = new RectangleF(x, y, 44f, 24f);
        var track = new RectangleF(x + 2f, y + 2f, Theme.ToggleWidth, Theme.ToggleHeight);
        Theme.DrawToggle(g, track, isOn, rect.Contains(_mousePos), disabled: false, onColor: ColBlue);

        _hitZones.Add(new HitZone(rect, hitId, onClick));
    }

    private void DrawSegmentedBar(
        Graphics g,
        RectangleF barRect,
        (string Key, string Title, Color ActiveCol)[] items,
        string activeKey,
        string idPrefix,
        Action<string> onPick)
    {
        if (items.Length == 0) return;
        int selected = -1;
        int hover = -1;
        float segW = (barRect.Width - 4f) / items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            if (string.Equals(activeKey, items[i].Key, StringComparison.OrdinalIgnoreCase)) selected = i;
            var approx = new RectangleF(barRect.X + 2f + i * segW, barRect.Y + 2f, segW, barRect.Height - 4f);
            if (approx.Contains(_mousePos)) hover = i;
        }

        bool softStyle = idPrefix.StartsWith("tool_");
        Theme.FillRound(g, barRect, 11, Color.FromArgb(64, 0, 0, 0));
        Theme.StrokeRound(g, barRect, 11, Theme.Border);
        for (int i = 0; i < items.Length; i++)
        {
            var segment = new RectangleF(barRect.X + 3 + i * segW, barRect.Y + 3, segW - 2, barRect.Height - 6);
            bool active = i == selected;
            Color tint = items[i].ActiveCol;
            if (active && softStyle)
            {
                Theme.FillRound(g, segment, 8, Theme.WithAlpha(tint, 40));
                Theme.StrokeRound(g, segment, 8, Theme.WithAlpha(tint, 96));
            }
            else if (active)
            {
                using (var glow = Theme.RoundRect(RectangleF.Inflate(segment, 2f, 2f), 10f))
                using (var gb = new SolidBrush(Theme.WithAlpha(ColBlue, 40))) g.FillPath(gb, glow);
                Theme.FillRoundGradient(g, segment, 8, Theme.AccentHover, Theme.AccentPressed);
            }
            else if (i == hover) Theme.FillRound(g, segment, 8, Theme.HoverOverlay);
            Color segText = active ? (softStyle ? Theme.TextVariant(tint) : Color.White) : (i == hover ? ColTextPrimary : ColTextSecondary);
            Theme.DrawText(g, items[i].Title, FitFont(g, items[i].Title, softStyle ? 12f : 12.5f, 10.5f, segment.Width - 10f, active), segText, RectangleF.Inflate(segment, -4, 0), StringAlignment.Center);
        }

        for (int i = 0; i < items.Length; i++)
        {
            string capturedKey = items[i].Key;
            var hitRect = new RectangleF(barRect.X + 2f + i * segW, barRect.Y + 2f, segW, barRect.Height - 4f);
            _hitZones.Add(new HitZone(hitRect, $"{idPrefix}_{capturedKey}", () => onPick(capturedKey)));
        }
    }

    private void ShowPopupOptions((string Label, bool Checked, Action Pick)[] options)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = true,
        };
        foreach (var opt in options)
        {
            var item = new ToolStripMenuItem(opt.Label)
            {
                Checked = opt.Checked,
            };
            var cb = opt.Pick;
            item.Click += (_, _) => cb();
            menu.Items.Add(item);
        }
        Theme.StyleMenu(menu);
        menu.Show(this, _mousePos);
    }

    private static GraphicsPath CreateRoundRect(RectangleF r, float radius) => Theme.RoundRect(r, radius);

    public void RenderPreviewToFile(string filePath, string section)
    {
        _activeSection = section;
        _scrollY = 0;
        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        using var pe = new PaintEventArgs(g, new Rectangle(0, 0, Width, Height));
        g.Clear(Color.Transparent);
        OnPaint(pe);
        bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private sealed record AuditRowItem(string TimeStr, string Badge, string Detail, bool Ok);
}

public sealed record ActiveGrantItem(
    string Key,
    string Tool,
    string Scope,
    string Target,
    int RemainingSec
);
