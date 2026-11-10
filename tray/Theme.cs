using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

// ============================================================================
// ZeroIDE design system — shared tokens + GDI+ helpers for every tray surface.
// Fluent-2-inspired dark graphite with a single accent. No user-visible strings
// live here (brand name excepted); any future text must go through I18n.T(en, ru).
// All Font instances exposed here are CACHED and SHARED: never Dispose them.
// ============================================================================

/// <summary>Visual role of a button.</summary>
internal enum ButtonKind { Primary, Secondary, Subtle, Danger, Success, Warning, Outline, DangerSolid }

/// <summary>Interaction state of a custom-drawn control.</summary>
internal enum ButtonState { Normal, Hover, Pressed, Disabled }

/// <summary>Line icons drawn by Theme.DrawIcon (24-unit grid, round caps/joins).</summary>
internal enum IconKind
{
    Settings, Tools, Access, Approvals, Notifications, Connection, Audit, About,
    Power, Play, Stop, Folder, Terminal, File, Edit, Eye, Check, Close,
    ChevronDown, ChevronRight, Clock, Lock, Warning, Copy, Refresh, Plus, Trash,
    Language, Sound, Menu, Globe, Link, ChevronLeft, ChevronUp, Minus, Search, Info,
    Home,
    Shield, ShieldCheck, Image, Lightbulb, Gauge, Server, User, Sliders, FolderLock,
}

/// <summary>Colour/metric set for toast & approval cards (card style: glass | light | compact | neon).</summary>
internal sealed record CardPalette(
    string Name,
    Color Surface,
    Color SurfaceRaised,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color Accent,
    Color Success,
    Color Warning,
    Color Danger,
    Color Info,
    int ShadowAlpha,
    Color? GlowColor,
    float Radius,
    float Padding,
    bool TopHighlight,
    bool IsLight);

internal static class Theme
{
    public const string ProductName = "ZeroIDE MCP";

    // ------------------------------------------------------------------ colours (dark, default)
    public static readonly Color Bg0 = Color.FromArgb(0x0D, 0x0F, 0x14);            // sidebar, deepest
    public static readonly Color Bg1 = Color.FromArgb(0x13, 0x16, 0x1C);            // content / window
    public static readonly Color BgGradTop = Color.FromArgb(0x16, 0x1A, 0x22);      // window gradient (top)
    public static readonly Color BgGradBottom = Color.FromArgb(0x11, 0x14, 0x1A);   // window gradient (bottom)
    public static readonly Color Surface = Color.FromArgb(0x18, 0x1C, 0x24);        // cards
    public static readonly Color SurfaceRaised = Color.FromArgb(0x23, 0x28, 0x35);  // inputs, idle pills, menus
    public static readonly Color SurfaceHover = Color.FromArgb(0x2A, 0x30, 0x3E);
    public static readonly Color SurfacePressed = Color.FromArgb(0x31, 0x38, 0x48);
    public static readonly Color Trough = Color.FromArgb(0x2A, 0x2F, 0x3B);         // toggle off / progress track
    public static readonly Color MenuBg = Color.FromArgb(0x1E, 0x21, 0x28);         // classic ContextMenuStrip

    public static readonly Color Border = Color.FromArgb(18, 255, 255, 255);        // white 7%
    public static readonly Color BorderStrong = Color.FromArgb(31, 255, 255, 255);  // white 12%
    public static readonly Color Divider = Color.FromArgb(15, 255, 255, 255);       // white 6%
    public static readonly Color HoverOverlay = Color.FromArgb(15, 255, 255, 255);  // white 6%
    public static readonly Color PressedOverlay = Color.FromArgb(26, 255, 255, 255);// white 10%
    public static readonly Color TopHighlight = Color.FromArgb(10, 255, 255, 255);  // white 4%

    public static readonly Color TextPrimary = Color.FromArgb(0xF2, 0xF4, 0xF7);
    public static readonly Color TextSecondary = Color.FromArgb(0xA9, 0xB0, 0xBE);
    public static readonly Color TextTertiary = Color.FromArgb(0x7D, 0x85, 0x96);   // captions only, >= 9pt
    public static readonly Color TextDisabled = Color.FromArgb(0x5A, 0x61, 0x6E);
    public static readonly Color TextOnAccent = Color.FromArgb(0xFF, 0xFF, 0xFF);

    public static readonly Color Accent = Color.FromArgb(0x5B, 0x6C, 0xFF);
    public static readonly Color AccentHover = Color.FromArgb(0x7A, 0x87, 0xFF);
    public static readonly Color AccentPressed = Color.FromArgb(0x4A, 0x59, 0xE6);
    public static readonly Color AccentSubtle = Color.FromArgb(41, 0x5B, 0x6C, 0xFF); // 16%
    public static readonly Color AccentText = Color.FromArgb(0xA9, 0xB3, 0xFF);
    public static readonly Color AccentGradTop = Color.FromArgb(0x6E, 0x7D, 0xFF);   // primary button gradient
    public static readonly Color AccentGradBottom = Color.FromArgb(0x4C, 0x5C, 0xF2);
    public static readonly Color AccentGlow = Color.FromArgb(70, 0x5B, 0x6C, 0xFF);  // primary button glow

    public static readonly Color Success = Color.FromArgb(0x3D, 0xD6, 0x8C);
    public static readonly Color SuccessText = Color.FromArgb(0x7C, 0xE8, 0xB2);
    public static readonly Color SuccessSubtle = Color.FromArgb(41, 0x3D, 0xD6, 0x8C);
    public static readonly Color Warning = Color.FromArgb(0xF5, 0xB5, 0x44);
    public static readonly Color WarningText = Color.FromArgb(0xFF, 0xCE, 0x7A);
    public static readonly Color WarningSubtle = Color.FromArgb(41, 0xF5, 0xB5, 0x44);
    public static readonly Color Danger = Color.FromArgb(0xFF, 0x6B, 0x6B);
    public static readonly Color DangerText = Color.FromArgb(0xFF, 0x9C, 0x9C);
    public static readonly Color DangerSubtle = Color.FromArgb(41, 0xFF, 0x6B, 0x6B);
    public static readonly Color Info = Color.FromArgb(0x4C, 0xB4, 0xFF);
    public static readonly Color InfoText = Color.FromArgb(0x96, 0xD1, 0xFF);
    public static readonly Color InfoSubtle = Color.FromArgb(41, 0x4C, 0xB4, 0xFF);
    public static readonly Color Violet = Color.FromArgb(0xA7, 0x8B, 0xFA);         // category tint (audit / misc)
    public static readonly Color Teal = Color.FromArgb(0x2D, 0xD4, 0xBF);           // category tint (network)

    // Tool permission states
    public static readonly Color StateAllow = Success;
    public static readonly Color StateAsk = Warning;
    public static readonly Color StateDeny = Danger;

    // Light palette ('light' card theme only)
    public static readonly Color LightSurface = Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly Color LightSurfaceRaised = Color.FromArgb(0xF3, 0xF4, 0xF7);
    public static readonly Color LightBorder = Color.FromArgb(26, 0, 0, 0);          // black 10%
    public static readonly Color LightTextPrimary = Color.FromArgb(0x16, 0x18, 0x1D);
    public static readonly Color LightTextSecondary = Color.FromArgb(0x4B, 0x52, 0x60);
    public static readonly Color LightAccent = Color.FromArgb(0x3F, 0x51, 0xF5);
    public static readonly Color LightSuccessText = Color.FromArgb(0x1E, 0x8A, 0x50);
    public static readonly Color LightWarningText = Color.FromArgb(0xA8, 0x64, 0x00);
    public static readonly Color LightDangerText = Color.FromArgb(0xC6, 0x2F, 0x36);
    public static readonly Color LightInfoText = Color.FromArgb(0x1F, 0x6F, 0xB2);

    // Neon palette ('neon' card theme only)
    public static readonly Color NeonSurface = Color.FromArgb(250, 0x09, 0x0B, 0x10);
    public static readonly Color NeonAccent = Color.FromArgb(0x2E, 0xF2, 0xD0);
    public static readonly Color NeonGlow = Color.FromArgb(77, 0x2E, 0xF2, 0xD0);    // 30%
    public static readonly Color NeonTextPrimary = Color.FromArgb(0xEF, 0xFF, 0xFB);
    public static readonly Color NeonTextSecondary = Color.FromArgb(0x9F, 0xC9, 0xC1);

    // ------------------------------------------------------------------ radii / spacing / metrics
    public const float RadiusWindow = 16f;
    public const float RadiusCard = 13f;
    public const float RadiusControl = 10f;
    public const float RadiusButton = 11f;
    public const float RadiusSmall = 7f;
    public const float RadiusTile = 10f;
    public static float RadiusPill(float height) => height / 2f;

    public const int S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20, S6 = 24, S8 = 32;

    public const float ButtonHeight = 34f;
    public const float ButtonHeightSmall = 28f;
    public const float RowMinHeight = 52f;
    public const float NavItemHeight = 36f;
    public const float ToggleWidth = 38f;
    public const float ToggleHeight = 22f;
    public const float IconTileSize = 36f;
    public const float IconSize = 16f;
    public const float MinFontSize = 9f;

