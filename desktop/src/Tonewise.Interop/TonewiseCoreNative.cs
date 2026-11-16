using System.Runtime.InteropServices;
using System.Text;

namespace Tonewise.Interop;

internal static class TonewiseCoreNative
{
    private const string DllName = "tonewise_core.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint tonewise_core_abi_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void tonewise_free_string(IntPtr ptr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tonewise_split_artists_json(IntPtr rawUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tonewise_evaluate_scene_json(
        IntPtr sceneJson,
        IntPtr tracksJson
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tonewise_merge_m3u8_json(IntPtr requestJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tonewise_audio_fingerprint_path(IntPtr pathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tonewise_lookup_waveform_json(
        IntPtr archivePathUtf8,
        IntPtr blake3HexUtf8
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tonewise_apply_lww_mutations_json(IntPtr requestJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tonewise_merge_sessions_json(IntPtr requestJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tonewise_apply_header_patch_path(
        IntPtr pathUtf8,
        IntPtr expectedOldHexUtf8,
        byte[] newHeaderPtr,
        nuint newHeaderLen
    );

    internal static IntPtr AllocUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    internal static string ConsumeOwnedUtf8(IntPtr nativePtr)
    {
        if (nativePtr == IntPtr.Zero)
        {
            return "{\"error\":\"null native string returned\"}";
        }
        try
        {
            return Marshal.PtrToStringUTF8(nativePtr) ?? string.Empty;
        }
        finally
        {
            tonewise_free_string(nativePtr);
        }
    }
}
