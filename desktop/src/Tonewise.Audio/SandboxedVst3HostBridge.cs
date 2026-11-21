using System.Diagnostics;

namespace Tonewise.Audio;

public sealed record Vst3SlotConfig(
    string PluginPath,
    double DryWetMix = 1.0,
    bool IsBypassed = false
);

/// <summary>
/// Bounded-latency in-process timeout circuit-breaker bridge contract for optional 64-bit VST3 effect slots
/// (provides fault-open bypass and lock-free snapshot swapping, not an OS-isolated child process).
/// </summary>
public sealed class SandboxedVst3HostBridge : IDisposable
{
    public const int MaxSlots = 3;
    public const int ProcessTimeoutMs = 15;
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    private sealed record BridgeSnapshot(
        Vst3SlotConfig[] Slots,
        int ActiveSlotCount,
        Func<double, int, int, double>? WorkerCallback,
        bool IsWorkerConnected,
        int LatencySamples);

    private BridgeSnapshot _snapshot = new([], 0, null, false, 0);
    private volatile bool _isFaultedBypass;
    private long _watchdogSampleCounter;

    public int ActiveSlotCount => Volatile.Read(ref _snapshot).ActiveSlotCount;
    public bool IsWorkerConnected => Volatile.Read(ref _snapshot).IsWorkerConnected;
    public bool IsFaultedBypass => _isFaultedBypass;
    public int LatencyCompensationSamples => Volatile.Read(ref _snapshot).LatencySamples;

    public void ConfigureSlots(
        IEnumerable<Vst3SlotConfig> slots,
        Func<double, int, int, double>? externalWorkerCallback = null,
        int latencySamples = 0)
    {
        var list = new List<Vst3SlotConfig>(MaxSlots);
        int active = 0;
        foreach (var s in slots.Take(MaxSlots))
        {
            var clamped = s with { DryWetMix = Math.Clamp(s.DryWetMix, 0.0, 1.0) };
            list.Add(clamped);
            if (!clamped.IsBypassed) active++;
        }
        bool connected = list.Count > 0 && externalWorkerCallback is not null;
        Volatile.Write(
            ref _snapshot,
            new BridgeSnapshot(list.ToArray(), active, externalWorkerCallback, connected, Math.Max(0, latencySamples)));
        _isFaultedBypass = false;
        Interlocked.Exchange(ref _watchdogSampleCounter, 0);
    }

    public double ProcessSample(double drySample, int channel)
    {
        var snap = Volatile.Read(ref _snapshot);
        if (snap.ActiveSlotCount == 0 || !snap.IsWorkerConnected || snap.WorkerCallback is null || _isFaultedBypass)
        {
            return drySample;
        }

        bool measureWatchdog = (Interlocked.Increment(ref _watchdogSampleCounter) - 1 & 255) == 0;
        long startTicks = measureWatchdog ? Stopwatch.GetTimestamp() : 0;
        double current = drySample;
        try
        {
            var slots = snap.Slots;
            var cb = snap.WorkerCallback;
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot.IsBypassed || slot.DryWetMix <= 0.0) continue;

                double processed = cb(current, channel, i);
                current = current * (1.0 - slot.DryWetMix) + processed * slot.DryWetMix;

                if (measureWatchdog && (Stopwatch.GetTimestamp() - startTicks) * MsPerTick > ProcessTimeoutMs)
                {
                    _isFaultedBypass = true;
                    return drySample;
                }
            }
            return current;
        }
        catch
        {
            // Third-party VST3 callback exception triggers zero-glitch Fail-Open bypass
            _isFaultedBypass = true;
            return drySample;
        }
    }

    public void Clear()
    {
        Volatile.Write(ref _snapshot, new BridgeSnapshot([], 0, null, false, 0));
        _isFaultedBypass = false;
        Interlocked.Exchange(ref _watchdogSampleCounter, 0);
    }

    public void Dispose()
    {
        Clear();
    }
}
