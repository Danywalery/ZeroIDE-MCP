using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;
internal sealed partial class TrayContext
{
    private static string GuideText(JsonElement root, string name, int limit, bool required = false)
    {
        string value = root.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        if (value.Length > limit || required && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Invalid guide text.");
        string clean = new string(value.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (required && clean.Length == 0) throw new ArgumentException("Empty guide text.");
        return clean;
    }
    private object HandleConnectionGuide(string path, JsonElement root)
    {
        if (path == "/connections/progress")
        {
            string task = GuideText(root, "taskId", 64, true);
            if (GuideActive && _guideTask != task) throw new InvalidOperationException("Another guide task is active.");
            if (root.TryGetProperty("clear", out var clear) && clear.GetBoolean()) { HideConnectionProgress(); return new { cleared = true }; }
            var steps = new List<ConnectionStep>();
            if (root.TryGetProperty("steps", out var array)) foreach (var step in array.EnumerateArray())
            {
                if (steps.Count == 8) throw new ArgumentException("Too many steps.");
                string state = GuideText(step, "state", 12, true);
                if (state is not ("done" or "current" or "todo")) throw new ArgumentException("Invalid step state.");
                steps.Add(new(GuideText(step, "text", 90, true), state, step.TryGetProperty("owner", out var owner) && owner.GetBoolean()));
            }
            string title = GuideText(root, "title", 80), waiting = GuideText(root, "waitingFor", 200);
            _guideTask = task; _guideSteps = steps.ToArray(); _guideTitle = title; _guideWaiting = waiting; _guideUpdated = DateTimeOffset.UtcNow;
            return new { updated = true, taskId = task };
        }
        if (path == "/connections/note")
        {
            string note = GuideText(root, "text", 600); TunnelProfiles.WritePrivate(GuideNotePath, note); return new { stored = true };
        }
        string kind = GuideText(root, "kind", 40, true);
        if (kind is not ("ngrok-authtoken" or "provider-token")) throw new ArgumentException("Unsupported secret kind.");
        string name = kind == "ngrok-authtoken" ? "ngrok" : GuideText(root, "name", 32, true);
        if (!Regex.IsMatch(name, "^[a-z0-9-]{1,32}$")) throw new ArgumentException("Invalid provider name.");
        int port = root.TryGetProperty("apiPort", out var p) ? p.GetInt32() : _cfg.NgrokApiPort + 1;
        if (port is < 1024 or > 65535) throw new ArgumentException("Invalid API port.");
        using var dialog = new ConnectionSecretDialog(name, GuideText(root, "reason", 200));
        if (dialog.ShowDialog() != DialogResult.OK) return new { stored = false, kind };
        string secret = dialog.Secret;
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096 || secret.Any(char.IsControl)) throw new ArgumentException("Invalid secret.");
        // Unique candidate files never overwrite the working provider's configuration.
        string id = Guid.NewGuid().ToString("N")[..12];
        string file = kind == "ngrok-authtoken" ? Path.Combine(TunnelProfiles.Home, "ngrok", "candidate-" + id + ".yml") : Path.Combine(TunnelProfiles.Home, "secrets", name + "-" + id + ".token");
        string content = kind == "ngrok-authtoken" ? "version: '2'\nauthtoken: '" + secret.Replace("'", "''") + "'\nweb_addr: 127.0.0.1:" + port + "\n" : secret;
        TunnelProfiles.WritePrivate(file, content);
        return new { stored = true, kind, configurationFile = file };
    }
}
internal sealed class ConnectionSecretDialog : ConnectionDialogBase
{
    private readonly TextBox _secret;
    internal string Secret => _secret.Text;
    public ConnectionSecretDialog(string provider, string reason) : base(I18n.T("Provider key", "Ключ провайдера"), 460)
    {
        Explanation(provider == "ngrok" ? I18n.T("ngrok key — this is NOT an MCP password", "Ключ ngrok — это НЕ пароль от MCP") : provider + I18n.T(" account key", " · ключ аккаунта"), 40);
        Explanation(I18n.T("Paste the key from your provider account. It will be saved in a protected local file. Only the file path is returned to the agent.", "Вставь ключ из аккаунта провайдера. Он сохранится в защищённом файле на ПК. Агент получит только путь к файлу."), 74);
        if (reason.Length > 0) Explanation(reason, 60);
        _secret = Field(I18n.T("Key", "Ключ"), ""); _secret.UseSystemPasswordChar = true; _secret.MaxLength = 4096;
        var row = Actions();
        row.Controls.Add(new ConnectionDialogButton(I18n.T("Save key", "Сохранить ключ"), () => { if (!string.IsNullOrWhiteSpace(_secret.Text)) { DialogResult = DialogResult.OK; Close(); } }, true));
        var cancel = new ConnectionDialogButton(I18n.T("Cancel", "Отмена"), () => { DialogResult = DialogResult.Cancel; Close(); }); row.Controls.Add(cancel); CancelButton = cancel;
    }
    protected override void Dispose(bool disposing) { if (disposing) _secret.Clear(); base.Dispose(disposing); }
}
