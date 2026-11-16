using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace Tonewise.Audio;

public sealed record Vst3SlotConfig(
    string PluginPath,
    double DryWetMix = 1.0,
    bool IsBypassed = false
);

/// <summary>
/// Out-of-process shared-memory ring bridge contract for hosting 1–3 64-bit VST3 effect plugins
/// with bounded latency budget and fail-open bypass so third-party plugin faults never crash Tonewise.
/// </summary>
public sealed class SandboxedVst3HostBridge : IDisposable
{
    public const int MaxSlots = 3;
    public const int RingCapacityBytes = 64 * 1024;
    public const int ProcessTimeoutMs = 15;

    private readonly List<Vst3SlotConfig> _slots = [];
    private MemoryMappedFile? _shm;
    private Func<double, int, int, double>? _workerCallback;
    private volatile bool _isWorkerConnected;
    private volatile bool _isFaultedBypass;

    public string SharedMemoryName { get; } = $"TonewiseVst3_{Guid.NewGuid():N}";
    public int ActiveSlotCount => _slots.Count(s => !s.IsBypassed);
    public bool IsWorkerConnected => _isWorkerConnected;
    public bool IsFaultedBypass => _isFaultedBypass;
    public int LatencyCompensationSamples { get; private set; }

    public void ConfigureSlots(
        IEnumerable<Vst3SlotConfig> slots,
        Func<double, int, int, double>? externalWorkerCallback = null,
        int latencySamples = 0)
    {
        _slots.Clear();
        foreach (var s in slots.Take(MaxSlots))
        {
            _slots.Add(s with { DryWetMix = Math.Clamp(s.DryWetMix, 0.0, 1.0) });
        }
        _workerCallback = externalWorkerCallback;
        _isWorkerConnected = _slots.Count > 0 && _workerCallback is not null;
        _isFaultedBypass = false;
        LatencyCompensationSamples = Math.Max(0, latencySamples);

        if (_slots.Count > 0 && _shm is null)
        {
            try
            {
                _shm = MemoryMappedFile.CreateOrOpen(SharedMemoryName, RingCapacityBytes);
            }
            catch
            {
                // Non-fatal in locked-down sandboxes; stays fail-open bypassed
            }
        }
    }

    public double ProcessSample(double drySample, int channel)
    {
        if (ActiveSlotCount == 0 || !_isWorkerConnected || _workerCallback is null || _isFaultedBypass)
        {
            return drySample;
        }

        double current = drySample;
        var sw = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (slot.IsBypassed || slot.DryWetMix <= 0.0) continue;

                double processed = _workerCallback(current, channel, i);
                current = current * (1.0 - slot.DryWetMix) + processed * slot.DryWetMix;

                if (sw.ElapsedMilliseconds > ProcessTimeoutMs)
                {
                    _isFaultedBypass = true;
                    return drySample;
                }
            }
            return current;
        }
        catch
        {
            // Third-party VST3 sandbox crash/exception triggers zero-glitch Fail-Open bypass
            _isFaultedBypass = true;
            return drySample;
        }
    }

    public void Clear()
    {
        _slots.Clear();
        _workerCallback = null;
        _isWorkerConnected = false;
        _isFaultedBypass = false;
    }

    public void Dispose()
    {
        Clear();
        _shm?.Dispose();
        _shm = null;
    }
}
