using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

/// <summary>Decides whether the exe was started from an unpacked release package (setup wizard)
/// or from an installation (tray). The decision never touches the network or writes files.</summary>
internal static class SetupLaunch
{
    public const string ExeName = "ZeroIDE-MCP.exe";
    /// <summary>ZEROIDE_INSTALL_ROOT (development aid) redirects the per-user root; install.ps1 follows LOCALAPPDATA, so set both for an isolated run.</summary>
    internal static string LocalRoot => Environment.GetEnvironmentVariable("ZEROIDE_INSTALL_ROOT") is { Length: > 0 } r && Path.IsPathFullyQualified(r) ? r : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string InstallDir => Path.Combine(LocalRoot, "ZeroIDE-MCP");
    /// <summary>A finished installation has tray-config.json; a folder without it is an interrupted install that the installer completes.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(InstallDir, "tray-config.json"));
    public static bool HasUnfinishedInstall => Directory.Exists(InstallDir) && !IsInstalled;
    public static string ExeDir => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Installed (or deliberately portable) copies keep tray-config.json next to the exe.</summary>
    public static bool IsInstalledLocation()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "tray-config.json"))) return true;
        try { return string.Equals(Path.GetFullPath(ExeDir), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }

    /// <summary>Package root = the folder holding installer\\install.ps1: the exe sits either at the root or in tray\build.</summary>
    public static string? FindPackageRoot()
    {
        string? dir = ExeDir;
        for (int i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "installer", "install.ps1"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static bool ShouldRunSetup(out string packageRoot)
    {
        packageRoot = "";
        if (IsInstalledLocation()) return false;
        packageRoot = FindPackageRoot() ?? ExeDir;
        return true;
    }

    public static string[] MissingPackageFiles(string root)
    {
        string[] required = ["installer\\install.ps1", "server\\server.mjs", "tools\\Configure-Connection.ps1", "tools\\Onboarding.Common.ps1", "tools\\Onboarding.Ngrok.ps1", "tools\\Test-LocalMcp.ps1", "tools\\Setup-Ngrok.ps1", "tools\\Onboarding.Tailscale.ps1", "tools\\Setup-Tailscale.ps1", "tools\\Onboarding.Cloudflare.ps1", "tools\\Setup-Cloudflare.ps1", "AGENT_INSTALL.md"];
        var missing = required.Where(r => !File.Exists(Path.Combine(root, r))).ToList();
        if (!File.Exists(Path.Combine(root, ExeName)) && !File.Exists(Path.Combine(root, "tray", "build", ExeName))) missing.Add(ExeName);
        return missing.ToArray();
    }

    public static bool PortFree(int port)
    {
        try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; } catch { return false; }
    }

    /// <summary>True when a ZeroIDE-MCP.exe other than this process runs from the installation folder (or its path cannot be read).</summary>
    public static bool InstalledAppRunning()
    {
        try
        {
            return Process.GetProcessesByName("ZeroIDE-MCP").Any(p =>
            {
                if (p.Id == Environment.ProcessId) return false;
                try { return p.MainModule?.FileName?.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase) ?? true; } catch { return true; }
            });
        }
        catch { return false; }
    }
    public static string LegacyInstallDir => Path.Combine(LocalRoot, "AgentPower");
    /// <summary>The pre-rename build (AgentPowerTray.exe, or ZeroIDE-MCP.exe started from the old folder) still runs: its files cannot be moved.</summary>
    public static bool LegacyAppRunning()
    {
        try
        {
            return Process.GetProcessesByName("AgentPowerTray").Concat(Process.GetProcessesByName("ZeroIDE-MCP")).Any(p =>
            {
                if (p.Id == Environment.ProcessId) return false;
                try { return p.MainModule?.FileName?.StartsWith(LegacyInstallDir, StringComparison.OrdinalIgnoreCase) ?? false; } catch { return false; }
            });
        }
        catch { return false; }
    }

    /// <summary>Runs a shipped PowerShell script hidden, streaming its output lines; UTF-8 so localized JSON survives.</summary>
    public static Process StartScript(string script, string arguments, string workingDirectory)
    {
        string quoted = "'" + script.Replace("'", "''") + "'";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; & " + quoted + " " + arguments + "; exit $LASTEXITCODE\"",
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        return p;
    }
}

// ------------------------------------------------------------------ widgets

internal static class WizardText
{
    /// <summary>Wrapped text height (Theme.MeasureText measures a single line).</summary>
    public static float Height(Graphics g, string text, Font font, float width)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags &= ~StringFormatFlags.NoWrap;
        return g.MeasureString(text, font, new SizeF(Math.Max(1, width), 10000), format).Height;
    }
}

