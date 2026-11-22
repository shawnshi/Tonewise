using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Tonewise.Audio;

/// <summary>
/// Configures WASAPI stream properties via <c>IAudioClient2::SetClientProperties</c> to mark
/// playback streams as <c>AudioCategory_Media</c> before <c>IAudioClient::Initialize</c>.
/// </summary>
public static class WasapiMediaCategoryHelper
{
    public const int AudioCategoryMedia = 11;
    private const int SetClientPropertiesVtableSlot = 16; // 3 IUnknown + 12 IAudioClient + 1 IsOffloadCapable
    private static readonly Guid IidIAudioClient2 = new("726778CD-F60A-4EDA-82DE-E47610CD78AA");

    private static readonly FieldInfo? WasapiAudioClientField =
        typeof(WasapiOut).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? AudioClientInterfaceField =
        typeof(AudioClient).GetField("audioClientInterface", BindingFlags.Instance | BindingFlags.NonPublic);

    public static int AudioClientPropertiesSizeBytes => Marshal.SizeOf<AudioClientProperties>();

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProperties
    {
        public uint CbSize;
        public int IsOffload;
        public int Category;
        public int Options;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetClientPropertiesDelegate(IntPtr self, ref AudioClientProperties properties);

    public static bool TryConfigureMediaCategory(WasapiOut? wasapi)
    {
        if (wasapi is null || WasapiAudioClientField is null || AudioClientInterfaceField is null)
        {
            return false;
        }

        IntPtr pUnk = IntPtr.Zero;
        IntPtr pClient2 = IntPtr.Zero;
        try
        {
            if (WasapiAudioClientField.GetValue(wasapi) is not AudioClient audioClient)
            {
                return false;
            }
            object? rawCom = AudioClientInterfaceField.GetValue(audioClient);
            if (rawCom is null || !Marshal.IsComObject(rawCom))
            {
                return false;
            }

            pUnk = Marshal.GetIUnknownForObject(rawCom);
            if (pUnk == IntPtr.Zero) return false;

            Guid iid = IidIAudioClient2;
            int qiHr = Marshal.QueryInterface(pUnk, in iid, out pClient2);
            if (qiHr < 0 || pClient2 == IntPtr.Zero)
            {
                return false;
            }

            IntPtr vtable = Marshal.ReadIntPtr(pClient2);
            if (vtable == IntPtr.Zero) return false;

            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, SetClientPropertiesVtableSlot * IntPtr.Size);
            if (fnPtr == IntPtr.Zero) return false;

            var setProperties = Marshal.GetDelegateForFunctionPointer<SetClientPropertiesDelegate>(fnPtr);
            var props = new AudioClientProperties
            {
                CbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                IsOffload = 0,
                Category = AudioCategoryMedia,
                Options = 0,
            };

            int hr = setProperties(pClient2, ref props);
            return hr >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pClient2 != IntPtr.Zero)
            {
                try { Marshal.Release(pClient2); } catch { }
            }
            if (pUnk != IntPtr.Zero)
            {
                try { Marshal.Release(pUnk); } catch { }
            }
        }
    }
}
