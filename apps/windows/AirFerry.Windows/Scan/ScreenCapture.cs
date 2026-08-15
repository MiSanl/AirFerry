using System.Diagnostics;
using System.Runtime.InteropServices;
using AirFerry.Windows.Models;
using OpenCvSharp;

namespace AirFerry.Windows.Scan;

/// <summary>
/// <see cref="IFrameSource"/> over a fixed screen rectangle or a top-level
/// window, captured with plain GDI: BitBlt for regions, PrintWindow
/// (PW_RENDERFULLCONTENT — renders the full DWM content and works while the
/// window is occluded for most apps, browsers included) with a BitBlt
/// fallback for windows whose render target refuses PrintWindow.
/// </summary>
/// <remarks>
/// <para>
/// Unlike a camera read, GDI capture never blocks, so <see cref="ReadGray"/>
/// throttles itself to the target fps — without it the producer thread would
/// spin BitBlt at full CPU.
/// </para>
/// <para>
/// <b>Limits</b> (documented in the README): a few DirectX exclusive-fullscreen
/// and UWP windows render black under PrintWindow and only the BitBlt fallback
/// (which requires the window to be visible) can help; very large 4K regions
/// may fall below 60 fps. Coordinates are physical pixels (PerMonitorV2).
/// </para>
/// <para>
/// <b>Threading</b>: same contract as <see cref="VideoCapture"/> —
/// <see cref="ReadGray"/>/<see cref="SnapshotBgr"/> run only on the producer
/// thread; Dispose runs on the cleanup path after the producer joined.
/// </para>
/// </remarks>
public sealed class ScreenCapture : IFrameSource
{
    /// <summary>Give up after this many consecutive capture misses.</summary>
    private const int MaxConsecutiveFailures = 30;

    private readonly ScreenRegionSource? _region;
    private readonly WindowSource? _window;
    private readonly long _frameInterval;

    private IntPtr _memDc;
    private IntPtr _bitmap;
    private IntPtr _stockBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;

    private readonly Mat _bgra = new();
    private readonly Mat _bgrPreview = new();
    private readonly Mat _gray = new();

    private bool _disposed;
    private bool _terminated;
    private int _consecutiveFailures;
    private long _nextFrameAt;

    /// <summary>Frame size actually delivered (0 until the first read).</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsOpen => !_disposed && !_terminated;

