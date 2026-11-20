using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeroIDE.Mcp;

public sealed class PermissionsConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("defaultMode")]
    public string DefaultMode { get; set; } = "ask";

    [JsonPropertyName("approvalTimeoutSec")]
    public int ApprovalTimeoutSec { get; set; } = 60;

    [JsonPropertyName("tools")]
    public Dictionary<string, ToolPermissionEntry> Tools { get; set; } = CreateDefaultTools();

    [JsonPropertyName("redFlags")]
    public RedFlagsConfig RedFlags { get; set; } = new();

    [JsonPropertyName("riskGate")]
    public RiskGateConfig RiskGate { get; set; } = new();

    [JsonPropertyName("confinement")]
    public ConfinementConfig Confinement { get; set; } = new();

    public static Dictionary<string, ToolPermissionEntry> CreateDefaultTools() => new()
    {
        ["read_file"] = new() { Mode = "allow" },
        ["read_image"] = new() { Mode = "ask" },
        ["list_directory"] = new() { Mode = "allow" },
        ["search_files"] = new() { Mode = "allow" },
        ["system_info"] = new() { Mode = "allow" },
        ["job_status"] = new() { Mode = "allow" },
        ["job_output"] = new() { Mode = "allow" },
        ["audit_log"] = new() { Mode = "allow" },
        ["journal_log"] = new() { Mode = "allow" },
        ["journal_entry"] = new() { Mode = "allow" },
        ["write_file"] = new() { Mode = "ask" },
        ["edit_file"] = new() { Mode = "ask" },
        ["file_ops"] = new() { Mode = "ask" },
        ["run_command"] = new() { Mode = "ask" },
        ["take_screenshot"] = new() { Mode = "ask" },
        ["user_message"] = new() { Mode = "allow" },
        ["task_status"] = new() { Mode = "allow" },
        ["agent_message"] = new() { Mode = "allow" },
        ["await_owner"] = new() { Mode = "deny" },
    };
}

public sealed class ToolPermissionEntry
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "ask";
}

public sealed class RiskGateConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("askAtOrAbove")]
    public string AskAtOrAbove { get; set; } = "high";

    [JsonPropertyName("grantsNeverCover")]
    public string GrantsNeverCover { get; set; } = "critical";

    [JsonPropertyName("sensitiveReads")]
    public bool SensitiveReads { get; set; } = true;
}

public sealed class RedFlagsConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("elevateToAsk")]
    public bool ElevateToAsk { get; set; } = true;

    [JsonPropertyName("patterns")]
    public List<string> Patterns { get; set; } = new()
    {
        @"(?i)\b(Remove-Item|ri|rm|del|erase)\b.*(-Recurse|-Force|/s|/q)",
        @"(?i)\b(Format-Volume|Clear-Disk|Initialize-Disk|diskpart|cipher\s+/w)\b",
        @"(?i)\b(Stop-Computer|Restart-Computer|shutdown|logoff)\b",
        @"(?i)\b(reg\s+delete|Remove-ItemProperty)\b.*HKLM",
        @"(?i)\b(Invoke-Expression|iex)\b.*(DownloadString|Invoke-WebRequest|curl|iwr)",
        @"(?i)\b(Set-MpPreference|Add-MpPreference).*Disable",
        @"(?i)\b(net\s+user|New-LocalUser|Add-LocalGroupMember)\b",
    };
}

public sealed class ConfinementConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("root")]
    public string? Root { get; set; } = null;
}

public sealed record ToolDescriptor(
    string Name,
    string TitleEn,
    string RawTitleRu,
    string CategoryKey,
    string CategoryEn,
    string CategoryRu,
    string DescriptionEn,
    string RawDescriptionRu,
    bool IsMutating
)
{
    public string TitleRu => RawTitleRu;
    public string Title => I18n.IsRu ? TitleRu : TitleEn;
    public string Category => I18n.IsRu ? CategoryRu : CategoryEn;
    public string DescriptionRu => RawDescriptionRu;
    public string Description => I18n.IsRu ? DescriptionRu : DescriptionEn;
}

