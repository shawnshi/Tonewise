using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Usb;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class UsbSyncTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "protocol", "schemas", "manifest.sql")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root");
    }

    private static string CreateEmptyManifestDb(string tempRoot)
    {
        string dotDir = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotDir);
        string dbPath = Path.Combine(dotDir, "manifest.db");
        string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = schemaSql;
        cmd.ExecuteNonQuery();
        new StagingDiffStore(dbPath).EnsureSchema();
        return dbPath;
    }

    private static void WriteMinimalWav(string path, int seed = 440)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int sampleRate = 44100;
        const int totalSamples = 4410;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataBytes = totalSamples * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        for (int i = 0; i < totalSamples; i++)
        {
            short sample = (short)(0.3 * Math.Sin(2.0 * Math.PI * seed * i / sampleRate) * 32767.0);
            bw.Write(sample);
        }
    }

    private static void InsertManifestTrack(string dbPath, string uid, string relPath, string audioPath)
    {
        var fi = new FileInfo(audioPath);
        byte[] dummyHash = Convert.FromHexString(uid.Length == 64 ? uid : new string('a', 64));
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO tracks (
                track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title, artists_json
            ) VALUES (
                $uid, $af, 44, $hb, 0,
                $rel, $fsz, $mt, 'wav', 44100, 100, 'Seed Track', '["Tester"]'
            );
            """;
        ins.Parameters.AddWithValue("$uid", uid);
        ins.Parameters.AddWithValue("$af", dummyHash);
        ins.Parameters.AddWithValue("$hb", dummyHash);
        ins.Parameters.AddWithValue("$rel", relPath);
        ins.Parameters.AddWithValue("$fsz", fi.Length);
        ins.Parameters.AddWithValue("$mt", new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds());
        ins.ExecuteNonQuery();
    }

    [Fact]
    public void UsbAdbBridge_ParseTrackDevicesPayload_ParsesDistinctStatesAndEmpty()
    {
        Assert.Empty(UsbAdbBridge.ParseTrackDevicesPayload(""));
        Assert.Empty(UsbAdbBridge.ParseTrackDevicesPayload("   \r\n  "));

        const string payload = "R5CT123456\tdevice\r\n192.168.1.9:5555\tunauthorized\nemulator-5554 offline\nR5CT123456\tdevice\n";
        var parsed = UsbAdbBridge.ParseTrackDevicesPayload(payload);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(new AdbDeviceInfo("R5CT123456", "device"), parsed[0]);
        Assert.Equal(new AdbDeviceInfo("192.168.1.9:5555", "unauthorized"), parsed[1]);
        Assert.Equal(new AdbDeviceInfo("emulator-5554", "offline"), parsed[2]);
    }

    [Fact]
    public void UsbAdbBridge_FilterSafeForeignMusicRelPaths_EnforcesTraversalAndAudioAllowlist()
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Artists/Tester/01_Seed.flac",
        };
        string[] raw =
        [
            "",
            "../etc/passwd.flac",
            "Artist/../escape.mp3",
            "/storage/emulated/0/Music/abs.flac",
            "bad\npath/track.mp3",
            ".tonewise/cache.flac",
            "Tonewise/Playlists/fav.flac",
            "Booklet/liner.pdf",
            "Artists/Tester/cover.jpg",
            "Artists/Tester/01_Seed.flac",
            "古典/交响曲/01 第一乐章.flac",
            "Jazz\\Live\\02_Track.mp3",
            "Jazz/Live/02_Track.mp3",
        ];

        var safe = UsbAdbBridge.FilterSafeForeignMusicRelPaths(raw, desired);
        Assert.Equal(2, safe.Count);
        Assert.Contains("古典/交响曲/01 第一乐章.flac", safe);
        Assert.Contains("Jazz/Live/02_Track.mp3", safe);
    }

    [Fact]
    public void SyncOutboxStore_PersistsHostId_PromotesCopy_AndComputesAuthoritativePolicy()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_usb_store_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string uid = new string('c', 64);
            string relPath = "Artists/Tester/01_Seed.wav";
            string wavPath = Path.Combine(tempRoot, "Artists", "Tester", "01_Seed.wav");
            WriteMinimalWav(wavPath);
            InsertManifestTrack(dbPath, uid, relPath, wavPath);

            var store1 = new SyncOutboxAndPairingStore(tempRoot);
            string hostId1 = store1.HostId;
            Assert.False(string.IsNullOrWhiteSpace(hostId1));
            Assert.True(Guid.TryParse(hostId1, out _));

            var store2 = new SyncOutboxAndPairingStore(tempRoot);
            Assert.Equal(hostId1, store2.HostId);

            // Empty outbox -> OutboxAuthoritative must be false
            store2.SetSyncPolicy(new SyncPolicyConfig(MirrorPruneEnabled: true));
            var emptyPayload = store2.ResolveOutbox();
            Assert.NotNull(emptyPayload.SyncPolicy);
            Assert.True(emptyPayload.SyncPolicy!.MirrorPruneEnabled);
            Assert.False(emptyPayload.SyncPolicy.OutboxAuthoritative);

            // Enqueue copy track -> PromoteCopyItemsToSubscribe -> persistent subscribe + authoritative
            store2.EnqueueTracks([uid], "Single Seed", mode: "copy");
            Assert.Equal(1, store2.PromoteCopyItemsToSubscribe());
            Assert.Equal("subscribe", store2.GetOutboxItems().Single().Mode);

            var validPayload = store2.ResolveOutbox();
            Assert.True(validPayload.SyncPolicy!.OutboxAuthoritative);
            Assert.Equal(0, validPayload.SyncPolicy.MissingSourceCount);

            // Delete physical source file -> MissingSourceCount > 0 -> OutboxAuthoritative = false (safety fuse)
            File.Delete(wavPath);
            // Force outbox stamp change so cache doesn't mask deleted file
            store2.SetSyncPolicy(new SyncPolicyConfig(MirrorPruneEnabled: true, PruneScope: "music_folder_all"));
            var missingSourcePayload = store2.ResolveOutbox();
            Assert.False(missingSourcePayload.SyncPolicy!.OutboxAuthoritative);
            Assert.True(missingSourcePayload.SyncPolicy.MissingSourceCount > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void OutboxStore_RemoveAndPruneMissingPlaylists_RestoresOutboxAuthoritative()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_pl_prune_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string uid = new string('e', 64);
            string relPath = "Artists/Tester/01_Seed.wav";
            string wavPath = Path.Combine(tempRoot, "Artists", "Tester", "01_Seed.wav");
            WriteMinimalWav(wavPath);
            InsertManifestTrack(dbPath, uid, relPath, wavPath);

            string playlistsDir = PlaylistStore.GetPlaylistsDirectory(tempRoot);
            Directory.CreateDirectory(playlistsDir);
            File.WriteAllText(
                Path.Combine(playlistsDir, "KeepMix.m3u8"),
                "#EXTM3U\nArtists/Tester/01_Seed.wav\n",
                Encoding.UTF8);

            var store = new SyncOutboxAndPairingStore(tempRoot);
            store.SetSyncPolicy(new SyncPolicyConfig(MirrorPruneEnabled: true, PruneScope: "music_folder_all"));
            store.EnqueuePlaylists(["KeepMix", "DeletedMix", "GhostMix"], mode: "subscribe");

            var broken = store.ResolveOutbox();
            Assert.False(broken.SyncPolicy!.OutboxAuthoritative);
            Assert.Equal(2, broken.SyncPolicy.MissingSourceCount);
            Assert.Equal(2, store.GetOutboxSummary().MissingSourceCount);

            Assert.Equal(1, store.RemovePlaylistOutboxItemsByName("DeletedMix"));
            var stillBroken = store.ResolveOutbox();
            Assert.False(stillBroken.SyncPolicy!.OutboxAuthoritative);
            Assert.Equal(1, stillBroken.SyncPolicy.MissingSourceCount);

            var pruned = store.PruneMissingPlaylistOutboxItems();
            Assert.Equal(["GhostMix"], pruned);

            var restored = store.ResolveOutbox();
            Assert.True(restored.SyncPolicy!.OutboxAuthoritative);
            Assert.Equal(0, restored.SyncPolicy.MissingSourceCount);
            Assert.Equal(0, store.GetOutboxSummary().MissingSourceCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void UsbAdbBridge_FilterAndBatchDeleteScript_HandlesNfcNfdAndCoverOnlyDirs()
    {
        var desiredNfc = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Schubert/M\u00FCllerin/01_Das Wandern.flac",
        };
        var candidates = new[]
        {
            "Schubert/Mu\u0308llerin/01_Das Wandern.flac", // NFD equivalent of desired track -> MUST NOT delete
            "Antonio Vivaldi/Old Copy/01 O'Connor.flac",
        };

        var filtered = UsbAdbBridge.FilterSafeForeignMusicRelPaths(candidates, desiredNfc);
        Assert.Single(filtered);
        Assert.Equal("Antonio Vivaldi/Old Copy/01 O'Connor.flac", filtered[0]);

        string batchDelCmd = UsbAdbBridge.BuildBatchDeleteFilesShellCommand(
            filtered.Select(r => $"/storage/emulated/0/Music/{r}"));
        Assert.Contains("'\\''Connor.flac'", batchDelCmd, StringComparison.Ordinal);
        Assert.Contains("TWDEL", batchDelCmd, StringComparison.Ordinal);

        string batchDirCmd = UsbAdbBridge.BuildBatchPruneEmptyDirsShellCommand(
            ["/storage/emulated/0/Music/Antonio Vivaldi/Old Copy"]);
        Assert.Contains("rmdir --", batchDirCmd, StringComparison.Ordinal);
        Assert.Contains("[Cc][Oo][Vv][Ee][Rr].[Jj][Pp][Gg]", batchDirCmd, StringComparison.Ordinal);

        // Verify SyncPolicyConfig `with` update preserves custom large-prune thresholds
        var customPolicy = new SyncPolicyConfig(
            MirrorPruneEnabled: false,
            PruneScope: "managed_only",
            LargePruneConfirmThresholdCount: 50,
            LargePruneConfirmThresholdRatio: 0.45);
        var updated = customPolicy with { MirrorPruneEnabled = true, PruneScope = "music_folder_all" };
        Assert.Equal(50, updated.LargePruneConfirmThresholdCount);
        Assert.Equal(0.45, updated.LargePruneConfirmThresholdRatio, 3);
    }

    [Fact]
    public async Task SyncHostServer_PingUsbPairAndProgressEndpoints_EnforceSecurityAndControl()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_usb_srv_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string uid = new string('d', 64);
            string wavPath = Path.Combine(tempRoot, "Artists", "Tester", "01_Seed.wav");
            WriteMinimalWav(wavPath);
            InsertManifestTrack(dbPath, uid, "Artists/Tester/01_Seed.wav", wavPath);

            await using var server = new SyncHostServer(tempRoot, "pc-usb-host");
            server.Store.SetSyncPolicy(new SyncPolicyConfig(MirrorPruneEnabled: true));
            server.Store.EnqueueTracks([uid], "Seed", mode: "subscribe");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // 1. /api/v2/ping includes host_id & usb_loopback hint, and omits paired_device_count
            using (var pingDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v2/ping")))
            {
                Assert.Equal(server.Store.HostId, pingDoc.RootElement.GetProperty("host_id").GetString());
                Assert.Equal("usb_loopback", pingDoc.RootElement.GetProperty("transport_hint").GetString());
                Assert.False(pingDoc.RootElement.TryGetProperty("paired_device_count", out _));
            }

            // 2. /api/v2/usb/pair rejects invalid nonce, accepts issued nonce once, rejects replay
            var badPair = await http.PostAsJsonAsync("/api/v2/usb/pair", new
            {
                nonce = "bad_nonce",
                device_id = "usb-phone-1",
                device_name = "Pixel USB"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, badPair.StatusCode);

            string nonce = server.IssueUsbSyncNonce("SERIAL_123", allowAutoPair: true);
            var okPair = await http.PostAsJsonAsync("/api/v2/usb/pair", new
            {
                nonce,
                device_id = "usb-phone-1",
                device_name = "Pixel USB"
            });
            okPair.EnsureSuccessStatusCode();
            string token;
            using (var pairDoc = JsonDocument.Parse(await okPair.Content.ReadAsStringAsync()))
            {
                token = pairDoc.RootElement.GetProperty("token").GetString()!;
                Assert.Equal(server.Store.HostId, pairDoc.RootElement.GetProperty("host_id").GetString());
            }

            var replayPair = await http.PostAsJsonAsync("/api/v2/usb/pair", new
            {
                nonce,
                device_id = "usb-phone-1",
                device_name = "Pixel USB"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, replayPair.StatusCode);

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // 3. Large prune confirmation propagates to /api/v2/outbox & /api/v2/sync-session/progress
            server.ConfirmLargePruneForDevice("usb-phone-1");
            using (var obDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v2/outbox")))
            {
                var policy = obDoc.RootElement.GetProperty("sync_policy");
                Assert.True(policy.GetProperty("mirror_prune_enabled").GetBoolean());
                Assert.True(policy.GetProperty("outbox_authoritative").GetBoolean());
                Assert.True(policy.GetProperty("prune_confirmed_for_session").GetBoolean());
            }

            server.RequestCancelActiveSync();
            var progResp1 = await http.PostAsJsonAsync("/api/v2/sync-session/progress", new
            {
                transport = "usb",
                phase = "downloading",
                is_running = true,
                completed = false
            });
            progResp1.EnsureSuccessStatusCode();
            using (var progDoc1 = JsonDocument.Parse(await progResp1.Content.ReadAsStringAsync()))
            {
                Assert.True(progDoc1.RootElement.GetProperty("cancel_requested").GetBoolean());
                Assert.True(progDoc1.RootElement.GetProperty("prune_confirmed").GetBoolean());
            }

            // Completion clears cancel flag & one-shot prune confirmation
            var progResp2 = await http.PostAsJsonAsync("/api/v2/sync-session/progress", new
            {
                transport = "usb",
                phase = "done",
                is_running = false,
                completed = true
            });
            progResp2.EnsureSuccessStatusCode();
            using (var obAfterDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v2/outbox")))
            {
                Assert.False(obAfterDoc.RootElement.GetProperty("sync_policy").GetProperty("prune_confirmed_for_session").GetBoolean());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UsbAutoSyncCoordinator_TriggersOncePerPlug_RearmsOnUnplug_AndExecutesAdbFallbackDelete()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_usb_coord_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string uid = new string('e', 64);
            string wavPath = Path.Combine(tempRoot, "Artists", "Tester", "01_Seed.wav");
            WriteMinimalWav(wavPath);
            InsertManifestTrack(dbPath, uid, "Artists/Tester/01_Seed.wav", wavPath);

            await using var server = new SyncHostServer(tempRoot, "pc-coord");
            server.Store.EnqueueTracks([uid], "Seed", mode: "copy");
            await server.StartAsync(port: 0, enableMdns: false);

            var fakeAdb = new FakeUsbAdbRunner();
            await using var coordinator = new UsbAutoSyncCoordinator(
                fakeAdb,
                _ => Task.FromResult<SyncHostServer?>(server),
                () => server,
                autoTriggerDebounce: TimeSpan.FromMilliseconds(30));
            coordinator.MirrorPruneEnabled = true;
            coordinator.PruneScope = "music_folder_all";
            coordinator.AllowAdbForeignDeleteFallback = true;

            coordinator.StartMonitoring();
            await fakeAdb.WaitForTrackerReadyAsync();

            // 1. Plug in -> auto-triggers once (and promotes copy item to subscribe)
            fakeAdb.EmitDevices(new AdbDeviceInfo("PIXEL_USB_1", "device"));
            await fakeAdb.WaitForTriggerCountAsync(1);
            Assert.Single(fakeAdb.TriggerCalls);
            Assert.Equal("subscribe", server.Store.GetOutboxItems().Single().Mode);

            // Emitting same attached state again must NOT duplicate trigger
            fakeAdb.EmitDevices(new AdbDeviceInfo("PIXEL_USB_1", "device"));
            await Task.Delay(120);
            Assert.Single(fakeAdb.TriggerCalls);

            // 2. Unplug and replug -> re-arms and auto-triggers a second time
            fakeAdb.EmitDevices();
            await Task.Delay(60);
            fakeAdb.EmitDevices(new AdbDeviceInfo("PIXEL_USB_1", "device"));
            await fakeAdb.WaitForTriggerCountAsync(2);
            Assert.Equal(2, fakeAdb.TriggerCalls.Count);

            // 3. Phone reports blocked foreign MediaStore paths -> ADB deletes ONLY safe non-desired audio files + rescans
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.ListeningPort}") };
            string nonce = server.IssueUsbSyncNonce("PIXEL_USB_1", allowAutoPair: true);
            var pairResp = await http.PostAsJsonAsync("/api/v2/usb/pair", new
            {
                nonce,
                device_id = "pixel-usb-1",
                device_name = "Pixel"
            });
            pairResp.EnsureSuccessStatusCode();
            using var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync());
            string token = pairDoc.RootElement.GetProperty("token").GetString()!;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var finishResp = await http.PostAsJsonAsync("/api/v2/sync-session/progress", new
            {
                transport = "usb",
                phase = "done",
                is_running = false,
                completed = true,
                blocked_foreign_paths = new[]
                {
                    "OldAlbum/Legacy.flac",
                    "../outside.flac",
                    "Notes/manual.pdf",
                    "Artists/Tester/01_Seed.wav"
                }
            });
            finishResp.EnsureSuccessStatusCode();

            await fakeAdb.WaitForForeignDeleteAsync();
            Assert.Equal(["OldAlbum/Legacy.flac"], fakeAdb.DeletedForeignPaths);
            Assert.Equal(1, fakeAdb.RescanCalls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class FakeUsbAdbRunner : IUsbAdbRunner
    {
        private readonly TaskCompletionSource _trackerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<IReadOnlyList<AdbDeviceInfo>>? _onDevicesChanged;
        public List<string> TriggerCalls { get; } = [];
        public List<string> DeletedForeignPaths { get; } = [];
        public int RescanCalls { get; private set; }
        private readonly SemaphoreSlim _triggerSig = new(0);
        private readonly SemaphoreSlim _deleteSig = new(0);

        public string? ResolveAdbPath(string? customPath = null) => @"C:\fake\adb.exe";

        public async Task TrackDevicesAsync(Action<IReadOnlyList<AdbDeviceInfo>> onDevicesChanged, Action<string>? onStatusLog, CancellationToken ct)
        {
            _onDevicesChanged = onDevicesChanged;
            onStatusLog?.Invoke("ADB fake tracker ready");
            _trackerReady.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public Task WaitForTrackerReadyAsync() => _trackerReady.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public void EmitDevices(params AdbDeviceInfo[] devices) => _onDevicesChanged?.Invoke(devices);

        public async Task WaitForTriggerCountAsync(int expectedCount)
        {
            for (int i = 0; i < expectedCount; i++)
            {
                if (TriggerCalls.Count >= expectedCount) return;
                await _triggerSig.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        public Task WaitForForeignDeleteAsync() => _deleteSig.WaitAsync(TimeSpan.FromSeconds(3));

        public Task<string?> GetDeviceModelAsync(string serial, CancellationToken ct) => Task.FromResult<string?>("Pixel 9");
        public Task<string?> DetectTonewisePackageAsync(string serial, CancellationToken ct) => Task.FromResult<string?>("com.shawnshi.tonewise");
        public Task<bool> SetupReverseTunnelAsync(string serial, int remotePort, int localPort, CancellationToken ct) => Task.FromResult(true);
        public Task RemoveReverseTunnelAsync(string serial, int remotePort, CancellationToken ct) => Task.CompletedTask;

        public Task<(bool Ok, string Detail)> TriggerPhoneUsbSyncAsync(
            string serial, string packageName, int port, string nonce, string hostId, bool allowAutoPair, CancellationToken ct)
        {
            TriggerCalls.Add($"{serial}:{packageName}:{port}");
            _triggerSig.Release();
            return Task.FromResult((true, "ok"));
        }

        public Task<bool> TriggerPhoneLibraryRescanAsync(string serial, string packageName, string nonce, CancellationToken ct)
        {
            RescanCalls++;
            _deleteSig.Release();
            return Task.FromResult(true);
        }

        public Task<int> DeleteForeignMusicFilesAsync(string serial, IReadOnlyList<string> validatedRelPaths, CancellationToken ct)
        {
            DeletedForeignPaths.AddRange(validatedRelPaths);
            return Task.FromResult(validatedRelPaths.Count);
        }

        public List<(string Serial, IReadOnlyList<UsbDirectPushTrackSpec> Tracks, IReadOnlyList<UsbDirectPushCoverSpec> Covers)> DirectPushCalls { get; } = [];
        public Func<string, IReadOnlyList<UsbDirectPushTrackSpec>, IReadOnlyList<UsbDirectPushCoverSpec>, string?, Func<UsbDirectPushProgress, Task>?, CancellationToken, Task<UsbDirectPushSummary>>? DirectPushOverride { get; set; }

        public Task<UsbDirectPushSummary> PushFilesDirectAsync(
            string serial,
            IReadOnlyList<UsbDirectPushTrackSpec> tracks,
            IReadOnlyList<UsbDirectPushCoverSpec> covers,
            string? remoteMusicRoot,
            Func<UsbDirectPushProgress, Task>? onProgress,
            CancellationToken ct)
        {
            DirectPushCalls.Add((serial, tracks, covers));
            if (DirectPushOverride is not null)
                return DirectPushOverride(serial, tracks, covers, remoteMusicRoot, onProgress, ct);

            var pushed = tracks
                .Select(t => new UsbDirectPushedTrackResult(t.TrackUid, t.RelPath, t.ExpectedSize))
                .ToArray();
            return Task.FromResult(new UsbDirectPushSummary(
                UsedDirectPush: true,
                PushedTracks: pushed,
                FailedTrackUids: [],
                CoversPushed: covers.Count,
                CoversSkipped: 0,
                TotalBytesTransferred: tracks.Sum(t => Math.Max(0L, t.ExpectedSize)),
                ElapsedSeconds: 0.05,
                AverageMbps: 50.0,
                Error: null));
        }
    }

    [Fact]
    public async Task UsbAdbSyncClient_WireProtocol_StreamsFilesBatchesMkdirAndAtomicMoves()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tonewise_adb_wire_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string cjkFile = Path.Combine(tempRoot, "cjk_track.flac");
            string mp3File = Path.Combine(tempRoot, "track2.mp3");
            string largeFile = Path.Combine(tempRoot, "large3.flac");
            string failSendFile = Path.Combine(tempRoot, "fail_send.flac");
            string coverNew = Path.Combine(tempRoot, "cover_new.jpg");
            string coverExisting = Path.Combine(tempRoot, "cover_existing.jpg");

            byte[] cjkBytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31)).ToArray();
            byte[] mp3Bytes = Enumerable.Range(0, 140_000).Select(i => (byte)(i * 17)).ToArray();
            byte[] largeBytes = new byte[2 * 1024 * 1024 + 12345];
            Random.Shared.NextBytes(largeBytes);
            byte[] failBytes = Enumerable.Range(0, 32_768).Select(i => (byte)(i * 7)).ToArray();
            byte[] coverNewBytes = Encoding.UTF8.GetBytes("JPEG_NEW_COVER_BYTES");
            byte[] coverExistingBytes = Encoding.UTF8.GetBytes("JPEG_EXISTING_COVER_BYTES");

            await File.WriteAllBytesAsync(cjkFile, cjkBytes);
            await File.WriteAllBytesAsync(mp3File, mp3Bytes);
            await File.WriteAllBytesAsync(largeFile, largeBytes);
            await File.WriteAllBytesAsync(failSendFile, failBytes);
            await File.WriteAllBytesAsync(coverNew, coverNewBytes);
            await File.WriteAllBytesAsync(coverExisting, coverExistingBytes);

            await using var mockAdb = new FakeAdbWireServer(
                existingStatPaths: ["/storage/emulated/0/Music/ExistingArtist/Album/cover.jpg"],
                failSendSubstrings: ["Artist D"]);
            int port = mockAdb.Start();

            var client = new UsbAdbSyncClient(adbPort: port, ensureAdbServerAsync: null, workerCount: 2);
            var progressUpdates = new ConcurrentBag<UsbDirectPushProgress>();

            string cjkRel = "测试艺术家/专辑 01/01 曲目 & 测试.flac";
            string mp3Rel = "Artist B/Album B/02 Track.mp3";
            string largeRel = "Artist C/Album C/03 Master.flac";
            string failRel = "Artist D/Album D/04 fail_send.flac";

            var tracks = new[]
            {
                new UsbDirectPushTrackSpec(new string('1', 64), cjkRel, cjkFile, cjkBytes.Length, 1_700_000_123_000L),
                new UsbDirectPushTrackSpec(new string('2', 64), mp3Rel, mp3File, mp3Bytes.Length, 1_700_000_456_000L),
                new UsbDirectPushTrackSpec(new string('3', 64), largeRel, largeFile, largeBytes.Length, 1_700_000_789_000L),
                new UsbDirectPushTrackSpec(new string('4', 64), failRel, failSendFile, failBytes.Length, 1_700_000_999_000L),
            };
            var covers = new[]
            {
                new UsbDirectPushCoverSpec("ExistingArtist/Album", coverExisting),
                new UsbDirectPushCoverSpec("测试艺术家/专辑 01", coverNew),
            };

            UsbDirectPushSummary summary = await client.PushAsync(
                "MOCK_SERIAL_1",
                tracks,
                covers,
                "/storage/emulated/0/Music",
                p =>
                {
                    progressUpdates.Add(p);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.True(summary.UsedDirectPush);
            Assert.Equal(3, summary.PushedTracks.Count);
            string failedUid = Assert.Single(summary.FailedTrackUids);
            Assert.Equal(new string('4', 64), failedUid);
            Assert.Equal(1, summary.CoversSkipped);
            Assert.Equal(1, summary.CoversPushed);
            Assert.True(summary.TotalBytesTransferred >= largeBytes.Length);
            Assert.True(summary.AverageMbps > 0.0);
            Assert.NotEmpty(progressUpdates);

            Assert.Equal(SHA256.HashData(cjkBytes), SHA256.HashData(mockAdb.FinalFiles[$"/storage/emulated/0/Music/{cjkRel}"]));
            Assert.Equal(SHA256.HashData(mp3Bytes), SHA256.HashData(mockAdb.FinalFiles[$"/storage/emulated/0/Music/{mp3Rel}"]));
            Assert.Equal(SHA256.HashData(largeBytes), SHA256.HashData(mockAdb.FinalFiles[$"/storage/emulated/0/Music/{largeRel}"]));
            Assert.Equal(coverNewBytes, mockAdb.FinalFiles["/storage/emulated/0/Music/测试艺术家/专辑 01/cover.jpg"]);
            Assert.False(mockAdb.FinalFiles.ContainsKey($"/storage/emulated/0/Music/{failRel}"));
            Assert.Contains(mockAdb.ShellCommands, c => c.StartsWith("mkdir -p", StringComparison.Ordinal));
            Assert.Empty(mockAdb.UncommittedTempFiles);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SyncHostServer_UsbPushTracks_StreamsNdjsonAndCancelsCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tonewise_usb_ndjson_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string uid = new string('f', 64);
            string wavPath = Path.Combine(tempRoot, "Artists", "Tester", "01_Seed.wav");
            WriteMinimalWav(wavPath);
            InsertManifestTrack(dbPath, uid, "Artists/Tester/01_Seed.wav", wavPath);

            await using var server = new SyncHostServer(tempRoot, "pc-ndjson-host");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            string nonce = server.IssueUsbSyncNonce("SERIAL_NDJSON", allowAutoPair: true);
            var pairResp = await http.PostAsJsonAsync("/api/v2/usb/pair", new
            {
                nonce,
                device_id = "usb-ndjson-phone",
                device_name = "Phone"
            });
            pairResp.EnsureSuccessStatusCode();
            using var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync());
            string token = pairDoc.RootElement.GetProperty("token").GetString()!;

            // 1. Without handler -> 501 for HTTP fallback
            using var unauthReq = new HttpRequestMessage(HttpMethod.Post, "/api/v2/usb/push-tracks")
            {
                Content = JsonContent.Create(new { tracks = Array.Empty<object>() })
            };
            var unauthResp = await http.SendAsync(unauthReq);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var unavailableResp = await http.PostAsJsonAsync("/api/v2/usb/push-tracks", new
            {
                tracks = new[] { new { track_uid = uid, rel_path = "Artists/Tester/01_Seed.wav", file_size = 100L, mtime_ms = 100L } }
            });
            Assert.Equal(HttpStatusCode.NotImplemented, unavailableResp.StatusCode);

            // 2. With handler -> streams progress + complete NDJSON lines
            server.UsbDirectPushHandler = async (req, onProgress, ct) =>
            {
                await onProgress(new UsbDirectPushProgress("pushing", 1, req.Tracks.Count, 44, 44, 42.5, req.Tracks[0].RelPath));
                return new UsbDirectPushSummary(
                    UsedDirectPush: true,
                    PushedTracks: [new UsbDirectPushedTrackResult(uid, "Artists/Tester/01_Seed.wav", 44)],
                    FailedTrackUids: [],
                    CoversPushed: 1,
                    CoversSkipped: 0,
                    TotalBytesTransferred: 44,
                    ElapsedSeconds: 0.01,
                    AverageMbps: 42.5,
                    Error: null);
            };

            var okResp = await http.PostAsJsonAsync("/api/v2/usb/push-tracks", new
            {
                remote_music_root = "/storage/emulated/0/Music",
                tracks = new[] { new { track_uid = uid, rel_path = "Artists/Tester/01_Seed.wav", file_size = 44L, mtime_ms = 100L } },
                covers = Array.Empty<object>()
            });
            okResp.EnsureSuccessStatusCode();
            string[] lines = (await okResp.Content.ReadAsStringAsync())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, lines.Length);

            using var ev0 = JsonDocument.Parse(lines[0]);
            Assert.Equal("progress", ev0.RootElement.GetProperty("event").GetString());
            Assert.Equal(42.5, ev0.RootElement.GetProperty("speed_mbps").GetDouble());

            using var ev1 = JsonDocument.Parse(lines[1]);
            Assert.Equal("complete", ev1.RootElement.GetProperty("event").GetString());
            Assert.True(ev1.RootElement.GetProperty("used_direct_push").GetBoolean());
            Assert.Equal(uid, ev1.RootElement.GetProperty("pushed_tracks")[0].GetProperty("track_uid").GetString());

            // 3. RequestCancelActiveSync cancels in-flight push handler CTS
            var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.UsbDirectPushHandler = async (_, _, ct) =>
            {
                startedTcs.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                throw new InvalidOperationException("Should have cancelled");
            };

            Task<HttpResponseMessage> cancelCallTask = http.PostAsJsonAsync("/api/v2/usb/push-tracks", new
            {
                tracks = new[] { new { track_uid = uid, rel_path = "Artists/Tester/01_Seed.wav", file_size = 44L, mtime_ms = 100L } }
            });
            await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            server.RequestCancelActiveSync();

            HttpResponseMessage cancelledResp = await cancelCallTask.WaitAsync(TimeSpan.FromSeconds(5));
            cancelledResp.EnsureSuccessStatusCode();
            string cancelledBody = await cancelledResp.Content.ReadAsStringAsync();
            using var cancelledDoc = JsonDocument.Parse(cancelledBody.Trim());
            Assert.False(cancelledDoc.RootElement.GetProperty("used_direct_push").GetBoolean());
            Assert.Equal("sync_cancelled", cancelledDoc.RootElement.GetProperty("error").GetString());
            server.ClearCancelActiveSync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveTrackPhysicalPath_UsesManifestStampCacheAndInvalidatesOnChange()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tonewise_resolve_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string dbPath = CreateEmptyManifestDb(tempRoot);
            string uid1 = new string('a', 64);
            string rel1 = "Artists/Cache/01.wav";
            string wav1 = Path.Combine(tempRoot, "Artists", "Cache", "01.wav");
            WriteMinimalWav(wav1);
            InsertManifestTrack(dbPath, uid1, rel1, wav1);

            var store = new SyncOutboxAndPairingStore(tempRoot);
            Assert.True(store.TryResolveTrackPhysicalPath(uid1, out string fp1, out string rp1));
            Assert.Equal(rel1, rp1);
            Assert.True(File.Exists(fp1));

            // Insert second track + delete/re-touch manifest.db to bump mtime/length stamp
            string uid2 = new string('b', 64);
            string rel2 = "Artists/Cache/02.wav";
            string wav2 = Path.Combine(tempRoot, "Artists", "Cache", "02.wav");
            WriteMinimalWav(wav2);
            InsertManifestTrack(dbPath, uid2, rel2, wav2);
            File.SetLastWriteTimeUtc(dbPath, DateTime.UtcNow.AddSeconds(2));

            Assert.True(store.TryResolveTrackPhysicalPath(uid2, out string fp2, out string rp2));
            Assert.Equal(rel2, rp2);
            Assert.True(File.Exists(fp2));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class FakeAdbWireServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly HashSet<string> _existingStatPaths;
        private readonly string[] _failSendSubstrings;
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;

        public ConcurrentDictionary<string, byte[]> FinalFiles { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, byte[]> StagedTempFiles { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> ShellCommands { get; } = new();

        public IReadOnlyList<string> UncommittedTempFiles => StagedTempFiles.Keys.ToArray();

        public FakeAdbWireServer(IEnumerable<string> existingStatPaths, IEnumerable<string> failSendSubstrings)
        {
            _existingStatPaths = new HashSet<string>(existingStatPaths, StringComparer.Ordinal);
            _failSendSubstrings = failSendSubstrings.ToArray();
        }

        public int Start()
        {
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
            return ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }
                _ = Task.Run(() => HandleConnectionAsync(client));
            }
        }

        private async Task HandleConnectionAsync(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    string? cmd = await ReadAdbFrameAsync(stream, _cts.Token);
                    if (cmd is null) break;

                    if (cmd.StartsWith("host:transport:", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync("OKAY"u8.ToArray(), _cts.Token);
                        continue;
                    }

                    if (cmd == "sync:")
                    {
                        await stream.WriteAsync("OKAY"u8.ToArray(), _cts.Token);
                        await HandleSyncLoopAsync(stream, _cts.Token);
                        break;
                    }

                    if (cmd.StartsWith("shell:", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync("OKAY"u8.ToArray(), _cts.Token);
                        string shellCmd = cmd["shell:".Length..];
                        ShellCommands.Enqueue(shellCmd);
                        string reply = ExecuteFakeShell(shellCmd);
                        byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                        await stream.WriteAsync(replyBytes, _cts.Token);
                        break;
                    }

                    await stream.WriteAsync("FAIL0007unknown"u8.ToArray(), _cts.Token);
                    break;
                }
            }
        }

        private string ExecuteFakeShell(string shellCmd)
        {
            if (shellCmd.StartsWith("mkdir -p", StringComparison.Ordinal))
                return "__TW_EXIT:0\n";

            if (shellCmd.Contains("mv -f -- ", StringComparison.Ordinal))
            {
                var sb = new StringBuilder();
                foreach (string stmt in shellCmd.Split("; ", StringSplitOptions.RemoveEmptyEntries))
                {
                    int okIdxPos = stmt.IndexOf("__TWOK:", StringComparison.Ordinal);
                    if (okIdxPos < 0) continue;
                    string idxStr = stmt[(okIdxPos + "__TWOK:".Length)..].Trim();
                    string[] quoted = ExtractSingleQuotedArgs(stmt);
                    if (quoted.Length >= 2 && StagedTempFiles.TryRemove(quoted[0], out byte[]? data))
                    {
                        FinalFiles[quoted[1]] = data;
                        sb.Append("__TWOK:").Append(idxStr).Append('\n');
                    }
                }
                return sb.ToString();
            }

            if (shellCmd.StartsWith("rm -f -- ", StringComparison.Ordinal))
            {
                foreach (string q in ExtractSingleQuotedArgs(shellCmd))
                {
                    StagedTempFiles.TryRemove(q, out _);
                }
                return string.Empty;
            }

            return "__TW_EXIT:0\n";
        }

        private async Task HandleSyncLoopAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] header = new byte[8];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await stream.ReadExactlyAsync(header.AsMemory(0, 8), ct);
                }
                catch
                {
                    break;
                }

                string tag = Encoding.ASCII.GetString(header, 0, 4);
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                if (tag == "QUIT") break;

                byte[] argBytes = len > 0 ? new byte[len] : [];
                if (len > 0)
                {
                    await stream.ReadExactlyAsync(argBytes.AsMemory(0, (int)len), ct);
                }
                string arg = Encoding.UTF8.GetString(argBytes);

                if (tag == "STAT")
                {
                    byte[] resp = new byte[16];
                    "STAT"u8.CopyTo(resp);
                    if (_existingStatPaths.Contains(arg))
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(4, 4), 33188);
                        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(8, 4), 128);
                        BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(12, 4), 1700000000);
                    }
                    await stream.WriteAsync(resp, ct);
                    continue;
                }

                if (tag == "SEND")
                {
                    int comma = arg.LastIndexOf(',');
                    string remoteTempPath = comma > 0 ? arg[..comma] : arg;
                    using var ms = new MemoryStream();
                    while (true)
                    {
                        byte[] chunkHdr = new byte[8];
                        await stream.ReadExactlyAsync(chunkHdr.AsMemory(0, 8), ct);
                        string chunkTag = Encoding.ASCII.GetString(chunkHdr, 0, 4);
                        uint chunkVal = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr.AsSpan(4, 4));
                        if (chunkTag == "DATA")
                        {
                            byte[] chunkData = new byte[chunkVal];
                            await stream.ReadExactlyAsync(chunkData.AsMemory(0, (int)chunkVal), ct);
                            ms.Write(chunkData, 0, chunkData.Length);
                        }
                        else if (chunkTag == "DONE")
                        {
                            break;
                        }
                    }

                    bool shouldFail = _failSendSubstrings.Any(s => remoteTempPath.Contains(s, StringComparison.OrdinalIgnoreCase));
                    if (shouldFail)
                    {
                        byte[] msg = "simulated_send_fail"u8.ToArray();
                        byte[] fail = new byte[8 + msg.Length];
                        "FAIL"u8.CopyTo(fail);
                        BinaryPrimitives.WriteUInt32LittleEndian(fail.AsSpan(4, 4), (uint)msg.Length);
                        msg.CopyTo(fail, 8);
                        await stream.WriteAsync(fail, ct);
                    }
                    else
                    {
                        StagedTempFiles[remoteTempPath] = ms.ToArray();
                        byte[] ok = new byte[8];
                        "OKAY"u8.CopyTo(ok);
                        await stream.WriteAsync(ok, ct);
                    }
                }
            }
        }

        private static async Task<string?> ReadAdbFrameAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] lenHdr = new byte[4];
            int totalRead = 0;
            while (totalRead < 4)
            {
                int r = await stream.ReadAsync(lenHdr.AsMemory(totalRead, 4 - totalRead), ct);
                if (r == 0) return null;
                totalRead += r;
            }
            int payloadLen = Convert.ToInt32(Encoding.ASCII.GetString(lenHdr), 16);
            byte[] payload = new byte[payloadLen];
            await stream.ReadExactlyAsync(payload.AsMemory(0, payloadLen), ct);
            return Encoding.UTF8.GetString(payload);
        }

        private static string[] ExtractSingleQuotedArgs(string input)
        {
            var list = new List<string>();
            int i = 0;
            while (i < input.Length)
            {
                int start = input.IndexOf('\'', i);
                if (start < 0) break;
                var sb = new StringBuilder();
                int pos = start + 1;
                while (pos < input.Length)
                {
                    if (pos + 3 < input.Length && input.AsSpan(pos, 4).SequenceEqual("'\"'\"'"))
                    {
                        sb.Append('\'');
                        pos += 4;
                        continue;
                    }
                    if (input[pos] == '\'')
                    {
                        pos++;
                        break;
                    }
                    sb.Append(input[pos]);
                    pos++;
                }
                list.Add(sb.ToString());
                i = pos;
            }
            return list.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            if (_acceptLoop is not null)
            {
                try { await _acceptLoop; } catch { }
            }
            _cts.Dispose();
        }
    }
}
