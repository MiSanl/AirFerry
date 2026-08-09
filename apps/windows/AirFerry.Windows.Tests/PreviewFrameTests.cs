using System.Buffers;
using AirFerry.Windows.Scan;
using Xunit;

namespace AirFerry.Windows.Tests;

public sealed class PreviewFrameTests
{
    [Fact]
    public void Dispose_IsIdempotent_AndInvalidatesBufferAccess()
    {
        byte[] pixels = ArrayPool<byte>.Shared.Rent(12);
        var frame = new PreviewFrame(pixels, width: 2, height: 2, stride: 6, length: 12);

        Assert.Same(pixels, frame.Pixels);
        Assert.Equal(12, frame.Length);

        frame.Dispose();
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = frame.Pixels);
    }
}
