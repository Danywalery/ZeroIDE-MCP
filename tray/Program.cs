// ZeroIDE MCP — tray supervisor for the ZeroIDE MCP server (Windows).
//
// The tray does NOT serve MCP itself. It starts/stops two child processes:
//   1. node.exe running the ZeroIDE MCP server (loopback only)
//   2. ssh.exe holding a reverse tunnel to the user's VPS (the front door)
// Exit from the tray kills both process trees: the PC is then fully offline
// as far as the agent is concerned. Autostart is OFF unless the user
// enables it in the menu.
//
// Build: see docs/BUILDING.md.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

internal sealed class TrayConfig
{
    public string ServerDir { get; set; } = "";
    public string NodeExe { get; set; } = "node.exe";
    public int Port { get; set; } = 39011;
    public int ControlPort { get; set; } = 39012;
    public string ControlSecret { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public string LogDir { get; set; } = "";
    // Missing ConnectionMode in older configs preserves the existing SSH profile.
    public string ConnectionMode { get; set; } = "ssh";
    public string NgrokUrl { get; set; } = "";
    public string NgrokExePath { get; set; } = "";
    public string NgrokConfigPath { get; set; } = "";
    public int NgrokApiPort { get; set; } = 4041;
    public string CloudflareUrl { get; set; } = "";
    public string CloudflareExePath { get; set; } = "";
    public string CloudflareConfigPath { get; set; } = "";
    public string CloudflareTunnelId { get; set; } = "";
    public string CloudflareTokenFile { get; set; } = "";
    public string SshPublicUrl { get; set; } = "";
    public bool TunnelEnabled { get; set; } = true;
    public string SshTarget { get; set; } = "";          // user@vps-host
    public string SshKeyPath { get; set; } = "";         // private key for the tunnel
    public int RemotePort { get; set; } = 39003;         // port bound on the VPS loopback
    public int IdleMinutes { get; set; } = 0;            // 0 = never auto-stop on idle
    public bool NotificationsEnabled { get; set; } = true; // real-time command notifications
    public bool ShowTaskStatus { get; set; } = true; // tray menu toggle "Show task status"
    public bool SaveScreenshots { get; set; } = false; // archive approved screenshots (off by default)
    public string ScreenshotsDir { get; set; } = ""; // empty = %LOCALAPPDATA%\ZeroIDE-MCP\screenshots
    public bool BlinkIconEnabled { get; set; } = true;
    public int DefaultGrantMinutes { get; set; } = 10;
    public string DefaultGrantScope { get; set; } = "tool";
    public string CardStyle { get; set; } = "glass";     // glass | light | compact | neon
    public string TrayMenuMode { get; set; } = "classic"; // classic | panel | mini
    public string ApprovalSound { get; set; } = "chime";  // chime | ping | bell | none
    public string Language { get; set; } = "en";         // en | ru
}

/// <summary>Live mirror of the screenshot-archive settings for tray-side
/// readers (control-server route, viewer). Refreshed on every SaveConfig
/// and at startup, so readers never parse tray-config.json themselves.</summary>
internal static class ScreenshotArchiveConfig
{
    public static bool Enabled;
    public static string Dir = "";
    public static void Refresh(TrayConfig cfg)
    {
        Enabled = cfg.SaveScreenshots;
        Dir = string.IsNullOrWhiteSpace(cfg.ScreenshotsDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroIDE-MCP", "screenshots")
            : cfg.ScreenshotsDir;
    }
}

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    // KILL_ON_JOB_CLOSE is valid with ExtendedLimitInformation (9), not Basic (2).
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    private static extern bool CloseKillJobHandle(IntPtr handle);
    private const int JobObjectExtendedLimitInformation = 9;

    // Kill-on-close job: if this tray process dies (kill, crash), Windows
    // closes the job handle and kills every process in the job — the server
    // (and its children) cannot outlive the supervisor. The server-side tray
    // heartbeat is the fallback if job assignment fails. The handle is held
    // for the tray lifetime and never closed explicitly (the OS closes it
    // at process death, which is exactly the guarantee).
    internal static bool TryAssignToKillJob(Process proc, ref IntPtr jobHandle)
    {
        try
        {
            if (jobHandle == IntPtr.Zero)
            {
                jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (jobHandle == IntPtr.Zero) return false;
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
                if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    CloseKillJobHandle(jobHandle); jobHandle = IntPtr.Zero;
                    return false;
                }
            }
            return AssignProcessToJobObject(jobHandle, proc.Handle);
        }
        catch { return false; }
    }

    [STAThread]
    private static void Main()
    {
        string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroIDE-MCP", "logs", "tray.log");
        using var mutex = new Mutex(true, "ZeroIDE-MCP.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            try { File.AppendAllText(logFile, $"{DateTime.Now:s}  SingleInstance mutex already held\r\n"); } catch { }
            return;
        }

        var uiThread = new Thread(() =>
        {
            try
            {
                try
                {
                    IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                    if (hDesk != IntPtr.Zero) SetThreadDesktop(hDesk);
                }
                catch { }

                ApplicationConfiguration.Initialize();
                Application.Run(new TrayContext());
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(logFile, $"{DateTime.Now:s}  FATAL uiThread error: {ex}\r\n"); } catch { }
            }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();
    }
}

internal sealed partial class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private ContextMenuStrip _classicMenu;
    private readonly TrayConfig _cfg;
    private readonly string _cfgPath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer _blinkTimer = new() { Interval = 220 };

    private readonly Icon _iconOff = Theme.MakeTrayIcon(Theme.TextTertiary);
    private readonly Icon _iconStarting = Theme.MakeTrayIcon(Theme.Warning);
    private readonly Icon _iconError = Theme.MakeTrayIcon(Theme.Danger);
    private readonly Icon _iconOn = Theme.MakeTrayIcon(Theme.Success);
    private readonly Icon _iconBlink = Theme.MakeTrayIcon(Theme.Info, ring: true);
    private readonly Icon _iconApproval = Theme.MakeTrayIcon(Theme.Warning, ring: true);

    private Process? _server;
    private IntPtr _serverJob = IntPtr.Zero; // kill-on-close job for the server (held for tray lifetime)
    private Process? _tunnel;
    private IntPtr _tunnelJob = IntPtr.Zero;
    private ApprovalControlServer? _controlServer;
    private SettingsForm? _settingsForm;
    private TrayPopupForm? _popupForm;
    private MissedRequestsForm? _missedForm;

    private int _tunnelRestarts;
    private DateTime _tunnelLastStart = DateTime.MinValue;
    private int _serverCrashTimes;
    private DateTime? _deadline;          // timed session end
    private DateTime? _lastActivity;      // last MCP activity from /local/status
    private long _lastActivitySeq = -1;
    private long _lastEventId;
    private McpToolEvent? _lastEvent;
    private int _activeCalls;
    private int _activeGrantsCount;
    private int _missedCount;
    private bool _confinementEnabled;
    private string _confinementRoot = "";
    private DateTime _blinkUntil = DateTime.MinValue;
    private bool _blinkPhase;
    private string _state = "off";        // off | starting | on | error
    private string _stateText = "Off";

    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _missedItem = new() { Text = "Missed: 0", Visible = false };
    private readonly ToolStripMenuItem _confinementItem = new() { Text = "📁 Single folder", Visible = false };
    private readonly ToolStripMenuItem _grantsItem = new() { Text = "Active grants: 0" };
    private readonly ToolStripMenuItem _notifyItem = new() { Text = I18n.T("Tray notifications", "Уведомления в трее") };
    private readonly ToolStripMenuItem _taskStatusItem = new() { Text = I18n.T("Show task status", "Показывать статус задачи") };
    private readonly ToolStripMenuItem _autostartItem = new() { Text = I18n.T("Tray autostart at Windows logon", "Автозапуск tray при входе в Windows") };

    public TrayConfig Config => _cfg;
    public string CurrentState => _state;
    public string CurrentStateText => _stateText;
    public DateTime? SessionDeadline => _deadline;
    public int ActiveGrantsCount => _activeGrantsCount;
    public int MissedCount => _missedCount;
    public bool ConfinementEnabled => _confinementEnabled;
    public string ConfinementRoot => _confinementRoot;
    public McpToolEvent? LastEvent => _lastEvent;

    public TrayContext()
    {
        _cfgPath = Path.Combine(AppContext.BaseDirectory, "tray-config.json");
        _cfg = LoadConfig();
        ScreenshotArchiveConfig.Refresh(_cfg);
        I18n.CurrentLanguage = _cfg.Language;
        EnsureControlSecretAndPermissions();
        LoadConnectionRegistry();

        _classicMenu = BuildMenu();
        _icon = new NotifyIcon
        {
            Icon = _iconOff,
            Visible = true,
            Text = Theme.ProductName + ": off",
            ContextMenuStrip = _cfg.TrayMenuMode == "classic" ? _classicMenu : null,
        };
        _icon.DoubleClick += (_, _) => OpenSettingsWindow("tools");
        _icon.MouseUp += OnTrayIconMouseUp;

        var uiCtx = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _controlServer = new ApprovalControlServer(
            _cfg.ControlPort > 0 ? _cfg.ControlPort : 39012,
            () => _cfg.ControlSecret,
            uiCtx,
            (req, onShown) => HandleApprovalRequestFromUiAsync(req, onShown, isDemo: false),
            Log,
            (text, kind, persistent, id) => uiCtx.Post(_ => SetUserMessage(text, kind, persistent, id), null),
            () => GetDebugStateJson(),
            (title, current, done, next, startedAt, agent) => uiCtx.Post(_ => SetTaskStatus(title, current, done, next, startedAt, agent), null),
            (method, path, body) => {
                var result = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
                uiCtx.Post(async _ => { try { result.SetResult(await HandleConnectionApi(method, path, body)); } catch { result.TrySetResult((500, "{}")); } }, null);
                return result.Task;
            });
        _controlServer.Start();

        ApprovalToastManager.PendingChanged += () =>
        {
            if (ApprovalToastManager.PendingCount > 0)
            {
                TriggerBlink();
            }
            UpdateStatusText();
        };

        _blinkTimer.Tick += (_, _) => OnBlinkTick();
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        Log("tray v0.2.1 started (server off)");
    }

