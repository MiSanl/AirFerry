using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AirFerry.Windows.Services;

namespace AirFerry.Windows.Views;

/// <summary>
/// Settings page — mirrors Android's <c>SettingsActivity</c>: a "default
/// redundancy" slider and an appearance selector (light / dark / follow
/// system), both persisted via <see cref="AppSettings"/>
/// (%AppData%\AirFerry\settings.json, the .NET analogue of SharedPreferences),
/// plus the version read from the assembly (the single source of truth — the
/// csproj <c>&lt;Version&gt;</c>).
/// </summary>
public partial class SettingsView : Page
{
    private bool _populating;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Populate();
    }

    private void Populate()
    {
        _populating = true;
        try
        {
            int redundancy = AppSettings.Redundancy;
            RedundancySlider.Value = redundancy;
            RedundancyText.Text = $"{redundancy}%";

            ThemeComboBox.SelectedIndex = AppSettings.Theme switch
            {
                AppSettings.ThemeLight => 1,
                AppSettings.ThemeDark => 2,
                _ => 0,
            };

            // Read version from the assembly (the csproj <Version>).
            Version? ver = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = ver is not null ? $"版本 {ver.Major}.{ver.Minor}.{ver.Build}" : "版本 ?";
        }
        finally
        {
            _populating = false;
        }
    }

    private void Redundancy_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int value = (int)Math.Round(e.NewValue);
        RedundancyText.Text = $"{value}%";
        if (_populating)
        {
            return;
        }
        AppSettings.SetRedundancy(value);
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating)
        {
            return;
        }
        string theme = ThemeComboBox.SelectedIndex switch
        {
            1 => AppSettings.ThemeLight,
            2 => AppSettings.ThemeDark,
            _ => AppSettings.ThemeSystem,
        };
        AppSettings.SetTheme(theme);
        ThemeService.ApplyPreference(theme, Window.GetWindow(this));
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavigationService?.GoBack();
}
