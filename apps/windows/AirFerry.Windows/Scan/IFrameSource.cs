using OpenCvSharp;

namespace AirFerry.Windows.Scan;

/// <summary>
/// The pull-mode contract the scan pipeline depends on.
/// <see cref="VideoCapture"/> implements it for DirectShow devices and
/// <see cref="ScreenCapture"/> implements it for screen rectangles / windows;
/// <c>ScanViewModel.ProducerLoop</c> is agnostic beyond this surface.
/// </summary>
/// <remarks>
/// <b>Threading</b>: <see cref="ReadGray"/> and <see cref="SnapshotBgr"/> are
/// not thread-safe and are only called from the single producer thread, exactly
/// as the <see cref="VideoCapture"/> contract before it. <see cref="IDisposable.Dispose"/>
/// runs on the cleanup path after the producer has joined.
/// </remarks>
public interface IFrameSource : IDisposable
{
    /// <summary>
    /// False once the source is gone for good (device unplugged, captured
    /// window closed, region invalid). The producer should stop and surface a
    /// message instead of spinning on null reads.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>Frame size actually delivered (0 until the first read).</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>
    /// Read one frame and convert it to grayscale (CV_8UC1). Returns null on a
    /// transient miss; returns null permanently once <see cref="IsOpen"/> turns
    /// false. The returned <see cref="Mat"/> is owned by the source and reused
    /// across calls — callers must not hold it across the next read.
    /// </summary>
    Mat? ReadGray();

    /// <summary>
    /// Copy the image produced by the latest <see cref="ReadGray"/> call into a
    /// compact managed BGR24 snapshot for the UI. Must be called on the same
    /// producer thread before the next read.
    /// </summary>
    PreviewFrame? SnapshotBgr();
}
