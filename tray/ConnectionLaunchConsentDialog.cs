using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using ZeroIDE.Mcp.ConnectionsV2;
namespace ZeroIDE.Mcp;

internal sealed class ConnectionLaunchConsentDialog : ConnectionDialogBase
{
    public ConnectionLaunchConsentDialog(ConnectionProfile profile) : base(I18n.T("Allow this connection?", "Разрешить это подключение?"), 760)
    {
        bool internet = profile.Scope == AccessScope.Public;
        Explanation(internet ? I18n.T("AI in a browser or on another computer will be able to control this PC and change files through this address.", "Через этот адрес AI в браузере или на другом компьютере сможет управлять этим ПК и менять файлы.") : I18n.T("Allow AI to connect to this PC using the selected connection?", "Разрешить AI подключаться к этому ПК выбранным способом?"), 66);
        Explanation(I18n.T("Access stays closed without authorization. Only an AI with an MCP token or a sign-in card you approve can enter. Sensitive actions require confirmation; actions are journaled. Exiting the tray disconnects AI from this PC.", "Без авторизации доступ закрыт. Войти может только AI с токеном MCP или карточкой входа, которую ты разрешишь. Опасные действия подтверждаются; действия пишутся в журнал. Выход из трея отключает AI от ПК."), 100);
        Explanation(profile.Name + (profile.Endpoint.Length > 0 ? "\n" + profile.Endpoint : ""), 54);
        if (profile.Launch != null)
        {
            Explanation(I18n.T("This program will start and stop with MCP. For the first internet connection, Allow also permits making the checked address active. Replacing an existing internet address requires another confirmation.", "Эта программа будет запускаться и выключаться вместе с MCP. При первом выходе в интернет «Разрешить» также разрешает сделать проверенную ссылку основной. Для замены действующей ссылки будет отдельное подтверждение."), 88);
            Body.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Width = 590, Height = 150,
                BackColor = Color.FromArgb(28, 32, 41), ForeColor = ForeColor, Font = Theme.FontMono(9.5f), Text = JsonSerializer.Serialize(profile.Launch, new JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\r\n") });
            Explanation(I18n.T("Program or configuration changes require fresh permission. Provider keys must stay in a protected file, not in command arguments.", "Изменение программы или конфига потребует нового разрешения. Ключи провайдера хранятся в защищённом файле, не в аргументах команды."), 60);
        }
        else Explanation(I18n.T("This makes the checked address active. The external service is not started or stopped by this app.", "Проверенная ссылка станет основной. Приложение не запускает и не останавливает внешний сервис."), 62);
        var row = Actions();
        var reject = new ConnectionDialogButton(I18n.T("Do not allow", "Не разрешать"), () => { DialogResult = DialogResult.No; Close(); }); row.Controls.Add(reject);
        row.Controls.Add(new ConnectionDialogButton(I18n.T("Allow", "Разрешить"), () => { DialogResult = DialogResult.Yes; Close(); }, true));
        CancelButton = reject; ActiveControl = reject;
    }
}