    // ------------------------------------------------------------------ typography
    private static readonly object FontLock = new();
    // Cached fonts are SHARED for the lifetime of the process — callers must NEVER dispose them (a disposed cached font breaks every later draw with "Parameter is not valid").
    private static readonly Dictionary<string, System.Drawing.Font> FontCache = new();
    private static bool _fontsProbed;
    private static string _familyText = "Segoe UI";
    private static string _familyTextSemibold = "Segoe UI Semibold";
    private static string _familyDisplay = "Segoe UI";
    private static string _familyDisplaySemibold = "Segoe UI Semibold";
    private static string _familySmall = "Segoe UI";
    private static string _familySmallSemibold = "Segoe UI Semibold";
    private static string _familyMono = "Consolas";
    private static bool _semiboldViaBold;
    private static bool _hasVariable;
    private static bool _hasCascadia;

    /// <summary>True when Segoe UI Variable (Text/Display) is installed and used.</summary>
    public static bool HasSegoeVariable { get { ProbeFonts(); return _hasVariable; } }
    /// <summary>True when Cascadia Mono is installed and used for Mono fonts.</summary>
    public static bool HasCascadiaMono { get { ProbeFonts(); return _hasCascadia; } }
    public static string BodyFamilyName { get { ProbeFonts(); return _familyText; } }
    public static string MonoFamilyName { get { ProbeFonts(); return _familyMono; } }

    private static void ProbeFonts()
    {
        if (_fontsProbed) return;
        lock (FontLock)
        {
            if (_fontsProbed) return;
            try
            {
                using var coll = new InstalledFontCollection();
                var names = new HashSet<string>(coll.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

                string? FindSemibold(string baseName) =>
                    names.FirstOrDefault(n => n.StartsWith(baseName + " Semib", StringComparison.OrdinalIgnoreCase));

                if (names.Contains("Segoe UI Variable Text") && names.Contains("Segoe UI Variable Display"))
                {
                    _hasVariable = true;
                    _familyText = "Segoe UI Variable Text";
                    _familyDisplay = "Segoe UI Variable Display";
                    _familySmall = names.Contains("Segoe UI Variable Small") ? "Segoe UI Variable Small" : _familyText;

                    string? ts = FindSemibold(_familyText);
                    string? ds = FindSemibold(_familyDisplay);
                    string? ss = FindSemibold(_familySmall);
                    if (ts != null && ds != null)
                    {
                        _familyTextSemibold = ts;
                        _familyDisplaySemibold = ds;
                        _familySmallSemibold = ss ?? ts;
                    }
                    else
                    {
                        // Named semibold instances not exposed to GDI+: use the variable family with Bold.
                        _familyTextSemibold = _familyText;
                        _familyDisplaySemibold = _familyDisplay;
                        _familySmallSemibold = _familySmall;
                        _semiboldViaBold = true;
                    }
                }
                else
                {
                    _familyText = _familyDisplay = _familySmall = "Segoe UI";
                    if (names.Contains("Segoe UI Semibold"))
                    {
                        _familyTextSemibold = _familyDisplaySemibold = _familySmallSemibold = "Segoe UI Semibold";
                    }
                    else
                    {
                        _familyTextSemibold = _familyDisplaySemibold = _familySmallSemibold = "Segoe UI";
                        _semiboldViaBold = true;
                    }
                }

                if (names.Contains("Cascadia Mono"))
                {
                    _hasCascadia = true;
                    _familyMono = "Cascadia Mono";
                }
                else
                {
                    _familyMono = "Consolas";
                }
            }
            catch
            {
                // keep defaults
            }
            _fontsProbed = true;
        }
    }

    private enum FontRole { Text, Display, Small, Mono }

    private static System.Drawing.Font GetFont(FontRole role, float size, bool semibold)
    {
        ProbeFonts();
        size = Math.Max(MinFontSize, size);
        string key = $"{role}|{size:0.###}|{(semibold ? 1 : 0)}";
        lock (FontLock)
        {
            if (FontCache.TryGetValue(key, out var cached)) return cached;
            string family;
            FontStyle style = FontStyle.Regular;
            switch (role)
            {
                case FontRole.Display:
                    family = semibold ? _familyDisplaySemibold : _familyDisplay;
                    break;
                case FontRole.Small:
                    family = semibold ? _familySmallSemibold : _familySmall;
                    break;
                case FontRole.Mono:
                    family = _familyMono;
                    if (semibold) style = FontStyle.Bold;
                    break;
                default:
                    family = semibold ? _familyTextSemibold : _familyText;
                    break;
            }
            if (semibold && role != FontRole.Mono && _semiboldViaBold) style = FontStyle.Bold;

            System.Drawing.Font f;
            try { f = new System.Drawing.Font(family, size, style, GraphicsUnit.Point); }
            catch { f = new System.Drawing.Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point); }
            FontCache[key] = f;
            return f;
        }
    }

    public static System.Drawing.Font Display => GetFont(FontRole.Display, 17f, true);
    public static System.Drawing.Font Title => GetFont(FontRole.Display, 13f, true);
    public static System.Drawing.Font Subtitle => GetFont(FontRole.Text, 11f, true);
    public static System.Drawing.Font Body => GetFont(FontRole.Text, 10f, false);
    public static System.Drawing.Font BodyStrong => GetFont(FontRole.Text, 10f, true);
    public static System.Drawing.Font Small => GetFont(FontRole.Small, 9.25f, false);
    public static System.Drawing.Font SmallStrong => GetFont(FontRole.Small, 9.25f, true);
    public static System.Drawing.Font Caption => GetFont(FontRole.Small, 9f, false);
    public static System.Drawing.Font Mono => GetFont(FontRole.Mono, 9.5f, false);
    public static System.Drawing.Font MonoSmall => GetFont(FontRole.Mono, 9f, false);
    public static System.Drawing.Font NavItem => GetFont(FontRole.Text, 10f, false);
    public static System.Drawing.Font NavItemActive => GetFont(FontRole.Text, 10f, true);

    /// <summary>Cached UI font for odd sizes (size clamped to >= 9pt). Sizes >= 13 use the Display family.</summary>
    public static System.Drawing.Font Font(float size, bool bold = false) =>
        GetFont(size >= 13f ? FontRole.Display : size < 10f ? FontRole.Small : FontRole.Text, size, bold);

    /// <summary>Cached monospace font (Cascadia Mono / Consolas), size clamped to >= 9pt.</summary>
    public static System.Drawing.Font FontMono(float size, bool bold = false) => GetFont(FontRole.Mono, size, bold);

    // ------------------------------------------------------------------ colour utilities
    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    /// <summary>Linear ARGB interpolation a→b by t (0..1).</summary>
    public static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int L(int x, int y) => (int)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    /// <summary>Mix towards white by amount (0..1), alpha preserved.</summary>
    public static Color Lighten(Color c, float amount) => WithAlpha(Blend(Color.FromArgb(255, c), Color.White, amount), c.A);

    /// <summary>Mix towards black by amount (0..1), alpha preserved.</summary>
    public static Color Darken(Color c, float amount) => WithAlpha(Blend(Color.FromArgb(255, c), Color.Black, amount), c.A);

    /// <summary>Alpha-composite fg over an opaque bg → opaque colour (use for WinForms BackColor/BorderColor).</summary>
    public static Color Composite(Color fg, Color bg)
    {
        float a = fg.A / 255f;
        int C(int f, int b) => (int)Math.Round(f * a + b * (1 - a));
        return Color.FromArgb(255, C(fg.R, bg.R), C(fg.G, bg.G), C(fg.B, bg.B));
    }

    /// <summary>16% wash of any colour (chips, selected backgrounds).</summary>
    public static Color SubtleOf(Color c) => WithAlpha(c, 41);

    /// <summary>Readable-on-dark text variant of a semantic base colour (Success→SuccessText, ...).</summary>
    public static Color TextVariant(Color semantic)
    {
        int argb = Color.FromArgb(255, semantic).ToArgb();
        if (argb == Success.ToArgb()) return SuccessText;
        if (argb == Warning.ToArgb()) return WarningText;
        if (argb == Danger.ToArgb()) return DangerText;
        if (argb == Info.ToArgb()) return InfoText;
        if (argb == Accent.ToArgb()) return AccentText;
        return Lighten(semantic, 0.35f);
    }

    /// <summary>Permission state colour: "allow" → Success, "ask" → Warning, "deny" → Danger, else TextSecondary.</summary>
    public static Color ToolStateColor(string? state)
    {
        switch ((state ?? "").Trim().ToLowerInvariant())
        {
            case "allow": case "allowed": case "on": return StateAllow;
            case "ask": case "prompt": return StateAsk;
            case "deny": case "denied": case "off": case "block": return StateDeny;
            default: return TextSecondary;
        }
    }

    /// <summary>Category colour for cards/toasts: command → Warning, write/edit → Accent, oauth → Info, error → Danger, read/other → Success.</summary>
    public static Color CategoryColor(string? kind, CardPalette? palette = null)
    {
        string k = (kind ?? "").ToLowerInvariant();
        var p = palette;
        if (k.Contains("error") || k.Contains("deny") || k.Contains("fail")) return p?.Danger ?? Danger;
        if (k.Contains("oauth") || k.Contains("auth")) return p?.Info ?? Info;
        if (k.Contains("command") || k.Contains("terminal") || k.Contains("shell") || k.Contains("exec") || k.Contains("power")) return p?.Warning ?? Warning;
        if (k.Contains("write") || k.Contains("edit") || k.Contains("delete") || k.Contains("move")) return p?.Accent ?? Accent;
        return p?.Success ?? Success;
    }