    public ScreenCapture(ScreenRegionSource region, int fps = 60)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentException("屏幕捕获区域尺寸无效", nameof(region));
        }
        _region = region;
        _frameInterval = Math.Max(1, Stopwatch.Frequency / Math.Max(1, fps));
        _memDc = ScreenNative.CreateCompatibleDC(IntPtr.Zero);
    }

    public ScreenCapture(WindowSource window, int fps = 60)
    {
        if (!ScreenNative.IsWindow(window.Hwnd))
        {
            throw new ArgumentException("目标窗口已不存在", nameof(window));
        }
        _window = window;
        _frameInterval = Math.Max(1, Stopwatch.Frequency / Math.Max(1, fps));
        _memDc = ScreenNative.CreateCompatibleDC(IntPtr.Zero);
    }

    /// <summary>
    /// Capture one frame (throttled to the target fps) and convert it to
    /// grayscale. Returns null on a transient miss; permanently once the
    /// captured window closes or capture keeps failing.
    /// </summary>
    public Mat? ReadGray()
    {
        if (!IsOpen)
        {
            return null;
        }
        Throttle();
        if (!CaptureFrame(out int width, out int height) || !DownloadBitmap(width, height))
        {
            return null;
        }

        _consecutiveFailures = 0;
        Width = width;
        Height = height;
        // GetDIBits 32bpp BI_RGB yields B,G,R,unused rows — luminance ignores
        // the fourth byte, so BGRA2GRAY is exact here.
        Cv2.CvtColor(_bgra, _gray, ColorConversionCodes.BGRA2GRAY);
        return _gray;
    }

    /// <summary>
    /// Convert the latest captured frame into a compact managed BGR24
    /// snapshot. Preview-only (15 Hz), so the BGRA→BGR conversion cost is fine.
    /// </summary>
    public PreviewFrame? SnapshotBgr()
    {
        if (_disposed || _bgra.Empty())
        {
            return null;
        }
        _bgrPreview.Create(_bgra.Rows, _bgra.Cols, MatType.CV_8UC3);
        Cv2.CvtColor(_bgra, _bgrPreview, ColorConversionCodes.BGRA2BGR);
        return PreviewFrameCopy.FromBgr(_bgrPreview);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_bitmap != IntPtr.Zero)
        {
            // A bitmap selected into a DC cannot be deleted — deselect first.
            if (_memDc != IntPtr.Zero && _stockBitmap != IntPtr.Zero)
            {
                _ = ScreenNative.SelectObject(_memDc, _stockBitmap);
            }
            _ = ScreenNative.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }
        if (_memDc != IntPtr.Zero)
        {
            _ = ScreenNative.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        _gray.Dispose();
        _bgrPreview.Dispose();
        _bgra.Dispose();
    }

    /// <summary>
    /// Resolve the capture rectangle (windows are re-queried every frame so
    /// the capture follows moves/resizes) and blit it into the reusable
    /// compatible bitmap.
    /// </summary>
    private bool CaptureFrame(out int width, out int height)
    {
        width = 0;
        height = 0;
        ScreenNative.RECT rect;
        if (_window is not null)
        {
            if (!ScreenNative.IsWindow(_window.Hwnd))
            {
                _terminated = true;
                return false;
            }
            // PrintWindow renders the full window (frame included) at (0,0)
            // sized to GetWindowRect, so both capture paths share this rect —
            // the BitBlt fallback just also picks up the shadow margin around
            // the visible bounds, which is harmless for decoding.
            _ = ScreenNative.GetWindowRect(_window.Hwnd, out rect);
        }
        else
        {
            rect = new ScreenNative.RECT
            {
                Left = _region!.X,
                Top = _region.Y,
                Right = _region.X + _region.Width,
                Bottom = _region.Y + _region.Height,
            };
        }

        width = rect.Width;
        height = rect.Height;
        if (width <= 0 || height <= 0 || !EnsureBitmap(width, height))
        {
            RegisterFailure();
            return false;
        }

        bool ok = false;
        if (_window is not null)
        {
            ok = ScreenNative.PrintWindow(_window.Hwnd, _memDc, ScreenNative.PW_RENDERFULLCONTENT);
        }
        if (!ok)
        {
            IntPtr screenDc = ScreenNative.GetDC(IntPtr.Zero);
            if (screenDc != IntPtr.Zero)
            {
                try
                {
                    ok = ScreenNative.BitBlt(_memDc, 0, 0, width, height,
                        screenDc, rect.Left, rect.Top, ScreenNative.SRCCOPY);
                }
                finally
                {
                    _ = ScreenNative.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }
        if (!ok)
        {
            RegisterFailure();
            return false;
        }
        return true;
    }

    /// <summary>
    /// Recreate the compatible bitmap when the target size changed (window
    /// resized); keep it selected into the memory DC.
    /// The bitmap must be created compatible with the SCREEN DC, not the memory
    /// DC: a memory DC still holds its stock 1×1 monochrome bitmap, and
    /// CreateCompatibleBitmap(memDc, …) would then produce a 1-bit-per-pixel
    /// bitmap — GetDIBits would hand back dithered mono pixels instead of
    /// BGRA32, destroying both the preview and QR decode reliability.
    /// </summary>
    private bool EnsureBitmap(int width, int height)
    {
        if (_memDc == IntPtr.Zero)
        {
            return false;
        }
        if (_bitmap != IntPtr.Zero && width == _bitmapWidth && height == _bitmapHeight)
        {
            return true;
        }
        IntPtr screenDc = ScreenNative.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }
        IntPtr fresh;
        try
        {
            fresh = ScreenNative.CreateCompatibleBitmap(screenDc, width, height);
        }
        finally
        {
            _ = ScreenNative.ReleaseDC(IntPtr.Zero, screenDc);
        }
        if (fresh == IntPtr.Zero)
        {
            return false;
        }
        IntPtr previous = ScreenNative.SelectObject(_memDc, fresh);
        if (previous == IntPtr.Zero)
        {
            _ = ScreenNative.DeleteObject(fresh);
            return false;
        }
        if (_stockBitmap == IntPtr.Zero)
        {
            // The DC's original 1×1 stock bitmap; kept for deselecting around
            // GetDIBits and on teardown. Stock objects are never deleted.
            _stockBitmap = previous;
        }
        else if (previous == _bitmap)
        {
            _ = ScreenNative.DeleteObject(_bitmap);
        }
        _bitmap = fresh;
        _bitmapWidth = width;
        _bitmapHeight = height;
        return true;
    }

    /// <summary>
    /// GetDIBits the compatible bitmap straight into the reused BGRA Mat
    /// (top-down rows, stride = width*4, matches a continuous Mat layout).
    /// </summary>
    private bool DownloadBitmap(int width, int height)
    {
        _bgra.Create(height, width, MatType.CV_8UC4);
        ScreenNative.BITMAPINFO info = default;
        info.bmiHeader.biSize = (uint)Marshal.SizeOf<ScreenNative.BITMAPINFOHEADER>();
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height; // negative = top-down row order
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = ScreenNative.BI_RGB;

        // MSDN: the bitmap must not be selected into a DC while GetDIBits
        // runs — swap the stock bitmap back in, download, then restore.
        IntPtr removed = ScreenNative.SelectObject(_memDc, _stockBitmap);
        int lines;
        try
        {
            lines = ScreenNative.GetDIBits(_memDc, _bitmap, 0, (uint)height,
                _bgra.Data, ref info, ScreenNative.DIB_RGB_COLORS);
        }
        finally
        {
            if (removed != IntPtr.Zero && removed != _stockBitmap)
            {
                _ = ScreenNative.SelectObject(_memDc, removed);
            }
        }
        if (lines != height)
        {
            RegisterFailure();
            return false;
        }
        return true;
    }

    private void RegisterFailure()
    {
        if (++_consecutiveFailures >= MaxConsecutiveFailures)
        {
            _terminated = true;
        }
    }

    /// <summary>
    /// Sleep/spin until the next frame slot. Sleep(1) while more than ~2 ms
    /// remain (the timer resolution makes longer sleeps overshoot), then spin
    /// the last stretch for a stable ~60 fps.
    /// </summary>
    private void Throttle()
    {
        long now = Stopwatch.GetTimestamp();
        if (_nextFrameAt == 0)
        {
            _nextFrameAt = now + _frameInterval;
            return;
        }
        while ((now = Stopwatch.GetTimestamp()) < _nextFrameAt)
        {
            long msLeft = (_nextFrameAt - now) * 1000 / Stopwatch.Frequency;
            if (msLeft > 2)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
        // Skip slots when a capture took longer than the interval.
        _nextFrameAt = Math.Max(Stopwatch.GetTimestamp(), _nextFrameAt + _frameInterval);
    }
}
