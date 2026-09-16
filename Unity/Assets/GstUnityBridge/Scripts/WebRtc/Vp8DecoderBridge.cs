using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

internal sealed class Vp8DecoderBridge : IDisposable
{
    private const string DllName = "Vp8DecoderBridge";
    private const int FormatBufferCapacity = 32;
#if UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
    private const int RtldNow = 2;

    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dlopen(string fileName, int flags);

    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dlsym(IntPtr handle, string symbol);

    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int dlclose(IntPtr handle);

    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dlerror();
#endif

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vp8_decoder_create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vp8_decoder_destroy(IntPtr decoder);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static extern bool vp8_decoder_decode(
        IntPtr decoder,
        byte[] data,
        int dataLength,
        out int width,
        out int height,
        out int pixelFormat,
        out IntPtr yPlane,
        out IntPtr uPlane,
        out IntPtr vPlane,
        out int yStride,
        out int uStride,
        out int vStride,
        StringBuilder formatName,
        int formatNameCapacity);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vp8_decoder_get_last_error(IntPtr decoder);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vp8_decoder_get_last_debug(IntPtr decoder);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int vp8_decoder_get_width(IntPtr decoder);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int vp8_decoder_get_height(IntPtr decoder);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static extern bool vp8_decoder_copy_rgba(IntPtr decoder, byte[] dst, int dstSize);

    private readonly IntPtr m_Decoder;

    public Vp8DecoderBridge()
    {
        m_Decoder = vp8_decoder_create();
    }

    public static bool TryResolveCopyRgbaExport(out string diagnostic)
    {
#if UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
        return TryResolveExport("vp8_decoder_copy_rgba", out diagnostic);
#else
        diagnostic = "Native export probing is only implemented for Linux in this build.";
        return false;
#endif
    }

    public bool IsAvailable => m_Decoder != IntPtr.Zero;

    public bool TryDecode(byte[] data, out DecodedFrameInfo decodedFrame)
    {
        decodedFrame = default;

        if (!IsAvailable || data == null || data.Length == 0)
        {
            return false;
        }

        StringBuilder formatName = new StringBuilder(FormatBufferCapacity);
        bool decoded = vp8_decoder_decode(
            m_Decoder,
            data,
            data.Length,
            out int width,
            out int height,
            out int pixelFormat,
            out IntPtr yPlane,
            out IntPtr uPlane,
            out IntPtr vPlane,
            out int yStride,
            out int uStride,
            out int vStride,
            formatName,
            formatName.Capacity);

        if (!decoded)
        {
            return false;
        }

        decodedFrame = new DecodedFrameInfo(
            width,
            height,
            pixelFormat,
            formatName.ToString(),
            yPlane,
            uPlane,
            vPlane,
            yStride,
            uStride,
            vStride);
        return true;
    }

    public string GetLastError()
    {
        if (!IsAvailable)
        {
            return "VP8 decoder bridge is unavailable.";
        }

        IntPtr errorPtr = vp8_decoder_get_last_error(m_Decoder);
        return errorPtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(errorPtr);
    }

    public string GetLastDebug()
    {
        if (!IsAvailable)
        {
            return "VP8 decoder bridge is unavailable.";
        }

        IntPtr debugPtr = vp8_decoder_get_last_debug(m_Decoder);
        return debugPtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(debugPtr);
    }

    public int GetWidth()
    {
        return IsAvailable ? vp8_decoder_get_width(m_Decoder) : 0;
    }

    public int GetHeight()
    {
        return IsAvailable ? vp8_decoder_get_height(m_Decoder) : 0;
    }

    public bool TryCopyRgba(byte[] dst)
    {
        if (!IsAvailable || dst == null || dst.Length == 0)
        {
            return false;
        }

        return vp8_decoder_copy_rgba(m_Decoder, dst, dst.Length);
    }

    public void Dispose()
    {
        if (m_Decoder != IntPtr.Zero)
        {
            vp8_decoder_destroy(m_Decoder);
        }
    }

#if UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
    private static bool TryResolveExport(string symbolName, out string diagnostic)
    {
        diagnostic = string.Empty;
        string pluginAbsolutePath = Path.Combine(
            Application.dataPath,
            "Plugins/Vp8DecoderBridge/x86_64/libVp8DecoderBridge.so");
        string[] libraryNames = { pluginAbsolutePath, "libVp8DecoderBridge.so", "Vp8DecoderBridge" };

        foreach (string libraryName in libraryNames)
        {
            IntPtr handle = IntPtr.Zero;

            try
            {
                handle = dlopen(libraryName, RtldNow);
                if (handle == IntPtr.Zero)
                {
                    diagnostic = $"path={libraryName} dlopen_failed={ReadDlError()}";
                    continue;
                }

                IntPtr symbol = dlsym(handle, symbolName);
                if (symbol != IntPtr.Zero)
                {
                    diagnostic = $"path={libraryName} symbol={symbolName} resolved=True address=0x{symbol.ToInt64():X}";
                    return true;
                }

                diagnostic = $"path={libraryName} symbol={symbolName} resolved=False dlsym_error={ReadDlError()}";
            }
            catch (Exception ex)
            {
                diagnostic = $"path={libraryName} symbol={symbolName} exception={ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    dlclose(handle);
                }
            }
        }

        return false;
    }

    private static string ReadDlError()
    {
        IntPtr errorPtr = dlerror();
        return errorPtr == IntPtr.Zero ? "<none>" : Marshal.PtrToStringAnsi(errorPtr);
    }
#endif
}

internal readonly struct DecodedFrameInfo
{
    public DecodedFrameInfo(
        int width,
        int height,
        int pixelFormat,
        string formatName,
        IntPtr yPlane,
        IntPtr uPlane,
        IntPtr vPlane,
        int yStride,
        int uStride,
        int vStride)
    {
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        FormatName = string.IsNullOrEmpty(formatName) ? "<unknown>" : formatName;
        YPlane = yPlane;
        UPlane = uPlane;
        VPlane = vPlane;
        YStride = yStride;
        UStride = uStride;
        VStride = vStride;
    }

    public int Width { get; }
    public int Height { get; }
    public int PixelFormat { get; }
    public string FormatName { get; }
    public IntPtr YPlane { get; }
    public IntPtr UPlane { get; }
    public IntPtr VPlane { get; }
    public int YStride { get; }
    public int UStride { get; }
    public int VStride { get; }
}
