using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

// v0.5 Settings → Changes: every file an agent wrote, edited, moved or deleted, with one-click undo.
// Drawn with the same primitives as «Journal and audit»; data comes from ChangeHistory (history/index.jsonl).
internal sealed partial class SettingsForm
{
    private const int ChangesVisible = 100;
    private ChangeSlice _changes = ChangeSlice.Empty;
    private Dictionary<string, UndoPlan> _changePlans = new(StringComparer.Ordinal);
    private HashSet<string> _changeCopies = new(StringComparer.Ordinal);
    private string _changesFilter = "all";
    private (long, long, long, long)? _changesStamp;
    private bool _changesLoading;
    private string _changesStatus = "";
    private bool _changesStatusOk = true;
    /// <summary>Test hook: answers the undo confirmation instead of a MessageBox.</summary>
    internal Func<AgentChange, UndoPlan, bool>? ConfirmUndoForTests = null;
    /// <summary>Test hook: intercepts «Show in folder» without spawning explorer.exe.</summary>
    internal Action<string>? RevealForTests = null;
    internal string ChangesStatus => _changesStatus;
    internal IReadOnlyList<AgentChange> ChangeRows => _changes.Rows;

    private static (long, long, long, long) HistoryStamp(string dir)
    {
        static (long, long) One(string f)
        {
            try { var fi = new FileInfo(f); return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : (-1, 0); }
            catch (Exception) { return (-2, 0); }
        }
        var a = One(Path.Combine(dir, ChangeHistory.IndexFile));
        var b = One(Path.Combine(dir, ChangeHistory.OwnerFile));
        return (a.Item1, a.Item2, b.Item1, b.Item2);
    }

