using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Tonewise.Audio;

public sealed record AudioOutputEndpoint(
    string Id,
    string FriendlyName,
    bool IsUsbDevice,
    bool IsDefault,
    bool IsAsio = false
);

public static class AudioDeviceRouter
{
    public const string AsioIdPrefix = "asio:";

    public static IReadOnlyList<AudioOutputEndpoint> EnumerateRenderEndpoints()
    {
        var list = new List<AudioOutputEndpoint>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch
            {
                // No active default endpoint
            }

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                {
                    string name = dev.FriendlyName;
                    bool isUsb =
                        name.Contains("USB", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("DAC", StringComparison.OrdinalIgnoreCase)
                        || dev.DeviceFriendlyName.Contains("USB", StringComparison.OrdinalIgnoreCase);
                    list.Add(new AudioOutputEndpoint(dev.ID, name, isUsb, dev.ID == defaultId, IsAsio: false));
                }
            }
        }
        catch
        {
            // Headless/sandboxed environment without WASAPI audio service
        }

        try
        {
            foreach (string driver in AsioOut.GetDriverNames())
            {
                if (string.IsNullOrWhiteSpace(driver)) continue;
                list.Add(new AudioOutputEndpoint(
                    Id: AsioIdPrefix + driver,
                    FriendlyName: $"ASIO: {driver}",
                    IsUsbDevice: true,
                    IsDefault: false,
                    IsAsio: true
                ));
            }
        }
        catch
        {
            // No ASIO drivers registered on host
        }

        return list;
    }

    internal static MMDevice? ResolveDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                return enumerator.GetDevice(deviceId);
            }
            catch
            {
                // Fallback to default endpoint
            }
        }
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class AudioDeviceChangeWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private int _disposed;

    public event Action? EndpointsChanged;

    public AudioDeviceChangeWatcher()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch
        {
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => EndpointsChanged?.Invoke();

    public void OnDeviceAdded(string pwstrDeviceId) => EndpointsChanged?.Invoke();

    public void OnDeviceRemoved(string deviceId) => EndpointsChanged?.Invoke();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render)
        {
            EndpointsChanged?.Invoke();
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Ignored to avoid notification storms on metadata/volume property updates
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_enumerator is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            try { _enumerator.Dispose(); } catch { }
        }
    }
}
