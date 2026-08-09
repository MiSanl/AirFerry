using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirFerry.Windows.Scan;
using AirFerry.Windows.ViewModels;

namespace AirFerry.Windows.Views;

/// <summary>
/// Scan page code-behind — owns the <see cref="ScanViewModel"/> and renders its
/// state into the WPF surface. The preview is a <see cref="WriteableBitmap"/>
/// fed by managed BGR snapshots from the VM's single camera producer; the WPF
/// dispatcher never opens or reads a video device.
/// A <see cref="DispatcherTimer"/> polls the VM for progress at ~7 Hz (mirrors
/// Android's UI refresh cadence).
/// </summary>
public partial class ScanView : Page
{
    private readonly ScanViewModel _vm;
    private readonly DispatcherTimer _progressTimer;
    private readonly object _stopGate = new();
    private Task _stopTask = Task.CompletedTask;
    private PreviewFrame? _latestPreview;
    private int _previewRenderScheduled;
    private int _activationEpoch;
    private volatile bool _pageActive;

    public ScanView(int deviceIndex)
    {
        InitializeComponent();
        _vm = new ScanViewModel();
        _vm.TransferCompleted += OnTransferCompleted;
        _vm.PreviewFrameReady += OnPreviewFrameReady;

        // Progress poll at 7 Hz (same as Android's ~7Hz UI refresh). Also syncs
        // the VM's observable fields into the WPF text controls each tick.
        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(140),
            DispatcherPriority.Normal, (_, _) =>
            {
                _vm.RefreshProgress();
                SyncUiFromViewModel();
                if (!string.IsNullOrEmpty(_vm.RecoveryStageText))
                {
                    RecoveryStageText.Text = _vm.RecoveryStageText;
                    RecoveryStageText.Visibility = Visibility.Visible;
                }
                else
                {
                    RecoveryStageText.Visibility = Visibility.Collapsed;
                }
            }, Dispatcher)
        {
            IsEnabled = false,
        };