public static class PermissionsStore
{
    public static readonly IReadOnlyList<ToolDescriptor> AllTools = new List<ToolDescriptor>
    {
        new("read_file", "Reading files", "Чтение файлов", "read", "Read", "Чтение", "Reads text files in the project folder and on disk", "Читает текстовые файлы в папке проекта и на диске", false),
        new("read_image", "Viewing images", "Просмотр изображений", "read", "Read", "Чтение", "Reads an image file (PNG, JPEG, WebP, GIF, BMP, ICO) and sends it to the AI; large images are downscaled on this PC", "Открывает файл картинки (PNG, JPEG, WebP, GIF, BMP, ICO) и передаёт его агенту; большие изображения сжимаются на ПК", true),
        new("list_directory", "Viewing folder contents", "Просмотр содержимого папок", "read", "Read", "Чтение", "Shows a list of files and subfolders with sizes", "Показывает список файлов и подпапок с размерами", false),
        new("search_files", "Text search across files", "Поиск текста по файлам", "read", "Read", "Чтение", "Searches for strings or regular expressions in files", "Ищет строки или регулярные выражения в файлах", false),
        new("system_info", "System and ZeroIDE info", "Сведения о системе", "read", "Read", "Чтение", "Reports Windows version, CPU, RAM, disks, displays, installed developer tools and active ZeroIDE setup", "Показывает версию Windows, CPU, ОЗУ, диски, экраны, инструменты разработчика и текущие настройки ZeroIDE", false),
        new("job_status", "Background job status", "Статус фоновой задачи", "execution", "Execution", "Выполнение", "Checks the state of a previously started background command", "Проверяет состояние ранее запущенной фоновой команды", false),
        new("job_output", "Background job output", "Вывод фоновой задачи", "execution", "Execution", "Выполнение", "Reads stdout/stderr of a background job", "Читает stdout/stderr фоновой задачи", false),
        new("write_file", "Creating and overwriting files", "Создание и перезапись файлов", "file_writes", "File writes", "Запись файлов", "Atomically writes a file and keeps the previous version in ZeroIDE history for 30 days", "Атомарно записывает файл и сохраняет прошлую версию в истории ZeroIDE на 30 дней", true),
        new("edit_file", "Precise file edits", "Точечная правка файлов", "file_writes", "File writes", "Запись файлов", "Replaces one or several exact text fragments inside a file (reversible via history)", "Заменяет один или несколько фрагментов текста внутри файла (обратимо через историю)", true),
        new("file_ops", "Copy, move, delete & folders", "Операции с файлами и папками", "file_writes", "File writes", "Запись файлов", "Copies, moves, deletes (reversibly to ZeroIDE history) and creates folders without running PowerShell", "Копирует, переносит, удаляет (в историю ZeroIDE с отменой) и создаёт папки без запуска PowerShell", true),
        new("run_command", "Running PowerShell commands", "Выполнение команд PowerShell", "execution", "Execution", "Выполнение", "Runs any PowerShell commands and scripts on your behalf", "Запускает любые команды и скрипты PowerShell от вашего имени", true),
        new("audit_log", "Reading the audit log", "Чтение журнала аудита", "journal", "Journal", "Журнал", "Shows the history of tool calls and decisions", "Показывает историю вызовов инструментов и решений", false),
        new("journal_log", "Reading the work journal", "Чтение рабочего журнала", "journal", "Journal", "Журнал", "Reads the agent entries about completed work stages", "Читает записи агента о проделанных этапах работы", false),
        new("journal_entry", "Writing a work outcome to the journal", "Запись итога работы в журнал", "journal", "Journal", "Журнал", "Adds a note about a completed step to journal.jsonl", "Добавляет отметку о завершённом шаге в journal.jsonl", false),
        new("take_screenshot", "Screenshot", "Скриншот", "read", "Read", "Чтение", "Captures the primary screen as a JPEG image; the image leaves the PC and needs its own approval card unless granted", "Снимает основной экран в JPEG; изображение покидает ПК, для него отдельная карточка подтверждения, если нет гранта", true),
        new("user_message", "Agent message", "Сообщение агента", "journal", "Journal", "Журнал", "Shows the agent's message in the tray (display-only, never asks for approval)", "Показывает сообщение агента в трее (только показ, подтверждения не требует)", false),
        new("task_status", "Current task status", "Статус текущей задачи", "journal", "Journal", "Журнал", "Updates the current task card and progress steps in the tray", "Обновляет карточку текущей задачи и этапы работы в трее", false),
        new("agent_message", "Agent messenger", "Мессенджер агентов", "journal", "Journal", "Журнал", "Lets another connected agent send live messages to an active agent", "Позволяет другому агенту отправлять живые сообщения активному агенту", false),
        new("await_owner", "Keep agent connected (5 min)", "Не разрывать связь агента (5 мин)", "journal", "Journal", "Журнал", "Keeps the agent's MCP connection open for 5 min after finishing work so you can test the result on your PC and send quick follow-up tweaks directly from the tray chat in the same context", "Позволяет не разрывать соединение агента с MCP на 5 минут после завершения задачи — удобно, чтобы проверить результат на ПК и сразу отправить правки из трея в тот же контекст, не возвращаясь в чат", false),
    };

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object _cacheLock = new();
    private static string _cachedPath = "";
    private static DateTime _cachedMtime = DateTime.MinValue;
    private static PermissionsConfig? _cachedPerms;

