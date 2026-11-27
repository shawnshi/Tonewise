using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Audio;
using Tonewise.Desktop.Services;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
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

    [Fact]
    public async Task SandboxedVst3HostBridge_And_PartitionedFirConvolver_ConcurrentSafetyAndWatchdog()
    {
        using var bridge = new Tonewise.Audio.SandboxedVst3HostBridge();
        bridge.ConfigureSlots(
            [new Tonewise.Audio.Vst3SlotConfig("slow.vst3", DryWetMix: 1.0, IsBypassed: false)],
            externalWorkerCallback: (s, _, _) =>
            {
                Thread.Sleep(25);
                return s * 0.5;
            });

        double dryFallback = bridge.ProcessSample(0.4, 0);
        Assert.Equal(0.4, dryFallback, 6);
        Assert.True(bridge.IsFaultedBypass);

        bridge.ConfigureSlots(
            [new Tonewise.Audio.Vst3SlotConfig("fast.vst3", DryWetMix: 0.5, IsBypassed: false)],
            externalWorkerCallback: (s, _, _) => s * 0.25);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Task readerTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _ = bridge.ProcessSample(0.5, 0);
                _ = bridge.ProcessSample(-0.5, 1);
            }
        });
        Task mutatorTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                bridge.Clear();
                bridge.ConfigureSlots(
                    [new Tonewise.Audio.Vst3SlotConfig("fast.vst3", DryWetMix: 0.5, IsBypassed: false)],
                    externalWorkerCallback: (s, _, _) => s * 0.25);
            }
        });
        await Task.WhenAll(readerTask, mutatorTask);

        var convolver = new Tonewise.Audio.PartitionedFirConvolver(2);
        convolver.LoadImpulse([1.0, 0.2, 0.1], [1.0, 0.2, 0.1], normalizeEnergy: false);
        using var convCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Task convProc = Task.Run(() =>
        {
            while (!convCts.IsCancellationRequested)
            {
                _ = convolver.ProcessSample(0.25, 0);
                _ = convolver.ProcessSample(-0.25, 1);
            }
        });
        Task convCtrl = Task.Run(() =>
        {
            while (!convCts.IsCancellationRequested)
            {
                convolver.Reset();
                convolver.Clear();
                convolver.LoadImpulse([1.0, 0.1], [1.0, 0.1], normalizeEnergy: false);
            }
        });
        await Task.WhenAll(convProc, convCtrl);
    }

    [Fact]
    public void P0_1_LibraryStore_MoveOrRename_PreservesTrackUidAndLedger()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p0_move_" + Guid.NewGuid().ToString("N"));
        try
        {
            string oldDir = Path.Combine(tempRoot, "FolderA");
            string newDir = Path.Combine(tempRoot, "FolderB");
            Directory.CreateDirectory(oldDir);
            string oldPath = Path.Combine(oldDir, "01_Track.flac");
            File.WriteAllBytes(oldPath, BuildValidFlacBytes());

            string dbPath = Path.Combine(tempRoot, ".tonewise", "manifest.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var store = new LibraryStore();
            // Run CLI or FFI scan; then seed curated ledger columns and exercise BuildManifestWithFfiFallback on move.
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));

            string initialUid;
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using (var q = conn.CreateCommand())
                {
                    q.CommandText = "SELECT track_uid FROM tracks WHERE rel_path = 'FolderA/01_Track.flac';";
                    initialUid = (string)(q.ExecuteScalar() ?? throw new InvalidOperationException("track missing"));
                }
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE tracks SET play_count_total = 42, rating = 5, dj_scene_json = '[\"focus\"]', dj_scene_ts = 123456 WHERE track_uid = $uid;";
                upd.Parameters.AddWithValue("$uid", initialUid);
                upd.ExecuteNonQuery();
            }

            Directory.CreateDirectory(newDir);
            string newPath = Path.Combine(newDir, "Renamed.flac");
            File.Move(oldPath, newPath);
            Directory.Delete(oldDir);

            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));

            using (var conn = TonewiseSqlite.OpenReadOnly(dbPath))
            {
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT track_uid, rel_path, play_count_total, rating, dj_scene_json FROM tracks;";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal(initialUid, r.GetString(0));
                Assert.Equal("FolderB/Renamed.flac", r.GetString(1));
                Assert.Equal(42, r.GetInt32(2));
                Assert.Equal(5, r.GetInt32(3));
                Assert.Equal("[\"focus\"]", r.GetString(4));
                Assert.False(r.Read());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void P0_2_ProbeTrack_And_BuildManifestWithFfiFallback_ExtractFlacTagsAndMp3Props()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p0_probe_" + Guid.NewGuid().ToString("N"));
        try
        {
            string albumDir = Path.Combine(tempRoot, "Beethoven", "Sym5");
            Directory.CreateDirectory(albumDir);
            string flacPath = Path.Combine(albumDir, "1-01 Symphony.flac");
            string mp3Path = Path.Combine(albumDir, "1-02 Encore.mp3");
            File.WriteAllBytes(flacPath, BuildValidFlacBytes());
            File.WriteAllBytes(mp3Path, BuildValidMp3Bytes());

            TonewiseCoreClient.WriteAudioTags(flacPath, new Dictionary<string, string?>
            {
                ["title"] = "Symphony No. 5: I. Allegro",
                ["album"] = "Beethoven Complete",
                ["album_artist"] = "Berliner Philharmoniker",
                ["artists_json"] = "[\"Herbert von Karajan\", \"Berliner Philharmoniker\"]",
                ["composers_json"] = "[\"Bach\"]",
                ["conductors_json"] = "[\"Herbert von Karajan\"]",
                ["genre"] = "クラシック; オペラ",
                ["publisher"] = "Deutsche Grammophon",
                ["dj_scene_json"] = "[\"focus\"]",
                ["dj_vocals"] = "instrumental",
                ["tempo_bpm"] = "118.0",
                ["year"] = "1977",
            });
            TonewiseCoreClient.WriteAudioTags(mp3Path, new Dictionary<string, string?>
            {
                ["title"] = "Encore Piece",
                ["album"] = "Beethoven Complete",
                ["artist"] = "Martha Argerich",
                ["genre"] = "Classical",
            });

            var flacProbe = TonewiseCoreClient.ProbeTrack(flacPath);
            Assert.Equal("flac", flacProbe.Codec);
            Assert.Equal("Deutsche Grammophon", flacProbe.Publisher);
            Assert.Equal("Classical; Opera", flacProbe.Genre);
            Assert.Equal(["Herbert von Karajan", "Berlin Philharmonic"], flacProbe.Artists);
            Assert.Equal(["Johann Sebastian Bach"], flacProbe.Composers);
            Assert.Equal("Symphony No. 5", flacProbe.WorkName);
            Assert.Equal("Allegro", flacProbe.MovementName);
            Assert.Equal(1u, flacProbe.MovementNumber);
            Assert.True(flacProbe.DurationMs > 0);
            Assert.Equal(44100u, flacProbe.SampleRateHz);

            var mp3Probe = TonewiseCoreClient.ProbeTrack(mp3Path);
            Assert.Equal("mp3", mp3Probe.Codec);
            Assert.Equal(44100u, mp3Probe.SampleRateHz);
            Assert.True(mp3Probe.DurationMs > 0);
            Assert.Equal("Encore Piece", mp3Probe.Title);

            // Seed empty tracks table then invoke forceRebuild: true -> BuildManifestWithFfiFallback
            string dbPath = Path.Combine(tempRoot, ".tonewise", "manifest.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var store = new LibraryStore();
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: false, fastMode: true));
            // Force FFI fallback path explicitly
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));

            using var conn = TonewiseSqlite.OpenReadOnly(dbPath);
            using var q = conn.CreateCommand();
            q.CommandText = "SELECT codec, duration_ms, sample_rate_hz, publisher, genre, dj_scene_json, dj_vocals FROM tracks WHERE rel_path = 'Beethoven/Sym5/1-01 Symphony.flac';";
            using (var r = q.ExecuteReader())
            {
                Assert.True(r.Read());
                Assert.Equal("flac", r.GetString(0));
                Assert.True(r.GetInt64(1) > 0);
                Assert.Equal(44100, r.GetInt32(2));
                Assert.Equal("Deutsche Grammophon", r.GetString(3));
                Assert.Equal("Classical; Opera", r.GetString(4));
                Assert.Equal("[\"focus\"]", r.GetString(5));
                Assert.Equal("instrumental", r.GetString(6));
            }

            using var qMp3 = conn.CreateCommand();
            qMp3.CommandText = "SELECT codec, duration_ms, sample_rate_hz, title FROM tracks WHERE rel_path = 'Beethoven/Sym5/1-02 Encore.mp3';";
            using (var r2 = qMp3.ExecuteReader())
            {
                Assert.True(r2.Read());
                Assert.Equal("mp3", r2.GetString(0));
                Assert.True(r2.GetInt64(1) > 0);
                Assert.Equal(44100, r2.GetInt32(2));
                Assert.Equal("Encore Piece", r2.GetString(3));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static byte[] BuildValidFlacBytes()
    {
        using var ms = new MemoryStream();
        ms.Write("fLaC"u8);
        ms.WriteByte(0x00); // STREAMINFO, not last
        ms.Write([0, 0, 34]);
        byte[] streaminfo = new byte[34];
        streaminfo[10] = 0x0A;
        streaminfo[11] = 0xC4;
        streaminfo[12] = 0x42;
        streaminfo[13] = 0xF0;
        streaminfo[15] = 0x02;
        streaminfo[16] = 0x04;
        streaminfo[17] = 0xCC; // 132300 samples = 3000ms @ 44.1kHz
        ms.Write(streaminfo);
        ms.WriteByte(0x81); // PADDING, last block
        ms.Write([0, 0, 128]);
        ms.Write(new byte[128]);
        byte[] audio = new byte[4096];
        Array.Fill(audio, (byte)0xAA);
        ms.Write(audio);
        return ms.ToArray();
    }

    private static byte[] BuildValidMp3Bytes()
    {
        using var ms = new MemoryStream();
        ms.Write("ID3\x03\x00\x00\x00\x00\x00\x00"u8);
        byte[] frame = new byte[417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        for (int i = 0; i < 8; i++)
        {
            ms.Write(frame);
        }
        return ms.ToArray();
    }

    [Fact]
    public void P1_LibraryStore_SelfHealSentinel_HealsUppercaseCodecZeroDurationAndMissingTrackNo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p1_heal_" + Guid.NewGuid().ToString("N"));
        try
        {
            string albumDir = Path.Combine(tempRoot, "Beethoven", "Sym5");
            Directory.CreateDirectory(albumDir);
            string flacPath = Path.Combine(albumDir, "1-03 Movement.flac");
            File.WriteAllBytes(flacPath, BuildValidFlacBytes());

            string dbPath = Path.Combine(tempRoot, ".tonewise", "manifest.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var store = new LibraryStore();
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));

            // Simulate historical broken row with matching (file_size, mtime_ms)
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using var corrupt = conn.CreateCommand();
                corrupt.CommandText = """
                    UPDATE tracks
                    SET codec = 'Flac', duration_ms = 0, bit_depth = NULL, track_number = 0, disc_number = 0
                    WHERE rel_path = 'Beethoven/Sym5/1-03 Movement.flac';
                    """;
                corrupt.ExecuteNonQuery();
            }

            Assert.True(store.HasPendingDiskChanges(tempRoot));
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));
            Assert.False(store.HasPendingDiskChanges(tempRoot));

            using (var conn = TonewiseSqlite.OpenReadOnly(dbPath))
            {
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT codec, duration_ms, bit_depth, track_number, disc_number FROM tracks WHERE rel_path = 'Beethoven/Sym5/1-03 Movement.flac';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("flac", r.GetString(0));
                Assert.Equal(3000L, r.GetInt64(1));
                Assert.Equal(16, r.GetInt32(2));
                Assert.Equal(3, r.GetInt32(3));
                Assert.Equal(1, r.GetInt32(4));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task P1_BasicMetadataAndCoverHardening_PreventsFalsePositivesAndRateWaste()
    {
        var hitUrls = new List<string>();
        var handler = new StubLambdaHttpHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            hitUrls.Add(url);
            if (url.Contains("m.douban.com/rexxar/api/v2/search/subjects", StringComparison.OrdinalIgnoreCase))
            {
                string json = """
                    {
                      "subjects": {
                        "items": [
                          { "target_type": "music", "target_id": "999999", "target": { "title": "Totally Unrelated Album", "card_subtitle": "Someone Else" } }
                        ]
                      }
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            }
            if (url.Contains("itunes.apple.com", StringComparison.OrdinalIgnoreCase))
            {
                // Exact same duration (210000ms) but completely different trackName -> must be rejected!
                string json = """
                    {
                      "results": [
                        { "trackName": "Wrong Song Name", "artistName": "Other", "collectionName": "Wrong Album", "trackTimeMillis": 210000 }
                      ]
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new BasicMetadataLookupService(http);

        // 1. Complete query with cover present -> zero MusicBrainz / Douban / iTunes requests
        var completeQ = new BasicMetadataQuery(
            "u1", "", "Martha Argerich/Live/24 Preludes.flac", "24 Preludes", "Martha Argerich", "Live",
            "", "", "", "", "Classical", 2020, "DG", 210_000, "", "cover.jpg");
        var (updComplete, _) = await svc.ResolveMissingMetadataAsync(completeQ);
        Assert.Empty(hitUrls);
        // Must NOT infer performer folder "Martha Argerich" as composer
        Assert.False(updComplete.ContainsKey("composers_json"));

        // 2. Unrelated Douban + duration-only iTunes hit must both be rejected
        var missingQ = new BasicMetadataQuery(
            "u2", "", "Unknown/Unknown/24 Preludes.flac", "24 Preludes", "Pianist X", "",
            "", "", "", "", "", null, "", 210_000, "", "cover.jpg");
        var (updMiss, _) = await svc.ResolveMissingMetadataAsync(missingQ);
        Assert.False(updMiss.ContainsKey("album"));
        Assert.Contains("itunes.apple.com", hitUrls.First());
        Assert.Contains(hitUrls, u => u.Contains("musicbrainz.org", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hitUrls, u => u.Contains("m.douban.com", StringComparison.OrdinalIgnoreCase));

        // 3. CoverArtWaterfall PNG detection + Fill-Missing checks cover.png
        string covDir = Path.Combine(Path.GetTempPath(), "tw_p1_covpng_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(covDir);
        try
        {
            byte[] validPng = new byte[24];
            validPng[0] = 0x89; validPng[1] = 0x50; validPng[2] = 0x4E; validPng[3] = 0x47;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(validPng.AsSpan(16, 4), 800);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(validPng.AsSpan(20, 4), 800);
            var covSvc = new CoverArtWaterfallService(http);
            var saved = covSvc.SaveFolderCoverIfMissing(covDir, validPng);
            Assert.True(saved.Saved);
            Assert.EndsWith("cover.png", saved.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(covDir, "cover.png")));
            Assert.False(File.Exists(Path.Combine(covDir, "cover.jpg")));

            var second = covSvc.SaveFolderCoverIfMissing(covDir, validPng);
            Assert.False(second.Saved);
            Assert.True(second.SkippedAlreadyExists);
        }
        finally
        {
            if (Directory.Exists(covDir)) Directory.Delete(covDir, recursive: true);
        }
    }

    [Fact]
    public void P1_ScanMissingMetadataAndEnqueue_CapsOfflinePriorsBelowAutoApproveAndNeverFabricatesBpm()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p1_5d_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = Path.Combine(tempRoot, ".tonewise", "manifest.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var store = new LibraryStore();
            // Create minimal manifest.db via empty root scan + seed 1 missing track
            string dummyFlac = Path.Combine(tempRoot, "01.flac");
            File.WriteAllBytes(dummyFlac, BuildValidFlacBytes());
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));
            var tracks = store.LoadFromMusicRoot(tempRoot);
            Assert.Single(tracks);
            var offlineTrack = tracks[0] with { FullPath = Path.Combine(tempRoot, "missing_on_disk.flac"), Genre = "Classical" };
            int added = store.ScanMissingMetadataAndEnqueue([offlineTrack]);
            Assert.True(added > 0);

            var diffStore = store.GetStagingDiffStore()!;
            var proposals = diffStore.GetPendingProposals(50);
            Assert.All(proposals, p => Assert.True(p.Confidence <= 0.55, $"Prior proposal exceeded 0.55: {p.FieldName}={p.Confidence}"));
            Assert.DoesNotContain(proposals, p => p.FieldName == "tempo_bpm");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void P1_Phase2Revision_UidMigrationPreservesPlayCountAndRating()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tw_rev_uid_migr_{Guid.NewGuid():N}");
        try
        {
            string relDir = Path.Combine(tempRoot, "Albums", "Disc1");
            Directory.CreateDirectory(relDir);
            string wavPath = Path.Combine(relDir, "01 Track One.wav");
            WriteMinimalPcmWav(wavPath);

            var store = new LibraryStore();
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: false, fastMode: true));

            string dbPath = Path.Combine(tempRoot, ".tonewise", "manifest.db");
            string realUid;
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using var sel = conn.CreateCommand();
                sel.CommandText = "SELECT track_uid FROM tracks WHERE rel_path = 'Albums/Disc1/01 Track One.wav';";
                realUid = (string)sel.ExecuteScalar()!;

                using var upd = conn.CreateCommand();
                upd.CommandText = """
                    UPDATE tracks
                    SET track_uid = 'legacy_uid_001',
                        audio_frame_blake3 = zeroblob(32),
                        codec = 'WAV',
                        play_count_total = 42,
                        legacy_play_count = 30,
                        lastfm_pulled_count = 10,
                        local_unsynced_expired_count = 2,
                        last_played_ts = 1700000000000,
                        rating = 4.5,
                        rating_ts = 1700000001000
                    WHERE track_uid = $realUid;
                    """;
                upd.Parameters.AddWithValue("$realUid", realUid);
                Assert.Equal(1, upd.ExecuteNonQuery());
            }

            Assert.True(store.HasPendingDiskChanges(tempRoot));
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));

            using (var conn = TonewiseSqlite.OpenReadOnly(dbPath))
            using (var check = conn.CreateCommand())
            {
                check.CommandText = """
                    SELECT track_uid, codec, play_count_total, legacy_play_count,
                           lastfm_pulled_count, local_unsynced_expired_count, last_played_ts,
                           rating, rating_ts
                    FROM tracks;
                    """;
                using var reader = check.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(realUid, reader.GetString(0));
                Assert.Equal("wav", reader.GetString(1));
                Assert.Equal(42, reader.GetInt32(2));
                Assert.Equal(30, reader.GetInt32(3));
                Assert.Equal(10, reader.GetInt32(4));
                Assert.Equal(2, reader.GetInt32(5));
                Assert.Equal(1700000000000L, reader.GetInt64(6));
                Assert.Equal(4.5, reader.GetDouble(7), 3);
                Assert.Equal(1700000001000L, reader.GetInt64(8));
                Assert.False(reader.Read());
            }

            Assert.False(store.HasPendingDiskChanges(tempRoot));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void P1_Phase2Revision_DuplicateFilesRecordKnownSkipAndClearPendingState()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tw_rev_dup_{Guid.NewGuid():N}");
        try
        {
            string albumDir = Path.Combine(tempRoot, "Albums");
            Directory.CreateDirectory(albumDir);
            string origPath = Path.Combine(albumDir, "01 Original.wav");
            WriteMinimalPcmWav(origPath);

            var store = new LibraryStore();
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: false, fastMode: true));

            string dupPath = Path.Combine(albumDir, "01 Original (2).wav");
            File.Copy(origPath, dupPath);

            Assert.True(store.HasPendingDiskChanges(tempRoot));
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));

            string dbPath = Path.Combine(tempRoot, ".tonewise", "manifest.db");
            using (var conn = TonewiseSqlite.OpenReadOnly(dbPath))
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT rel_path FROM tracks;";
                using var reader = check.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("Albums/01 Original.wav", reader.GetString(0));
                Assert.False(reader.Read());
            }

            Assert.True(File.Exists(Path.Combine(tempRoot, ".tonewise", "scan_known_skips.json")));
            Assert.False(store.HasPendingDiskChanges(tempRoot));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void P1_Phase2Revision_LegacyDuplicateRowMergesLedgerAndRetiresGhostRow()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tw_rev_dup_retire_{Guid.NewGuid():N}");
        try
        {
            string albumDir = Path.Combine(tempRoot, "Albums");
            Directory.CreateDirectory(albumDir);
            string origPath = Path.Combine(albumDir, "01 Original.wav");
            string dupPath = Path.Combine(albumDir, "01 Original (2).wav");
            WriteMinimalPcmWav(origPath);

            var store = new LibraryStore();
            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: false, fastMode: true));
            File.Copy(origPath, dupPath);

            string dbPath = Path.Combine(tempRoot, ".tonewise", "manifest.db");
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using (var updCanon = conn.CreateCommand())
                {
                    updCanon.CommandText = "UPDATE tracks SET play_count_total = 5, rating = NULL WHERE rel_path = 'Albums/01 Original.wav';";
                    Assert.Equal(1, updCanon.ExecuteNonQuery());
                }

                using var insGhost = conn.CreateCommand();
                insGhost.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, artists_json, composers_json, conductors_json,
                        play_count_total, rating, rating_ts
                    ) VALUES (
                        'legacy_dup_001', zeroblob(32), 44, zeroblob(32), 0,
                        'Albums/01 Original (2).wav', 100, 1000, 'WAV', 44100, 0,
                        '01 Original (2)', '[]', '[]', '[]',
                        99, 5.0, 1700000009000
                    );
                    """;
                Assert.Equal(1, insGhost.ExecuteNonQuery());
            }

            Assert.True(store.ScanAndBuildManifestIfMissing(tempRoot, forceRebuild: true, fastMode: true));

            using (var conn = TonewiseSqlite.OpenReadOnly(dbPath))
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT rel_path, codec, play_count_total, rating, rating_ts FROM tracks;";
                using var reader = check.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("Albums/01 Original.wav", reader.GetString(0));
                Assert.Equal("wav", reader.GetString(1));
                Assert.Equal(99, reader.GetInt32(2));
                Assert.Equal(5.0, reader.GetDouble(3), 3);
                Assert.Equal(1700000009000L, reader.GetInt64(4));
                Assert.False(reader.Read());
            }

            Assert.True(File.Exists(Path.Combine(tempRoot, ".tonewise", "scan_known_skips.json")));
            Assert.False(store.HasPendingDiskChanges(tempRoot));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void P1_WriteAudioTags_StripsLeadingId3v2OnFlac_AndPreservesAudioHash()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"tw_id3_flac_net_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string flacPath = Path.Combine(tempDir, "id3_prefixed.flac");
            WriteMinimalId3v2PrefixedFlac(flacPath);

            var before = TonewiseCoreClient.ProbeTrack(flacPath);
            Assert.Equal("flac", before.Codec);
            string expectedUid = before.AudioFrameBlake3Hex;

            var updated = TonewiseCoreClient.WriteAudioTags(
                flacPath,
                new Dictionary<string, string?>
                {
                    ["composer"] = "Sergei Prokofiev",
                    ["work_name"] = "The Love for Three Oranges, Op. 33",
                });

            Assert.Equal(expectedUid, updated.AudioFrameBlake3Hex);
            byte[] header = new byte[4];
            using (var fs = File.OpenRead(flacPath))
            {
                Assert.Equal(4, fs.Read(header, 0, 4));
            }
            Assert.Equal("fLaC"u8.ToArray(), header);

            var after = TonewiseCoreClient.ProbeTrack(flacPath);
            Assert.Equal(expectedUid, after.AudioFrameBlake3Hex);
            Assert.Contains("Sergei Prokofiev", after.Composers);
            Assert.Equal("The Love for Three Oranges, Op. 33", after.WorkName);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteMinimalId3v2PrefixedFlac(string path)
    {
        // ID3v2.3 header (10 bytes + TIT2 frame = 10 + 18 = 28 bytes) followed by minimal valid FLAC stream
        byte[] titlePayload = [0x03, .. System.Text.Encoding.UTF8.GetBytes("ID3 Legacy Title")];
        byte[] frameHdr =
        [
            (byte)'T', (byte)'I', (byte)'T', (byte)'2',
            0, 0, 0, (byte)titlePayload.Length,
            0, 0,
        ];
        int tagSize = frameHdr.Length + titlePayload.Length;
        byte[] id3Hdr =
        [
            (byte)'I', (byte)'D', (byte)'3', 0x03, 0x00, 0x00,
            (byte)((tagSize >> 21) & 0x7F),
            (byte)((tagSize >> 14) & 0x7F),
            (byte)((tagSize >> 7) & 0x7F),
            (byte)(tagSize & 0x7F),
        ];

        // Minimal FLAC: "fLaC" + STREAMINFO (34 bytes) + last PADDING block (128 bytes) + 4096 audio bytes
        byte[] streamInfo = new byte[34];
        streamInfo[10] = 0x0A;
        streamInfo[11] = 0xC4;
        streamInfo[12] = 0x42;
        streamInfo[13] = 0xF0;
        streamInfo[14] = 0x00;
        streamInfo[15] = 0x02;
        streamInfo[16] = 0x04;
        streamInfo[17] = 0xCC;

        byte[] audioFrames = new byte[4096];
        Array.Fill(audioFrames, (byte)0xAB);

        using var fs = File.Create(path);
        fs.Write(id3Hdr);
        fs.Write(frameHdr);
        fs.Write(titlePayload);
        fs.Write("fLaC"u8.ToArray());
        fs.Write([0x00, 0x00, 0x00, 0x22]);
        fs.Write(streamInfo);
        fs.Write([0x81, 0x00, 0x00, 0x80]);
        fs.Write(new byte[128]);
        fs.Write(audioFrames);
    }

    private static void WriteMinimalPcmWav(string path)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleCount = 4410;
        int dataSize = sampleCount * channels * (bitsPerSample / 8);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);
        bw.Write("data"u8.ToArray());
        bw.Write(dataSize);
        bw.Write(new byte[dataSize]);
    }

    private sealed class StubLambdaHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
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