    // ------------------------------------------------------------------ card palettes
    private static readonly CardPalette GlassPalette = new(
        "glass",
        Color.FromArgb(240, 0x1C, 0x1F, 0x26), SurfaceRaised, Border, TextPrimary, TextSecondary,
        Accent, Success, Warning, Danger, Info,
        ShadowAlpha: 90, GlowColor: null, Radius: RadiusCard, Padding: S4, TopHighlight: true, IsLight: false);

    private static readonly CardPalette CompactPalette = GlassPalette with { Name = "compact", Radius = 10f, Padding = S3 };

    private static readonly CardPalette LightPalette = new(
        "light",
        Color.FromArgb(250, 0xFF, 0xFF, 0xFF), LightSurfaceRaised, LightBorder, LightTextPrimary, LightTextSecondary,
        LightAccent, LightSuccessText, LightWarningText, LightDangerText, LightInfoText,
        ShadowAlpha: 40, GlowColor: null, Radius: RadiusCard, Padding: S4, TopHighlight: false, IsLight: true);

    private static readonly CardPalette NeonPalette = new(
        "neon",
        NeonSurface, Color.FromArgb(255, 0x12, 0x16, 0x1D), WithAlpha(NeonAccent, 90), NeonTextPrimary, NeonTextSecondary,
        NeonAccent, NeonAccent, Warning, Danger, Info,
        ShadowAlpha: 110, GlowColor: NeonGlow, Radius: RadiusCard, Padding: S4, TopHighlight: false, IsLight: false);

    /// <summary>'glass' | 'light' | 'compact' | 'neon' (case-insensitive; unknown/null → glass).</summary>
    public static CardPalette GetCardPalette(string? cardStyle)
    {
        switch ((cardStyle ?? "").Trim().ToLowerInvariant())
        {
            case "light": return LightPalette;
            case "compact": return CompactPalette;
            case "neon": return NeonPalette;
            default: return GlassPalette;
        }
    }

    // ------------------------------------------------------------------ graphics setup / text
    /// <summary>AntiAlias + HighQuality pixel offset; ClearType text for opaque surfaces, AntiAliasGridFit for layered (per-pixel alpha) windows.</summary>
    public static void PrepareGraphics(Graphics g, bool layered = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = layered ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.ClearTypeGridFit;
    }

    private static TextRenderingHint ChooseTextHint(Graphics g) =>
        g.TextRenderingHint is TextRenderingHint.AntiAlias or TextRenderingHint.AntiAliasGridFit
            ? g.TextRenderingHint   // caller is rendering to a layered/transparent surface: keep grayscale AA
            : TextRenderingHint.ClearTypeGridFit;

