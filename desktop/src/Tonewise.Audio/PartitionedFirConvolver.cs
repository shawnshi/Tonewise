using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace Tonewise.Audio;

/// <summary>
/// Zero-latency 64-bit double-precision partitioned FIR convolver (up to 262,144 taps per channel)
/// using direct circular convolution for the initial 256-tap head and Overlap-Add radix-2 FFT for tails.
/// </summary>
public sealed class PartitionedFirConvolver
{
    public const int MaxTaps = 262_144;
    private const int PartitionSize = 256;
    private const int FftSize = PartitionSize * 2;

    private readonly int _channels;
    private ChannelConvolver[]? _channelConvolvers;
    private volatile bool _isEnabled;
    private double _dryWetMix = 1.0;

    public PartitionedFirConvolver(int channels)
    {
        _channels = Math.Clamp(channels, 1, 8);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public double DryWetMix
    {
        get => Volatile.Read(ref _dryWetMix);
        set => Volatile.Write(ref _dryWetMix, Math.Clamp(value, 0.0, 1.0));
    }

    public int LoadedTapCount { get; private set; }
    public string? LoadedImpulsePath { get; private set; }

    public void Clear()
    {
        _channelConvolvers = null;
        LoadedTapCount = 0;
        LoadedImpulsePath = null;
        _isEnabled = false;
    }

    public void LoadImpulse(double[] leftIr, double[] rightIr, bool normalizeEnergy = true, string? sourceLabel = null)
    {
        ArgumentNullException.ThrowIfNull(leftIr);
        ArgumentNullException.ThrowIfNull(rightIr);
        if (leftIr.Length == 0 || rightIr.Length == 0)
        {
            Clear();
            return;
        }

        double[] lTrimmed = TrimToMaxTaps(leftIr);
        double[] rTrimmed = TrimToMaxTaps(rightIr);
        if (normalizeEnergy)
        {
            double sharedNorm = Math.Max(ComputeL2Energy(lTrimmed), ComputeL2Energy(rTrimmed));
            if (sharedNorm > 1e-12 && Math.Abs(sharedNorm - 1.0) > 1e-6)
            {
                ScaleByInverseNorm(lTrimmed, sharedNorm);
                ScaleByInverseNorm(rTrimmed, sharedNorm);
            }
        }

        var convolvers = new ChannelConvolver[_channels];
        for (int ch = 0; ch < _channels; ch++)
        {
            double[] ir = (ch % 2 == 0) ? lTrimmed : rTrimmed;
            convolvers[ch] = new ChannelConvolver(ir);
        }

        _channelConvolvers = convolvers;
        LoadedTapCount = Math.Max(lTrimmed.Length, rTrimmed.Length);
        LoadedImpulsePath = sourceLabel;
        _isEnabled = true;
    }

    public void LoadImpulseFromWav(string wavPath, bool normalizeEnergy = true, int? targetSampleRateHz = null)
    {
        var (left, right) = ReadImpulseArraysFromWav(wavPath, targetSampleRateHz);
        LoadImpulse(left, right, normalizeEnergy, wavPath);
    }

    public static (double[] Left, double[] Right) ReadImpulseArraysFromWav(string wavPath, int? targetSampleRateHz = null)
    {
        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException("FIR impulse file not found", wavPath);
        }

        using var reader = new AudioFileReader(wavPath);
        int srcRate = reader.WaveFormat.SampleRate;
        int wavChannels = Math.Max(reader.WaveFormat.Channels, 1);
        float[] temp = new float[8192];
        var left = new List<double>();
        var right = new List<double>();

        while (left.Count < MaxTaps)
        {
            int read = reader.Read(temp, 0, temp.Length);
            if (read <= 0) break;

            for (int i = 0; i + wavChannels <= read && left.Count < MaxTaps; i += wavChannels)
            {
                double lVal = temp[i];
                double rVal = wavChannels > 1 ? temp[i + 1] : lVal;
                left.Add(lVal);
                right.Add(rVal);
            }
        }

        double[] lArr = left.ToArray();
        double[] rArr = right.ToArray();
        if (targetSampleRateHz is > 0 && srcRate > 0 && srcRate != targetSampleRateHz.Value && lArr.Length > 0)
        {
            lArr = ResampleImpulseLinear(lArr, srcRate, targetSampleRateHz.Value);
            rArr = ResampleImpulseLinear(rArr, srcRate, targetSampleRateHz.Value);
        }

        return (lArr, rArr);
    }

    /// <summary>
    /// Loads either a `.wav` impulse file or a `.sofa` (TW-SOFA JSON/binary HRIR or HDF5 BRIR) impulse.
    /// </summary>
    public void LoadImpulseFromSofaOrWav(string filePath, bool normalizeEnergy = true, int? targetSampleRateHz = null)
    {
        var (left, right) = ReadImpulseArraysFromSofaOrWav(filePath, targetSampleRateHz);
        LoadImpulse(left, right, normalizeEnergy, filePath);
    }

