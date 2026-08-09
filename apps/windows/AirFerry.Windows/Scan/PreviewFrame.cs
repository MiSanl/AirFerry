using System.Buffers;

namespace AirFerry.Windows.Scan;

/// <summary>
/// Owned snapshot of one compact BGR24 preview frame. Its buffer comes from
/// <see cref="ArrayPool{T}"/>; the consumer must dispose the frame after copying
/// it into the UI surface.
/// </summary>
public sealed class PreviewFrame : IDisposable
{
    private byte[]? _pixels;

    internal PreviewFrame(byte[] pixels, int width, int height, int stride, int length)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Length = length;
    }

    public byte[] Pixels => Volatile.Read(ref _pixels) ??
        throw new ObjectDisposedException(nameof(PreviewFrame));
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int Length { get; }

    public void Dispose()
    {
        byte[]? pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }
}
