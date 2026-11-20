using System.Drawing.Imaging;
using System.Text.Json;
using ZeroIDE.Mcp;

// v0.5 tray surfaces: risk/diff approval cards and the agent-message "Open" link.
internal static partial class TrayUiTests
{
    static ApprovalRequestDto CardFixture(string json) => JsonSerializer.Deserialize<ApprovalRequestDto>(json)!;

    static void RenderCard(ApprovalRequestDto req, string name, Action<ApprovalToastForm>? prepare = null)
    {
        int h = ApprovalToastManager.ComputeCardHeight(req);
        using var form = new ApprovalToastForm(req, 10, "tool", "glass", (_, _) => { }, ApprovalToastManager.CardWidth, h, 0, 0, 0, _ => { });
        prepare?.Invoke(form);
        using var bmp = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(24, 28, 34)); form.RenderToGraphics(g); }
        if (captures.Length > 0) bmp.Save(Path.Combine(captures, name + ".png"), ImageFormat.Png);
    }

    static void GuardianCards()
    {
        string before = I18n.CurrentLanguage; I18n.CurrentLanguage = "ru";
        try
        {
            // The exact payload server.mjs sends for an escalated critical command.
            var critical = CardFixture("{\"id\":\"apr_1\",\"tool\":\"run_command\",\"note\":\"Чищу старые сборки перед релизом\",\"summary\":{\"title\":\"Команда PowerShell\",\"target\":\"Remove-Item -Recurse -Force C:\\\\Users\\\\Dev\\\\* ; vssadmin delete shadows /all\",\"detail\":\"\"},\"timeoutSec\":60,\"agent\":\"Claude · Релиз\"," +
                "\"assessment\":{\"level\":\"critical\",\"headline\":\"Критический риск: проверьте каждое слово\",\"reasons\":[\"Удаляет точки восстановления или меняет загрузку Windows.\",\"Удаляет целый диск, папку Windows или профиль пользователя.\",\"Принудительно удаляет файлы, включая скрытые и защищённые от записи.\"],\"escalated\":\"grant\",\"grantable\":false}}");
            Check(critical.Assessment is { Level: "critical", Grantable: false, Reasons.Count: 3 } && critical.Agent == "Claude · Релиз", "card DTO reads assessment, agent and grantable from the server payload");
            Check(ApprovalToastManager.ComputeCardHeight(critical) > ApprovalToastManager.ToolCardHeight, "risk block gets its own room on the card");
            ApprovalResponseDto? answer = null;
            using (var form = new ApprovalToastForm(critical, 10, "tool", "glass", (_, _) => { }, ApprovalToastManager.CardWidth, ApprovalToastManager.ComputeCardHeight(critical), 0, 0, 0, r => answer = r))
            {
                Check(!form.Grantable, "critical card does not offer a temporary grant");
                using var bmp = new Bitmap(form.Width, form.Height); using var g = Graphics.FromImage(bmp); form.RenderToGraphics(g);
                form.ForceResolve("allow_grant", 30, "tool", "simulate");
            }
            Check(answer is { Decision: "allow_once", GrantMinutes: null }, "a grant answer on a critical card is sent as allow_once");
            RenderCard(critical, "approval-critical-ru");

            var edit = CardFixture("{\"id\":\"apr_2\",\"tool\":\"edit_file\",\"note\":\"Обновляю заголовок и цену на лендинге\",\"summary\":{\"title\":\"Правка файла\",\"target\":\"C:\\\\Projects\\\\landing\\\\index.html\",\"detail\":\"правок: 2\"},\"timeoutSec\":60,\"agent\":\"Лендинг\"," +
                "\"assessment\":{\"level\":\"low\",\"headline\":\"Низкий риск\",\"reasons\":[],\"grantable\":true}," +
                "\"preview\":{\"exists\":true,\"added\":2,\"removed\":2,\"text\":\"…\\n   <header>\\n-    <h1>Старый заголовок</h1>\\n+    <h1>ZeroIDE: агент на вашем ПК</h1>\\n   </header>\\n…\\n-  <p class=\\\"price\\\">990 ₽</p>\\n+  <p class=\\\"price\\\">0 ₽ — бесплатно</p>\\n…\"}}");
            Check(edit.Preview is { Added: 2, Removed: 2, Exists: true } && ApprovalToastManager.PreviewLineCount(edit) == 9, "diff preview payload is parsed line by line");
            using (var form = new ApprovalToastForm(edit, 10, "tool", "glass", (_, _) => { }, ApprovalToastManager.CardWidth, ApprovalToastManager.ComputeCardHeight(edit), 0, 0, 0, _ => { }))
                Check(form.Grantable, "low-risk edit keeps the temporary grant button");
            RenderCard(edit, "approval-diff-ru");

            var high = CardFixture("{\"id\":\"apr_3\",\"tool\":\"run_command\",\"note\":\"Ставлю ночную сборку\",\"summary\":{\"title\":\"Команда PowerShell\",\"target\":\"schtasks /create /tn NightlyBuild /tr C:\\\\ci\\\\build.cmd /sc daily\"},\"timeoutSec\":60," +
                "\"assessment\":{\"level\":\"high\",\"headline\":\"Высокий риск: прочитайте перед разрешением\",\"reasons\":[\"Создаёт задачу планировщика, которая запустится позже, возможно многократно.\"],\"escalated\":\"rule\",\"grantable\":true}}");
            RenderCard(high, "approval-high-ru");
            var legacy = CardFixture("{\"id\":\"apr_4\",\"tool\":\"write_file\",\"summary\":{\"target\":\"C:\\\\x\\\\a.txt\"}}");
            Check(legacy.Assessment == null && ApprovalToastManager.ComputeCardHeight(legacy) == ApprovalToastManager.ToolCardHeight, "an older server without assessment keeps the classic card");

            // Agent message links: the tray re-validates instead of trusting the server.
            Check(MessageLink.TryCreate("url", "https://example.com/report", false) is { Kind: "url" }, "https link accepted");
            Check(MessageLink.TryCreate("url", "file:///C:/Windows/notepad.exe", false) == null && MessageLink.TryCreate("url", "javascript:alert(1)", false) == null, "non-http schemes rejected");
            Check(MessageLink.TryCreate("file", @"\\server\share\a.txt", false) == null && MessageLink.TryCreate("file", @"relative\a.txt", false) == null, "UNC and relative paths rejected");
            Check(MessageLink.TryCreate("file", @"C:\Tools\setup.exe", false) is { Reveal: true } && MessageLink.TryCreate("file", @"C:\out\run.CMD", false) is { Reveal: true }, "programs and scripts are only revealed even if the server said otherwise");
            Check(MessageLink.TryCreate("file", @"C:\out\report.html", false) is { Reveal: false, ButtonText: "Открыть" } && MessageLink.TryCreate("folder", @"C:\out", false)!.ButtonText == "Открыть папку", "documents open, folders open in Explorer");
            string missingFile = Path.Combine(Path.GetTempPath(), "zeroide-missing-" + Guid.NewGuid().ToString("N") + ".txt");
            string missingDir = Path.Combine(Path.GetTempPath(), "zeroide-missing-dir-" + Guid.NewGuid().ToString("N"));
            Check(!new MessageLink("file", missingFile, false).Open(), "a link to a vanished file reports failure instead of throwing");
            Check(!DesktopLauncher.OpenDocument(missingFile) && !DesktopLauncher.OpenExistingFolder(missingDir) && !new MessageLink("folder", missingDir, false).Open(),
                "document and existing-folder launchers reject missing targets safely");
        }
        finally { I18n.CurrentLanguage = before; }
    }
}
