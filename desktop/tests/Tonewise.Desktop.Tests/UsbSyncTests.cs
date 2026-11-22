using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
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
    }
}
