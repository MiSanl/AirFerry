using AirFerry.Windows.Services;
using Wpf.Ui.Controls;

namespace AirFerry.Windows.Views;

/// <summary>
/// The single host window — replaces the implicit <c>NavigationWindow</c> that
/// WPF generated from the old <c>StartupUri</c>-to-Page setup. All views remain
/// <see cref="System.Windows.Controls.Page"/> instances navigated inside the
/// embedded Frame, so every existing <c>NavigationService.Navigate / GoBack</c>
/// call keeps working unchanged. The appearance preference is applied here
/// (before the first render) because <see cref="SystemThemeWatcher"/> needs a
/// window to observe.
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeService.ApplyPreference(AppSettings.Theme, this);
    }
}
