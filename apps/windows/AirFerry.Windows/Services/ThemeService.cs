using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AirFerry.Windows.Services;

/// <summary>
/// Applies the appearance preference from <see cref="AppSettings"/> ("light" /
/// "dark" / "system") to the whole app: swaps the WPF-UI theme dictionary,
/// re-points the AirFerry semantic-token dictionary, and re-applies the brand
/// accent (#2563EB — shared with the Android and web hosts). In "system" mode a
/// <see cref="SystemThemeWatcher"/> follows OS theme changes live.
/// </summary>
public static class ThemeService
{
    private static readonly Color BrandAccent = Color.FromRgb(0x25, 0x63, 0xEB);
    private static Window? _watchedWindow;
    private static bool _changedHooked;

    /// <summary>
    /// Apply the persisted preference. Call once before the main window is
    /// shown (no visible light/dark flash), then again whenever the user
    /// changes the appearance setting.
    /// </summary>
    public static void ApplyPreference(string preference, Window? windowToWatch)
    {
        if (!_changedHooked)
        {
            // Every theme application (SystemThemeWatcher in "system" mode
            // included) raises Changed — the hook that keeps the brand accent
            // and the DesignTokens semantic brushes in sync when the OS flips
            // the theme, since WPF-UI itself only swaps its own dictionaries
            // there. Idempotent: applies the *current* theme each time.
            ApplicationThemeManager.Changed += (_, _) => ApplyAccentAndTokens();
            _changedHooked = true;
        }

        if (_watchedWindow is not null)
        {
            // UnWatch is idempotent enough for our purposes; always detach
            // before (re-)applying so a repeated "system" selection or a
            // replacement window never stacks watchers.
            SystemThemeWatcher.UnWatch(_watchedWindow);
            _watchedWindow = null;
        }

        switch (preference)
        {
            case AppSettings.ThemeLight:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None, updateAccent: false);
                break;
            case AppSettings.ThemeDark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, updateAccent: false);
                break;
            default:
                // "system": Watch applies the current OS theme and keeps
                // following OS changes until UnWatch. Without a window (very
                // early startup) fall back to a one-shot apply.
                if (windowToWatch is not null)
                {
                    SystemThemeWatcher.Watch(windowToWatch, WindowBackdropType.None, updateAccents: false);
                    _watchedWindow = windowToWatch;
                }
                else
                {
                    ApplicationThemeManager.ApplySystemTheme();
                }
                break;
        }

        ApplyAccentAndTokens();
    }

    /// <summary>
    /// Re-apply the brand accent for the current effective theme (WPF-UI
    /// derives different accent shades per theme) and swap the semantic-token
    /// dictionary. Called both after an explicit preference apply and from the
    /// <see cref="ApplicationThemeManager.Changed"/> hook, so "system" mode
    /// keeps Success/Error/Warning brushes in sync with live OS theme flips.
    /// </summary>
    private static void ApplyAccentAndTokens()
    {
        ApplicationTheme effective = ApplicationThemeManager.GetAppTheme();
        ApplicationAccentColorManager.Apply(BrandAccent, effective, false, false);
        SwapTokenDictionary(effective == ApplicationTheme.Dark);
    }

    /// <summary>Current effective theme after the last apply.</summary>
    public static bool IsDarkEffective =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    private static void SwapTokenDictionary(bool dark)
    {
        ResourceDictionary? appResources = Application.Current?.Resources;
        if (appResources is null)
        {
            return;
        }
        foreach (ResourceDictionary dict in appResources.MergedDictionaries)
        {
            if (dict.Source?.OriginalString.Contains("DesignTokens") == true)
            {
                string name = dark ? "DesignTokens.Dark.xaml" : "DesignTokens.Light.xaml";
                if (!dict.Source.OriginalString.EndsWith(name, StringComparison.Ordinal))
                {
                    dict.Source = new Uri($"Themes/{name}", UriKind.Relative);
                }
                return;
            }
        }
    }
}
