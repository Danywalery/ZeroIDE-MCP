using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZeroIDE.Mcp;

// v0.5 change history: server record parsing, undo plans, real undo on a temp fixture,
// and Settings → Changes driven exactly like clicks. Every file lives under the per-run temp folder.
internal static partial class TrayUiTests
{
    static string ShaOf(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();

    static void ChangesFlow(string tmp)
    {
        string root = Path.Combine(tmp, "changes"), proj = Path.Combine(root, "proj"), hist = Path.Combine(root, "history");
        string day = DateTime.UtcNow.ToString("yyyy-MM-dd"), copies = Path.Combine(hist, day);
        Directory.CreateDirectory(proj); Directory.CreateDirectory(copies);
        var t0 = DateTimeOffset.UtcNow.AddSeconds(-60); int tick = 0; var lines = new List<string>();
        void Row(object o) => lines.Add(JsonSerializer.Serialize(o));
        string Ts() => t0.AddSeconds(++tick).ToString("o");

        // The same record shapes server.mjs appends to history/index.jsonl.
        string a = Path.Combine(proj, "a.txt"), aCopy = Path.Combine(copies, "chg_a__a.txt");
        File.WriteAllText(aCopy, "alpha\nold line\nomega\n"); File.WriteAllText(a, "alpha\nnew line\nomega\n");
        Row(new { v = 1, ts = Ts(), id = "chg_a", op = "modify", tool = "edit_file", path = a, kind = "file", backup = aCopy, afterSha = ShaOf(a), bytesBefore = 21, bytesAfter = 21, agent = "Claude · Тест", note = "Правка строки", edits = 1 });
        string b = Path.Combine(proj, "b.txt"), bKeep = Path.Combine(hist, "trash", day, "chg_b", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(bKeep)!); File.WriteAllText(bKeep, "bee");
        Row(new { v = 1, ts = Ts(), id = "chg_b", op = "delete", tool = "file_ops", path = b, kind = "file", backup = bKeep, files = 1, bytes = 3 });
        string c = Path.Combine(proj, "c.txt"); File.WriteAllText(c, "sea");
        Row(new { v = 1, ts = Ts(), id = "chg_c", op = "create", tool = "write_file", path = c, kind = "file", backup = (string?)null, afterSha = ShaOf(c), bytesBefore = 0, bytesAfter = 3 });
        string d = Path.Combine(proj, "d.txt"), dTo = Path.Combine(proj, "sub", "d.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dTo)!); File.WriteAllText(dTo, "dee");
        Row(new { v = 1, ts = Ts(), id = "chg_d", op = "move", tool = "file_ops", path = d, to = dTo, kind = "file" });
        string e = Path.Combine(proj, "empty"); Directory.CreateDirectory(e);
        Row(new { v = 1, ts = Ts(), id = "chg_e", op = "mkdir", tool = "file_ops", path = e, kind = "dir" });
        string f = Path.Combine(proj, "f.txt"), fKeep = Path.Combine(hist, "trash", day, "chg_f", "f.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(fKeep)!); File.WriteAllText(fKeep, "old f"); File.WriteAllText(f, "new f");
        Row(new { v = 1, ts = Ts(), id = "chg_f", op = "delete", tool = "file_ops", path = f, kind = "file", backup = fKeep });
        string g = Path.Combine(proj, "g.txt"), outside = Path.Combine(root, "outside.txt"); File.WriteAllText(g, "g now"); File.WriteAllText(outside, "planted");
        Row(new { v = 1, ts = Ts(), id = "chg_g", op = "modify", tool = "write_file", path = g, kind = "file", backup = outside });
        Row(new { v = 1, ts = Ts(), id = "chg_h", op = "modify", tool = "write_file", path = g, kind = "file", backup = (string?)null });
        lines.Add("{\"v\":1,\"id\":\"chg_x\",\"op\":\"modify\",\"path\":\"relative\\\\x.txt\"}");
        lines.Add("{\"v\":1,\"id\":\"chg_y\",\"op\":\"format_disk\",\"path\":\"C:\\\\x\"}");
        lines.Add("{broken");
        File.WriteAllText(Path.Combine(hist, ChangeHistory.IndexFile), string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

        Check(ChangeHistory.Read(Path.Combine(root, "nope")).State == HistoryState.Missing && ChangeHistory.Read(proj) is { State: HistoryState.Ready, Rows.Count: 0 }, "missing history is distinct from an empty one");
        var s = ChangeHistory.Read(hist);
        Check(s.Rows.Count == 8 && s.Skipped == 3 && s.Rows[0].Id == "chg_h" && s.Rows[^1].Id == "chg_a", "history parses newest first (CRLF too); relative paths, unknown ops and broken lines are skipped");
        Check(s.Rows[^1] is { Kind: ChangeKind.Modified, Agent: "Claude · Тест", Edits: 1, BytesBefore: 21 } && s.Rows.Single(x => x.Id == "chg_d").Shown == dTo, "row keeps agent, edit count, sizes and the new location of a moved file");
        AgentChange R(string id) => ChangeHistory.Read(hist).Rows.Single(x => x.Id == id);
        var none = new HashSet<string>();
        Check(ChangeHistory.Plan(R("chg_a"), none, hist, true) is { Possible: true, Caution: false } && ChangeHistory.Plan(R("chg_g"), none, hist, true) is { Possible: false, Caution: true }
            && ChangeHistory.Plan(R("chg_h"), none, hist, false) is { Possible: false, Action: "No copy was kept" }, "plans: restorable edit, copy outside history refused, missing copy explained");
        bool threw = false; try { ChangeHistory.Undo(R("chg_g"), none, hist, true); } catch (InvalidOperationException) { threw = true; }
        Check(threw && File.ReadAllText(g) == "g now", "a planted copy outside ZeroIDE history is never restored");

        File.AppendAllText(a, "later\n"); string onDisk = File.ReadAllText(a);
        Check(ChangeHistory.Plan(R("chg_a"), none, hist, true).Caution, "later edits after the agent change are flagged before restoring");
        ChangeHistory.Undo(R("chg_a"), none, hist, true); s = ChangeHistory.Read(hist);
        var undoRow = s.Rows.First(x => x.Kind == ChangeKind.Undo && x.Target == "chg_a");
        Check(File.ReadAllText(a) == "alpha\nold line\nomega\n" && s.Undone.Contains("chg_a") && File.Exists(Path.Combine(hist, ChangeHistory.OwnerFile)), "undo restores the kept bytes and records the owner action in owner.jsonl");
        Check(ChangeHistory.Plan(R("chg_a"), s.Undone, hist, true) is { Possible: false } && undoRow.Reverse.Op == Inverse.RestoreVersion, "an undone change cannot be undone twice, but the undo itself is reversible");
        ChangeHistory.Undo(undoRow, s.Undone, hist, true);
        Check(File.ReadAllText(a) == onDisk, "undoing the undo brings back exactly what was on disk");

        ChangeHistory.Undo(R("chg_b"), none, hist, true);
        Check(File.ReadAllText(b) == "bee" && !File.Exists(bKeep), "deleted file returns from ZeroIDE trash to its folder");
        var fPlan = ChangeHistory.Plan(R("chg_f"), none, hist, true); ChangeHistory.Undo(R("chg_f"), none, hist, true);
        Check(fPlan is { Possible: true, Caution: true } && File.ReadAllText(f) == "new f" && File.ReadAllText(Path.Combine(proj, "f (restored).txt")) == "old f", "restoring onto a taken name keeps both files");
        ChangeHistory.Undo(R("chg_c"), none, hist, true); s = ChangeHistory.Read(hist);
        var trashRow = s.Rows.First(x => x.Kind == ChangeKind.Undo && x.Target == "chg_c");
        Check(!File.Exists(c) && File.Exists(trashRow.Backup) && trashRow.Backup.StartsWith(hist, StringComparison.OrdinalIgnoreCase), "removing a created file moves it into ZeroIDE history");
        ChangeHistory.Undo(trashRow, s.Undone, hist, true);
        Check(File.ReadAllText(c) == "sea", "a removed file can be put back");
        ChangeHistory.Undo(R("chg_d"), none, hist, true);
        Check(File.ReadAllText(d) == "dee" && !File.Exists(dTo), "moved file goes back to its original path");
        File.WriteAllText(Path.Combine(e, "x"), "x");
        Check(ChangeHistory.Plan(R("chg_e"), none, hist, true) is { Possible: false }, "a folder with content is never removed by undo");
        File.Delete(Path.Combine(e, "x")); ChangeHistory.Undo(R("chg_e"), none, hist, true);
        Check(!Directory.Exists(e), "an empty folder created by the agent can be removed");

        var diff = ChangeHistory.Diff("a\nb\nc\n", "a\nB\nc\n")!;
        Check(diff.Contains(('-', "b")) && diff.Contains(('+', "B")) && ChangeHistory.Diff("x\ny", "x\ny")!.All(l => l.Kind is not ('+' or '-')), "line diff marks removed and added lines and nothing else");
        string big = string.Join("\n", Enumerable.Range(0, 2100).Select(i => "a" + i)), big2 = string.Join("\n", Enumerable.Range(0, 2100).Select(i => "b" + i));
        Check(ChangeHistory.Diff(big, big2) == null && ChangeHistory.LooksText(c, out var txt) && txt == "sea", "oversized comparisons are refused instead of freezing the window");
        Check(ChangeHistory.Match("ЗАМЕТКИ ёлка", "Переношу заметки, елка") && !ChangeHistory.Match("zzz", "abc") && ChangeHistory.Match("", "anything"), "search is case-insensitive, multiword and treats ё as е");
    }

    static void SettingsChanges(string tmp)
    {
        // Live section over a temp history (LogDir = root\logs → history = root\history, as in the tray).
        string root = Path.Combine(tmp, "settings"), lp = Path.Combine(root, "proj"), lh = Path.Combine(root, "history");
        string day = DateTime.UtcNow.ToString("yyyy-MM-dd"), lcopy = Path.Combine(lh, day, "chg_l__page.html"), page = Path.Combine(lp, "page.html");
        Directory.CreateDirectory(lp); Directory.CreateDirectory(Path.GetDirectoryName(lcopy)!);
        File.WriteAllText(lcopy, "<h1>Old</h1>\n"); File.WriteAllText(page, "<h1>New</h1>\n");
        string gone = Path.Combine(lp, "gone.txt"), goneKeep = Path.Combine(lh, "trash", day, "chg_k", "gone.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(goneKeep)!); File.WriteAllText(goneKeep, "kept");
        File.WriteAllText(Path.Combine(lh, ChangeHistory.IndexFile),
            JsonSerializer.Serialize(new { v = 1, ts = DateTimeOffset.UtcNow.AddSeconds(-9).ToString("o"), id = "chg_k", op = "delete", tool = "file_ops", path = gone, kind = "file", backup = goneKeep, agent = "Codex" }) + "\n" +
            JsonSerializer.Serialize(new { v = 1, ts = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("o"), id = "chg_l", op = "modify", tool = "edit_file", path = page, kind = "file", backup = lcopy, afterSha = ShaOf(page), agent = "Codex", note = "Заголовок" }) + "\n");
        var ctx = FakeTray(root);
        Check(ctx.HistoryDirPath == Path.GetFullPath(lh), "history folder sits next to the logs folder");
        using (var form = new SettingsForm(ctx, "changes"))
        {
            form.LoadChangesNow(); form.PaintForTests();
            var zones = form.ZoneIdsForTests;
            Check(zones.Contains("nav_changes") && zones.Contains("chg_undo_chg_l") && zones.Contains("chg_undo_chg_k") && zones.Contains("chg_cmp_chg_l") && !zones.Contains("chg_cmp_chg_k"),
                "sidebar has «Изменения»; rows offer undo, and compare only where a text copy exists");
            Check(form.ZoneTooltipForTests("chg_undo_chg_l").StartsWith("Вернуть версию до"), "the undo button explains exactly what it will do");
            form.ConfirmUndoForTests = (_, _) => false; form.InvokeZoneForTests("chg_undo_chg_l");
            Check(File.ReadAllText(page) == "<h1>New</h1>\n" && !File.Exists(Path.Combine(lh, ChangeHistory.OwnerFile)), "declining the confirmation leaves the file and history untouched");
            form.ConfirmUndoForTests = (_, _) => true; form.InvokeZoneForTests("chg_undo_chg_l");
            Check(File.ReadAllText(page) == "<h1>Old</h1>\n" && form.ChangesStatus.StartsWith("Готово") && form.ChangeRows.Count == 3, "confirmed undo restores the file; the owner's undo appears as its own row");
            form.PaintForTests(); zones = form.ZoneIdsForTests;
            string undoId = form.ChangeRows.First(r => r.Kind == ChangeKind.Undo).Id;
            Check(!zones.Contains("chg_undo_chg_l") && zones.Contains("chg_why_chg_l") && zones.Contains("chg_undo_" + undoId) && zones.Contains("chg_status"), "the undone row shows why it cannot be undone again, the undo row is reversible");
            form.InvokeZoneForTests("chg_flt_deletions"); form.PaintForTests(); zones = form.ZoneIdsForTests;
            Check(zones.Contains("chg_undo_chg_k") && !zones.Contains("chg_why_chg_l"), "«Удаления» filter shows deleted files only");
            form.InvokeZoneForTests("chg_status"); form.InvokeZoneForTests("chg_flt_all");
            form.ConfirmUndoForTests = (_, _) => true; form.PaintForTests(); form.InvokeZoneForTests("chg_undo_chg_k");
            Check(File.ReadAllText(gone) == "kept", "a deleted file comes back from the history trash");
            form.Size = new Size(1000, 680); form.PaintForTests();
            Check(form.ZoneIdsForTests.Contains("chg_reveal_chg_l"), "the section also lays out in the smallest window");

            string revealed = "";
            form.RevealForTests = p => revealed = p;
            Check(form.InvokeZoneForTests("chg_reveal_chg_l") && revealed == page, "clicking «Show in folder» dispatches the exact file path");

            // Reveal target resolution: spaces + Cyrillic file, directory, deleted file, deleted nested subfolder.
            string cyrDir = Path.Combine(lp, "Папка с пробелом");
            string cyrFile = Path.Combine(cyrDir, "README.txt");
            Directory.CreateDirectory(cyrDir);
            File.WriteAllText(cyrFile, "hello");
            Check(DesktopLauncher.ResolveRevealTarget(cyrFile.Replace('\\', '/')) == ("select", cyrFile), "reveal normalizes slashes and selects an existing Cyrillic/spaced file");
            Check(DesktopLauncher.ResolveRevealTarget(cyrDir) == ("folder", cyrDir), "reveal opens an existing directory directly");
            Check(DesktopLauncher.ResolveRevealTarget(Path.Combine(cyrDir, "missing.txt")) == ("folder", cyrDir), "reveal falls back to parent folder when file was deleted");
            Check(DesktopLauncher.ResolveRevealTarget(Path.Combine(cyrDir, "gone-sub", "deep", "x.txt")) == ("folder", cyrDir), "reveal walks up to nearest existing ancestor folder");

            // Detailed diff line numbers and skipped counts.
            string sampleBefore = string.Join("\n",
                "# Hill Rider PC",
                "Версия: 0.4.2",
                "",
                "## Трассы",
                "- Альпы (гравитация 1.0)",
                "- Каньон (гравитация 1.0)",
                "- Снег (гравитация 1.0)",
                "",
                "## Управление",
                "Газ: D / Стрелка вправо",
                "Тормоз: A / Стрелка влево",
                "Сброс: R",
                "Пауза: Esc",
                "Камера: C",
                "Звук: M",
                "",
                "## Тесты",
                "python tests/browser-smoke.py --tracks 3");
            string sampleAfter = string.Join("\n",
                "# Hill Rider PC",
                "Версия: 0.5.0",
                "",
                "## Трассы",
                "- Альпы (гравитация 1.0)",
                "- Каньон (гравитация 1.0)",
                "- Снег (гравитация 1.0)",
                "- Луна SS 04 (низкая гравитация 0.38)",
                "",
                "## Управление",
                "Газ: D / Стрелка вправо",
                "Тормоз: A / Стрелка влево",
                "Сброс: R",
                "Пауза: Esc",
                "Камера: C",
                "Звук: M",
                "",
                "## Тесты",
                "python tests/browser-smoke.py --tracks 4 --include-moon");

            var detailed = ChangeHistory.DiffDetailed(sampleBefore, sampleAfter, context: 2, fullFile: false)!;
            var fullDetailed = ChangeHistory.DiffDetailed(sampleBefore, sampleAfter, context: 2, fullFile: true)!;
            Check(detailed.Any(d => d.Kind == '…' && d.SkippedCount > 0) && fullDetailed.All(d => d.Kind != '…'), "detailed diff tracks skipped line counts and supports full-file mode");

            using (var dlg = new ChangeCompareForm("page.html", "12:00", "<h1>Old</h1>\n", "<h1>New</h1>\n", true))
            {
                Check(dlg.DiffText.Contains("- <h1>Old</h1>") && dlg.DiffText.Contains("+ <h1>New</h1>"), "compare window lists removed and added lines");
                dlg.FullFileMode = true;
                dlg.WordWrapMode = true;
                Check(dlg.FullFileMode && dlg.WordWrapMode, "compare window toggles full-file and word-wrap modes cleanly");
            }

            foreach (var (lang, suffix) in new[] { ("ru", "ru"), ("en", "en") })
            {
                I18n.CurrentLanguage = lang;
                using var cmp = new ChangeCompareForm(
                    "README.txt",
                    "00:35",
                    sampleBefore,
                    sampleAfter,
                    fileExists: true,
                    fullPath: @"C:\Users\Даня\Desktop\Hill Rider PC\README.txt",
                    agent: "monday agent",
                    note: lang == "ru" ? "Описываю новую лунную трассу в README" : "Documenting the new lunar track in README",
                    canUndo: true);
                string cmpFile = Path.Combine(tmp, "compare-dialog-" + suffix + ".png");
                cmp.RenderPreviewToFile(cmpFile);
                using var bmp = new Bitmap(cmpFile);
                Check(Near(bmp.GetPixel(32, 22), Color.FromArgb(0x82, 0xEB, 0xC8)), "compare dialog header shows the ZeroIDE logo (" + lang + ")");
                Save(bmp, "compare-dialog-" + suffix);
            }
            I18n.CurrentLanguage = "ru";
        }
        using (var empty = new SettingsForm(FakeTray(Path.Combine(tmp, "empty")), "changes"))
        {
            empty.LoadChangesNow(); empty.PaintForTests();
            Check(empty.ChangeRows.Count == 0 && !empty.ZoneIdsForTests.Any(z => z.StartsWith("chg_undo_")), "no history yet: an explanation instead of an empty list");
        }

        // Renders for the owner: the real Settings window with sample rows, RU and EN, plus About.
        foreach (var (lang, suffix) in new[] { ("ru", "ru"), ("en", "en") })
        {
            I18n.CurrentLanguage = lang;
            using var show = new SettingsForm(FakeTray(Path.Combine(tmp, "show-" + lang)), "changes");
            show.SetChangesForPreview(ChangeHistory.PreviewSamples(DateTimeOffset.Now, lang == "ru"));
            string file = Path.Combine(tmp, "settings-changes-" + suffix + ".png");
            show.RenderPreviewToFile(file, "changes");
            using (var bmp = new Bitmap(file))
            {
                Check(Near(bmp.GetPixel(28, 21), Color.FromArgb(0x82, 0xEB, 0xC8)), "Settings sidebar shows the logo (" + lang + ")");
                Save(bmp, "settings-changes-" + suffix);
            }
            if (lang == "ru")
            {
                string about = Path.Combine(tmp, "settings-about-ru.png"); show.RenderPreviewToFile(about, "about");
                using (var ab = new Bitmap(about)) Save(ab, "settings-about-ru");
                string tools = Path.Combine(tmp, "settings-tools-ru.png"); show.RenderPreviewToFile(tools, "tools");
                using (var tb = new Bitmap(tools)) Save(tb, "settings-tools-ru");
            }
        }
        I18n.CurrentLanguage = "ru";
    }
}