    private static StringFormat MakeFormat(StringAlignment h, StringAlignment v, bool ellipsis, bool wrap)
    {
        var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = h,
            LineAlignment = v,
            Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None,
        };
        sf.FormatFlags = (wrap ? 0 : StringFormatFlags.NoWrap) | StringFormatFlags.MeasureTrailingSpaces;
        if (!wrap) sf.FormatFlags |= StringFormatFlags.LineLimit;
        return sf;
    }

    /// <summary>Draws text inside rect (single line + ellipsis by default). Honors layered text hint.</summary>
    public static void DrawText(Graphics g, string? text, System.Drawing.Font font, Color color, RectangleF rect,
        StringAlignment horizontal = StringAlignment.Near, StringAlignment vertical = StringAlignment.Center,
        bool ellipsis = true, bool wrap = false)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0) return;
        var oldHint = g.TextRenderingHint;
        g.TextRenderingHint = ChooseTextHint(g);
        using var sf = MakeFormat(horizontal, vertical, ellipsis, wrap);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, rect, sf);
        g.TextRenderingHint = oldHint;
    }

    /// <summary>Measures text with the same typographic format DrawText uses.</summary>
    public static SizeF MeasureText(Graphics g, string? text, System.Drawing.Font font, float maxWidth = 10000f)
    {
        if (string.IsNullOrEmpty(text)) return SizeF.Empty;
        using var sf = MakeFormat(StringAlignment.Near, StringAlignment.Near, false, false);
        return g.MeasureString(text, font, new SizeF(maxWidth, 10000f), sf);
    }

    // ------------------------------------------------------------------ shapes
    /// <summary>Rounded rectangle path (radius clamped to half the shorter side; 0 → plain rect).</summary>
    public static GraphicsPath RoundRect(RectangleF r, float radius) => RoundRect(r, radius, radius, radius, radius);

    /// <summary>Rounded rectangle with per-corner radii (top-left, top-right, bottom-right, bottom-left).</summary>
    public static GraphicsPath RoundRect(RectangleF r, float topLeft, float topRight, float bottomRight, float bottomLeft)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }
        float max = Math.Min(r.Width, r.Height) / 2f;
        float tl = Math.Clamp(topLeft, 0, max), tr = Math.Clamp(topRight, 0, max);
        float br = Math.Clamp(bottomRight, 0, max), bl = Math.Clamp(bottomLeft, 0, max);
        if (tl + tr + br + bl <= 0.01f) { path.AddRectangle(r); return path; }

        if (tl > 0) path.AddArc(r.X, r.Y, tl * 2, tl * 2, 180, 90); else path.AddLine(r.X, r.Y, r.X, r.Y);
        if (tr > 0) path.AddArc(r.Right - tr * 2, r.Y, tr * 2, tr * 2, 270, 90); else path.AddLine(r.Right, r.Y, r.Right, r.Y);
        if (br > 0) path.AddArc(r.Right - br * 2, r.Bottom - br * 2, br * 2, br * 2, 0, 90); else path.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);
        if (bl > 0) path.AddArc(r.X, r.Bottom - bl * 2, bl * 2, bl * 2, 90, 90); else path.AddLine(r.X, r.Bottom, r.X, r.Bottom);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        if (color.A == 0) return;
        using var path = RoundRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Strokes a rounded rect; the stroke is kept INSIDE r (inset by width/2) so 1px borders are crisp.</summary>
    public static void StrokeRound(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
    {
        if (color.A == 0) return;
        var rr = RectangleF.Inflate(r, -width / 2f, -width / 2f);
        using var path = RoundRect(rr, Math.Max(0, radius - width / 2f));
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    /// <summary>Soft multi-layer drop shadow drawn around/below r. Draw BEFORE the surface.</summary>
    public static void DrawShadow(Graphics g, RectangleF r, float radius, int layers = 6, int maxAlpha = 60, float offsetY = 2f)
    {
        if (layers <= 0 || maxAlpha <= 0) return;
        // Layers are stacked from the widest (faintest) to the tightest, so the accumulated alpha
        // falls off smoothly (quadratic) — reads as a soft blur instead of hard rings.
        float total = 0f;
        for (int i = 1; i <= layers; i++) total += (layers - i + 1f) * (layers - i + 1f);
        for (int i = layers; i >= 1; i--)
        {
            float w = (layers - i + 1f) * (layers - i + 1f) / total;
            int a = Math.Clamp((int)Math.Round(maxAlpha * w), 1, 255);
            var sr = RectangleF.Inflate(r, i, i);
            sr.Offset(0, offsetY * (0.5f + 0.5f * i / layers));
            using var path = RoundRect(sr, radius + i);
            using var brush = new SolidBrush(Color.FromArgb(a, 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    /// <summary>Standard card: Surface fill + subtle top highlight (white 4%) + 1px Border.</summary>
    public static void DrawCard(Graphics g, RectangleF r, Color? fill = null, float radius = RadiusCard, Color? border = null)
    {
        FillRound(g, r, radius, fill ?? Surface);
        DrawTopHighlight(g, r, radius, TopHighlight);
        StrokeRound(g, r, radius, border ?? Border);
    }

    /// <summary>Palette-aware card (toasts / approval cards): shadow + surface + highlight + border (+ neon glow; accent border when hover).</summary>
    public static void DrawCard(Graphics g, RectangleF r, CardPalette p, bool hover = false, bool shadow = true)
    {
        if (p.GlowColor is Color glow)
        {
            for (int i = 4; i >= 1; i--)
            {
                using var gp = RoundRect(RectangleF.Inflate(r, i * 1.5f, i * 1.5f), p.Radius + i * 1.5f);
                using var gb = new SolidBrush(WithAlpha(glow, glow.A / (i + 2)));
                g.FillPath(gb, gp);
            }
        }
        else if (shadow)
        {
            DrawShadow(g, r, p.Radius, 10, p.ShadowAlpha, 4f);
        }
        FillRound(g, r, p.Radius, p.Surface);
        if (p.TopHighlight) DrawTopHighlight(g, r, p.Radius, TopHighlight);
        Color borderCol = hover
            ? (p.IsLight ? WithAlpha(p.Accent, 110) : p.GlowColor.HasValue ? WithAlpha(p.Accent, 200) : BorderStrong)
            : p.Border;
        StrokeRound(g, r, p.Radius, borderCol, p.GlowColor.HasValue ? 1.25f : 1f);
    }

    private static void DrawTopHighlight(Graphics g, RectangleF r, float radius, Color highlight)
    {
        float h = Math.Min(r.Height, 28f);
        if (h < 2f) return;
        using var path = RoundRect(r, radius);
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        var hr = new RectangleF(r.X, r.Y, r.Width, h);
        using (var brush = new LinearGradientBrush(new RectangleF(hr.X, hr.Y - 0.5f, hr.Width, hr.Height + 1f),
                   highlight, Color.FromArgb(0, highlight), LinearGradientMode.Vertical))
        {
            g.FillRectangle(brush, hr);
        }
        using (var pen = new Pen(WithAlpha(Color.White, Math.Min(255, highlight.A * 2)), 1f))
        {
            g.DrawLine(pen, r.X + radius, r.Y + 1.5f, r.Right - radius, r.Y + 1.5f);
        }
        g.Restore(state);
    }

    // ------------------------------------------------------------------ buttons
    /// <summary>(background, foreground, border) for a button kind/state. Border may be transparent.
    /// Primary's Back is the flat equivalent of its gradient (used by WinForms buttons).</summary>
    public static (Color Back, Color Fore, Color Border) ButtonColors(ButtonKind kind, ButtonState state)
    {
        if (state == ButtonState.Disabled)
        {
            return kind == ButtonKind.Subtle
                ? (Color.Transparent, TextDisabled, Color.Transparent)
                : (SurfaceRaised, TextDisabled, kind is ButtonKind.Secondary or ButtonKind.Outline ? Border : Color.Transparent);
        }
        bool hov = state == ButtonState.Hover, prs = state == ButtonState.Pressed;
        switch (kind)
        {
            case ButtonKind.Primary:
                return (hov ? AccentHover : prs ? AccentPressed : Accent, TextOnAccent, Color.Transparent);
            case ButtonKind.Secondary:
                return (hov ? SurfaceHover : prs ? SurfacePressed : SurfaceRaised, TextPrimary, BorderStrong);
            case ButtonKind.Subtle:
                return (hov ? HoverOverlay : prs ? PressedOverlay : Color.Transparent, TextPrimary, Color.Transparent);
            case ButtonKind.Outline:
                return (hov ? WithAlpha(Accent, 46) : prs ? WithAlpha(Accent, 66) : WithAlpha(Accent, 20), AccentText, WithAlpha(Accent, hov || prs ? 170 : 130));
            case ButtonKind.Danger:
                // soft destructive: red wash + red text + red hairline (mockups: «Отклонить», «Запретить»)
                return (hov ? WithAlpha(Danger, 56) : prs ? WithAlpha(Danger, 76) : WithAlpha(Danger, 36), DangerText, WithAlpha(Danger, hov || prs ? 120 : 84));
            default:
                Color b = kind == ButtonKind.DangerSolid ? Danger : kind == ButtonKind.Success ? Success : Warning;
                Color bg = hov ? Lighten(b, 0.08f) : prs ? Darken(b, 0.10f) : b;
                Color fg = kind == ButtonKind.DangerSolid ? Color.White : Color.FromArgb(0x0E, 0x12, 0x18);
                return (bg, fg, Color.Transparent);
        }
    }

    /// <summary>
    /// Draws a custom button (text centred, ellipsis). Default font BodyStrong, radius RadiusButton.
    /// Primary = indigo vertical gradient + soft accent glow + top sheen; Secondary = raised + hairline;
    /// Outline = accent wash + accent border; Danger = soft red; DangerSolid/Success/Warning = filled.
    /// Leave ~6px around Primary buttons for the glow.
    /// </summary>
    public static void DrawButton(Graphics g, RectangleF r, string text, ButtonKind kind, ButtonState state,
        System.Drawing.Font? font = null, float radius = RadiusButton, IconKind? icon = null)
    {
        var (back, fore, border) = ButtonColors(kind, state);
        if (kind == ButtonKind.Primary && state != ButtonState.Disabled)
        {
            DrawGlow(g, r, radius, state == ButtonState.Hover ? WithAlpha(Accent, 95) : AccentGlow, 6, 2f);
            Color top = state == ButtonState.Hover ? Lighten(AccentGradTop, 0.10f) : state == ButtonState.Pressed ? AccentPressed : AccentGradTop;
            Color bot = state == ButtonState.Hover ? Lighten(AccentGradBottom, 0.08f) : state == ButtonState.Pressed ? Darken(AccentGradBottom, 0.08f) : AccentGradBottom;
            FillRoundGradient(g, r, radius, top, bot);
            DrawTopHighlight(g, r, radius, Color.FromArgb(28, 255, 255, 255));
            StrokeRound(g, r, radius, Color.FromArgb(40, 255, 255, 255));
        }
        else
        {
            FillRound(g, r, radius, back);
            if (border.A > 0) StrokeRound(g, r, radius, border);
        }
        float padX = Math.Min(S3, r.Width / 6f);
        if (icon is IconKind ik)
        {
            float iw = IconSize;
            float tw = string.IsNullOrEmpty(text) ? 0f : MeasureText(g, text, font ?? BodyStrong).Width;
            float gap = tw > 0 ? 7f : 0f;
            float total = Math.Min(r.Width - padX * 2, iw + gap + tw);
            float x0 = r.X + (r.Width - total) / 2f;
            DrawIcon(g, ik, new RectangleF(x0, r.Y + (r.Height - iw) / 2f, iw, iw), fore);
            if (tw > 0)
                DrawText(g, text, font ?? BodyStrong, fore, new RectangleF(x0 + iw + gap, r.Y, Math.Max(0, r.Right - padX - (x0 + iw + gap)), r.Height));
            return;
        }
        var tr = RectangleF.Inflate(r, -padX, 0);
        DrawText(g, text, font ?? BodyStrong, fore, tr, StringAlignment.Center, StringAlignment.Center);
    }

    /// <summary>Recommended width for a button with this text (padding 16 each side, min 72).</summary>
    public static float MeasureButtonWidth(Graphics g, string text, System.Drawing.Font? font = null, float minWidth = 72f) =>
        Math.Max(minWidth, MeasureText(g, text, font ?? BodyStrong).Width + S5 * 2);

    // ------------------------------------------------------------------ toggle / segmented / chips / dots
    /// <summary>Fluent toggle; pass a 40x20 rect (ToggleWidth x ToggleHeight).</summary>
    public static void DrawToggle(Graphics g, RectangleF r, bool on, bool hover, bool disabled, Color? onColor = null)
    {
        float radius = r.Height / 2f;
        Color accent = onColor ?? Accent;
        float knobD;
        Color knob;
        if (on)
        {
            Color fill = disabled ? SurfacePressed : hover ? Lighten(accent, 0.10f) : accent;
            if (!disabled) DrawGlow(g, r, radius, WithAlpha(accent, 50), 4, 1f);
            FillRound(g, r, radius, fill);
            knob = disabled ? TextDisabled : Color.White;
            knobD = hover && !disabled ? r.Height - 6f : r.Height - 8f;
            float kx = r.Right - (r.Height - knobD) / 2f - knobD;
            FillCircle(g, new RectangleF(kx, r.Y + (r.Height - knobD) / 2f, knobD, knobD), knob);
        }
        else
        {
            FillRound(g, r, radius, disabled ? Trough : hover ? SurfaceHover : Trough);
            StrokeRound(g, r, radius, disabled ? Border : BorderStrong);
            knob = disabled ? TextDisabled : hover ? TextPrimary : TextSecondary;
            knobD = hover && !disabled ? r.Height - 6f : r.Height - 8f;
            float kx = r.X + (r.Height - knobD) / 2f;
            FillCircle(g, new RectangleF(kx, r.Y + (r.Height - knobD) / 2f, knobD, knobD), knob);
        }
    }

    private static void FillCircle(Graphics g, RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillEllipse(b, r);
    }

    /// <summary>
    /// Segmented control. Track SurfaceRaised (radius 8); selected segment SurfaceHover (radius 6) with
    /// coloured text (TextVariant of selectedColors[i], default AccentText) and a 2px bottom indicator.
    /// hoverIndex = -1 for none. Returns the segment rects (use them as hit zones).
    /// </summary>
    public static RectangleF[] DrawSegmented(Graphics g, RectangleF r, string[] items, int selected, int hoverIndex, Color[]? selectedColors = null)
    {
        var rects = new RectangleF[items.Length];
        if (items.Length == 0) return rects;
        FillRound(g, r, RadiusControl, SurfaceRaised);
        StrokeRound(g, r, RadiusControl, Border);
        const float pad = 3f;
        float segW = (r.Width - pad * 2) / items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            var seg = new RectangleF(r.X + pad + i * segW, r.Y + pad, segW, r.Height - pad * 2);
            rects[i] = seg;
            bool sel = i == selected;
            Color baseCol = selectedColors != null && i < selectedColors.Length ? selectedColors[i] : Accent;
            if (sel)
            {
                FillRound(g, seg, RadiusSmall, SurfaceHover);
                StrokeRound(g, seg, RadiusSmall, Border);
                float iw = Math.Min(Math.Max(14f, seg.Width * 0.35f), 28f);
                FillRound(g, new RectangleF(seg.X + (seg.Width - iw) / 2f, seg.Bottom - 3f, iw, 2f), 1f, baseCol);
            }
            else if (i == hoverIndex)
            {
                FillRound(g, seg, RadiusSmall, HoverOverlay);
            }
            Color txt = sel ? TextVariant(baseCol) : i == hoverIndex ? TextPrimary : TextSecondary;
            DrawText(g, items[i], sel ? SmallStrong : Small, txt, RectangleF.Inflate(seg, -4f, 0), StringAlignment.Center, StringAlignment.Center);
        }
        return rects;
    }

    /// <summary>Pill-shaped chip: bgSubtle fill (e.g. SuccessSubtle), fg text (e.g. SuccessText). Default font SmallStrong.</summary>
    public static void DrawChip(Graphics g, RectangleF r, string text, Color fg, Color bgSubtle, System.Drawing.Font? font = null)
    {
        FillRound(g, r, r.Height / 2f, bgSubtle);
        DrawText(g, text, font ?? SmallStrong, fg, RectangleF.Inflate(r, -S2, 0), StringAlignment.Center, StringAlignment.Center);
    }

    /// <summary>Chip with a hairline border in the foreground colour (mockup status pills: «Готов», «Онлайн»), optional leading dot.</summary>
    public static void DrawChipOutlined(Graphics g, RectangleF r, string text, Color fg, Color bgSubtle, bool dot = false, System.Drawing.Font? font = null)
    {
        FillRound(g, r, r.Height / 2f, bgSubtle);
        StrokeRound(g, r, r.Height / 2f, WithAlpha(fg, 70));
        var tr = RectangleF.Inflate(r, -S2 - 2, 0);
        if (dot)
        {
            DrawStatusDot(g, new PointF(tr.X + 3f, r.Y + r.Height / 2f), 3f, fg);
            tr = new RectangleF(tr.X + 11f, tr.Y, tr.Width - 11f, tr.Height);
        }
        DrawText(g, text, font ?? SmallStrong, TextVariant(fg), tr, dot ? StringAlignment.Near : StringAlignment.Center, StringAlignment.Center);
    }

    /// <summary>Width DrawChipOutlined needs (padding 10 each side, +11 for the dot).</summary>
    public static float MeasureChipOutlinedWidth(Graphics g, string text, bool dot = false, System.Drawing.Font? font = null) =>
        MeasureText(g, text, font ?? SmallStrong).Width + (S2 + 2) * 2 + (dot ? 11f : 0f) + 2;

    /// <summary>Alias of DrawChip.</summary>
    public static void DrawPill(Graphics g, RectangleF r, string text, Color fg, Color bgSubtle, System.Drawing.Font? font = null) =>
        DrawChip(g, r, text, fg, bgSubtle, font);

    /// <summary>Width a chip needs for text (padding 8 each side).</summary>
    public static float MeasureChipWidth(Graphics g, string text, System.Drawing.Font? font = null) =>
        MeasureText(g, text, font ?? SmallStrong).Width + S2 * 2 + 2;

    /// <summary>Status dot; halo adds a 2x-radius ring at 22% alpha.</summary>
    public static void DrawStatusDot(Graphics g, PointF center, float r, Color c, bool halo = false)
    {
        if (halo)
        {
            // three concentric rings fading out to ~2.4x radius (soft "online" glow)
            float[] k = { 2.4f, 1.9f, 1.45f };
            int[] al = { 22, 34, 52 };
            for (int i = 0; i < k.Length; i++)
            {
                float hr = r * k[i];
                FillCircle(g, new RectangleF(center.X - hr, center.Y - hr, hr * 2, hr * 2), WithAlpha(c, al[i]));
            }
        }
        FillCircle(g, new RectangleF(center.X - r, center.Y - r, r * 2, r * 2), c);
    }

    // ------------------------------------------------------------------ composite helpers (settings)
    /// <summary>Sidebar nav item: selected = AccentSubtle rounded bg (r8) + 3px accent bar + NavItemActive + AccentText icon.</summary>
    public static void DrawNavItem(Graphics g, RectangleF r, IconKind icon, string text, bool selected, bool hover)
    {
        if (selected)
        {
            FillRound(g, r, RadiusControl, AccentSubtle);
            FillRound(g, new RectangleF(r.X, r.Y + (r.Height - 16f) / 2f, 3f, 16f), 1.5f, Accent);
        }
        else if (hover)
        {
            FillRound(g, r, RadiusControl, HoverOverlay);
        }
        var ib = new RectangleF(r.X + S3, r.Y + (r.Height - IconSize) / 2f, IconSize, IconSize);
        DrawIcon(g, icon, ib, selected ? AccentText : TextSecondary);
        var tr = new RectangleF(ib.Right + S3, r.Y, r.Right - ib.Right - S3 - S2, r.Height);
        DrawText(g, text, selected ? NavItemActive : NavItem, selected ? TextPrimary : hover ? TextPrimary : TextSecondary, tr);
    }

    /// <summary>Group title above a card (SmallStrong, TextSecondary, no uppercase). Returns y below the title.</summary>
    public static float DrawGroupTitle(Graphics g, float x, float y, float width, string title)
    {
        DrawText(g, title, SmallStrong, TextSecondary, new RectangleF(x + S1, y, width - S1, 20f), StringAlignment.Near, StringAlignment.Center);
        return y + 20f + S2;
    }

    /// <summary>Row text block: title (Body, TextPrimary) + optional description (Small, TextSecondary), vertically centered in r.</summary>
    public static void DrawRowText(Graphics g, RectangleF r, string title, string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            DrawText(g, title, Body, TextPrimary, r);
            return;
        }
        float th = 20f, dh = 18f, total = th + dh;
        float top = r.Y + (r.Height - total) / 2f;
        DrawText(g, title, Body, TextPrimary, new RectangleF(r.X, top, r.Width, th));
        DrawText(g, description, Small, TextSecondary, new RectangleF(r.X, top + th, r.Width, dh));
    }

    /// <summary>1px horizontal divider (Divider colour).</summary>
    public static void DrawDivider(Graphics g, float x1, float x2, float y)
    {
        using var pen = new Pen(Divider, 1f);
        g.DrawLine(pen, x1, y + 0.5f, x2, y + 0.5f);
    }

    // ------------------------------------------------------------------ redesign primitives (2026-10)
    /// <summary>Vertical-gradient rounded fill.</summary>
    public static void FillRoundGradient(Graphics g, RectangleF r, float radius, Color top, Color bottom)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundRect(r, radius);
        using var brush = new LinearGradientBrush(new RectangleF(r.X, r.Y - 0.5f, r.Width, r.Height + 1f), top, bottom, LinearGradientMode.Vertical);
        g.FillPath(brush, path);
    }

    /// <summary>Coloured soft glow around r (draw BEFORE the surface). Same falloff as DrawShadow.</summary>
    public static void DrawGlow(Graphics g, RectangleF r, float radius, Color color, int layers = 6, float offsetY = 0f)
    {
        if (layers <= 0 || color.A == 0) return;
        float total = 0f;
        for (int i = 1; i <= layers; i++) total += (layers - i + 1f) * (layers - i + 1f);
        for (int i = layers; i >= 1; i--)
        {
            float w = (layers - i + 1f) * (layers - i + 1f) / total;
            int a = Math.Clamp((int)Math.Round(color.A * w), 1, 255);
            var sr = RectangleF.Inflate(r, i, i);
            sr.Offset(0, offsetY);
            using var path = RoundRect(sr, radius + i);
            using var brush = new SolidBrush(Color.FromArgb(a, color));
            g.FillPath(brush, path);
        }
    }

    /// <summary>Window background: BgGradTop → BgGradBottom vertical gradient (optionally clipped to a rounded rect).</summary>
    public static void DrawWindowBackground(Graphics g, RectangleF r, float radius = 0f)
    {
        if (radius > 0) FillRoundGradient(g, r, radius, BgGradTop, BgGradBottom);
        else
        {
            using var brush = new LinearGradientBrush(new RectangleF(r.X, r.Y - 0.5f, r.Width, r.Height + 1f), BgGradTop, BgGradBottom, LinearGradientMode.Vertical);
            g.FillRectangle(brush, r);
        }
    }

    /// <summary>Elevated floating surface (tray panel, toast, modal): soft shadow + gradient fill + top sheen + hairline.</summary>
    public static void DrawFloatingSurface(Graphics g, RectangleF r, float radius = RadiusWindow, int shadowAlpha = 110)
    {
        DrawShadow(g, r, radius, 12, shadowAlpha, 6f);
        FillRoundGradient(g, r, radius, BgGradTop, BgGradBottom);
        DrawTopHighlight(g, r, radius, Color.FromArgb(12, 255, 255, 255));
        StrokeRound(g, r, radius, BorderStrong);
    }

    /// <summary>Tinted icon tile (mockups: 36px, radius 10): tint wash 16% + tint hairline 22% + icon in the text variant.</summary>
    public static void DrawIconTile(Graphics g, RectangleF r, IconKind icon, Color tint, float iconSize = 18f, float radius = RadiusTile)
    {
        FillRound(g, r, radius, WithAlpha(tint, 41));
        StrokeRound(g, r, radius, WithAlpha(tint, 56));
        float s = Math.Min(iconSize, Math.Min(r.Width, r.Height) - 8f);
        DrawIcon(g, icon, new RectangleF(r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s), TextVariant(tint), 1.7f);
    }

    /// <summary>Category tint for a permission/tool group (settings rows, icon tiles).</summary>
    public static Color CategoryTint(IconKind icon) => icon switch
    {
        IconKind.Terminal or IconKind.Power or IconKind.Warning => Warning,
        IconKind.File or IconKind.Folder or IconKind.Eye or IconKind.Search => Success,
        IconKind.Edit or IconKind.Tools or IconKind.Settings or IconKind.Home => Accent,
        IconKind.Globe or IconKind.Link or IconKind.Connection => Info,
        IconKind.Lock or IconKind.Access or IconKind.Trash or IconKind.Stop => Danger,
        IconKind.Audit or IconKind.Clock or IconKind.Notifications => Violet,
        _ => Accent,
    };

    /// <summary>Progress bar: Trough track + accent gradient fill (pill). fraction is clamped to [0,1].</summary>
    public static void DrawProgressBar(Graphics g, RectangleF r, float fraction, Color? color = null)
    {
        float radius = r.Height / 2f;
        FillRound(g, r, radius, Trough);
        float f = Math.Clamp(fraction, 0f, 1f);
        if (f <= 0f) return;
        var fr = new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * f), r.Height);
        Color c = color ?? Accent;
        using var path = RoundRect(fr, radius);
        using var brush = new LinearGradientBrush(new RectangleF(fr.X - 0.5f, fr.Y, fr.Width + 1f, fr.Height), Lighten(c, 0.12f), c, LinearGradientMode.Horizontal);
        g.FillPath(brush, path);
    }

    /// <summary>Count badge (e.g. «×3», «3»): accent pill with white text; returns the drawn rect (anchored at the right edge if alignRight).</summary>
    public static RectangleF DrawBadge(Graphics g, PointF anchor, string text, Color? color = null, bool alignRight = false)
    {
        var f = Font(9f, true);
        float w = Math.Max(18f, MeasureText(g, text, f).Width + 12f), h = 18f;
        var r = new RectangleF(alignRight ? anchor.X - w : anchor.X, anchor.Y - h / 2f, w, h);
        FillRound(g, r, h / 2f, color ?? Accent);
        DrawText(g, text, f, TextOnAccent, r, StringAlignment.Center, StringAlignment.Center);
        return r;
    }

    /// <summary>Hairline "keyboard / code" box (SurfaceRaised + Border, radius RadiusSmall) for paths, commands, shortcuts.</summary>
    public static void DrawCodeBox(Graphics g, RectangleF r, string? text, Color? fore = null, bool wrap = false)
    {
        FillRound(g, r, RadiusControl, WithAlpha(Bg0, 200));
        StrokeRound(g, r, RadiusControl, Border);
        DrawText(g, text, Mono, fore ?? TextPrimary, RectangleF.Inflate(r, -S3, wrap ? -S2 : 0), StringAlignment.Near,
            wrap ? StringAlignment.Near : StringAlignment.Center, ellipsis: true, wrap: wrap);
    }

    /// <summary>Section header used across redesigned surfaces: title (Subtitle, TextPrimary) + optional caption on the right.</summary>
    public static float DrawSectionHeader(Graphics g, RectangleF r, string title, string? trailing = null)
    {
        DrawText(g, title, Subtitle, TextPrimary, new RectangleF(r.X, r.Y, r.Width, 22f));
        if (!string.IsNullOrEmpty(trailing))
            DrawText(g, trailing, Small, TextTertiary, new RectangleF(r.X, r.Y, r.Width, 22f), StringAlignment.Far);
        return r.Y + 22f + S2;
    }

    // ------------------------------------------------------------------ icons
    /// <summary>Line icon centred in box (square, min(w,h)); stroke is specified for a 16px box and scales with size.</summary>
    public static void DrawIcon(Graphics g, IconKind kind, RectangleF box, Color color, float stroke = 1.6f)
    {
        float size = Math.Min(box.Width, box.Height);
        if (size <= 0) return;
        float s = size / 24f;
        float ox = box.X + (box.Width - size) / 2f;
        float oy = box.Y + (box.Height - size) / 2f;

        using var path = new GraphicsPath();
        using var fill = new GraphicsPath();
        BuildIcon(kind, path, fill);

        using var m = new Matrix();
        m.Translate(ox, oy);
        m.Scale(s, s);
        path.Transform(m);
        fill.Transform(m);

        var oldSmooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(color, Math.Max(1f, stroke * size / 16f)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            if (path.PointCount > 0) g.DrawPath(pen, path);
        }
        if (fill.PointCount > 0)
        {
            using var b = new SolidBrush(color);
            g.FillPath(b, fill);
        }
        g.SmoothingMode = oldSmooth;
    }

    private static void BuildIcon(IconKind kind, GraphicsPath p, GraphicsPath f)
    {
        void Line(float x1, float y1, float x2, float y2) { p.StartFigure(); p.AddLine(x1, y1, x2, y2); }
        void Poly(params float[] xy)
        {
            p.StartFigure();
            var pts = new PointF[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(xy[i * 2], xy[i * 2 + 1]);
            p.AddLines(pts);
        }
        void Polygon(params float[] xy)
        {
            p.StartFigure();
            var pts = new PointF[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(xy[i * 2], xy[i * 2 + 1]);
            p.AddPolygon(pts);
        }
        void Circle(float cx, float cy, float r) { p.StartFigure(); p.AddEllipse(cx - r, cy - r, r * 2, r * 2); }
        void Dot(float cx, float cy, float r = 1.15f) { f.StartFigure(); f.AddEllipse(cx - r, cy - r, r * 2, r * 2); }
        void Arc(float cx, float cy, float r, float start, float sweep) { p.StartFigure(); p.AddArc(cx - r, cy - r, r * 2, r * 2, start, sweep); }
        void Rect(float x, float y, float w, float h, float rad)
        {
            p.StartFigure();
            using var rr = RoundRect(new RectangleF(x, y, w, h), rad);
            p.AddPath(rr, false);
        }
        void ArrowHead(float tipX, float tipY, float dx, float dy, float len = 3.6f, float wid = 3.2f)
        {
            float l = MathF.Sqrt(dx * dx + dy * dy);
            if (l < 0.001f) return;
            dx /= l; dy /= l;
            float nx = -dy, ny = dx;
            Poly(tipX - dx * len + nx * wid, tipY - dy * len + ny * wid, tipX, tipY, tipX - dx * len - nx * wid, tipY - dy * len - ny * wid);
        }

        switch (kind)
        {
            case IconKind.Settings:
            {
                var pts = new List<PointF>();
                for (int t = 0; t < 8; t++)
                {
                    float b = t * 45f;
                    foreach (var (a, rad) in new[] { (b - 15f, 7.2f), (b - 8f, 9.6f), (b + 8f, 9.6f), (b + 15f, 7.2f) })
                    {
                        float ar = a * MathF.PI / 180f;
                        pts.Add(new PointF(12f + rad * MathF.Cos(ar), 12f + rad * MathF.Sin(ar)));
                    }
                }
                p.StartFigure();
                p.AddPolygon(pts.ToArray());
                Circle(12, 12, 3f);
                break;
            }
            case IconKind.Tools:
                // open-jaw wrench head (gap towards upper-right) + notch + handle
                Arc(15.5f, 8.5f, 5f, 0f, 270f);
                Line(20.5f, 8.5f, 17.2f, 8.5f);
                Line(15.5f, 3.5f, 15.5f, 6.8f);
                Line(11.9f, 12.1f, 4.5f, 19.5f);
                break;
            case IconKind.Access:
                Circle(8f, 16f, 4.2f);
                Line(11f, 13f, 20f, 4f);
                Line(17f, 7f, 19.5f, 9.5f);
                Line(14.5f, 9.5f, 16.5f, 11.5f);
                break;
            case IconKind.Approvals:
                p.StartFigure();
                p.AddLine(12f, 3f, 19f, 6f);
                p.AddLine(19f, 6f, 19f, 11f);
                p.AddBezier(19f, 11f, 19f, 15.5f, 16f, 19f, 12f, 21f);
                p.AddBezier(12f, 21f, 8f, 19f, 5f, 15.5f, 5f, 11f);
                p.AddLine(5f, 11f, 5f, 6f);
                p.CloseFigure();
                Poly(9f, 12f, 11.2f, 14.2f, 15.3f, 10f);
                break;
            case IconKind.Notifications:
                p.StartFigure();
                p.AddLine(6f, 16.5f, 6f, 11f);
                p.AddBezier(6f, 11f, 6f, 7.7f, 8.7f, 5f, 12f, 5f);
                p.AddBezier(12f, 5f, 15.3f, 5f, 18f, 7.7f, 18f, 11f);
                p.AddLine(18f, 11f, 18f, 16.5f);
                p.AddLine(18f, 16.5f, 19.5f, 18f);
                p.AddLine(19.5f, 18f, 4.5f, 18f);
                p.AddLine(4.5f, 18f, 6f, 16.5f);
                p.CloseFigure();
                Arc(12f, 19.5f, 2f, 0f, 180f);
                Line(12f, 3f, 12f, 5f);
                break;
            case IconKind.Connection:
            case IconKind.Link:
            {
                using var a = RoundRect(new RectangleF(2.5f, 9f, 11.5f, 6f), 3f);
                using var b = RoundRect(new RectangleF(10f, 9f, 11.5f, 6f), 3f);
                using var rot = new Matrix();
                rot.RotateAt(-45f, new PointF(12f, 12f));
                a.Transform(rot);
                b.Transform(rot);
                p.StartFigure(); p.AddPath(a, false);
                p.StartFigure(); p.AddPath(b, false);
                break;
            }
            case IconKind.Audit:
                Line(9f, 6f, 20f, 6f);
                Line(9f, 12f, 20f, 12f);
                Line(9f, 18f, 20f, 18f);
                Dot(4.5f, 6f); Dot(4.5f, 12f); Dot(4.5f, 18f);
                break;
            case IconKind.About:
                Circle(12f, 12f, 9f);
                Line(12f, 11f, 12f, 16.5f);
                Dot(12f, 7.8f);
                break;
            case IconKind.Power:
                Arc(12f, 13f, 7.5f, -50f, 280f);
                Line(12f, 3f, 12f, 11f);
                break;
            case IconKind.Play:
                Polygon(8f, 5f, 19f, 12f, 8f, 19f);
                break;
            case IconKind.Stop:
                Rect(6f, 6f, 12f, 12f, 2f);
                break;
            case IconKind.Folder:
                Polygon(3f, 5f, 9.5f, 5f, 11.5f, 7.5f, 21f, 7.5f, 21f, 19f, 3f, 19f);
                break;
            case IconKind.Terminal:
                Rect(2.5f, 4f, 19f, 16f, 2.5f);
                Poly(6.5f, 9f, 9.5f, 12f, 6.5f, 15f);
                Line(12f, 15.5f, 17f, 15.5f);
                break;
            case IconKind.File:
                Polygon(6f, 3f, 14f, 3f, 19f, 8f, 19f, 21f, 6f, 21f);
                Poly(14f, 3f, 14f, 8f, 19f, 8f);
                break;
            case IconKind.Edit:
                Polygon(4f, 20f, 4.8f, 16.2f, 15.8f, 5.2f, 18.8f, 8.2f, 7.8f, 19.2f);
                Line(13.6f, 7.4f, 16.6f, 10.4f);
                break;
            case IconKind.Eye:
                p.StartFigure();
                p.AddBezier(2.5f, 12f, 5f, 7f, 8.5f, 5f, 12f, 5f);
                p.AddBezier(12f, 5f, 15.5f, 5f, 19f, 7f, 21.5f, 12f);
                p.AddBezier(21.5f, 12f, 19f, 17f, 15.5f, 19f, 12f, 19f);
                p.AddBezier(12f, 19f, 8.5f, 19f, 5f, 17f, 2.5f, 12f);
                p.CloseFigure();
                Circle(12f, 12f, 3f);
                break;
            case IconKind.Check:
                Poly(5f, 12.5f, 10f, 17.5f, 19.5f, 7f);
                break;
            case IconKind.Close:
                Line(6f, 6f, 18f, 18f);
                Line(18f, 6f, 6f, 18f);
                break;
            case IconKind.ChevronDown:
                Poly(6f, 9f, 12f, 15f, 18f, 9f);
                break;
            case IconKind.ChevronUp:
                Poly(6f, 15f, 12f, 9f, 18f, 15f);
                break;
            case IconKind.ChevronRight:
                Poly(9f, 6f, 15f, 12f, 9f, 18f);
                break;
            case IconKind.ChevronLeft:
                Poly(15f, 6f, 9f, 12f, 15f, 18f);
                break;
            case IconKind.Clock:
                Circle(12f, 12f, 9f);
                Poly(12f, 7f, 12f, 12f, 15.5f, 14f);
                break;
            case IconKind.Lock:
                Rect(5f, 11f, 14f, 10f, 2f);
                p.StartFigure();
                p.AddLine(8f, 11f, 8f, 8f);
                p.AddArc(8f, 4f, 8f, 8f, 180f, 180f);
                p.AddLine(16f, 8f, 16f, 11f);
                break;
            case IconKind.Warning:
                Polygon(12f, 3.5f, 21.5f, 20f, 2.5f, 20f);
                Line(12f, 9.5f, 12f, 14f);
                Dot(12f, 17f);
                break;
            case IconKind.Copy:
                Rect(8.5f, 8.5f, 12.5f, 12.5f, 2f);
                Poly(15.5f, 5.5f, 15.5f, 3f, 3f, 3f, 3f, 15.5f, 5.5f, 15.5f);
                break;
            case IconKind.Refresh:
            {
                const float r = 7.5f, start = 40f, sweep = 290f;
                Arc(12f, 12f, r, start, sweep);
                float endA = (start + sweep) * MathF.PI / 180f;
                float ex = 12f + r * MathF.Cos(endA), ey = 12f + r * MathF.Sin(endA);
                ArrowHead(ex, ey, -MathF.Sin(endA), MathF.Cos(endA));
                break;
            }
            case IconKind.Plus:
                Line(12f, 5f, 12f, 19f);
                Line(5f, 12f, 19f, 12f);
                break;
            case IconKind.Minus:
                Line(5f, 12f, 19f, 12f);
                break;
            case IconKind.Trash:
                Line(4f, 6.5f, 20f, 6.5f);
                Poly(9f, 6.5f, 9f, 4f, 15f, 4f, 15f, 6.5f);
                Poly(6f, 6.5f, 7f, 20.5f, 17f, 20.5f, 18f, 6.5f);
                Line(10f, 10.5f, 10f, 16.5f);
                Line(14f, 10.5f, 14f, 16.5f);
                break;
            case IconKind.Language:
            case IconKind.Globe:
                Circle(12f, 12f, 9f);
                p.StartFigure(); p.AddEllipse(8f, 3f, 8f, 18f);
                Line(3f, 12f, 21f, 12f);
                break;
            case IconKind.Sound:
                Polygon(4f, 9.5f, 8f, 9.5f, 13f, 5f, 13f, 19f, 8f, 14.5f, 4f, 14.5f);
                Arc(13f, 12f, 3.8f, -50f, 100f);
                Arc(13f, 12f, 7.2f, -50f, 100f);
                break;
            case IconKind.Menu:
                Line(4f, 7f, 20f, 7f);
                Line(4f, 12f, 20f, 12f);
                Line(4f, 17f, 20f, 17f);
                break;
            case IconKind.Search:
                Circle(10.5f, 10.5f, 6.5f);
                Line(15.3f, 15.3f, 20f, 20f);
                break;
            case IconKind.Info:
                Circle(12f, 12f, 9f);
                Line(12f, 11f, 12f, 17f);
                Dot(12f, 7.5f);
                break;
            case IconKind.Shield:
            case IconKind.ShieldCheck:
                p.StartFigure();
                p.AddLine(12f, 2.6f, 19.6f, 5.4f);
                p.AddLine(19.6f, 5.4f, 19.6f, 11.6f);
                p.AddBezier(19.6f, 11.6f, 19.6f, 16.8f, 15.6f, 19.9f, 12f, 21.4f);
                p.AddBezier(12f, 21.4f, 8.4f, 19.9f, 4.4f, 16.8f, 4.4f, 11.6f);
                p.AddLine(4.4f, 11.6f, 4.4f, 5.4f);
                p.CloseFigure();
                if (kind == IconKind.ShieldCheck) Poly(8.8f, 12f, 11f, 14.2f, 15.3f, 9.9f);
                break;
            case IconKind.Image:
                Rect(3f, 3.5f, 18f, 17f, 2.6f);
                Circle(9f, 9.2f, 1.8f);
                Poly(20.8f, 15.2f, 16f, 10.6f, 5.2f, 20.3f);
                break;
            case IconKind.Lightbulb:
                Arc(12f, 9f, 6f, 135f, 270f);
                Line(7.76f, 13.24f, 9.2f, 15.6f);
                Line(16.24f, 13.24f, 14.8f, 15.6f);
                Line(9.2f, 15.6f, 14.8f, 15.6f);
                Line(9.3f, 18.4f, 14.7f, 18.4f);
                Line(10.4f, 21.2f, 13.6f, 21.2f);
                break;
            case IconKind.Gauge:
                Arc(12f, 14f, 8.6f, 150f, 240f);
                Line(12f, 14f, 15.6f, 10.4f);
                Dot(12f, 14f, 1.4f);
                break;
            case IconKind.Server:
                Rect(3f, 3.5f, 18f, 7.4f, 2f);
                Rect(3f, 13.1f, 18f, 7.4f, 2f);
                Dot(7f, 7.2f);
                Dot(7f, 16.8f);
                break;
            case IconKind.User:
                Circle(12f, 8f, 4f);
                Arc(12f, 21.5f, 7.5f, 180f, 180f);
                break;
            case IconKind.Sliders:
                Line(3.5f, 6f, 20.5f, 6f);
                Line(3.5f, 12f, 20.5f, 12f);
                Line(3.5f, 18f, 20.5f, 18f);
                Line(14.5f, 3.5f, 14.5f, 8.5f);
                Line(8.5f, 9.5f, 8.5f, 14.5f);
                Line(16f, 15.5f, 16f, 20.5f);
                break;
            case IconKind.FolderLock:
                Poly(10.5f, 19f, 3f, 19f, 3f, 5f, 9.5f, 5f, 11.5f, 7.5f, 21f, 7.5f, 21f, 11f);
                Rect(13.5f, 15f, 7.5f, 6f, 1.2f);
                Arc(17.25f, 15f, 2f, 180f, 180f);
                break;
            case IconKind.Home:
                Line(3f, 12f, 12f, 3.5f);
                Line(12f, 3.5f, 21f, 12f);
                Line(6f, 11f, 6f, 20f);
                Line(18f, 11f, 18f, 20f);
                Line(6f, 20f, 18f, 20f);
                break;
        }
    }

    // ------------------------------------------------------------------ tray icon
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Rounded-square tray icon filled with stateColor and a bold white 'Z' (polygon path, pixel-aligned at 16px).
    /// ring = white 1px inner outline (attention / pending state). size: 16 (tray) or 32 (hi-DPI / form icon).
    /// Handle cleanup: the HICON from GetHicon is copied into a managed Icon (Clone) and destroyed immediately,
    /// so the returned Icon owns its handle — Dispose() the old Icon when you replace NotifyIcon.Icon.
    /// </summary>
    public static Icon MakeTrayIcon(Color stateColor, bool ring = false, int size = 16)
    {
        size = Math.Clamp(size, 16, 256);
        float k = size / 16f;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var r = new RectangleF(0, 0, size, size);
            using (var path = RoundRect(r, 4f * k))
            using (var brush = new SolidBrush(Color.FromArgb(255, stateColor)))
            {
                g.FillPath(brush, path);
            }
            if (ring) StrokeRound(g, r, 4f * k, Color.FromArgb(235, 255, 255, 255), Math.Max(1f, k));

            float[] z = { 4, 4, 12, 4, 12, 6, 7, 10, 12, 10, 12, 12, 4, 12, 4, 10, 9, 6, 4, 6 };
            var pts = new PointF[z.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(z[i * 2] * k, z[i * 2 + 1] * k);
            using var zp = new GraphicsPath();
            zp.AddPolygon(pts);
            using var wb = new SolidBrush(Color.White);
            g.FillPath(wb, zp);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    // ------------------------------------------------------------------ classic menus
    /// <summary>Applies ZeroMenuRenderer + Body font + padding to a ContextMenuStrip (and its submenus).</summary>
    public static void StyleMenu(ContextMenuStrip menu)
    {
        menu.Renderer = new ZeroMenuRenderer();
        menu.Font = Body;
        menu.BackColor = MenuBg;
        menu.ForeColor = TextPrimary;
        menu.Padding = new Padding(0, S1, 0, S1);
        menu.DropShadowEnabled = true;
        if (menu.Items.Count > 0)
        {
            bool needsMargin = UsesChecksOrImages(menu.Items);
            menu.ShowImageMargin = needsMargin;
            menu.ShowCheckMargin = false;
        }
        StyleItems(menu.Items);
    }

    private static bool UsesChecksOrImages(ToolStripItemCollection items)
    {
        foreach (ToolStripItem it in items)
        {
            if (it.Image != null) return true;
            if (it is ToolStripMenuItem mi && (mi.Checked || mi.CheckOnClick)) return true;
        }
        return false;
    }

    private static void StyleItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem it in items)
        {
            it.ForeColor = TextPrimary;
            if (it is ToolStripMenuItem mi)
            {
                mi.Padding = new Padding(mi.Padding.Left, 3, mi.Padding.Right, 3);
                if (mi.HasDropDownItems && mi.DropDown is ToolStripDropDownMenu dd)
                {
                    dd.Renderer = new ZeroMenuRenderer();
                    dd.Font = Body;
                    dd.BackColor = MenuBg;
                    dd.ForeColor = TextPrimary;
                    dd.Padding = new Padding(0, S1, 0, S1);
                    dd.ShowImageMargin = UsesChecksOrImages(mi.DropDownItems);
                    dd.ShowCheckMargin = false;
                    StyleItems(mi.DropDownItems);
                }
            }
        }
    }

    // ------------------------------------------------------------------ standard WinForms controls
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    /// <summary>Dark title bar for standard-border dialogs (applied now or on HandleCreated).</summary>
    public static void ApplyDarkTitleBar(Form form)
    {
        void Apply()
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            }
            catch { /* older Windows: ignore */ }
        }
        if (form.IsHandleCreated) Apply();
        else form.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Bg1 background, TextPrimary, Body font, dark title bar. styleChildren=true also styles Buttons/TextBoxes/Labels recursively.</summary>
    public static void StyleForm(Form form, bool styleChildren = false)
    {
        form.BackColor = Bg1;
        form.ForeColor = TextPrimary;
        form.Font = Body;
        ApplyDarkTitleBar(form);
        if (styleChildren) StyleChildren(form, form);
    }

    private static void StyleChildren(Control parent, Form form)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case Button b:
                    StyleButton(b, ReferenceEquals(form.AcceptButton, b) ? ButtonKind.Primary : ButtonKind.Secondary);
                    break;
                case TextBox t:
                    StyleTextBox(t);
                    break;
                case Label l:
                    StyleLabel(l);
                    break;
            }
            if (c.HasChildren) StyleChildren(c, form);
        }
    }

    /// <summary>Flat WinForms button with per-state colours (opaque composites over Bg1), BodyStrong, hand cursor, min height 32.</summary>
    public static void StyleButton(Button b, ButtonKind kind)
    {
        Color baseBg = b.Parent?.BackColor is Color pc && pc.A == 255 ? pc : Bg1;
        Color Op(Color c) => c.A == 255 ? c : Composite(c, baseBg);

        var normal = ButtonColors(kind, ButtonState.Normal);
        var hover = ButtonColors(kind, ButtonState.Hover);
        var pressed = ButtonColors(kind, ButtonState.Pressed);
        var disabled = ButtonColors(kind, ButtonState.Disabled);

        b.UseVisualStyleBackColor = false;
        b.FlatStyle = FlatStyle.Flat;
        bool bordered = kind is ButtonKind.Secondary or ButtonKind.Outline or ButtonKind.Danger;
        b.FlatAppearance.BorderSize = bordered ? 1 : 0;
        b.FlatAppearance.BorderColor = Op(bordered ? normal.Border : normal.Back);
        b.FlatAppearance.MouseOverBackColor = Op(hover.Back);
        b.FlatAppearance.MouseDownBackColor = Op(pressed.Back);
        b.Font = BodyStrong;
        b.Cursor = Cursors.Hand;
        b.MinimumSize = new Size(Math.Max(b.MinimumSize.Width, 72), Math.Max(b.MinimumSize.Height, (int)ButtonHeight));
        if (b.Height < ButtonHeight) b.Height = (int)ButtonHeight;

        void ApplyState()
        {
            var c = b.Enabled ? normal : disabled;
            b.BackColor = Op(c.Back);
            b.ForeColor = c.Fore;
        }
        ApplyState();
        b.EnabledChanged += (_, _) => ApplyState();
    }

    /// <summary>SurfaceRaised background, TextPrimary, FixedSingle border, Body font.</summary>
    public static void StyleTextBox(TextBox t)
    {
        t.BackColor = SurfaceRaised;
        t.ForeColor = TextPrimary;
        t.BorderStyle = BorderStyle.FixedSingle;
        t.Font = Body;
    }

    /// <summary>Transparent label: Body/TextPrimary, or Small/TextSecondary when secondary.</summary>
    public static void StyleLabel(Label l, bool secondary = false)
    {
        l.BackColor = Color.Transparent;
        l.ForeColor = secondary ? TextSecondary : TextPrimary;
        l.Font = secondary ? Small : Body;
    }
}

