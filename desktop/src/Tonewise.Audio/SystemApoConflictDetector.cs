using Microsoft.Win32;

namespace Tonewise.Audio;

public enum AudioOutputMode
{
    WasapiShared,
    WasapiExclusive,
}

public sealed record ApoConflictStatus(bool HasConflict, string? WarningMessage);

/// <summary>
/// Detects whether system-level Equalizer APO is attached to the target WASAPI Shared endpoint
/// so Tonewise can guard against accidental double-EQ filtering.
/// </summary>
public static class SystemApoConflictDetector
{
    private const string EqualizerApoRegistryPath = @"SOFTWARE\EqualizerAPO\Child APOs";

    public static ApoConflictStatus CheckEndpointConflict(
        string? mmDeviceId,
        AudioOutputMode outputMode,
        IEnumerable<string>? injectedRegistrySubkeys = null)
    {
        if (outputMode != AudioOutputMode.WasapiShared || string.IsNullOrWhiteSpace(mmDeviceId))
        {
            return new ApoConflictStatus(false, null);
        }

        string normalizedGuid = ExtractDeviceGuidToken(mmDeviceId);
        IEnumerable<string> subKeys = injectedRegistrySubkeys ?? EnumerateEqualizerApoEndpointsFromRegistry();

        foreach (string key in subKeys)
        {
            if (key.Contains(normalizedGuid, StringComparison.OrdinalIgnoreCase)
                || key.Contains(mmDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return new ApoConflictStatus(
                    true,
                    "检测到系统级 Equalizer APO 已挂载当前共享端点，建议切换至 WASAPI Exclusive 或暂停内置同类耳机 EQ 以防双重滤波"
                );
            }
        }

        return new ApoConflictStatus(false, null);
    }

    private static string ExtractDeviceGuidToken(string mmDeviceId)
    {
        int lastDot = mmDeviceId.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < mmDeviceId.Length
            ? mmDeviceId[(lastDot + 1)..].Trim()
            : mmDeviceId.Trim();
    }

    private static IReadOnlyList<string> EnumerateEqualizerApoEndpointsFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var apoKey = baseKey.OpenSubKey(EqualizerApoRegistryPath, writable: false);
            return apoKey?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }
}
