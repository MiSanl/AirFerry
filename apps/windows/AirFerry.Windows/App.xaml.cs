using System.Windows;
using AirFerry.Windows.Bundle;
using AirFerry.Windows.Views;

namespace AirFerry.Windows;

/// <summary>
/// Code-behind for App.xaml. Startup shows <see cref="MainWindow"/> — a
/// FluentWindow hosting a Frame whose first page is <c>DeviceSelectView</c>
/// (the device-selection page — the user's first interaction), mirroring how
/// Android's launcher Activity is <c>ScanActivity</c>. Navigation between views
/// uses WPF's <c>NavigationService</c>, the WPF analogue of Android's
/// <c>Intent</c>-based Activity switching.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            ContentStore.MigrateLegacyReceivedIfNeeded();
        }
        catch
        {
            // Non-fatal
        }
        try
        {
            ShareExport.PruneExpired();
        }
        catch
        {
            // Non-fatal
        }
        // MainWindow's constructor applies the persisted theme preference.
        new MainWindow().Show();
    }
}
