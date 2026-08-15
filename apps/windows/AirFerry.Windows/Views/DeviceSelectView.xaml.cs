using System.Windows;
using System.Windows.Controls;
using AirFerry.Windows.Models;
using AirFerry.Windows.Scan;
using AirFerry.Windows.Services;

namespace AirFerry.Windows.Views;

/// <summary>
/// The landing page — mutually-exclusive scan-source selection. DirectShow
/// cameras/capture cards and the screen picker are peers in one list; the only
/// start button dispatches the selected source, so screen capture cannot be
/// triggered accidentally alongside a hardware source.
/// </summary>
public partial class DeviceSelectView : Page
{
    private IReadOnlyList<ScanSourceOption> _sources = Array.Empty<ScanSourceOption>();
    private readonly string? _resumeRootId;

    public DeviceSelectView() : this(null)
    {
    }

    public DeviceSelectView(string? resumeRootId)
    {
        _resumeRootId = resumeRootId;
        InitializeComponent();
        if (_resumeRootId is not null)
        {
            ResumeBar.Message = $"继续任务 {_resumeRootId[..8]}…：扫码时会忽略其他文件";
            ResumeBar.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => RefreshDevices();
    }

    private void RefreshDevices()
    {
        IReadOnlyList<DeviceInfo> devices = DeviceEnumerator.Enumerate();
        _sources = ScanSourceOption.Build(devices);
        DeviceList.ItemsSource = _sources;
        EmptyStateBar.Visibility = devices.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectedInfo.Text = devices.Count == 0
            ? "屏幕捕获可用"
            : $"{devices.Count} 个视频设备 + 屏幕捕获";
        // Keep quick start for hardware; when none exists, screen capture is
        // the sole source and is selected explicitly in the same list.
        DeviceList.SelectedIndex = 0;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not ScanSourceOption source)
        {
            StartButton.IsEnabled = false;
            return;
        }
        StartButton.IsEnabled = true;
        SelectedInfo.Text = $"已选择：{source.FriendlyName}";
        StartButton.Content = source.IsScreenCapture
            ? (_resumeRootId is null ? "选择屏幕并开始扫码" : "选择屏幕并继续恢复")
            : (_resumeRootId is null ? "开始扫码" : "继续恢复");
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not ScanSourceOption selected)
        {
            return;
        }
        StartButton.IsEnabled = false;
        DeviceList.IsEnabled = false;
        try
        {
            ScanSource? source = selected.IsScreenCapture
                ? await RegionPicker.PickAsync()
                : selected.CreateImmediateSource();
            if (source is null)
            {
                return;
            }
            NavigationService?.Navigate(new ScanView(source, _resumeRootId));
        }
        catch (Exception ex)
        {
            await UiMessages.ErrorAsync($"扫描来源启动失败：{ex.Message}", "开始扫码");
        }
        finally
        {
            DeviceList.IsEnabled = true;
            StartButton.IsEnabled = DeviceList.SelectedItem is ScanSourceOption;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        NavigationService?.Navigate(new SettingsView());
    }

    /// <summary>History/received files — reachable from the landing page, not
    /// only through the scan page.</summary>
    private void Files_Click(object sender, RoutedEventArgs e)
    {
        NavigationService?.Navigate(new FileListView());
    }
}
