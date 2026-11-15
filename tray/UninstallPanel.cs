using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

internal sealed partial class SettingsForm
{
    private float DrawUninstallContent(Graphics g, float x, float w, float y)
    {
        float inner = w - 40;
        float Card(string title, string[] lines, Color tone)
        {
            float titleH = 28, bodyH = 0;
            foreach (var line in lines) bodyH += Math.Max(24, ConnectionMeasure(g, "•  " + line, UiSmall, inner - 8).Height + 6);
            float h = 20 + titleH + bodyH + 14;
            DrawCardSurface(g, new RectangleF(x, y, w, h));
            Theme.StrokeRound(g, new RectangleF(x, y, w, h), 16, Theme.WithAlpha(tone, 90), 1);
            ConnectionText(g, title, UiBodyStrong, Theme.TextVariant(tone), x + 20, y + 14, inner);
            float cy = y + 14 + titleH;
            foreach (var line in lines) cy += ConnectionText(g, "•  " + line, UiSmall, ColTextPrimary, x + 24, cy, inner - 8);
            return h;
        }
        y = DrawGroupTitle(g, x, y, I18n.T("WHAT WILL BE REMOVED", "ЧТО БУДЕТ УДАЛЕНО"));
        y += Card(I18n.T("Everything this app put on the PC", "Всё, что приложение положило на этот ПК"), [
            I18n.T("The installation folder: ", "Папка установки: ") + AppContext.BaseDirectory.TrimEnd('\\') + I18n.T(" (the app, the MCP server, portable Node.js, downloaded ngrok and cloudflared)", " (приложение, MCP-сервер, portable Node.js, скачанные ngrok и cloudflared)"),
            I18n.T("All local data inside it: the MCP token, access tokens and OAuth clients, connection settings, provider keys, permissions, logs, the audit journal and the screenshot archive", "Все локальные данные в ней: токен MCP, токены доступа и OAuth-клиенты, настройки подключений, ключи провайдеров, права, журналы, аудит и архив скриншотов"),
            I18n.T("The Start Menu shortcut and the “run with Windows” entry", "Ярлык в меню «Пуск» и запись «Запускать вместе с Windows»"),
            I18n.T("Running connections (ngrok, cloudflared, Tailscale Serve) are stopped; the MCP server is switched off", "Работающие подключения (ngrok, cloudflared, Tailscale Serve) останавливаются; MCP-сервер выключается")], Theme.Danger) + 16;
        y = DrawGroupTitle(g, x, y, I18n.T("WHAT STAYS", "ЧТО ОСТАНЕТСЯ"));
        y += Card(I18n.T("Things outside this app", "То, что живёт вне приложения"), [
            I18n.T("Your files and project folders: the app never deletes them", "Твои файлы и папки проектов: приложение их не трогает"),
            I18n.T("Your accounts at ngrok, Cloudflare and Tailscale, and the tunnel or hostname created there: remove them in those services if you no longer need them", "Твои аккаунты ngrok, Cloudflare и Tailscale и созданные там туннели или hostname: удали их в этих сервисах, если они больше не нужны"),
            I18n.T("The Tailscale app, if it was installed: remove it from Windows Settings → Apps", "Приложение Tailscale, если оно устанавливалось: удаляется через Параметры Windows → Приложения"),
            I18n.T("The MCP server entry in your AI client: delete it there", "Запись MCP-сервера в твоём AI-клиенте: удали её там"),
            string.IsNullOrWhiteSpace(_ctx.Config.ScreenshotsDir) ? I18n.T("Nothing else", "Больше ничего") : I18n.T("The custom screenshot folder you chose: ", "Выбранная тобой папка скриншотов: ") + _ctx.Config.ScreenshotsDir], Theme.Info) + 20;
        float h2 = 92;
        DrawCardSurface(g, new RectangleF(x, y, w, h2));
        ConnectionText(g, I18n.T("Remove ZeroIDE MCP from this PC", "Удалить ZeroIDE MCP с этого ПК"), UiBodyStrong, ColTextPrimary, x + 20, y + 16, inner - 260);
        ConnectionText(g, I18n.T("Asks for confirmation, then closes the app and deletes the folder. This cannot be undone.", "Спросит подтверждение, затем закроет приложение и удалит папку. Отменить нельзя."), UiSmall, ColTextSecondary, x + 20, y + 44, inner - 260);
        DrawPillButtonRight(g, x + w - 20f, y + 28f, I18n.T("Uninstall", "Удалить"), ColRed, Color.White, "uninstall_run", () => _ctx.UninstallFromUi(this));
        return y + h2 + 16;
    }
}

internal sealed partial class TrayContext
{
    /// <summary>Full removal: stops everything, removes autostart and the shortcut, exits, and a detached PowerShell deletes the folder once this process is gone.</summary>
    public void UninstallFromUi(IWin32Window owner)
    {
        string dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string question = I18n.T("Remove ZeroIDE MCP and all its local data from this PC?\n\nFolder: ", "Удалить ZeroIDE MCP и все его локальные данные с этого ПК?\n\nПапка: ") + dir
            + I18n.T("\n\nTokens, connections, logs and screenshots in this folder are deleted. Your own files are not touched.", "\n\nТокены, подключения, журналы и скриншоты в этой папке будут удалены. Твои файлы не затрагиваются.");
        if (MessageBox.Show(owner, question, Theme.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try { SetAutostart(false); } catch { }
        try
        {
            string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "ZeroIDE MCP.lnk");
            if (File.Exists(shortcut)) File.Delete(shortcut);
        }
        catch { }
        try { StopAll("uninstall"); } catch { }
        try
        {
            string quoted = dir.Replace("'", "''");
            string script = "Wait-Process -Id " + Environment.ProcessId + " -ErrorAction SilentlyContinue; "
                + "foreach ($n in 1..10) { Start-Sleep -Seconds 1; try { Remove-Item -LiteralPath '" + quoted + "' -Recurse -Force -ErrorAction Stop; break } catch { } }";
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + script + "\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
        }
        catch (Exception ex) { Log("uninstall scheduler error: " + ex.Message); }
        Log("uninstall requested; exiting");
        ExitTray();
    }
}
