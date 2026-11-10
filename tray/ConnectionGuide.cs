using System.Text.Json;
using System.Windows.Forms;
using ZeroIDE.Mcp.ConnectionsV2;

namespace ZeroIDE.Mcp;

internal enum ConnectionTone { Good, Info, Waiting, Warning, Error, Neutral }
internal sealed record ConnectionStep(string Text, string State, bool Owner);
internal sealed record ConnectionAction(string Id, string Label, bool Primary = false);
internal sealed record ConnectionStatusView(string Key, ConnectionTone Tone, string Title, string Detail, ConnectionAction[] Actions, ConnectionStep[] Steps);

internal sealed partial class TrayContext
{
    private ConnectionStep[] _guideSteps = [];
    private string _guideTask = "", _guideTitle = "", _guideWaiting = "";
    private DateTimeOffset _guideUpdated;
    private string _launchApprovedOperation = "", _connectionCheckResult = "";
    private bool? _clientSeenPreview = null;
    private DateTimeOffset _clientSeenReadAt, _clientSeenAt;
    private string _clientSeenUrl = "";
    internal bool ConnectionIsPublic => UniversalRuntime ? _connections!.Active.Scope == AccessScope.Public : ConnectionMode != "local";
    internal string ConnectionProviderName => UniversalRuntime
        ? _connections!.Active.Adapter is "external" or "local" or "" ? _connections.Active.Name : _connections.Active.Adapter
        : ConnectionMode == "ssh" ? I18n.T("your service", "свой сервис") : ConnectionMode == "cloudflare" ? "Cloudflare" : ConnectionMode;
    internal string ConnectionKindTitle => !ConnectionIsPublic
        ? UniversalRuntime && _connections!.Active.Scope == AccessScope.Private ? I18n.T("Private network", "В частной сети") : I18n.T("Only this computer", "Только этот компьютер")
        : I18n.T("Over the internet · ", "Через интернет · ") + ConnectionProviderName;
    internal string ConnectionKindDescription => !ConnectionIsPublic
        ? UniversalRuntime && _connections!.Active.Scope == AccessScope.Private ? I18n.T("AI must be connected to the same private network.", "AI должен находиться в той же частной сети.") : I18n.T("For AI apps on this PC. An AI in a browser cannot use this address.", "Для AI-приложений на этом ПК. Для AI в браузере этот адрес не подойдёт.")
        : (UniversalRuntime && _connections!.Active.Ownership == ConnectionOwnership.External
            ? I18n.T("An external service provides the address; this app does not start or stop it.", "Адрес обслуживает внешний сервис; приложение не запускает и не выключает его.")
            : I18n.T("The app starts and stops ", "Приложение запускает и выключает ") + ConnectionProviderName + I18n.T(" together with MCP.", " вместе с MCP."))
          + (UniversalRuntime && _connections!.Active.AddressKind == "temporary" ? I18n.T(" The address may change after a restart.", " После перезапуска адрес может измениться.") : "");
    private string GuideNotePath => Path.Combine(Path.GetDirectoryName(_cfgPath)!, "connection-note.txt");
    internal string ConnectionGuideNote
    {
        get { try { var f = new FileInfo(GuideNotePath); return f.Exists && f.Length <= 4096 ? File.ReadAllText(f.FullName) : ""; } catch { return ""; } }
    }
    private bool GuideActive => !string.IsNullOrEmpty(_guideTask);
    // Token timestamps also change during local checks: never use them as client proof.
    private bool ClientSeen()
    {
        if (_clientSeenPreview.HasValue) return _clientSeenPreview.Value;
        if (_clientSeenUrl == ConnectionUrl && DateTimeOffset.UtcNow - _clientSeenReadAt < TimeSpan.FromSeconds(5)) return _clientSeenAt != default;
        _clientSeenUrl = ConnectionUrl; _clientSeenReadAt = DateTimeOffset.UtcNow; _clientSeenAt = default;
        try
        {
            string file = Path.Combine(Path.GetDirectoryName(_cfgPath)!, "connection-client.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 8192) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(file)); var r = doc.RootElement;
            var url = new Uri(ConnectionUrl);
            // A proxy prefix is not inferable from the backend path: no false success.
            if (url.AbsolutePath != r.GetProperty("path").GetString() || !url.Authority.Equals(r.GetProperty("host").GetString(), StringComparison.OrdinalIgnoreCase)) return false;
            var at = DateTimeOffset.Parse(r.GetProperty("at").GetString()!);
            var since = _connections?.History.FirstOrDefault()?.ReplacedAt ?? DateTimeOffset.MinValue;
            if (at >= since) _clientSeenAt = at;
        }
        catch { }
        return _clientSeenAt != default;
    }
    internal ConnectionStatusView ConnectionStatus()
    {
        ConnectionAction A(string id, string en, string ru, bool primary = false) => new(id, I18n.T(en, ru), primary);
        ConnectionStatusView V(string key, ConnectionTone tone, string en, string ru, string detail, params ConnectionAction[] actions) => new(key, tone, I18n.T(en, ru), detail, actions, []);
        if (!string.IsNullOrEmpty(_connectionsError)) return V("registry-error", ConnectionTone.Error, "Connection settings need attention", "Нужно восстановить настройки подключения", _connectionsError, A("agent_fix", "Ask agent to fix", "Поручить агенту", true));
        if (!McpEnabled) return V("off", ConnectionTone.Neutral, "MCP is off — AI cannot reach this PC", "MCP выключен — AI сейчас не достанет до ПК", I18n.T("Your address is saved. Enable MCP to reconnect.", "Адрес сохранён. Включи MCP, чтобы снова подключиться."), A("power_on", "Enable MCP", "Включить MCP", true));
        if (_state == "starting") return V("server-starting", ConnectionTone.Info, "MCP is starting…", "MCP запускается…", I18n.T("Wait for the server to start before connecting AI.", "Дождись запуска сервера, затем подключай AI."));
        if (GuideActive)
        {
            bool stale = DateTimeOffset.UtcNow - _guideUpdated > TimeSpan.FromMinutes(10);
            return new("guide", stale ? ConnectionTone.Warning : string.IsNullOrEmpty(_guideWaiting) ? ConnectionTone.Info : ConnectionTone.Waiting,
                stale ? I18n.T("No update from the agent yet", "От агента давно нет обновлений") : !string.IsNullOrEmpty(_guideWaiting) ? I18n.T("Your action is needed", "Нужно твоё действие") : I18n.T("The agent is setting up your connection", "Агент настраивает подключение"),
                (_guideTitle ?? "") + (!string.IsNullOrEmpty(_guideWaiting) ? "\n" + _guideWaiting : "") + (stale ? I18n.T("\nCheck the agent's chat. Your current address is unchanged.", "\nОткрой чат агента. Текущий адрес не изменён.") : ""),
                [A("guide_hide", "Hide progress", "Скрыть прогресс")], _guideSteps ?? []);
        }
        if (_connections?.Pending != null) return _connectionProof != null
            ? V("pending-ready", ConnectionTone.Waiting, "The new address is checked — ready to switch", "Новая ссылка проверена — можно переключить", I18n.T("Update your AI's URL after switching. Your current connection is still working.\n", "После переключения обнови ссылку в AI. Прежнее подключение пока работает.\n") + _connections.Pending.Profile.Endpoint, A("activate", "Switch", "Переключить", true), A("cancel", "Cancel", "Отменить"))
            : V("pending", ConnectionTone.Info, "A new connection is being prepared", "Готовится новое подключение", I18n.T("Your current address stays active while the agent checks the new one.", "Прежняя ссылка работает, пока агент проверяет новую."), A("cancel", "Cancel change", "Отменить изменение"));
        if (HasPreviousConnection) return V("handover", ConnectionTone.Waiting, "Update the URL in your AI", "Обнови ссылку в своём AI", I18n.T("After your AI connects using the new URL, stop the old connection. You can still restore it.", "Когда AI подключится по новой ссылке, отключи прежнее подключение. Пока его можно вернуть."), A("retire", "Stop previous", "Отключить прежнее", true), A("restore", "Restore previous", "Вернуть прежнее"));
        if (string.IsNullOrWhiteSpace(ConnectionUrl)) return V("not-configured", ConnectionTone.Info, "Choose which AI to connect", "Выбери, какой AI подключить", I18n.T("There is no internet address yet. Copy a task for your agent; it will recommend and set up a suitable connection.", "Адреса в интернете пока нет. Скопируй задание агенту — он предложит подходящий вариант и настроит подключение."), A("agent_internet", "Ask your agent", "Поручить агенту", true));
        if (!ConnectionIsPublic) return V("local", ConnectionTone.Info, "AI can connect locally", "AI может подключиться локально", ConnectionKindDescription, A("agent_internet", "Connect an AI in a browser", "Подключить AI в браузере", true));
        if (NetworkState == "error") return V("network-error", ConnectionTone.Error, "The internet connection is not working", "Подключение через интернет не работает", I18n.T("Your address is saved. Restart the service or ask your agent to fix it.", "Адрес сохранён. Перезапусти сервис или поручи агенту починить связь."), CanRestartConnection ? new("restart", I18n.T("Restart ", "Перезапустить ") + ConnectionProviderName, true) : A("check", "Check again", "Проверить снова", true), A("agent_fix", "Ask agent to fix", "Поручить агенту"));
        if (NetworkState is "starting" or "connecting") return V("starting", ConnectionTone.Info, "Starting the connection…", "Подключение запускается…", I18n.T("Your saved address will be available when the service starts.", "Сохранённая ссылка станет доступна после запуска сервиса."));
        if (_connectionCheckResult == "fail") return V("check-failed", ConnectionTone.Warning, "The address check failed", "Проверка ссылки не пройдена", ConnectionCheck, A("check", "Check again", "Проверить снова", true), A("agent_fix", "Ask agent to fix", "Поручить агенту"));
        if (ClientSeen()) return V("working", ConnectionTone.Good, "A real AI client has connected", "AI-клиент уже входил", I18n.T("A client signed in using this address. Connected clients and revocation are in Access.", "Клиент вошёл через этот адрес. Кто подключён и как отключить — в разделе «Доступ».") + (_clientSeenAt == default ? "" : " · " + _clientSeenAt.ToLocalTime().ToString("dd.MM HH:mm")));
        return V("ready", ConnectionTone.Info, "The address is ready — connect your AI", "Ссылка готова — осталось подключить AI", I18n.T("Paste the URL into your AI client and approve its access on this PC.", "Добавь ссылку в свой AI-клиент и разреши его вход карточкой на этом ПК.") + (ConnectionProviderName.Contains("ngrok", StringComparison.OrdinalIgnoreCase) ? I18n.T(" On ngrok's warning page, click Visit Site.", " На странице предупреждения ngrok нажми Visit Site.") : "") + (_connectionCheckResult == "ok" ? I18n.T(" So far, only checked from this PC.", " Пока проверено только с этого ПК.") : ""), A("copy", "Copy URL", "Скопировать ссылку", true));
    }
    internal string BuildAgentTask(string goal)
    {
        string target = goal switch { "url" => I18n.T("Change my MCP URL", "Поменяй ссылку MCP"), "type" => I18n.T("Change my connection type", "Поменяй тип подключения"), "fix" => I18n.T("Fix my AI connection to this PC", "Почини подключение AI к этому ПК"), "local" => I18n.T("Allow AI only on this PC", "Оставь доступ AI только с этого компьютера"), _ => I18n.T("Connect my AI client to this PC", "Подключи мой AI-клиент к этому ПК") };
        return target + ".\n" + I18n.T("Current type: ", "Текущий тип: ") + ConnectionKindTitle + "\nURL: " + ConnectionUrl + "\n" + ConnectionCheck + "\n\n" + I18n.T(
            "Read docs/AGENT_CONNECTIONS.md in the ZeroIDE MCP project and use tools/Configure-Connection.ps1. First ask which AI client I use; give one recommendation and explain what I must do. Keep the working connection until the replacement is verified and I approve switching on this PC. Never restart or disconnect the active connection without consent. Provider keys belong in a masked window on the PC via secret-request, never chat, commands or logs. Report progress via /connections/progress; leave a short /connections/note. Finish by connecting the actual AI client: a local MCP check is not proof of client success.",
            "Прочитай docs/AGENT_CONNECTIONS.md в проекте ZeroIDE MCP и используй tools/Configure-Connection.ps1. Сначала уточни, каким AI я пользуюсь; предложи один понятный вариант и объясни, что потребуется от меня. Сохрани рабочее подключение, пока новое не проверено и я не разрешу переключение на ПК. Без согласия не перезапускай и не отключай действующее подключение. Ключи провайдера — только в скрытое поле окна на ПК через secret-request, не в чат, команды или журнал. Показывай шаги через /connections/progress; оставь короткую /connections/note. Заверши подключением реального AI-клиента: проверка MCP с этого ПК ещё не означает успех клиента.");
    }
    internal void CopyConnectionAgentTask(string goal) => Clipboard.SetText(BuildAgentTask(goal));
    internal void HideConnectionProgress() { _guideTask = ""; _guideSteps = []; _guideTitle = _guideWaiting = ""; }
    internal async Task ActivatePendingFromUi()
    {
        var c = EnsureConnectionRegistry(); if (c.Pending == null) return;
        await HandleConnectionApi("POST", "/connections/activate", JsonSerializer.Serialize(new { revision = c.Revision, operationId = c.Pending.OperationId }));
    }
}
