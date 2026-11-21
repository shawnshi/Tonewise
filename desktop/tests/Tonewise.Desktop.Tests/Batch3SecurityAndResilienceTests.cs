using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class Batch3SecurityAndResilienceTests
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

    private static string CreateManifestDb(string tempRoot)
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

    private static byte[] BuildMdnsQueryPacket(string qname, ushort qtype = 12, ushort flags = 0x0000)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        void WriteU16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
            bw.Write(b);
        }
        WriteU16(0x1234); // ID
        WriteU16(flags);
        WriteU16(1);      // QDCOUNT = 1
        WriteU16(0);      // ANCOUNT
        WriteU16(0);      // NSCOUNT
        WriteU16(0);      // ARCOUNT
        foreach (string label in qname.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            bw.Write((byte)bytes.Length);
            bw.Write(bytes);
        }
        bw.Write((byte)0);
        WriteU16(qtype);
        WriteU16(1); // IN
        return ms.ToArray();
    }

    [Fact]
    public void PairingLockout_IsScopedPerClientIpKey_NoGlobalLanDoS()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b3_pair_ip_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SyncOutboxAndPairingStore(tempRoot);
            string code = store.CurrentPairingCode;

            // Attacker at 192.168.1.50 triggers 5 bad attempts -> locked out
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(
                    PairAttemptStatus.InvalidCode,
                    store.TryPairWithStatus("000000", "attacker", "Bad", out _, out _, out _, clientKey: "192.168.1.50"));
            }
            Assert.Equal(
                PairAttemptStatus.LockedOut,
                store.TryPairWithStatus("000000", "attacker", "Bad", out _, out _, out int retrySec, clientKey: "192.168.1.50"));
            Assert.True(retrySec > 0);

            // Attacker still locked out even with valid code
            Assert.Equal(
                PairAttemptStatus.LockedOut,
                store.TryPairWithStatus(code, "attacker", "Bad", out _, out _, out _, clientKey: "192.168.1.50"));

            // Legitimate phone at 192.168.1.99 pairs cleanly!
            Assert.Equal(
                PairAttemptStatus.Success,
                store.TryPairWithStatus(code, "pixel9", "Pixel 9", out string? token, out var device, out _, clientKey: "192.168.1.99"));
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.Equal("pixel9", device!.DeviceId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SyncHost_PingHidesPairedCount_AndUnpairRevokesTokenAndDeviceOutbox()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b3_unpair_" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-b3-sec");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // 1. /api/v2/ping must not leak paired_device_count
            using (var pingDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v2/ping")))
            {
                Assert.False(pingDoc.RootElement.TryGetProperty("paired_device_count", out _));
                Assert.True(pingDoc.RootElement.GetProperty("requires_pairing").GetBoolean());
            }

            // 2. Pair and enqueue device-specific + broadcast outbox items
            var pairResp = await http.PostAsJsonAsync("/api/v2/pair", new
            {
                code = server.Store.CurrentPairingCode,
                device_id = "phone-unpair-1",
                device_name = "Phone 1"
            });
            pairResp.EnsureSuccessStatusCode();
            string token;
            using (var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync()))
            {
                token = pairDoc.RootElement.GetProperty("token").GetString()!;
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            server.Store.EnqueuePlaylist("Private Mix", deviceId: "phone-unpair-1");
            server.Store.EnqueuePlaylist("Public Broadcast", deviceId: "*");

            // 3. POST /api/v2/unpair removes device + its specific outbox while keeping "*"
            var unpairResp = await http.PostAsync("/api/v2/unpair", content: null);
            Assert.Equal(HttpStatusCode.OK, unpairResp.StatusCode);

            var outboxAfter = server.Store.GetOutboxItems(deviceId: "other-phone");
            Assert.DoesNotContain(outboxAfter, i => i.TargetKey == "Private Mix");
            Assert.Contains(outboxAfter, i => i.TargetKey == "Public Broadcast" && i.DeviceId == "*");

            // 4. Subsequent request with revoked token returns 401
            var outboxResp = await http.GetAsync("/api/v2/outbox");
            Assert.Equal(HttpStatusCode.Unauthorized, outboxResp.StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MdnsAdvertiser_EmitsProto2AndGoodbye_AndMatchesOnlyRelevantDnsQueries()
    {
        var advertiser = new MdnsAdvertiser("Tonewise-Desk", 45820);
        var overrideIp = IPAddress.Parse("192.168.1.42");

        byte[] announce = advertiser.BuildAnnouncementPacket(ttlSeconds: 120, overrideIp: overrideIp);
        string ascii = Encoding.ASCII.GetString(announce);
        Assert.Contains("proto=2", ascii);
        Assert.DoesNotContain("proto=1", ascii);
        Assert.Equal([192, 168, 1, 42], announce[^4..]);

        byte[] goodbye = advertiser.BuildAnnouncementPacket(ttlSeconds: 0, overrideIp: overrideIp);
        Assert.True(goodbye.Length == announce.Length);
        // Response packet (QR=1) must not be treated as a query
        Assert.False(MdnsAdvertiser.IsMatchingMdnsQuery(announce, "Tonewise-Desk", "myhost.local"));

        // Matching mDNS queries
        Assert.True(MdnsAdvertiser.IsMatchingMdnsQuery(
            BuildMdnsQueryPacket("_tonewise._tcp.local"), "Tonewise-Desk", "myhost.local"));
        Assert.True(MdnsAdvertiser.IsMatchingMdnsQuery(
            BuildMdnsQueryPacket("_services._dns-sd._udp.local"), "Tonewise-Desk", "myhost.local"));
        Assert.True(MdnsAdvertiser.IsMatchingMdnsQuery(
            BuildMdnsQueryPacket("Tonewise-Desk._tonewise._tcp.local"), "Tonewise-Desk", "myhost.local"));

        // Non-matching service query, response QR=1 query, and malformed short buffers must return false safely
        Assert.False(MdnsAdvertiser.IsMatchingMdnsQuery(
            BuildMdnsQueryPacket("_http._tcp.local"), "Tonewise-Desk", "myhost.local"));
        Assert.False(MdnsAdvertiser.IsMatchingMdnsQuery(
            BuildMdnsQueryPacket("_tonewise._tcp.local", flags: 0x8400), "Tonewise-Desk", "myhost.local"));
        Assert.False(MdnsAdvertiser.IsMatchingMdnsQuery(new byte[] { 0, 1, 2 }, "Tonewise-Desk", "myhost.local"));
        // Infinite self-referential DNS pointer loop guard (0xC0 0x0C at offset 12)
        byte[] malicious = [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C, 0, 12, 0, 1];
        Assert.False(MdnsAdvertiser.IsMatchingMdnsQuery(malicious, "Tonewise-Desk", "myhost.local"));
    }

    [Fact]
    public async Task StatusEndpoint_ManifestSha256ChangesOnUncheckpointedWalCommit()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b3_wal_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-b3-wal");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var pairResp = await http.PostAsJsonAsync("/api/v2/pair", new
            {
                code = server.Store.CurrentPairingCode,
                device_id = "wal-phone",
                device_name = "WAL"
            });
            pairResp.EnsureSuccessStatusCode();
            using (var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync()))
            {
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", pairDoc.RootElement.GetProperty("token").GetString()!);
            }

            string sha1;
            using (var st1 = JsonDocument.Parse(await http.GetStringAsync("/api/v1/status")))
            {
                sha1 = st1.RootElement.GetProperty("manifest_sha256").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(sha1));
            }

            // Hold an active SQLite read snapshot so WAL cannot be truncated into main db
            using var holdReader = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            holdReader.Open();
            using (var beginCmd = holdReader.CreateCommand())
            {
                beginCmd.CommandText = "BEGIN DEFERRED; SELECT COUNT(*) FROM tracks;";
                beginCmd.ExecuteScalar();
            }

            using (var writerConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                writerConn.Open();
                using var ins = writerConn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES (
                        'wal_uid_1', X'ABCD', 64, X'1234', 0, 'Jazz/01.flac', 1000, 1000, 'flac', 44100, 120000, 'WAL Song'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            // With holdReader keeping WAL pinned, manifest_sha256 must STILL change!
            string sha2;
            using (var st2 = JsonDocument.Parse(await http.GetStringAsync("/api/v1/status")))
            {
                sha2 = st2.RootElement.GetProperty("manifest_sha256").GetString()!;
            }
            Assert.NotEqual(sha1, sha2);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicStores_SessionSlotsAndPlaylistsLeaveValidFilesWithNoTempArtifacts()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b3_atomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            string sessionsPath = Path.Combine(tempRoot, ".tonewise", "sessions.json");
            var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            {
                int slotIdx = (i % 5) + 1;
                SessionSlotStore.SaveSlot(
                    sessionsPath,
                    new SessionSlotDto(slotIdx, $"Slot {slotIdx}", "focus", (ulong)(1000 + i), [$"uid_{i}"], 0, 100UL));
            }));
            await Task.WhenAll(tasks);

            var loadedSlots = SessionSlotStore.LoadOrInitializeDefault(sessionsPath);
            Assert.Equal(5, loadedSlots.Count);

            string savedPl = PlaylistStore.SavePlaylist(
                tempRoot,
                "Atomic Night Mix",
                [new PlaylistEntry("Jazz/01.flac", "Song 1", "Miles", "Blue", 200_000, true, "uid_1")]);
            Assert.True(File.Exists(savedPl));

            string[] leftoverTemps = Directory.GetFiles(tempRoot, "*.*", SearchOption.AllDirectories)
                .Where(f => f.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)
                         || f.Contains(".twtmp.", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(leftoverTemps);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ManifestEndpoint_Sha256MatchesServedBytes_AndIfRangeProtectsAcrossSnapshotRotation()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b3_ifrange_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-b3-ifrange");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var pairResp = await http.PostAsJsonAsync("/api/v2/pair", new
            {
                code = server.Store.CurrentPairingCode,
                device_id = "ifrange-phone",
                device_name = "IfRange"
            });
            pairResp.EnsureSuccessStatusCode();
            using (var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync()))
            {
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", pairDoc.RootElement.GetProperty("token").GetString()!);
            }

            string statusSha1;
            using (var st1 = JsonDocument.Parse(await http.GetStringAsync("/api/v1/status")))
            {
                statusSha1 = st1.RootElement.GetProperty("manifest_sha256").GetString()!;
            }

            byte[] full1;
            string? etag1;
            using (var fullResp1 = await http.GetAsync("/api/v1/manifest"))
            {
                fullResp1.EnsureSuccessStatusCode();
                etag1 = fullResp1.Headers.ETag?.Tag;
                full1 = await fullResp1.Content.ReadAsByteArrayAsync();
            }
            Assert.Equal($"\"{statusSha1}\"", etag1);
            Assert.Equal(statusSha1, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(full1)));

            // Resuming with matching If-Range returns 206 Partial Content that reconstructs a valid SQLite DB.
            int split = full1.Length / 2;
            using var rangeReq1 = new HttpRequestMessage(HttpMethod.Get, "/api/v1/manifest");
            rangeReq1.Headers.Range = new RangeHeaderValue(split, null);
            rangeReq1.Headers.TryAddWithoutValidation("If-Range", etag1!);
            using var rangeResp1 = await http.SendAsync(rangeReq1);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResp1.StatusCode);
            byte[] part2 = await rangeResp1.Content.ReadAsByteArrayAsync();
            byte[] combined = new byte[split + part2.Length];
            Buffer.BlockCopy(full1, 0, combined, 0, split);
            Buffer.BlockCopy(part2, 0, combined, split, part2.Length);
            Assert.Equal(full1, combined);

            string checkPath = Path.Combine(tempRoot, "reconstructed.db");
            await File.WriteAllBytesAsync(checkPath, combined);
            using (var checkConn = new SqliteConnection($"Data Source={checkPath};Mode=ReadOnly"))
            {
                checkConn.Open();
                using var cmd = checkConn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", cmd.ExecuteScalar()?.ToString());
            }

            // Mutate source DB so snapshot rotates.
            using (var writerConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                writerConn.Open();
                using var ins = writerConn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES (
                        'rot_uid_2', X'DCBA', 64, X'4321', 0, 'Jazz/02.flac', 2000, 2000, 'flac', 44100, 120000, 'Rotated Song'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            // Stale If-Range + Range must return 200 OK full new snapshot rather than splicing two snapshots.
            using var staleRangeReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/manifest");
            staleRangeReq.Headers.Range = new RangeHeaderValue(split, null);
            staleRangeReq.Headers.TryAddWithoutValidation("If-Range", etag1!);
            using var staleResp = await http.SendAsync(staleRangeReq);
            Assert.Equal(HttpStatusCode.OK, staleResp.StatusCode);
            string? etag2 = staleResp.Headers.ETag?.Tag;
            Assert.NotNull(etag2);
            Assert.NotEqual(etag1, etag2);
            byte[] full2 = await staleResp.Content.ReadAsByteArrayAsync();
            Assert.Equal(etag2, $"\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(full2))}\"");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void PairingLockout_RotatesCodeAfter15GlobalFailuresAcrossIps_AndBoundsClientMap()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b3_pair_global_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SyncOutboxAndPairingStore(tempRoot);
            string initialCode = store.CurrentPairingCode;

            // 14 distributed failures across distinct client IPs stay under global cap
            for (int i = 1; i <= 14; i++)
            {
                Assert.Equal(
                    PairAttemptStatus.InvalidCode,
                    store.TryPairWithStatus("999999", $"bot-{i}", "Bot", out _, out _, out _, clientKey: $"192.168.10.{i}"));
            }

            // 15th distributed failure trips global cap, rotates pairing code, and returns LockedOut
            Assert.Equal(
                PairAttemptStatus.LockedOut,
                store.TryPairWithStatus("999999", "bot-15", "Bot", out _, out _, out int retrySec, clientKey: "192.168.10.15"));
            Assert.True(retrySec > 0);

            // Even if random 6-digit rarely collided (1e-6), pairing with CurrentPairingCode from a fresh/reset IP succeeds
            string currentCode = store.CurrentPairingCode;
            Assert.Equal(
                PairAttemptStatus.Success,
                store.TryPairWithStatus(currentCode, "legit-phone", "Phone", out string? token, out _, out _, clientKey: "192.168.10.1"));
            Assert.False(string.IsNullOrWhiteSpace(token));

            // Flood >260 distinct client IPs across multiple rotations; valid pairing still succeeds
            for (int i = 0; i < 270; i++)
            {
                _ = store.TryPairWithStatus("000000", $"flood-{i}", "Flood", out _, out _, out _, clientKey: $"fd00::{i:X4}");
            }
            string finalCode = store.CurrentPairingCode;
            Assert.NotEqual(initialCode, finalCode);
            Assert.Equal(
                PairAttemptStatus.Success,
                store.TryPairWithStatus(finalCode, "legit-phone-2", "Phone 2", out string? token2, out _, out _, clientKey: "192.168.10.200"));
            Assert.False(string.IsNullOrWhiteSpace(token2));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StatusEndpoint_HandlesConcurrentWalCheckpointAndRemoval_Without500()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_b3_wal_race_" + Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = CreateManifestDb(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-wal-race");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            Assert.Equal(
                PairAttemptStatus.Success,
                server.Store.TryPairWithStatus(server.Store.CurrentPairingCode, "wal-phone", "Phone", out string? token, out _, out _, clientKey: "127.0.0.1"));

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var cts = new CancellationTokenSource();
            var writerTask = Task.Run(() =>
            {
                int i = 0;
                while (!cts.IsCancellationRequested)
                {
                    using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
                    {
                        using var ins = conn.CreateCommand();
                        ins.CommandText = """
                            INSERT OR REPLACE INTO tracks (
                                track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                                rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                                title, artists_json
                            ) VALUES (
                                'uid_wal_race', X'01', 16, X'02', 0,
                                'Race/01.flac', 1000, $ts, 'flac', 44100, 120000,
                                'Race Track', '["Tester"]'
                            );
                            """;
                        ins.Parameters.AddWithValue("$ts", 1000 + i);
                        ins.ExecuteNonQuery();

                        using var chk = conn.CreateCommand();
                        chk.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        chk.ExecuteNonQuery();
                    }

                    if ((i & 1) == 1)
                    {
                        SqliteConnection.ClearAllPools();
                    }
                    i++;
                }
            });

            try
            {
                for (int n = 0; n < 30; n++)
                {
                    using var statusResp = await http.GetAsync("/api/v1/status");
                    Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
                    using var doc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync());
                    string? manifestSha = doc.RootElement.GetProperty("manifest_sha256").GetString();
                    Assert.False(string.IsNullOrWhiteSpace(manifestSha));
                    Assert.Equal(64, manifestSha!.Length);

                    if (n % 10 == 0)
                    {
                        using var manResp = await http.GetAsync("/api/v1/manifest");
                        Assert.Equal(HttpStatusCode.OK, manResp.StatusCode);
                        byte[] bytes = await manResp.Content.ReadAsByteArrayAsync();
                        Assert.NotEmpty(bytes);
                    }
                }
            }
            finally
            {
                cts.Cancel();
                await writerTask;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