    public static string GetPermissionsPath() =>
        Path.Combine(AppContext.BaseDirectory, "permissions.json");

    /// <summary>Fast cached check used by the tray UI; never creates permissions.json on disk.</summary>
    public static bool IsToolEnabled(string tool)
    {
        string path = GetPermissionsPath();
        try
        {
            if (!File.Exists(path))
            {
                var defaults = PermissionsConfig.CreateDefaultTools();
                return !defaults.TryGetValue(tool, out var defEntry) || !string.Equals(defEntry.Mode, "deny", StringComparison.OrdinalIgnoreCase);
            }
            DateTime mtime = File.GetLastWriteTimeUtc(path);
            lock (_cacheLock)
            {
                if (_cachedPerms == null || _cachedPath != path || _cachedMtime != mtime)
                {
                    string raw = File.ReadAllText(path, Utf8NoBom);
                    var parsed = JsonSerializer.Deserialize<PermissionsConfig>(raw) ?? new PermissionsConfig();
                    parsed.Tools ??= PermissionsConfig.CreateDefaultTools();
                    foreach (var kv in PermissionsConfig.CreateDefaultTools())
                    {
                        if (!parsed.Tools.ContainsKey(kv.Key)) parsed.Tools[kv.Key] = kv.Value;
                    }
                    _cachedPerms = parsed;
                    _cachedPath = path;
                    _cachedMtime = mtime;
                }
                return !string.Equals(GetToolMode(_cachedPerms, tool), "deny", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            var defaults = PermissionsConfig.CreateDefaultTools();
            return !defaults.TryGetValue(tool, out var defEntry) || !string.Equals(defEntry.Mode, "deny", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static PermissionsConfig Load()
    {
        string path = GetPermissionsPath();
        try
        {
            if (File.Exists(path))
            {
                string raw = File.ReadAllText(path, Utf8NoBom);
                var cfg = JsonSerializer.Deserialize<PermissionsConfig>(raw);
                if (cfg != null)
                {
                    cfg.Tools ??= PermissionsConfig.CreateDefaultTools();
                    foreach (var kv in PermissionsConfig.CreateDefaultTools())
                    {
                        if (!cfg.Tools.ContainsKey(kv.Key))
                            cfg.Tools[kv.Key] = kv.Value;
                    }
                    cfg.Confinement ??= new ConfinementConfig();
                    cfg.RiskGate ??= new RiskGateConfig();
                    if (cfg.ApprovalTimeoutSec <= 0) cfg.ApprovalTimeoutSec = 60;
                    return cfg;
                }
            }
        }
        catch { /* fallback to default */ }

        var def = new PermissionsConfig();
        Save(def);
        return def;
    }

    public static void Save(PermissionsConfig cfg)
    {
        string path = GetPermissionsPath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..6];
            string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json, Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
            lock (_cacheLock)
            {
                _cachedPerms = cfg;
                _cachedPath = path;
                _cachedMtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
        }
        catch { /* ignore write errors */ }
    }

    public static string GetToolMode(PermissionsConfig cfg, string tool)
    {
        if (cfg.Tools != null && cfg.Tools.TryGetValue(tool, out var entry) && !string.IsNullOrWhiteSpace(entry.Mode))
            return entry.Mode;
        return string.IsNullOrWhiteSpace(cfg.DefaultMode) ? "ask" : cfg.DefaultMode;
    }

    public static void SetToolMode(PermissionsConfig cfg, string tool, string mode)
    {
        cfg.Tools ??= PermissionsConfig.CreateDefaultTools();
        if (!cfg.Tools.TryGetValue(tool, out var entry))
        {
            entry = new ToolPermissionEntry();
            cfg.Tools[tool] = entry;
        }
        entry.Mode = mode;
        Save(cfg);
    }

    public static bool IsHighRiskFilterEnabled(PermissionsConfig? cfg)
    {
        if (cfg == null) return true;
        bool rfOn = cfg.RedFlags?.Enabled != false && cfg.RedFlags?.ElevateToAsk != false;
        var g = cfg.RiskGate;
        bool gateHigh = g == null || (g.Enabled
            && !string.Equals(g.AskAtOrAbove, "critical", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(g.AskAtOrAbove, "none", StringComparison.OrdinalIgnoreCase));
        return rfOn && gateHigh;
    }

    public static bool IsCriticalGuardEnabled(PermissionsConfig? cfg)
    {
        if (cfg?.RiskGate == null) return true;
        if (!cfg.RiskGate.Enabled) return false;
        return !string.Equals(cfg.RiskGate.GrantsNeverCover, "none", StringComparison.OrdinalIgnoreCase);
    }

    public static void ApplyRiskToggles(PermissionsConfig cfg, bool highOn, bool criticalOn, bool save = true)
    {
        cfg.RedFlags ??= new RedFlagsConfig();
        cfg.RiskGate ??= new RiskGateConfig();
        cfg.RedFlags.Enabled = highOn;
        cfg.RedFlags.ElevateToAsk = highOn;
        if (highOn && criticalOn)
        {
            cfg.RiskGate.Enabled = true;
            cfg.RiskGate.AskAtOrAbove = "high";
            cfg.RiskGate.GrantsNeverCover = "critical";
            cfg.RiskGate.SensitiveReads = true;
        }
        else if (!highOn && criticalOn)
        {
            cfg.RiskGate.Enabled = true;
            cfg.RiskGate.AskAtOrAbove = "critical";
            cfg.RiskGate.GrantsNeverCover = "critical";
            cfg.RiskGate.SensitiveReads = false;
        }
        else if (highOn && !criticalOn)
        {
            cfg.RiskGate.Enabled = true;
            cfg.RiskGate.AskAtOrAbove = "high";
            cfg.RiskGate.GrantsNeverCover = "none";
            cfg.RiskGate.SensitiveReads = true;
        }
        else
        {
            cfg.RiskGate.Enabled = false;
            cfg.RiskGate.AskAtOrAbove = "none";
            cfg.RiskGate.GrantsNeverCover = "none";
            cfg.RiskGate.SensitiveReads = false;
        }
        if (save) Save(cfg);
    }

    public static void ApplyPreset(PermissionsConfig cfg, string preset)
    {
        cfg.Tools ??= PermissionsConfig.CreateDefaultTools();
        string prevAwait = cfg.Tools.TryGetValue("await_owner", out var ae) && !string.IsNullOrWhiteSpace(ae.Mode) ? ae.Mode : "deny";
        foreach (var t in AllTools)
        {
            string mode = t.Name == "await_owner"
                ? prevAwait
                : preset switch
                {
                    "cautious" => (t.IsMutating || t.Name == "journal_entry") ? "ask" : "allow",
                    "trusting" => t.Name == "run_command" ? "ask" : "allow",
                    _ => t.IsMutating ? "ask" : "allow", // balanced
                };
            cfg.Tools[t.Name] = new ToolPermissionEntry { Mode = mode };
        }
        Save(cfg);
    }

    public static string DetectActivePreset(PermissionsConfig cfg)
    {
        bool allReadAllow = true;
        foreach (var t in AllTools)
        {
            if (t.Name == "await_owner") continue;
            if (!t.IsMutating && t.Name != "journal_entry" && GetToolMode(cfg, t.Name) != "allow")
                allReadAllow = false;
        }
        if (!allReadAllow) return "custom";

        string writeMode = GetToolMode(cfg, "write_file");
        string editMode = GetToolMode(cfg, "edit_file");
        string runMode = GetToolMode(cfg, "run_command");
        string journalEntryMode = GetToolMode(cfg, "journal_entry");

        if (writeMode == "ask" && editMode == "ask" && runMode == "ask" && journalEntryMode == "ask")
            return "cautious";
        if (writeMode == "ask" && editMode == "ask" && runMode == "ask" && journalEntryMode == "allow")
            return "balanced";
        if (writeMode == "allow" && editMode == "allow" && runMode == "ask" && journalEntryMode == "allow")
            return "trusting";
        return "custom";
    }
}

public static class DesktopLauncher
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool IsSafeHttpUrl(string? rawUrl, out string safeUrl)
    {
        safeUrl = "";
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;
        string trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        safeUrl = uri.AbsoluteUri;
        return true;
    }

    public static bool OpenUrl(string? rawUrl)
    {
        if (!IsSafeHttpUrl(rawUrl, out string safeUrl)) return false;
        string explorer = ResolveExplorerExe();
        try
        {
            if (LaunchOnDefaultDesktop($"\"{explorer}\" \"{safeUrl}\"", null)) return true;
        }
        catch { }
        try
        {
            Process.Start(new ProcessStartInfo(safeUrl) { UseShellExecute = true });
            return true;
        }
        catch { }
        try
        {
            Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { safeUrl }, UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }

    public static bool OpenExistingFolder(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        try
        {
            string fullPath = Path.GetFullPath(rawPath.Trim().Trim('"')).Replace('/', '\\');
            if (!Directory.Exists(fullPath)) return false;
            string explorer = ResolveExplorerExe();
            if (LaunchOnDefaultDesktop($"\"{explorer}\" \"{fullPath}\"", fullPath)) return true;
            try
            {
                Process.Start(new ProcessStartInfo(explorer, $"\"{fullPath}\"") { UseShellExecute = true });
                return true;
            }
            catch { }
            try
            {
                Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { fullPath }, UseShellExecute = false });
                return true;
            }
            catch { }
        }
        catch { }
        return false;
    }

    public static bool OpenDocument(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        try
        {
            string fullPath = Path.GetFullPath(rawPath.Trim().Trim('"')).Replace('/', '\\');
            if (!File.Exists(fullPath)) return false;
            string? workDir = Path.GetDirectoryName(fullPath);
            string explorer = ResolveExplorerExe();
            if (LaunchOnDefaultDesktop($"\"{explorer}\" \"{fullPath}\"", workDir)) return true;
            try
            {
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true, WorkingDirectory = workDir ?? "" });
                return true;
            }
            catch { }
        }
        catch { }
        return false;
    }

    private static string ResolveExplorerExe()
    {
        try
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(winDir))
            {
                string candidate = Path.Combine(winDir, "explorer.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return "explorer.exe";
    }

    internal static (string Mode, string Target) ResolveRevealTarget(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return ("none", "");
        try
        {
            string fullPath = Path.GetFullPath(rawPath.Trim().Trim('"')).Replace('/', '\\');
            string root = Path.GetPathRoot(fullPath) ?? "";
            if (fullPath.Length > root.Length) fullPath = fullPath.TrimEnd('\\');

            if (File.Exists(fullPath)) return ("select", fullPath);
            if (Directory.Exists(fullPath)) return ("folder", fullPath);

            string? dir = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(dir)) return ("folder", dir);
                string? parent = Path.GetDirectoryName(dir);
                if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
                dir = parent;
            }
        }
        catch { }
        return ("none", "");
    }

