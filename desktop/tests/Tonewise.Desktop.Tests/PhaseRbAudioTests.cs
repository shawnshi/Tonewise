using System.Buffers.Binary;
using System.Text;
using Tonewise.Audio;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class PhaseRbAudioTests
{
    [Fact]
    public void DsdStreamReader_ParsesDsfAndDffStreams_DecimatesTo352kHzDxd_AndSeeksCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_rb_dsd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Minimal valid stereo DSF file (`4096 * 8 = 32768` bits/channel at 2.8224 MHz DSD64)
            string dsfPath = Path.Combine(tempDir, "test_dsd64.dsf");
            WriteSyntheticDsf(dsfPath, channels: 2, sampleRateHz: 2_822_400, blockBytesPerCh: 4096);

            using (var dsf = new DsdStreamReader(dsfPath))
            {
                Assert.Equal(2_822_400, dsf.DsdSampleRateHz);
                Assert.Equal(352_800, dsf.WaveFormat.SampleRate);
                Assert.Equal(2, dsf.WaveFormat.Channels);
                Assert.True(dsf.TotalTime.TotalSeconds > 0.01);

                float[] buf = new float[256];
                int read = dsf.Read(buf, 0, buf.Length);
                Assert.Equal(256, read);
                // Dense alternating 0xAA (+1/-1) after Blackman-Harris lowpass + -6 dB headroom stays near 0, alternating 0xFF pushes positive
                Assert.Contains(buf, s => s > 0.1f);

                dsf.SeekFraction(0.5);
                Assert.InRange(dsf.CurrentTime.TotalSeconds / dsf.TotalTime.TotalSeconds, 0.45, 0.55);
            }

            // 2. Minimal valid stereo DFF (DSDIFF) file
            string dffPath = Path.Combine(tempDir, "test_dsd64.dff");
            WriteSyntheticDff(dffPath, channels: 2, sampleRateHz: 2_822_400, bytesPerChannel: 4096);

            using (var dff = new DsdStreamReader(dffPath))
            {
                Assert.Equal(2_822_400, dff.DsdSampleRateHz);
                Assert.Equal(352_800, dff.WaveFormat.SampleRate);
                float[] buf = new float[128];
                int read = dff.Read(buf, 0, buf.Length);
                Assert.Equal(128, read);
                Assert.Contains(buf, s => s < -0.1f);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SandboxedVst3HostBridge_MixesWetAndTripsFailOpenBypassOnPluginFault()
    {
        using var bridge = new SandboxedVst3HostBridge();

        // Active wet mix (50% dry + 50% inverted plugin = 0.0)
        bridge.ConfigureSlots(
            [new Vst3SlotConfig("C:/vst3/TestGain.vst3", DryWetMix: 0.5, IsBypassed: false)],
            externalWorkerCallback: (sample, _, _) => -sample
        );
        Assert.Equal(1, bridge.ActiveSlotCount);
        Assert.True(bridge.IsWorkerConnected);
        Assert.Equal(0.0, bridge.ProcessSample(0.8, 0), 8);

        // Plugin exception triggers instant Fail-Open bypass without throwing
        bridge.ConfigureSlots(
            [new Vst3SlotConfig("C:/vst3/Crashy.vst3", DryWetMix: 1.0, IsBypassed: false)],
            externalWorkerCallback: (_, _, _) => throw new InvalidOperationException("Simulated VST3 segfault")
        );
        Assert.Equal(0.75, bridge.ProcessSample(0.75, 0), 8);
        Assert.True(bridge.IsFaultedBypass);
    }

    [Fact]
    public void PartitionedFirConvolver_LoadsCompactJsonAndHdf5TwirSofaFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_rb_sofa_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string jsonSofa = Path.Combine(tempDir, "brir.sofa");
            File.WriteAllText(jsonSofa, "{\"left_ir\":[0.0,1.0,0.0],\"right_ir\":[1.0,0.0,0.0]}");

            var conv = new PartitionedFirConvolver(2);
            conv.LoadImpulseFromSofaOrWav(jsonSofa, normalizeEnergy: false);
            Assert.Equal(3, conv.LoadedTapCount);
            Assert.Equal(0.0, conv.ProcessSample(0.9, 0), 8);
            Assert.Equal(0.9, conv.ProcessSample(0.0, 0), 8);
            Assert.Equal(0.9, conv.ProcessSample(0.9, 1), 8);

            // HDF5 header + embedded TWIR float32 stereo block
            string hdfSofa = Path.Combine(tempDir, "ash_brir.sofa");
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(new byte[] { 0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A });
                bw.Write("TWIR"u8);
                bw.Write(2); // taps
                bw.Write(2); // channels
                bw.Write(1.0f); bw.Write(0.5f); // tap 0 L, R
                bw.Write(0.0f); bw.Write(0.5f); // tap 1 L, R
                File.WriteAllBytes(hdfSofa, ms.ToArray());
            }

            conv.LoadImpulseFromSofaOrWav(hdfSofa, normalizeEnergy: false);
            Assert.Equal(2, conv.LoadedTapCount);
            Assert.Equal(0.8, conv.ProcessSample(0.8, 0), 6);
            Assert.Equal(0.4, conv.ProcessSample(0.8, 1), 6);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteSyntheticDsf(string path, int channels, int sampleRateHz, int blockBytesPerCh)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        ulong payloadBytes = (ulong)(channels * blockBytesPerCh);
        ulong totalSize = 28UL + 52UL + 12UL + payloadBytes;

        bw.Write(Encoding.ASCII.GetBytes("DSD "));
        bw.Write(28UL);
        bw.Write(totalSize);
        bw.Write(0UL); // no metadata

        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(52UL);
        bw.Write(1U); // format version
        bw.Write(0U); // raw DSD
        bw.Write(2U); // stereo
        bw.Write((uint)channels);
        bw.Write((uint)sampleRateHz);
        bw.Write(1U); // 1-bit LSB
        bw.Write((ulong)(blockBytesPerCh * 8)); // samples per ch
        bw.Write((uint)blockBytesPerCh);
        bw.Write(0U); // reserved

        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(12UL + payloadBytes);
        byte[] allOnes = Enumerable.Repeat((byte)0xFF, blockBytesPerCh).ToArray();
        for (int ch = 0; ch < channels; ch++) bw.Write(allOnes);
    }

    private static void WriteSyntheticDff(string path, int channels, int sampleRateHz, int bytesPerChannel)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        byte[] fsChunk = BuildBeChunk("FS  ", BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(sampleRateHz)));
        byte[] chnlData = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(chnlData, (ushort)channels);
        byte[] chnlChunk = BuildBeChunk("CHNL", chnlData);

        byte[] propPayload;
        using (var propMs = new MemoryStream())
        {
            propMs.Write("SND "u8);
            propMs.Write(fsChunk);
            propMs.Write(chnlChunk);
            propPayload = propMs.ToArray();
        }
        byte[] propChunk = BuildBeChunk("PROP", propPayload);

        byte[] dsdPayload = Enumerable.Repeat((byte)0x00, channels * bytesPerChannel).ToArray(); // all -1.0
        byte[] dsdChunk = BuildBeChunk("DSD ", dsdPayload);

        bw.Write("FRM8"u8);
        byte[] formLen = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(formLen, (ulong)(4 + propChunk.Length + dsdChunk.Length));
        bw.Write(formLen);
        bw.Write("DSD "u8);
        bw.Write(propChunk);
        bw.Write(dsdChunk);
    }

    private static byte[] BuildBeChunk(string id, byte[] payload)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(id));
        byte[] len = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(len, (ulong)payload.Length);
        ms.Write(len);
        ms.Write(payload);
        if ((payload.Length & 1) == 1) ms.WriteByte(0);
        return ms.ToArray();
    }
}