    private static (ChangeSlice Slice, Dictionary<string, UndoPlan> Plans, HashSet<string> Copies) ReadChanges(string dir, bool ru)
    {
        var slice = ChangeHistory.Read(dir);
        var plans = new Dictionary<string, UndoPlan>(StringComparer.Ordinal);
        var copies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in slice.Rows)
        {
            plans[c.Id] = ChangeHistory.Plan(c, slice.Undone, dir, ru);
            if (c.CanCompare && File.Exists(c.Reverse.From)) copies.Add(c.Id);
        }
        return (slice, plans, copies);
    }

    /// <summary>Reloads on a worker thread when index.jsonl / owner.jsonl changed (or when forced).</summary>
    private void LoadChanges(bool force = false)
    {
        if (_changesPreview) return;
        string dir = _ctx.HistoryDirPath;
        var stamp = HistoryStamp(dir);
        if (_changesLoading || (!force && _changesStamp == stamp)) return;
        if (!IsHandleCreated) { LoadChangesNow(); return; }
        _changesLoading = true;
        bool ru = I18n.IsRu;
        Task.Run(() => ReadChanges(dir, ru)).ContinueWith(t =>
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    _changesLoading = false;
                    if (t.IsCompletedSuccessfully) { (_changes, _changePlans, _changeCopies) = t.Result; _changesStamp = stamp; }
                    if (_activeSection == "changes") Invalidate();
                });
            }
            catch (InvalidOperationException) { _changesLoading = false; }
        }, TaskScheduler.Default);
    }

    /// <summary>Preview renders (--preview, tests): fixed sample rows instead of the disk.</summary>
    private bool _changesPreview;
    internal void SetChangesForPreview((ChangeSlice Slice, Dictionary<string, UndoPlan> Plans, HashSet<string> Copies) data)
    {
        (_changes, _changePlans, _changeCopies) = data;
        _changesPreview = true;
    }

    // Test hooks: paint into a bitmap and press a hit zone exactly as a click would.
    internal void PaintForTests()
    {
        using var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        using var g = Graphics.FromImage(bmp);
        using var pe = new PaintEventArgs(g, new Rectangle(0, 0, Width, Height));
        OnPaint(pe);
    }
    internal void PaintForTests(int scrollY)
    {
        _scrollY = Math.Max(0, scrollY);
        PaintForTests();
        if (scrollY > 0)
        {
            _scrollY = Math.Clamp(scrollY, 0, Math.Max(0, _maxScrollY));
            PaintForTests();
        }
    }
    internal bool InvokeZoneForTests(string id) { var z = _hitZones.FirstOrDefault(h => h.Id == id); z?.OnClick(); return z != null; }
    internal IReadOnlyList<string> ZoneIdsForTests => _hitZones.Select(h => h.Id).ToList();
    internal string ZoneTooltipForTests(string id) => _hitZones.FirstOrDefault(h => h.Id == id)?.Tooltip ?? "";

    /// <summary>Synchronous load (first paint before the window has a handle, previews and tests).</summary>
    internal void LoadChangesNow()
    {
        if (_changesPreview) return;
        string dir = _ctx.HistoryDirPath;
        (_changes, _changePlans, _changeCopies) = ReadChanges(dir, I18n.IsRu);
        _changesStamp = HistoryStamp(dir);
    }

    private ChangeFilter CurrentChangeFilter => _changesFilter switch { "today" => ChangeFilter.Today, "deletions" => ChangeFilter.Deletions, _ => ChangeFilter.All };

    private static string ChangeKindText(AgentChange c) => c.Kind switch
    {
        ChangeKind.Created => I18n.T("Created", "Создан"),
        ChangeKind.Modified => c.Edits > 1 ? I18n.T("Edited ×", "Изменён ×") + c.Edits : I18n.T("Edited", "Изменён"),
        ChangeKind.Replaced => I18n.T("Replaced", "Заменён"),
        ChangeKind.Deleted => c.IsDir ? I18n.T("Folder deleted", "Папка удалена") : I18n.T("Deleted", "Удалён"),
        ChangeKind.Moved => I18n.T("Moved", "Перемещён"),
        ChangeKind.Copied => I18n.T("Copied", "Скопирован"),
        ChangeKind.Folder => I18n.T("Folder created", "Папка создана"),
        _ => I18n.T("Your undo", "Ваша отмена"),
    };

    private static (IconKind Icon, Color Tint) ChangeLook(AgentChange c) => c.Kind switch
    {
        ChangeKind.Created => (IconKind.Plus, ColGreen),
        ChangeKind.Modified => (IconKind.Edit, ColBlue),
        ChangeKind.Replaced => (IconKind.Copy, ColOrange),
        ChangeKind.Deleted => (IconKind.Trash, ColRed),
        ChangeKind.Moved => (IconKind.Folder, ColCyan),
        ChangeKind.Copied => (IconKind.Copy, ColGreen),
        ChangeKind.Folder => (IconKind.Folder, ColGreen),
        _ => (IconKind.History, ColOrange),
    };

    private float DrawChangesContent(Graphics g, float x, float w, float y)
    {
        bool compactToolbar = w < 800f;
        float actionsY = y + (compactToolbar ? 44f : 1f);
        DrawSegmentedBar(
            g,
            new RectangleF(x, y, 400f, 32f),
            new (string Key, string Title, Color ActiveCol)[]
            {
                ("all", I18n.T("All changes", "Все изменения"), ColBlue),
                ("today", I18n.T("Today", "Сегодня"), ColBlue),
                ("deletions", I18n.T("Deletions", "Удаления"), ColRed),
            },
            _changesFilter,
            "chg_flt",
            picked =>
            {
                _changesFilter = picked;
                _scrollY = 0;
                Invalidate();
            });

        float rx = x + w;
        rx = DrawPillButtonRight(g, rx, actionsY, I18n.T("Refresh", "Обновить"), ColPillIdle, ColTextPrimary, "chg_refresh", () => { _changesStatus = ""; LoadChanges(force: true); Invalidate(); }) - 8f;
        DrawPillButtonRight(g, rx, actionsY, I18n.T("History folder", "Папка истории"), ColPillIdle, ColTextPrimary, "chg_open_dir", OpenHistoryFolder);
        y += compactToolbar ? 90f : 46f;

        if (_changesStatus.Length > 0)
        {
            float top = y;
            y = DrawTintBanner(g, x, w, y, _changesStatus, _changesStatusOk ? ColGreen : ColRed, _changesStatusOk ? IconKind.Check : IconKind.Warning);
            _hitZones.Add(new HitZone(new RectangleF(x, top, w, y - top), "chg_status", () => { _changesStatus = ""; Invalidate(); }, I18n.T("Click to hide", "Нажмите, чтобы скрыть")));
            y += 14f;
        }

        var filter = CurrentChangeFilter;
        var rows = _changes.Rows.Where(c => c.Matches(filter, "", ChangeKindText(c))).ToList();
        string head = I18n.T("RECENT AGENT CHANGES", "ПОСЛЕДНИЕ ИЗМЕНЕНИЯ АГЕНТОВ") + (rows.Count > 0 ? "  ·  " + rows.Count : "");
        y = DrawGroupTitle(g, x, y, head);

        const float rowH = 64f;
        if (rows.Count == 0)
        {
            var card = new RectangleF(x, y, w, rowH);
            DrawCardSurface(g, card);
            (string title, string sub) = _changesLoading && _changes.Rows.Count == 0
                ? (I18n.T("Reading history…", "Читаем историю…"), "")
                : _changes.State switch
                {
                    HistoryState.Unavailable => (I18n.T("The history could not be read", "Не удалось прочитать историю"), I18n.T("Check access to the history folder and press Refresh.", "Проверьте доступ к папке истории и нажмите «Обновить».")),
                    _ when _changes.Rows.Count == 0 => (I18n.T("Agents have not changed any files yet", "Агенты пока ничего не меняли"), I18n.T("Every write, edit, move or delete appears here with its previous version.", "Каждая запись, правка, перенос или удаление появится здесь вместе с прежней версией.")),
                    _ => (I18n.T("No changes for the selected filter", "Нет изменений для выбранного фильтра"), I18n.T("Choose «All changes» to see the full list.", "Выберите «Все изменения», чтобы увидеть весь список.")),
                };
            DrawSettingRow(g, x, y, w, rowH, Theme.TextTertiary, "history", title, sub, drawDivider: false);
            y += rowH + 16f;
        }
        else
        {
            var visible = rows.Take(ChangesVisible).ToList();
            var card = new RectangleF(x, y, w, visible.Count * rowH);
            DrawCardSurface(g, card);
            for (int i = 0; i < visible.Count; i++)
            {
                if (y + rowH >= 0 && y <= Height) DrawChangeRow(g, visible[i], x, y, w, rowH, i < visible.Count - 1);
                y += rowH;
            }
            y += 10f;
            if (rows.Count > visible.Count || _changes.Limited)
            {
                Theme.DrawText(g, I18n.Format("Showing the latest {0} of {1}. Older entries stay in the history folder.", "Показаны последние {0} из {1}. Более ранние записи остаются в папке истории.", visible.Count, rows.Count),
                    UiCaption, Theme.TextTertiary, new RectangleF(x + 4f, y, w - 8f, 20f));
                y += 26f;
            }
        }

        y = DrawInfoBanner(g, x, w, y, I18n.T(
            "Before an agent overwrites, edits or deletes a file, ZeroIDE keeps the previous version for 30 days. Undo never destroys anything: the current version is saved first, so an undo can be undone too.",
            "Перед тем как агент перезапишет, изменит или удалит файл, ZeroIDE сохраняет прежнюю версию на 30 дней. Отмена ничего не уничтожает: текущая версия сначала сохраняется, поэтому и отмену можно отменить."));
        return y + 16f;
    }

    private void DrawChangeRow(Graphics g, AgentChange c, float x, float y, float w, float rowH, bool divider)
    {
        _changePlans.TryGetValue(c.Id, out var plan);
        bool undone = _changes.Undone.Contains(c.Id);
        var (icon, tint) = ChangeLook(c);
        bool compact = w < 820f;

        var tile = new RectangleF(x + 16f, y + (rowH - 36f) / 2f, 36f, 36f);
        DrawTintTile(g, tile, icon, undone ? Theme.TextTertiary : tint, 10f, 18f);

        // Right edge: reveal, undo (or why not), compare.
        float right = x + w - 16f, btnY = y + (rowH - 32f) / 2f;
        var reveal = new RectangleF(right - 32f, btnY, 32f, 32f);
        bool revHov = reveal.Contains(_mousePos);
        Theme.FillRound(g, reveal, Theme.RadiusControl, revHov ? Theme.SurfaceHover : Theme.SurfaceRaised);
        Theme.StrokeRound(g, reveal, Theme.RadiusControl, Theme.Border);
        Theme.DrawIcon(g, IconKind.Folder, new RectangleF(reveal.X + 8f, reveal.Y + 8f, 16f, 16f), revHov ? ColTextPrimary : ColTextSecondary, 1.7f);
        _hitZones.Add(new HitZone(reveal, "chg_reveal_" + c.Id, () => RevealChange(c), I18n.T("Show in folder", "Показать в папке")));
        right = reveal.Left - 8f;

        if (plan is { Possible: true } && !undone)
        {
            right = DrawPillButtonRight(g, right, btnY, I18n.T("Undo", "Отменить"), ColBlue, Color.White, "chg_undo_" + c.Id, () => UndoChange(c)) - 8f;
            _hitZones[^1] = _hitZones[^1] with { Tooltip = WrapTooltipText(plan.Action + "\n" + plan.Detail) };
        }
        else
        {
            string why = undone ? I18n.T("Already undone", "Уже отменено") : plan?.Action ?? "";
            var font = UiSmall;
            float ww = Math.Min(170f, Theme.MeasureText(g, why, font).Width + 6f);
            var r = new RectangleF(right - ww, btnY, ww, 32f);
            Theme.DrawText(g, why, font, Theme.TextTertiary, r, StringAlignment.Far, StringAlignment.Center);
            string detail = undone ? I18n.T("The undo is listed as its own row and can be reverted there.", "Сама отмена записана отдельной строкой — её можно вернуть там.") : plan?.Detail ?? "";
            if (detail.Length > 0) { string tip = WrapTooltipText(why + "\n" + detail); _hitZones.Add(new HitZone(r, "chg_why_" + c.Id, () => _toolTip.Show(tip, this, new Point((int)r.X, (int)r.Bottom + 4), 5000), tip)); }
            right = r.Left - 10f;
        }
        if (!undone && _changeCopies.Contains(c.Id))
        {
            right = DrawPillButtonRight(g, right, btnY, I18n.T("Compare", "Сравнить"), ColPillIdle, ColTextPrimary, "chg_cmp_" + c.Id, () => CompareChange(c)) - 8f;
            _hitZones[^1] = _hitZones[^1] with { Tooltip = I18n.T("Kept copy against the file on disk", "Сохранённая копия против файла на диске") };
        }

        // Meta column: when and who.
        string who = c.Kind == ChangeKind.Undo ? I18n.T("you", "вы") : c.Agent.Length > 0 ? c.Agent : I18n.T("agent", "агент");
        string when = c.Stamp is { } st ? (c.IsToday ? st.ToLocalTime().ToString("HH:mm:ss") : st.ToLocalTime().ToString("d MMM, HH:mm", new CultureInfo(I18n.IsRu ? "ru-RU" : "en-US"))) : "--:--";
        float metaW = compact ? 0f : 150f;
        if (!compact)
        {
            Theme.DrawText(g, when, UiSmallStrong, undone ? Theme.TextTertiary : ColTextSecondary, new RectangleF(right - metaW, y + 11f, metaW, 20f), StringAlignment.Far);
            Theme.DrawText(g, who, UiSmall, Theme.TextTertiary, new RectangleF(right - metaW, y + 33f, metaW, 20f), StringAlignment.Far);
            right -= metaW + 14f;
        }

        // Text: name · kind chip · agent note, then the path.
        float tx = tile.Right + 14f, tw = Math.Max(60f, right - tx);
        string name = c.Name;
        float nameW = Math.Min(Theme.MeasureText(g, name, UiBodyStrong).Width + 2f, tw * 0.62f);
        Theme.DrawText(g, name, UiBodyStrong, undone ? ColTextSecondary : ColTextPrimary, new RectangleF(tx, y + 9f, nameW, 23f));
        float cx = tx + nameW + 10f;
        DrawInfoChip(g, ref cx, tx + tw, y + 9.5f, undone ? IconKind.History : null, undone ? I18n.T("Undone", "Отменено") : ChangeKindText(c), null, null, undone ? null : tint);
        if (!undone && c.Risk is "high" or "critical") DrawInfoChip(g, ref cx, tx + tw, y + 9.5f, IconKind.Warning, I18n.T("risk", "риск"), null, null, ColRed);
        if (c.Note.Length > 0 && cx < tx + tw - 40f)
            Theme.DrawText(g, "«" + c.Note + "»", UiSmall, Theme.TextTertiary, new RectangleF(cx + 2f, y + 9f, tx + tw - cx - 2f, 23f));
        string where = c.Kind is ChangeKind.Moved or ChangeKind.Copied && c.To.Length > 0 ? c.Path + "  →  " + c.To : c.Path;
        if (compact) where = when + "  ·  " + who + "  ·  " + where;
        Theme.DrawText(g, where, Theme.FontMono(9f), undone ? Theme.TextTertiary : ColTextSecondary, new RectangleF(tx, y + 35f, tw, 20f));

        if (divider) Theme.DrawDivider(g, x + 66f, x + w - 16f, y + rowH - 0.5f);
    }

    private static float DrawTintBanner(Graphics g, float x, float w, float y, string text, Color tint, IconKind icon)
    {
        var font = PanelTypography.Get(12.5f);
        float textW = w - 54f;
        float textH = Theme.MeasureText(g, text, font, textW).Height;
        float h = Math.Max(40f, textH + 20f);
        var r = new RectangleF(x, y, w, h);
        Theme.FillRound(g, r, 11f, Theme.WithAlpha(tint, 36));
        Theme.StrokeRound(g, r, 11f, Theme.WithAlpha(tint, 76));
        Theme.DrawIcon(g, icon, new RectangleF(x + 14f, y + (Math.Min(h, 40f) - 16f) / 2f, 16f, 16f), Theme.TextVariant(tint), 1.7f);
        Theme.DrawText(g, text, font, Theme.TextVariant(tint), new RectangleF(x + 40f, y, textW, h), vertical: StringAlignment.Center, ellipsis: false, wrap: true);
        return y + h;
    }

    private void OpenHistoryFolder()
    {
        string dir = _ctx.HistoryDirPath;
        try { Directory.CreateDirectory(dir); } catch (Exception) { }
        DesktopLauncher.OpenFolder(dir);
    }

    private void UndoChange(AgentChange c)
    {
        string dir = _ctx.HistoryDirPath;
        bool ru = I18n.IsRu;
        var plan = ChangeHistory.Plan(c, _changes.Undone, dir, ru); // fresh: the disk may have changed since the last paint
        if (!plan.Possible) { _changesStatus = plan.Action + (plan.Detail.Length > 0 ? ": " + plan.Detail : ""); _changesStatusOk = false; LoadChanges(force: true); Invalidate(); return; }
        string where = c.Kind == ChangeKind.Moved ? c.To : c.Path;
        bool ok = ConfirmUndoForTests != null
            ? ConfirmUndoForTests(c, plan)
            : MessageBox.Show(this, plan.Action + "?\n\n" + where + "\n\n" + plan.Detail, Theme.ProductName, MessageBoxButtons.YesNo,
                plan.Caution ? MessageBoxIcon.Warning : MessageBoxIcon.Question, plan.Caution ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) == DialogResult.Yes;
        if (!ok) return;
        try
        {
            string changed = ChangeHistory.Undo(c, _changes.Undone, dir, ru);
            _changesStatus = I18n.Format("Done: {0} — {1}", "Готово: {0} — {1}", plan.Action, changed);
            _changesStatusOk = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _changesStatus = I18n.T("Could not undo: ", "Не удалось отменить: ") + e.Message;
            _changesStatusOk = false;
        }
        if (IsHandleCreated) LoadChanges(force: true); else LoadChangesNow();
        Invalidate();
    }

    private void CompareChange(AgentChange c)
    {
        var (_, from, dest) = c.Reverse;
        if (!ChangeHistory.LooksText(from, out string old))
        {
            MessageBox.Show(this, I18n.T("Only text files up to 2 MB can be compared.", "Сравнить можно только текстовые файлы до 2 МБ."), Theme.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string now = "";
        if (File.Exists(dest) && !ChangeHistory.LooksText(dest, out now))
        {
            MessageBox.Show(this, I18n.T("The current file is not text or is larger than 2 MB.", "Текущий файл не текстовый или больше 2 МБ."), Theme.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string label = c.Stamp is { } st ? st.ToLocalTime().ToString("HH:mm") : "";
        _changePlans.TryGetValue(c.Id, out var plan);
        bool canUndo = plan is { Possible: true } && !_changes.Undone.Contains(c.Id);
        using var dlg = new ChangeCompareForm(
            c.Name,
            label,
            old,
            now,
            File.Exists(dest),
            fullPath: c.Shown,
            agent: c.Agent,
            note: c.Note,
            onReveal: () => RevealChange(c),
            canUndo: canUndo);
        if (dlg.ShowDialog(this) == DialogResult.Retry && canUndo)
        {
            UndoChange(c);
        }
    }

    private void RevealChange(AgentChange c)
    {
        string target = c.Shown;
        if (RevealForTests != null)
        {
            RevealForTests(target);
            return;
        }
        if (!DesktopLauncher.RevealInFolder(target))
        {
            _changesStatus = I18n.T("Could not open folder: the file and its parent folder no longer exist.", "Не удалось открыть папку: файл и его папка больше не существуют.");
            _changesStatusOk = false;
            Invalidate();
        }
    }
}

/// <summary>ZeroIDE-styled dark diff viewer: compares the kept copy against the current file on disk.</summary>
internal sealed class ChangeCompareForm : Form
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    private const int WM_NCLBUTTONDOWN = 0xA1, HT_CAPTION = 0x2;
    private const int WM_NCHITTEST = 0x84;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    private const float HeaderH = 54f;
    private const float FooterH = 64f;
    private const float GutterW = 104f;
    private const float RowH = 22f;
    private const float GapRowH = 28f;
    private const int MaxRenderedLines = 4000;

    private sealed record HitZone(RectangleF Rect, string Id, Action OnClick, string? Tooltip = null);
    private sealed record VisualRow(DiffLine Source, string Segment, bool IsContinuation, float Y, float Height, int SourceIndex);

    private readonly string _name;
    private readonly string _keptAt;
    private readonly string _fullPath;
    private readonly string _agent;
    private readonly string _note;
    private readonly bool _fileExists;
    private readonly bool _canUndo;
    private readonly Action? _onReveal;

    private readonly List<DiffLine>? _changesLines;
    private readonly List<DiffLine>? _fullLines;
    private readonly int _plusCount;
    private readonly int _minusCount;
    private readonly int _hunkCount;

    private readonly List<HitZone> _hits = new();
    private readonly ToolTip _toolTip = new() { InitialDelay = 300, ReshowDelay = 120, AutoPopDelay = 7000 };
    private readonly System.Windows.Forms.Timer _feedbackTimer = new() { Interval = 2200 };

    private bool _fullMode;
    private bool _wordWrap;
    private int _selectedSourceIndex = -1;
    private float _scrollY;
    private float _maxScrollY;
    private float _scrollX;
    private float _maxScrollX;

    private Point _mousePos = new(-1, -1);
    private string _hoveredId = "";
    private string? _lastTip;
    private string _feedback = "";
    private bool _feedbackOk = true;

    private RectangleF _bodyRect;
    private RectangleF _vTrackRect;
    private RectangleF _vThumbRect;
    private RectangleF _hTrackRect;
    private RectangleF _hThumbRect;
    private bool _draggingVScroll;
    private bool _draggingHScroll;
    private float _dragGrabOffset;

    private List<VisualRow> _visualRows = new();
    private float _totalContentH;
    private float _maxLineWidth;
    private (bool Full, bool Wrap, int BodyW) _layoutKey = (false, false, -1);

    private static Font UiFont(float px, bool bold = false) => PanelTypography.Get(px, bold);
    private static Font MonoFont => Theme.FontMono(9.5f);
    private static Font MonoNumFont => Theme.FontMono(8.5f);

    /// <summary>Plain unified diff text (also used by Copy and UI tests).</summary>
    internal string DiffText { get; }

    /// <summary>Current visible diff lines according to the selected view mode.</summary>
    internal IReadOnlyList<DiffLine> VisibleLines => CurrentLines ?? (IReadOnlyList<DiffLine>)Array.Empty<DiffLine>();

    internal bool FullFileMode
    {
        get => _fullMode;
        set
        {
            if (_fullMode == value) return;
            _fullMode = value;
            _scrollY = 0;
            _scrollX = 0;
            _selectedSourceIndex = -1;
            _layoutKey = (false, false, -1);
            Invalidate();
        }
    }

    internal bool WordWrapMode
    {
        get => _wordWrap;
        set
        {
            if (_wordWrap == value) return;
            _wordWrap = value;
            _scrollX = 0;
            _layoutKey = (false, false, -1);
            Invalidate();
        }
    }

    private List<DiffLine>? CurrentLines => _fullMode ? _fullLines : _changesLines;

    internal ChangeCompareForm(
        string name,
        string keptAt,
        string before,
        string after,
        bool fileExists,
        string fullPath = "",
        string agent = "",
        string note = "",
        Action? onReveal = null,
        bool canUndo = false)
    {
        _name = string.IsNullOrWhiteSpace(name) ? "file" : name;
        _keptAt = keptAt ?? "";
        _fullPath = fullPath ?? "";
        _agent = agent ?? "";
        _note = note ?? "";
        _fileExists = fileExists;
        _onReveal = onReveal;
        _canUndo = canUndo;

        Text = I18n.T("Compare versions", "Сравнение версий") + " — " + _name;
        Theme.ApplyLogo(this);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(960, 640);
        MinimumSize = new Size(640, 440);
        BackColor = Theme.Bg1;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _changesLines = ChangeHistory.DiffDetailed(before, after, context: 3, fullFile: false);
        _fullLines = _changesLines == null ? null : ChangeHistory.DiffDetailed(before, after, context: 3, fullFile: true);
        if (_changesLines != null)
        {
            bool inHunk = false;
            foreach (var l in _changesLines)
            {
                if (l.Kind == '+') { _plusCount++; if (!inHunk) { _hunkCount++; inHunk = true; } }
                else if (l.Kind == '-') { _minusCount++; if (!inHunk) { _hunkCount++; inHunk = true; } }
                else if (l.Kind == '…') { inHunk = false; }
            }
        }
        DiffText = BuildUnifiedText(_changesLines, _plusCount, _minusCount);

        _feedbackTimer.Tick += (_, _) => { _feedbackTimer.Stop(); _feedback = ""; Invalidate(); };
        Shown += (_, _) => { Activate(); BringToFront(); };
        ApplyRoundedRegion();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _feedbackTimer.Stop();
            _feedbackTimer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
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
        var prev = Region;
        if (WindowState == FormWindowState.Maximized)
        {
            Region = null;
        }
        else
        {
            using var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), 16f);
            Region = new Region(path);
        }
        prev?.Dispose();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _layoutKey = (false, false, -1);
        ApplyRoundedRegion();
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            var pt = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            const int grip = 6;
            const int corner = 12;
            bool left = pt.X <= grip, right = pt.X >= Width - grip;
            bool top = pt.Y <= grip, bottom = pt.Y >= Height - grip;

            // Keep caption close/maximize buttons clickable near top-right corner.
            if (pt.Y <= 46 && pt.X >= Width - 100)
            {
                base.WndProc(ref m);
                return;
            }
            if (pt.X <= corner && pt.Y <= corner) { m.Result = (IntPtr)HTTOPLEFT; return; }
            if (pt.X >= Width - corner && pt.Y >= Height - corner) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
            if (pt.X <= corner && pt.Y >= Height - corner) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
            if (pt.X >= Width - corner && pt.Y <= corner) { m.Result = (IntPtr)HTTOPRIGHT; return; }
            if (left) { m.Result = (IntPtr)HTLEFT; return; }
            if (right) { m.Result = (IntPtr)HTRIGHT; return; }
            if (top) { m.Result = (IntPtr)HTTOP; return; }
            if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }
        }
        base.WndProc(ref m);
    }

    private static string BuildUnifiedText(List<DiffLine>? lines, int plus, int minus)
    {
        if (lines == null) return I18n.T("The files are too large to compare here.", "Файлы слишком большие для сравнения здесь.");
        if (plus + minus == 0) return I18n.T("No differences: the file on disk matches the kept copy.", "Различий нет: файл на диске совпадает с сохранённой копией.");
        var sb = new StringBuilder();
        foreach (var l in lines.Take(MaxRenderedLines))
        {
            if (l.Kind == '…') sb.AppendLine("  ⋯");
            else sb.Append(l.Kind).Append(' ').AppendLine(l.Text);
        }
        return sb.ToString();
    }

    private void ShowFeedback(string text, bool ok = true)
    {
        _feedback = text;
        _feedbackOk = ok;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
        Invalidate();
    }

    private void CopyDiff()
    {
        try
        {
            string payload = DiffText;
            if (_selectedSourceIndex >= 0 && CurrentLines is { } list && _selectedSourceIndex < list.Count && list[_selectedSourceIndex].Kind != '…')
            {
                payload = list[_selectedSourceIndex].Text;
                Clipboard.SetText(payload);
                ShowFeedback(I18n.T("Selected line copied", "Строка скопирована"));
                return;
            }
            Clipboard.SetText(payload);
            ShowFeedback(I18n.T("Diff copied to clipboard", "Сравнение скопировано в буфер"));
        }
        catch { ShowFeedback(I18n.T("Could not copy to clipboard", "Не удалось скопировать"), ok: false); }
    }

    private void CopyPath()
    {
        if (string.IsNullOrWhiteSpace(_fullPath)) return;
        try
        {
            Clipboard.SetText(_fullPath);
            ShowFeedback(I18n.T("Path copied", "Путь скопирован"));
        }
        catch { ShowFeedback(I18n.T("Could not copy path", "Не удалось скопировать путь"), ok: false); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        if (keyData == (Keys.Control | Keys.C)) { CopyDiff(); return true; }
        if (keyData == Keys.Down) { ScrollVerticalBy(RowH * 3f); return true; }
        if (keyData == Keys.Up) { ScrollVerticalBy(-RowH * 3f); return true; }
        if (keyData == Keys.PageDown) { ScrollVerticalBy(Math.Max(RowH * 4f, _bodyRect.Height - 40f)); return true; }
        if (keyData == Keys.PageUp) { ScrollVerticalBy(-Math.Max(RowH * 4f, _bodyRect.Height - 40f)); return true; }
        if (keyData == Keys.Home) { _scrollY = 0; Invalidate(); return true; }
        if (keyData == Keys.End) { _scrollY = _maxScrollY; Invalidate(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ScrollVerticalBy(float delta)
    {
        if (_maxScrollY <= 0) return;
        _scrollY = Math.Clamp(_scrollY + delta, 0, _maxScrollY);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Shift) != 0 && !_wordWrap && _maxScrollX > 0)
        {
            _scrollX = Math.Clamp(_scrollX - e.Delta * 0.6f, 0, _maxScrollX);
            Invalidate();
            return;
        }
        if (_maxScrollY > 0)
        {
            _scrollY = Math.Clamp(_scrollY - e.Delta * 0.55f, 0, _maxScrollY);
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mousePos = e.Location;

        if (_draggingVScroll && _maxScrollY > 0 && _vTrackRect.Height > _vThumbRect.Height)
        {
            float rel = (e.Y - _vTrackRect.Top - _dragGrabOffset) / (_vTrackRect.Height - _vThumbRect.Height);
            _scrollY = Math.Clamp(rel, 0f, 1f) * _maxScrollY;
            Invalidate();
            return;
        }
        if (_draggingHScroll && _maxScrollX > 0 && _hTrackRect.Width > _hThumbRect.Width)
        {
            float rel = (e.X - _hTrackRect.Left - _dragGrabOffset) / (_hTrackRect.Width - _hThumbRect.Width);
            _scrollX = Math.Clamp(rel, 0f, 1f) * _maxScrollX;
            Invalidate();
            return;
        }

        var hit = _hits.FirstOrDefault(h => h.Rect.Contains(e.Location));
        string newId = hit?.Id ?? "";
        bool overScroll = (_maxScrollY > 0 && _vTrackRect.Contains(e.Location)) || (_maxScrollX > 0 && _hTrackRect.Contains(e.Location));
        Cursor = hit != null || overScroll ? Cursors.Hand : Cursors.Default;
        if (newId != _hoveredId)
        {
            _hoveredId = newId;
            Invalidate();
        }
        string? tip = hit?.Tooltip;
        if (tip != _lastTip)
        {
            _lastTip = tip;
            _toolTip.SetToolTip(this, tip);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mousePos = new Point(-1, -1);
        Cursor = Cursors.Default;
        if (_lastTip != null) { _lastTip = null; _toolTip.SetToolTip(this, null); }
        if (_hoveredId.Length > 0) { _hoveredId = ""; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _draggingVScroll = false;
        _draggingHScroll = false;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        // Scrollbar thumb drag / track jump.
        if (_maxScrollY > 0 && _vTrackRect.Contains(e.Location))
        {
            if (_vThumbRect.Contains(e.Location))
            {
                _draggingVScroll = true;
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
        if (_maxScrollX > 0 && _hTrackRect.Contains(e.Location))
        {
            if (_hThumbRect.Contains(e.Location))
            {
                _draggingHScroll = true;
                _dragGrabOffset = e.X - _hThumbRect.Left;
            }
            else if (_hTrackRect.Width > _hThumbRect.Width)
            {
                float rel = (e.X - _hTrackRect.Left - _hThumbRect.Width / 2f) / (_hTrackRect.Width - _hThumbRect.Width);
                _scrollX = Math.Clamp(rel, 0f, 1f) * _maxScrollX;
                Invalidate();
            }
            return;
        }

        foreach (var hit in _hits.ToList())
        {
            if (hit.Rect.Contains(e.Location))
            {
                hit.OnClick();
                return;
            }
        }

        // Row click inside diff body: select row (or expand gap to full file).
        if (_bodyRect.Contains(e.Location) && _visualRows.Count > 0)
        {
            float contentY = (e.Y - _bodyRect.Top) + _scrollY;
            var vr = _visualRows.FirstOrDefault(r => contentY >= r.Y && contentY < r.Y + r.Height);
            if (vr != null)
            {
                if (vr.Source.Kind == '…')
                {
                    FullFileMode = true;
                    return;
                }
                _selectedSourceIndex = _selectedSourceIndex == vr.SourceIndex ? -1 : vr.SourceIndex;
                if (e.Clicks == 2 && _selectedSourceIndex >= 0) CopyDiff();
                else Invalidate();
                return;
            }
        }

        if (e.Y <= HeaderH)
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

    internal void RenderPreviewToFile(string filePath)
    {
        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using var g = Graphics.FromImage(bmp);
        PaintDialog(g);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintDialog(e.Graphics);
    }

    private void EnsureLayout(Graphics g, float codeViewportW)
    {
        int wKey = (int)Math.Round(codeViewportW);
        if (_layoutKey == (_fullMode, _wordWrap, wKey)) return;
        _layoutKey = (_fullMode, _wordWrap, wKey);
        _visualRows.Clear();
        _totalContentH = 0;
        _maxLineWidth = 0;

        var list = CurrentLines;
        if (list == null || (_plusCount + _minusCount == 0 && !_fullMode)) return;

        int maxCharsPerLine = 0;
        if (_wordWrap)
        {
            float charW = Math.Max(6.5f, g.MeasureString("MMMMMMMMMM", MonoFont, 1000, StringFormat.GenericTypographic).Width / 10f);
            maxCharsPerLine = Math.Max(20, (int)Math.Floor((codeViewportW - 36f) / charW));
        }

        float y = 0;
        int limit = Math.Min(list.Count, MaxRenderedLines);
        for (int i = 0; i < limit; i++)
        {
            var line = list[i];
            if (line.Kind == '…')
            {
                _visualRows.Add(new VisualRow(line, "", false, y, GapRowH, i));
                y += GapRowH;
                continue;
            }

            string expanded = line.Text.Replace("\t", "    ");
            if (!_wordWrap || expanded.Length <= maxCharsPerLine)
            {
                _visualRows.Add(new VisualRow(line, expanded, false, y, RowH, i));
                y += RowH;
                if (!_wordWrap && expanded.Length > 0)
                {
                    float lw = g.MeasureString(expanded, MonoFont, 10000, StringFormat.GenericTypographic).Width;
                    if (lw > _maxLineWidth) _maxLineWidth = lw;
                }
            }
            else
            {
                int pos = 0;
                bool cont = false;
                while (pos < expanded.Length)
                {
                    int take = Math.Min(maxCharsPerLine, expanded.Length - pos);
                    _visualRows.Add(new VisualRow(line, expanded.Substring(pos, take), cont, y, RowH, i));
                    y += RowH;
                    pos += take;
                    cont = true;
                }
            }
        }
        _totalContentH = y + 8f;
    }

    private void PaintDialog(Graphics g)
    {
        _hits.Clear();
        Theme.PrepareGraphics(g);
        bool maximized = WindowState == FormWindowState.Maximized;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var winPath = Theme.RoundRect(bounds, maximized ? 0f : 16f);
        var saved = g.Save();
        g.SetClip(winPath);

        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), Theme.BgGradTop, Theme.BgGradBottom, 90f))
            g.FillRectangle(bg, 0, 0, Width, Height);

        DrawHeader(g);
        float cardTop = DrawToolbar(g);
        DrawDiffCard(g, cardTop);
        DrawFooter(g);

        using (var border = new Pen(Theme.BorderStrong))
            g.DrawPath(border, winPath);
        g.Restore(saved);
    }

    private void DrawHeader(Graphics g)
    {
        Theme.DrawLogo(g, new RectangleF(18f, 13f, 28f, 28f));

        // Window buttons on the right: maximize/restore + close.
        var maxRect = new RectangleF(Width - 92f, 6f, 40f, 40f);
        var closeRect = new RectangleF(Width - 48f, 6f, 40f, 40f);
        DrawCaptionButton(g, maxRect, isClose: false);
        DrawCaptionButton(g, closeRect, isClose: true);
        _hits.Add(new HitZone(maxRect, "cmp_max", () => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized,
            WindowState == FormWindowState.Maximized ? I18n.T("Restore", "Восстановить") : I18n.T("Maximize", "Развернуть")));
        _hits.Add(new HitZone(closeRect, "cmp_close_x", () => { DialogResult = DialogResult.Cancel; Close(); }, I18n.T("Close", "Закрыть")));

        float x = 56f;
        float rightLimit = maxRect.Left - 14f;
        var titleFont = UiFont(14.5f, true);
        float titleMaxW = Math.Max(120f, rightLimit - x - 165f);
        float titleW = Math.Min(Theme.MeasureText(g, _name, titleFont).Width + 4f, titleMaxW);
        Theme.DrawText(g, _name, titleFont, Theme.TextPrimary, new RectangleF(x, 12f, titleW, 30f));
        x += titleW + 10f;

        // Diff stats pills: +N / -M
        if (_changesLines != null)
        {
            if (_plusCount > 0 || _minusCount > 0)
            {
                if (_plusCount > 0) x = DrawStatChip(g, x, rightLimit, "+" + _plusCount, Theme.Success) + 6f;
                if (_minusCount > 0) x = DrawStatChip(g, x, rightLimit, "−" + _minusCount, Theme.Danger) + 6f;
            }
            else
            {
                DrawStatChip(g, x, rightLimit, I18n.T("Identical", "Без изменений"), Theme.Info);
            }
        }

        Theme.DrawDivider(g, 0, Width, HeaderH);
    }

    private float DrawStatChip(Graphics g, float x, float rightLimit, string label, Color tint)
    {
        var font = UiFont(11.5f, true);
        float w = Theme.MeasureText(g, label, font).Width + 16f;
        if (x + w > rightLimit) return x;
        var r = new RectangleF(x, 15.5f, w, 23f);
        Theme.FillRound(g, r, 11.5f, Theme.WithAlpha(tint, 28));
        Theme.StrokeRound(g, r, 11.5f, Theme.WithAlpha(tint, 82));
        Theme.DrawText(g, label, font, Theme.TextVariant(tint), r, StringAlignment.Center, StringAlignment.Center);
        return r.Right;
    }

    private void DrawCaptionButton(Graphics g, RectangleF r, bool isClose)
    {
        bool hov = r.Contains(_mousePos);
        if (hov)
            Theme.FillRound(g, r, 8f, isClose ? Theme.Danger : Theme.HoverOverlay);
        Color fg = hov ? (isClose ? Color.White : Theme.TextPrimary) : Theme.TextSecondary;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        if (isClose)
        {
            Theme.DrawIcon(g, IconKind.Close, new RectangleF(cx - 6f, cy - 6f, 12f, 12f), fg, 1.9f);
        }
        else
        {
            using var pen = new Pen(fg, 1.3f);
            if (WindowState == FormWindowState.Maximized)
            {
                g.DrawRectangle(pen, cx - 5f, cy - 3f, 8f, 8f);
                g.DrawRectangle(pen, cx - 1f, cy - 7f, 8f, 8f);
            }
            else
            {
                g.DrawRectangle(pen, cx - 5f, cy - 5f, 10f, 10f);
            }
        }
    }

    private float DrawToolbar(Graphics g)
    {
        float padX = 20f;
        float y = HeaderH + 14f;
        float right = Width - padX;

        // Right-aligned controls: Copy diff, Reveal folder, Segmented mode switch.
        right = DrawActionPillRight(g, right, y + 4f, I18n.T("Copy diff", "Копировать"), IconKind.Copy, "cmp_copy", CopyDiff,
            I18n.T("Copy diff (or selected line) to clipboard", "Скопировать сравнение (или выделенную строку)")) - 8f;

        if (_onReveal != null || !string.IsNullOrWhiteSpace(_fullPath))
        {
            var rev = new RectangleF(right - 32f, y + 4f, 32f, 32f);
            bool revHov = rev.Contains(_mousePos);
            Theme.FillRound(g, rev, Theme.RadiusControl, revHov ? Theme.SurfaceHover : Theme.SurfaceRaised);
            Theme.StrokeRound(g, rev, Theme.RadiusControl, Theme.Border);
            Theme.DrawIcon(g, IconKind.Folder, new RectangleF(rev.X + 8f, rev.Y + 8f, 16f, 16f), revHov ? Theme.TextPrimary : Theme.TextSecondary, 1.7f);
            _hits.Add(new HitZone(rev, "cmp_reveal", () =>
            {
                if (_onReveal != null) _onReveal();
                else if (!DesktopLauncher.RevealInFolder(_fullPath))
                    ShowFeedback(I18n.T("Folder no longer exists", "Папка больше не существует"), ok: false);
            }, I18n.T("Show in folder", "Показать в папке")));
            right = rev.Left - 10f;
        }

        // Segmented bar: Changes only | Full file
        string lblChanges = I18n.T("Changes only", "Только правки");
        string lblFull = I18n.T("Full file", "Весь файл");
        var segFont = UiFont(12f, true);
        float segW = Math.Max(Theme.MeasureText(g, lblChanges, segFont).Width, Theme.MeasureText(g, lblFull, segFont).Width) + 28f;
        float barW = segW * 2f + 8f;
        var bar = new RectangleF(right - barW, y + 4f, barW, 32f);
        Theme.FillRound(g, bar, 9f, Theme.Bg0);
        Theme.StrokeRound(g, bar, 9f, Theme.Border);

        var leftSeg = new RectangleF(bar.X + 3f, bar.Y + 3f, segW, bar.Height - 6f);
        var rightSeg = new RectangleF(leftSeg.Right + 2f, bar.Y + 3f, segW, bar.Height - 6f);
        DrawSegmentItem(g, leftSeg, lblChanges, !_fullMode, "cmp_mode_changes", () => FullFileMode = false);
        DrawSegmentItem(g, rightSeg, lblFull, _fullMode, "cmp_mode_full", () => FullFileMode = true);
        right = bar.Left - 16f;

        // Left metadata: full path (clickable to copy) + before → now + agent/note.
        float textW = Math.Max(140f, right - padX);
        string compareMeta = I18n.T("Before: copy from ", "Было: копия от ") + (_keptAt.Length > 0 ? _keptAt : "--:--")
            + (_fileExists
                ? I18n.T("  →  Now: file on disk", "  →  Сейчас: файл на диске")
                : I18n.T("  →  Now: file removed", "  →  Сейчас: файла нет на диске"));
        if (_agent.Length > 0) compareMeta += "  ·  " + _agent;

        if (_fullPath.Length > 0)
        {
            var pathRect = new RectangleF(padX, y, textW, 20f);
            bool pathHov = pathRect.Contains(_mousePos);
            Theme.DrawText(g, _fullPath, Theme.FontMono(8.8f), pathHov ? Theme.AccentText : Theme.TextSecondary, pathRect);
            _hits.Add(new HitZone(pathRect, "cmp_copy_path", CopyPath, I18n.T("Click to copy file path", "Нажмите, чтобы скопировать путь")));
            Theme.DrawText(g, compareMeta, UiFont(12f), _fileExists ? Theme.TextTertiary : Theme.WarningText, new RectangleF(padX, y + 21f, textW, 20f));
        }
        else
        {
            Theme.DrawText(g, compareMeta, UiFont(12.5f), _fileExists ? Theme.TextSecondary : Theme.WarningText, new RectangleF(padX, y + 10f, textW, 22f));
        }

        return y + 52f;
    }

    private void DrawSegmentItem(Graphics g, RectangleF r, string text, bool active, string id, Action onClick)
    {
        bool hov = r.Contains(_mousePos);
        if (active)
            Theme.FillRound(g, r, 6.5f, Theme.Accent);
        else if (hov)
            Theme.FillRound(g, r, 6.5f, Theme.HoverOverlay);
        Theme.DrawText(g, text, UiFont(11.8f, active), active ? Color.White : (hov ? Theme.TextPrimary : Theme.TextSecondary), r, StringAlignment.Center, StringAlignment.Center);
        _hits.Add(new HitZone(r, id, onClick));
    }

    private float DrawActionPillRight(Graphics g, float right, float y, string text, IconKind icon, string id, Action onClick, string? tooltip = null)
    {
        var font = UiFont(12f, true);
        float textW = Theme.MeasureText(g, text, font).Width;
        float w = textW + 42f;
        var r = new RectangleF(right - w, y, w, 32f);
        bool hov = r.Contains(_mousePos);
        Theme.FillRound(g, r, Theme.RadiusControl, hov ? Theme.SurfaceHover : Theme.SurfaceRaised);
        Theme.StrokeRound(g, r, Theme.RadiusControl, Theme.Border);
        Theme.DrawIcon(g, icon, new RectangleF(r.X + 11f, r.Y + 8.5f, 15f, 15f), hov ? Theme.TextPrimary : Theme.TextSecondary, 1.7f);
        Theme.DrawText(g, text, font, hov ? Theme.TextPrimary : Theme.TextSecondary, new RectangleF(r.X + 31f, r.Y, r.Width - 38f, r.Height), StringAlignment.Near, StringAlignment.Center);
        _hits.Add(new HitZone(r, id, onClick, tooltip));
        return r.Left;
    }

    private void DrawDiffCard(Graphics g, float top)
    {
        float padX = 20f;
        float cardH = Math.Max(140f, Height - FooterH - top - 14f);
        var card = new RectangleF(padX, top, Width - padX * 2f, cardH);

        using var cardPath = Theme.RoundRect(card, 12f);
        using (var bg = new SolidBrush(Theme.Bg0)) g.FillPath(bg, cardPath);

        var st = g.Save();
        g.SetClip(cardPath, CombineMode.Intersect);

        // Table header bar inside card
        const float barH = 32f;
        var barRect = new RectangleF(card.X, card.Y, card.Width, barH);
        using (var barBrush = new SolidBrush(Color.FromArgb(18, 22, 31)))
            g.FillRectangle(barBrush, barRect);

        var colFont = UiFont(11f, true);
        Theme.DrawText(g, I18n.T("Old", "До"), colFont, Theme.TextTertiary, new RectangleF(card.X + 6f, card.Y, 44f, barH), StringAlignment.Far, StringAlignment.Center);
        Theme.DrawText(g, I18n.T("New", "После"), colFont, Theme.TextTertiary, new RectangleF(card.X + 52f, card.Y, 44f, barH), StringAlignment.Far, StringAlignment.Center);
        using (var divPen = new Pen(Theme.Border))
            g.DrawLine(divPen, card.X + GutterW, card.Y, card.X + GutterW, card.Bottom);

        // Right side of table header: Wrap toggle pill
        string wrapLabel = I18n.T("Wrap lines", "Перенос строк");
        float wrapW = Theme.MeasureText(g, wrapLabel, UiFont(11f, true)).Width + 20f;
        var wrapRect = new RectangleF(card.Right - wrapW - 10f, card.Y + 4.5f, wrapW, 23f);
        bool wrapHov = wrapRect.Contains(_mousePos);
        if (_wordWrap)
        {
            Theme.FillRound(g, wrapRect, 6f, Theme.WithAlpha(Theme.Accent, 46));
            Theme.StrokeRound(g, wrapRect, 6f, Theme.WithAlpha(Theme.Accent, 120));
        }
        else
        {
            Theme.FillRound(g, wrapRect, 6f, wrapHov ? Theme.SurfaceHover : Theme.SurfaceRaised);
            Theme.StrokeRound(g, wrapRect, 6f, Theme.Border);
        }
        Theme.DrawText(g, wrapLabel, UiFont(11f, _wordWrap), _wordWrap ? Theme.AccentText : (wrapHov ? Theme.TextPrimary : Theme.TextSecondary), wrapRect, StringAlignment.Center, StringAlignment.Center);
        _hits.Add(new HitZone(wrapRect, "cmp_wrap", () => WordWrapMode = !_wordWrap, I18n.T("Toggle long line wrapping", "Переключает перенос длинных строк")));

        string summary = _changesLines == null
            ? I18n.T("Large file", "Большой файл")
            : (_plusCount + _minusCount == 0
                ? I18n.T("No changes detected", "Изменений не найдено")
                : I18n.Format("{0} added · {1} removed", "{0} добавлено · {1} удалено", _plusCount, _minusCount)
                    + (_hunkCount > 0 ? "  ·  " + FormatHunks(_hunkCount) : ""));
        if (_note.Length > 0) summary += "  ·  «" + _note + "»";
        Theme.DrawText(g, summary, UiFont(11.5f), Theme.TextSecondary, new RectangleF(card.X + GutterW + 14f, card.Y, Math.Max(40f, wrapRect.Left - (card.X + GutterW + 22f)), barH), StringAlignment.Near, StringAlignment.Center);

        Theme.DrawDivider(g, card.X, card.Right, card.Y + barH);

        _bodyRect = new RectangleF(card.X, card.Y + barH, card.Width, card.Height - barH);
        float codeViewportW = Math.Max(80f, _bodyRect.Width - GutterW - 14f);
        EnsureLayout(g, codeViewportW);

        // Special empty / oversized states
        if (_changesLines == null)
        {
            DrawEmptyState(g, _bodyRect, IconKind.Warning, Theme.Warning,
                I18n.T("Files are too large to compare here", "Файлы слишком большие для сравнения здесь"),
                I18n.T("Open the file in your editor to inspect full differences.", "Откройте файл в редакторе, чтобы посмотреть полные различия."));
            _maxScrollY = 0; _maxScrollX = 0;
        }
        else if (_plusCount + _minusCount == 0 && !_fullMode)
        {
            DrawEmptyState(g, _bodyRect, IconKind.Check, Theme.Success,
                I18n.T("No differences found", "Различий нет"),
                I18n.T("The file on disk matches the kept copy. Switch to “Full file” to browse its content.", "Файл на диске полностью совпадает с сохранённой копией. Переключитесь на «Весь файл», чтобы посмотреть содержимое."));
            _maxScrollY = 0; _maxScrollX = 0;
        }
        else
        {
            bool needHScroll = !_wordWrap && (_maxLineWidth + 44f > codeViewportW);
            float viewH = _bodyRect.Height - (needHScroll ? 12f : 0f);
            _maxScrollY = Math.Max(0f, _totalContentH - viewH);
            _scrollY = Math.Clamp(_scrollY, 0f, _maxScrollY);
            _maxScrollX = needHScroll ? Math.Max(0f, (_maxLineWidth + 44f) - codeViewportW) : 0f;
            _scrollX = Math.Clamp(_scrollX, 0f, _maxScrollX);

            var rowsClip = new RectangleF(_bodyRect.X, _bodyRect.Y, _bodyRect.Width, viewH);
            var bodySaved = g.Save();
            g.SetClip(rowsClip, CombineMode.Intersect);

            // Gutter background fill
            using (var gutterBase = new SolidBrush(Color.FromArgb(14, 17, 23)))
                g.FillRectangle(gutterBase, _bodyRect.X, _bodyRect.Y, GutterW, viewH);

            Color addBg = Color.FromArgb(15, 36, 28);
            Color addGutter = Color.FromArgb(18, 49, 36);
            Color delBg = Color.FromArgb(41, 18, 23);
            Color delGutter = Color.FromArgb(56, 22, 29);
            Color gapBg = Color.FromArgb(20, 26, 37);

            using var addBrush = new SolidBrush(addBg);
            using var addGutterBrush = new SolidBrush(addGutter);
            using var delBrush = new SolidBrush(delBg);
            using var delGutterBrush = new SolidBrush(delGutter);
            using var gapBrush = new SolidBrush(gapBg);
            using var addStripe = new SolidBrush(Theme.Success);
            using var delStripe = new SolidBrush(Theme.Danger);
            using var selBrush = new SolidBrush(Theme.WithAlpha(Theme.Accent, 28));
            using var selPen = new Pen(Theme.WithAlpha(Theme.Accent, 100));
            using var codeFmt = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
                Trimming = StringTrimming.None,
                LineAlignment = StringAlignment.Center,
            };

            float codeLeft = _bodyRect.X + GutterW;
            var codeClip = new RectangleF(codeLeft + 1f, _bodyRect.Y, _bodyRect.Width - GutterW - (_maxScrollY > 0 ? 12f : 2f), viewH);

            foreach (var vr in _visualRows)
            {
                float ry = _bodyRect.Y + vr.Y - _scrollY;
                if (ry + vr.Height < _bodyRect.Y) continue;
                if (ry > _bodyRect.Y + viewH) break;

                var rowRect = new RectangleF(_bodyRect.X, ry, _bodyRect.Width, vr.Height);
                var gutterRect = new RectangleF(_bodyRect.X, ry, GutterW, vr.Height);

                if (vr.Source.Kind == '…')
                {
                    g.FillRectangle(gapBrush, rowRect);
                    Theme.DrawDivider(g, _bodyRect.X, _bodyRect.Right, ry);
                    Theme.DrawDivider(g, _bodyRect.X, _bodyRect.Right, ry + vr.Height - 0.5f);
                    Theme.DrawText(g, "⋯", UiFont(13f, true), Theme.AccentText, gutterRect, StringAlignment.Center, StringAlignment.Center);
                    string gapMsg = vr.Source.SkippedCount > 0
                        ? FormatHiddenLines(vr.Source.SkippedCount)
                        : I18n.T("Unchanged lines hidden — click to show full file", "Скрытые строки без изменений — нажмите, чтобы показать весь файл");
                    bool gapHov = rowRect.Contains(_mousePos);
                    Theme.DrawText(g, gapMsg, UiFont(11.5f), gapHov ? Theme.AccentText : Theme.TextTertiary,
                        new RectangleF(codeLeft + 14f, ry, codeClip.Width - 18f, vr.Height), StringAlignment.Near, StringAlignment.Center);
                    continue;
                }

                if (vr.Source.Kind == '+')
                {
                    g.FillRectangle(addBrush, rowRect);
                    g.FillRectangle(addGutterBrush, gutterRect);
                    g.FillRectangle(addStripe, _bodyRect.X, ry, 3f, vr.Height);
                }
                else if (vr.Source.Kind == '-')
                {
                    g.FillRectangle(delBrush, rowRect);
                    g.FillRectangle(delGutterBrush, gutterRect);
                    g.FillRectangle(delStripe, _bodyRect.X, ry, 3f, vr.Height);
                }

                if (vr.SourceIndex == _selectedSourceIndex)
                {
                    g.FillRectangle(selBrush, rowRect);
                    g.DrawRectangle(selPen, rowRect.X + 0.5f, rowRect.Y + 0.5f, rowRect.Width - 1f, rowRect.Height - 1f);
                }

                // Line numbers (only on the first visual line of a wrapped row)
                if (!vr.IsContinuation)
                {
                    Color numCol = vr.Source.Kind == '+' ? Theme.WithAlpha(Theme.SuccessText, 185)
                        : vr.Source.Kind == '-' ? Theme.WithAlpha(Theme.DangerText, 185)
                        : Theme.TextTertiary;
                    if (vr.Source.OldLine is int oldNo)
                        Theme.DrawText(g, oldNo.ToString(CultureInfo.InvariantCulture), MonoNumFont, numCol, new RectangleF(_bodyRect.X + 6f, ry, 44f, vr.Height), StringAlignment.Far, StringAlignment.Center);
                    if (vr.Source.NewLine is int newNo)
                        Theme.DrawText(g, newNo.ToString(CultureInfo.InvariantCulture), MonoNumFont, numCol, new RectangleF(_bodyRect.X + 52f, ry, 44f, vr.Height), StringAlignment.Far, StringAlignment.Center);
                }

                // Diff prefix (+ / −) and code line clipped to code area
                var cSave = g.Save();
                g.SetClip(codeClip, CombineMode.Intersect);

                if (!vr.IsContinuation)
                {
                    string mark = vr.Source.Kind == '+' ? "+" : vr.Source.Kind == '-' ? "−" : " ";
                    Color markCol = vr.Source.Kind == '+' ? Theme.SuccessText : vr.Source.Kind == '-' ? Theme.DangerText : Theme.TextTertiary;
                    using var markBrush = new SolidBrush(markCol);
                    g.DrawString(mark, Theme.FontMono(9.5f, true), markBrush, new RectangleF(codeLeft + 10f - _scrollX, ry, 16f, vr.Height), codeFmt);
                }

                Color codeCol = vr.Source.Kind == '+' ? Color.FromArgb(228, 249, 238)
                    : vr.Source.Kind == '-' ? Color.FromArgb(253, 226, 229)
                    : Theme.TextSecondary;
                using var codeBrush = new SolidBrush(codeCol);
                g.DrawString(vr.Segment, MonoFont, codeBrush, new RectangleF(codeLeft + 26f - _scrollX, ry, Math.Max(codeClip.Width + _scrollX + 64f, _maxLineWidth + 64f), vr.Height), codeFmt);
                g.Restore(cSave);
            }

            // Gutter separator line over rows
            using (var divPen = new Pen(Theme.Border))
                g.DrawLine(divPen, codeLeft, _bodyRect.Y, codeLeft, _bodyRect.Y + viewH);

            g.Restore(bodySaved);

            // Vertical scrollbar
            if (_maxScrollY > 0)
            {
                _vTrackRect = new RectangleF(_bodyRect.Right - 10f, _bodyRect.Y + 4f, 6f, viewH - 8f);
                float thumbH = Math.Max(32f, _vTrackRect.Height * (viewH / Math.Max(viewH, _totalContentH)));
                float thumbY = _vTrackRect.Top + (_vTrackRect.Height - thumbH) * (_scrollY / _maxScrollY);
                _vThumbRect = new RectangleF(_vTrackRect.X, thumbY, _vTrackRect.Width, thumbH);
                bool vActive = _draggingVScroll || _vTrackRect.Contains(_mousePos);
                Theme.FillRound(g, _vThumbRect, 3f, Color.FromArgb(vActive ? 145 : 85, 220, 230, 250));
            }
            else { _vTrackRect = RectangleF.Empty; _vThumbRect = RectangleF.Empty; }

            // Horizontal scrollbar
            if (_maxScrollX > 0)
            {
                float hY = _bodyRect.Bottom - 9f;
                _hTrackRect = new RectangleF(_bodyRect.X + GutterW + 8f, hY, _bodyRect.Width - GutterW - 22f, 5f);
                float thumbW = Math.Max(36f, _hTrackRect.Width * (codeViewportW / (_maxLineWidth + 44f)));
                float thumbX = _hTrackRect.Left + (_hTrackRect.Width - thumbW) * (_scrollX / _maxScrollX);
                _hThumbRect = new RectangleF(thumbX, hY, thumbW, 5f);
                bool hActive = _draggingHScroll || _hTrackRect.Contains(_mousePos);
                Theme.FillRound(g, _hThumbRect, 2.5f, Color.FromArgb(hActive ? 145 : 85, 220, 230, 250));
            }
            else { _hTrackRect = RectangleF.Empty; _hThumbRect = RectangleF.Empty; }
        }

        g.Restore(st);
        using (var cardBorder = new Pen(Theme.Border))
            g.DrawPath(cardBorder, cardPath);
    }

    private static void DrawEmptyState(Graphics g, RectangleF area, IconKind icon, Color tint, string title, string subtitle)
    {
        float cx = area.X + area.Width / 2f;
        float cy = area.Y + area.Height / 2f - 10f;
        var tile = new RectangleF(cx - 24f, cy - 42f, 48f, 48f);
        Theme.FillRound(g, tile, 14f, Theme.WithAlpha(tint, 28));
        Theme.StrokeRound(g, tile, 14f, Theme.WithAlpha(tint, 78));
        Theme.DrawIcon(g, icon, new RectangleF(tile.X + 13f, tile.Y + 13f, 22f, 22f), Theme.TextVariant(tint), 1.9f);
        Theme.DrawText(g, title, UiFont(14.5f, true), Theme.TextPrimary, new RectangleF(area.X + 32f, cy + 16f, area.Width - 64f, 26f), StringAlignment.Center, StringAlignment.Center);
        Theme.DrawText(g, subtitle, UiFont(12.5f), Theme.TextSecondary, new RectangleF(area.X + 48f, cy + 44f, area.Width - 96f, 42f), StringAlignment.Center, StringAlignment.Near, ellipsis: false, wrap: true);
    }

    private void DrawFooter(Graphics g)
    {
        float y = Height - FooterH;
        Theme.DrawDivider(g, 0, Width, y);

        float right = Width - 20f;
        float btnY = y + 15f;

        // Primary Close button
        string closeLbl = I18n.T("Close", "Закрыть");
        float closeW = Math.Max(116f, Theme.MeasureText(g, closeLbl, UiFont(12.5f, true)).Width + 40f);
        var closeRect = new RectangleF(right - closeW, btnY, closeW, 34f);
        bool closeHov = closeRect.Contains(_mousePos);
        using (var p = Theme.RoundRect(closeRect, 9f))
        using (var br = new LinearGradientBrush(closeRect, closeHov ? Theme.AccentHover : Theme.AccentGradTop, closeHov ? Theme.Accent : Theme.AccentGradBottom, 90f))
            g.FillPath(br, p);
        Theme.DrawText(g, closeLbl, UiFont(12.5f, true), Color.White, closeRect, StringAlignment.Center, StringAlignment.Center);
        _hits.Add(new HitZone(closeRect, "cmp_close", () => { DialogResult = DialogResult.Cancel; Close(); }));
        right = closeRect.Left - 10f;

        // Optional Undo button
        if (_canUndo)
        {
            string undoLbl = I18n.T("Undo change", "Отменить правку");
            float undoW = Theme.MeasureText(g, undoLbl, UiFont(12.5f, true)).Width + 46f;
            var undoRect = new RectangleF(right - undoW, btnY, undoW, 34f);
            bool undoHov = undoRect.Contains(_mousePos);
            Theme.FillRound(g, undoRect, 9f, undoHov ? Theme.SurfaceHover : Theme.SurfaceRaised);
            Theme.StrokeRound(g, undoRect, 9f, Theme.Border);
            Theme.DrawIcon(g, IconKind.History, new RectangleF(undoRect.X + 12f, undoRect.Y + 9.5f, 15f, 15f), undoHov ? Theme.TextPrimary : Theme.TextSecondary, 1.7f);
            Theme.DrawText(g, undoLbl, UiFont(12.5f, true), undoHov ? Theme.TextPrimary : Theme.TextSecondary, new RectangleF(undoRect.X + 32f, undoRect.Y, undoRect.Width - 40f, undoRect.Height), StringAlignment.Near, StringAlignment.Center);
            _hits.Add(new HitZone(undoRect, "cmp_undo", () => { DialogResult = DialogResult.Retry; Close(); },
                I18n.T("Restore the previous version (asks for confirmation)", "Вернуть прежнюю версию (с подтверждением)")));
            right = undoRect.Left - 14f;
        }

        // Left side: feedback chip or compact legend
        float lx = 20f;
        if (_feedback.Length > 0)
        {
            Color tint = _feedbackOk ? Theme.Success : Theme.Danger;
            var fFont = UiFont(12f, true);
            float fw = Math.Min(right - lx - 8f, Theme.MeasureText(g, _feedback, fFont).Width + 40f);
            var fr = new RectangleF(lx, y + 17f, fw, 30f);
            Theme.FillRound(g, fr, 8f, Theme.WithAlpha(tint, 28));
            Theme.StrokeRound(g, fr, 8f, Theme.WithAlpha(tint, 80));
            Theme.DrawIcon(g, _feedbackOk ? IconKind.Check : IconKind.Warning, new RectangleF(fr.X + 10f, fr.Y + 7.5f, 15f, 15f), Theme.TextVariant(tint), 1.8f);
            Theme.DrawText(g, _feedback, fFont, Theme.TextVariant(tint), new RectangleF(fr.X + 30f, fr.Y, fr.Width - 36f, fr.Height), StringAlignment.Near, StringAlignment.Center);
            return;
        }

        float midY = y + FooterH / 2f;
        lx = DrawLegendDot(g, lx, midY, Theme.Success, I18n.T("Added", "Добавлено")) + 16f;
        lx = DrawLegendDot(g, lx, midY, Theme.Danger, I18n.T("Removed", "Удалено")) + 16f;
        if (lx + 90f < right)
            Theme.DrawText(g, I18n.T("Esc to close · Ctrl+C to copy", "Esc — закрыть · Ctrl+C — копировать"), UiFont(11.5f), Theme.TextTertiary, new RectangleF(lx, y, right - lx, FooterH), StringAlignment.Near, StringAlignment.Center);
    }

    private static string FormatHunks(int count)
    {
        if (!I18n.IsRu) return count == 1 ? "1 change block" : $"{count} change blocks";
        int m100 = count % 100, m10 = count % 10;
        string noun = (m100 is >= 11 and <= 14) ? "блоков" : m10 == 1 ? "блок" : (m10 is >= 2 and <= 4) ? "блока" : "блоков";
        return $"{count} {noun} правок";
    }

    private static string FormatHiddenLines(int count)
    {
        if (!I18n.IsRu) return count == 1 ? "1 unchanged line hidden — click to show full file" : $"{count} unchanged lines hidden — click to show full file";
        return $"Скрыто строк без изменений: {count} — нажмите, чтобы показать весь файл";
    }

    private static float DrawLegendDot(Graphics g, float x, float midY, Color dotCol, string label)
    {
        Theme.DrawStatusDot(g, new PointF(x + 4f, midY), 3.5f, dotCol, halo: false);
        var font = UiFont(11.5f);
        float w = Theme.MeasureText(g, label, font).Width + 4f;
        Theme.DrawText(g, label, font, Theme.TextSecondary, new RectangleF(x + 13f, midY - 10f, w, 20f), StringAlignment.Near, StringAlignment.Center);
        return x + 13f + w;
    }
}