    public static (double[] Left, double[] Right) ReadImpulseArraysFromSofaOrWav(string filePath, int? targetSampleRateHz = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Impulse file not found", filePath);

        if (!filePath.EndsWith(".sofa", StringComparison.OrdinalIgnoreCase))
        {
            return ReadImpulseArraysFromWav(filePath, targetSampleRateHz);
        }

        byte[] raw = File.ReadAllBytes(filePath);
        ReadOnlySpan<byte> jsonSpan = raw;
        if (jsonSpan.Length >= 3 && jsonSpan[0] == 0xEF && jsonSpan[1] == 0xBB && jsonSpan[2] == 0xBF)
        {
            jsonSpan = jsonSpan[3..];
        }
        // 1. Compact TW-SOFA JSON envelope (`{ "left_ir": [...], "right_ir": [...] }`)
        if (jsonSpan.Length > 0 && jsonSpan[0] == (byte)'{')
        {
            using var doc = JsonDocument.Parse(jsonSpan.ToArray());
            double[] l = ReadJsonDoubleArray(doc.RootElement, "left_ir");
            double[] r = doc.RootElement.TryGetProperty("right_ir", out _)
                ? ReadJsonDoubleArray(doc.RootElement, "right_ir")
                : l;
            return (l, r);
        }

        // 2. Validate HDF5 SOFA signature (\x89HDF\r\n\x1a\n) or TW-SOFA binary impulse block
        ReadOnlySpan<byte> hdfMagic = [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];
        if (raw.Length >= 8 && raw.AsSpan(0, 8).SequenceEqual(hdfMagic))
        {
            // Look for trailing TWIR float32 stereo payload marker if present in ASH/SOFA exports
            int markerIdx = raw.AsSpan().IndexOf("TWIR"u8);
            if (markerIdx >= 0 && markerIdx + 12 <= raw.Length)
            {
                int taps = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(markerIdx + 4, 4)), 1, MaxTaps);
                int chCount = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(markerIdx + 8, 4)), 1, 2);
                int dataPos = markerIdx + 12;
                double[] l = new double[taps];
                double[] r = new double[taps];
                for (int i = 0; i < taps && dataPos + 4 <= raw.Length; i++)
                {
                    l[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(dataPos, 4)));
                    dataPos += 4;
                    if (chCount == 2 && dataPos + 4 <= raw.Length)
                    {
                        r[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(dataPos, 4)));
                        dataPos += 4;
                    }
                    else
                    {
                        r[i] = l[i];
                    }
                }
                return (l, r);
            }
            throw new InvalidDataException("HDF5 .sofa dataset requires pre-extracted WAV or TWIR stereo impulse block");
        }

        throw new InvalidDataException("Unrecognized .sofa impulse container");
    }

    private static double[] ReadJsonDoubleArray(JsonElement root, string propName)
    {
        var list = new List<double>();
        if (root.TryGetProperty(propName, out var arr))
        {
            foreach (var v in arr.EnumerateArray())
            {
                list.Add(v.GetDouble());
            }
        }
        return list.ToArray();
    }

    public double ProcessSample(double drySample, int channel)
    {
        if (!_isEnabled || _channelConvolvers is null || channel < 0 || channel >= _channelConvolvers.Length)
        {
            return drySample;
        }

        double wet = _channelConvolvers[channel].ProcessSample(drySample);
        double mix = DryWetMix;
        return drySample * (1.0 - mix) + wet * mix;
    }

    public void Reset()
    {
        if (_channelConvolvers is null) return;
        foreach (var conv in _channelConvolvers)
        {
            conv.Reset();
        }
    }

    public void ResetState() => Reset();

    private static double[] ResampleImpulseLinear(double[] ir, int srcRate, int dstRate)
    {
        if (ir.Length <= 1 || srcRate <= 0 || dstRate <= 0 || srcRate == dstRate) return ir;
        double ratio = dstRate / (double)srcRate;
        int outLen = Math.Clamp((int)Math.Round(ir.Length * ratio), 1, MaxTaps);
        double[] resampled = new double[outLen];
        double gainScale = srcRate / (double)dstRate;
        for (int i = 0; i < outLen; i++)
        {
            double srcPos = i / ratio;
            int idx0 = (int)Math.Floor(srcPos);
            double frac = srcPos - idx0;
            double s0 = idx0 < ir.Length ? ir[idx0] : 0.0;
            double s1 = idx0 + 1 < ir.Length ? ir[idx0 + 1] : 0.0;
            resampled[i] = (s0 + (s1 - s0) * frac) * gainScale;
        }
        return resampled;
    }

    private static double[] TrimToMaxTaps(double[] ir)
    {
        int len = Math.Min(ir.Length, MaxTaps);
        double[] copy = new double[len];
        Array.Copy(ir, copy, len);
        return copy;
    }

    private static double ComputeL2Energy(double[] ir)
    {
        double sumSq = 0.0;
        for (int i = 0; i < ir.Length; i++)
        {
            sumSq += ir[i] * ir[i];
        }
        return Math.Sqrt(sumSq);
    }

    private static void ScaleByInverseNorm(double[] ir, double norm)
    {
        for (int i = 0; i < ir.Length; i++)
        {
            ir[i] /= norm;
        }
    }

    private sealed class ChannelConvolver
    {
        private readonly double[] _headIr;
        private readonly double[] _headRing;
        private int _headCursor;

        private readonly Complex[][] _tailIrFfts;
        private readonly Complex[][] _fdl;
        private readonly double[] _blockIn;
        private readonly double[] _tailOutReady;
        private readonly double[] _prevUpperHalf;
        private readonly Complex[] _fftScratch;
        private int _blockPos;
        private int _fdlCursor;

        public ChannelConvolver(double[] ir)
        {
            int headLen = Math.Min(ir.Length, PartitionSize);
            _headIr = new double[headLen];
            Array.Copy(ir, _headIr, headLen);
            _headRing = new double[headLen];

            int tailRemaining = Math.Max(0, ir.Length - PartitionSize);
            int tailCount = (tailRemaining + PartitionSize - 1) / PartitionSize;

            _tailIrFfts = new Complex[tailCount][];
            _fdl = new Complex[tailCount][];
            _blockIn = new double[PartitionSize];
            _tailOutReady = new double[PartitionSize];
            _prevUpperHalf = new double[PartitionSize];
            _fftScratch = new Complex[FftSize];

            for (int p = 0; p < tailCount; p++)
            {
                int srcStart = (p + 1) * PartitionSize;
                int copyLen = Math.Min(PartitionSize, ir.Length - srcStart);
                var spectrum = new Complex[FftSize];
                for (int i = 0; i < copyLen; i++)
                {
                    spectrum[i] = new Complex(ir[srcStart + i], 0.0);
                }
                FftInPlace(spectrum, inverse: false);
                _tailIrFfts[p] = spectrum;
                _fdl[p] = new Complex[FftSize];
            }
        }

        public double ProcessSample(double x)
        {
            int headLen = _headIr.Length;
            _headRing[_headCursor] = x;

            double headSum = 0.0;
            int idx = _headCursor;
            for (int m = 0; m < headLen; m++)
            {
                headSum += _headRing[idx] * _headIr[m];
                idx--;
                if (idx < 0) idx = headLen - 1;
            }
            _headCursor = (_headCursor + 1) % headLen;

            if (_tailIrFfts.Length == 0)
            {
                return headSum;
            }

            double tailContribution = _tailOutReady[_blockPos];
            _blockIn[_blockPos] = x;
            _blockPos++;

            if (_blockPos == PartitionSize)
            {
                ComputeNextTailBlock();
                _blockPos = 0;
            }

            return headSum + tailContribution;
        }

        private void ComputeNextTailBlock()
        {
            int tailCount = _tailIrFfts.Length;
            var currentFft = _fdl[_fdlCursor];
            for (int i = 0; i < PartitionSize; i++)
            {
                currentFft[i] = new Complex(_blockIn[i], 0.0);
            }
            Array.Clear(currentFft, PartitionSize, PartitionSize);
            FftInPlace(currentFft, inverse: false);

            Array.Clear(_fftScratch);
            for (int p = 0; p < tailCount; p++)
            {
                int slot = (_fdlCursor - p + tailCount) % tailCount;
                Complex[] xSpec = _fdl[slot];
                Complex[] hSpec = _tailIrFfts[p];
                for (int k = 0; k < FftSize; k++)
                {
                    _fftScratch[k] += xSpec[k] * hSpec[k];
                }
            }

            FftInPlace(_fftScratch, inverse: true);
            for (int i = 0; i < PartitionSize; i++)
            {
                _tailOutReady[i] = _prevUpperHalf[i] + _fftScratch[i].Real;
                _prevUpperHalf[i] = _fftScratch[PartitionSize + i].Real;
            }

            _fdlCursor = (_fdlCursor + 1) % tailCount;
        }

        public void Reset()
        {
            Array.Clear(_headRing);
            _headCursor = 0;
            Array.Clear(_blockIn);
            Array.Clear(_tailOutReady);
            Array.Clear(_prevUpperHalf);
            _blockPos = 0;
            _fdlCursor = 0;
            for (int p = 0; p < _fdl.Length; p++)
            {
                Array.Clear(_fdl[p]);
            }
        }
    }

    private static void FftInPlace(Complex[] data, bool inverse)
    {
        int n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }
            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        double sign = inverse ? 2.0 * Math.PI : -2.0 * Math.PI;
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = sign / len;
            var wLen = new Complex(Math.Cos(ang), Math.Sin(ang));
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int j = 0; j < half; j++)
                {
                    Complex u = data[i + j];
                    Complex v = data[i + j + half] * w;
                    data[i + j] = u + v;
                    data[i + j + half] = u - v;
                    w *= wLen;
                }
            }
        }

        if (inverse)
        {
            double invN = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                data[i] *= invN;
            }
        }
    }
}