    private void OnTrayIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (_cfg.TrayMenuMode == "classic") return;
        if (e.Button is MouseButtons.Left or MouseButtons.Right)
        {
            ShowTrayPopup(_cfg.TrayMenuMode);
        }
    }

    private string _userMessageText = "";
    private string _userMessageKind = "status";
    private string _userMessageId = "";

    private readonly List<McpToolEvent> _recentEvents = new();
    private string _taskTitle = "";
    private string _taskCurrent = "";
    private List<string> _taskDone = new();
    private List<string> _taskNext = new();
    private string _taskAgent = "";
    private DateTime? _taskStartedAt;
    private DateTime _taskUpdatedAt = DateTime.MinValue;

    internal string UserMessageText => _userMessageText;
    internal string UserMessageKind => _userMessageKind;
    internal IReadOnlyList<McpToolEvent> RecentEvents => _recentEvents;
    internal string TaskTitle => _taskTitle;
    internal string TaskCurrent => _taskCurrent;
    internal IReadOnlyList<string> TaskDone => _taskDone;
    internal IReadOnlyList<string> TaskNext => _taskNext;
    internal string TaskAgent => _taskAgent;
    internal DateTime? TaskStartedAt => _taskStartedAt;
    internal DateTime TaskUpdatedAt => _taskUpdatedAt;
    internal bool ShowTaskStatus => _cfg.ShowTaskStatus;

    internal void SetUserMessage(string text, string kind, bool persistent, string id)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ClearUserMessage();
            return;
        }
        string clean = text.Trim();
        if (!persistent)
        {
            if (kind == "request")
                Balloon(I18n.T("Agent message", "Сообщение агента"), clean);
            return;
        }
        _userMessageText = clean;
        _userMessageKind = (kind == "request" || kind == "warning") ? kind : "status";
        _userMessageId = id ?? "";
        Log($"user message ({_userMessageKind}): {_userMessageText}");
        if (_userMessageKind == "request")
            Balloon(I18n.T("Agent message", "Сообщение агента"), _userMessageText);
        UpdateStatusText();
    }

    private string GetDebugStateJson()
    {
        try
        {
            string serverMjs = string.IsNullOrWhiteSpace(_cfg.ServerDir) ? "" : Path.Combine(_cfg.ServerDir, "server.mjs");
            return JsonSerializer.Serialize(new
            {
                state = _state,
                stateText = _stateText,
                serverPid = _server?.Id,
                serverExited = _server?.HasExited,
                serverDir = _cfg.ServerDir,
                serverMjsExists = !string.IsNullOrEmpty(serverMjs) && File.Exists(serverMjs),
                nodeExe = _cfg.NodeExe,
                nodeExists = !string.IsNullOrWhiteSpace(_cfg.NodeExe) && File.Exists(_cfg.NodeExe),
                tunnelPid = _tunnel?.Id,
                tunnelExited = _tunnel?.HasExited,
                statusItemText = _statusItem.Text,
                logDir = LogDirPath,
                taskTitle = _taskTitle,
                taskCurrent = _taskCurrent,
                taskAgent = _taskAgent,
                taskStartedAt = _taskStartedAt?.ToString("o"),
                taskUpdatedAt = _taskUpdatedAt.ToString("o"),
                recentEvents = _recentEvents.Select(e => new { e.Id, e.Tool, e.Ts, e.Status, detail = (e.Detail ?? "").Length > 80 ? (e.Detail ?? "")[..80] : (e.Detail ?? "") }).ToArray(),
                logFileExists = File.Exists(Path.Combine(LogDirPath, "tray.log")),
            });
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.ToString() }); }
    }

    internal void ClearUserMessage()
    {
        _userMessageText = "";
        _userMessageKind = "status";
        _userMessageId = "";
        UpdateStatusText();
    }

    internal void SetTaskStatus(string title, string current, string[] done, string[] next, string startedAt, string agent)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            ClearTaskStatus();
            return;
        }
        _taskTitle = title.Trim();
        _taskCurrent = (current ?? "").Trim();
        _taskDone = (done ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(10).ToList();
        _taskNext = (next ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(10).ToList();
        _taskAgent = (agent ?? "").Trim();
        DateTime? parsed = null;
        if (DateTime.TryParse(startedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var st))
            parsed = st.Kind == DateTimeKind.Utc ? st.ToLocalTime() : st;
        _taskStartedAt = parsed;
        _taskUpdatedAt = DateTime.Now;
        Log($"task status: {_taskTitle} — {_taskCurrent}");
        UpdateStatusText();
    }

    internal void ClearTaskStatus()
    {
        _taskTitle = "";
        _taskCurrent = "";
        _taskDone = new List<string>();
        _taskNext = new List<string>();
        _taskAgent = "";
        _taskStartedAt = null;
        UpdateStatusText();
    }

    public void ShowTrayPopup(string mode)
    {
        try
        {
            if (_popupForm != null && !_popupForm.IsDisposed)
            {
                _popupForm.Close();
            }
            _popupForm = new TrayPopupForm(this, mode);
            _popupForm.Show();
            _popupForm.Activate();
        }
        catch (Exception ex)
        {
            Log("popup error: " + ex.Message);
        }
    }

    public void ApplyLanguage(string lang)
    {
        _cfg.Language = string.IsNullOrWhiteSpace(lang) ? "en" : lang.Trim().ToLowerInvariant();
        I18n.CurrentLanguage = _cfg.Language;
        SaveConfig();
        _classicMenu = BuildMenu();
        if (_cfg.TrayMenuMode == "classic")
        {
            _icon.ContextMenuStrip = _classicMenu;
        }
        UpdateStatusText();
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.Invalidate();
        }
    }

    public void ApplyTrayMenuMode(string mode)
    {
        _cfg.TrayMenuMode = string.IsNullOrWhiteSpace(mode) ? "classic" : mode.ToLowerInvariant();
        SaveConfig();
        _icon.ContextMenuStrip = _cfg.TrayMenuMode == "classic" ? _classicMenu : null;
    }

    public void ApplyCardStyle(string style)
    {
        _cfg.CardStyle = string.IsNullOrWhiteSpace(style) ? "glass" : style.ToLowerInvariant();
        SaveConfig();
        ApprovalToastManager.UpdateAllCardsStyle(_cfg.CardStyle);
        TrayToastManager.UpdateAllCardsStyle(_cfg.CardStyle);
    }

    // ------------------------------------------------------------ config

    private TrayConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_cfgPath))
            {
                var cfg = JsonSerializer.Deserialize<TrayConfig>(File.ReadAllText(_cfgPath));
                if (cfg != null) return cfg;
            }
        }
        catch (Exception e) { Log("config read error: " + e.Message); }
        return new TrayConfig();
    }

    private void SaveConfig()
    {
        ScreenshotArchiveConfig.Refresh(_cfg);
        try
        {
            string tmp = _cfgPath + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _cfgPath, overwrite: true);
        }
        catch (Exception e) { Log("config write error: " + e.Message); }
    }

    private void EnsureControlSecretAndPermissions()
    {
        try
        {
            // Ensure permissions.json exists and initialize confinement state
            var initPerms = PermissionsStore.Load();
            _confinementEnabled = initPerms.Confinement?.Enabled == true && !string.IsNullOrWhiteSpace(initPerms.Confinement.Root);
            _confinementRoot = _confinementEnabled ? (initPerms.Confinement!.Root ?? "") : "";

            // Ensure ProjectDir has a sensible fallback if empty
            if (string.IsNullOrWhiteSpace(_cfg.ProjectDir))
            {
                _cfg.ProjectDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (_cfg.ControlPort <= 0) _cfg.ControlPort = 39012;
            if (_cfg.DefaultGrantMinutes <= 0) _cfg.DefaultGrantMinutes = 10;
            if (string.IsNullOrWhiteSpace(_cfg.DefaultGrantScope)) _cfg.DefaultGrantScope = "tool";

            string envPath = Path.Combine(
                string.IsNullOrWhiteSpace(_cfg.ServerDir) ? Path.Combine(AppContext.BaseDirectory, "server") : _cfg.ServerDir,
                "config.env");

            string envSecret = "";
            if (File.Exists(envPath))
            {
                foreach (var line in File.ReadAllLines(envPath))
                {
                    if (line.StartsWith("TRAY_CONTROL_SECRET=", StringComparison.Ordinal))
                        envSecret = line["TRAY_CONTROL_SECRET=".Length..].Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(envSecret))
            {
                _cfg.ControlSecret = envSecret;
            }
            else if (string.IsNullOrWhiteSpace(_cfg.ControlSecret))
            {
                byte[] bytes = new byte[24];
                System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
                _cfg.ControlSecret = Convert.ToHexString(bytes).ToLowerInvariant();
            }

            if (File.Exists(envPath) && string.IsNullOrWhiteSpace(envSecret))
            {
                File.AppendAllText(envPath,
                    $"{Environment.NewLine}TRAY_CONTROL_PORT={_cfg.ControlPort}{Environment.NewLine}TRAY_CONTROL_SECRET={_cfg.ControlSecret}{Environment.NewLine}");
            }

            SaveConfig();
        }
        catch (Exception e)
        {
            Log("init secret/permissions error: " + e.Message);
        }
    }

    private string LogDirPath => string.IsNullOrWhiteSpace(_cfg.LogDir)
        ? Path.Combine(AppContext.BaseDirectory, "logs") : _cfg.LogDir;

    private string ProjectDirPath => string.IsNullOrWhiteSpace(_cfg.ProjectDir)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _cfg.ProjectDir;

    public string GetLogDirPath() => LogDirPath;

    public void OpenLogsFolderFromUi()
    {
        Log("opening logs folder: " + LogDirPath);
        DesktopLauncher.OpenFolder(LogDirPath);
    }

    public void OpenProjectFolderFromUi()
    {
        Log("opening project folder: " + ProjectDirPath);
        DesktopLauncher.OpenFolder(ProjectDirPath);
    }

    private void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(LogDirPath);
            File.AppendAllText(Path.Combine(LogDirPath, "tray.log"),
                DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine);
        }
        catch { /* logging must never crash the tray */ }
    }

    // ------------------------------------------------------------ menu

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);

        _missedItem.Click += (_, _) => OpenMissedRequestsWindow();
        menu.Items.Add(_missedItem);

        _confinementItem.Click += (_, _) => OpenSettingsWindow("access");
        menu.Items.Add(_confinementItem);

        var revokeAllSub = new ToolStripMenuItem(I18n.T("Revoke all now", "Отозвать все сейчас"), null, async (_, _) => await RevokeGrantAsync(null, all: true));
        var openGrantsSub = new ToolStripMenuItem(I18n.T("Manage in settings…", "Управление в настройках…"), null, (_, _) => OpenSettingsWindow("approvals"));
        _grantsItem.DropDownItems.Add(revokeAllSub);
        _grantsItem.DropDownItems.Add(openGrantsSub);
        menu.Items.Add(_grantsItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(I18n.T("Enable", "Включить"), null, (_, _) => StartAll(null));
        menu.Items.Add(I18n.T("Enable for 30 minutes", "Включить на 30 минут"), null, (_, _) => StartAll(30));
        menu.Items.Add(I18n.T("Enable for 60 minutes", "Включить на 60 минут"), null, (_, _) => StartAll(60));
        menu.Items.Add(I18n.T("Disable", "Выключить"), null, (_, _) => StopAll("manual stop"));
        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem(I18n.T("Settings…", "Настройки…"), null, (_, _) => OpenSettingsWindow("general"))
        {
            Font = Theme.Font(9.75f, true),
        };
        menu.Items.Add(settingsItem);
        menu.Items.Add(I18n.T("Tools and permissions…", "Инструменты и права…"), null, (_, _) => OpenSettingsWindow("tools"));
        menu.Items.Add(I18n.T("Access (tokens and OAuth)…", "Доступ (токены и OAuth)…"), null, (_, _) => OpenSettingsWindow("access"));

        _notifyItem.CheckOnClick = true;
        _notifyItem.Checked = _cfg.NotificationsEnabled;
        _notifyItem.CheckedChanged += (_, _) =>
        {
            _cfg.NotificationsEnabled = _notifyItem.Checked;
            SaveConfig();
            if (!_cfg.NotificationsEnabled) TrayToastManager.DismissAll();
            Log("notifications " + (_cfg.NotificationsEnabled ? "enabled" : "disabled"));
        };
        menu.Items.Add(_notifyItem);
        _taskStatusItem.CheckOnClick = true;
        _taskStatusItem.Checked = _cfg.ShowTaskStatus;
        _taskStatusItem.CheckedChanged += (_, _) =>
        {
            _cfg.ShowTaskStatus = _taskStatusItem.Checked;
            SaveConfig();
            UpdateStatusText();
            Log("task status display " + (_cfg.ShowTaskStatus ? "enabled" : "disabled"));
        };
        menu.Items.Add(_taskStatusItem);

        _autostartItem.CheckOnClick = true;
        _autostartItem.Checked = GetAutostart();
        _autostartItem.CheckedChanged += (_, _) => SetAutostart(_autostartItem.Checked);
        menu.Items.Add(_autostartItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(I18n.T("Open project folder", "Открыть папку проекта"), null, (_, _) => OpenProjectFolderFromUi());
        menu.Items.Add(I18n.T("Open logs folder", "Открыть папку логов"), null, (_, _) => OpenLogsFolderFromUi());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(I18n.T("Exit", "Выход"), null, (_, _) => ExitTray());
        Theme.StyleMenu(menu);
        settingsItem.Font = Theme.BodyStrong;
        return menu;
    }

    public void OpenSettingsWindow(string section = "tools")
    {
        try
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new SettingsForm(this, section);
                _settingsForm.Show();
            }
            else
            {
                _settingsForm.NavigateToSection(section);
            }
        }
        catch (Exception ex)
        {
            Log("settings window error: " + ex.Message);
        }
    }

    public void OpenMissedRequestsWindow()
    {
        try
        {
            if (_missedForm == null || _missedForm.IsDisposed)
            {
                _missedForm = new MissedRequestsForm(this);
                _missedForm.Show();
            }
            else
            {
                if (_missedForm.WindowState == FormWindowState.Minimized)
                    _missedForm.WindowState = FormWindowState.Normal;
                _missedForm.Activate();
                _missedForm.BringToFront();
            }
        }
        catch (Exception ex)
        {
            Log("missed window error: " + ex.Message);
        }
    }

    public void RefreshConfinementFromPermissions()
    {
        try
        {
            var perms = PermissionsStore.Load();
            _confinementEnabled = perms.Confinement?.Enabled == true && !string.IsNullOrWhiteSpace(perms.Confinement.Root);
            _confinementRoot = _confinementEnabled ? (perms.Confinement!.Root ?? "") : "";
            UpdateStatusText();
        }
        catch { }
    }

    public Task<ApprovalResponseDto> HandleApprovalRequestFromUiAsync(ApprovalRequestDto req, Action<string>? onCardShown = null, bool isDemo = false)
    {
        Log($"approval requested [{req.Tool}] id={req.Id} note={req.Note}");
        TriggerBlink();
        return ApprovalToastManager.ShowApprovalAsync(
            req,
            _cfg.DefaultGrantMinutes,
            _cfg.DefaultGrantScope,
            _cfg.CardStyle,
            _cfg.ApprovalSound,
            (newMins, newScope) =>
            {
                _cfg.DefaultGrantMinutes = newMins;
                _cfg.DefaultGrantScope = newScope;
                SaveConfig();
            },
            onCardShown,
            isDemo);
    }

    public void ShowDemoCardsFromUi()
    {
        try
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TrayToastManager.Show(
                999001,
                "terminal",
                "Checks the process list",
                "Get-Process · run_command",
                DateTime.Now.ToString("HH:mm:ss"),
                "done",
                340,
                nowMs - 340,
                _cfg.CardStyle);

            var demoReq = new ApprovalRequestDto
            {
                Id = "demo-" + Guid.NewGuid().ToString("N")[..6],
                Tool = "run_command",
                TimeoutSec = 60,
                Note = "Approval card demo",
                Summary = new ApprovalSummaryDto
                {
                    Title = "PowerShell command",
                    Target = "Get-Date -Format 'yyyy-MM-dd HH:mm:ss'",
                    Detail = "cwd: C:\\Projects\\demo-project · timeout: 30s"
                }
            };
            _ = HandleApprovalRequestFromUiAsync(demoReq, null, isDemo: true);
        }
        catch (Exception ex)
        {
            Log("demo cards error: " + ex.Message);
        }
    }

    public void ExitFromUi() => ExitTray();

    public void RenderAllPreviewsToFiles(string outDir)
    {
        string savedState = _state;
        string savedStateText = _stateText;
        _state = "on";
        _stateText = "Online";
        if (_lastEvent == null)
        {
            _lastEvent = new McpToolEvent(1, DateTime.Now.ToString("o"), "run_command", "Get-Date", "Get-Date", "", "", "done", 320, null);
        }

        using (var hf = new SettingsForm(this, "home"))
        {
            hf.RenderPreviewToFile(Path.Combine(outDir, "settings-home-2026-10-24.png"), "home");
        }

        try
        {
            // 1. Settings sections
            using (var f = new SettingsForm(this, "tools"))
            {
                f.RenderPreviewToFile(Path.Combine(outDir, "settings-tools-2026-10-24.png"), "tools");

                var origTok = AccessStore.LoadTokens();
                var origOAuth = AccessStore.LoadOAuth();
                bool seededPreview = origTok.Tokens.Count == 0 && origOAuth.Families.Count == 0;
                if (seededPreview)
                {
                    AccessStore.SaveTokens(new TokensRegistry
                    {
                        Version = 1,
                        Tokens = new List<TokenEntry>
                        {
                            new()
                            {
                                Id = "tok_9f42a1b0",
                                Label = "Claude Code Desktop",
                                Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                                Scope = "full",
                                Root = @"C:\Projects\demo-project",
                                Source = "manual",
                                CreatedAt = DateTime.UtcNow.AddDays(-2).ToString("o"),
                                ExpiresAt = DateTime.UtcNow.AddDays(28).ToString("o"),
                                TtlDays = 30,
                                MaxRequests = 1000,
                                UsedRequests = 42,
                                LastUsedAt = DateTime.UtcNow.AddMinutes(-5).ToString("o"),
                                Revoked = false
                            },
                            new()
                            {
                                Id = "tok_3c81d4e2",
                                Label = "ChatGPT Web Connector",
                                Hash = "a1b2c3d4e5f60123456789abcdef0123456789abcdef0123456789abcdef0123",
                                Scope = "readwrite",
                                Root = null,
                                Source = "oauth",
                                OauthClientId = "client_chatgpt01",
                                FamilyId = "fam_77bc10a9f2",
                                CreatedAt = DateTime.UtcNow.AddMinutes(-15).ToString("o"),
                                ExpiresAt = DateTime.UtcNow.AddMinutes(45).ToString("o"),
                                UsedRequests = 8,
                                LastUsedAt = DateTime.UtcNow.AddMinutes(-1).ToString("o"),
                                Revoked = false
                            }
                        }
                    });
                    AccessStore.SaveOAuth(new OAuthStore
                    {
                        Version = 1,
                        Clients = new List<OAuthClientEntry>
                        {
                            new()
                            {
                                ClientId = "client_chatgpt01",
                                ClientName = "ChatGPT (OpenAI MCP)",
                                RedirectUris = new List<string> { "https://chatgpt.com/connector_platform_oauth_redirect" },
                                CreatedAt = DateTime.UtcNow.AddDays(-1).ToString("o")
                            }
                        },
                        Families = new List<OAuthFamilyEntry>
                        {
                            new()
                            {
                                Id = "fam_77bc10a9f2",
                                ClientId = "client_chatgpt01",
                                Scope = "readwrite",
                                OauthScope = "mcp.readwrite",
                                CreatedAt = DateTime.UtcNow.AddDays(-1).ToString("o"),
                                ExpiresAt = DateTime.UtcNow.AddDays(29).ToString("o"),
                                Revoked = false
                            }
                        }
                    });
                }

                try
                {
                    f.RenderPreviewToFile(Path.Combine(outDir, "settings-access-2026-10-24.png"), "access");
                }
                finally
                {
                    if (seededPreview)
                    {
                        AccessStore.SaveTokens(origTok);
                        AccessStore.SaveOAuth(origOAuth);
                    }
                }

                f.RenderPreviewToFile(Path.Combine(outDir, "settings-general-2026-10-24.png"), "general");
                f.RenderPreviewToFile(Path.Combine(outDir, "settings-approvals-2026-10-24.png"), "approvals");
                f.RenderPreviewToFile(Path.Combine(outDir, "settings-notifications-2026-10-24.png"), "notifications");
                f.RenderPreviewToFile(Path.Combine(outDir, "settings-about-2026-10-24.png"), "about");
            }

            // 1b. OAuth 2.1 Consent Cards (known site + unknown site with amber warning)
            var oauthKnownReq = new ApprovalRequestDto
            {
                Id = "consent_known",
                Kind = "oauth_consent",
                ClientName = "ChatGPT",
                ClientId = "client_chatgpt",
                RedirectHost = "chatgpt.com",
                Scope = "readwrite",
                ScopeHuman = "Read and write files (no PowerShell commands)",
                Risk = "known",
                Warning = null,
                TimeoutSec = 120,
                CreatedAt = DateTime.UtcNow.ToString("o")
            };
            using (var knownForm = new ApprovalToastForm(
                oauthKnownReq, 10, "tool", "glass", (_, _) => { },
                904, ApprovalToastManager.OAuthCardHeightKnown,
                0, 0, 0, _ => { }))
            using (var knownBmp = new Bitmap(904, ApprovalToastManager.OAuthCardHeightKnown, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var kg = Graphics.FromImage(knownBmp))
            {
                knownForm.RenderToGraphics(kg);
                knownBmp.Save(Path.Combine(outDir, "oauth-consent-known-2026-10-24.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            var oauthWarnReq = new ApprovalRequestDto
            {
                Id = "consent_warn",
                Kind = "oauth_consent",
                ClientName = "Random Service",
                ClientId = "client_random",
                RedirectHost = "random-service.example",
                Scope = "readwrite",
                ScopeHuman = "Read and write files (no PowerShell commands)",
                Risk = "unknown_site",
                Warning = "Site random-service.example is not on the list of known services (ChatGPT, Claude). It will get access to this PC within the scope: Read and write files (no PowerShell commands). Connect only trusted resources.",
                TimeoutSec = 120,
                CreatedAt = DateTime.UtcNow.ToString("o")
            };
            using (var warnForm = new ApprovalToastForm(
                oauthWarnReq, 10, "tool", "glass", (_, _) => { },
                904, ApprovalToastManager.OAuthCardHeightWarning,
                0, 0, 0, _ => { }))
            using (var warnBmp = new Bitmap(904, ApprovalToastManager.OAuthCardHeightWarning, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var wg = Graphics.FromImage(warnBmp))
            {
                warnForm.RenderToGraphics(wg);
                warnBmp.Save(Path.Combine(outDir, "oauth-consent-warning-2026-10-24.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            // 1c. Missed Requests Window Preview
            using (var missedWin = new MissedRequestsForm(this))
            {
                missedWin.RenderToBitmapFile(
                    Path.Combine(outDir, "missed-requests-2026-10-24.png"),
                    new List<MissedApprovalItem>
                    {
                        new(DateTime.UtcNow.AddMinutes(-2).ToString("o"), "apr_1", "run_command", "Get-Service | Where-Object Status -eq Running", "cwd: C:\\Projects\\demo-project", "Checking the list of running Windows services", "timeout", "claude-code"),
                        new(DateTime.UtcNow.AddMinutes(-8).ToString("o"), "apr_2", "write_file", "C:\\Projects\\demo-project\\output\\reports\\summary.md", "1420 bytes", "Saving the build report", "unavailable", "chatgpt-web"),
                    });
            }

            // 1d. Tray Panel with Badges (Missed: 2 + 📁 Single folder)
            int savedMissed = _missedCount;
            bool savedConf = _confinementEnabled;
            string savedConfRoot = _confinementRoot;
            _missedCount = 2;
            _confinementEnabled = true;
            _confinementRoot = @"C:\Projects\demo-project";
            using (var badgePanel = new TrayPopupForm(this, "panel"))
            {
                badgePanel.RenderToBitmapFile(Path.Combine(outDir, "tray-panel-badges-2026-10-24.png"));
            }
            _missedCount = savedMissed;
            _confinementEnabled = savedConf;
            _confinementRoot = savedConfRoot;

        // 2. 4 Card Styles (Informational Toast + Approval Card stacked cleanly)
        var styleKeys = new (string Key, string Label)[]
        {
            ("glass", "1. Glass"),
            ("light", "2. Apple Light"),
            ("compact", "3. Compact"),
            ("neon", "4. Neon"),
        };

        int styleW = Math.Max(TrayToastManager.CardWidth, ApprovalToastManager.CardWidth) + 44;
        int styleH = 34 + TrayToastManager.StandardCardHeight + 20 + ApprovalToastManager.ToolCardHeight + 20;
        using var gridBmp = new Bitmap(styleW * 2 + 60, (styleH + 20) * 2 + 36, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var gg = Graphics.FromImage(gridBmp))
        {
            gg.SmoothingMode = SmoothingMode.AntiAlias;
            gg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            gg.Clear(Color.FromArgb(18, 18, 22));

            for (int i = 0; i < styleKeys.Length; i++)
            {
                var (styleKey, label) = styleKeys[i];
                int col = i % 2;
                int row = i / 2;
                int cellX = 20 + col * (styleW + 20);
                int cellY = 18 + row * (styleH + 20);

                using var singleBmp = new Bitmap(styleW, styleH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var sg = Graphics.FromImage(singleBmp))
                {
                    sg.SmoothingMode = SmoothingMode.AntiAlias;
                    sg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    sg.Clear(Color.FromArgb(24, 24, 30));

                    var hdrFont = Theme.Font(10.0f, true);
                    using var hdrBrush = new SolidBrush(Color.FromArgb(215, 215, 225));
                    sg.DrawString(label, hdrFont, hdrBrush, new PointF(10f, 8f));

                    int toastH = styleKey == "compact" ? TrayToastManager.CompactCardHeight : TrayToastManager.StandardCardHeight;
                    using var toastForm = new TrayToastForm(
                        101,
                        "terminal",
                        "Checks the current date and time",
                        "Get-Date · run_command",
                        "04:45:12",
                        "done",
                        310,
                        null,
                        styleKey,
                        TrayToastManager.CardWidth,
                        toastH,
                        0,
                        0,
                        0);
                    using var toastBmp = new Bitmap(TrayToastManager.CardWidth, toastH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var tg = Graphics.FromImage(toastBmp))
                    {
                        toastForm.RenderToGraphics(tg);
                    }
                    sg.DrawImage(toastBmp, 12, 34 + (TrayToastManager.StandardCardHeight - toastH));

                    var demoReq = new ApprovalRequestDto
                    {
                        Id = "prev-" + styleKey,
                        Tool = "run_command",
                        TimeoutSec = 60,
                        Note = "Checking the current time and time zone in this session",
                        Summary = new ApprovalSummaryDto
                        {
                            Title = "PowerShell command",
                            Target = "Get-Date -Format 'yyyy-MM-dd HH:mm:ss'",
                            Detail = "cwd: C:\\Projects\\demo-project · timeout: 30s"
                        }
                    };
                    using var aprForm = new ApprovalToastForm(
                        demoReq,
                        10,
                        "tool",
                        styleKey,
                        (_, _) => { },
                        ApprovalToastManager.CardWidth,
                        ApprovalToastManager.ToolCardHeight,
                        0,
                        0,
                        0,
                        _ => { });
                    using var aprBmp = new Bitmap(ApprovalToastManager.CardWidth, ApprovalToastManager.ToolCardHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var ag = Graphics.FromImage(aprBmp))
                    {
                        aprForm.RenderToGraphics(ag);
                    }
                    sg.DrawImage(aprBmp, 22, 34 + TrayToastManager.StandardCardHeight + 20);
                }

                singleBmp.Save(Path.Combine(outDir, $"card-style-{styleKey}-2026-10-24.png"), System.Drawing.Imaging.ImageFormat.Png);
                gg.DrawImage(singleBmp, cellX, cellY);
            }
        }
        gridBmp.Save(Path.Combine(outDir, "card-styles-4-2026-10-24.png"), System.Drawing.Imaging.ImageFormat.Png);

        // 3. 3 Tray Menu Modes ("panel", "mini", "classic")
        if (_lastEvent == null)
        {
            _lastEvent = new McpToolEvent(1, DateTime.Now.ToString("o"), "run_command", "Get-Date", "Get-Date", "", "", "done", 320, null);
        }
        using (var panelForm = new TrayPopupForm(this, "panel"))
        {
            panelForm.RenderToBitmapFile(Path.Combine(outDir, "tray-mode-panel-2026-10-24.png"));
        }
        using (var miniForm = new TrayPopupForm(this, "mini"))
        {
            miniForm.RenderToBitmapFile(Path.Combine(outDir, "tray-mode-mini-2026-10-24.png"));
        }
        using (var classicBmp = new Bitmap(340, 290, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (var cg = Graphics.FromImage(classicBmp))
        {
            cg.SmoothingMode = SmoothingMode.AntiAlias;
            cg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            cg.Clear(Color.FromArgb(32, 32, 36));
            using var bPen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
            cg.DrawRectangle(bPen, 0, 0, 339, 289);
            var mFont = Theme.Font(9.75f);
            var bFont = Theme.Font(9.75f, true);
            using var wBrush = new SolidBrush(Color.FromArgb(240, 240, 245));
            using var gBrush = new SolidBrush(Color.FromArgb(150, 150, 158));
            using var sepPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f);

            string[] items = new[]
            {
                "Status: Online",
                "Active grants: 0",
                "---",
                "▶ Enable",
                "⏱ Enable for 30 minutes",
                "⏱ Enable for 1 hour",
                "⏹ Disable",
                "---",
                "⚙ Settings…",
                "☑ Show command notifications",
                "☑ Run with Windows",
                "📂 Open logs folder",
                "📁 Open project working folder",
                "---",
                "✖ Exit"
            };
            float iy = 10f;
            foreach (var it in items)
            {
                if (it == "---")
                {
                    cg.DrawLine(sepPen, 12f, iy + 4f, 328f, iy + 4f);
                    iy += 9f;
                }
                else
                {
                    var fnt = it.Contains("Settings") ? bFont : mFont;
                    var br = it.StartsWith("Status") || it.StartsWith("Active") ? gBrush : wBrush;
                    cg.DrawString(it, fnt, br, new PointF(16f, iy));
                    iy += 21f;
                }
            }
            classicBmp.Save(Path.Combine(outDir, "tray-mode-classic-2026-10-24.png"), System.Drawing.Imaging.ImageFormat.Png);
            // Screenshot viewer (sample image + sample description).
            using (var sample = new Bitmap(1440, 810))
            {
                using (var sg = Graphics.FromImage(sample))
                {
                    sg.Clear(Color.FromArgb(32, 36, 44));
                    using var vr1 = new SolidBrush(Color.FromArgb(58, 67, 86));
                    sg.FillRectangle(vr1, 60, 60, 600, 380);
                    using var vr2 = new SolidBrush(Color.FromArgb(74, 84, 104));
                    sg.FillRectangle(vr2, 700, 60, 680, 380);
                    using var vr3 = new SolidBrush(Color.FromArgb(46, 53, 66));
                    sg.FillRectangle(vr3, 60, 480, 1320, 220);
                    using var vtBrush = new SolidBrush(Color.FromArgb(230, 230, 235));
                    var vtFont = Theme.Font(20f, true); // Shared cached font — never dispose.
                    sg.DrawString("SAMPLE SCREENSHOT 1440×810", vtFont, vtBrush, new PointF(60f, 720f));
                }
                using var viewer = new ScreenshotViewerForm(sample, "Sample description: the model asked for this screenshot to check the new viewer layout, zoom controls and the info bar below the image.", DateTime.UtcNow.ToString("o"));
                using var vbmp = new Bitmap(viewer.ClientSize.Width, viewer.ClientSize.Height);
                viewer.DrawToBitmap(vbmp, new Rectangle(0, 0, vbmp.Width, vbmp.Height));
                vbmp.Save(Path.Combine(outDir, "viewer-2026-10-24.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        }
        finally
        {
            _state = savedState;
            _stateText = savedStateText;
        }
    }

    public async Task<List<ActiveGrantItem>> FetchActiveGrantsAsync()
    {
        var list = new List<ActiveGrantItem>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_cfg.Port}/local/grants");
            if (!string.IsNullOrEmpty(_cfg.ControlSecret))
                req.Headers.Add("X-ZeroIDE-Control", _cfg.ControlSecret);
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return list;
            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("grants", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in arr.EnumerateArray())
                {
                    string key = g.TryGetProperty("key", out var kEl) ? (kEl.GetString() ?? "") : "";
                    string tool = g.TryGetProperty("tool", out var tEl) ? (tEl.GetString() ?? "") : "";
                    string scope = g.TryGetProperty("scope", out var sEl) ? (sEl.GetString() ?? "tool") : "tool";
                    string target = g.TryGetProperty("target", out var tgEl) ? (tgEl.GetString() ?? "") : "";
                    int remSec = g.TryGetProperty("remainingSec", out var rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : 0;
                    list.Add(new ActiveGrantItem(key, tool, scope, target, remSec));
                }
            }
        }
        catch { }
        return list;
    }

    public async Task RevokeGrantAsync(string? key, bool all)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_cfg.Port}/local/grants/revoke");
            if (!string.IsNullOrEmpty(_cfg.ControlSecret))
                req.Headers.Add("X-ZeroIDE-Control", _cfg.ControlSecret);
            string body = JsonSerializer.Serialize(new { key, all });
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            Log($"grant revoke (all={all}, key={key}): {res.StatusCode}");
        }
        catch (Exception ex)
        {
            Log("grant revoke error: " + ex.Message);
        }
    }

    public async Task<List<MissedApprovalItem>> FetchMissedApprovalsAsync()
    {
        var list = new List<MissedApprovalItem>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_cfg.Port}/local/missed");
            if (!string.IsNullOrEmpty(_cfg.ControlSecret))
                req.Headers.Add("X-ZeroIDE-Control", _cfg.ControlSecret);
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return list;
            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("missed", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    string ts = m.TryGetProperty("ts", out var tsEl) ? (tsEl.GetString() ?? "") : "";
                    string kind = m.TryGetProperty("kind", out var kEl) ? (kEl.GetString() ?? "timeout")
                        : (m.TryGetProperty("reason", out var rEl) ? (rEl.GetString() ?? "timeout") : "timeout");
                    string id = m.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    string tool = m.TryGetProperty("tool", out var tEl) ? (tEl.GetString() ?? "") : "";
                    string note = m.TryGetProperty("note", out var nEl) ? (nEl.GetString() ?? "") : "";
                    string target = m.TryGetProperty("target", out var tgEl) ? (tgEl.GetString() ?? "") : "";
                    string detail = m.TryGetProperty("detail", out var dEl) ? (dEl.GetString() ?? "") : "";
                    string token = m.TryGetProperty("token", out var tokEl) ? (tokEl.GetString() ?? "") : "";
                    list.Add(new MissedApprovalItem(ts, id, tool, target, detail, note, kind, token));
                }
            }
        }
        catch { }
        return list;
    }

    public async Task ClearMissedApprovalsAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_cfg.Port}/local/missed/clear");
            if (!string.IsNullOrEmpty(_cfg.ControlSecret))
                req.Headers.Add("X-ZeroIDE-Control", _cfg.ControlSecret);
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            _missedCount = 0;
            UpdateStatusText();
            Log($"missed approvals cleared: {res.StatusCode}");
        }
        catch (Exception ex)
        {
            Log("missed clear error: " + ex.Message);
        }
    }

    public void StartServerFromUi(int? minutes) => StartAll(minutes);
    public void StopServerFromUi(string reason) => StopAll(reason);
    public void SaveConfigFromUi() => SaveConfig();
    public bool GetAutostartFromUi() => GetAutostart();
    public void SetAutostartFromUi(bool on) { _autostartItem.Checked = on; SetAutostart(on); }
    public void SetNotificationsEnabledFromUi(bool on) { _notifyItem.Checked = on; _cfg.NotificationsEnabled = on; SaveConfig(); }
    public void RestartTunnelFromUi()
    {
        if (UniversalRuntime) { if (McpEnabled && CanRestartConnection) { _activeConnectionProcess?.Dispose(); _activeConnectionProcess = null; StartUniversalConnection(); } return; }
        ResetConnectionState();
        _tunnelRestarts = 0;
        KillTree(_tunnel);
        _tunnel = null;
        if (_cfg.TunnelEnabled && (_state == "on" || _state == "starting"))
            StartTunnel();
    }

    // ------------------------------------------------------------ autostart

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private bool GetAutostart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("ZeroIDE-MCP") != null;
        }
        catch { return false; }
    }

    private void SetAutostart(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue("ZeroIDE-MCP", '"' + Application.ExecutablePath + '"');
            else key.DeleteValue("ZeroIDE-MCP", false);
            Log("autostart " + (on ? "enabled" : "disabled"));
        }
        catch (Exception e) { Log("autostart error: " + e.Message); }
    }

    // ------------------------------------------------------------ lifecycle

    private void StartAll(int? minutes)
    {
        if (_state == "on" || _state == "starting")
        {
            if (minutes.HasValue) { _deadline = DateTime.Now.AddMinutes(minutes.Value); Balloon(Theme.ProductName, I18n.T("Timer updated: shutdown at ", "Таймер обновлён: выключение в ") + _deadline.Value.ToString("HH:mm")); }
            return;
        }
        if (string.IsNullOrWhiteSpace(_cfg.ServerDir) || !File.Exists(Path.Combine(_cfg.ServerDir, "server.mjs")))
        {
            SetState("error", I18n.T("Server folder not found — check tray-config.json", "Не найдена папка сервера — проверь tray-config.json"));
            Balloon(Theme.ProductName, I18n.T("Error: server.mjs not found. Open tray settings.", "Ошибка: не найден server.mjs. Открой настройки tray."));
            return;
        }
        SetState("starting", I18n.T("Starting…", "Запуск…"));
        _deadline = minutes.HasValue ? DateTime.Now.AddMinutes(minutes.Value) : null;
        _serverCrashTimes = 0;
        StartServer();
        if (UniversalRuntime || _cfg.TunnelEnabled) StartTunnel();
        Log("started" + (minutes.HasValue ? $" (timed {minutes} min)" : ""));
    }

    // A stale server from a dead tray can keep our MCP port (its heartbeat is
    // answered by the new tray on the same control port), leaving the new
    // server to crash-loop on EADDRINUSE while the tray reports "on".
    // Before spawning, kill any node.exe listening on our port.
    private void ReclaimServerPort()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            if (p == null) return;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            int self = Process.GetCurrentProcess().Id;
            foreach (var line in outp.Split('\n'))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || parts[0] != "TCP" || parts[3] != "LISTENING") continue;
                if (!parts[1].EndsWith(":" + _cfg.Port)) continue;
                if (!int.TryParse(parts[4], out int pid) || pid == self) continue;
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (proc.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill();
                        Log("reclaimed MCP port " + _cfg.Port + " from stale node pid " + pid);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void StartServer()
    {
        ReclaimServerPort();
        var psi = new ProcessStartInfo
        {
            FileName = _cfg.NodeExe,
            Arguments = '"' + Path.Combine(_cfg.ServerDir, "server.mjs") + '"',
            WorkingDirectory = _cfg.ServerDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.Environment["PORT"] = _cfg.Port.ToString();
        psi.Environment["MCP_HOST"] = "127.0.0.1";
        psi.Environment["TRAY_CONTROL_PORT"] = (_cfg.ControlPort > 0 ? _cfg.ControlPort : 39012).ToString();
        psi.Environment["TRAY_CONTROL_SECRET"] = _cfg.ControlSecret;
        psi.Environment["MCP_PERMISSIONS_FILE"] = PermissionsStore.GetPermissionsPath();
        psi.Environment["MCP_LANG"] = I18n.IsRu ? "ru" : "en";
        if (!string.IsNullOrWhiteSpace(_cfg.ProjectDir)) psi.Environment["PROJECT_DIR"] = _cfg.ProjectDir;
        if (!string.IsNullOrWhiteSpace(_cfg.LogDir)) psi.Environment["MCP_LOG_DIR"] = _cfg.LogDir;
        psi.Environment["MCP_CONFIG_FILE"] = Path.Combine(_cfg.ServerDir, "config.env");
        try
        {
            _server = Process.Start(psi);
            if (_server != null)
            {
                _server.EnableRaisingEvents = true;
                _server.Exited += (_, _) => OnServerExited();
                if (!Program.TryAssignToKillJob(_server, ref _serverJob))
                    Log("server job: kill-on-close unavailable, tray heartbeat is the fallback");
            }
        }
        catch (Exception e)
        {
            SetState("error", I18n.T("Server failed to start: ", "Сервер не запустился: ") + e.Message);
            Log("server start error: " + e);
        }
    }

    private void StartSshTunnel()
    {
        if (string.IsNullOrWhiteSpace(_cfg.SshTarget))
        {
            Log("tunnel skipped: SshTarget is empty");
            return;
        }
        // Adopt an already-running tunnel with identical params (e.g. orphaned by
        // a dead tray) instead of spawning a duplicate that can never bind.
        var adopted = FindExistingTunnel();
        if (adopted != null)
        {
            _tunnel = adopted;
            _tunnelLastStart = DateTime.Now;
            _tunnel.EnableRaisingEvents = true;
            _tunnel.Exited += (_, _) => OnTunnelExited();
            Program.TryAssignToKillJob(_tunnel, ref _tunnelJob);
            Log($"tunnel adopted (already running, pid={adopted.Id})");
            return;
        }
        var args = "-N -T -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -o ServerAliveCountMax=3 " +
                   "-o StrictHostKeyChecking=accept-new -o BatchMode=yes ";
        if (!string.IsNullOrWhiteSpace(_cfg.SshKeyPath)) args += "-i \"" + _cfg.SshKeyPath + "\" ";
        args += $"-R 127.0.0.1:{_cfg.RemotePort}:127.0.0.1:{_cfg.Port} {_cfg.SshTarget}";
        var psi = new ProcessStartInfo
        {
            FileName = "ssh.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            _tunnel = Process.Start(psi);
            _tunnelLastStart = DateTime.Now;
            if (_tunnel != null)
            {
                _tunnel.EnableRaisingEvents = true;
                _tunnel.Exited += (_, _) => OnTunnelExited();
                if (!Program.TryAssignToKillJob(_tunnel, ref _tunnelJob))
                    Log("tunnel job: kill-on-close unavailable, adopt-on-start is the fallback");
            }
        }
        catch (Exception e) { Log("tunnel start error: " + e); }
    }

    private Process? FindExistingTunnel()
    {
        try
        {
            string forward = $"-R 127.0.0.1:{_cfg.RemotePort}:127.0.0.1:{_cfg.Port}";
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'ssh.exe'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string? cmd = mo["CommandLine"] as string;
                if (string.IsNullOrEmpty(cmd)) continue;
                if (!cmd.Contains(forward)) continue;
                if (!string.IsNullOrWhiteSpace(_cfg.SshTarget) && !cmd.Contains(_cfg.SshTarget)) continue;
                if (!string.IsNullOrWhiteSpace(_cfg.SshKeyPath) && !cmd.Contains(_cfg.SshKeyPath)) continue;
                if (!int.TryParse(mo["ProcessId"]?.ToString(), out int pid)) continue;
                try { return Process.GetProcessById(pid); } catch { continue; }
            }
        }
        catch (Exception ex) { Log("tunnel adopt scan failed: " + ex.Message); }
        return null;
    }

    private void OnServerExited()
    {
        if (_state != "on" && _state != "starting") return;
        _serverCrashTimes++;
        Log($"server exited unexpectedly (crashes={_serverCrashTimes})");
    }

    private void OnTunnelExited()
    {
        if (_state != "on" && _state != "starting") return;
        Log($"tunnel process exited (restarts={_tunnelRestarts})");
    }

    private void StopAll(string reason)
    {
        StopUniversalConnections();
        ResetConnectionState();
        _state = "off";
        _deadline = null;
        _lastActivity = null;
        _lastActivitySeq = -1;
        _lastEventId = 0;
        _activeCalls = 0;
        _activeGrantsCount = 0;
        _blinkUntil = DateTime.MinValue;
        _blinkTimer.Stop();
        _tunnelRestarts = 0;
        _serverCrashTimes = 0;

        ApprovalToastManager.DismissAll("user");

        KillTree(_tunnel);
        _tunnel = null;

        KillTree(_server);
        _server = null;

        SetState("off", I18n.T("Off", "Выключено"));
        Log("stopped: " + reason);
    }

    private void KillTree(Process? proc)
    {
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch
                {
                    using var tk = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/PID {proc.Id} /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                    tk?.WaitForExit(3000);
                }
            }
        }
        catch (Exception e) { Log("kill error: " + e.Message); }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------ polling & health

    private async Task PollCoreAsync()
    {
        if (string.IsNullOrEmpty(_statusItem.Text))
        {
            UpdateStatusText();
            var cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs.Contains("--start"))
            {
                StartAll(null);
            }
            else
            {
                Balloon(Theme.ProductName, I18n.T("Running in the tray (^ arrow near the clock). Right-click the icon for the menu.", "Запущен в трее (стрелка ^ возле часов). ПКМ по значку — меню."));
            }
            if (cmdArgs.Contains("--settings"))
            {
                OpenSettingsWindow("tools");
            }
            if (cmdArgs.Contains("--preview"))
            {
                try
                {
                    string outDir = PreviewOutput.Resolve(cmdArgs);
                    Directory.CreateDirectory(outDir);
                    Log("preview output: " + outDir);
                    RenderAllPreviewsToFiles(outDir);
                }
                catch (Exception ex) { Log("preview error: " + ex.Message); }
            }
        }
        if (_state != "on" && _state != "starting") return;

        if (_deadline.HasValue && DateTime.Now >= _deadline.Value)
        {
            Balloon(Theme.ProductName, I18n.T("Session time expired — server switched off.", "Время сессии истекло — сервер выключен."));
            StopAll("timer expired");
            return;
        }

        if (_cfg.IdleMinutes > 0 && _lastActivity.HasValue &&
            (DateTime.Now - _lastActivity.Value).TotalMinutes >= _cfg.IdleMinutes)
        {
            Balloon(Theme.ProductName, I18n.Format("Auto shutdown: no activity for {0} min.", "Автовыключение: нет активности {0} мин.", _cfg.IdleMinutes));
            StopAll("idle");
            return;
        }

        if (_server == null || _server.HasExited)
        {
            if (_serverCrashTimes <= 3)
            {
                Log($"restarting server (attempt {_serverCrashTimes})");
                StartServer();
            }
            else
            {
                StopAll("server crashed");
                SetState("error", "Error: server crashed");
                Balloon(Theme.ProductName, "Server stopped unexpectedly several times. Check the logs.");
                return;
            }
        }

        ConnectionTick();
        int statusEpoch = _networkEpoch;
        bool statusConfirmed = false;

        try
        {
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_cfg.Port}/local/status");
            if (!string.IsNullOrWhiteSpace(_cfg.ControlSecret))
                statusRequest.Headers.Add("X-ZeroIDE-Control", _cfg.ControlSecret);
            using var statusResponse = await _http.SendAsync(statusRequest);
            statusResponse.EnsureSuccessStatusCode();
            var json = await statusResponse.Content.ReadAsStringAsync();
            if (statusEpoch != _networkEpoch || !McpEnabled) return;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
            {
                statusConfirmed = true;
                if (doc.RootElement.TryGetProperty("lastActivityAt", out var actEl) &&
                    actEl.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(actEl.GetString(), out var parsedAct))
                {
                    _lastActivity = parsedAct.ToLocalTime();
                }
                else if (_lastActivity == null)
                {
                    _lastActivity = DateTime.Now;
                }

                if (doc.RootElement.TryGetProperty("activeGrantsCount", out var gcEl) && gcEl.ValueKind == JsonValueKind.Number)
                {
                    _activeGrantsCount = gcEl.GetInt32();
                }

                if (doc.RootElement.TryGetProperty("missedCount", out var mcEl) && mcEl.ValueKind == JsonValueKind.Number)
                {
                    _missedCount = mcEl.GetInt32();
                }

                if (doc.RootElement.TryGetProperty("confinement", out var cfEl) && cfEl.ValueKind == JsonValueKind.Object)
                {
                    _confinementEnabled = cfEl.TryGetProperty("enabled", out var cEn) && cEn.ValueKind == JsonValueKind.True;
                    _confinementRoot = cfEl.TryGetProperty("root", out var cRt) && cRt.ValueKind == JsonValueKind.String
                        ? (cRt.GetString() ?? "")
                        : "";
                }

                _serverCrashTimes = 0;
                if (_state != "on")
                {
                    SetState("on", "Online");
                    Balloon(Theme.ProductName, I18n.T("MCP server online: ", "MCP-сервер включён: ") + (PublicMcpUrl.Length > 0 ? PublicMcpUrl : $"http://127.0.0.1:{_cfg.Port}/mcp"));
                }
                else
                {
                    UpdateStatusText();
                }

                if (doc.RootElement.TryGetProperty("activeCalls", out var acEl) && acEl.ValueKind == JsonValueKind.Number)
                {
                    _activeCalls = acEl.GetInt32();
                }

                if (doc.RootElement.TryGetProperty("activitySeq", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number)
                {
                    long seq = seqEl.GetInt64();
                    if (_lastActivitySeq == -1)
                    {
                        _lastActivitySeq = seq;
                    }
                    else if (seq != _lastActivitySeq || _activeCalls > 0 || ApprovalToastManager.PendingCount > 0)
                    {
                        _lastActivitySeq = seq;
                        TriggerBlink();
                    }
                }
                else if (_activeCalls > 0 || ApprovalToastManager.PendingCount > 0)
                {
                    TriggerBlink();
                }

                if (doc.RootElement.TryGetProperty("recentEvents", out var evArr) && evArr.ValueKind == JsonValueKind.Array)
                {
                    var newEvents = new List<McpToolEvent>();
                    foreach (var item in evArr.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                        long id = idEl.GetInt64();
                        string tool = item.TryGetProperty("tool", out var tEl) && tEl.ValueKind == JsonValueKind.String ? (tEl.GetString() ?? "tool") : "tool";
                        string detail = item.TryGetProperty("detail", out var dEl) && dEl.ValueKind == JsonValueKind.String ? (dEl.GetString() ?? "") : "";
                        string note = item.TryGetProperty("note", out var nEl) && nEl.ValueKind == JsonValueKind.String ? (nEl.GetString() ?? "") : "";
                        string cmd = item.TryGetProperty("cmd", out var cEl) && cEl.ValueKind == JsonValueKind.String ? (cEl.GetString() ?? "") : "";
                        string path = item.TryGetProperty("path", out var pEl) && pEl.ValueKind == JsonValueKind.String ? (pEl.GetString() ?? "") : "";
                        string query = item.TryGetProperty("query", out var qEl) && qEl.ValueKind == JsonValueKind.String ? (qEl.GetString() ?? "") : "";
                        string status = item.TryGetProperty("status", out var sEl) && sEl.ValueKind == JsonValueKind.String ? (sEl.GetString() ?? "done") : "done";
                        int? durationMs = item.TryGetProperty("durationMs", out var durEl) && durEl.ValueKind == JsonValueKind.Number ? durEl.GetInt32() : null;
                        long? startedAtMs = item.TryGetProperty("startedAtMs", out var stEl) && stEl.ValueKind == JsonValueKind.Number ? stEl.GetInt64() : null;
                        string tsStr = item.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String ? (tsEl.GetString() ?? "") : "";

                        var curEv = new McpToolEvent(id, tsStr, tool, detail, cmd, path, query, status, durationMs, startedAtMs, note);
                        _lastEvent = curEv;

                        if (id <= _lastEventId)
                        {
                            if (status != "running")
                            {
                                TrayToastManager.UpdateEventStatus(id, status, durationMs);
                                // History entries are immutable records: refresh the stored
                                // copy too, or a call first seen mid-flight stays "running".
                                var idx = _recentEvents.FindIndex(e => e.Id == id);
                                if (idx >= 0) _recentEvents[idx] = _recentEvents[idx] with { Status = status, DurationMs = durationMs };
                            }
                            continue;
                        }

                        _lastEventId = id;
                        newEvents.Add(curEv);
                        _recentEvents.Add(curEv);
                        while (_recentEvents.Count > 30) _recentEvents.RemoveAt(0);
                        Log($"mcp tool [{tool}]: {detail}");
                    }

                    if (newEvents.Count > 0)
                    {
                        TriggerBlink();
                        if (_cfg.NotificationsEnabled)
                        {
                            ShowToolNotification(newEvents);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (statusEpoch != _networkEpoch || !McpEnabled) return;
            if ((DateTime.Now - _statusFailureAt).TotalSeconds > 30)
            {
                _statusFailureAt = DateTime.Now;
                Log("status poll " + (statusConfirmed ? "UI/event" : "HTTP/JSON") + " error: " + ex.GetType().Name + " " + ex.StackTrace);
            }
            if (statusConfirmed) return; // optional notifications must not turn a healthy server yellow
            if (_state == "on")
            {
                SetState("starting", "Starting…");
            }
            else
            {
                UpdateStatusText();
            }
        }
    }

    // ------------------------------------------------------------ UI helpers

    private void TriggerBlink()
    {
        if (_state != "on" || !_cfg.BlinkIconEnabled) return;
        _blinkUntil = DateTime.Now.AddSeconds(2.4);
        if (!_blinkTimer.Enabled)
        {
            _blinkPhase = true;
            _icon.Icon = ApprovalToastManager.PendingCount > 0 ? _iconApproval : _iconBlink;
            _blinkTimer.Start();
        }
    }

    private void OnBlinkTick()
    {
        if (_state != "on" || !_cfg.BlinkIconEnabled)
        {
            _blinkTimer.Stop();
            if (_state == "on") _icon.Icon = _iconOn;
            return;
        }
        if (DateTime.Now >= _blinkUntil && _activeCalls <= 0 && ApprovalToastManager.PendingCount <= 0)
        {
            _blinkTimer.Stop();
            _blinkPhase = false;
            _icon.Icon = _iconOn;
            return;
        }
        _blinkPhase = !_blinkPhase;
        Icon activeBlink = ApprovalToastManager.PendingCount > 0 ? _iconApproval : _iconBlink;
        _icon.Icon = _blinkPhase ? activeBlink : _iconOn;
    }

    private void ShowToolNotification(List<McpToolEvent> events)
    {
        try
        {
            foreach (var ev in events)
            {
                var (iconKind, headline, metaPrefix) = EventHumanizer.Format(ev);
                string timeStr = DateTime.TryParse(ev.Ts, out var dt)
                    ? dt.ToLocalTime().ToString("HH:mm:ss")
                    : DateTime.Now.ToString("HH:mm:ss");
                TrayToastManager.Show(ev.Id, iconKind, headline, metaPrefix, timeStr, ev.Status, ev.DurationMs, ev.StartedAtMs, _cfg.CardStyle, ev);
            }
        }
        catch { /* ignore */ }
    }

    private void SetState(string state, string text)
    {
        _state = state;
        _stateText = text;
        UpdateStatusText();

        if (state != "on")
        {
            _blinkTimer.Stop();
            _blinkPhase = false;
        }

        _icon.Icon = state switch
        {
            "on" => _iconOn,
            "starting" => _iconStarting,
            "error" => _iconError,
            _ => _iconOff,
        };
    }

    private void UpdateStatusText()
    {
        int pending = ApprovalToastManager.PendingCount;
        var pendingSuffix = pending > 0 ? I18n.Format(" [awaiting answer: {0}]", " [ждёт ответа: {0}]", pending) : "";
        var activeSuffix = _activeCalls > 0 ? I18n.Format(" [active: {0}]", " [активно: {0}]", _activeCalls) : "";
        var suffix = _deadline.HasValue ? I18n.Format(" (until {0:HH:mm})", " (до {0:HH:mm})", _deadline.Value) : "";
        _statusItem.Text = I18n.Format("Status: {0}{1}{2}{3}", "Статус: {0}{1}{2}{3}", _stateText, pendingSuffix, activeSuffix, suffix);
        _grantsItem.Text = I18n.Format("Active grants: {0}", "Активные разрешения: {0}", _activeGrantsCount);

        if (_missedCount > 0)
        {
            _missedItem.Text = I18n.Format("⏱ Missed: {0}", "⏱ Пропущено: {0}", _missedCount);
            _missedItem.Visible = true;
        }
        else
        {
            _missedItem.Visible = false;
        }

        if (_confinementEnabled && !string.IsNullOrWhiteSpace(_confinementRoot))
        {
            string leaf = EventHumanizer.GetLeafName(_confinementRoot);
            _confinementItem.Text = I18n.Format("📁 Single folder: {0} · terminal off", "📁 Одна папка: {0} · терминал откл.", (string.IsNullOrEmpty(leaf) ? _confinementRoot : leaf));
            _confinementItem.Visible = true;
        }
        else
        {
            _confinementItem.Visible = false;
        }

        var tip = $"{Theme.ProductName}: {_stateText}{pendingSuffix}{activeSuffix}{suffix}";
        if (!string.IsNullOrEmpty(_userMessageText))
            tip += $" · {_userMessageText}";
        if (_cfg.ShowTaskStatus && !string.IsNullOrEmpty(_taskTitle))
            tip += $" · Задача: {_taskTitle}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void Balloon(string title, string body)
    {
        UpdateStatusText();
        try
        {
            string kind = _state == "error" ? "error" : "power";
            TrayToastManager.Show(0, kind, body, title, DateTime.Now.ToString("HH:mm:ss"), _state == "error" ? "error" : "done", null, null, _cfg.CardStyle);
        }
        catch { /* ignore */ }
    }

    private static Icon MakeIcon(Color fillColor, Color borderColor) => Theme.MakeTrayIcon(fillColor);

    private void ExitTray()
    {
        _poll.Stop();
        _blinkTimer.Stop();
        _controlServer?.Dispose();
        ApprovalToastManager.DismissAll("user");
        TrayToastManager.DismissAll();
        try { _popupForm?.Close(); } catch { }
        try { _missedForm?.Close(); } catch { }
        try { _settingsForm?.Close(); } catch { }
        StopAll("exit");
        _icon.Visible = false;
        _icon.Dispose();
        _http.Dispose();
        Application.Exit();
    }
}

internal sealed record McpToolEvent(
    long Id,
    string Ts,
    string Tool,
    string Detail,
    string Cmd,
    string Path,
    string Query,
    string Status,
    int? DurationMs,
    long? StartedAtMs = null,
    string Note = ""
);

// ============================================================ Heuristic Humanizer (2-Layer)

internal static class EventHumanizer
{
    public static (string IconKind, string Headline, string MetaPrefix) Format(McpToolEvent ev)
    {
        var formatted = FormatFallback(ev);
        // The server's explicit purpose is more useful than guessing from a script.
        // Keep the heuristic icon and technical context, including for older servers.
        if (string.IsNullOrWhiteSpace(ev.Note)) return formatted;
        string headline = Regex.Replace(ev.Note.Trim(), @"\s+", " ");
        return (formatted.IconKind, headline, formatted.MetaPrefix);
    }

    private static (string IconKind, string Headline, string MetaPrefix) FormatFallback(McpToolEvent ev)
    {
        string rawPath = !string.IsNullOrWhiteSpace(ev.Path) ? ev.Path : ev.Detail;
        string shortPath = ShortenPath(rawPath);
        string leaf = GetLeafName(rawPath);

        switch (ev.Tool)
        {
            case "list_directory":
                return ("folder",
                        $"Views folder contents {(string.IsNullOrEmpty(leaf) ? "project" : leaf)}",
                        $"{(string.IsNullOrEmpty(shortPath) ? "." : shortPath)} · list_directory");

            case "read_file":
                return ("file",
                        $"Reads file {(string.IsNullOrEmpty(leaf) ? shortPath : leaf)}",
                        $"{shortPath} · read_file");

            case "write_file":
                return ("edit",
                        $"Writes file {(string.IsNullOrEmpty(leaf) ? shortPath : leaf)}",
                        $"{shortPath} · write_file");

            case "edit_file":
                return ("edit",
                        $"Edits file {(string.IsNullOrEmpty(leaf) ? shortPath : leaf)}",
                        $"{shortPath} · edit_file");

            case "search_files":
            {
                string q = Truncate(string.IsNullOrWhiteSpace(ev.Query) ? ev.Detail : ev.Query, 24);
                string where = string.IsNullOrWhiteSpace(ev.Path) ? "" : $" in {GetLeafName(ev.Path)}";
                string meta = string.IsNullOrWhiteSpace(ev.Path) ? $"«{q}»" : ShortenPath(ev.Path);
                return ("search", $"Searches for «{q}»{where}", $"{meta} · search_files");
            }

            case "job_status":
                return ("job", "Checks the background job", $"{Truncate(ev.Detail, 22)} · job_status");

            case "job_output":
                return ("job", "Reads the background job output", $"{Truncate(ev.Detail, 22)} · job_output");

            case "journal_entry":
                return ("file", "Writes a step to the journal", $"{Truncate(ev.Detail, 24)} · journal_entry");

            case "journal_log":
                return ("file", "Reads the work journal", "journal.jsonl · journal_log");

            case "audit_log":
                return ("search", "Checks the audit log", "audit.jsonl · audit_log");

            case "run_command":
            {
                string cmd = !string.IsNullOrWhiteSpace(ev.Cmd) ? ev.Cmd : ev.Detail;
                string core = UnwrapPowerShell(cmd);
                var (kind, headline) = HumanizeCommand(core);
                string snippet = Truncate(core, 24);
                return (kind, headline, $"{snippet} · run_command");
            }

            default:
                return ("terminal", $"{ev.Tool}: {Truncate(ev.Detail, 36)}", $"{ev.Tool}");
        }
    }

    private static string UnwrapPowerShell(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        string s = cmd.Trim();
        var mFile = Regex.Match(s, @"powershell(?:\.exe)?\s+(?:-\w+\s+)*-File\s+[""']?([^""'\s]+)[""']?(.*)$", RegexOptions.IgnoreCase);
        if (mFile.Success)
        {
            return mFile.Groups[1].Value.Trim() + " " + mFile.Groups[2].Value.Trim();
        }
        var mCmd = Regex.Match(s, @"^powershell(?:\.exe)?\s+(?:-\w+\s+)*-Command\s+[@""']*(.+?)[""'@]*$", RegexOptions.IgnoreCase);
        if (mCmd.Success)
        {
            s = mCmd.Groups[1].Value.Trim();
        }
        return s;
    }

    private static (string Kind, string Text) HumanizeCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return ("terminal", I18n.T("Runs a command", "Выполняет команду"));

        var parts = cmd.Split(';', '\n')
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p) &&
                        !p.StartsWith("#") &&
                        !Regex.IsMatch(p, @"^(?:Set-Location|cd|Push-Location|Pop-Location|Start-Sleep)\b", RegexOptions.IgnoreCase))
            .ToList();

        string primary = parts.FirstOrDefault() ?? cmd.Trim();
        primary = Regex.Replace(primary, @"^\$[A-Za-z0-9_]+\s*=\s*", "").Trim();

        // 1. Get-Process
        var mProc = Regex.Match(primary, @"^Get-Process\b(?:\s+-Name)?\s*['""]?([A-Za-z0-9_.*,-]+)['""]?", RegexOptions.IgnoreCase);
        if (mProc.Success)
        {
            string name = mProc.Groups[1].Value.Trim('\'', '"', ' ');
            if (!name.StartsWith("-"))
                return ("terminal", I18n.Format("Checks whether {0} is running", "Проверяет, запущен ли {0}", name));
            return ("terminal", "Checks the process list");
        }
        if (Regex.IsMatch(primary, @"^Get-Process\b", RegexOptions.IgnoreCase))
            return ("terminal", "Checks the process list");

        // 2. Stop-Process / taskkill
        var mStop = Regex.Match(primary, @"^(?:Stop-Process|taskkill(?:\.exe)?)\b.*?(?:-Name\s+['""]?([A-Za-z0-9_.-]+)|/IM\s+([^\s]+)|/PID\s+(\d+))", RegexOptions.IgnoreCase);
        if (mStop.Success)
        {
            string target = mStop.Groups[1].Value + mStop.Groups[2].Value + (mStop.Groups[3].Success ? "PID " + mStop.Groups[3].Value : "");
            return ("error", $"Stops process {target}");
        }
        if (Regex.IsMatch(primary, @"\b(?:Stop-Process|taskkill)\b", RegexOptions.IgnoreCase))
            return ("error", "Stops a process");

        // 3. Start-Process
        var mStart = Regex.Match(primary, @"^Start-Process\b(?:\s+-FilePath)?\s+['""]?([^'""\s]+)", RegexOptions.IgnoreCase);
        if (mStart.Success)
            return ("power", I18n.Format("Opens {0}", "Открывает {0}", GetLeafName(mStart.Groups[1].Value)));

        // 4. Get-ChildItem / dir / ls
        var mLs = Regex.Match(primary, @"^(?:Get-ChildItem|gci|dir|ls)\b(?:\s+(?:-\w+\s+)*['""]?([^'""|-]+)['""]?)?", RegexOptions.IgnoreCase);
        if (mLs.Success)
        {
            string arg = mLs.Groups[1].Value.Trim();
            string leaf = string.IsNullOrEmpty(arg) || arg.StartsWith("-") ? I18n.T("folder", "папке") : GetLeafName(arg);
            return ("folder", I18n.Format("Views files in {0}", "Смотрит файлы в {0}", (string.IsNullOrEmpty(leaf) ? I18n.T("folder", "папке") : leaf)));
        }

        // 5. Get-Content / cat / type
        var mCat = Regex.Match(primary, @"^(?:Get-Content|gc|cat|type)\b(?:\s+(?:-\w+\s+)*['""]?([^'""|-]+)['""]?)?", RegexOptions.IgnoreCase);
        if (mCat.Success)
        {
            string leaf = GetLeafName(mCat.Groups[1].Value.Trim());
            return ("file", I18n.Format("Reads {0}", "Читает {0}", (string.IsNullOrEmpty(leaf) ? I18n.T("file", "файл") : leaf)));
        }

        // 6. New-Item / mkdir
        if (Regex.IsMatch(primary, @"^(?:New-Item|mkdir|md)\b", RegexOptions.IgnoreCase))
        {
            var mPath = Regex.Match(primary, @"(?:-Path\s+)?['""]?([^'""\s|]+)['""]?\s*$", RegexOptions.IgnoreCase);
            string leaf = mPath.Success ? GetLeafName(mPath.Groups[1].Value) : "";
            bool isDir = Regex.IsMatch(primary, @"Directory|mkdir|md\b", RegexOptions.IgnoreCase);
            return isDir
                ? ("folder", I18n.Format("Creates a folder {0}", "Создаёт папку {0}", (string.IsNullOrEmpty(leaf) ? "" : leaf)).Trim())
                : ("edit", I18n.Format("Creates {0}", "Создаёт {0}", (string.IsNullOrEmpty(leaf) ? I18n.T("item", "элемент") : leaf)));
        }

        // 7. Remove-Item / rm / del
        if (Regex.IsMatch(primary, @"^(?:Remove-Item|ri|rm|del|erase)\b", RegexOptions.IgnoreCase))
        {
            var mPath = Regex.Match(primary, @"['""]?([^'""\s|-]+)['""]?\s*$");
            string leaf = mPath.Success ? GetLeafName(mPath.Groups[1].Value) : "";
            return ("error", I18n.Format("Deletes {0}", "Удаляет {0}", (string.IsNullOrEmpty(leaf) ? I18n.T("files", "файлы") : leaf)));
        }

        // 8. Copy-Item / Move-Item
        if (Regex.IsMatch(primary, @"^(?:Copy-Item|cp|copy)\b", RegexOptions.IgnoreCase))
            return ("folder", I18n.T("Copies files", "Копирует файлы"));
        if (Regex.IsMatch(primary, @"^(?:Move-Item|mv|move)\b", RegexOptions.IgnoreCase))
            return ("folder", I18n.T("Moves files", "Перемещает файлы"));

        // 9. Test-NetConnection / Test-Path
        var mPort = Regex.Match(primary, @"^Test-NetConnection\b.*?-Port\s+(\d+)", RegexOptions.IgnoreCase);
        if (mPort.Success)
            return ("net", I18n.Format("Checks port {0}", "Проверяет порт {0}", mPort.Groups[1].Value));
        var mTestPath = Regex.Match(primary, @"^Test-Path\s+['""]?([^'""|]+)", RegexOptions.IgnoreCase);
        if (mTestPath.Success)
            return ("search", I18n.Format("Checks for {0}", "Проверяет наличие {0}", GetLeafName(mTestPath.Groups[1].Value)));

        // 10. Invoke-RestMethod / Invoke-WebRequest / curl
        var mUrl = Regex.Match(primary, @"\b(?:Invoke-RestMethod|Invoke-WebRequest|irm|iwr|curl)\b.*?(https?://[^\s'""]+)", RegexOptions.IgnoreCase);
        if (mUrl.Success && Uri.TryCreate(mUrl.Groups[1].Value, UriKind.Absolute, out var uri))
            return ("net", I18n.Format("Request to {0}{1}", "Запрос к {0}{1}", uri.Host, uri.AbsolutePath));

        // 11. Archive operations
        if (Regex.IsMatch(primary, @"\b(?:Expand-Archive|unzip)\b", RegexOptions.IgnoreCase))
            return ("folder", I18n.T("Extracts an archive", "Распаковывает архив"));
        if (Regex.IsMatch(primary, @"\b(?:Compress-Archive|zip)\b", RegexOptions.IgnoreCase))
            return ("folder", I18n.T("Packs an archive", "Упаковывает архив"));

        // 12. Known scripts & tools
        if (Regex.IsMatch(cmd, @"build-tray\.ps1|dotnet\s+(?:publish|build)", RegexOptions.IgnoreCase))
            return ("terminal", I18n.T("Builds ZeroIDE MCP", "Собирает ZeroIDE MCP"));
        if (Regex.IsMatch(cmd, @"install\.ps1", RegexOptions.IgnoreCase))
            return ("power", I18n.T("Runs the package installer", "Запускает установщик пакета"));
        if (Regex.IsMatch(cmd, @"run-tests\.ps1|smoke\.mjs", RegexOptions.IgnoreCase))
            return ("power", I18n.T("Runs the server self-tests", "Запускает автотесты сервера"));

        var mGit = Regex.Match(primary, @"^git\s+([a-z-]+)", RegexOptions.IgnoreCase);
        if (mGit.Success)
            return ("terminal", $"Git: {mGit.Groups[1].Value}");

        var mNpm = Regex.Match(primary, @"^(?:npm|pnpm|yarn)\s+([a-z-]+)", RegexOptions.IgnoreCase);
        if (mNpm.Success)
            return ("power", I18n.Format("Packages: npm {0}", "Пакеты: npm {0}", mNpm.Groups[1].Value));

        var mNode = Regex.Match(primary, @"^node(?:\.exe)?\s+['""]?([^'""\s]+)", RegexOptions.IgnoreCase);
        if (mNode.Success)
            return ("power", I18n.Format("Runs {0}", "Запускает {0}", GetLeafName(mNode.Groups[1].Value)));

        if (Regex.IsMatch(primary, @"^scp\b", RegexOptions.IgnoreCase))
            return ("net", I18n.T("Transfers files via SCP", "Передаёт файлы по SCP"));
        if (Regex.IsMatch(primary, @"^ssh\b", RegexOptions.IgnoreCase))
            return ("net", I18n.T("Commands on the VPS via SSH", "Команда на VPS по SSH"));

        var mEcho = Regex.Match(primary, @"^(?:Write-Output|Write-Host|echo)\s+['""]?(.+?)['""]?$", RegexOptions.IgnoreCase);
        if (mEcho.Success)
            return ("terminal", I18n.Format("Outputs: {0}", "Выводит: {0}", Truncate(mEcho.Groups[1].Value, 34)));

        return ("terminal", Truncate(primary, 46));
    }

    public static string ShortenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Trim('"', '\'');
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile) && p.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            p = p[userProfile.Length..].TrimStart('\\', '/');
        }
        return Truncate(string.IsNullOrEmpty(p) ? path : p, 22);
    }

    public static string GetLeafName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string cleaned = path.Trim().Trim('"', '\'').TrimEnd('\\', '/');
        try
        {
            string name = Path.GetFileName(cleaned);
            return string.IsNullOrEmpty(name) ? cleaned : name;
        }
        catch
        {
            return cleaned;
        }
    }

    public static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string single = Regex.Replace(text, @"\s+", " ").Trim();
        return single.Length <= maxLen ? single : single[..(maxLen - 1)] + "…";
    }
}

