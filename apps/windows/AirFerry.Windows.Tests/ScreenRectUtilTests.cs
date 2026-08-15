using AirFerry.Windows.Scan;
using Xunit;

namespace AirFerry.Windows.Tests;

public class ScreenRectUtilTests
{
    [Theory]
    [InlineData(100, 200, 300, 400, 100, 200, 200, 200)]   // drag down-right
    [InlineData(300, 400, 100, 200, 100, 200, 200, 200)]   // drag up-left (start is bottom-right corner)
    [InlineData(300, 200, 100, 400, 100, 200, 200, 200)]   // horizontal flip
    [InlineData(100, 400, 300, 200, 100, 200, 200, 200)]   // vertical flip
    public void Normalize_HandlesAnyDragDirection(
        int x1, int y1, int x2, int y2, int ex, int ey, int ew, int eh)
    {
        (int x, int y, int w, int h) = ScreenRectUtil.Normalize(x1, y1, x2, y2);
        Assert.Equal((ex, ey, ew, eh), (x, y, w, h));
    }

    [Fact]
    public void Normalize_AllowsNegativeOrigins_ForSecondaryMonitors()
    {
        (int x, int y, int w, int h) = ScreenRectUtil.Normalize(-1920, 0, -100, 500);
        Assert.Equal((-1920, 0, 1820, 500), (x, y, w, h));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(6, 6, true)]
    [InlineData(-6, 5, true)]
    [InlineData(7, 0, false)]
    [InlineData(0, -7, false)]
    public void IsClick_ClassifiesPressReleaseDistance(int dx, int dy, bool expected)
    {
        Assert.Equal(expected, ScreenRectUtil.IsClick(dx, dy));
    }

    [Theory]
    [InlineData(32, 32, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(31, 1000, false)]
    [InlineData(1000, 31, false)]
    [InlineData(0, 0, false)]
    public void IsRegionSize_EnforcesMinimum(int width, int height, bool expected)
    {
        Assert.Equal(expected, ScreenRectUtil.IsRegionSize(width, height));
    }
}
