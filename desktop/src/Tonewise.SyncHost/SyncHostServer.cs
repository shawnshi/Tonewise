using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tonewise.Interop;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;

namespace Tonewise.SyncHost;

public sealed record M3u8MergeApiRequest(
    [property: JsonPropertyName("playlist_name")] string PlaylistName,
    [property: JsonPropertyName("base")] List<string> Base,
    [property: JsonPropertyName("ours")] List<string> Ours,
    [property: JsonPropertyName("theirs")] List<string> Theirs
);

public sealed record HistoryPushRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("events")] List<JsonElement> Events
);

public sealed record MutationsPushRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("mutations")] List<JsonElement> Mutations
);

public sealed record PairApiRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string? DeviceName
);

public sealed record ReportApiRequest(
    [property: JsonPropertyName("device_id")] string? DeviceId,
    [property: JsonPropertyName("play_events")] List<JsonElement>? PlayEvents,
    [property: JsonPropertyName("mutations")] List<JsonElement>? Mutations,
    [property: JsonPropertyName("completed_outbox_ids")] List<string>? CompletedOutboxIds,
    [property: JsonPropertyName("handoff_slots")] List<SessionSlotDto>? HandoffSlots
);

public sealed class SyncHostServer : IAsyncDisposable
{
    private readonly string _musicRoot;
    private readonly string _tonewiseDir;
    private readonly string _deviceId;
    private readonly Lock _fileLock = new();
    private WebApplication? _app;
    private MdnsAdvertiser? _mdns;
    private string? _manifestHashPath;
    private long _manifestHashLen;
    private long _manifestHashWriteTicks;
    private string? _manifestHashHex;

    public SyncOutboxAndPairingStore Store { get; }
    public event Action? SyncStateChanged;

    public int ListeningPort { get; private set; }
    public bool IsRunning => _app is not null;

    public SyncHostServer(string musicRoot, string? deviceId = null, SyncOutboxAndPairingStore? store = null)
    {
        _musicRoot = musicRoot;
        _tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        _deviceId = deviceId ?? Environment.MachineName;
        Store = store ?? new SyncOutboxAndPairingStore(musicRoot);
    }