// ============================================================ Informational Toasts (4 Card Styles + Non-Overlapping Stack + Real Duration)

// Notification surfaces: reference 05a / 05b / 05c. Approval prompts remain separate.
internal static class TrayToastManager
{
    private static readonly List<TrayToastForm> Active = new();
    internal const int CardWidth = 456; // 440px surface + two 8px shadow gutters
    internal const int StandardCardHeight = 140;
    internal const int CompactCardHeight = 92;
    private const int MaxVisible = 4;
    private const int RightMargin = 14, BottomMargin = 12, Gap = 8;
    private static string _currentStyle = "glass";
    private static bool _reflowing;

    public static void Show(long eventId, string iconKind, string headline, string metaPrefix, string timeStr,
        string status, int? durationMs, long? startedAtMs = null, string? cardStyle = null, McpToolEvent? toolEvent = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(cardStyle)) _currentStyle = cardStyle;
            if (eventId > 0)
            {
                var existing = Active.FirstOrDefault(t => t.ContainsEvent(eventId));
                if (existing != null) { existing.UpdateEventStatus(eventId, status, durationMs); return; }
            }
            var last = Active.LastOrDefault();
            if (last != null && last.MatchesStack(iconKind, headline))
            {
                last.BumpStack(timeStr, status, durationMs, eventId, toolEvent, startedAtMs, metaPrefix);
                Reflow(); return;
            }
            while (Active.Count >= MaxVisible)
            {
                var oldest = Active.FirstOrDefault(t => !t.IsExpanded) ?? Active[0];
                Active.Remove(oldest); oldest.SetLayoutVisible(false); oldest.BeginFadeOut();
            }
            var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            int h = _currentStyle.Equals("compact", StringComparison.OrdinalIgnoreCase) ? CompactCardHeight : StandardCardHeight;
            int y = wa.Bottom - ApprovalToastManager.GetReservedBottomHeight() - h - BottomMargin;
            var toast = new TrayToastForm(eventId, iconKind, headline, metaPrefix, timeStr, status, durationMs, startedAtMs,
                _currentStyle, CardWidth, h, wa.Right - CardWidth - RightMargin, y + 12, y, toolEvent);
            toast.FormClosed += (_, _) => { Active.Remove(toast); Reflow(); };
            Active.Add(toast); Reflow(); toast.ShowToast();
        }
        catch { /* A notification must never interrupt the supervisor. */ }
    }

    public static void UpdateEventStatus(long eventId, string status, int? durationMs)
    {
        if (eventId <= 0) return;
        foreach (var toast in Active.ToArray()) toast.UpdateEventStatus(eventId, status, durationMs);
    }
    public static void CollapseOtherDetails(TrayToastForm expanded)
    { foreach (var t in Active.ToArray()) if (t != expanded) t.CollapseDetails(); }
    public static void UpdateAllCardsStyle(string style)
    {
        _currentStyle = string.IsNullOrWhiteSpace(style) ? "glass" : style;
        foreach (var t in Active.ToArray()) t.SetCardStyle(_currentStyle);
        Reflow();
    }
    public static void DismissAll()
    {
        foreach (var t in Active.ToArray()) { try { t.Close(); t.Dispose(); } catch { } }
        Active.Clear();
    }

    public static void Reflow() => ReflowIn((Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea, ApprovalToastManager.GetReservedBottomHeight());
    internal static void ReflowIn(Rectangle wa, int reservedBottom)
    {
        if (_reflowing) return;
        _reflowing = true;
        try
        {
            int available = Math.Max(0, wa.Height - Math.Max(0, reservedBottom) - BottomMargin - 12);
            int remaining = available;
            var visible = new HashSet<TrayToastForm>();
            // Pin the expanded card; use the remaining space for newest notifications.
            var candidates = Active.Where(t => t.IsExpanded).Concat(Active.AsEnumerable().Reverse().Where(t => !t.IsExpanded)).ToArray();
            foreach (var card in candidates)
            {
                card.FitToWorkArea(wa, remaining);
                if (card.Height <= remaining)
                { visible.Add(card); remaining -= card.Height + Gap; }
            }
            int bottom = wa.Bottom - Math.Max(0, reservedBottom) - BottomMargin;
            foreach (var card in Active.AsEnumerable().Reverse().ToArray())
            {
                bool show = visible.Contains(card);
                if (show) { card.PlaceAt(wa.Right - RightMargin, bottom - card.Height); bottom -= card.Height + Gap; }
                card.SetLayoutVisible(show);
            }
        }
        finally { _reflowing = false; }
    }
}