        Loaded += async (_, _) => await StartAsync(deviceIndex);
        Unloaded += async (_, _) => await CleanupAsync();
    }

    private async Task StartAsync(int deviceIndex)
    {
        int epoch = Interlocked.Increment(ref _activationEpoch);
        _pageActive = true;
        Task pendingStop;
        lock (_stopGate)
        {
            pendingStop = _stopTask;
        }
        try
        {
            await pendingStop;
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"停止设备失败: {ex.Message}";
            SyncUiFromViewModel();
            return;
        }
        if (!_pageActive || epoch != Volatile.Read(ref _activationEpoch))
        {
            return;
        }

        DrawProgressRing(0);
        _vm.StartScan(deviceIndex);
        _progressTimer.Start();
        StopButton.Content = _vm.IsScanning ? "⏹ 停止" : "▶ 重试";
    }

    private void OnPreviewFrameReady(PreviewFrame frame)
    {
        if (!_pageActive)
        {
            frame.Dispose();
            return;
        }
        PreviewFrame? replaced = Interlocked.Exchange(ref _latestPreview, frame);
        replaced?.Dispose();
        SchedulePreviewRender();
    }

    private void SchedulePreviewRender()
    {
        if (Interlocked.Exchange(ref _previewRenderScheduled, 1) != 0)
        {
            return;
        }
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _previewRenderScheduled, 0);
            Interlocked.Exchange(ref _latestPreview, null)?.Dispose();
            return;
        }
        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Render,
                new Action(RenderLatestPreview));
        }
        catch
        {
            Interlocked.Exchange(ref _previewRenderScheduled, 0);
            Interlocked.Exchange(ref _latestPreview, null)?.Dispose();
        }
    }

    private void RenderLatestPreview()
    {
        PreviewFrame? frame = Interlocked.Exchange(ref _latestPreview, null);
        if (frame is not null)
        {
            try
            {
                if (_pageActive)
                {
                    RenderPreview(frame);
                }
            }
            finally
            {
                frame.Dispose();
            }
        }
        Interlocked.Exchange(ref _previewRenderScheduled, 0);
        if (_pageActive && Volatile.Read(ref _latestPreview) is not null)
        {
            SchedulePreviewRender();
        }
    }

    private void RenderPreview(PreviewFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 ||
            frame.Stride < frame.Width * 3 ||
            frame.Length < frame.Stride * frame.Height)
        {
            return;
        }
        if (PreviewImage.Source is not WriteableBitmap wb ||
            wb.PixelWidth != frame.Width || wb.PixelHeight != frame.Height)
        {
            wb = new WriteableBitmap(frame.Width, frame.Height, 96, 96,
                PixelFormats.Bgr24, null);
            PreviewImage.Source = wb;
        }
        wb.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Pixels, frame.Stride, 0);
    }

    /// <summary>Draw the circular progress ring (0..100) on the overlay canvas.</summary>
    private void DrawProgressRing(double percent)
    {
        ProgressCanvas.Children.Clear();
        double size = 180;
        double stroke = 12;
        double radius = (size - stroke) / 2;
        Point center = new(size / 2, size / 2);

        // Background ring.
        var bg = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size,
            Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
            StrokeThickness = stroke,
        };
        Canvas.SetLeft(bg, 0);
        Canvas.SetTop(bg, 0);
        ProgressCanvas.Children.Add(bg);

        // Progress arc (drawn as a Path because WPF has no arc shape).
        double angle = Math.Clamp(percent, 0, 100) / 100.0 * 360.0;
        if (angle > 0)
        {
            double rad = (angle - 90) * Math.PI / 180.0;
            Point end = new(
                center.X + radius * Math.Cos(rad),
                center.Y + radius * Math.Sin(rad));
            bool largeArc = angle > 180;
            var arc = new System.Windows.Shapes.Path
            {
                Stroke = (Brush)FindResource("Accent"),
                StrokeThickness = stroke,
                Data = new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new(center.X, center.Y - radius),
                            Segments = { new ArcSegment(end, new Size(radius, radius), 0, largeArc, SweepDirection.Clockwise, true) },
                        },
                    },
                },
            };
            ProgressCanvas.Children.Add(arc);
        }

        // Percent label.
        var label = new TextBlock
        {
            Text = $"{percent:F0}%",
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextPrimary"),
        };
        Canvas.SetLeft(label, center.X - 30);
        Canvas.SetTop(label, center.Y - 20);
        ProgressCanvas.Children.Add(label);
    }

    private void OnTransferCompleted(Models.RecoveryResult result)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_pageActive)
            {
                return;
            }
            DrawProgressRing(100);
            if (result.IsText)
            {
                // Prefer descriptor / staged display name; else path basename;
                // ReceiveTextView falls back to 文字消息.txt when still empty.
                string? suggested = !string.IsNullOrWhiteSpace(result.DisplayName)
                    ? result.DisplayName
                    : result.SingleFilePath is not null
                        ? System.IO.Path.GetFileName(result.SingleFilePath)
                        : null;
                NavigationService?.Navigate(new ReceiveTextView(result, suggested));
            }
            else if (result.IsBundle && result.Bundle is not null)
            {
                NavigationService?.Navigate(new ReceiveBundleView(result));
            }
            else if (result.SingleFilePath is not null)
            {
                NavigationService?.Navigate(new ReceiveDetailView(result));
            }
        });
    }

    // Bind VM properties → UI on each progress tick (simpler than full INotifyPropertyChanged hookup).
    // Called via the VM's RefreshProgress indirectly: we poll VM fields here.

    private async void Back_Click(object sender, RoutedEventArgs e) =>
        await CleanupAndGoBackAsync();

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        try
        {
            if (_vm.IsScanning)
            {
                _progressTimer.Stop();
                StatusText.Text = "正在停止设备…";
                await StopPipelineAsync();
                if (_pageActive)
                {
                    SyncUiFromViewModel();
                    StopButton.Content = "▶ 继续";
                }
            }
            else
            {
                await StartAsync(_vm.SelectedDeviceIndex);
            }
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"设备操作失败: {ex.Message}";
            SyncUiFromViewModel();
        }
        StopButton.IsEnabled = true;
    }

    private void FileList_Click(object sender, RoutedEventArgs e)
    {
        NavigationService?.Navigate(new FileListView());
    }

    private Task StopPipelineAsync()
    {
        lock (_stopGate)
        {
            if (!_vm.IsScanning && _stopTask.IsCompleted)
            {
                return Task.CompletedTask;
            }
            if (_stopTask.IsCompleted)
            {
                // StopScan waits for producer/workers before disposing native
                // handles. Run that safe wait off the WPF dispatcher.
                _stopTask = Task.Run(_vm.StopScan);
            }
            return _stopTask;
        }
    }

    private async Task CleanupAsync()
    {
        _pageActive = false;
        Interlocked.Increment(ref _activationEpoch);
        _progressTimer.Stop();
        Interlocked.Exchange(ref _latestPreview, null)?.Dispose();
        try
        {
            await StopPipelineAsync();
        }
        catch
        {
            // The page is leaving; the next activation will surface a failed
            // stop before attempting to reopen the device.
        }
    }

    private async Task CleanupAndGoBackAsync()
    {
        await CleanupAsync();
        _vm.PreviewFrameReady -= OnPreviewFrameReady;
        _vm.TransferCompleted -= OnTransferCompleted;
        _vm.Dispose();
        NavigationService?.GoBack();
    }

    private void SyncUiFromViewModel()
    {
        StatusText.Text = _vm.StatusText;
        FileSummaryText.Text = _vm.FileSummaryText;
        ProgressText.Text = $"{_vm.ReceivedSymbolsText} / {_vm.TotalSymbolsText}";
        ScanMetricsText.Text = _vm.ScanMetricsText;
        TransferMetricsText.Text = _vm.TransferMetricsText;
        CodeStatusText.Text = _vm.CodeStatusText;
        DrawProgressRing(_vm.Progress);
    }
}
