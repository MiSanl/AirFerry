using System.Threading;
using AirFerry.Windows.Models;
using AirFerry.Windows.Scan;

namespace AirFerry.Windows.Views;

/// <summary>
/// Shared state across the per-monitor picker overlays: the hover/drag
/// highlight (physical virtual-screen rects) and the single completion every
/// overlay settles on. All members run on the UI thread (every overlay window
/// shares the creating thread's dispatcher).
/// </summary>
internal sealed class RegionPickerSession
{
    private readonly List<RegionPickerWindow> _windows = new();
    private int _completed;

    public TaskCompletionSource<ScanSource?> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScreenNative.RECT? HoverRect { get; private set; }

    public ScreenNative.RECT? DragRect { get; private set; }

    public bool IsDragging { get; private set; }

    public void Register(RegionPickerWindow window) => _windows.Add(window);

    public void BeginDrag()
    {
        IsDragging = true;
        DragRect = null;
        HoverRect = null;
        RenderAll();
    }

    public void SetDrag(ScreenNative.RECT rect)
    {
        DragRect = rect;
        RenderAll();
    }

    public void CancelDrag()
    {
        IsDragging = false;
        DragRect = null;
        RenderAll();
    }

    public void SetHover(ScreenNative.RECT? rect)
    {
        if (IsDragging)
        {
            return;
        }
        HoverRect = rect;
        RenderAll();
    }

    /// <summary>
    /// Settle the picker exactly once: resolve the task and close every
    /// overlay. Closing also lands here via each window's OnClosing, which is
    /// a no-op once completed.
    /// </summary>
    public bool TryComplete(ScanSource? result)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
        {
            return false;
        }
        _ = Completion.TrySetResult(result);
        foreach (RegionPickerWindow window in _windows.ToArray())
        {
            try
            {
                window.Close();
            }
            catch
            {
                // A half-torn overlay must not block the others.
            }
        }
        return true;
    }

    private void RenderAll()
    {
        foreach (RegionPickerWindow window in _windows)
        {
            window.RenderSession();
        }
    }
}

/// <summary>
/// Entry point of the screenshot-style source picker: one translucent overlay
/// per monitor; drag = custom region, click = the window under the cursor,
/// Esc = cancel.
/// </summary>
internal static class RegionPicker
{
    /// <summary>
    /// Show the overlays and resolve the chosen source (null when cancelled).
    /// Must be called on the UI thread; completion continues on the same
    /// dispatcher.
    /// </summary>
    public static Task<ScanSource?> PickAsync()
    {
        List<ScreenNative.RECT> monitors = new();
        bool EnumMonitors(IntPtr monitor, IntPtr hdc, ref ScreenNative.RECT rect, IntPtr data)
        {
            monitors.Add(rect);
            return true;
        }
        _ = ScreenNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, EnumMonitors, IntPtr.Zero);
        if (monitors.Count == 0)
        {
            return Task.FromResult<ScanSource?>(null);
        }

        RegionPickerSession session = new();
        RegionPickerWindow? focused = null;
        try
        {
            foreach (ScreenNative.RECT monitor in monitors)
            {
                RegionPickerWindow window = new(monitor, session);
                session.Register(window);
                window.Show();
                focused ??= window;
            }
            // Keyboard focus (for Esc) on one of the overlays.
            focused?.Focus();
        }
        catch
        {
            // This method is NOT async: an exception escaping here propagates
            // synchronously to the caller *before the Task exists*, and the
            // callers await it from async void event handlers — unhandled there
            // it kills the process. It would also leave every overlay shown on
            // monitor N-1 stuck on screen. Tear the session down (closes all
            // registered windows; completing with null is unobservable since
            // we rethrow) and let the caller surface the failure.
            _ = session.TryComplete(null);
            throw;
        }
        return session.Completion.Task;
    }
}
