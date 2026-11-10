namespace ZeroIDE.Mcp;

// Where "ZeroIDE-MCP.exe --preview" writes its PNG previews of the UI.
// Order: --preview-out=<folder>, then the ZEROIDE_PREVIEW_DIR environment
// variable, then <checkout>\output\charts when the exe runs from inside a
// source checkout (found by tray\ZeroIDE-MCP.csproj), otherwise
// %TEMP%\zeroide-previews. Nothing machine- or person-specific is compiled in.
internal static class PreviewOutput
{
    public static string Resolve(string[] args)
    {
        const string flag = "--preview-out=";
        foreach (string a in args)
        {
            if (!a.StartsWith(flag, StringComparison.OrdinalIgnoreCase)) continue;
            string value = a.Substring(flag.Length).Trim().Trim('"');
            if (value.Length > 0) return Path.GetFullPath(value);
        }
        string? fromEnv = Environment.GetEnvironmentVariable("ZEROIDE_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv.Trim());
        string? checkout = FindCheckout(AppContext.BaseDirectory);
        return checkout != null
            ? Path.Combine(checkout, "output", "charts")
            : Path.Combine(Path.GetTempPath(), "zeroide-previews");
    }

    private static string? FindCheckout(string start)
    {
        try
        {
            DirectoryInfo? dir = new DirectoryInfo(start);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tray", "ZeroIDE-MCP.csproj"))) return dir.FullName;
            }
        }
        catch (Exception) { }
        return null;
    }
}
