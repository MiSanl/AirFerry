using System.Buffers;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace AirFerry.Windows.Scan;

/// <summary>
/// Copies a BGR Mat into a pooled, compact BGR24 <see cref="PreviewFrame"/>.
/// Extracted from <see cref="VideoCapture.SnapshotBgr"/> so every
/// <see cref="IFrameSource"/> shares one stride-handling implementation.
/// </summary>
/// <remarks>
/// Deliberately not part of <c>PreviewFrame.cs</c>: that file is linked into
/// the cross-platform (net8.0) test project, which must not reference
/// OpenCvSharp.
/// </remarks>
internal static class PreviewFrameCopy
{
    /// <summary>
    /// Returns null when <paramref name="bgr"/> is empty or not 3-channel;
    /// never partially initializes the rented buffer on failure.
    /// </summary>
    public static PreviewFrame? FromBgr(Mat? bgr)
    {
        if (bgr is null || bgr.Empty() || bgr.Channels() != 3)
        {
            return null;
        }

        int width = bgr.Width;
        int height = bgr.Height;
        int rowBytes = checked(width * 3);
        int length = checked(rowBytes * height);
        int sourceStride = checked((int)bgr.Step());
        if (sourceStride < rowBytes)
        {
            return null;
        }
        byte[] pixels = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            if (sourceStride == rowBytes)
            {
                Marshal.Copy(bgr.Data, pixels, 0, length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(bgr.Data, checked(y * sourceStride)),
                        pixels, checked(y * rowBytes), rowBytes);
                }
            }
            return new PreviewFrame(pixels, width, height, rowBytes, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
    }
}