internal sealed class WizardButton : Button
{
    private readonly ButtonKind _kind;
    private bool _hover, _pressed;
    public WizardButton(string text, Action action, ButtonKind kind = ButtonKind.Secondary, int minWidth = 110)
    {
        Text = text; _kind = kind; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
        Font = Theme.Font(10, true); AutoSize = false; Height = 42; Margin = new Padding(0, 0, 10, 10); BackColor = Theme.Bg1;
        Width = Math.Max(minWidth, TextRenderer.MeasureText(text, Font).Width + 44);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AccessibleName = text; Click += (_, _) => action();
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; _pressed = false; Invalidate(); };
        MouseDown += (_, _) => { _pressed = true; Invalidate(); };
        MouseUp += (_, _) => { _pressed = false; Invalidate(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg1); Theme.PrepareGraphics(e.Graphics);
        var state = !Enabled ? ButtonState.Disabled : _pressed ? ButtonState.Pressed : (_hover || Focused ? ButtonState.Hover : ButtonState.Normal);
        Theme.DrawButton(e.Graphics, new RectangleF(5, 5, Width - 10, Height - 10), Text, _kind, state, Font);
    }
}

/// <summary>Clickable card: title, description, chevron. Primary cards get an accent border.</summary>
internal sealed class WizardChoice : Control
{
    private readonly string _title, _desc; private readonly bool _primary; private bool _hover;
    public WizardChoice(string title, string desc, Action onClick, int width, bool primary = false)
    {
        _title = title; _desc = desc; _primary = primary; Width = width; Cursor = Cursors.Hand; Margin = new Padding(0, 0, 0, 10);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        using (var g = CreateGraphics()) Height = (int)(40 + WizardText.Height(g, _desc, Theme.Small, width - 76)) + 18;
        Click += (_, _) => onClick(); Text = title; AccessibleName = title; AccessibleRole = AccessibleRole.PushButton; TabStop = true;
        MouseEnter += (_, _) => { _hover = true; Invalidate(); }; MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) onClick(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg1); Theme.PrepareGraphics(g);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        Theme.FillRound(g, r, Theme.RadiusCard, _hover ? Theme.SurfaceHover : Theme.Surface);
        Theme.StrokeRound(g, r, Theme.RadiusCard, _primary ? Theme.WithAlpha(Theme.Accent, _hover ? 200 : 140) : _hover || Focused ? Theme.BorderStrong : Theme.Border, _primary ? 1.5f : 1f);
        Theme.FillRound(g, new RectangleF(r.X + 14, r.Y + 16, 4, 20), 2, _primary ? Theme.Accent : Theme.WithAlpha(Theme.TextTertiary, 120));
        Theme.DrawText(g, _title, Theme.BodyStrong, Theme.TextPrimary, new RectangleF(r.X + 30, r.Y + 10, r.Width - 76, 30));
        Theme.DrawText(g, _desc, Theme.Small, Theme.TextSecondary, new RectangleF(r.X + 30, r.Y + 40, r.Width - 76, r.Height - 46), StringAlignment.Near, StringAlignment.Near, false, true);
        Theme.DrawText(g, "›", Theme.Font(16), _hover ? Theme.TextPrimary : Theme.TextTertiary, new RectangleF(r.Right - 40, r.Y, 26, r.Height), StringAlignment.Center);
    }
}

/// <summary>Static tinted note card (warning/info/success).</summary>
internal sealed class WizardNote : Control
{
    private readonly string _title, _text; private readonly Color _tone;
    public WizardNote(string title, string text, Color tone, int width)
    {
        _title = title; _text = text; _tone = tone; Width = width; Margin = new Padding(0, 0, 0, 14);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        using var g = CreateGraphics();
        Height = (int)((title.Length > 0 ? 30 : 6) + WizardText.Height(g, _text, Theme.Small, width - 44)) + 28;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg1); Theme.PrepareGraphics(g);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        Theme.FillRound(g, r, Theme.RadiusCard, Theme.WithAlpha(_tone, 22)); Theme.StrokeRound(g, r, Theme.RadiusCard, Theme.WithAlpha(_tone, 90));
        float y = r.Y + 12;
        if (_title.Length > 0) { Theme.DrawText(g, _title, Theme.BodyStrong, Theme.TextVariant(_tone), new RectangleF(r.X + 20, y, r.Width - 40, 24)); y += 28; }
        Theme.DrawText(g, _text, Theme.Small, Theme.TextPrimary, new RectangleF(r.X + 20, y, r.Width - 40, r.Height - (y - r.Y) - 8), StringAlignment.Near, StringAlignment.Near, false, true);
    }
}

internal sealed record WizardStep(string Text, string State, bool Owner = false);

