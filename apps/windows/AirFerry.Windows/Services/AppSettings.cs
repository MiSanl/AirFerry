using System.IO;

namespace AirFerry.Windows.Services;

/// <summary>
/// Single owner of <c>%AppData%\AirFerry\settings.json</c> — the .NET analogue
/// of Android's SharedPreferences. The file stays a tiny hand-rolled JSON object
/// (no System.Text.Json dependency, cross-end format parity). It currently holds
/// two keys: <c>default_redundancy</c> (int, 5–50) and <c>theme</c>
/// ("light" | "dark" | "system"). Values are cached in memory; every mutation
/// rewrites the whole file so one key never drops the other.
/// </summary>
public static class AppSettings
{
    public const int DefaultRedundancy = 5;
    public const string ThemeSystem = "system";
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AirFerry", "settings.json");

    private static bool _loaded;
    private static int _redundancy = DefaultRedundancy;
    private static string _theme = ThemeSystem;

    public static int Redundancy
    {
        get { EnsureLoaded(); return _redundancy; }
    }

    public static string Theme
    {
        get { EnsureLoaded(); return _theme; }
    }

    public static void SetRedundancy(int value)
    {
        EnsureLoaded();
        _redundancy = Math.Clamp(value, 5, 50);
        Save();
    }

    public static void SetTheme(string? value)
    {
        EnsureLoaded();
        _theme = NormalizeTheme(value);
        Save();
    }

    private static string NormalizeTheme(string? value) =>
        value is ThemeLight or ThemeDark ? value : ThemeSystem;

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }
            string json = File.ReadAllText(SettingsPath);
            // Minimal hand-rolled parse — deliberately no System.Text.Json here.
            _redundancy = Math.Clamp(ParseInt(json, "default_redundancy", DefaultRedundancy), 5, 50);
            _theme = NormalizeTheme(ParseString(json, "theme"));
        }
        catch { /* fall through to defaults */ }
    }

    private static int ParseInt(string json, string key, int fallback)
    {
        int idx = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (idx < 0)
        {
            return fallback;
        }
        int colon = json.IndexOf(':', idx);
        if (colon < 0)
        {
            return fallback;
        }
        int start = colon + 1;
        while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
        int end = start;
        while (end < json.Length && char.IsDigit(json[end])) end++;
        return int.TryParse(json.AsSpan(start, end - start), out int v) ? v : fallback;
    }

    private static string? ParseString(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }
        int colon = json.IndexOf(':', idx);
        if (colon < 0)
        {
            return null;
        }
        int start = colon + 1;
        while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
        if (start >= json.Length || json[start] != '"')
        {
            return null;
        }
        int end = json.IndexOf('"', start + 1);
        return end < 0 ? null : json.Substring(start + 1, end - start - 1);
    }

    private static void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(SettingsPath,
                $"{{\"default_redundancy\":{_redundancy},\"theme\":\"{_theme}\"}}");
        }
        catch { /* settings are best-effort; never block the UI */ }
    }
}
