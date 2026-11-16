using System.Buffers.Binary;
using System.Text;
using NAudio.Wave;

namespace Tonewise.Audio;

/// <summary>
/// Streaming 1-bit DSD (.dsf / .dff) reader with a 64-tap Blackman-Harris windowed-sinc
/// low-pass decimation filter producing 64-bit internal DXD (352.8 kHz) output.
/// </summary>
public sealed class DsdStreamReader : ISampleProvider, IDisposable
{
    public const int TargetDxdSampleRateHz = 352_800;
    private const int FilterTaps = 64;

    private readonly Stream _stream;
    private readonly bool _isDsf;
    private readonly bool _lsbFirst;
    private readonly long _dataStartOffset;
    private readonly long _dataLengthBytes;
    private readonly int _blockSizePerChannel;
    private readonly int _dsdSampleRateHz;
    private readonly int _decimationFactor;
    private readonly long _totalDsdBitsPerChannel;
    private readonly double[] _firCoeffs;
    private readonly double[][] _bitHistories;
    private readonly int[] _histPos;
    private readonly byte[] _blockBuffer;

    private long _currentBitIndex;
    private long _blockBaseBitIndex = -1;
    private int _blockValidBytesPerChannel;

    public WaveFormat WaveFormat { get; }
    public int DsdSampleRateHz => _dsdSampleRateHz;
    public int Channels { get; }

    public TimeSpan TotalTime =>
        _dsdSampleRateHz > 0
            ? TimeSpan.FromSeconds(_totalDsdBitsPerChannel / (double)_dsdSampleRateHz)
            : TimeSpan.Zero;

    public TimeSpan CurrentTime
    {
        get => _dsdSampleRateHz > 0
            ? TimeSpan.FromSeconds(_currentBitIndex / (double)_dsdSampleRateHz)
            : TimeSpan.Zero;
        set
        {
            double frac = TotalTime.TotalSeconds > 0
                ? Math.Clamp(value.TotalSeconds / TotalTime.TotalSeconds, 0.0, 1.0)
                : 0.0;
            SeekFraction(frac);
        }
    }

    public DsdStreamReader(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("DSD audio file not found", filePath);
        }

