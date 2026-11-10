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

    [JsonPropertyName("confinement")]
    public ConfinementConfig Confinement { get; set; } = new();

    public static Dictionary<string, ToolPermissionEntry> CreateDefaultTools() => new()
    {
        ["read_file"] = new() { Mode = "allow" },
        ["list_directory"] = new() { Mode = "allow" },
        ["search_files"] = new() { Mode = "allow" },
        ["job_status"] = new() { Mode = "allow" },
        ["job_output"] = new() { Mode = "allow" },
        ["audit_log"] = new() { Mode = "allow" },
        ["journal_log"] = new() { Mode = "allow" },
        ["journal_entry"] = new() { Mode = "allow" },
        ["write_file"] = new() { Mode = "ask" },
        ["edit_file"] = new() { Mode = "ask" },
        ["run_command"] = new() { Mode = "ask" },
        ["take_screenshot"] = new() { Mode = "ask" },
        ["user_message"] = new() { Mode = "allow" },
        ["task_status"] = new() { Mode = "allow" },
    };
}

public sealed class ToolPermissionEntry
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "ask";
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
        new("list_directory", "Viewing folder contents", "Просмотр содержимого папок", "read", "Read", "Чтение", "Shows a list of files and subfolders with sizes", "Показывает список файлов и подпапок с размерами", false),
        new("search_files", "Text search across files", "Поиск текста по файлам", "read", "Read", "Чтение", "Searches for strings or regular expressions in files", "Ищет строки или регулярные выражения в файлах", false),
        new("job_status", "Background job status", "Статус фоновой задачи", "execution", "Execution", "Выполнение", "Checks the state of a previously started background command", "Проверяет состояние ранее запущенной фоновой команды", false),
        new("job_output", "Background job output", "Вывод фоновой задачи", "execution", "Execution", "Выполнение", "Reads stdout/stderr of a background job", "Читает stdout/stderr фоновой задачи", false),
        new("write_file", "Creating and overwriting files", "Создание и перезапись файлов", "file_writes", "File writes", "Запись файлов", "Atomically writes a new or existing file (with a .bak copy)", "Атомарно записывает новый или существующий файл (с .bak копией)", true),
        new("edit_file", "Precise file edits", "Точечная правка файлов", "file_writes", "File writes", "Запись файлов", "Replaces an exact text fragment inside a file", "Заменяет точный фрагмент текста внутри файла", true),
        new("run_command", "Running PowerShell commands", "Выполнение команд PowerShell", "execution", "Execution", "Выполнение", "Runs any PowerShell commands and scripts on your behalf", "Запускает любые команды и скрипты PowerShell от вашего имени", true),
        new("audit_log", "Reading the audit log", "Чтение журнала аудита", "journal", "Journal", "Журнал", "Shows the history of tool calls and decisions", "Показывает историю вызовов инструментов и решений", false),
        new("journal_log", "Reading the work journal", "Чтение рабочего журнала", "journal", "Journal", "Журнал", "Reads the agent entries about completed work stages", "Читает записи агента о проделанных этапах работы", false),
        new("journal_entry", "Writing a work outcome to the journal", "Запись итога работы в журнал", "journal", "Journal", "Журнал", "Adds a note about a completed step to journal.jsonl", "Добавляет отметку о завершённом шаге в journal.jsonl", false),
        new("take_screenshot", "Screenshot", "Скриншот", "read", "Read", "Чтение", "Captures the primary screen as a JPEG image; the image leaves the PC and needs its own approval card unless granted", "Снимает основной экран в JPEG; изображение покидает ПК, для него отдельная карточка подтверждения, если нет гранта", true),
        new("user_message", "Agent message", "Сообщение агента", "journal", "Journal", "Журнал", "Shows the agent's message in the tray (display-only, never asks for approval)", "Показывает сообщение агента в трее (только показ, подтверждения не требует)", false),
    };

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string GetPermissionsPath() =>
        Path.Combine(AppContext.BaseDirectory, "permissions.json");

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

    public static void ApplyPreset(PermissionsConfig cfg, string preset)
    {
        cfg.Tools ??= PermissionsConfig.CreateDefaultTools();
        foreach (var t in AllTools)
        {
            string mode = preset switch
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
        string lpCommandLine,
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

    public static void OpenFolder(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        try
        {
            string fullPath = Path.GetFullPath(rawPath.Trim().Trim('"'));
            Directory.CreateDirectory(fullPath);
            if (!LaunchOnDefaultDesktop($"explorer.exe \"{fullPath}\"", fullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
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
            if (CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workingDir, ref si, out var pi))
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
