using AirFerry.Windows.Models;
using Xunit;

namespace AirFerry.Windows.Tests;

public class ScanSourceOptionTests
{
    [Fact]
    public void Build_AppendsOneScreenChoiceAfterHardwareSources()
    {
        DeviceInfo[] devices =
        [
            new DeviceInfo("Integrated Camera", "device-0", 0, false),
            new DeviceInfo("USB Capture", "device-1", 1, true),
        ];

        IReadOnlyList<ScanSourceOption> choices = ScanSourceOption.Build(devices);

        Assert.Equal(3, choices.Count);
        Assert.Equal("Integrated Camera", choices[0].FriendlyName);
        Assert.Equal("USB Capture", choices[1].FriendlyName);
        Assert.Same(ScanSourceOption.ScreenCapture, choices[2]);
        Assert.Single(choices, choice => choice.IsScreenCapture);
    }

    [Fact]
    public void Build_WithNoHardware_StillOffersScreenCapture()
    {
        IReadOnlyList<ScanSourceOption> choices =
            ScanSourceOption.Build(Array.Empty<DeviceInfo>());

        ScanSourceOption choice = Assert.Single(choices);
        Assert.True(choice.IsScreenCapture);
        Assert.Null(choice.CreateImmediateSource());
    }

    [Fact]
    public void HardwareChoice_CreatesOnlyItsSelectedDeviceSource()
    {
        ScanSourceOption choice = ScanSourceOption.FromDevice(
            new DeviceInfo("HDMI Capture", "device-4", 4, true));

        DeviceSource source = Assert.IsType<DeviceSource>(choice.CreateImmediateSource());
        Assert.Equal(4, source.Index);
        Assert.Equal("HDMI Capture", source.FriendlyName);
        Assert.True(choice.IsCaptureCard);
        Assert.False(choice.IsScreenCapture);
    }
}
