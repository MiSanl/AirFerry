namespace AirFerry.Windows.Scan;

/// <summary>
/// Pure rectangle math shared by the region picker (drag rect normalization,
/// click-vs-drag threshold, minimum region size). No OS dependencies so the
/// cross-platform test project can link and verify it.
/// </summary>
public static class ScreenRectUtil
{
    /// <summary>Minimum drag width/height in physical pixels for a valid region.</summary>
    public const int MinRegionPx = 32;

    /// <summary>Maximum movement in physical pixels for a press to count as a click.</summary>
    public const int ClickMaxPx = 6;

    /// <summary>
    /// Normalize two corner points (in any corner order) into an origin-size
    /// tuple. Virtual-screen coordinates, negative origins allowed.
    /// </summary>
    public static (int X, int Y, int Width, int Height) Normalize(int x1, int y1, int x2, int y2)
    {
        int left = Math.Min(x1, x2);
        int top = Math.Min(y1, y2);
        return (left, top, Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    /// <summary>True when the press/release distance stays within the click threshold.</summary>
    public static bool IsClick(int dx, int dy) =>
        Math.Abs(dx) <= ClickMaxPx && Math.Abs(dy) <= ClickMaxPx;

    /// <summary>True when a normalized rect is large enough to scan.</summary>
    public static bool IsRegionSize(int width, int height) =>
        width >= MinRegionPx && height >= MinRegionPx;
}
