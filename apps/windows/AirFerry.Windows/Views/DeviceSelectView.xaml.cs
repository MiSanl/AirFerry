using System.Windows;
using System.Windows.Controls;
using AirFerry.Windows.Models;
using AirFerry.Windows.Scan;
using AirFerry.Windows.Services;

namespace AirFerry.Windows.Views;

/// <summary>
/// The landing page — device selection (webcams + capture cards). Mirrors the
/// user's explicit ask ("添加设备选择功能，可以选择摄像头或采集卡"): enumerate every
/// DirectShow video-input device, let the user pick one, then navigate to the
/// scan view bound to that device index.
/// </summary>
public partial class DeviceSelectView : Page
{
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();
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
            StartButton.Content = "继续恢复";
        }
        Loaded += (_, _) => RefreshDevices();
    }

    private void RefreshDevices()
    {
        DeviceList.Items.Clear();
        _devices = DeviceEnumerator.Enumerate();
        if (_devices.Count == 0)
        {
            EmptyStateBar.Visibility = Visibility.Visible;
            SelectedInfo.Text = "";
            StartButton.IsEnabled = false;
            return;
        }
        EmptyStateBar.Visibility = Visibility.Collapsed;
        foreach (DeviceInfo d in _devices)
        {
            DeviceList.Items.Add(d);
        }
        SelectedInfo.Text = $"共 {_devices.Count} 个设备";
        // Pre-select the first device for quick start.
        DeviceList.SelectedIndex = 0;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedIndex < 0 || DeviceList.SelectedIndex >= _devices.Count)
        {
            StartButton.IsEnabled = false;
            return;
        }
        DeviceInfo d = _devices[DeviceList.SelectedIndex];
        StartButton.IsEnabled = true;
        SelectedInfo.Text = d.IsCaptureCard
            ? $"已选择采集卡: {d.FriendlyName}"
            : $"已选择摄像头: {d.FriendlyName}";
    }

    private void StartScan_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedIndex < 0 || DeviceList.SelectedIndex >= _devices.Count)
        {
            return;
        }
        DeviceInfo selected = _devices[DeviceList.SelectedIndex];
        NavigationService?.Navigate(
            new ScanView(new DeviceSource(selected.Index, selected.FriendlyName), _resumeRootId));
    }

    /// <summary>
    /// Screenshot-style picker: drag = screen region, click = window, Esc =
    /// cancel. Navigates to the scan page with the chosen source.
    /// </summary>
    private async void ScreenCapture_Click(object sender, RoutedEventArgs e)
    {
        // async void: any exception escaping here (PickAsync teardown rethrow,
        // navigation failure, …) becomes an unhandled UI exception and kills
        // the process — same containment as ReceiveBundleView.SaveAll_Click.
        try
        {
            ScanSource? source = await RegionPicker.PickAsync();
            if (source is null)
            {
                return;
            }
            NavigationService?.Navigate(new ScanView(source, _resumeRootId));
        }
        catch (Exception ex)
        {
            await UiMessages.ErrorAsync($"屏幕捕获启动失败：{ex.Message}", "屏幕捕获");
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
