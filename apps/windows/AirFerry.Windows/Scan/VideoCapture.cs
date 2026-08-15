using System.Diagnostics;
using OpenCvSharp;

namespace AirFerry.Windows.Scan;

/// <summary>
/// Frames a single video device for the scan pipeline. The Windows counterpart
/// of Android's <c>CameraX + QrStreamAnalyzer</c>: bind a webcam or capture
/// card (DirectShow backend), pull frames at a target resolution, and hand the
/// decoder a grayscale luminance image while exposing an occasional managed
/// BGR24 snapshot for preview. Both outputs come from the same device read.
/// </summary>
/// <remarks>
/// <para>
/// Devices are opened by the 0-based index returned by
/// <see cref="DeviceEnumerator"/> (DirectShow's native addressing). The
/// moniker-string path is kept as a future enhancement but OpenCvSharp's
/// DirectShow backend currently binds by index, so the index is authoritative.
/// </para>
/// <para>
/// <b>Resolution</b>: defaults to 1920×1080 at 60 fps to match Android's
/// <c>ResolutionStrategy(CLOSEST_HIGHER_THEN_LOWER)</c> + 60 fps AE target. If
/// the device doesn't support those caps DirectShow picks the nearest, exactly
/// as CameraX does.
/// </para>
/// <para>
/// <b>Threading</b>: <see cref="ReadGray"/> and <see cref="SnapshotBgr"/> are
/// <b>not</b> thread-safe. Only the producer thread in <c>ScanViewModel</c>
/// calls them; the WPF dispatcher never reads from the device.
/// </para>
/// </remarks>
public sealed class VideoCapture : IFrameSource
{
    /// <summary>
    /// Sustained no-good-frame window after which the device is declared lost.
    /// DirectShow's <c>IsOpened()</c> stays true after an unplug — reads just
    /// fail forever — so only a time-based streak can detect it and let
    /// ProducerLoop's IsOpen check report the source as gone. Time-based
    /// (unlike ScreenCapture's 30-frame rule) because a capture card whose
    /// HDMI signal arrives a few seconds after opening must recover in place,
    /// not be torn down after ~0.3 s of failures.
    /// </summary>
    private static readonly long LossTimeoutTicks = (long)(Stopwatch.Frequency * 5.0);

    private readonly OpenCvSharp.VideoCapture _cap;
    private readonly Mat _bgr = new();
    private readonly Mat _gray = new();
    private bool _disposed;
    private bool _lost;
    private long _lastGoodTick;

    /// <summary>Width/height the device actually delivers (0 until first read).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    public bool IsOpen => !_disposed && !_lost && _cap.IsOpened();

    /// <summary>
    /// Open <paramref name="deviceIndex"/> with the given caps. Returns false
    /// (does not throw) on failure so the UI can prompt "device in use?".
    /// </summary>
    public VideoCapture(int deviceIndex, int width = 1920, int height = 1080, int fps = 60)
    {
        _cap = new OpenCvSharp.VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
        _lastGoodTick = Stopwatch.GetTimestamp();
        if (_cap.IsOpened())
        {
            _cap.FrameWidth = width;
            _cap.FrameHeight = height;
            _cap.Fps = fps;
        }
    }

    /// <summary>
    /// Read one frame and convert to grayscale (luminance). Returns null when
    /// the device is exhausted/closed — the caller should treat repeated nulls
    /// as a fatal device error. The returned <see cref="Mat"/> is owned by this
    /// object (reused across calls); callers must clone before holding a
    /// reference across the next read.
    /// </summary>
    public Mat? ReadGray()
    {
        if (_disposed || _lost || !_cap.IsOpened())
        {
            return null;
        }
        bool ok = _cap.Read(_bgr);
        if (!ok || _bgr.Empty())
        {
            // No good frame for the whole loss window (device unplugged, or
            // never delivered anything at all) → declare the source lost so
            // ProducerLoop stops spinning and surfaces the loss to the user.
            if (Stopwatch.GetTimestamp() - _lastGoodTick >= LossTimeoutTicks)
            {
                _lost = true;
            }
            return null;
        }
        _lastGoodTick = Stopwatch.GetTimestamp();
        Width = _bgr.Width;
        Height = _bgr.Height;
        Cv2.CvtColor(_bgr, _gray, ColorConversionCodes.BGR2GRAY);
        return _gray;
    }

    /// <summary>
    /// Copy the BGR image produced by the latest <see cref="ReadGray"/> call
    /// into a compact managed BGR24 snapshot for the UI. Must be called on the
    /// same producer thread before the next camera read.
    /// </summary>
    public PreviewFrame? SnapshotBgr() => PreviewFrameCopy.FromBgr(_bgr);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gray.Dispose();
        _bgr.Dispose();
        _cap.Dispose();
    }
}
