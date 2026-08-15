namespace AirFerry.Windows.Models;

/// <summary>
/// Identifies what the scan pipeline should pull frames from. Generalizes the
/// bare DirectShow device index that used to flow through
/// <c>DeviceSelectView → ScanView → ScanViewModel.StartScan</c>: a source is
/// either a camera/capture-card device, a fixed screen rectangle, or a specific
/// top-level window. The scan pipeline only ever sees this record — how the
/// frame is actually obtained is decided by the frame-source factory.
/// </summary>
public abstract record ScanSource
{
    /// <summary>Human-readable label for the scan page status line.</summary>
    public abstract string DisplayName { get; }
}

/// <summary>
/// A DirectShow video-input device (webcam or USB/HDMI/SDI capture card),
/// bound by the 0-based index <c>DeviceEnumerator</c> reports.
/// </summary>
public sealed record DeviceSource(int Index, string FriendlyName) : ScanSource
{
    public override string DisplayName =>
        string.IsNullOrWhiteSpace(FriendlyName) ? $"摄像头 #{Index}" : FriendlyName;
}

/// <summary>
/// A fixed rectangle of the virtual screen, in physical pixels. Coordinates
/// live in the virtual-screen space, so monitors placed left of / above the
/// primary yield negative X/Y. Valid under PerMonitorV2 DPI awareness, which
/// the app manifest already declares — no DPI rescaling is applied.
/// </summary>
public sealed record ScreenRegionSource(int X, int Y, int Width, int Height) : ScanSource
{
    public override string DisplayName => $"屏幕区域 {Width}×{Height}";
}

/// <summary>
/// A top-level window captured through its HWND (GDI PrintWindow with a
/// BitBlt fallback). The picker resolves the HWND at selection time; the
/// capture follows the window across moves/resizes until it closes.
/// </summary>
public sealed record WindowSource(nint Hwnd, string Title) : ScanSource
{
    public override string DisplayName =>
        string.IsNullOrWhiteSpace(Title) ? "窗口捕获" : $"窗口: {Title}";
}