        _stream = File.OpenRead(filePath);
        try
        {
            byte[] magic = new byte[4];
            if (_stream.Read(magic, 0, 4) < 4)
                throw new InvalidDataException("File too short to be DSD");

            if (Encoding.ASCII.GetString(magic) == "DSD ")
            {
                _isDsf = true;
                (Channels, _dsdSampleRateHz, _lsbFirst, _totalDsdBitsPerChannel, _blockSizePerChannel, _dataStartOffset, _dataLengthBytes) =
                    ParseDsfHeader(_stream);
            }
            else if (Encoding.ASCII.GetString(magic) == "FRM8")
            {
                _isDsf = false;
                (Channels, _dsdSampleRateHz, _totalDsdBitsPerChannel, _dataStartOffset, _dataLengthBytes) =
                    ParseDffHeader(_stream);
                _lsbFirst = false;
                _blockSizePerChannel = 1;
            }
            else
            {
                throw new InvalidDataException("Unsupported DSD container header (expected .dsf 'DSD ' or .dff 'FRM8')");
            }

            _decimationFactor = Math.Max(1, (int)Math.Round(_dsdSampleRateHz / (double)TargetDxdSampleRateHz));
            int effectiveOutRate = Math.Max(44100, _dsdSampleRateHz / _decimationFactor);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(effectiveOutRate, Channels);

            _firCoeffs = BuildBlackmanHarrisLowPass(FilterTaps, cutoffNorm: 0.45 / _decimationFactor);
            _bitHistories = new double[Channels][];
            _histPos = new int[Channels];
            for (int c = 0; c < Channels; c++)
            {
                _bitHistories[c] = new double[FilterTaps];
            }

            int bufLen = _isDsf ? Math.Max(Channels * _blockSizePerChannel, 4096) : Math.Max(Channels * 4096, 4096);
            _blockBuffer = new byte[bufLen];
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public void SeekFraction(double fraction)
    {
        double clamped = Math.Clamp(fraction, 0.0, 1.0);
        long targetBit = (long)(_totalDsdBitsPerChannel * clamped);
        targetBit -= targetBit % _decimationFactor;
        _currentBitIndex = Math.Clamp(targetBit, 0, _totalDsdBitsPerChannel);
        _blockBaseBitIndex = -1;
        for (int c = 0; c < Channels; c++)
        {
            Array.Clear(_bitHistories[c]);
            _histPos[c] = 0;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / Channels;
        int samplesWritten = 0;

        for (int f = 0; f < framesRequested; f++)
        {
            if (_currentBitIndex + _decimationFactor > _totalDsdBitsPerChannel)
            {
                break;
            }

            for (int d = 0; d < _decimationFactor; d++)
            {
                EnsureBlockLoadedForBit(_currentBitIndex);
                for (int ch = 0; ch < Channels; ch++)
                {
                    double bitSample = ReadChannelBitSample(_currentBitIndex, ch);
                    int p = _histPos[ch];
                    _bitHistories[ch][p] = bitSample;
                    _histPos[ch] = (p + 1) % FilterTaps;
                }
                _currentBitIndex++;
            }

            for (int ch = 0; ch < Channels; ch++)
            {
                double sum = 0.0;
                double[] hist = _bitHistories[ch];
                int idx = _histPos[ch] - 1;
                if (idx < 0) idx = FilterTaps - 1;

                for (int k = 0; k < FilterTaps; k++)
                {
                    sum += hist[idx] * _firCoeffs[k];
                    idx--;
                    if (idx < 0) idx = FilterTaps - 1;
                }

                // Scale with 6 dB ultrasonic headroom to match DXD mastering norms
                buffer[offset + samplesWritten++] = (float)(sum * 0.5);
            }
        }

        return samplesWritten;
    }

    private void EnsureBlockLoadedForBit(long bitIndex)
    {
        if (_isDsf)
        {
            long bitsPerBlock = (long)_blockSizePerChannel * 8L;
            long blockNumber = bitIndex / bitsPerBlock;
            long blockStartBit = blockNumber * bitsPerBlock;
            if (_blockBaseBitIndex == blockStartBit) return;

            long stride = (long)_blockSizePerChannel * Channels;
            long fileOffset = _dataStartOffset + blockNumber * stride;
            _stream.Seek(fileOffset, SeekOrigin.Begin);
            int toRead = (int)Math.Min(stride, Math.Max(0, _dataStartOffset + _dataLengthBytes - fileOffset));
            int read = ReadExactOrAvailable(_stream, _blockBuffer, toRead);
            _blockBaseBitIndex = blockStartBit;
            _blockValidBytesPerChannel = Channels > 0 ? read / Channels : 0;
        }
        else
        {
            const int dffBytesPerCh = 4096;
            long bitsPerChunk = dffBytesPerCh * 8L;
            long blockNumber = bitIndex / bitsPerChunk;
            long blockStartBit = blockNumber * bitsPerChunk;
            if (_blockBaseBitIndex == blockStartBit) return;

            long stride = (long)dffBytesPerCh * Channels;
            long fileOffset = _dataStartOffset + blockNumber * stride;
            _stream.Seek(fileOffset, SeekOrigin.Begin);
            int toRead = (int)Math.Min(stride, Math.Max(0, _dataStartOffset + _dataLengthBytes - fileOffset));
            int read = ReadExactOrAvailable(_stream, _blockBuffer, toRead);
            _blockBaseBitIndex = blockStartBit;
            _blockValidBytesPerChannel = Channels > 0 ? read / Channels : 0;
        }
    }

    private double ReadChannelBitSample(long bitIndex, int channel)
    {
        long relBit = bitIndex - _blockBaseBitIndex;
        int byteInBlock = (int)(relBit >> 3);
        int bitInByte = (int)(relBit & 7);
        if (byteInBlock >= _blockValidBytesPerChannel) return 0.0;

        int bufferIdx = _isDsf
            ? channel * _blockSizePerChannel + byteInBlock
            : byteInBlock * Channels + channel;

        byte b = _blockBuffer[bufferIdx];
        int bit = _lsbFirst
            ? ((b >> bitInByte) & 1)
            : ((b >> (7 - bitInByte)) & 1);
        return bit == 1 ? 1.0 : -1.0;
    }

    private static int ReadExactOrAvailable(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static (int Channels, int SampleRate, bool LsbFirst, long BitsPerChannel, int BlockSize, long DataOffset, long DataLen)
        ParseDsfHeader(Stream s)
    {
        Span<byte> dsdChunk = stackalloc byte[24];
        if (s.Read(dsdChunk) < 24) throw new InvalidDataException("Truncated DSF header");

        Span<byte> fmtHdr = stackalloc byte[52];
        if (s.Read(fmtHdr) < 52 || Encoding.ASCII.GetString(fmtHdr[0..4]) != "fmt ")
            throw new InvalidDataException("Missing DSF 'fmt ' chunk");

        int channels = Math.Clamp((int)BinaryPrimitives.ReadUInt32LittleEndian(fmtHdr.Slice(24, 4)), 1, 8);
        int sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmtHdr.Slice(28, 4));
        uint bps = BinaryPrimitives.ReadUInt32LittleEndian(fmtHdr.Slice(32, 4));
        bool lsbFirst = bps == 1;
        long sampleCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(fmtHdr.Slice(36, 8));
        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmtHdr.Slice(44, 4));

        Span<byte> dataHdr = stackalloc byte[12];
        if (s.Read(dataHdr) < 12 || Encoding.ASCII.GetString(dataHdr[0..4]) != "data")
            throw new InvalidDataException("Missing DSF 'data' chunk");

        long dataChunkSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(dataHdr.Slice(4, 8));
        long payloadBytes = Math.Max(0, dataChunkSize - 12);
        return (channels, Math.Max(sampleRate, 2_822_400), lsbFirst, sampleCount, Math.Max(blockSize, 4096), s.Position, payloadBytes);
    }

    private static (int Channels, int SampleRate, long BitsPerChannel, long DataOffset, long DataLen)
        ParseDffHeader(Stream s)
    {
        Span<byte> frm = stackalloc byte[12];
        if (s.Read(frm) < 12 || Encoding.ASCII.GetString(frm.Slice(8, 4)) != "DSD ")
            throw new InvalidDataException("Invalid DFF 'FRM8/DSD ' header");

        int sampleRate = 2_822_400;
        int channels = 2;
        long dataOffset = 0;
        long dataLen = 0;

        Span<byte> ck = stackalloc byte[12];
        Span<byte> sub = stackalloc byte[12];
        Span<byte> fsB = stackalloc byte[4];
        Span<byte> chB = stackalloc byte[2];

        while (s.Position + 12 <= s.Length)
        {
            if (s.Read(ck) < 12) break;
            string id = Encoding.ASCII.GetString(ck[0..4]);
            long size = (long)BinaryPrimitives.ReadUInt64BigEndian(ck.Slice(4, 8));
            long nextPos = s.Position + size + (size & 1);

            if (id == "PROP")
            {
                long propEnd = Math.Min(s.Length, s.Position + size);
                s.Seek(4, SeekOrigin.Current); // 'SND '
                while (s.Position + 12 <= propEnd)
                {
                    if (s.Read(sub) < 12) break;
                    string subId = Encoding.ASCII.GetString(sub[0..4]);
                    long subSz = (long)BinaryPrimitives.ReadUInt64BigEndian(sub.Slice(4, 8));
                    long subNext = s.Position + subSz + (subSz & 1);
                    if (subId == "FS  " && subSz >= 4)
                    {
                        if (s.Read(fsB) == 4) sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(fsB);
                    }
                    else if (subId == "CHNL" && subSz >= 2)
                    {
                        if (s.Read(chB) == 2) channels = Math.Clamp((int)BinaryPrimitives.ReadUInt16BigEndian(chB), 1, 8);
                    }
                    s.Position = subNext;
                }
            }
            else if (id == "DSD ")
            {
                dataOffset = s.Position;
                dataLen = size;
                break;
            }

            s.Position = nextPos;
        }

        if (dataOffset == 0 || dataLen == 0)
            throw new InvalidDataException("Missing DFF 'DSD ' audio data chunk");

        long bitsPerCh = channels > 0 ? (dataLen * 8L) / channels : 0;
        return (channels, sampleRate, bitsPerCh, dataOffset, dataLen);
    }

    private static double[] BuildBlackmanHarrisLowPass(int taps, double cutoffNorm)
    {
        var coeffs = new double[taps];
        int center = (taps - 1) / 2;
        double sum = 0.0;
        for (int i = 0; i < taps; i++)
        {
            double n = i - center;
            double sinc = Math.Abs(n) < 1e-9 ? 2.0 * cutoffNorm : Math.Sin(2.0 * Math.PI * cutoffNorm * n) / (Math.PI * n);
            double wArg = 2.0 * Math.PI * i / (taps - 1);
            double window = 0.35875 - 0.48829 * Math.Cos(wArg) + 0.14128 * Math.Cos(2.0 * wArg) - 0.01168 * Math.Cos(3.0 * wArg);
            coeffs[i] = sinc * window;
            sum += coeffs[i];
        }
        if (Math.Abs(sum) > 1e-12)
        {
            for (int i = 0; i < taps; i++) coeffs[i] /= sum;
        }
        return coeffs;
    }

    public void Dispose() => _stream.Dispose();
}
