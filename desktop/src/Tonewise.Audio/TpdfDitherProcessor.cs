namespace Tonewise.Audio;

/// <summary>
/// ±1 LSB Triangular Probability Density Function (TPDF) dither applied prior to integer PCM quantization.
/// Eliminates deterministic harmonic truncation distortion when dropping from 64-bit DSP float to 16/24-bit DACs.
/// </summary>
public sealed class TpdfDitherProcessor
{
    private readonly Random _rng;
    private volatile bool _isEnabled = true;
    private int _targetBitDepth = 24;
    private double _lsb = 1.0 / (1L << 23);

    public TpdfDitherProcessor(int? deterministicSeed = null)
    {
        _rng = deterministicSeed.HasValue ? new Random(deterministicSeed.Value) : new Random();
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

        double u1 = _rng.NextDouble() - 0.5;
        double u2 = _rng.NextDouble() - 0.5;
        double dither = (u1 + u2) * _lsb;
        return Math.Clamp(sample + dither, -1.0, 1.0 - _lsb);
    }
}
