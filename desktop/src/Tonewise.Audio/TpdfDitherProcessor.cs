namespace Tonewise.Audio;

/// <summary>
/// ±1 LSB Triangular Probability Density Function (TPDF) dither applied prior to integer PCM quantization.
/// Eliminates deterministic harmonic truncation distortion when dropping from 64-bit DSP float to 16/24-bit DACs.
/// </summary>
public sealed class TpdfDitherProcessor
{
    private ulong _rngState;
    private volatile bool _isEnabled = true;
    private int _targetBitDepth = 24;
    private double _lsb = 1.0 / (1L << 23);

    public TpdfDitherProcessor(int? deterministicSeed = null)
    {
        ulong seed = deterministicSeed.HasValue
            ? (uint)deterministicSeed.Value | 0x9E3779B97F4A7C15UL
            : (ulong)Random.Shared.NextInt64();
        _rngState = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public int TargetBitDepth
    {
        get => _targetBitDepth;
        set
        {
            _targetBitDepth = value switch
            {
                16 => 16,
                20 => 20,
                _ => 24,
            };
            _lsb = 1.0 / (1L << (_targetBitDepth - 1));
        }
    }

    public double ProcessSample(double sample)
    {
        if (!_isEnabled)
        {
            return Math.Clamp(sample, -1.0, 1.0 - _lsb);
        }

        const double Inv2Pow53 = 1.0 / (1UL << 53);
        double u1 = ((NextUInt64() >> 11) * Inv2Pow53) - 0.5;
        double u2 = ((NextUInt64() >> 11) * Inv2Pow53) - 0.5;
        double dither = (u1 + u2) * _lsb;
        return Math.Clamp(sample + dither, -1.0, 1.0 - _lsb);
    }

    private ulong NextUInt64()
    {
        ulong x = _rngState;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _rngState = x;
        return x * 0x2545F4914F6CDD1DUL;
    }
}
