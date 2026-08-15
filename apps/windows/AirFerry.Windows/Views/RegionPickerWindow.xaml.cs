using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AirFerry.Windows.Models;
using AirFerry.Windows.Scan;

namespace AirFerry.Windows.Views;

/// <summary>
/// One full-monitor translucent overlay of the region picker. Owns no state
/// beyond its monitor rect, DPI scale and drag anchor — the selection lives in
/// the shared <see cref="RegionPickerSession"/> and every overlay renders the
/// portion of it that intersects its monitor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placement</b>: pinned to its monitor's physical rect via SetWindowPos
/// after <c>SourceInitialized</c>; the app is PerMonitorV2-aware, so mouse
/// coordinates convert with this window's own PixelsPerDip.
/// </para>
/// <para>
/// <b>Interaction</b>: drag draws a region; a click (press/release within
/// <see cref="ScreenRectUtil.ClickMaxPx"/>) picks the window under the cursor;
/// Esc cancels. Mouse capture routes move/up to the window where the press
/// started, so drags may cross monitor boundaries.
/// </para>
/// </remarks>
internal partial class RegionPickerWindow : Window
{
    private static readonly uint CurrentPid = (uint)Environment.ProcessId;

    private readonly RegionPickerSession _session;
    private readonly ScreenNative.RECT _monitor;
    private double _pxPerDip = 1.0;
    private int _dragStartX;
    private int _dragStartY;
    private bool _dragging;

    public RegionPickerWindow(ScreenNative.RECT monitor, RegionPickerSession session)
    {
        _monitor = monitor;
        _session = session;
        InitializeComponent();

        SourceInitialized += (_, _) => PinToMonitor();
        // Paint the initial full-screen dim before any mouse event arrives
        // (the dim borders start life at size 0).
        Loaded += (_, _) => RenderSession();
        PreviewMouseLeftButtonDown += OnDown;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;
        PreviewKeyDown += OnKey;
    }

