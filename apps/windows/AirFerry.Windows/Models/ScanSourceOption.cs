namespace AirFerry.Windows.Models;

/// <summary>
/// One mutually-exclusive choice shown by <c>DeviceSelectView</c>. Hardware
/// choices map directly to <see cref="DeviceSource"/>; screen capture stays a
/// sentinel until the user confirms a region/window in the picker.
/// </summary>
public sealed record ScanSourceOption(
    int? DeviceIndex,
    string FriendlyName,
    string Description,
    bool IsCaptureCard,
    bool IsScreenCapture)
{
    public static ScanSourceOption FromDevice(DeviceInfo device) => new(
        device.Index,
        device.FriendlyName,
        device.IsCaptureCard ? "视频采集卡" : "摄像头",
        device.IsCaptureCard,
        false);

    public static ScanSourceOption ScreenCapture { get; } = new(
        null,
        "屏幕捕获",
        "启动时选择窗口、屏幕区域或整个屏幕",
        false,
        true);

    public static IReadOnlyList<ScanSourceOption> Build(IReadOnlyList<DeviceInfo> devices) =>
        devices.Select(FromDevice).Append(ScreenCapture).ToArray();

    /// <summary>Returns the immediate hardware source, or null when the screen
    /// picker must run first.</summary>
    public ScanSource? CreateImmediateSource() => IsScreenCapture
        ? null
        : new DeviceSource(DeviceIndex!.Value, FriendlyName);
}
