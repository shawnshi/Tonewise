using System.Diagnostics;

namespace Tonewise.Audio;

public sealed record Vst3SlotConfig(
    string PluginPath,
    double DryWetMix = 1.0,
    bool IsBypassed = false
);

/// <summary>
/// Bounded latency fail-open host bridge contract for optional 64-bit VST3 effect slots.
/// </summary>
public sealed class SandboxedVst3HostBridge : IDisposable
{
    public const int MaxSlots = 3;
    public const int ProcessTimeoutMs = 15;

    private readonly List<Vst3SlotConfig> _slots = [];
    private Func<double, int, int, double>? _workerCallback;
    private volatile int _activeSlotCount;
    private volatile bool _isWorkerConnected;
    private volatile bool _isFaultedBypass;

    public int ActiveSlotCount => _activeSlotCount;
    public bool IsWorkerConnected => _isWorkerConnected;
    public bool IsFaultedBypass => _isFaultedBypass;
    public int LatencyCompensationSamples { get; private set; }

    public void ConfigureSlots(
        IEnumerable<Vst3SlotConfig> slots,
        Func<double, int, int, double>? externalWorkerCallback = null,
        int latencySamples = 0)
    {
        _slots.Clear();
        int active = 0;
        foreach (var s in slots.Take(MaxSlots))
        {
            var clamped = s with { DryWetMix = Math.Clamp(s.DryWetMix, 0.0, 1.0) };
            _slots.Add(clamped);
            if (!clamped.IsBypassed) active++;
        }
        _activeSlotCount = active;
        _workerCallback = externalWorkerCallback;
        _isWorkerConnected = _slots.Count > 0 && _workerCallback is not null;
        _isFaultedBypass = false;
        LatencyCompensationSamples = Math.Max(0, latencySamples);
    }

    public double ProcessSample(double drySample, int channel)
    {
        if (_activeSlotCount == 0 || !_isWorkerConnected || _workerCallback is null || _isFaultedBypass)
        {
            return drySample;
        }

        double current = drySample;
        long startTicks = Stopwatch.GetTimestamp();
        double msPerTick = 1000.0 / Stopwatch.Frequency;
        try
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (slot.IsBypassed || slot.DryWetMix <= 0.0) continue;

                double processed = _workerCallback(current, channel, i);
                current = current * (1.0 - slot.DryWetMix) + processed * slot.DryWetMix;

                if ((Stopwatch.GetTimestamp() - startTicks) * msPerTick > ProcessTimeoutMs)
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
        _activeSlotCount = 0;
        _workerCallback = null;
        _isWorkerConnected = false;
        _isFaultedBypass = false;
    }

    public void Dispose()
    {
        Clear();
    }
}
