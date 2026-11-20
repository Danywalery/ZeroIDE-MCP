using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZeroIDE.Mcp;

// v0.5 change history (Settings → Changes). server.mjs appends one record per agent change to
// history/index.jsonl (write_file, edit_file, file_ops) and keeps the previous bytes next to it;
// agents cannot write there. The owner's own undo actions go to history/owner.jsonl, written only
// here. Every row has exactly one inverse operation, so an undo can itself be undone.
internal enum ChangeKind { Created, Modified, Replaced, Deleted, Moved, Copied, Folder, Undo }
internal enum Inverse { None, RestoreVersion, Trash, Untrash, Move, RemoveEmptyDir, MakeDir }
internal enum ChangeFilter { All, Today, Deletions }
internal enum HistoryState { Ready, Missing, Unavailable }

internal sealed record AgentChange(string Id, DateTimeOffset? Stamp, ChangeKind Kind, string Tool, string Path, string To, bool IsDir,
    string Backup, string AfterSha, long BytesBefore, long BytesAfter, int Files, int Edits, string Agent, string Note, string Risk,
    string Target, string Mode)
{
    /// <summary>Where the item is now (the destination of a move or copy).</summary>
    internal string Shown => Kind is ChangeKind.Moved or ChangeKind.Copied && To.Length > 0 ? To : Path;
    internal string Name { get { string n = System.IO.Path.GetFileName(Shown.TrimEnd('\\', '/')); return n.Length > 0 ? n : Shown; } }
    internal string LocalTime => Stamp?.ToLocalTime().ToString("HH:mm:ss") ?? "--:--:--";
    internal bool IsToday => Stamp?.ToLocalTime().Date == DateTime.Today;
    /// <summary>The single operation that reverses this row.</summary>
    internal (Inverse Op, string From, string Dest) Reverse => Kind switch
    {
        ChangeKind.Created => (Inverse.Trash, Path, ""),
        ChangeKind.Modified or ChangeKind.Replaced => Backup.Length > 0 ? (Inverse.RestoreVersion, Backup, Path) : (Inverse.None, "", ""),
        ChangeKind.Deleted => Backup.Length > 0 ? (Inverse.Untrash, Backup, Path) : (Inverse.None, "", ""),
        ChangeKind.Moved => (Inverse.Move, To, Path),
        ChangeKind.Copied => (Inverse.Trash, To, ""),
        ChangeKind.Folder => (Inverse.RemoveEmptyDir, Path, ""),
        ChangeKind.Undo => Mode switch
        {
            "restore-version" => Backup.Length > 0 ? (Inverse.RestoreVersion, Backup, Path) : (Inverse.Trash, Path, ""),
            "trash" => (Inverse.Untrash, Backup, Path),
            "untrash" => (Inverse.Trash, Path, ""),
            "move-back" => (Inverse.Move, Path, To),
            "rmdir" => (Inverse.MakeDir, Path, ""),
            "mkdir" => (Inverse.RemoveEmptyDir, Path, ""),
            _ => (Inverse.None, "", ""),
        },
        _ => (Inverse.None, "", ""),
    };
    /// <summary>True when Compare can show the kept copy against the file on disk.</summary>
    internal bool CanCompare => Reverse.Op == Inverse.RestoreVersion && !IsDir;
    internal bool Matches(ChangeFilter filter, string query, string translated = "") =>
        (filter != ChangeFilter.Today || IsToday) && (filter != ChangeFilter.Deletions || Kind == ChangeKind.Deleted) &&
        ChangeHistory.Match(query, string.Join(' ', Path, To, Agent, Note, Tool, LocalTime, translated));
}
internal sealed record ChangeSlice(IReadOnlyList<AgentChange> Rows, HistoryState State, bool Limited, int Skipped, IReadOnlySet<string> Undone)
{
    internal static readonly ChangeSlice Empty = new(Array.Empty<AgentChange>(), HistoryState.Missing, false, 0, new HashSet<string>());
}
internal sealed record UndoPlan(bool Possible, string Action, string Detail, bool Caution);
internal readonly record struct DiffLine(char Kind, int? OldLine, int? NewLine, string Text, int SkippedCount = 0);

