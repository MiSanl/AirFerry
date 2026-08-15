using AirFerry.Windows.Models;
using Xunit;

namespace AirFerry.Windows.Tests;

public class ScanSourceTests
{
    [Fact]
    public void DeviceSource_DisplayName_UsesFriendlyName()
    {
        Assert.Equal("USB3.0 Capture", new DeviceSource(0, "USB3.0 Capture").DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void DeviceSource_DisplayName_FallsBackToIndexWhenNameBlank(string? name)
    {
        Assert.Equal("摄像头 #2", new DeviceSource(2, name!).DisplayName);
    }

    [Fact]
    public void ScreenRegionSource_DisplayName_ShowsSize()
    {
        Assert.Equal("屏幕区域 1280×720",
            new ScreenRegionSource(-100, 0, 1280, 720).DisplayName);
    }

    [Fact]
    public void WindowSource_DisplayName_UsesTitle()
    {
        Assert.Equal("窗口: AirFerry — Mozilla Firefox",
            new WindowSource(0x0001010A, "AirFerry — Mozilla Firefox").DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WindowSource_DisplayName_FallsBackWhenTitleBlank(string? title)
    {
        Assert.Equal("窗口捕获", new WindowSource(1, title!).DisplayName);
    }

    [Fact]
    public void Sources_WithSameValues_AreEqual()
    {
        // Records: equality matters because the VM stores the selected source
        // and reuses it for restarts.
        Assert.Equal(new ScreenRegionSource(10, 20, 640, 480),
            new ScreenRegionSource(10, 20, 640, 480));
        Assert.NotEqual<ScanSource>(
            new ScreenRegionSource(10, 20, 640, 480),
            new ScreenRegionSource(10, 20, 641, 480));
    }
}