    public static bool RevealInFolder(string? rawPath)
    {
        var (mode, target) = ResolveRevealTarget(rawPath);
        if (mode == "none" || string.IsNullOrEmpty(target)) return false;

        string explorer = ResolveExplorerExe();
        try
        {
            if (mode == "select")
            {
                string? workDir = Path.GetDirectoryName(target);
                string args = $"/select,\"{target}\"";
                if (LaunchOnDefaultDesktop($"\"{explorer}\" {args}", workDir)) return true;
                try
                {
                    Process.Start(new ProcessStartInfo(explorer, args) { UseShellExecute = true });
                    return true;
                }
                catch { }
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false });
                    return true;
                }
                catch { }
                return false;
            }

            if (LaunchOnDefaultDesktop($"\"{explorer}\" \"{target}\"", target)) return true;
            try
            {
                Process.Start(new ProcessStartInfo(explorer, $"\"{target}\"") { UseShellExecute = true });
                return true;
            }
            catch { }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { target }, UseShellExecute = false });
                return true;
            }
            catch { }
        }
        catch { }
        return false;
    }

    public static void OpenFolder(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        try
        {
            string fullPath = Path.GetFullPath(rawPath.Trim().Trim('"'));
            Directory.CreateDirectory(fullPath);
            string explorer = ResolveExplorerExe();
            if (!LaunchOnDefaultDesktop($"\"{explorer}\" \"{fullPath}\"", fullPath))
            {
                Process.Start(new ProcessStartInfo(explorer, $"\"{fullPath}\"") { UseShellExecute = true });
            }
        }
        catch { /* ignore */ }
    }

    public static void OpenFile(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        try
        {
            string fullPath = Path.GetFullPath(rawPath.Trim().Trim('"'));
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(fullPath)) File.WriteAllText(fullPath, "");
            if (!LaunchOnDefaultDesktop($"notepad.exe \"{fullPath}\"", dir))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{fullPath}\"") { UseShellExecute = true });
            }
        }
        catch { /* ignore */ }
    }

    private static bool LaunchOnDefaultDesktop(string cmdLine, string? workingDir)
    {
        try
        {
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = 0x00000001, // STARTF_USESHOWWINDOW
                wShowWindow = 1,      // SW_SHOWNORMAL
            };
            var cmd = new System.Text.StringBuilder(cmdLine);
            if (CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workingDir, ref si, out var pi))
            {
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                return true;
            }
        }
        catch { }
        return false;
    }
}