/// <summary>Step list with ✓ / ● / ○ markers and a "YOU" badge for owner steps.</summary>
internal sealed class WizardChecklist : Control
{
    private WizardStep[] _items = [];
    private string _signature = "";
    public WizardChecklist(int width) { Width = width; Margin = new Padding(0, 0, 0, 14); SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    public void SetItems(IEnumerable<WizardStep> items)
    {
        var list = items.ToArray(); string sig = string.Join("|", list.Select(i => i.Text + ":" + i.State + ":" + i.Owner));
        if (sig == _signature) return;
        _signature = sig; _items = list;
        using var g = CreateGraphics();
        Height = 20 + (int)_items.Sum(i => RowHeight(g, i));
        Invalidate();
    }
    private float RowHeight(Graphics g, WizardStep s) => Math.Max(32, WizardText.Height(g, s.Text, Theme.Small, Width - 112) + 14);
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg1); Theme.PrepareGraphics(g);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        Theme.FillRound(g, r, Theme.RadiusCard, Theme.Surface); Theme.StrokeRound(g, r, Theme.RadiusCard, Theme.Border);
        float y = r.Y + 10;
        foreach (var s in _items)
        {
            float h = RowHeight(g, s);
            Color c = s.State == "done" ? Theme.Success : s.State == "current" ? Theme.Accent : Theme.TextTertiary;
            Theme.DrawText(g, s.State == "done" ? "✓" : s.State == "current" ? "●" : "○", Theme.BodyStrong, c, new RectangleF(r.X + 16, y, 24, h));
            Theme.DrawText(g, s.Text, Theme.Small, s.State == "todo" ? Theme.TextSecondary : Theme.TextPrimary, new RectangleF(r.X + 44, y + 7, r.Width - 110, h - 8), StringAlignment.Near, StringAlignment.Near, false, true);
            if (s.Owner)
            {
                var badge = new RectangleF(r.Right - 60, y + (h - 22) / 2, 44, 22);
                Theme.FillRound(g, badge, 8, Theme.WithAlpha(Theme.Warning, 34));
                Theme.DrawText(g, I18n.T("YOU", "ТЫ"), Theme.Caption, Theme.TextVariant(Theme.Warning), badge, StringAlignment.Center);
            }
            y += h;
        }
    }
}

/// <summary>Thin progress bar; Value &lt; 0 animates as indeterminate.</summary>
internal sealed class WizardProgress : Control
{
    private float _phase; public float Value { get; set; } = -1;
    public WizardProgress(int width) { Width = width; Height = 8; Margin = new Padding(0, 4, 0, 14); SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    public void Tick() { _phase = (_phase + 0.03f) % 1f; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg1); Theme.PrepareGraphics(g);
        var r = new RectangleF(0, 1, Width, 6); Theme.FillRound(g, r, 3, Theme.Trough);
        if (Value >= 0) Theme.FillRound(g, new RectangleF(0, 1, Math.Max(6, Width * Math.Min(1f, Value)), 6), 3, Theme.Accent);
        else { float w = Width * 0.28f; float x = (Width + w) * _phase - w; float sx = Math.Max(0, x); Theme.FillRound(g, new RectangleF(sx, 1, Math.Max(6, Math.Min(w, Width - sx)), 6), 3, Theme.Accent); }
    }
}

/// <summary>Left rail: product name, numbered steps, language switch.</summary>
internal sealed class WizardRail : Control
{
    private readonly (string En, string Ru)[] _steps; private int _current; private readonly Action<string> _onLanguage;
    private RectangleF _en, _ru;
    public WizardRail((string En, string Ru)[] steps, Action<string> onLanguage)
    {
        _steps = steps; _onLanguage = onLanguage; Width = 236; Dock = DockStyle.Left;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        MouseClick += (_, e) => { if (_en.Contains(e.Location)) _onLanguage("en"); else if (_ru.Contains(e.Location)) _onLanguage("ru"); };
        MouseMove += (_, e) => Cursor = _en.Contains(e.Location) || _ru.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }
    public void SetCurrent(int index) { _current = index; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.PrepareGraphics(g);
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle, Theme.Bg0, Theme.Bg1, 90f)) g.FillRectangle(brush, ClientRectangle);
        using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        Theme.DrawLogo(g, new RectangleF(27, 31, 26, 26));
        Theme.DrawText(g, Theme.ProductName, Theme.Title, Theme.TextPrimary, new RectangleF(58, 28, 170, 32));
        Theme.DrawText(g, I18n.T("Setup", "Настройка"), Theme.Caption, Theme.TextTertiary, new RectangleF(58, 56, 170, 18));
        float y = 112;
        for (int i = 0; i < _steps.Length; i++)
        {
            bool done = i < _current, cur = i == _current;
            Color c = done ? Theme.Success : cur ? Theme.Accent : Theme.TextTertiary;
            var circle = new RectangleF(28, y, 26, 26);
            if (cur) Theme.FillRound(g, circle, 13, Theme.Accent); else Theme.StrokeRound(g, circle, 13, done ? Theme.Success : Theme.BorderStrong, 1.5f);
            Theme.DrawText(g, done ? "✓" : (i + 1).ToString(), Theme.SmallStrong, cur ? Theme.TextOnAccent : c, circle, StringAlignment.Center);
            Theme.DrawText(g, I18n.T(_steps[i].En, _steps[i].Ru), cur ? Theme.BodyStrong : Theme.Body, cur ? Theme.TextPrimary : done ? Theme.TextSecondary : Theme.TextTertiary, new RectangleF(66, y, 160, 26));
            if (i < _steps.Length - 1) using (var pen = new Pen(Theme.WithAlpha(done ? Theme.Success : Theme.BorderStrong, 120), 1.5f)) g.DrawLine(pen, 41, y + 30, 41, y + 46);
            y += 50;
        }
        _en = new RectangleF(28, Height - 48, 38, 26); _ru = new RectangleF(70, Height - 48, 38, 26);
        Theme.FillRound(g, I18n.IsRu ? _ru : _en, 8, Theme.AccentSubtle);
        Theme.DrawText(g, "EN", Theme.SmallStrong, I18n.IsRu ? Theme.TextTertiary : Theme.AccentText, _en, StringAlignment.Center);
        Theme.DrawText(g, "RU", Theme.SmallStrong, I18n.IsRu ? Theme.AccentText : Theme.TextTertiary, _ru, StringAlignment.Center);
    }
}

