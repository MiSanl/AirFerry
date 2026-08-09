using System.Text;
using AirFerry.Windows.Scan;
using Xunit;

namespace AirFerry.Windows.Tests;

public class Crc32Tests
{
    [Fact]
    public void StreamAndSpanImplementationsMatchKnownVector()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("123456789");
        using var stream = new MemoryStream(bytes);

        Assert.Equal(0xCBF4_3926UL, Crc32.Compute(bytes));
        Assert.Equal(Crc32.Compute(bytes), Crc32.Compute(stream));
    }
}
