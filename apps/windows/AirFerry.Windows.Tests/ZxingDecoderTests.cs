using System.Buffers.Binary;
using AirFerry.Windows.Scan;
using Xunit;

namespace AirFerry.Windows.Tests;

public sealed class ZxingDecoderTests
{
    [Fact]
    public void ParseMulti_DecodesSharedNativeLayout()
    {
        byte[] first = [0x45, 0x54, 0x01];
        byte[] second = [0x10, 0x20];
        byte[] packed = Pack(
            (first, new[] { 10, 20, 110, 120 }),
            (second, new[] { 200, 30, 300, 130 }));

        List<ZxingDecoder.MultiResult> decoded = ZxingDecoder.ParseMulti(packed);

        Assert.Equal(2, decoded.Count);
        Assert.Equal(first, decoded[0].Payload);
        Assert.Equal(new[] { 10, 20, 110, 120 }, decoded[0].Bbox);
        Assert.Equal(second, decoded[1].Payload);
        Assert.Equal(new[] { 200, 30, 300, 130 }, decoded[1].Bbox);
    }

    [Fact]
    public void ParseMulti_RejectsTruncatedOrTrailingData()
    {
        byte[] valid = Pack(([0x01], new[] { 1, 2, 3, 4 }));
        Assert.Empty(ZxingDecoder.ParseMulti(valid.AsSpan(0, valid.Length - 1)));
        Assert.Empty(ZxingDecoder.ParseMulti([.. valid, 0xFF]));
    }

    private static byte[] Pack(params (byte[] Payload, int[] Bbox)[] results)
    {
        int length = 4 + results.Sum(result => 4 + result.Payload.Length + 16);
        byte[] packed = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(packed, (uint)results.Length);
        int offset = 4;
        foreach ((byte[] payload, int[] bbox) in results)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packed.AsSpan(offset, 4), (uint)payload.Length);
            offset += 4;
            payload.CopyTo(packed, offset);
            offset += payload.Length;
            foreach (int coordinate in bbox)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    packed.AsSpan(offset, 4), coordinate);
                offset += 4;
            }
        }
        return packed;
    }
}