internal sealed class TrayToastForm : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;


    private sealed record ToastItem(long Id, string Kind, string Headline, string Meta, string Time,
        string Status, int? Duration, long? Started, McpToolEvent? Event, DateTimeOffset SeenAt);
    private readonly List<ToastItem> _items = new();
    private readonly string _stackKind, _stackHeadline;
    private readonly int _collapsedWidth;
    private int _selectedIndex;
    private bool _detailsExpanded;
    private int _detailScrollY, _detailMaxScroll;
    private string _cardStyle;
    private readonly UiFrameTimer _animTimer;
    private readonly UiAnimationClock _motion = new();
    internal event Action<double>? FramePresented;
    private Bitmap? _animationSurface;
    private string _animationSurfaceKey = "";
    private bool _suppressLiveParts;
    private readonly ToolTip _toolTip = new();
    private PointF _mouse = new(-1, -1);
    private int _rightEdge, _targetY;
    private double _currentY, _alpha, _wavePhase, _pulse;
    private int _elapsedMs, _lastReservedBottom;
    private const int LifetimeMs = 5000;
    private bool _fadingOut, _isHovered, _layoutVisible = true, _hasBeenShown;
    private string _copyFeedback = "";
    private DateTime _copyFeedbackUntil;
    private int _heightLimit = int.MaxValue, _widthLimit = int.MaxValue;
    internal int LayeredFrameVersion { get; private set; }
    internal string CopyPayload => Selected.Headline + Environment.NewLine + DetailText;
    private DetailLayout? _cachedLayout;
    private string _layoutKey = "";
    private float ScaleFactor => Math.Max(1f, DeviceDpi / 96f);
    private float LogicalWidth => Width / ScaleFactor;
    private float LogicalHeight => Height / ScaleFactor;
    private bool Compact => _cardStyle.Equals("compact", StringComparison.OrdinalIgnoreCase);
    private bool Light => _cardStyle.Equals("light", StringComparison.OrdinalIgnoreCase);
    private bool Neon => _cardStyle.Equals("neon", StringComparison.OrdinalIgnoreCase);
    private ToastItem Selected => _items[Math.Clamp(_selectedIndex, 0, _items.Count - 1)];
    private ToastItem Newest => _items[^1];
    public long EventId => Newest.Id;
    public bool IsExpanded => _detailsExpanded;
    public int StackCount => _items.Count;
    internal bool IsLayoutHidden => !_layoutVisible;
    internal string AggregateStatus => _items.Any(i => i.Status == "running") ? "running"
        : _items.Any(i => i.Status == "pending") ? "pending"
        : _items.Any(i => i.Status is "error" or "failed") ? "error"
        : _items.Any(i => i.Status == "timeout") ? "timeout"
        : _items.Any(i => i.Status == "denied") ? "denied"
        : _items.Any(i => i.Status == "cancelled") ? "cancelled"
        : _items.All(i => i.Status == "done") ? "done" : Newest.Status;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00080000; // no-activate / tool / topmost / layered
            return cp;
        }
    }

    public TrayToastForm(long eventId, string iconKind, string headline, string metaPrefix, string timeStr,
        string status, int? durationMs, long? startedAtMs, string cardStyle, int width, int height,
        int x, int startY, int targetY, McpToolEvent? toolEvent = null)
    {
        _animTimer = new UiFrameTimer(this);
        _stackKind = iconKind; _stackHeadline = headline; _collapsedWidth = Math.Max(300, width);
        _cardStyle = string.IsNullOrWhiteSpace(cardStyle) ? "glass" : cardStyle;
        _items.Add(MakeItem(eventId, iconKind, headline, metaPrefix, timeStr, status, durationMs, startedAtMs, toolEvent));
        _rightEdge = x + width; _currentY = startY; _targetY = targetY;
        _lastReservedBottom = ApprovalToastManager.GetReservedBottomHeight();
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; ShowInTaskbar = false; TopMost = true;
        Cursor = Cursors.Hand; AccessibleName = TitleFor(Newest, status);
        ResizeForDetails(); Location = new Point(_rightEdge - Width, startY);
        _animTimer.Tick += (_, _) => OnAnimTick();
    }
    private static ToastItem MakeItem(long id, string kind, string headline, string meta, string time,
        string status, int? duration, long? started, McpToolEvent? ev)
    {
        var seen = DateTimeOffset.Now;
        if (ev != null && DateTimeOffset.TryParse(ev.Ts, out var parsed)) seen = parsed;
        return new ToastItem(id, kind, headline ?? "", meta ?? "", time ?? "", status ?? "unknown", duration, started, ev, seen);
    }
    public void ShowToast()
    {
        _hasBeenShown = true; _motion.Step(); _motion.SetActive(true);
        if (_layoutVisible) { Show(); RenderLayered(); SetWindowPos(Handle, HWND_TOPMOST, Left, Top, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW); }
        _animTimer.Start();
    }
    internal void PlaceAt(int rightEdge, int y)
    {
        _rightEdge = rightEdge; _targetY = y;
        _currentY = y; Location = new Point(rightEdge - Width, y);
    }
    public void SetTargetY(int y) { _targetY = y; }
    internal void SetLayoutVisible(bool visible)
    {
        if (_layoutVisible == visible) return;
        _layoutVisible = visible;
        if (!visible) Hide();
        else if (_hasBeenShown && !_fadingOut && !IsDisposed) { Show(); RenderLayered(); }
    }
    internal void FitToWorkArea(Rectangle wa, int availableHeight)
    {
        _widthLimit = Math.Max(180, wa.Width - 28);
        _heightLimit = Math.Max(0, availableHeight);
        ResizeForDetails();
    }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); ResizeForDetails(); TrayToastManager.Reflow(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    { base.OnDpiChanged(e); ResizeForDetails(); TrayToastManager.Reflow(); RenderLayered(); }
    protected override void OnFormClosed(FormClosedEventArgs e)
    { _animTimer.Stop(); base.OnFormClosed(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _animTimer.Stop(); _animTimer.Dispose(); _motion.Dispose(); _animationSurface?.Dispose(); _toolTip.Dispose(); }
        base.Dispose(disposing);
    }
    public void SetCardStyle(string style)
    {
        _cardStyle = string.IsNullOrWhiteSpace(style) ? "glass" : style;
        _cachedLayout = null; _animationSurfaceKey = ""; ResizeForDetails(); TrayToastManager.Reflow(); RenderLayered();
    }
    public void BeginFadeOut() { _fadingOut = true; if (!_animTimer.Enabled) _animTimer.Start(); }
    public bool ContainsEvent(long eventId) => _items.Any(i => i.Id == eventId);
    public void UpdateCompletion(string status, int? durationMs) => UpdateEventStatus(EventId, status, durationMs);
    public void UpdateEventStatus(long id, string status, int? durationMs)
    {
        int index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return;
        var old = _items[index];
        if (old.Status == status && old.Duration == durationMs) return;
        _items[index] = old with { Status = status, Duration = durationMs, Event = old.Event is null ? null : old.Event with { Status = status, DurationMs = durationMs } };
        _elapsedMs = Math.Min(_elapsedMs, LifetimeMs - 2500); _animationSurfaceKey = "";
        AccessibleName = TitleFor(Newest, AggregateStatus);
        RenderLayered(); // Repaint the layered HWND now, even without hover or movement.
    }
    public bool MatchesStack(string iconKind, string headline) => !_detailsExpanded && !_fadingOut && _items.Count < 50
        && _stackKind == iconKind && _stackHeadline == headline;
    public void BumpStack(string timeStr, string status, int? durationMs, long eventId,
        McpToolEvent? toolEvent = null, long? startedAtMs = null, string? metaPrefix = null)
    {
        if (_fadingOut) return;
        if (eventId > 0 && ContainsEvent(eventId)) { UpdateEventStatus(eventId, status, durationMs); return; }
        var previous = Newest;
        var ev = toolEvent ?? (previous.Event is null ? null : previous.Event with { Id = eventId, Status = status, DurationMs = durationMs, StartedAtMs = startedAtMs });
        _items.Add(MakeItem(eventId, _stackKind, _stackHeadline, metaPrefix ?? previous.Meta, timeStr, status, durationMs, startedAtMs, ev));
        _selectedIndex = _items.Count - 1; _cachedLayout = null; _animationSurfaceKey = ""; _elapsedMs = 0; _pulse = 1;
        ResizeForDetails(); TrayToastManager.Reflow(); RenderLayered();
    }
    public void ToggleDetails()
    {
        if (_fadingOut) return;
        _detailsExpanded = !_detailsExpanded; _detailScrollY = 0; _elapsedMs = 0; _copyFeedback = "";
        if (_detailsExpanded) { _selectedIndex = _items.Count - 1; TrayToastManager.CollapseOtherDetails(this); }
        _cachedLayout = null; _animationSurfaceKey = ""; ResizeForDetails(); TrayToastManager.Reflow(); RenderLayered();
    }
    public void CollapseDetails()
    {
        if (!_detailsExpanded) return;
        _detailsExpanded = false; _detailScrollY = 0; _elapsedMs = 0;
        ResizeForDetails(); RenderLayered();
    }
    internal void SelectStackItem(int index)
    {
        if (!_detailsExpanded || index < 0 || index >= StackCount) return;
        _selectedIndex = index; _detailScrollY = 0; _cachedLayout = null; _animationSurfaceKey = ""; _copyFeedback = "";
        ResizeForDetails(); TrayToastManager.Reflow(); RenderLayered();
    }

    private string DetailText => Selected.Event is null ? Selected.Meta
        : !string.IsNullOrWhiteSpace(Selected.Event.Cmd) ? Selected.Event.Cmd
        : !string.IsNullOrWhiteSpace(Selected.Event.Path) ? Selected.Event.Path + (string.IsNullOrWhiteSpace(Selected.Event.Query) ? "" : "\n" + Selected.Event.Query)
        : !string.IsNullOrWhiteSpace(Selected.Event.Query) ? Selected.Event.Query : Selected.Event.Detail;
    private string DetailLabel => Selected.Event is null ? I18n.T("Details", "Подробности")
        : !string.IsNullOrWhiteSpace(Selected.Event.Cmd) ? I18n.T("Command", "Команда")
        : !string.IsNullOrWhiteSpace(Selected.Event.Path) ? I18n.T("Path / request", "Путь / запрос") : I18n.T("Request", "Запрос");
    private bool DetailMayBeTruncated => Selected.Event is { } ev && (ev.Cmd.Length >= 240 || ev.Path.Length >= 240 || ev.Query.Length >= 120);
    private static string TruncationHint => I18n.T("The server may have shortened this text. Full information is available in the journal.", "Сервер мог сократить этот текст. Полная информация доступна в журнале.");
    private static Font Ui(float px, bool strong = false) => PanelTypography.Get(px, strong);
    private static Font Mono => Theme.FontMono(9.375f);
    private sealed record DetailLayout(string[] Head, string[] Code, string[] Hint, float HeadHeight, float CodeY, float CodeHeight, float Height);
    private DetailLayout GetDetailLayout(Graphics g, float width)
    {
        string key = _selectedIndex + "|" + width.ToString(CultureInfo.InvariantCulture) + "|" + I18n.CurrentLanguage;
        if (_cachedLayout != null && key == _layoutKey) return _cachedLayout;
        var head = Wrap(g, Selected.Headline, Ui(14, true), width);
        string codeText = (Selected.Event?.Tool == "run_command" ? "PS> " : "") + DetailText;
        var code = Wrap(g, codeText, Mono, width - 28);
        var hint = DetailMayBeTruncated ? Wrap(g, TruncationHint, Ui(12), width) : Array.Empty<string>();
        float hh = head.Length * 21f, cy = hh + 14 + 18 + 6, ch = code.Length * 20.5f + 24;
        _layoutKey = key;
        return _cachedLayout = new DetailLayout(head, code, hint, hh, cy, ch, cy + ch + (hint.Length > 0 ? 12 + hint.Length * 18 : 0));
    }
    private static string[] Wrap(Graphics g, string? text, Font font, float width)
    {
        var output = new List<string>(); width = Math.Max(40, width);
        foreach (string paragraph in (text ?? "").Replace("\r", "").Split('\n'))
        {
            string remaining = paragraph;
            if (remaining.Length == 0) { output.Add(""); continue; }
            while (remaining.Length > 0)
            {
                int[] boundaries = StringInfo.ParseCombiningCharacters(remaining);
                int lo = 1, hi = boundaries.Length, fit = 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2, end = mid == boundaries.Length ? remaining.Length : boundaries[mid];
                    if (Theme.MeasureText(g, remaining[..end], font).Width <= width) { fit = mid; lo = mid + 1; } else hi = mid - 1;
                }
                int cut = fit == boundaries.Length ? remaining.Length : boundaries[fit];
                if (cut < remaining.Length)
                {
                    int space = remaining.LastIndexOf(' ', Math.Max(0, cut - 1), cut);
                    if (space > cut / 3) cut = space + 1;
                }
                output.Add(remaining[..cut].TrimEnd()); remaining = remaining[cut..];
            }
        }
        return output.Count == 0 ? new[] { "" } : output.ToArray();
    }

    private float StackTail => !_detailsExpanded ? Math.Min(2, StackCount - 1) * 9 : 0;
    private RectangleF CardRect => new(8, 8, LogicalWidth - 16, _detailsExpanded ? LogicalHeight - 16 : Compact ? 76 : 124);
    private RectangleF CloseRect => new(CardRect.Right - 42, CardRect.Y + (Compact && !_detailsExpanded ? 6 : 8), 30, Compact && !_detailsExpanded ? 22 : 28);
    private RectangleF ExpandRect => new(CardRect.Right - 42, CardRect.Y + (Compact ? 30 : 42), 30, Compact ? 22 : 28);
    private float CollapseWidth => CardRect.Width < 340 ? 40 : CardRect.Width < 470 ? 112 : 132;
    private RectangleF CollapseRect => new(CardRect.Right - 20 - CollapseWidth, CardRect.Bottom - 48, CollapseWidth, 32);
    private RectangleF CopyRect => new(CollapseRect.X - 8 - (CardRect.Width < 470 ? 40 : 164), CardRect.Bottom - 48, CardRect.Width < 470 ? 40 : 164, 32);
    private RectangleF PrevRect => new(CardRect.X + 16, CardRect.Bottom - 48, 28, 32);
    private RectangleF NextRect => new(CardRect.X + 82, CardRect.Bottom - 48, 28, 32);
    private RectangleF DetailViewport => new(CardRect.X + 20, CardRect.Y + 88, CardRect.Width - 40, Math.Max(1, CardRect.Height - 168));
    private void ResizeForDetails()
    {
        float scale = ScaleFactor;
        var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        int widthLimit = Math.Min(_widthLimit, Math.Max(180, wa.Width - 28));
        int w = Math.Min((int)Math.Round((_detailsExpanded ? 576 : _collapsedWidth) * scale), widthLimit);
        float logicalW = w / scale;
        int h;
        if (!_detailsExpanded) h = (int)Math.Ceiling(((Compact ? 92 : 140) + StackTail) * scale);
        else
        {
            using var b = new Bitmap(1, 1); b.SetResolution(96, 96); using var g = Graphics.FromImage(b);
            float desired = 184 + GetDetailLayout(g, logicalW - 56).Height;
            int available = Math.Min(_heightLimit, wa.Height - ApprovalToastManager.GetReservedBottomHeight() - 24);
            int min = (int)Math.Ceiling(236 * scale);
            h = Math.Max(min, Math.Min((int)Math.Ceiling(Math.Min(560, desired) * scale), available));
        }
        int bottom = Top + Height;
        if (Width != w || Height != h)
        {
            Size = new Size(w, h); Left = _rightEdge - Width;
            if (_hasBeenShown) { Top = Math.Max(wa.Top + 8, bottom - Height); _currentY = Top; }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); _mouse = new PointF(e.X / ScaleFactor, e.Y / ScaleFactor);
        bool clickable = !_detailsExpanded || CloseRect.Contains(_mouse) || CopyRect.Contains(_mouse) || CollapseRect.Contains(_mouse)
            || (StackCount > 1 && (PrevRect.Contains(_mouse) || NextRect.Contains(_mouse)));
        Cursor = clickable ? Cursors.Hand : Cursors.Default;
        string? tip = !_detailsExpanded ? TechnicalLine + "\n" + I18n.T("Click for details", "Нажмите для просмотра подробностей") : null;
        if (CloseRect.Contains(_mouse)) tip = I18n.T("Dismiss notification", "Закрыть уведомление");
        else if (_detailsExpanded && CollapseRect.Contains(_mouse)) tip = I18n.T("Collapse details", "Свернуть подробности");
        else if (_detailsExpanded && CopyRect.Contains(_mouse)) tip = I18n.T("Copy the selected action and its details", "Скопировать выбранное действие и его подробности");
        _toolTip.SetToolTip(this, tip); RenderLayered();
    }
    protected override void OnMouseLeave(EventArgs e)
    { base.OnMouseLeave(e); _mouse = new(-1, -1); _toolTip.SetToolTip(this, null); RenderLayered(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e); if (e.Button != MouseButtons.Left) return;
        var p = new PointF(e.X / ScaleFactor, e.Y / ScaleFactor);
        if (CloseRect.Contains(p)) { BeginFadeOut(); return; }
        if (!_detailsExpanded) { if (CardRect.Contains(p)) ToggleDetails(); return; }
        if (CopyRect.Contains(p))
        {
            try { Clipboard.SetText(CopyPayload); _copyFeedback = I18n.T("Copied", "Скопировано"); }
            catch { _copyFeedback = I18n.T("Try again", "Повторить"); }
            _copyFeedbackUntil = DateTime.Now.AddSeconds(2); RenderLayered(); return;
        }
        if (CollapseRect.Contains(p)) { ToggleDetails(); return; }
        if (StackCount > 1 && PrevRect.Contains(p)) { SelectStackItem(_selectedIndex - 1); return; }
        if (StackCount > 1 && NextRect.Contains(p)) { SelectStackItem(_selectedIndex + 1); return; }
        var v = DetailViewport;
        if (_detailMaxScroll > 0 && p.X >= CardRect.Right - 14 && p.Y >= v.Top && p.Y <= v.Bottom)
        { _detailScrollY = (int)(_detailMaxScroll * Math.Clamp((p.Y - v.Y) / v.Height, 0, 1)); RenderLayered(); }
        // Clicking content does not accidentally collapse it or lose the selected member.
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e); if (!_detailsExpanded) return;
        _detailScrollY = Math.Clamp(_detailScrollY - Math.Sign(e.Delta) * 44, 0, _detailMaxScroll); RenderLayered();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { if (_detailsExpanded) ToggleDetails(); else BeginFadeOut(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnAnimTick()
    {
        if (IsDisposed) return;
        int reserved = ApprovalToastManager.GetReservedBottomHeight();
        if (reserved != _lastReservedBottom) { _lastReservedBottom = reserved; TrayToastManager.Reflow(); }
        double previousAlpha = _alpha, previousPulse = _pulse;
        bool previousHover = _isHovered;
        _isHovered = _layoutVisible && Bounds.Contains(Cursor.Position);
        double dt = _motion.Step();
        _motion.SetActive(_layoutVisible && (_alpha < 1 || _fadingOut || _pulse > 0 || Math.Abs(_targetY - _currentY) > .2 || IsRunning(AggregateStatus)));
        _wavePhase = _motion.Seconds * 5;
        if (_fadingOut)
        {
            _alpha -= dt / .20;
            if (_alpha <= .01) { _animTimer.Stop(); Close(); Dispose(); return; }
        }
        else
        {
            _alpha = Math.Min(1, _alpha + dt / .18); _pulse = Math.Max(0, _pulse - dt / .32);
            if (_layoutVisible && !_isHovered && !_detailsExpanded && !IsRunning(AggregateStatus))
            { _elapsedMs += (int)Math.Round(dt * 1000); if (_elapsedMs >= LifetimeMs) _fadingOut = true; }
        }
        if (_copyFeedback.Length > 0 && DateTime.Now >= _copyFeedbackUntil) { _copyFeedback = ""; RenderLayered(); }
        int top = (int)Math.Round(_currentY = UiAnimationClock.Follow(_currentY, _targetY, dt));
        bool moved = top != Top; if (moved) Top = top;
        if (_layoutVisible && (moved || Math.Abs(previousAlpha - _alpha) > .003 || Math.Abs(previousPulse - _pulse) > .001
            || previousHover != _isHovered || IsRunning(_detailsExpanded ? Selected.Status : AggregateStatus))) RenderLayered();
    }

    public void RenderToGraphics(Graphics g)
    {
        double pulse = _pulse; _pulse = 0;
        try { DrawCardContent(g); } finally { _pulse = pulse; }
    }
    private void RenderLayered()
    {
        if (IsDisposed || !IsHandleCreated || !_layoutVisible) return;

        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            DrawCardContent(g);
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);
            var size = new Size(Width, Height);
            var pointSrc = new Point(0, 0);
            var topPos = new Point(Left, Top);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Clamp((int)(_alpha * 255), 0, 255),
                AlphaFormat = AC_SRC_ALPHA,
            };
            if (UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref pointSrc, 0, ref blend, ULW_ALPHA)) { LayeredFrameVersion++; FramePresented?.Invoke(_motion.Seconds); }
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
        }
    }


    private void DrawCardContent(Graphics output)
    {
        bool live = !_detailsExpanded && IsRunning(AggregateStatus) && _pulse <= 0;
        if (live)
        {
            string key = $"{Width}/{Height}/{_cardStyle}/{StackCount}/{AggregateStatus}/{(long)_motion.Seconds}/{CloseRect.Contains(_mouse)}/{ExpandRect.Contains(_mouse)}";
            if (_animationSurface == null || key != _animationSurfaceKey)
            {
                _animationSurface?.Dispose(); _animationSurface = CreateCanvas();
                using var g = Graphics.FromImage(_animationSurface);
                _suppressLiveParts = true;
                try { PaintLogicalCard(g); } finally { _suppressLiveParts = false; }
                _animationSurfaceKey = key;
            }
            output.DrawImage(_animationSurface, new Rectangle(0, 0, Width, Height), new Rectangle(0, 0, _animationSurface.Width, _animationSurface.Height), GraphicsUnit.Pixel);
            var st = output.Save(); output.ScaleTransform(Width / (float)_animationSurface.Width, Height / (float)_animationSurface.Height);
            Theme.PrepareGraphics(output, layered: true);
            var r = CardRect; float tile = Compact ? 30 : 40, top = r.Y + (Compact ? 8 : 14);
            DrawStateTile(output, new RectangleF(r.X + (Compact ? 14 : 18), top + 2, tile, tile), AggregateStatus);
            UiAnimationClock.DrawMarquee(output, new RectangleF(r.X + 18, r.Bottom - 5, r.Width - 36, 2), StatusColor(AggregateStatus), _motion.Seconds);
            output.Restore(st); return;
        }
        using var canvas = CreateCanvas(); using(var g = Graphics.FromImage(canvas)) PaintLogicalCard(g);
        output.DrawImage(canvas, new Rectangle(0, 0, Width, Height), new Rectangle(0, 0, canvas.Width, canvas.Height), GraphicsUnit.Pixel);
    }
    private Bitmap CreateCanvas()
    {
        var b = new Bitmap(Math.Max(1, (int)Math.Ceiling(LogicalWidth)), Math.Max(1, (int)Math.Ceiling(LogicalHeight)), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        b.SetResolution(96, 96); return b;
    }
    private void PaintLogicalCard(Graphics g)
    {
            Theme.PrepareGraphics(g, layered: true); g.Clear(Color.Transparent);
            var r = CardRect;
            if (!_detailsExpanded)
                for (int i = Math.Min(2, StackCount - 1); i >= 1; i--)
                {
                    var back = new RectangleF(r.X + i * 14, r.Bottom - 26, r.Width - i * 28, 26 + i * 9);
                    Theme.DrawShadow(g, back, 16, 6, 60, 3);
                    Theme.FillRound(g, back, 16, Light ? Color.FromArgb(220 - i * 18, 234, 237, 244) : Color.FromArgb(225 - i * 24, 24, 28, 36));
                    Theme.StrokeRound(g, back, 16, Light ? Color.FromArgb(24, 0, 0, 0) : Theme.Border);
                }
            Theme.DrawShadow(g, r, 16, 7, Light ? 50 : 95, 3);
            if (Neon) Theme.DrawGlow(g, r, 16, Theme.WithAlpha(Theme.Accent, 65), 5, 0);
            Theme.FillRoundGradient(g, r, 16, Light ? Color.White : Theme.BgGradTop, Light ? Color.FromArgb(245, 247, 251) : Theme.BgGradBottom);
            string status = _detailsExpanded ? Selected.Status : AggregateStatus;
            Color semantic = StatusColor(status);
            var clipState = g.Save();
            using (var clip = Theme.RoundRect(r, 16)) g.SetClip(clip);
            using (var brush = new SolidBrush(semantic)) g.FillRectangle(brush, r.X, r.Y, 3, _detailsExpanded ? 72 : r.Height);
            if (_detailsExpanded) DrawExpanded(g, r); else DrawCollapsed(g, r, status);
            g.Restore(clipState);
            Theme.StrokeRound(g, r, 16, Neon ? Theme.WithAlpha(Theme.Accent, 145) : Light ? Color.FromArgb(28, 22, 30, 48) : Theme.BorderStrong);
            if (_pulse > 0 && !_detailsExpanded) Theme.StrokeRound(g, r, 16, Theme.WithAlpha(Theme.Accent, (int)(_pulse * 105)), 1.3f);
    }
    private Color Primary => Light ? Color.FromArgb(35, 41, 53) : Theme.TextPrimary;
    private Color Secondary => Light ? Color.FromArgb(92, 102, 120) : Theme.TextSecondary;
    private Color Tertiary => Light ? Color.FromArgb(108, 118, 136) : Theme.TextTertiary;
    private Color LineColor => Light ? Color.FromArgb(22, 25, 34, 49) : Theme.Divider;
    private static bool IsRunning(string status) => status is "running" or "pending";
    private static bool IsFailure(string status) => status is "error" or "failed" or "timeout" or "denied" or "cancelled";
    private Color StatusColor(string status) => status == "done" ? (Light ? Color.FromArgb(17, 132, 78) : Theme.Success)
        : IsRunning(status) ? (Light ? Color.FromArgb(69, 83, 210) : Theme.AccentHover)
        : status is "timeout" or "denied" or "cancelled" ? (Light ? Color.FromArgb(157, 100, 10) : Theme.Warning)
        : IsFailure(status) ? (Light ? Color.FromArgb(197, 58, 69) : Theme.Danger) : (Light ? Color.FromArgb(45, 112, 179) : Theme.Info);
    private static string StatusText(string status) => status switch
    {
        "done" => I18n.T("Completed", "Завершено"), "running" => I18n.T("Running", "Выполняется"),
        "pending" => I18n.T("Pending", "Ожидание"), "error" or "failed" => I18n.T("Failed", "Ошибка"),
        "timeout" => I18n.T("Timed out", "Таймаут"), "denied" => I18n.T("Denied", "Отклонено"),
        "cancelled" => I18n.T("Cancelled", "Отменено"), _ => I18n.T("Status unknown", "Статус неизвестен"),
    };
    private static string TitleFor(ToastItem item, string status)
    {
        if (item.Event is null && item.Id == 0 && !string.IsNullOrWhiteSpace(item.Meta)) return item.Meta;
        if (status == "timeout") return I18n.T("Request timed out", "Время ожидания истекло");
        if (status == "denied") return I18n.T("Action denied", "Действие отклонено");
        if (status == "cancelled") return I18n.T("Action cancelled", "Действие отменено");
        if (status == "pending") return I18n.T("Waiting for execution", "Ожидание выполнения");
        string tool = item.Event?.Tool ?? (item.Kind == "terminal" ? "run_command" : "");
        bool running = IsRunning(status), failed = IsFailure(status);
        if (!running && !failed && status != "done") return I18n.T("Action status", "Статус действия");
        return tool switch
        {
            "run_command" => running ? I18n.T("Running command", "Выполняется команда") : failed ? I18n.T("Command failed", "Ошибка команды") : status == "done" ? I18n.T("Command completed", "Команда выполнена") : I18n.T("Command status", "Статус команды"),
            "read_file" => running ? I18n.T("Reading file", "Чтение файла") : failed ? I18n.T("Could not read file", "Не удалось прочитать файл") : I18n.T("File read", "Файл прочитан"),
            "write_file" => running ? I18n.T("Writing file", "Запись файла") : failed ? I18n.T("Could not write file", "Не удалось записать файл") : I18n.T("File written", "Файл записан"),
            "edit_file" => running ? I18n.T("Editing file", "Изменение файла") : failed ? I18n.T("Could not edit file", "Не удалось изменить файл") : I18n.T("File updated", "Файл изменён"),
            "take_screenshot" => running ? I18n.T("Taking screenshot", "Создание скриншота") : failed ? I18n.T("Screenshot failed", "Ошибка скриншота") : I18n.T("Screenshot ready", "Скриншот готов"),
            _ => running ? I18n.T("Action in progress", "Выполняется действие") : failed ? I18n.T("Action failed", "Ошибка действия") : status == "done" ? I18n.T("Action completed", "Действие выполнено") : I18n.T("Action status", "Статус действия"),
        };
    }
    private static string Age(ToastItem item)
    {
        var age = DateTimeOffset.Now - item.SeenAt;
        if (age.TotalSeconds < 60) return I18n.T("Just now", "Только что");
        if (age.TotalMinutes < 60) return I18n.Format("{0} min ago", "{0} мин назад", (int)age.TotalMinutes);
        if (age.TotalHours < 24) return I18n.Format("{0} h ago", "{0} ч назад", (int)age.TotalHours);
        return item.SeenAt.ToLocalTime().ToString("dd.MM HH:mm");
    }
    private void DrawStateTile(Graphics g, RectangleF r, string status)
    {
        Color c = StatusColor(status);
        if (status == "done")
        {
            Theme.DrawGlow(g, r, r.Height * .3f, Theme.WithAlpha(Theme.Success, Light ? 25 : 65), 5, 2);
            Theme.FillRoundGradient(g, r, r.Height * .3f, Color.FromArgb(67, 222, 149), Color.FromArgb(35, 184, 114));
            Theme.DrawIcon(g, IconKind.Check, RectangleF.Inflate(r, -r.Width * .23f, -r.Height * .23f), Color.FromArgb(6, 40, 26), 2.8f);
        }
        else
        {
            Theme.FillRoundGradient(g, r, r.Height * .3f, Theme.WithAlpha(c, Light ? 35 : 45), Theme.WithAlpha(c, 18));
            Theme.StrokeRound(g, r, r.Height * .3f, Theme.WithAlpha(c, 65));
            if (IsRunning(status))
            {
                using var pen = new Pen(c, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, RectangleF.Inflate(r, -r.Width * .24f, -r.Height * .24f), (float)(_wavePhase * 36 % 360), 265);
            }
            else Theme.DrawIcon(g, IsFailure(status) ? IconKind.Warning : IconKind.Info, RectangleF.Inflate(r, -r.Width * .24f, -r.Height * .24f), c, 2);
        }
    }
    private void IconButton(Graphics g, RectangleF r, IconKind icon, bool enabled = true)
    {
        if (enabled && r.Contains(_mouse)) Theme.FillRound(g, r, 8, Light ? Color.FromArgb(16, 40, 51, 76) : Theme.SurfaceHover);
        Theme.DrawIcon(g, icon, new RectangleF(r.X + (r.Width - 16) / 2, r.Y + (r.Height - 16) / 2, 16, 16), enabled ? Secondary : Theme.WithAlpha(Secondary, 85));
    }
    private float Badge(Graphics g, float x, float y)
    {
        string label = "×" + StackCount; float w = Theme.MeasureText(g, label, Ui(12, true)).Width + 18;
        var r = new RectangleF(x, y, w, 22);
        Theme.FillRound(g, r, 11, Light ? Color.FromArgb(20, Theme.Accent) : Theme.AccentSubtle);
        Theme.StrokeRound(g, r, 11, Theme.WithAlpha(Theme.Accent, 90));
        Theme.DrawText(g, label, Ui(12, true), Light ? Theme.AccentPressed : Theme.AccentText, r, StringAlignment.Center);
        return w;
    }
    private void DrawCollapsed(Graphics g, RectangleF r, string status)
    {
        float tileSize = Compact ? 30 : 40, tx = r.X + (Compact ? 54 : 72), top = r.Y + (Compact ? 8 : 14);
        if (!_suppressLiveParts) DrawStateTile(g, new RectangleF(r.X + (Compact ? 14 : 18), top + 2, tileSize, tileSize), status);
        string title = TitleFor(Newest, status);
        float textW = Math.Max(40, CloseRect.X - tx - 8), badgeW = StackCount > 1 ? Theme.MeasureText(g, "×" + StackCount, Ui(12, true)).Width + 18 : 0;
        float titleW = textW - (badgeW > 0 ? badgeW + 8 : 0);
        Theme.DrawText(g, title, Ui(Compact ? 13 : 14.5f, true), Primary, new RectangleF(tx, top, titleW, 22));
        if (badgeW > 0) Badge(g, tx + Math.Min(titleW, Theme.MeasureText(g, title, Ui(Compact ? 13 : 14.5f, true)).Width) + 8, top);
        IconButton(g, CloseRect, IconKind.Close);
        if (Compact)
        {
            string duration = FormatRealDuration(Newest.Duration, Newest.Started, status);
            Theme.DrawText(g, (duration.Length > 0 ? duration + " · " : "") + Newest.Headline, Ui(12), Secondary, new RectangleF(tx, top + 23, textW, 18));
            Theme.DrawText(g, TechnicalLine, Ui(11.5f), Tertiary, new RectangleF(tx, top + 44, textW, 18));
            if (IsRunning(status) && !_suppressLiveParts) UiAnimationClock.DrawMarquee(g, new RectangleF(r.X + 16, r.Bottom - 5, r.Width - 32, 2), StatusColor(status), _motion.Seconds);
            IconButton(g, ExpandRect, IconKind.ChevronDown); return;
        }
        DrawSummary(g, Newest.Headline, new RectangleF(tx, top + 27, textW, 22));
        var timingItem = IsRunning(status) ? _items.LastOrDefault(i => IsRunning(i.Status)) ?? Newest : Newest;
        string time = Age(Newest), dur = FormatRealDuration(timingItem.Duration, timingItem.Started, timingItem.Status);
        if (IsRunning(status)) time = StatusText(status);
        if (dur.Length > 0) time += " · " + dur;
        if (StackCount > 1 && _items.Any(i => IsFailure(i.Status))) time += I18n.Format(" · not completed: {0}", " · не выполнено: {0}", _items.Count(i => IsFailure(i.Status)));
        Theme.DrawText(g, time, Ui(12), IsRunning(status) ? StatusColor(status) : Tertiary, new RectangleF(tx, top + 75, textW, 20));
        Theme.DrawText(g, TechnicalLine, Ui(11.5f), Tertiary, new RectangleF(tx, top + 52, textW, 20));
        if (IsRunning(status) && !_suppressLiveParts) UiAnimationClock.DrawMarquee(g, new RectangleF(r.X + 18, r.Bottom - 5, r.Width - 36, 2), StatusColor(status), _motion.Seconds);
        IconButton(g, ExpandRect, IconKind.ChevronDown);
    }
    internal string TechnicalLine
    {
        get {
            var ev = Newest.Event;
            string tool = ev?.Tool ?? Newest.Meta;
            string target = ev is null ? "" : !string.IsNullOrWhiteSpace(ev.Cmd) ? ev.Cmd : !string.IsNullOrWhiteSpace(ev.Path) ? ev.Path : ev.Detail;
            return tool + (string.IsNullOrWhiteSpace(target) ? "" : " · " + target.Replace("\r", " ").Replace("\n", " "));
        }
    }
    private void DrawSummary(Graphics g, string summary, RectangleF r)
    {
        // Inline filename treatment from 05a. Use it only when the text really ends
        // in a short filename; never fabricate a successful output from an intent.
        var match = Regex.Match(summary ?? "", @"^(.*?:\s*)([^\\/:\r\n]{1,36}\.[A-Za-z0-9]{1,8})$");
        if (match.Success)
        {
            string prefix = match.Groups[1].Value, file = match.Groups[2].Value;
            float pw = Theme.MeasureText(g, prefix, Ui(13)).Width, fw = Theme.MeasureText(g, file, Mono).Width + 12;
            if (pw + fw <= r.Width)
            {
                Theme.DrawText(g, prefix, Ui(13), Secondary, new RectangleF(r.X, r.Y, pw, r.Height));
                var chip = new RectangleF(r.X + pw, r.Y, fw, 22);
                Theme.FillRound(g, chip, 6, Light ? Color.FromArgb(16, 20, 27, 42) : Color.FromArgb(16, 255, 255, 255));
                Theme.DrawText(g, file, Mono, Primary, chip, StringAlignment.Center); return;
            }
        }
        Theme.DrawText(g, summary, Ui(13), Secondary, r);
    }
    private void DrawExpanded(Graphics g, RectangleF r)
    {
        string status = Selected.Status;
        DrawStateTile(g, new RectangleF(r.X + 18, r.Y + 15, 40, 40), status);
        Theme.DrawText(g, TitleFor(Selected, status), Ui(14.5f, true), Primary, new RectangleF(r.X + 72, r.Y + 12, r.Width - 128, 24));
        float x = r.X + 72, y = r.Y + 39;
        string st = StatusText(status);
        Theme.DrawText(g, st, Ui(12), StatusColor(status), new RectangleF(x, y, r.Width - 132, 22));
        x += Theme.MeasureText(g, st, Ui(12)).Width + 10;
        if (StackCount > 1) x += Badge(g, x, y) + 10;
        string dur = FormatRealDuration(Selected.Duration, Selected.Started, status);
        Theme.DrawText(g, "· " + Age(Selected) + (dur.Length > 0 ? " · " + dur : ""), Ui(12), Tertiary, new RectangleF(x, y, Math.Max(1, r.Right - 45 - x), 22));
        IconButton(g, CloseRect, IconKind.Close);
        using var line = new Pen(LineColor);
        g.DrawLine(line, r.X, r.Y + 72, r.Right, r.Y + 72);
        var viewport = DetailViewport; var layout = GetDetailLayout(g, viewport.Width);
        _detailMaxScroll = Math.Max(0, (int)Math.Ceiling(layout.Height - viewport.Height));
        _detailScrollY = Math.Clamp(_detailScrollY, 0, _detailMaxScroll);
        var saved = g.Save(); g.SetClip(viewport, CombineMode.Intersect);
        y = viewport.Y - _detailScrollY;
        foreach (string text in layout.Head) { Theme.DrawText(g, text, Ui(14, true), Primary, new RectangleF(viewport.X, y, viewport.Width, 21)); y += 21; }
        y = viewport.Y - _detailScrollY + layout.HeadHeight + 14;
        Theme.DrawText(g, DetailLabel, Ui(12, true), Secondary, new RectangleF(viewport.X, y, viewport.Width, 18));
        var code = new RectangleF(viewport.X, viewport.Y - _detailScrollY + layout.CodeY, viewport.Width, layout.CodeHeight);
        Theme.FillRound(g, code, 10, Light ? Color.FromArgb(238, 241, 247) : Color.FromArgb(11, 13, 18));
        Theme.StrokeRound(g, code, 10, Light ? Color.FromArgb(25, 30, 40, 60) : Theme.BorderStrong);
        y = code.Y + 12;
        for (int i = 0; i < layout.Code.Length; i++)
        {
            string text = layout.Code[i];
            if (i == 0 && text.StartsWith("PS> "))
            {
                float pw = Theme.MeasureText(g, "PS> ", Mono).Width;
                Theme.DrawText(g, "PS> ", Mono, Light ? Theme.AccentPressed : Theme.AccentText, new RectangleF(code.X + 14, y, pw, 20.5f), ellipsis: false);
                Theme.DrawText(g, text[4..], Mono, Primary, new RectangleF(code.X + 14 + pw, y, code.Width - 28 - pw, 20.5f), ellipsis: false);
            }
            else Theme.DrawText(g, text, Mono, Primary, new RectangleF(code.X + 14, y, code.Width - 28, 20.5f), ellipsis: false);
            y += 20.5f;
        }
        y = code.Bottom + 12;
        foreach (string text in layout.Hint) { Theme.DrawText(g, text, Ui(12), Tertiary, new RectangleF(viewport.X, y, viewport.Width, 18)); y += 18; }
        g.Restore(saved);
        if (_detailMaxScroll > 0)
        {
            float thumb = Math.Max(24, viewport.Height * viewport.Height / layout.Height);
            Theme.FillRound(g, new RectangleF(r.Right - 9, viewport.Y, 3, viewport.Height), 1.5f, LineColor);
            Theme.FillRound(g, new RectangleF(r.Right - 9, viewport.Y + (viewport.Height - thumb) * _detailScrollY / _detailMaxScroll, 3, thumb), 1.5f, Secondary);
        }
        g.DrawLine(line, r.X, r.Bottom - 64, r.Right, r.Bottom - 64);
        if (StackCount > 1)
        {
            IconButton(g, PrevRect, IconKind.ChevronLeft, _selectedIndex > 0);
            Theme.DrawText(g, (_selectedIndex + 1) + " / " + StackCount, Ui(12), Secondary, new RectangleF(PrevRect.Right, PrevRect.Y, NextRect.X - PrevRect.Right, 32), StringAlignment.Center);
            IconButton(g, NextRect, IconKind.ChevronRight, _selectedIndex < StackCount - 1);
        }
        DetailButton(g, CopyRect, _copyFeedback.Length > 0 ? _copyFeedback : I18n.T("Copy text", "Копировать текст"), _copyFeedback.Length > 0 ? IconKind.Check : IconKind.Copy, CopyRect.Width < 80);
        DetailButton(g, CollapseRect, I18n.T("Collapse", "Свернуть"), IconKind.ChevronUp, CollapseRect.Width < 80);
    }
    private void DetailButton(Graphics g, RectangleF r, string text, IconKind icon, bool iconOnly = false)
    {
        bool hover = r.Contains(_mouse);
        Theme.FillRound(g, r, 9, Light ? (hover ? Color.FromArgb(225, 230, 240) : Color.FromArgb(234, 237, 244)) : hover ? Theme.SurfaceHover : Theme.SurfaceRaised);
        Theme.StrokeRound(g, r, 9, Light ? Color.FromArgb(28, 22, 30, 48) : Theme.BorderStrong);
        if (iconOnly) { Theme.DrawIcon(g, icon, new RectangleF(r.X + 12, r.Y + 8, 16, 16), Primary); return; }
        float tw = Theme.MeasureText(g, text, Ui(12.5f, true)).Width, total = Math.Min(r.Width - 16, tw + 24), x = r.X + (r.Width - total) / 2;
        Theme.DrawIcon(g, icon, new RectangleF(x, r.Y + 8, 16, 16), Primary);
        Theme.DrawText(g, text, Ui(12.5f, true), Primary, new RectangleF(x + 24, r.Y, Math.Max(1, r.Right - x - 30), 32));
    }
    public static string FormatRealDuration(int? durationMs, long? startedAtMs, string status)
    {
        long? msToFormat = null;
        if (status == "running")
        {
            if (startedAtMs.HasValue && startedAtMs.Value > 0)
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                msToFormat = Math.Max(0L, nowMs - startedAtMs.Value);
            }
        }
        else if (durationMs.HasValue && durationMs.Value >= 0)
        {
            msToFormat = durationMs.Value;
        }

        if (!msToFormat.HasValue)
            return "";

        long ms = msToFormat.Value;
        var culture = I18n.IsRu ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("en-US");
        if (ms < 1000)
        {
            double sec = Math.Round(ms / 1000.0, 1);
            return sec.ToString("0.0", culture) + I18n.T(" s", " с");
        }
        if (ms < 60000)
        {
            double sec = Math.Round(ms / 1000.0, 1);
            return sec.ToString("0.0", culture) + I18n.T(" s", " с");
        }
        long mins = ms / 60000;
        long remSec = (ms % 60000) / 1000;
        return I18n.Format("{0} min {1:00} s", "{0} мин {1:00} с", mins, remSec);
    }


}