internal static class ChangeHistory
{
    internal const string IndexFile = "index.jsonl", OwnerFile = "owner.jsonl";
    internal const int RowLimit = 400;
    internal const int ByteLimit = 4 * 1024 * 1024;
    static string S(JsonElement r, string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    static long N(JsonElement r, string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) ? v : -1;

    /// <summary>Case-insensitive, every word must occur; ё and е are the same letter.</summary>
    internal static bool Match(string query, string text)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        static string Norm(string s) => s.ToLowerInvariant().Replace('ё', 'е');
        string hay = Norm(text);
        return Norm(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w, StringComparison.Ordinal));
    }

    internal static AgentChange? Parse(string line, bool owner)
    {
        try
        {
            using var doc = JsonDocument.Parse(line); var r = doc.RootElement; if (r.ValueKind != JsonValueKind.Object) return null;
            string id = S(r, "id"), op = S(r, "op"), path = S(r, "path");
            if (id.Length == 0 || path.Length == 0 || !System.IO.Path.IsPathFullyQualified(path)) return null;
            ChangeKind? kind = owner ? (op == "undo" ? ChangeKind.Undo : null) : op switch
            {
                "create" => ChangeKind.Created, "modify" => ChangeKind.Modified, "overwrite" => ChangeKind.Replaced, "delete" => ChangeKind.Deleted,
                "move" => ChangeKind.Moved, "copy" => ChangeKind.Copied, "mkdir" => ChangeKind.Folder, _ => null,
            };
            if (kind == null) return null;
            long files = N(r, "files");
            return new AgentChange(id, DateTimeOffset.TryParse(S(r, "ts"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? ts : null,
                kind.Value, owner ? "owner" : S(r, "tool"), path, S(r, "to"), S(r, "kind") == "dir", S(r, "backup"), S(r, "afterSha"),
                N(r, "bytesBefore") >= 0 ? N(r, "bytesBefore") : N(r, "bytes"), N(r, "bytesAfter"), files > 0 ? (int)Math.Min(files, int.MaxValue) : 0,
                N(r, "edits") > 0 ? (int)N(r, "edits") : 0, S(r, "agent"), S(r, "note"), S(r, "risk"), S(r, "target"), S(r, "mode"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads at most the last ByteLimit bytes; a cut first line is dropped, never half-parsed.</summary>
    internal static string[] Tail(string file, out bool cut)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - ByteLimit); cut = start > 0;
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start]; int read = 0;
        while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
        int from = 0;
        if (cut) { int nl = Array.IndexOf(buf, (byte)'\n', 0, read); from = nl < 0 ? read : nl + 1; }
        string text = new UTF8Encoding(false).GetString(buf, from, read - from).TrimStart('\uFEFF');
        return text.Split('\n');
    }

    internal static ChangeSlice Read(string historyDir)
    {
        try
        {
            string index = System.IO.Path.Combine(historyDir, IndexFile), own = System.IO.Path.Combine(historyDir, OwnerFile);
            if (!File.Exists(index) && !File.Exists(own)) return new(Array.Empty<AgentChange>(), Directory.Exists(historyDir) ? HistoryState.Ready : HistoryState.Missing, false, 0, new HashSet<string>());
            var rows = new List<AgentChange>(); int skipped = 0; bool limited = false; var undone = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (file, owner) in new[] { (index, false), (own, true) })
            {
                if (!File.Exists(file)) continue;
                string[] lines = Tail(file, out bool cut); limited |= cut;
                foreach (string raw in lines)
                {
                    string line = raw.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (Parse(line, owner) is { } c) { rows.Add(c); if (owner && c.Target.Length > 0) undone.Add(c.Target); } else skipped++;
                }
            }
            var ordered = rows.OrderByDescending(c => c.Stamp ?? DateTimeOffset.MinValue).ToList();
            if (ordered.Count > RowLimit) { ordered = ordered.Take(RowLimit).ToList(); limited = true; }
            return new(ordered, HistoryState.Ready, limited, skipped, undone);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return new(Array.Empty<AgentChange>(), HistoryState.Unavailable, false, 0, new HashSet<string>()); }
    }

    static string Sha(string file)
    {
        try { var fi = new FileInfo(file); if (!fi.Exists || fi.Length > 64L * 1024 * 1024) return ""; using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
    static bool Inside(string dir, string path)
    {
        try { string d = System.IO.Path.GetFullPath(dir).TrimEnd('\\') + "\\", p = System.IO.Path.GetFullPath(path); return p.StartsWith(d, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
    static string Sibling(string path)
    {
        string dir = System.IO.Path.GetDirectoryName(path) ?? "", stem = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        for (int i = 1; i < 100; i++) { string c = System.IO.Path.Combine(dir, stem + (i == 1 ? " (restored)" : " (restored " + i + ")") + ext); if (!Exists(c)) return c; }
        return System.IO.Path.Combine(dir, stem + " (restored " + Guid.NewGuid().ToString("N")[..6] + ")" + ext);
    }

    /// <summary>What Undo would do for this row, in plain words, and whether it can.</summary>
    internal static UndoPlan Plan(AgentChange c, IReadOnlySet<string> undone, string historyDir, bool ru)
    {
        string L(string en, string r) => ru ? r : en;
        if (undone.Contains(c.Id)) return new(false, L("Already undone", "Уже отменено"), L("This change was undone earlier. The undo is listed as its own row and can be reverted there.", "Это изменение уже отменено. Сама отмена записана отдельной строкой — её тоже можно вернуть."), false);
        var (op, from, dest) = c.Reverse;
        string stamp = c.Stamp?.ToLocalTime().ToString("HH:mm") ?? "";
        if (op is Inverse.RestoreVersion or Inverse.Untrash && from.Length > 0 && !Inside(historyDir, from))
            return new(false, L("Copy outside history", "Копия вне истории"), L("The saved copy is not inside ZeroIDE history, so it is not used.", "Сохранённая копия лежит не в истории ZeroIDE, поэтому не используется."), true);
        switch (op)
        {
            case Inverse.RestoreVersion:
                if (!File.Exists(from)) return new(false, L("Copy no longer kept", "Копия уже удалена"), L("History keeps copies for 30 days; this one is gone.", "История хранит копии 30 дней; эта уже удалена."), false);
                bool later = c.AfterSha.Length > 0 && File.Exists(dest) && Sha(dest) is { Length: > 0 } now && now != c.AfterSha;
                return new(true, L("Restore version before ", "Вернуть версию до ") + stamp,
                    (later ? L("The file changed again after this. Those later edits will be replaced too, ", "После этого файл ещё менялся. Эти правки тоже будут заменены, ") : L("The file gets its previous content back, ", "Файл получит прежнее содержимое, "))
                    + L("and the current version is saved to history first.", "а текущая версия сначала сохранится в истории."), later);
            case Inverse.Trash:
                if (!Exists(from)) return new(false, L("Nothing to remove", "Удалять нечего"), L("The file is no longer there.", "Файла уже нет на месте."), false);
                bool edited = c.Kind == ChangeKind.Created && c.AfterSha.Length > 0 && File.Exists(from) && Sha(from) is { Length: > 0 } cur && cur != c.AfterSha;
                return new(true, c.Kind == ChangeKind.Copied ? L("Remove the copy", "Убрать копию") : L("Remove created file", "Убрать созданный файл"),
                    (edited ? L("It was edited after creation. ", "После создания его меняли. ") : "") + L("It moves to ZeroIDE history, so this is reversible.", "Он переместится в историю ZeroIDE, это обратимо."), edited);
            case Inverse.Untrash:
                if (!Exists(from)) return new(false, L("Copy no longer kept", "Копия уже удалена"), L("History keeps deleted items for 30 days; this one is gone.", "История хранит удалённое 30 дней; эта копия уже удалена."), false);
                return Exists(dest)
                    ? new(true, L("Restore next to it", "Восстановить рядом"), L("Something with this name exists again, so the item comes back as ", "На этом месте снова есть файл с таким именем, поэтому восстановленный получит имя ") + System.IO.Path.GetFileName(Sibling(dest)) + ".", true)
                    : new(true, L("Put it back", "Вернуть на место"), L("The deleted item returns to its original folder.", "Удалённое вернётся в исходную папку."), false);
            case Inverse.Move:
                if (!Exists(from)) return new(false, L("Cannot move back", "Вернуть нельзя"), L("The item is no longer at its new location.", "На новом месте этого уже нет."), false);
                if (Exists(dest)) return new(false, L("Original place is taken", "Исходное место занято"), L("Something new now exists at the original path; move it away first.", "По исходному пути теперь лежит другой файл; сначала уберите его."), true);
                return new(true, L("Move it back", "Вернуть на прежнее место"), L("Moves it back to ", "Переместит обратно в ") + dest, false);
            case Inverse.RemoveEmptyDir:
                if (!Directory.Exists(from)) return new(false, L("Folder is gone", "Папки уже нет"), "", false);
                if (Directory.EnumerateFileSystemEntries(from).Any()) return new(false, L("Folder is not empty", "Папка не пуста"), L("Only an empty folder is removed this way.", "Так удаляется только пустая папка."), false);
                return new(true, L("Remove empty folder", "Удалить пустую папку"), L("The folder the agent created is empty and will be removed.", "Созданная агентом папка пуста и будет удалена."), false);
            case Inverse.MakeDir:
                return Exists(from) ? new(false, L("Already exists", "Уже существует"), "", false) : new(true, L("Create the folder again", "Создать папку снова"), "", false);
            default:
                return new(false, L("No copy was kept", "Копия не сохранялась"), L("Large files (over 25 MB) and changes made with backup=false are recorded without a copy.", "Большие файлы (больше 25 МБ) и правки с backup=false записываются без копии."), false);
        }
    }

    static string NewId() => "own_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x") + RandomNumberGenerator.GetHexString(6, true);
    static string Day => DateTime.UtcNow.ToString("yyyy-MM-dd");
    static string SafeName(string path) { string n = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')); foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_'); return n.Length > 90 ? n[^90..] : n.Length == 0 ? "item" : n; }
    static void MovePath(string src, string dst)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
        if (Directory.Exists(src)) Directory.Move(src, dst); else File.Move(src, dst);
    }
    static void Record(string historyDir, object entry)
    {
        Directory.CreateDirectory(historyDir);
        File.AppendAllText(System.IO.Path.Combine(historyDir, OwnerFile), JsonSerializer.Serialize(entry) + "\n", new UTF8Encoding(false));
    }

    /// <summary>Performs the inverse of a row and records it in owner.jsonl. Returns the path that changed.</summary>
    internal static string Undo(AgentChange c, IReadOnlySet<string> undone, string historyDir, bool ru)
    {
        var plan = Plan(c, undone, historyDir, ru);
        if (!plan.Possible) throw new InvalidOperationException(plan.Action);
        var (op, from, dest) = c.Reverse; string id = NewId(), ts = DateTimeOffset.UtcNow.ToString("o");
        string note = (ru ? "Отмена: " : "Undo: ") + c.Kind + " " + c.Name;
        switch (op)
        {
            case Inverse.RestoreVersion:
            {
                string? saved = null;
                if (File.Exists(dest)) { string dir = System.IO.Path.Combine(historyDir, Day); Directory.CreateDirectory(dir); saved = System.IO.Path.Combine(dir, id + "__" + SafeName(dest)); File.Copy(dest, saved, false); }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                string tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dest)!, "." + SafeName(dest) + ".restore-" + id[^6..]);
                File.Copy(from, tmp, true); File.Move(tmp, dest, true);
                Record(historyDir, new { v = 1, ts, id, op = "undo", target = c.Id, mode = "restore-version", path = dest, kind = "file", backup = saved, afterSha = Sha(dest), note });
                return dest;
            }
            case Inverse.Trash:
            {
                string keep = System.IO.Path.Combine(historyDir, "trash", Day, id, SafeName(from)); bool dir = Directory.Exists(from);
                MovePath(from, keep);
                Record(historyDir, new { v = 1, ts, id, op = "undo", target = c.Id, mode = "trash", path = from, kind = dir ? "dir" : "file", backup = keep, note });
                return from;
            }
            case Inverse.Untrash:
            {
                string place = Exists(dest) ? Sibling(dest) : dest; bool dir = Directory.Exists(from);
                MovePath(from, place);
                Record(historyDir, new { v = 1, ts, id, op = "undo", target = c.Id, mode = "untrash", path = place, kind = dir ? "dir" : "file", afterSha = dir ? "" : Sha(place), note });
                return place;
            }
            case Inverse.Move:
                MovePath(from, dest);
                Record(historyDir, new { v = 1, ts, id, op = "undo", target = c.Id, mode = "move-back", path = dest, to = from, kind = Directory.Exists(dest) ? "dir" : "file", note });
                return dest;
            case Inverse.RemoveEmptyDir:
                Directory.Delete(from, false);
                Record(historyDir, new { v = 1, ts, id, op = "undo", target = c.Id, mode = "rmdir", path = from, kind = "dir", note });
                return from;
            case Inverse.MakeDir:
                Directory.CreateDirectory(from);
                Record(historyDir, new { v = 1, ts, id, op = "undo", target = c.Id, mode = "mkdir", path = from, kind = "dir", note });
                return from;
            default: throw new InvalidOperationException(plan.Action);
        }
    }

    /// <summary>Sample rows for UI previews (--preview, tests): realistic paths and plans, nothing read from disk.</summary>
    internal static (ChangeSlice Slice, Dictionary<string, UndoPlan> Plans, HashSet<string> Copies) PreviewSamples(DateTimeOffset now, bool ru)
    {
        string L(string en, string r) => ru ? r : en;
        string At(int min) => now.AddMinutes(-min).ToLocalTime().ToString("HH:mm");
        AgentChange Row(int min, ChangeKind k, string tool, string path, string agent, string note, string to = "", int edits = 0, int files = 0, string risk = "", string target = "", string mode = "")
            => new("chg_p" + min, now.AddMinutes(-min), k, tool, path, to, k == ChangeKind.Folder || files > 1, "", "", -1, -1, files, edits, agent, note, risk, target, mode);
        var rows = new List<AgentChange>
        {
            Row(2, ChangeKind.Undo, "owner", @"C:\Projects\api\.env.example", "", "", target: "chg_p22", mode: "restore-version"),
            Row(3, ChangeKind.Modified, "edit_file", @"C:\Projects\landing\index.html", "Claude · " + L("Landing", "Лендинг"), L("New headline and price", "Новый заголовок и цена"), edits: 2),
            Row(6, ChangeKind.Deleted, "file_ops", @"C:\Projects\landing\old-build", "Claude · " + L("Landing", "Лендинг"), L("Removing the old build", "Убираю старую сборку"), files: 214, risk: "high"),
            Row(9, ChangeKind.Created, "write_file", @"C:\Projects\landing\styles\hero.css", "Claude · " + L("Landing", "Лендинг"), L("Styles for the new block", "Стили для нового блока")),
            Row(15, ChangeKind.Moved, "file_ops", @"C:\Projects\api\notes.md", "Codex", L("Moving notes to docs", "Переношу заметки в docs"), to: @"C:\Projects\api\docs\notes.md"),
            Row(22, ChangeKind.Replaced, "file_ops", @"C:\Projects\api\.env.example", "Codex", L("Updating the settings example", "Обновляю пример настроек")),
            Row(41, ChangeKind.Folder, "file_ops", @"C:\Projects\api\docs", "Codex", L("Folder for documentation", "Папка для документации")),
        };
        var plans = new Dictionary<string, UndoPlan>(StringComparer.Ordinal)
        {
            ["chg_p2"] = new(true, L("Restore version before ", "Вернуть версию до ") + At(2), L("Brings back the version you replaced with this undo.", "Вернёт версию, которую заменила ваша отмена."), false),
            ["chg_p3"] = new(true, L("Restore version before ", "Вернуть версию до ") + At(3), L("The file gets its previous content back, and the current version is saved to history first.", "Файл получит прежнее содержимое, а текущая версия сначала сохранится в истории."), false),
            ["chg_p6"] = new(true, L("Put it back", "Вернуть на место"), L("All 214 files return to the original folder.", "Все 214 файлов вернутся в исходную папку."), false),
            ["chg_p9"] = new(true, L("Remove created file", "Убрать созданный файл"), L("It moves to ZeroIDE history, so this is reversible.", "Он переместится в историю ZeroIDE, это обратимо."), false),
            ["chg_p15"] = new(true, L("Move it back", "Вернуть на прежнее место"), L("Moves it back to ", "Переместит обратно в ") + @"C:\Projects\api\notes.md", false),
            ["chg_p22"] = new(false, L("Already undone", "Уже отменено"), L("This change was undone earlier.", "Это изменение уже отменено."), false),
            ["chg_p41"] = new(false, L("Folder is not empty", "Папка не пуста"), L("Only an empty folder is removed this way.", "Так удаляется только пустая папка."), false),
        };
        return (new ChangeSlice(rows, HistoryState.Ready, false, 0, new HashSet<string>(StringComparer.Ordinal) { "chg_p22" }), plans, new HashSet<string>(StringComparer.Ordinal) { "chg_p3" });
    }

    // ---- Comparison of a kept copy with the file on disk (text only) ----
    internal static bool LooksText(string file, out string text)
    {
        text = "";
        try { var fi = new FileInfo(file); if (!fi.Exists || fi.Length > 2 * 1024 * 1024) return false; byte[] b = File.ReadAllBytes(file); if (Array.IndexOf(b, (byte)0) >= 0) return false; text = new UTF8Encoding(false).GetString(b).TrimStart('\uFEFF'); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Unified-style line diff with 3 lines of context; null when the inputs are too large to compare.</summary>
    internal static List<(char Kind, string Text)>? Diff(string before, string after, int context = 3)
    {
        var detailed = DiffDetailed(before, after, context, fullFile: false);
        if (detailed == null) return null;
        return detailed.Select(d => (d.Kind, d.Text)).ToList();
    }

    /// <summary>Line diff with 1-based before/after line numbers and skipped-line counts; null when too large.</summary>
    internal static List<DiffLine>? DiffDetailed(string before, string after, int context = 3, bool fullFile = false)
    {
        string[] a = before.Replace("\r\n", "\n").Split('\n'), b = after.Replace("\r\n", "\n").Split('\n');
        int pre = 0; while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0; while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        int n = a.Length - pre - suf, m = b.Length - pre - suf;
        if ((long)n * m > 4_000_000) return null;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--) for (int j = m - 1; j >= 0; j--) lcs[i, j] = a[pre + i] == b[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var all = new List<DiffLine>(pre + n + m + suf);
        int oldNo = 1, newNo = 1;
        for (int k = 0; k < pre; k++) all.Add(new DiffLine(' ', oldNo++, newNo++, a[k]));
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[pre + x] == b[pre + y]) { all.Add(new DiffLine(' ', oldNo++, newNo++, a[pre + x])); x++; y++; }
            else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1])) { all.Add(new DiffLine('-', oldNo++, null, a[pre + x])); x++; } // removed lines first, as in unified diffs
            else { all.Add(new DiffLine('+', null, newNo++, b[pre + y])); y++; }
        }
        for (int k = a.Length - suf; k < a.Length; k++) all.Add(new DiffLine(' ', oldNo++, newNo++, a[k]));

        if (fullFile) return all;

        var keep = new bool[all.Count];
        for (int i = 0; i < all.Count; i++)
            if (all[i].Kind != ' ')
                for (int k = Math.Max(0, i - context); k <= Math.Min(all.Count - 1, i + context); k++) keep[k] = true;

        var outp = new List<DiffLine>();
        int skipped = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (keep[i])
            {
                if (skipped > 0) { outp.Add(new DiffLine('…', null, null, "", skipped)); skipped = 0; }
                outp.Add(all[i]);
            }
            else
            {
                skipped++;
            }
        }
        if (skipped > 0) outp.Add(new DiffLine('…', null, null, "", skipped));
        return outp;
    }
}
