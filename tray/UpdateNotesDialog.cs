using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

/// <summary>ZeroIDE-styled dark dialog showing release notes («What's new») for an available or installed update.</summary>
internal sealed class UpdateNotesDialog : Form
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    private const int WM_NCLBUTTONDOWN = 0xA1, HT_CAPTION = 0x2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    private const float HeaderH = 54f;
    private const float MetaH = 42f;
    private const float FooterH = 64f;

    private sealed record HitZone(RectangleF Rect, string Id, Action OnClick);
    private sealed record NoteItem(NoteKind Kind, string Text, float Y, float Height);
    private enum NoteKind { Heading, Bullet, Paragraph, Spacer }

    private readonly ReleaseAssetInfo _release;
    private readonly string _currentVersion;
    private readonly bool _showInstallButton;
    private readonly List<HitZone> _hits = new();
    private readonly List<NoteItem> _items = new();
    private float _contentH;
    private int _layoutWidth = -1;
    private float _scrollY;
    private float _maxScrollY;
    private RectangleF _cardRect;
    private RectangleF _vTrackRect;
    private RectangleF _vThumbRect;
    private bool _draggingScroll;
    private float _dragGrabOffset;
    private Point _mousePos = new(-1, -1);
    private string _hoveredId = "";

    public UpdateNotesDialog(ReleaseAssetInfo release, string currentVersion, bool showInstallButton = true)
    {
        _release = release;
        _currentVersion = currentVersion;
        _showInstallButton = showInstallButton;

        Text = I18n.T("What's new", "Что нового") + " — " + release.Tag;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 520);
        BackColor = Theme.Bg1;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = false;
        Theme.ApplyLogo(this);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        Shown += (_, _) => { Activate(); BringToFront(); };
        ApplyRoundedRegion();
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

    private void ApplyRoundedRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), 16f);
        var prev = Region;
        Region = new Region(path);
        prev?.Dispose();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _layoutWidth = -1;
        ApplyRoundedRegion();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        if (keyData == Keys.Down) { ScrollBy(36f); return true; }
        if (keyData == Keys.Up) { ScrollBy(-36f); return true; }
        if (keyData == Keys.PageDown) { ScrollBy(Math.Max(80f, _cardRect.Height - 40f)); return true; }
        if (keyData == Keys.PageUp) { ScrollBy(-Math.Max(80f, _cardRect.Height - 40f)); return true; }
        if (keyData == Keys.Home) { _scrollY = 0; Invalidate(); return true; }
        if (keyData == Keys.End) { _scrollY = _maxScrollY; Invalidate(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ScrollBy(float delta)
    {
        if (_maxScrollY <= 0) return;
        _scrollY = Math.Clamp(_scrollY + delta, 0, _maxScrollY);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_maxScrollY <= 0) return;
        _scrollY = Math.Clamp(_scrollY - e.Delta * 0.45f, 0, _maxScrollY);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mousePos = e.Location;
        if (_draggingScroll && _maxScrollY > 0 && _vTrackRect.Height > _vThumbRect.Height)
        {
            float rel = (e.Y - _vTrackRect.Top - _dragGrabOffset) / (_vTrackRect.Height - _vThumbRect.Height);
            _scrollY = Math.Clamp(rel, 0f, 1f) * _maxScrollY;
            Invalidate();
            return;
        }
        var hit = _hits.FirstOrDefault(h => h.Rect.Contains(e.Location));
        string nid = hit?.Id ?? "";
        bool overScroll = _maxScrollY > 0 && _vTrackRect.Contains(e.Location);
        Cursor = !string.IsNullOrEmpty(nid) || overScroll ? Cursors.Hand : Cursors.Default;
        if (nid != _hoveredId) { _hoveredId = nid; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mousePos = new Point(-1, -1);
        Cursor = Cursors.Default;
        if (!string.IsNullOrEmpty(_hoveredId)) { _hoveredId = ""; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _draggingScroll = false;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (_maxScrollY > 0 && _vTrackRect.Contains(e.Location))
        {
            if (_vThumbRect.Contains(e.Location))
            {
                _draggingScroll = true;
                _dragGrabOffset = e.Y - _vThumbRect.Top;
            }
            else if (_vTrackRect.Height > _vThumbRect.Height)
            {
                float rel = (e.Y - _vTrackRect.Top - _vThumbRect.Height / 2f) / (_vTrackRect.Height - _vThumbRect.Height);
                _scrollY = Math.Clamp(rel, 0f, 1f) * _maxScrollY;
                Invalidate();
            }
            return;
        }

        foreach (var hit in _hits.ToList())
        {
            if (hit.Rect.Contains(e.Location)) { hit.OnClick(); return; }
        }
        if (e.Y <= HeaderH)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }
    }

    internal void RenderPreviewToFile(string filePath)
    {
        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) PaintDialog(g);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void OnPaint(PaintEventArgs e) => PaintDialog(e.Graphics);

    private void EnsureLayout(Graphics g, float textW)
    {
        int wKey = (int)Math.Round(textW);
        if (_layoutWidth == wKey) return;
        _layoutWidth = wKey;
        _items.Clear();

        string raw = (_release.ReleaseNotes ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) raw = (_release.NotesSummary ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
        {
            raw = I18n.T("Release notes are not included for this version. You can open the release page on GitHub.",
                         "Описание изменений для этой версии не указано. Подробности можно посмотреть на странице релиза GitHub.");
        }

        float y = 14f;
        var hFont = PanelTypography.Get(13.5f, true);
        var bFont = PanelTypography.Get(12.2f);
        var pFont = PanelTypography.Get(12.2f);
        bool firstBlock = true;

        foreach (string lineRaw in raw.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = lineRaw.Trim();
            if (trimmed.Length == 0)
            {
                y += 8f;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                string heading = trimmed.TrimStart('#', ' ', '\t');
                if (heading.Length == 0) continue;
                if (!firstBlock) y += 8f;
                float h = Math.Max(24f, MeasureWrappedHeight(g, heading, hFont, textW) + 2f);
                _items.Add(new NoteItem(NoteKind.Heading, heading, y, h));
                y += h + 4f;
                firstBlock = false;
                continue;
            }

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• "))
            {
                string bullet = StripInlineMarkdown(trimmed[2..].Trim());
                float h = Math.Max(22f, MeasureWrappedHeight(g, bullet, bFont, Math.Max(80f, textW - 18f)));
                _items.Add(new NoteItem(NoteKind.Bullet, bullet, y, h));
                y += h + 6f;
                firstBlock = false;
                continue;
            }

            string para = StripInlineMarkdown(trimmed);
            float ph = Math.Max(22f, MeasureWrappedHeight(g, para, pFont, textW));
            _items.Add(new NoteItem(NoteKind.Paragraph, para, y, ph));
            y += ph + 7f;
            firstBlock = false;
        }
        _contentH = y + 10f;
    }

    private static float MeasureWrappedHeight(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return 0f;
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.MeasureTrailingSpaces,
        };
        return (float)Math.Ceiling(g.MeasureString(text, font, new SizeF(maxWidth, 10000f), sf).Height) + 2f;
    }

    private static string StripInlineMarkdown(string s)
    {
        return s.Replace("**", "").Replace("__", "");
    }

    private void PaintDialog(Graphics g)
    {
        _hits.Clear();
        Theme.PrepareGraphics(g);
        var saved = g.Save();
        using var winPath = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 16f);
        g.SetClip(winPath);

        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), Theme.BgGradTop, Theme.BgGradBottom, 90f))
            g.FillRectangle(bg, 0, 0, Width, Height);

        // ---- Header
        Theme.DrawLogo(g, new RectangleF(18, 13, 28, 28));
        string title = I18n.T("What's new", "Что нового");
        var tFont = PanelTypography.Get(14.5f, true);
        float tW = Theme.MeasureText(g, title, tFont).Width;
        Theme.DrawText(g, title, tFont, Theme.TextPrimary, new RectangleF(56, 15, tW + 6, 24));

        var chipFont = PanelTypography.Get(11.5f, true);
        float chipW = Theme.MeasureChipOutlinedWidth(g, _release.Tag, false, chipFont);
        float cx = 56 + tW + 12f;
        Theme.DrawChipOutlined(g, new RectangleF(cx, 15, chipW, 24), _release.Tag, Theme.Accent, Theme.WithAlpha(Theme.Accent, 26), dot: false, font: chipFont);
        cx += chipW + 8f;

        if (_release.IsDemo)
        {
            string demoTxt = I18n.T("Demo", "Демо");
            var dFont = PanelTypography.Get(10.5f, true);
            float dw = Theme.MeasureChipOutlinedWidth(g, demoTxt, false, dFont);
            Theme.DrawChipOutlined(g, new RectangleF(cx, 16, dw, 22), demoTxt, Theme.Warning, Theme.WithAlpha(Theme.Warning, 24), dot: false, font: dFont);
        }

        var closeRect = new RectangleF(Width - 46, 6, 40, 40);
        bool closeHov = closeRect.Contains(_mousePos);
        if (closeHov) Theme.FillRound(g, closeRect, 10f, Theme.Danger);
        Theme.DrawIcon(g, IconKind.Close, new RectangleF(closeRect.X + 13, closeRect.Y + 13, 14, 14), closeHov ? Color.White : Theme.TextSecondary, 1.8f);
        _hits.Add(new HitZone(closeRect, "notes_close_x", () => { DialogResult = DialogResult.Cancel; Close(); }));
        Theme.DrawDivider(g, 0, Width, HeaderH);

        // ---- Metadata bar
        float metaY = HeaderH + 8f;
        string routeMeta = I18n.Format("Version {0}  →  {1}", "Версия {0}  →  {1}", _currentVersion, _release.Version);
        if (DateTimeOffset.TryParse(_release.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
        {
            string dateStr = dt.ToLocalTime().ToString("d MMM yyyy", new CultureInfo(I18n.IsRu ? "ru-RU" : "en-US"));
            routeMeta += "  ·  " + dateStr;
        }
        Theme.DrawText(g, routeMeta, PanelTypography.Get(12f), Theme.TextSecondary, new RectangleF(22, metaY, Width - 160, 22), StringAlignment.Near, StringAlignment.Center);

        string shaLbl = "SHA-256";
        var sFont = PanelTypography.Get(10.5f, true);
        float sW = Theme.MeasureChipOutlinedWidth(g, shaLbl, true, sFont);
        Theme.DrawChipOutlined(g, new RectangleF(Width - 20 - sW, metaY, sW, 22), shaLbl, Theme.Success, Theme.SuccessSubtle, dot: true, font: sFont);

        // ---- Notes card
        float cardTop = HeaderH + MetaH;
        float cardH = Math.Max(140f, Height - FooterH - cardTop - 12f);
        _cardRect = new RectangleF(20, cardTop, Width - 40, cardH);

        using (var cardPath = Theme.RoundRect(_cardRect, 12f))
        {
            using (var cbg = new SolidBrush(Theme.Bg0)) g.FillPath(cbg, cardPath);
            var st = g.Save();
            g.SetClip(cardPath, CombineMode.Intersect);

            float textX = _cardRect.X + 18f;
            float textW = Math.Max(120f, _cardRect.Width - 46f);
            EnsureLayout(g, textW);

            _maxScrollY = Math.Max(0f, _contentH - _cardRect.Height);
            _scrollY = Math.Clamp(_scrollY, 0f, _maxScrollY);

            var hFont = PanelTypography.Get(13.5f, true);
            var bFont = PanelTypography.Get(12.2f);
            var pFont = PanelTypography.Get(12.2f);

            foreach (var item in _items)
            {
                float iy = _cardRect.Y + item.Y - _scrollY;
                if (iy + item.Height < _cardRect.Y || iy > _cardRect.Bottom) continue;

                switch (item.Kind)
                {
                    case NoteKind.Heading:
                        Theme.DrawText(g, item.Text, hFont, Theme.TextPrimary, new RectangleF(textX, iy, textW, item.Height), StringAlignment.Near, StringAlignment.Near, ellipsis: false, wrap: true);
                        break;
                    case NoteKind.Bullet:
                        Theme.DrawStatusDot(g, new PointF(textX + 5f, iy + 9f), 2.8f, Theme.AccentText, halo: false);
                        Theme.DrawText(g, item.Text, bFont, Theme.TextSecondary, new RectangleF(textX + 18f, iy, Math.Max(80f, textW - 18f), item.Height), StringAlignment.Near, StringAlignment.Near, ellipsis: false, wrap: true);
                        break;
                    case NoteKind.Paragraph:
                        Theme.DrawText(g, item.Text, pFont, Theme.TextSecondary, new RectangleF(textX, iy, textW, item.Height), StringAlignment.Near, StringAlignment.Near, ellipsis: false, wrap: true);
                        break;
                }
            }

            if (_maxScrollY > 0)
            {
                _vTrackRect = new RectangleF(_cardRect.Right - 10f, _cardRect.Y + 6f, 6f, _cardRect.Height - 12f);
                float thumbH = Math.Max(28f, _vTrackRect.Height * (_cardRect.Height / Math.Max(_cardRect.Height, _contentH)));
                float thumbY = _vTrackRect.Top + (_vTrackRect.Height - thumbH) * (_scrollY / _maxScrollY);
                _vThumbRect = new RectangleF(_vTrackRect.X, thumbY, _vTrackRect.Width, thumbH);
                bool active = _draggingScroll || _vTrackRect.Contains(_mousePos);
                Theme.FillRound(g, _vThumbRect, 3f, Color.FromArgb(active ? 145 : 85, 220, 230, 250));
            }
            else
            {
                _vTrackRect = RectangleF.Empty;
                _vThumbRect = RectangleF.Empty;
            }

            g.Restore(st);
        }
        Theme.StrokeRound(g, _cardRect, 12f, Theme.Border);

        // ---- Footer
        float footerY = Height - FooterH;
        Theme.DrawDivider(g, 0, Width, footerY);
        float btnY = footerY + 14f;

        string ghLbl = I18n.T("Open on GitHub", "Открыть на GitHub");
        var btnFont = PanelTypography.Get(12.2f, true);
        float ghW = Math.Max(184f, Theme.MeasureText(g, ghLbl, btnFont).Width + 60f);
        var ghRect = new RectangleF(20, btnY, ghW, 36f);
        Theme.DrawButton(g, ghRect, ghLbl, ButtonKind.Secondary, ghRect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal, btnFont, 9f, icon: IconKind.Globe);
        _hits.Add(new HitZone(ghRect, "notes_github", () => DesktopLauncher.OpenUrl(_release.HtmlUrl)));

        float right = Width - 20f;
        if (_showInstallButton)
        {
            string updLbl = I18n.Format("Update to {0}", "Обновить до {0}", _release.Tag);
            float updW = Math.Max(158f, Theme.MeasureText(g, updLbl, btnFont).Width + 42f);
            var updRect = new RectangleF(right - updW, btnY, updW, 36f);
            Theme.DrawButton(g, updRect, updLbl, ButtonKind.Primary, updRect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal, btnFont, 9f);
            _hits.Add(new HitZone(updRect, "notes_install", () => { DialogResult = DialogResult.Yes; Close(); }));
            right = updRect.Left - 10f;
        }

        string closeLbl = I18n.T("Close", "Закрыть");
        float closeW = Math.Max(100f, Theme.MeasureText(g, closeLbl, btnFont).Width + 32f);
        var closeBtnRect = new RectangleF(right - closeW, btnY, closeW, 36f);
        Theme.DrawButton(g, closeBtnRect, closeLbl, _showInstallButton ? ButtonKind.Subtle : ButtonKind.Primary, closeBtnRect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal, btnFont, 9f);
        _hits.Add(new HitZone(closeBtnRect, "notes_close", () => { DialogResult = DialogResult.Cancel; Close(); }));

        using (var border = new Pen(Theme.BorderStrong)) g.DrawPath(border, winPath);
        g.Restore(saved);
    }
}