    public async Task<int> StartAsync(int port = 45820, bool enableMdns = true)
    {
        if (_app is not null) return ListeningPort;
        Directory.CreateDirectory(_tonewiseDir);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });

        var app = builder.Build();
        MapRoutes(app);
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        string? firstAddr = addresses?.Addresses.FirstOrDefault();
        ListeningPort = firstAddr is not null && Uri.TryCreate(firstAddr, UriKind.Absolute, out var uri)
            ? uri.Port
            : port;

        _app = app;
        if (enableMdns && ListeningPort > 0)
        {
            _mdns = new MdnsAdvertiser($"Tonewise-{_deviceId}", ListeningPort);
            _mdns.Start();
        }
        return ListeningPort;
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/v1/status", () =>
        {
            string wfPath = Path.Combine(_tonewiseDir, "waveforms.bin");
            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            string encPath = Path.Combine(_tonewiseDir, "encyclopedia.db");
            if (File.Exists(dbPath))
            {
                CheckpointWalIfPresent(dbPath);
            }
            return Results.Json(new
            {
                schema_version = 1,
                device_id = _deviceId,
                has_manifest = File.Exists(dbPath),
                has_waveforms = File.Exists(wfPath),
                has_encyclopedia = File.Exists(encPath),
                waveforms_bytes = File.Exists(wfPath) ? new FileInfo(wfPath).Length : 0L,
                manifest_sha256 = File.Exists(dbPath) ? GetCachedManifestSha256Hex(dbPath) : null,
            });
        });

        app.MapGet("/api/v1/encyclopedia", (string? entityType, string? q) =>
        {
            string encPath = Path.Combine(_tonewiseDir, "encyclopedia.db");
            if (!File.Exists(encPath))
            {
                return Results.Json(new { items = Array.Empty<EntityBiographyEntry>() });
            }
            var svc = new EncyclopediaService(encPath);
            var items = svc.GetAllBiographies(entityType, q);
            return Results.Json(new { items });
        });

        app.MapGet("/api/v1/patch/{trackUid}", (string trackUid) =>
        {
            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            if (!File.Exists(dbPath) || string.IsNullOrWhiteSpace(trackUid))
            {
                return Results.NotFound();
            }

            string? relPath = null;
            long headerSize = 0;
            byte[]? headerHash = null;
            byte[]? audioHash = null;

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT rel_path, header_size, header_blake3, audio_frame_blake3
                    FROM tracks
                    WHERE track_uid = $uid;
                    """;
                cmd.Parameters.AddWithValue("$uid", trackUid);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    return Results.NotFound();
                }
                relPath = reader.GetString(0);
                headerSize = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                headerHash = reader.IsDBNull(2) ? null : (byte[])reader[2];
                audioHash = reader.IsDBNull(3) ? null : (byte[])reader[3];
            }

            if (string.IsNullOrWhiteSpace(relPath) || headerSize <= 0)
            {
                return Results.NotFound();
            }

            string rootFull = Path.GetFullPath(_musicRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string audioFull = Path.GetFullPath(Path.Combine(_musicRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!audioFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(audioFull))
            {
                return Results.NotFound();
            }

            int bytesToRead = (int)Math.Min(headerSize, 2 * 1024 * 1024L);
            byte[] headerBytes = new byte[bytesToRead];
            int totalRead = 0;
            using (var fs = File.OpenRead(audioFull))
            {
                while (totalRead < bytesToRead)
                {
                    int n = fs.Read(headerBytes, totalRead, bytesToRead - totalRead);
                    if (n == 0) break;
                    totalRead += n;
                }
            }

            if (totalRead < headerBytes.Length)
            {
                Array.Resize(ref headerBytes, totalRead);
            }

            return Results.Json(new
            {
                track_uid = trackUid,
                audio_frame_blake3_hex = audioHash is not null ? Convert.ToHexStringLower(audioHash) : null,
                header_blake3_hex = headerHash is not null ? Convert.ToHexStringLower(headerHash) : null,
                header_size = totalRead,
                header_base64 = Convert.ToBase64String(headerBytes),
            });
        });

        app.MapGet("/api/v1/scenes", () =>
        {
            string scenesPath = Path.Combine(_tonewiseDir, "scenes_5d.json");
            if (File.Exists(scenesPath))
            {
                return Results.Text(File.ReadAllText(scenesPath, Encoding.UTF8), "application/json", Encoding.UTF8);
            }
            return Results.Text(DefaultScenesJson(), "application/json", Encoding.UTF8);
        });

        app.MapGet("/api/v1/waveforms", () =>
        {
            string wfPath = Path.Combine(_tonewiseDir, "waveforms.bin");
            if (!File.Exists(wfPath)) return Results.NotFound();
            return Results.File(wfPath, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapGet("/api/v1/manifest", () =>
        {
            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            if (!File.Exists(dbPath)) return Results.NotFound();
            CheckpointWalIfPresent(dbPath);
            return Results.File(dbPath, "application/x-sqlite3", enableRangeProcessing: true);
        });

        app.MapPost("/api/v1/playlists/merge", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<M3u8MergeApiRequest>(request.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.PlaylistName))
                return Results.BadRequest(new { error = "invalid playlist merge payload" });

            string playlistsDir = Path.Combine(_musicRoot, "Playlists");
            string safeName = Path.GetFileNameWithoutExtension(body.PlaylistName) + ".m3u8";
            string outPath = Path.Combine(playlistsDir, safeName);

            List<string> normalizedBase = body.Base
                .Select(NormalizePlaylistRelPath)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            List<string> normalizedTheirs = body.Theirs
                .Select(NormalizePlaylistRelPath)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            List<string> effectiveOurs;
            lock (_fileLock)
            {
                if (File.Exists(outPath))
                {
                    effectiveOurs = File.ReadAllLines(outPath, Encoding.UTF8)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
                        .Select(NormalizePlaylistRelPath)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                }
                else
                {
                    effectiveOurs = body.Ours
                        .Select(NormalizePlaylistRelPath)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                }
            }

            var result = TonewiseCoreClient.MergeM3u8(normalizedBase, effectiveOurs, normalizedTheirs);
            if (result.Conflicts.Count == 0)
            {
                Directory.CreateDirectory(playlistsDir);
                string m3uText = "#EXTM3U\n" + string.Join("\n", result.Merged) + "\n";
                lock (_fileLock)
                {
                    File.WriteAllText(outPath, m3uText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            return Results.Json(result);
        });

        app.MapPost("/api/v1/history/push", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<HistoryPushRequest>(request.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.DeviceId))
                return Results.BadRequest(new { error = "invalid history payload" });

            string safeDevice = SanitizeId(body.DeviceId);
            string histPath = Path.Combine(_tonewiseDir, $"history.{safeDevice}.jsonl");
            var lines = body.Events.Select(e => e.GetRawText()).ToList();
            IncomingHistoryMergeResult mergeResult = new(0, new Dictionary<string, int>(StringComparer.Ordinal), []);
            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");

            lock (_fileLock)
            {
                File.AppendAllLines(histPath, lines, new UTF8Encoding(false));
                if (File.Exists(dbPath))
                {
                    mergeResult = LastFmLedgerService.MergeIncomingHistoryEventsDetailed(dbPath, body.Events, safeDevice);
                }
            }
            return Results.Json(new
            {
                accepted = lines.Count,
                merged_tracks = mergeResult.MergedTracks,
                effective_counts = mergeResult.EffectiveCounts,
                loved_uids = mergeResult.LovedUids,
            });
        });

        app.MapPost("/api/v1/mutations/push", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<MutationsPushRequest>(request.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.DeviceId))
                return Results.BadRequest(new { error = "invalid mutations payload" });

            string safeDevice = SanitizeId(body.DeviceId);
            string mutPath = Path.Combine(_tonewiseDir, $"mutations.{safeDevice}.jsonl");
            var lines = body.Mutations.Select(m => m.GetRawText()).ToList();
            LwwApplyResult applyResult = new(0, [], []);
            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            lock (_fileLock)
            {
                File.AppendAllLines(mutPath, lines, new UTF8Encoding(false));
                if (File.Exists(dbPath) && body.Mutations.Count > 0)
                {
                    applyResult = ApplyLwwMutationsToManifest(dbPath, body.Mutations);
                }
            }
            return Results.Json(new
            {
                accepted = lines.Count,
                applied_count = applyResult.AppliedCount,
                acknowledged_keys = applyResult.AcknowledgedKeys,
                unmatched_uids = applyResult.UnmatchedUids,
            });
        });

        app.MapGet("/api/v1/handoff", () =>
        {
            var slots = ReadSessions();
            return Results.Json(slots);
        });

        app.MapPost("/api/v1/handoff", async (HttpRequest request) =>
        {
            var incoming = await JsonSerializer.DeserializeAsync<List<SessionSlotDto>>(request.Body);
            if (incoming is null) return Results.BadRequest(new { error = "invalid sessions payload" });

            List<SessionSlotDto> merged;
            lock (_fileLock)
            {
                var current = ReadSessions();
                merged = TonewiseCoreClient.MergeSessions(current, incoming);
                WriteSessions(merged);
            }
            return Results.Json(merged);
        });

        // === v2 LAN Sync Content & Auth Endpoints ===

        app.MapGet("/api/v2/ping", () => Results.Json(new
        {
            schema_version = 2,
            host_name = _deviceId,
            port = ListeningPort,
            requires_pairing = true,
            paired_device_count = Store.GetPairedDevices().Count,
        }));

        app.MapPost("/api/v2/pair", async (HttpRequest request) =>
        {
            PairApiRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<PairApiRequest>(request.Body);
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid pairing payload" });
            }

            if (body is null || string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.DeviceId))
                return Results.BadRequest(new { error = "code and device_id are required" });

            if (!Store.TryPair(body.Code, body.DeviceId, body.DeviceName, out string? issuedToken, out var device) || device is null)
            {
                return Results.Json(new { error = "invalid_pairing_code" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            SyncStateChanged?.Invoke();
            return Results.Json(new
            {
                token = issuedToken,
                host_name = _deviceId,
                device_id = device.DeviceId,
                device_name = device.DeviceName,
                paired_at_ms = device.PairedAtMs,
            });
        });

        app.MapGet("/api/v2/outbox", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out var device, out var errorResult))
                return errorResult!;

            var payload = Store.ResolveOutbox(device!.DeviceId, _deviceId);
            return Results.Json(payload);
        });

        app.MapGet("/api/v2/catalog", (HttpRequest request, string? kind) =>
        {
            if (!TryAuthenticateV2(request, out _, out var errorResult))
                return errorResult!;

            string resolvedKind = string.IsNullOrWhiteSpace(kind) ? "playlists" : kind.Trim().ToLowerInvariant();
            var entries = Store.GetCatalog(resolvedKind);
            return Results.Json(new { kind = resolvedKind, items = entries });
        });

        app.MapGet("/api/v2/tracks/{trackUid}/file", (HttpRequest request, string trackUid) =>
        {
            if (!TryAuthenticateV2(request, out _, out var errorResult))
                return errorResult!;

            if (!Store.TryResolveTrackPhysicalPath(trackUid, out string fullPath, out _))
                return Results.NotFound();

            string ioPath = LongPathHelper.ToExtendedIfLong(fullPath);
            var stream = new FileStream(
                ioPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Results.File(
                stream,
                contentType: ResolveAudioMimeType(fullPath),
                fileDownloadName: Path.GetFileName(fullPath),
                enableRangeProcessing: true);
        });

        app.MapGet("/api/v2/tracks/{trackUid}/cover", (HttpRequest request, string trackUid) =>
        {
            if (!TryAuthenticateV2(request, out _, out var errorResult))
                return errorResult!;

            if (!Store.TryResolveTrackCoverPath(trackUid, out string coverPath))
                return Results.NotFound();

            string ioPath = LongPathHelper.ToExtendedIfLong(coverPath);
            string mime = coverPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            var stream = new FileStream(
                ioPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 32 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Results.File(stream, contentType: mime);
        });

        app.MapPost("/api/v2/report", async (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out var device, out var errorResult))
                return errorResult!;

            ReportApiRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ReportApiRequest>(request.Body);
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid report payload" });
            }
            if (body is null) return Results.BadRequest(new { error = "empty report payload" });

            string effectiveDeviceId = device!.DeviceId;
            string safeDevice = SanitizeId(effectiveDeviceId);
            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            IncomingHistoryMergeResult histResult = new(0, new Dictionary<string, int>(StringComparer.Ordinal), []);
            LwwApplyResult mutResult = new(0, [], []);
            int completedOutbox = 0;
            List<SessionSlotDto> mergedSlots;

            lock (_fileLock)
            {
                Store.TouchDeviceSync(effectiveDeviceId, nowMs);

                if (body.PlayEvents is { Count: > 0 })
                {
                    string deltaLogPath = Path.Combine(_tonewiseDir, $"lan_reports.{safeDevice}.jsonl");
                    File.AppendAllLines(deltaLogPath, body.PlayEvents.Select(e => e.GetRawText()), new UTF8Encoding(false));
                    if (File.Exists(dbPath))
                    {
                        histResult = LastFmLedgerService.MergeIncomingHistoryEventsDetailed(dbPath, body.PlayEvents, effectiveDeviceId);
                    }
                }
                else if (File.Exists(dbPath))
                {
                    var (counts, loved) = LastFmLedgerService.ReadEffectiveCountsAndLoved(dbPath);
                    histResult = new IncomingHistoryMergeResult(0, counts, loved);
                }

                if (body.Mutations is { Count: > 0 })
                {
                    string mutPath = Path.Combine(_tonewiseDir, $"mutations.{safeDevice}.jsonl");
                    File.AppendAllLines(mutPath, body.Mutations.Select(m => m.GetRawText()), new UTF8Encoding(false));
                    if (File.Exists(dbPath))
                    {
                        mutResult = ApplyLwwMutationsToManifest(dbPath, body.Mutations);
                    }
                }

                if (body.CompletedOutboxIds is { Count: > 0 })
                {
                    completedOutbox = Store.MarkCopyItemsCompleted(effectiveDeviceId, body.CompletedOutboxIds);
                }

                var currentSlots = ReadSessions();
                if (body.HandoffSlots is { Count: > 0 })
                {
                    mergedSlots = TonewiseCoreClient.MergeSessions(currentSlots, body.HandoffSlots);
                    WriteSessions(mergedSlots);
                }
                else
                {
                    mergedSlots = currentSlots;
                }
            }

            SyncStateChanged?.Invoke();
            return Results.Json(new
            {
                ok = true,
                merged_tracks = histResult.MergedTracks,
                effective_counts = histResult.EffectiveCounts,
                loved_uids = histResult.LovedUids,
                applied_mutations = mutResult.AppliedCount,
                acknowledged_mutation_keys = mutResult.AcknowledgedKeys,
                unmatched_mutation_uids = mutResult.UnmatchedUids,
                completed_outbox_count = completedOutbox,
                handoff_slots = mergedSlots,
            });
        });
    }

    private bool TryAuthenticateV2(HttpRequest request, out PairedDeviceRecord? device, out IResult? errorResult)
    {
        string? auth = request.Headers.Authorization.FirstOrDefault();
        if (Store.ValidateBearerToken(auth, out device))
        {
            errorResult = null;
            return true;
        }
        errorResult = Results.Json(new { error = "unauthorized", requires_pairing = true }, statusCode: StatusCodes.Status401Unauthorized);
        return false;
    }

    private string? GetCachedManifestSha256Hex(string dbPath)
    {
        lock (_fileLock)
        {
            if (!File.Exists(dbPath)) return null;
            var fi = new FileInfo(dbPath);
            long ticks = fi.LastWriteTimeUtc.Ticks;
            if (string.Equals(_manifestHashPath, dbPath, StringComparison.OrdinalIgnoreCase)
                && _manifestHashLen == fi.Length
                && _manifestHashWriteTicks == ticks
                && _manifestHashHex is not null)
            {
                return _manifestHashHex;
            }

            string hex = ComputeSha256Hex(dbPath);
            _manifestHashPath = dbPath;
            _manifestHashLen = fi.Length;
            _manifestHashWriteTicks = ticks;
            _manifestHashHex = hex;
            return hex;
        }
    }

    private static string NormalizePlaylistRelPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string p = raw.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        p = p.TrimStart('/');
        if (p.StartsWith("../Tracks/", StringComparison.OrdinalIgnoreCase))
            p = p["../Tracks/".Length..];
        if (p.StartsWith("Music/Tonewise/", StringComparison.OrdinalIgnoreCase))
            p = p["Music/Tonewise/".Length..];
        else if (p.StartsWith("Tonewise/", StringComparison.OrdinalIgnoreCase))
            p = p["Tonewise/".Length..];
        return p;
    }

    private static string ResolveAudioMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".flac" => "audio/flac",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".alac" or ".aac" or ".mp4" => "audio/mp4",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".opus" => "audio/opus",
        ".aiff" or ".aif" => "audio/aiff",
        ".dsf" or ".dff" => "application/octet-stream",
        _ => "application/octet-stream",
    };

    private List<SessionSlotDto> ReadSessions()
    {
        string path = Path.Combine(_tonewiseDir, "sessions.json");
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SessionSlotDto>>(File.ReadAllText(path, Encoding.UTF8)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WriteSessions(List<SessionSlotDto> slots)
    {
        string path = Path.Combine(_tonewiseDir, "sessions.json");
        string json = JsonSerializer.Serialize(slots, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + "\n", new UTF8Encoding(false));
    }

    private static void CheckpointWalIfPresent(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Non-fatal WAL checkpoint guard
        }
    }

    private sealed record LwwApplyResult(
        int AppliedCount,
        List<string> AcknowledgedKeys,
        List<string> UnmatchedUids
    );

    private static LwwApplyResult ApplyLwwMutationsToManifest(string dbPath, IReadOnlyList<JsonElement> mutations)
    {
        int applied = 0;
        var acknowledged = new List<string>();
        var unmatched = new List<string>();

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var resolver = LastFmLedgerService.TrackUidResolver.Build(conn);
        using var tx = conn.BeginTransaction();

        foreach (var m in mutations)
        {
            if (m.ValueKind != JsonValueKind.Object) continue;

            string rawUid = m.TryGetProperty("track_uid", out var uidEl) ? (uidEl.GetString() ?? "") : "";
            string? rawField = m.TryGetProperty("field", out var fEl) ? fEl.GetString()
                : (m.TryGetProperty("field_name", out var fnEl) ? fnEl.GetString() : null);

            long incomingTs = 0;
            if (m.TryGetProperty("ts", out var tsEl) && tsEl.TryGetInt64(out long t1)) incomingTs = t1;
            else if (m.TryGetProperty("ts_ms", out var ts2El) && ts2El.TryGetInt64(out long t2)) incomingTs = t2;
            else if (m.TryGetProperty("updated_at_ms", out var ts3El) && ts3El.TryGetInt64(out long t3)) incomingTs = t3;

            if (string.IsNullOrWhiteSpace(rawField) || incomingTs <= 0)
                continue;

            if (!LwwFieldColumnMap.TryGetValue(rawField.Trim(), out var mapping))
                continue;

            string mutationKey = m.TryGetProperty("mutation_key", out var mkEl) && mkEl.ValueKind == JsonValueKind.String
                ? (mkEl.GetString() ?? "")
                : $"{rawUid}|{rawField.Trim()}|{incomingTs}";

            string? resolvedUid = resolver.ResolveFromJson(m);
            if (string.IsNullOrWhiteSpace(resolvedUid))
            {
                if (!string.IsNullOrWhiteSpace(rawUid)) unmatched.Add(rawUid);
                continue;
            }

            object dbVal = DBNull.Value;
            if (m.TryGetProperty("value", out var valEl) && valEl.ValueKind != JsonValueKind.Null && valEl.ValueKind != JsonValueKind.Undefined)
            {
                if (mapping.ValCol is "dj_scene_json" or "mood_json")
                {
                    if (valEl.ValueKind == JsonValueKind.Array)
                    {
                        dbVal = valEl.GetRawText();
                    }
                    else
                    {
                        string str = valEl.ValueKind == JsonValueKind.String ? (valEl.GetString() ?? "") : valEl.ToString();
                        dbVal = str.TrimStart().StartsWith('[')
                            ? str
                            : JsonSerializer.Serialize(str.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                }
                else if (mapping.ValCol == "rating")
                {
                    int rVal = valEl.ValueKind == JsonValueKind.Number && valEl.TryGetInt32(out int ri) ? ri
                        : (int.TryParse(valEl.ToString(), out int rp) ? rp : 0);
                    dbVal = Math.Clamp(rVal, 0, 5);
                }
                else if (mapping.ValCol == "tempo_bpm")
                {
                    if (valEl.ValueKind == JsonValueKind.Number && valEl.TryGetDouble(out double dVal))
                        dbVal = dVal;
                    else if (double.TryParse(valEl.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dp))
                        dbVal = dp;
                }
                else
                {
                    dbVal = valEl.ValueKind == JsonValueKind.String ? (object)(valEl.GetString() ?? "") : valEl.ToString();
                }
            }

            long currentTs = 0;
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = tx;
                checkCmd.CommandText = $"SELECT {mapping.TsCol} FROM tracks WHERE track_uid = $uid;";
                checkCmd.Parameters.AddWithValue("$uid", resolvedUid);
                object? existing = checkCmd.ExecuteScalar();
                if (existing is null || existing == DBNull.Value)
                {
                    if (!string.IsNullOrWhiteSpace(rawUid)) unmatched.Add(rawUid);
                    continue;
                }
                currentTs = Convert.ToInt64(existing);
            }

            if (incomingTs > currentTs)
            {
                using var updCmd = conn.CreateCommand();
                updCmd.Transaction = tx;
                updCmd.CommandText = $"UPDATE tracks SET {mapping.ValCol} = $val, {mapping.TsCol} = $ts WHERE track_uid = $uid;";
                updCmd.Parameters.AddWithValue("$val", dbVal);
                updCmd.Parameters.AddWithValue("$ts", incomingTs);
                updCmd.Parameters.AddWithValue("$uid", resolvedUid);
                applied += updCmd.ExecuteNonQuery();
            }

            // Track matched in DB: acknowledge both applied and superseded writes so client clears them
            acknowledged.Add(mutationKey);
        }

        tx.Commit();
        return new LwwApplyResult(applied, acknowledged, unmatched);
    }

    private static readonly IReadOnlyDictionary<string, (string ValCol, string TsCol)> LwwFieldColumnMap =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["dj_scene"] = ("dj_scene_json", "dj_scene_ts"),
            ["dj_scene_json"] = ("dj_scene_json", "dj_scene_ts"),
            ["dj_vocals"] = ("dj_vocals", "dj_vocals_ts"),
            ["dj_energy"] = ("dj_energy", "dj_energy_ts"),
            ["tempo_bpm"] = ("tempo_bpm", "tempo_ts"),
            ["tempo_bucket"] = ("tempo_bucket", "tempo_ts"),
            ["mood"] = ("mood_json", "mood_ts"),
            ["mood_json"] = ("mood_json", "mood_ts"),
            ["rating"] = ("rating", "rating_ts"),
        };

    private static string SanitizeId(string id) =>
        string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));

    private static string ComputeSha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static string DefaultScenesJson() => """
        {
          "schema_version": 1,
          "scenes": [
            {
              "id": "11111111-1111-4111-8111-111111111111",
              "title": "深度专注 · 纯器乐",
              "subtitle": "低/中能量纯器乐，Tempo 空值非阻断保留",
              "accent_hex": "#005EB8",
              "updated_at_ms": 1763251200000,
              "sort_arc": "flat",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "dj_vocals", "op": "eq", "values": ["instrumental"] } },
                  { "predicate": { "field": "dj_energy", "op": "in", "values": ["low", "medium"] } }
                ]
              }
            },
            {
              "id": "33333333-3333-4333-8333-333333333333",
              "title": "夜间降噪 · 松弛助眠",
              "subtitle": "静谧松弛 Mood 簇 + 阶梯能量衰减弧线",
              "accent_hex": "#2A6B54",
              "updated_at_ms": 1763251200000,
              "sort_arc": "energy_desc_sleep",
              "tempo_null_passthrough": true,
              "rule": {
                "any": [
                  { "predicate": { "field": "mood_cluster", "op": "in", "values": ["calm_relaxed"] } },
                  { "predicate": { "field": "dj_energy", "op": "eq", "values": ["low"] } }
                ]
              }
            },
            {
              "id": "44444444-4444-4444-8444-444444444444",
              "title": "沉睡馆藏唤醒",
              "subtitle": "超 90 天未播高分馆藏曲目重温",
              "accent_hex": "#8B5E3C",
              "updated_at_ms": 1763251200000,
              "sort_arc": "rediscovery_90d",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "unplayed_days_gte", "op": "gte", "values": [90] } },
                  { "predicate": { "field": "min_rating", "op": "gte", "values": [4] } }
                ]
              }
            }
          ]
        }
        """;

    public async ValueTask DisposeAsync()
    {
        if (_mdns is not null)
        {
            await _mdns.DisposeAsync();
            _mdns = null;
        }
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
