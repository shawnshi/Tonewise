using System.Net.Http.Json;
using System.Text.Json;
using Tonewise.Audio;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class CoreInteropAndDspTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "protocol", "fixtures", "rules_golden.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Tonewise repo root");
    }

    [Fact]
    public void PInvoke_AbiAndArtistWhitelist_MatchesRustCore()
    {
        Assert.Equal(1u, TonewiseCoreClient.AbiVersion());
        Assert.Equal(["Above & Beyond"], TonewiseCoreClient.SplitArtists("Above & Beyond"));
        Assert.Equal(["AC/DC", "Slash"], TonewiseCoreClient.SplitArtists("AC/DC / Slash"));
    }

    [Fact]
    public void PInvoke_RulesGoldenFixtures_MatchExactly()
    {
        string root = FindRepoRoot();
        string raw = File.ReadAllText(Path.Combine(root, "protocol", "fixtures", "rules_golden.json"));
        using var doc = JsonDocument.Parse(raw);
        string tracksJson = doc.RootElement.GetProperty("tracks").GetRawText();

        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            string sceneJson = c.GetProperty("scene").GetRawText();
            var expected = c.GetProperty("expected_track_uids")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();

            var actual = TonewiseCoreClient.EvaluateScene(sceneJson, tracksJson);
            Assert.True(expected.SequenceEqual(actual), $"C# P/Invoke mismatch on golden case '{name}'");
        }
    }

    [Fact]
    public void PInvoke_M3u8ThreeWayMerge_MatchesGoldenFixtures()
    {
        string root = FindRepoRoot();
        string raw = File.ReadAllText(Path.Combine(root, "protocol", "fixtures", "m3u8_merge_golden.json"));
        using var doc = JsonDocument.Parse(raw);

        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var baseList = c.GetProperty("base").EnumerateArray().Select(e => e.GetString()!).ToList();
            var oursList = c.GetProperty("ours").EnumerateArray().Select(e => e.GetString()!).ToList();
            var theirsList = c.GetProperty("theirs").EnumerateArray().Select(e => e.GetString()!).ToList();
            var expectedMerged = c.GetProperty("expected_merged").EnumerateArray().Select(e => e.GetString()!).ToList();
            int expectedConflicts = c.GetProperty("expected_conflict_count").GetInt32();

            var result = TonewiseCoreClient.MergeM3u8(baseList, oursList, theirsList);
            Assert.True(expectedMerged.SequenceEqual(result.Merged), $"Merge mismatch on '{name}'");
            Assert.Equal(expectedConflicts, result.Conflicts.Count);
        }
    }

    [Fact]
    public void EqualizerApo_64BitBiquad_GuardsClippingAndSupportsBitPerfectBypass()
    {
        const string apo = """
            Preamp: -4.5 dB
            Filter: ON LSC Fc 85 Hz Gain 4.5 dB Q 0.71
            Filter: ON HSC Fc 6000 Hz Gain -2.5 dB Q 0.71
            """;
        var profile = EqualizerApoProfile.Parse(apo);
        Assert.Equal(2, profile.Filters.Count);
        Assert.Equal(-7.5, profile.ComputeEffectivePreampDb(-3.0), 5);

        // Verify DspSampleProvider bypass preserves float samples bit-for-bit
        var source = new StubWaveProvider([0.5f, -0.25f, 0.75f, -0.8f]);
        var dsp = new DspSampleProvider(source);
        dsp.Configure(profile, -3.0);
        dsp.IsBypassed = true;

        float[] outBuf = new float[4];
        int read = dsp.Read(outBuf, 0, 4);
        Assert.Equal(4, read);
        Assert.Equal(0.5f, outBuf[0]);
        Assert.Equal(-0.25f, outBuf[1]);
        Assert.Equal(0.75f, outBuf[2]);
        Assert.Equal(-0.8f, outBuf[3]);

        // Verify DSP active mode attenuates signal when Preamp is negative
        dsp.IsBypassed = false;
        source.Reset();
        float[] dspBuf = new float[4];
        dsp.Read(dspBuf, 0, 4);
        Assert.True(Math.Abs(dspBuf[0]) < 0.5f);
    }

    [Fact]
    public async Task SyncHostServer_EndToEnd_Scenes_M3u8Merge_History_And_Handoff()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_synchost_test_" + Guid.NewGuid().ToString("N"));
        string dotTonewise = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotTonewise);
        File.WriteAllBytes(Path.Combine(dotTonewise, "waveforms.bin"), [0x54, 0x57, 0x57, 0x46, 1, 0, 150, 0, 0, 0, 0, 0]);

        try
        {
            await using var server = new SyncHostServer(tempRoot, "pc-test", allowPhonePlaylistUpload: true);
            int port = await server.StartAsync(port: 0, enableMdns: false);
            Assert.True(port > 0);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var pairResp = await http.PostAsJsonAsync("/api/v2/pair", new
            {
                code = server.Store.CurrentPairingCode,
                device_id = "pc-test-phone",
                device_name = "Test"
            });
            pairResp.EnsureSuccessStatusCode();
            using (var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync()))
            {
                string token = pairDoc.RootElement.GetProperty("token").GetString()!;
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            // 1. Status & Scenes
            using var statusDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v1/status"));
            Assert.True(statusDoc.RootElement.GetProperty("has_waveforms").GetBoolean());

            using var scenesDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v1/scenes"));
            Assert.True(scenesDoc.RootElement.GetProperty("scenes").GetArrayLength() >= 3);

            // 2. Waveforms binary download
            byte[] wf = await http.GetByteArrayAsync("/api/v1/waveforms");
            Assert.Equal(12, wf.Length);

            // 3. M3U8 3-way merge via Rust core
            var mergeResp = await http.PostAsJsonAsync("/api/v1/playlists/merge", new
            {
                playlist_name = "Focus",
                @base = new[] { "a.flac", "b.flac" },
                ours = new[] { "a.flac", "b.flac", "c.flac" },
                theirs = new[] { "a.flac" },
            });
            mergeResp.EnsureSuccessStatusCode();
            var merged = await mergeResp.Content.ReadFromJsonAsync<M3u8MergeResult>();
            Assert.NotNull(merged);
            Assert.Equal(["a.flac", "c.flac"], merged!.Merged);
            Assert.Empty(merged.Conflicts);
            Assert.True(File.Exists(Path.Combine(tempRoot, "Playlists", "Focus.m3u8")));

            // 4. Handoff 5-slot merge
            var slot1 = new SessionSlotDto(1, "Evening Focus", "focus", 5000, ["uid_a", "uid_b"], 1, 42_500);
            var handoffResp = await http.PostAsJsonAsync("/api/v1/handoff", new[] { slot1 });
            handoffResp.EnsureSuccessStatusCode();
            var slots = await handoffResp.Content.ReadFromJsonAsync<List<SessionSlotDto>>();
            Assert.Single(slots!);
            Assert.Equal(42_500UL, slots![0].PositionMs);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class StubWaveProvider(float[] samples) : NAudio.Wave.ISampleProvider
    {
        private int _pos;
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public void Reset() => _pos = 0;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, samples.Length - _pos);
            Array.Copy(samples, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
