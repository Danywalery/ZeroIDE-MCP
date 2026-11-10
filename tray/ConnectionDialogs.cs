using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using ZeroIDE.Mcp.ConnectionsV2;

namespace ZeroIDE.Mcp;

internal sealed class ConnectionDialogButton : Button
{
    private readonly bool _primary;
    public ConnectionDialogButton(string text, Action action, bool primary = false)
    {
        Text = text; _primary = primary; AutoSize = false; Height = 38; Width = 215;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 10, 10); AccessibleName = text; Click += (_, _) => action();
    }
    protected override void OnPaint(PaintEventArgs e) => Theme.DrawButton(e.Graphics, new RectangleF(1, 1, Width - 2, Height - 2), Text,
        _primary ? ButtonKind.Primary : ButtonKind.Secondary, Enabled ? Focused || ClientRectangle.Contains(PointToClient(Cursor.Position)) ? ButtonState.Hover : ButtonState.Normal : ButtonState.Disabled, Theme.Font(10, true));
}
internal class ConnectionDialogBase : Form
{
    protected readonly FlowLayoutPanel Body;
    protected ConnectionDialogBase(string title, int height = 520)
    {
        Text = title; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(650, height); MinimumSize = new Size(590, 430);
        BackColor = Color.FromArgb(20, 23, 29); ForeColor = Color.FromArgb(240, 242, 248); Font = Theme.Font(10);
        ShowInTaskbar = false; MaximizeBox = false; MinimizeBox = false;
        Body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
        Controls.Add(Body);
    }
    protected void Copy(string text) => Clipboard.SetText(text);
    protected Label Explanation(string text, int height = 56)
    {
        var label = new Label { Text = text, Width = 590, Height = height, ForeColor = Color.FromArgb(180, 189, 207), Margin = new Padding(0, 0, 0, 12) };
        Body.Controls.Add(label); return label;
    }
    protected TextBox Field(string label, string text, bool readOnly = false)
    {
        Explanation(label, 24);
        var input = new TextBox { Text = text, Width = 590, ReadOnly = readOnly, BackColor = Color.FromArgb(32, 36, 47), ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 18), Font = Theme.Font(11) };
        Body.Controls.Add(input); return input;
    }
    protected FlowLayoutPanel Actions()
    {
        var row = new FlowLayoutPanel { Width = 590, Height = 96, WrapContents = true, Margin = new Padding(0, 8, 0, 8) };
        Body.Controls.Add(row); return row;
    }
}
internal sealed class ConnectionAddressDialog : ConnectionDialogBase
{
    private readonly TextBox _url;
    public string Endpoint => _url.Text.Trim();
    public string ConnectionName => I18n.T("External connection", "Внешнее подключение");
    public ConnectionAddressDialog(string endpoint) : base(I18n.T("Change connection URL", "Изменить ссылку подключения"), 440)
    {
        Explanation(I18n.T("Paste a new MCP URL. It will be checked before saving, while the old connection keeps working. Need help getting a URL? Copy a task for your agent.", "Вставь новую ссылку MCP. Она будет проверена до сохранения, пока прежнее подключение работает. Нужна помощь с получением ссылки? Скопируй задание агенту."), 72);

        _url = Field(I18n.T("Full MCP URL", "Полная ссылка MCP"), endpoint);
        var buttons = Actions();
        buttons.Controls.Add(new ConnectionDialogButton(I18n.T("Check URL", "Проверить ссылку"), () => { DialogResult = DialogResult.OK; Close(); }, true));
        buttons.Controls.Add(new ConnectionDialogButton(I18n.T("Ask your agent", "Поручить агенту"), () => { DialogResult = DialogResult.Retry; Close(); }));
        buttons.Controls.Add(new ConnectionDialogButton(I18n.T("Cancel", "Отмена"), Close));
    }
}
internal sealed class ConnectionControlDialog : ConnectionDialogBase
{
    public ConnectionControlDialog(TrayContext context) : base(I18n.T("Connection control", "Управление подключением"), 650)
    {
        Explanation(I18n.T("The agent handles setup. Here you control the address, launch and handover. Any HTTPS MCP provider can be used; a provider name never grants execution permission.", "Настройку выполняет агент. Здесь ты управляешь адресом, запуском и переключением. Подойдёт любой провайдер HTTPS MCP; название провайдера само по себе не разрешает запуск программ."), 80);
        Field(I18n.T("Current URL", "Текущая ссылка"), context.ConnectionUrl, true);
        Explanation(context.ConnectionControl, 52);
        var actions = Actions(); actions.Height = 150;
        actions.Controls.Add(new ConnectionDialogButton(I18n.T("Change URL", "Изменить ссылку"), async () => { await context.ChangeConnectionAddressFromUi(this); Close(); }, true));
        actions.Controls.Add(new ConnectionDialogButton(I18n.T("Import agent settings", "Настройки от агента"), async () => {
            using var picker = new OpenFileDialog { Filter = "Connection settings (*.json)|*.json", CheckFileExists = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            if (new FileInfo(picker.FileName).Length > 65536) { MessageBox.Show("Profile exceeds 64 KiB."); return; }
            try { await context.ConfigureProfileFromUi(JsonSerializer.Deserialize<ConnectionProfile>(File.ReadAllText(picker.FileName), new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })!); Close(); }
            catch { MessageBox.Show(I18n.T("Invalid connection settings.", "Некорректные настройки подключения.")); }
        }));
        actions.Controls.Add(new ConnectionDialogButton(I18n.T("Local connection", "Только этот компьютер"), async () => { await context.ConfigureProfileFromUi(new ConnectionProfile { Id = "local", Name = I18n.T("On this PC", "На этом ПК"), Scope = AccessScope.Local, Ownership = ConnectionOwnership.Local, Adapter = "local", Endpoint = $"http://127.0.0.1:{context.Config.Port}/mcp" }); Close(); }));
        if (!context.UsesUniversalConnection) actions.Controls.Add(new ConnectionDialogButton(I18n.T("Provider settings", "Настройки провайдера"), () => context.ConfigureConnectionFromUi(this)));
        actions.Controls.Add(new ConnectionDialogButton(I18n.T("Cancel / restore previous", "Отменить / вернуть прежнее"), () => { context.RestoreConnectionFromUi(); Close(); }));
        if (context.HasPreviousConnection) actions.Controls.Add(new ConnectionDialogButton(I18n.T("Stop previous connection", "Отключить прежнее"), () => { context.RetirePreviousConnectionFromUi(); Close(); }));
        Explanation(I18n.T("To change the connection type, ask your agent to prepare a new connection and send its settings to the app. Starting a program requires your approval; external services are independently controlled.\n\nChanging the address also requires updating it in your AI client. OAuth may need reconnection after a hostname or path change.", "Для смены способа попроси агента подготовить новое подключение и передать настройки приложению. Запуск программы требует твоего разрешения; внешним сервисом приложение не управляет.\n\nПосле смены адреса обнови его в AI-клиенте. При изменении домена или пути может понадобиться повторный вход OAuth."), 145);
        Body.Controls.Add(new ConnectionDialogButton(I18n.T("Done", "Готово"), Close));
    }
}
internal sealed partial class TrayContext
{
    internal bool UsesUniversalConnection => UniversalRuntime;
    internal async Task ConfigureProfileFromUi(ConnectionProfile profile)
    {
        var c = EnsureConnectionRegistry();
        (int code, string body) result = await HandleConnectionApi("POST", "/connections/prepare", JsonSerializer.Serialize(new { revision = c.Revision, profile }));
        if (result.code != 200) { MessageBox.Show(I18n.T("Cancel the pending change or reload the registry first.", "Сначала отмени незавершённое изменение или проверь реестр.")); return; }
        string operation = _connections!.Pending!.OperationId;
        foreach (string step in profile.Ownership == ConnectionOwnership.Managed ? new[] { "start", "check", "activate" } : new[] { "check", "activate" })
        {
            result = await HandleConnectionApi("POST", "/connections/" + step, JsonSerializer.Serialize(new { revision = _connections.Revision, operationId = operation }));
            if (result.code != 200) { MessageBox.Show(I18n.T("The change is pending; your current connection was kept. Check the new service, then retry or cancel.", "Изменение не завершено; прежнее подключение сохранено. Проверь новый сервис, затем повтори проверку или отмени изменение.")); break; }
        }
    }
}