// ============================================================================
// Classic ContextMenuStrip renderer (dark, rounded accent-subtle hover).
// ============================================================================
internal sealed class ZeroColorTable : ProfessionalColorTable
{
    public ZeroColorTable() { UseSystemColors = false; }

    private static Color Bg => Theme.MenuBg;
    private static Color BorderOpaque => Theme.Composite(Theme.BorderStrong, Theme.MenuBg);
    private static Color HoverOpaque => Theme.Composite(Theme.AccentSubtle, Theme.MenuBg);

    public override Color ToolStripDropDownBackground => Bg;
    public override Color MenuBorder => BorderOpaque;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => HoverOpaque;
    public override Color MenuItemSelectedGradientBegin => HoverOpaque;
    public override Color MenuItemSelectedGradientEnd => HoverOpaque;
    public override Color MenuItemPressedGradientBegin => HoverOpaque;
    public override Color MenuItemPressedGradientMiddle => HoverOpaque;
    public override Color MenuItemPressedGradientEnd => HoverOpaque;
    public override Color ImageMarginGradientBegin => Bg;
    public override Color ImageMarginGradientMiddle => Bg;
    public override Color ImageMarginGradientEnd => Bg;
    public override Color SeparatorDark => Theme.Composite(Theme.Divider, Theme.MenuBg);
    public override Color SeparatorLight => Bg;
    public override Color CheckBackground => Color.Transparent;
    public override Color CheckSelectedBackground => Color.Transparent;
    public override Color CheckPressedBackground => Color.Transparent;
    public override Color ButtonSelectedBorder => Color.Transparent;
    public override Color ToolStripBorder => BorderOpaque;
}

internal sealed class ZeroMenuRenderer : ToolStripProfessionalRenderer
{
    public ZeroMenuRenderer() : base(new ZeroColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.MenuBg);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Composite(Theme.BorderStrong, Theme.MenuBg), 1f);
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.MenuBg);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(4f, 1f, e.Item.Width - 8f, e.Item.Height - 2f);
        Theme.FillRound(g, r, Theme.RadiusSmall, Theme.AccentSubtle);
        g.SmoothingMode = old;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Composite(Theme.Divider, Theme.MenuBg), 1f);
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? Theme.TextDisabled : Theme.TextSecondary;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        float sz = Math.Min(16f, Math.Min(r.Width, r.Height));
        var box = new RectangleF(r.X + (r.Width - sz) / 2f, r.Y + (r.Height - sz) / 2f, sz, sz);
        Theme.DrawIcon(e.Graphics, IconKind.Check, box, e.Item.Enabled ? Theme.Accent : Theme.TextDisabled, 1.8f);
    }
}
