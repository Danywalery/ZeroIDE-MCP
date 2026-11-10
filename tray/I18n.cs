namespace ZeroIDE.Mcp;

internal static class I18n
{
    private static string _lang = "en";

    public static string CurrentLanguage
    {
        get => _lang;
        set => _lang = string.Equals(value, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
    }

    public static bool IsRu => string.Equals(_lang, "ru", StringComparison.OrdinalIgnoreCase);

    public static string T(string en, string ru) => IsRu ? ru : en;

    public static string Format(string enFmt, string ruFmt, params object?[] args)
    {
        string fmt = IsRu ? ruFmt : enFmt;
        return string.Format(fmt, args);
    }
}