    /// <summary>
    /// Pin the overlay to its monitor in physical pixels, then align WPF's own
    /// DIP layout with that placement (the move itself may have shifted the
    /// window across a DPI boundary).
    /// </summary>
    private void PinToMonitor()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _ = ScreenNative.SetWindowPos(hwnd, ScreenNative.HWND_TOPMOST,
            _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height,
            ScreenNative.SWP_SHOWWINDOW);
        _pxPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Left = _monitor.Left / _pxPerDip;
        Top = _monitor.Top / _pxPerDip;
        Width = _monitor.Width / _pxPerDip;
        Height = _monitor.Height / _pxPerDip;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _pxPerDip = newDpi.PixelsPerDip;
        RenderSession();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Any close path (Alt+F4 included) must settle the shared completion.
        _session.TryComplete(null);
        base.OnClosing(e);
    }

    /// <summary>Window-local DIP → physical virtual-screen point.</summary>
    private Point ToPhysical(Point dip) =>
        new(_monitor.Left + dip.X * _pxPerDip, _monitor.Top + dip.Y * _pxPerDip);

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Point p = ToPhysical(e.GetPosition(this));
        _dragStartX = (int)p.X;
        _dragStartY = (int)p.Y;
        _dragging = true;
        CaptureMouse();
        _session.BeginDrag();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        Point p = ToPhysical(e.GetPosition(this));
        if (_dragging)
        {
            (int x, int y, int w, int h) = ScreenRectUtil.Normalize(
                _dragStartX, _dragStartY, (int)p.X, (int)p.Y);
            _session.SetDrag(new ScreenNative.RECT
            {
                Left = x,
                Top = y,
                Right = x + w,
                Bottom = y + h,
            });
        }
        else if (TryGetPickableWindow(p, out _, out ScreenNative.RECT bounds))
        {
            _session.SetHover(bounds);
        }
        else
        {
            _session.SetHover(null);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        ReleaseMouseCapture();
        Point p = ToPhysical(e.GetPosition(this));

        if (ScreenRectUtil.IsClick((int)p.X - _dragStartX, (int)p.Y - _dragStartY))
        {
            if (TryGetPickableWindow(p, out nint hwnd, out _))
            {
                _session.TryComplete(new WindowSource(hwnd, ScreenNative.GetWindowTitle(hwnd)));
            }
            else
            {
                // Click on the desktop/taskbar/own overlay — keep picking.
                _session.CancelDrag();
            }
            e.Handled = true;
            return;
        }

        (int x, int y, int w, int h) = ScreenRectUtil.Normalize(
            _dragStartX, _dragStartY, (int)p.X, (int)p.Y);
        if (ScreenRectUtil.IsRegionSize(w, h))
        {
            _session.TryComplete(new ScreenRegionSource(x, y, w, h));
        }
        else
        {
            _session.CancelDrag();
        }
        e.Handled = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _session.TryComplete(null);
        }
    }

    /// <summary>
    /// Resolve the top-level pickable window under a physical point. The
    /// overlays themselves are topmost and cover everything, so plain
    /// WindowFromPoint would always return them — instead scan the Z order and
    /// take the first eligible window (not this process, visible, not a tool
    /// window, not cloaked, at least 64 px on a side) whose rect contains the
    /// point.
    /// </summary>
    private static bool TryGetPickableWindow(Point physical, out nint hwnd, out ScreenNative.RECT bounds)
    {
        bounds = default;
        ScreenNative.RECT found = default;
        nint foundHwnd = 0;
        bool Callback(nint candidate, nint lParam)
        {
            ScreenNative.GetWindowThreadProcessId(candidate, out uint pid);
            if (pid == CurrentPid)
            {
                return true;
            }
            if (!ScreenNative.IsWindowVisible(candidate))
            {
                return true;
            }
            _ = ScreenNative.GetWindowRect(candidate, out ScreenNative.RECT r);
            if (physical.X < r.Left || physical.X >= r.Right ||
                physical.Y < r.Top || physical.Y >= r.Bottom)
            {
                return true;
            }
            if ((ScreenNative.GetWindowLong(candidate, ScreenNative.GWL_EXSTYLE) &
                 ScreenNative.WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }
            if (ScreenNative.DwmGetWindowAttribute(candidate, ScreenNative.DWMWA_CLOAKED,
                    out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }
            if (r.Width < 64 || r.Height < 64)
            {
                return true;
            }

            // Z-order decision used GetWindowRect (contains the DWM shadow
            // margin); highlight the visible frame instead.
            found = ScreenNative.GetWindowFrameBounds(candidate);
            foundHwnd = candidate;
            return false; // stop at the topmost eligible window
        }

        _ = ScreenNative.EnumWindows(Callback, IntPtr.Zero);
        bounds = found;
        hwnd = foundHwnd;
        return hwnd != 0;
    }

    /// <summary>
    /// Redraw the dim/selection visuals from the shared session state. Rects
    /// live in physical virtual-screen coordinates; each overlay clips to its
    /// own monitor area.
    /// </summary>
    internal void RenderSession()
    {
        if (!IsLoaded)
        {
            return;
        }
        ScreenNative.RECT? selection = _session.IsDragging ? _session.DragRect : _session.HoverRect;
        double width = ActualWidth;
        double height = ActualHeight;
        if (selection is null)
        {
            Place(DimTop, 0, 0, width, height);
            Place(DimLeft, 0, 0, 0, 0);
            Place(DimRight, 0, 0, 0, 0);
            Place(DimBottom, 0, 0, 0, 0);
            Place(SelBorder, 0, 0, 0, 0);
            SelLabel.Visibility = Visibility.Collapsed;
            return;
        }

        double left = Math.Clamp((selection.Value.Left - _monitor.Left) / _pxPerDip, 0, width);
        double top = Math.Clamp((selection.Value.Top - _monitor.Top) / _pxPerDip, 0, height);
        double right = Math.Clamp((selection.Value.Right - _monitor.Left) / _pxPerDip, 0, width);
        double bottom = Math.Clamp((selection.Value.Bottom - _monitor.Top) / _pxPerDip, 0, height);
        double selWidth = Math.Max(0, right - left);
        double selHeight = Math.Max(0, bottom - top);

        Place(DimTop, 0, 0, width, top);
        Place(DimLeft, 0, top, left, selHeight);
        Place(DimRight, left + selWidth, top, Math.Max(0, width - (left + selWidth)), selHeight);
        Place(DimBottom, 0, bottom, width, Math.Max(0, height - bottom));
        Place(SelBorder, left, top, selWidth, selHeight);
        SelLabel.Text = $"{selection.Value.Width} × {selection.Value.Height}";
        SelLabel.Visibility = selWidth > 110 && selHeight > 44
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }
}