// ------------------------------------------------------------------ form

/// <summary>Guided setup. Package mode (started from the unpacked zip): explain, choose agent/manual, install.
/// Connect mode (inside the installed tray): choose the AI, set up the connection, hand off to the client.</summary>
internal sealed partial class SetupWizardForm : Form
{
    private const int BodyWidth = 620;
    private readonly string _packageRoot;
    private readonly TrayContext? _ctx;
    private bool Package => _ctx == null;
    private readonly WizardRail _rail;
    private readonly Label _title = new(), _subtitle = new();
    private readonly FlowLayoutPanel _body, _footerLeft, _footerRight;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private string _step = "";
    private Action? _tick;
    private string _client = "", _provider = "";
    private bool _busy;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public SetupWizardForm(string packageRoot, string initialStep) : this(packageRoot, (TrayContext?)null) { if (initialStep.Length > 0) Show(initialStep); }
    public SetupWizardForm(TrayContext ctx) : this("", ctx) { }
    private SetupWizardForm(string packageRoot, TrayContext? ctx)
    {
        _packageRoot = packageRoot; _ctx = ctx;
        Theme.ApplyLogo(this);
        if (Package)
        {
            string[] args = Environment.GetCommandLineArgs(); int langArg = Array.IndexOf(args, "--lang");
            I18n.CurrentLanguage = langArg > 0 && langArg + 1 < args.Length ? args[langArg + 1] : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        }
        Text = Theme.ProductName + I18n.T(" — Setup", " — Настройка");
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; ClientSize = new Size(960, 680);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Bg1; ForeColor = Theme.TextPrimary; Font = Theme.Body;
        DoubleBuffered = true;

        _rail = new WizardRail(Package
            ? [("What it is", "Что это"), ("How to install", "Способ"), ("Install", "Установка")]
            : [("Which AI", "Какой AI"), ("Connection", "Подключение"), ("Your AI client", "Клиент")], SwitchLanguage);
        var header = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = Theme.Bg1 };
        _title.Font = Theme.Display; _title.ForeColor = Theme.TextPrimary; _title.AutoSize = true; _title.MaximumSize = new Size(BodyWidth + 40, 0); _title.Location = new Point(40, 30);
        _subtitle.Font = Theme.Body; _subtitle.ForeColor = Theme.TextSecondary; _subtitle.AutoSize = true; _subtitle.MaximumSize = new Size(BodyWidth + 40, 0); _subtitle.Location = new Point(40, 66);
        header.Controls.Add(_title); header.Controls.Add(_subtitle);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 70, BackColor = Theme.Bg1, Padding = new Padding(36, 10, 30, 0) };
        _footerLeft = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 200, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg1, Padding = new Padding(0, 6, 0, 0) };
        _footerRight = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 460, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg1, Padding = new Padding(0, 6, 0, 0) };
        footer.Controls.Add(_footerLeft); footer.Controls.Add(_footerRight);
        footer.Paint += (_, e) => { using var pen = new Pen(Theme.Divider); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        _body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(40, 6, 20, 20), BackColor = Theme.Bg1 };
        Controls.Add(_body); Controls.Add(header); Controls.Add(footer); Controls.Add(_rail); _body.BringToFront();

        _timer.Tick += (_, _) => _tick?.Invoke();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _ctx?.MarkSetupCompleted(); };
        HandleCreated += (_, _) => { try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { } };
        Show(Package ? "welcome" : "client");
    }

    private void SwitchLanguage(string lang)
    {
        if (Package) I18n.CurrentLanguage = lang; else _ctx!.ApplyLanguage(lang);
        Text = Theme.ProductName + I18n.T(" — Setup", " — Настройка");
        if (!_busy) Show(_step); else _rail.Invalidate();
    }

    // ---------------------------------------------------------------- body helpers
    private void Clear()
    {
        _tick = null;
        foreach (Control c in _body.Controls.Cast<Control>().ToArray()) c.Dispose();
        _body.Controls.Clear(); _footerLeft.Controls.Clear(); _footerRight.Controls.Clear();
    }
    private void Head(string en, string ru, string subEn, string subRu) { _title.Text = I18n.T(en, ru); _subtitle.Text = I18n.T(subEn, subRu); }
    private Label Para(string en, string ru, bool strong = false, Color? color = null, int bottom = 12)
    {
        var l = new Label { Text = I18n.T(en, ru), AutoSize = true, MaximumSize = new Size(BodyWidth, 0), Font = strong ? Theme.BodyStrong : Theme.Body, ForeColor = color ?? Theme.TextPrimary, Margin = new Padding(0, 0, 0, bottom) };
        _body.Controls.Add(l); return l;
    }
    private Label Small(string en, string ru, Color? color = null, int bottom = 10)
    {
        var l = new Label { Text = I18n.T(en, ru), AutoSize = true, MaximumSize = new Size(BodyWidth, 0), Font = Theme.Small, ForeColor = color ?? Theme.TextSecondary, Margin = new Padding(0, 0, 0, bottom) };
        _body.Controls.Add(l); return l;
    }
    private void Bullets(params (string En, string Ru)[] items)
    {
        foreach (var (en, ru) in items)
            _body.Controls.Add(new Label { Text = (char.IsDigit(en[0]) ? "" : "•   ") + I18n.T(en, ru), AutoSize = true, MaximumSize = new Size(BodyWidth - 16, 0), Font = Theme.Body, ForeColor = Theme.TextPrimary, Margin = new Padding(16, 0, 0, 6) });
        Space(8);
    }
    private void Space(int h) => _body.Controls.Add(new Panel { Height = h, Width = 10, Margin = Padding.Empty });
    private WizardNote Note(string titleEn, string titleRu, string en, string ru, Color tone) { var n = new WizardNote(I18n.T(titleEn, titleRu), I18n.T(en, ru), tone, BodyWidth); _body.Controls.Add(n); return n; }
    private WizardChoice Choice(string titleEn, string titleRu, string en, string ru, Action onClick, bool primary = false) { var c = new WizardChoice(I18n.T(titleEn, titleRu), I18n.T(en, ru), onClick, BodyWidth, primary); _body.Controls.Add(c); return c; }
    private WizardChecklist Checklist(params WizardStep[] steps) { var c = new WizardChecklist(BodyWidth); c.SetItems(steps); _body.Controls.Add(c); return c; }
    private FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, MaximumSize = new Size(BodyWidth + 10, 0), Margin = new Padding(0, 0, 0, 6), BackColor = Theme.Bg1 };
        foreach (var c in controls) row.Controls.Add(c);
        _body.Controls.Add(row); return row;
    }
    private WizardButton Btn(string en, string ru, Action action, ButtonKind kind = ButtonKind.Secondary) => new(I18n.T(en, ru), action, kind);
    /// <summary>Read-only value with a Copy button; multiline for long text.</summary>
    private TextBox CodeBox(string text, bool multiline = false)
    {
        var box = new TextBox { Text = text, ReadOnly = true, Multiline = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None, Font = Theme.FontMono(10), BackColor = Theme.SurfaceRaised, ForeColor = Theme.InfoText, BorderStyle = BorderStyle.FixedSingle, Width = multiline ? BodyWidth : BodyWidth - 160, Height = multiline ? 118 : 34, Margin = new Padding(0, 4, 10, 10) };
        Label? status = null;
        var copy = Btn("Copy", "Скопировать", () => { try { Clipboard.SetText(box.Text); if (status != null) status.Text = I18n.T("Copied.", "Скопировано."); } catch { } }, ButtonKind.Primary);
        if (multiline) { _body.Controls.Add(box); Row(copy); } else Row(box, copy);
        status = Small("", "", Theme.SuccessText, 6);
        return box;
    }
    private void Footer(WizardButton? left, params WizardButton[] right)
    {
        if (left != null) _footerLeft.Controls.Add(left);
        foreach (var b in right.Reverse()) _footerRight.Controls.Add(b);
    }
    private static bool OpenUrl(string url) => DesktopLauncher.OpenUrl(url);
    private static string Dir(string path) => path.Replace(SetupLaunch.LocalRoot, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase).Replace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- steps
    public void Navigate(string step) { int c = step.IndexOf(':'); if (c > 0) { _provider = step[(c + 1)..]; step = step[..c]; } Show(step); }
    private void Show(string step)
    {
        _step = step; Clear();
        _rail.SetCurrent(step switch { "welcome" or "client" => 0, "path" or "agent" or "local" or "internet" or "provider" or "other" or "compare" => 1, _ => 2 });
        switch (step)
        {
            case "welcome": StepWelcome(); break;
            case "path": StepPath(); break;
            case "agent": StepAgent(); break;
            case "install": StepInstall(); break;
            case "exists": StepExists(); break;
            case "client": StepClient(); break;
            case "local": StepLocal(); break;
            case "internet": StepInternet(); break;
            case "provider": StepProvider(); break;
            case "compare": StepCompare(); break;
            case "other": StepOther(); break;
            case "handoff": StepHandoff(); break;
            case "done": StepDone(); break;
        }
        _rail.Invalidate(); _body.AutoScrollPosition = new Point(0, 0);
    }

    private void StepWelcome()
    {
        Head("Welcome to ZeroIDE MCP", "Это ZeroIDE MCP",
            "Your AI assistant gets hands on this PC, only while you allow it.", "AI-помощник получает руки на этом компьютере, но только пока ты это разрешаешь.");
        Para("ZeroIDE MCP is a small Windows tray app. It gives your AI (ChatGPT, Claude, Notion, Cursor, Claude Code or any other client that supports MCP) tools to work with files and run commands on this computer. You can keep using your familiar chat in the browser: it will be able to see and do things here.",
            "ZeroIDE MCP — небольшое приложение в трее Windows. Оно даёт твоему AI (ChatGPT, Claude, Notion, Cursor, Claude Code и любому другому клиенту с поддержкой MCP) инструменты для работы с файлами и командами на этом компьютере. Можно остаться в привычном чате в браузере: он сможет видеть и делать что-то здесь.", bottom: 16);
        Para("What the AI can do here", "Что AI сможет делать здесь", true, bottom: 8);
        Bullets(("Run PowerShell commands and follow long tasks", "Выполнять команды PowerShell и следить за долгими задачами"),
            ("Read, create, edit and search files", "Читать, создавать, править и искать файлы"),
            ("Take screenshots and send you messages", "Делать скриншоты и присылать тебе сообщения"),
            ("Everything is written to an action log", "Все действия записываются в журнал"));
        Para("You stay in control", "Ты управляешь доступом", true, bottom: 8);
        Bullets(("The server is off by default: you switch it on in the tray for as long as you need", "Сервер выключен по умолчанию: ты включаешь его в трее на нужное время"),
            ("Writing files and running commands wait for your confirmation", "Запись файлов и команды ждут твоего подтверждения"),
            ("Each client gets its own access that you can revoke at any time", "У каждого клиента свой доступ, его можно отозвать в любой момент"));
        Note("Keep in mind", "Важно", "This is not a sandbox: allowed commands run with your Windows user's permissions.", "Это не изолированная песочница: разрешённые команды выполняются с правами твоего пользователя Windows.", Theme.Warning);
        Footer(null, Btn("Next", "Далее", () => Show("path"), ButtonKind.Primary));
    }

    private void StepPath()
    {
        Head("How would you like to install?", "Как будем устанавливать?", "Both ways lead to the same result. Pick what is easier for you.", "Оба пути ведут к одному результату. Выбери, что удобнее.");
        Space(6);
        Choice("Manually, with guidance", "Вручную, с подсказками",
            "This wizard installs everything itself and asks a couple of questions: which AI to connect and how. Nothing to type.",
            "Мастер установит всё сам и задаст пару вопросов: какой AI подключаем и как. Ничего набирать не придётся.",
            () => Show(SetupLaunch.IsInstalled ? "exists" : "install"), true);
        Choice("With an AI agent on this PC", "Через AI-агента на этом ПК",
            "Already have Claude Code, Cursor, Codex or another agent that can run commands here? Copy a task for it and it will do the installation.",
            "Уже есть Claude Code, Cursor, Codex или другой агент, который умеет выполнять команды здесь? Скопируй задание, и он всё установит сам.",
            () => Show("agent"));
        Footer(Btn("Back", "Назад", () => Show("welcome"), ButtonKind.Subtle));
    }

    private void StepAgent()
    {
        Head("Task for your agent", "Задание для агента", "Paste it into the chat of an agent running on this computer.", "Вставь его в чат агента, запущенного на этом компьютере.");
        string manual = Path.Combine(_packageRoot, "AGENT_INSTALL.md");
        string prompt = I18n.T(
            "Read the file " + manual + " and guide me through installing ZeroIDE MCP on this computer. Follow that instruction exactly, use the shipped scripts, explain in plain language and ask one question at a time.",
            "Прочитай файл " + manual + " и проведи меня через установку ZeroIDE MCP на этот компьютер. Делай всё строго по этой инструкции, используй готовые скрипты из пакета, объясняй простыми словами и задавай по одному вопросу за раз.");
        Space(6);
        CodeBox(prompt, true);
        Para("The agent will read the instruction from the package, ask which AI you want to connect and set everything up. Keys and tokens are entered only in this app's windows, never in the chat.",
            "Агент прочитает инструкцию из пакета, спросит, какой AI подключать, и всё настроит. Ключи и токены вводятся только в окна приложения, не в чат.");
        Small("You can come back to this wizard at any time: just start this file again.", "К мастеру можно вернуться в любой момент: просто запусти этот файл снова.");
        Footer(Btn("Back", "Назад", () => Show("path"), ButtonKind.Subtle), Btn("Close", "Закрыть", Close));
    }

    private void StepExists()
    {
        Head("Already installed", "Уже установлено", "ZeroIDE MCP is already in " + Dir(SetupLaunch.InstallDir) + ".", "ZeroIDE MCP уже есть в " + Dir(SetupLaunch.InstallDir) + ".");
        Space(6);
        Choice("Open the installed app", "Открыть установленное приложение", "Start it from here or from the Start Menu. The connection wizard lives in the app, under Connection.", "Запустить отсюда или из меню «Пуск». Мастер подключения живёт в приложении, в разделе «Подключение».",
            () => { if (SetupLaunch.InstalledAppRunning()) Close(); else LaunchInstalledAndClose(); }, true);
        Choice("Update to the version in this package", "Обновить до версии из этого пакета", "Settings, access tokens and connections stay. Close the running app first: tray icon → Exit.", "Настройки, токены и подключения сохранятся. Перед обновлением закрой приложение: иконка в трее → Выход.",
            () => { Clear(); _step = "install"; StepInstall(true); });
        Footer(Btn("Back", "Назад", () => Show("path"), ButtonKind.Subtle), Btn("Close", "Закрыть", Close));
    }

    private void LaunchInstalledAndClose()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(SetupLaunch.InstallDir, SetupLaunch.ExeName)) { UseShellExecute = true, WorkingDirectory = SetupLaunch.InstallDir }); } catch { }
        Close();
    }

    private void StepInstall(bool update = false)
    {
        // An installation under the old name (AgentPower) is updated in place: the installer moves its state over.
        bool legacy = !update && Directory.Exists(SetupLaunch.LegacyInstallDir);
        bool legacyRunning = legacy && SetupLaunch.LegacyAppRunning();
        bool asUpdate = update || legacy;
        Head(asUpdate ? "Update" : "Install", asUpdate ? "Обновление" : "Установка",
            "Installs into your user folder. No administrator rights needed.", "Ставится в папку пользователя. Права администратора не нужны.");
        var missing = SetupLaunch.MissingPackageFiles(_packageRoot);
        bool ports = update || (SetupLaunch.PortFree(39011) && SetupLaunch.PortFree(39012));
        bool running = update && SetupLaunch.InstalledAppRunning();
        bool ok = missing.Length == 0 && ports && !running && !legacyRunning;
        Space(6);
        var items = new List<WizardStep>();
        if (legacy) items.Add(new(legacyRunning
            ? I18n.T("Close the earlier build first: its tray icon → Exit. Then check again.", "Сначала закрой прежнюю сборку: её иконка в трее → Выход. Потом проверь снова.")
            : I18n.T("An earlier build was found in ", "Найдена прежняя сборка в ") + Dir(SetupLaunch.LegacyInstallDir) + I18n.T(": its settings, access tokens, connection address and logs move into this installation; the old folder is then removed.", ": её настройки, токены доступа, адрес подключения и журнал переедут в эту установку; старая папка затем будет удалена."),
            legacyRunning ? "current" : "done"));
        items.AddRange([
            new(missing.Length == 0 ? I18n.T("Package files are complete", "Файлы пакета на месте") : I18n.T("Package is incomplete: ", "В пакете не хватает: ") + string.Join(", ", missing) + I18n.T(". Download zeroide-mcp.zip from Releases (not “Source code”).", ". Скачай zeroide-mcp.zip со страницы Releases (не «Source code»)."), missing.Length == 0 ? "done" : "current"),
            new(ports ? I18n.T("Ports 39011 and 39012 are free", "Порты 39011 и 39012 свободны") : I18n.T("Port 39011 or 39012 is busy. Close the program that uses it (or an older ZeroIDE MCP) and come back.", "Порт 39011 или 39012 занят. Закрой программу, которая его использует (или старый ZeroIDE MCP), и вернись сюда."), ports ? "done" : "current"),
            new(running ? I18n.T("Close ZeroIDE MCP first: tray icon → Exit.", "Сначала закрой ZeroIDE MCP: иконка в трее → Выход.") : !update && SetupLaunch.HasUnfinishedInstall ? I18n.T("An unfinished installation was found in ", "Найдена незавершённая установка в ") + Dir(SetupLaunch.InstallDir) + I18n.T("; it will be completed.", "; она будет завершена.") : I18n.T("Folder: ", "Папка: ") + Dir(SetupLaunch.InstallDir), running ? "current" : "done"),
            new(I18n.T("Internet is needed once, to download Node.js (about 30 MB) from nodejs.org", "Интернет нужен один раз: скачать Node.js (около 30 МБ) с nodejs.org"), "todo")]);
        Checklist(items.ToArray());
        Para(asUpdate
            ? "What happens: the app, server and helper scripts are replaced with this version. Your settings, access tokens, connections and logs stay."
            : "What happens: the app and server are copied, Node.js is downloaded and its checksum verified, a local key is created (it is never shown), and a Start Menu shortcut is added. Nothing starts and nothing is added to autostart.",
            asUpdate
            ? "Что произойдёт: приложение, сервер и вспомогательные скрипты заменятся на эту версию. Настройки, токены доступа, подключения и журналы сохранятся."
            : "Что произойдёт: скопируем приложение и сервер, скачаем Node.js и проверим его контрольную сумму, создадим локальный ключ (он никогда не показывается), добавим ярлык в меню «Пуск». Ничего не запускается и не попадает в автозагрузку.");
        var status = Small("", "", Theme.TextPrimary, 6);
        var progress = new WizardProgress(BodyWidth) { Visible = false }; _body.Controls.Add(progress);
        var log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = BodyWidth, Height = 150, Font = Theme.FontMono(9), BackColor = Theme.Surface, ForeColor = Theme.TextSecondary, BorderStyle = BorderStyle.FixedSingle, Visible = false, Margin = new Padding(0, 0, 0, 10) };
        _body.Controls.Add(log);
        var back = Btn("Back", "Назад", () => Show(update ? "exists" : "path"), ButtonKind.Subtle);
        if (ok)
        {
            WizardButton? start = null;
            string args = update ? "-Update" : legacy ? "-MigrateFrom \"" + SetupLaunch.LegacyInstallDir + "\"" : "";
            start = Btn(asUpdate ? "Update" : "Install", asUpdate ? "Обновить" : "Установить", () => _ = RunInstallAsync(asUpdate, args, status, progress, log, start!, back), ButtonKind.Primary);
            Footer(back, start, Btn("Cancel", "Отмена", Close));
        }
        else Footer(back, Btn("Check again", "Проверить снова", () => { Clear(); StepInstall(update); }, ButtonKind.Primary));
    }

    private async Task RunInstallAsync(bool update, string args, Label status, WizardProgress progress, TextBox log, WizardButton start, WizardButton back)
    {
        _busy = true; start.Enabled = false; back.Enabled = false; progress.Visible = true; log.Visible = true; log.Clear();
        _tick = progress.Tick;
        string[] phases = ["Moving settings", "Installing into", "Updating", "Copying server", "Copying tray", "Copying agent", "Portable Node.js", "Server dependencies", "Generating config.env", "tray-config.json", "Start Menu shortcut", "Removing the earlier", "Done"];
        (string En, string Ru)[] labels = [("Moving settings, tokens and connections…", "Переносим настройки, токены и подключения…"), ("Preparing the folder…", "Готовим папку…"), ("Preparing the update…", "Готовим обновление…"), ("Copying the server…", "Копируем сервер…"), ("Copying the app…", "Копируем приложение…"), ("Copying helper scripts…", "Копируем вспомогательные скрипты…"), ("Downloading Node.js — the longest step…", "Скачиваем Node.js — это самый долгий шаг…"), ("Checking server dependencies…", "Проверяем зависимости сервера…"), ("Creating the local key…", "Создаём локальный ключ…"), ("Writing settings…", "Записываем настройки…"), ("Adding the Start Menu shortcut…", "Добавляем ярлык в меню «Пуск»…"), ("Removing the earlier folder…", "Удаляем старую папку…"), ("Done", "Готово")];
        status.Text = I18n.T("Starting…", "Запускаем…");
        var tail = new StringBuilder(); int exit = -1;
        try
        {
            var p = SetupLaunch.StartScript(Path.Combine(_packageRoot, "installer", "install.ps1"), args, _packageRoot);
            void OnLine(string? line)
            {
                if (line == null) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        if (IsDisposed || log.IsDisposed) return;
                        log.AppendText(line + Environment.NewLine); tail.AppendLine(line);
                        if (!line.StartsWith("=== ")) return;
                        for (int i = 0; i < phases.Length; i++)
                            if (line.Contains(phases[i], StringComparison.OrdinalIgnoreCase)) { status.Text = I18n.T(labels[i].En, labels[i].Ru); progress.Value = (i + 1f) / phases.Length; progress.Invalidate(); break; }
                    });
                }
                catch { }
            }
            p.OutputDataReceived += (_, e) => OnLine(e.Data); p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            await p.WaitForExitAsync(); exit = p.ExitCode;
        }
        catch (Exception ex) { if (!IsDisposed) log.AppendText("ERROR: " + ex.Message + Environment.NewLine); }
        _tick = null; _busy = false;
        if (IsDisposed) return;
        if (exit == 0 && File.Exists(Path.Combine(SetupLaunch.InstallDir, SetupLaunch.ExeName)))
        {
            progress.Value = 1; progress.Invalidate(); log.Visible = false; status.Visible = false;
            Head(update ? "Updated" : "Installed", update ? "Обновлено" : "Установлено", "", "");
            var note = Note("Done", "Готово",
                update ? "ZeroIDE MCP is up to date. Open it to continue." : "ZeroIDE MCP is installed. Next, the app asks which AI you want to connect and sets up the connection.",
                update ? "ZeroIDE MCP обновлён. Открой приложение, чтобы продолжить." : "ZeroIDE MCP установлен. Дальше приложение спросит, какой AI подключаем, и настроит подключение.", Theme.Success);
            _body.Controls.SetChildIndex(note, 0);
            _footerLeft.Controls.Clear(); _footerRight.Controls.Clear();
            Footer(null, Btn("Open ZeroIDE MCP", "Открыть ZeroIDE MCP", LaunchInstalledAndClose, ButtonKind.Primary));
        }
        else
        {
            progress.Visible = false; back.Enabled = true;
            status.Text = I18n.T("The installation did not finish. The log shows the failing step; nothing from a failed download was unpacked.", "Установка не завершилась. В журнале видно, на каком шаге; из неудачной загрузки ничего не распаковано.");
            status.ForeColor = Theme.DangerText;
            _footerRight.Controls.Clear();
            Footer(null, Btn("Try again", "Повторить", () => { Clear(); StepInstall(update); }, ButtonKind.Primary), Btn("Copy log", "Скопировать журнал", () => { try { Clipboard.SetText(tail.ToString()); } catch { } }));
        }
    }
}
