using AirFerry.Windows.Models;

namespace AirFerry.Windows.Scan;

/// <summary>
/// Maps a <see cref="ScanSource"/> onto the <see cref="IFrameSource"/> that
/// realizes it. Keeps <c>ScanViewModel.StartScan</c> free of per-source
/// construction knowledge.
/// </summary>
internal static class FrameSourceFactory
{
    public static IFrameSource Create(ScanSource source) => source switch
    {
        DeviceSource device => new VideoCapture(device.Index),
        ScreenRegionSource region => new ScreenCapture(region),
        WindowSource window => new ScreenCapture(window),
        _ => throw new NotSupportedException($"未支持的视频源类型: {source.GetType().Name}"),
    };
}
