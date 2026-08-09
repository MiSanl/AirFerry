using System.Runtime.InteropServices;

namespace AirFerry.Windows.Native;

/// <summary>
/// C ABI for the shared ZXing-C++ decoder used by both Android and Windows.
/// Input pixels remain caller-owned; successful decode output is native-owned
/// and must be released with <see cref="BufferFree"/> after copying.
/// </summary>
internal static class NativeZxingBridge
{
    private const string LibName = "airferry_zxing.dll";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "airferry_zxing_abi_version")]
    internal static extern uint AbiVersion();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "airferry_zxing_decode_multi_y")]
    internal static extern int DecodeMultiY(
        byte[] pixels,
        nuint pixelLen,
        int width,
        int height,
        int rowStride,
        int[]? hints,
        nuint hintCount,
        float marginFraction,
        out IntPtr outBuffer,
        out nuint outLen);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "airferry_zxing_buffer_free")]
    internal static extern void BufferFree(IntPtr buffer);
}
