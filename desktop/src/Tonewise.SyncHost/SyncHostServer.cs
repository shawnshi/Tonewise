using System.Net;
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
using Microsoft.Net.Http.Headers;
using Tonewise.Interop;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;
using Tonewise.SyncHost.Usb;

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

public sealed record UsbPairApiRequest(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string? DeviceName
);

public sealed record UsbPushTrackRequestItem(
    [property: JsonPropertyName("track_uid")] string TrackUid,
    [property: JsonPropertyName("rel_path")] string RelPath,
    [property: JsonPropertyName("file_size")] long FileSize,
    [property: JsonPropertyName("mtime_ms")] long MtimeMs
);

public sealed record UsbPushCoverRequestItem(
    [property: JsonPropertyName("album_dir")] string AlbumDir,
    [property: JsonPropertyName("sample_track_uid")] string SampleTrackUid
);

public sealed record UsbPushTracksApiRequest(
    [property: JsonPropertyName("remote_music_root")] string? RemoteMusicRoot,
    [property: JsonPropertyName("tracks")] IReadOnlyList<UsbPushTrackRequestItem> Tracks,
    [property: JsonPropertyName("covers")] IReadOnlyList<UsbPushCoverRequestItem>? Covers
);

public sealed record SyncSessionProgressReport(
    [property: JsonPropertyName("device_id")] string? DeviceId = null,
    [property: JsonPropertyName("transport")] string? Transport = null,
    [property: JsonPropertyName("phase")] string? Phase = null,
    [property: JsonPropertyName("is_running")] bool IsRunning = false,
    [property: JsonPropertyName("completed_tracks")] int CompletedTracks = 0,
    [property: JsonPropertyName("total_tracks")] int TotalTracks = 0,
    [property: JsonPropertyName("downloaded_bytes")] long DownloadedBytes = 0,
    [property: JsonPropertyName("total_bytes")] long TotalBytes = 0,
    [property: JsonPropertyName("current_track_title")] string? CurrentTrackTitle = null,
    [property: JsonPropertyName("added_count")] int AddedCount = 0,
    [property: JsonPropertyName("updated_count")] int UpdatedCount = 0,
    [property: JsonPropertyName("skipped_count")] int SkippedCount = 0,
    [property: JsonPropertyName("deleted_count")] int DeletedCount = 0,
    [property: JsonPropertyName("blocked_foreign_paths")] IReadOnlyList<string>? BlockedForeignPaths = null,
    [property: JsonPropertyName("pending_large_prune_count")] int PendingLargePruneCount = 0,
    [property: JsonPropertyName("local_audio_count")] int LocalAudioCount = 0,
    [property: JsonPropertyName("pending_large_prune_ratio")] double PendingLargePruneRatio = 0.0,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null,
    [property: JsonPropertyName("completed")] bool Completed = false
);

public sealed class SyncHostServer : IAsyncDisposable
{
    private sealed record ManifestSnapshotCache(
        string FilePath,
        string Sha256Hex,
        long Length,
        string SourceStamp
    );

    private sealed record UsbNonceEntry(
        string Nonce,
        string Serial,
        bool AllowAutoPair,
        bool PruneConfirmed,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc
    );

    private readonly string _musicRoot;
    private readonly string _tonewiseDir;
    private readonly string _deviceId;
    private readonly bool _allowPhonePlaylistUpload;
    private readonly Lock _fileLock = new();
    private readonly Lock _manifestSnapshotLock = new();
    private readonly Lock _usbSessionLock = new();
    private readonly List<UsbNonceEntry> _usbNonces = [];
    private readonly HashSet<string> _largePruneConfirmedDevices = new(StringComparer.OrdinalIgnoreCase);
    private bool _cancelActiveSync;
    private CancellationTokenSource? _activeUsbDirectPushCts;
    private WebApplication? _app;
    private MdnsAdvertiser? _mdns;
    private ManifestSnapshotCache? _manifestSnapshot;
    private readonly List<string> _retiredManifestSnapshots = [];
    private string? _waveformsHashPath;
    private long _waveformsHashLen;
    private long _waveformsHashWriteTicks;
    private string? _waveformsHashHex;

    public SyncOutboxAndPairingStore Store { get; }
    public Func<UsbPushTracksApiRequest, Func<UsbDirectPushProgress, Task>, CancellationToken, Task<UsbDirectPushSummary>>? UsbDirectPushHandler { get; set; }
    public event Action? SyncStateChanged;
    public event Action<SyncSessionProgressReport>? SyncSessionProgressChanged;
    public event Action<string>? AutoEqPolicyUpdated;

    private static readonly Lock AutoEqFileLock = new();

    public SyncSessionProgressReport? LatestSyncProgress { get; private set; }
    public int ListeningPort { get; private set; }
    public bool IsRunning => _app is not null;

    public SyncHostServer(
        string musicRoot,
        string? deviceId = null,
        SyncOutboxAndPairingStore? store = null,
        bool allowPhonePlaylistUpload = false)
    {
        _musicRoot = musicRoot;
        _tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        _deviceId = deviceId ?? Environment.MachineName;
        _allowPhonePlaylistUpload = allowPhonePlaylistUpload;
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
            _ = Task.Run(() => Store.WarmResolveCache());
        }
        return ListeningPort;
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/v1/status", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

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
                waveforms_sha256 = File.Exists(wfPath) ? GetCachedWaveformsSha256Hex(wfPath) : null,
                manifest_sha256 = File.Exists(dbPath) ? GetOrBuildManifestSnapshot(dbPath).Sha256Hex : null,
            });
        });

        app.MapGet("/api/v1/encyclopedia", (HttpRequest request, string? entityType, string? q) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

            string encPath = Path.Combine(_tonewiseDir, "encyclopedia.db");
            if (!File.Exists(encPath))
            {
                return Results.Json(new { items = Array.Empty<EntityBiographyEntry>() });
            }
            var svc = new EncyclopediaService(encPath);
            var items = svc.GetAllBiographies(entityType, q);
            return Results.Json(new { items });
        });

        app.MapGet("/api/v1/patch/{trackUid}", (HttpRequest request, string trackUid) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            if (!File.Exists(dbPath) || string.IsNullOrWhiteSpace(trackUid))
            {
                return Results.NotFound();
            }

            string? relPath = null;
            long headerSize = 0;
            byte[]? headerHash = null;
            byte[]? audioHash = null;

            using (var conn = TonewiseSqlite.OpenReadOnly(dbPath))
            {
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

            const long maxHeaderPatchBytes = 2 * 1024 * 1024L;
            if (headerSize > maxHeaderPatchBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            string rootFull = Path.GetFullPath(_musicRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string audioFull = Path.GetFullPath(Path.Combine(_musicRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!audioFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(audioFull))
            {
                return Results.NotFound();
            }

            try
            {
                var liveFp = TonewiseCoreClient.ComputeFingerprint(audioFull);
                if (liveFp.FooterSize > 0)
                {
                    return Results.Json(
                        new { error = "trailing_metadata_requires_full_sync" },
                        statusCode: StatusCodes.Status409Conflict);
                }
            }
            catch
            {
                // Continue best-effort prefix read if native fingerprint probe fails on synthetic files
            }

            int bytesToRead = (int)headerSize;
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

            if (totalRead != bytesToRead)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
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

        app.MapGet("/api/v1/scenes", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

            string scenesPath = Path.Combine(_tonewiseDir, "scenes_5d.json");
            if (File.Exists(scenesPath))
            {
                return Results.Text(File.ReadAllText(scenesPath, Encoding.UTF8), "application/json", Encoding.UTF8);
            }
            return Results.Text(DefaultScenesJson(), "application/json", Encoding.UTF8);
        });

        app.MapGet("/api/v1/waveforms", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

            string wfPath = Path.Combine(_tonewiseDir, "waveforms.bin");
            if (!File.Exists(wfPath)) return Results.NotFound();
            return Results.File(wfPath, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapGet("/api/v1/manifest", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            if (!File.Exists(dbPath)) return Results.NotFound();

            var snap = GetOrBuildManifestSnapshot(dbPath);
            var stream = new FileStream(
                snap.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Results.File(
                stream,
                contentType: "application/x-sqlite3",
                lastModified: null,
                entityTag: new EntityTagHeaderValue($"\"{snap.Sha256Hex}\""),
                enableRangeProcessing: true);
        });

        app.MapPost("/api/v1/playlists/merge", async (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;
            if (!_allowPhonePlaylistUpload)
            {
                return Results.Json(
                    new { ok = false, disabled = true, error = "Phone-to-PC playlist sync is disabled (playlists sync PC -> Phone only)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var body = await JsonSerializer.DeserializeAsync<M3u8MergeApiRequest>(request.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.PlaylistName))
                return Results.BadRequest(new { error = "invalid playlist merge payload" });

            var (displayName, fileName) = NormalizePlaylistMergeName(body.PlaylistName);
            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(fileName))
                return Results.BadRequest(new { error = "invalid playlist merge payload" });

            string playlistsDir = Path.Combine(_musicRoot, "Playlists");
            string fullPlaylistsDir = Path.GetFullPath(playlistsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string outPath = Path.GetFullPath(Path.Combine(playlistsDir, fileName));
            if (!outPath.StartsWith(fullPlaylistsDir, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "invalid playlist name" });

            List<string> normalizedBase = (body.Base ?? [])
                .Select(NormalizePlaylistRelPath)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            List<string> normalizedOurs = (body.Ours ?? [])
                .Select(NormalizePlaylistRelPath)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            List<string> normalizedTheirs = (body.Theirs ?? [])
                .Select(NormalizePlaylistRelPath)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            List<string> effectiveOurs;
            var extinfByNormPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unmatchedEntries = new List<(string? ExtInf, string UnmatchedLine)>();
            lock (_fileLock)
            {
                if (File.Exists(outPath))
                {
                    effectiveOurs = [];
                    string? pendingExtInf = null;
                    foreach (string rawLine in File.ReadAllLines(outPath, Encoding.UTF8))
                    {
                        string line = rawLine.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                        {
                            pendingExtInf = line;
                            continue;
                        }
                        if (line.StartsWith("#EXTTW-UNMATCHED:", StringComparison.OrdinalIgnoreCase))
                        {
                            unmatchedEntries.Add((pendingExtInf, line));
                            pendingExtInf = null;
                            continue;
                        }
                        if (line.StartsWith('#'))
                        {
                            pendingExtInf = null;
                            continue;
                        }

                        string norm = NormalizePlaylistRelPath(line);
                        if (!string.IsNullOrEmpty(norm))
                        {
                            effectiveOurs.Add(norm);
                            if (!string.IsNullOrEmpty(pendingExtInf))
                            {
                                extinfByNormPath.TryAdd(norm, pendingExtInf);
                            }
                        }
                        pendingExtInf = null;
                    }
                }
                else
                {
                    // Absent on PC: preserve explicit distinct `ours` overrides from stateless API callers,
                    // while preventing Android (`ours` empty or `== base`) from resurrecting PC-deleted playlists.
                    bool hasExplicitDistinctOurs = normalizedOurs.Count > 0
                        && !normalizedOurs.SequenceEqual(normalizedBase, StringComparer.OrdinalIgnoreCase);
                    effectiveOurs = hasExplicitDistinctOurs ? normalizedOurs : [];
                }
            }

            var result = TonewiseCoreClient.MergeM3u8(normalizedBase, effectiveOurs, normalizedTheirs);
            bool deleted = false;
            if (result.Conflicts.Count == 0)
            {
                lock (_fileLock)
                {
                    if (result.Merged.Count == 0 && unmatchedEntries.Count == 0 && normalizedBase.Count > 0)
                    {
                        if (File.Exists(outPath)) File.Delete(outPath);
                        deleted = true;
                    }
                    else
                    {
                        Directory.CreateDirectory(playlistsDir);
                        var sb = new StringBuilder();
                        sb.Append("#EXTM3U\n#PLAYLIST:").Append(displayName).Append('\n');
                        foreach (string trackPath in result.Merged)
                        {
                            if (extinfByNormPath.TryGetValue(trackPath, out string? extinf))
                            {
                                sb.Append(extinf).Append('\n');
                            }
                            sb.Append(trackPath).Append('\n');
                        }
                        foreach (var (extinf, unmatchedLine) in unmatchedEntries)
                        {
                            if (!string.IsNullOrEmpty(extinf))
                            {
                                sb.Append(extinf).Append('\n');
                            }
                            sb.Append(unmatchedLine).Append('\n');
                        }
                        string tmpPath = outPath + $".twtmp.{Guid.NewGuid():N}";
                        File.WriteAllText(tmpPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        File.Move(tmpPath, outPath, overwrite: true);
                    }
                }
                SyncStateChanged?.Invoke();
            }
            return Results.Json(new
            {
                merged = result.Merged,
                conflicts = result.Conflicts,
                deleted,
            });
        });

        app.MapPost("/api/v1/history/push", async (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

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
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

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
                    applyResult = ApplyLwwMutationsToManifest(dbPath, body.Mutations, _tonewiseDir);
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

        app.MapGet("/api/v1/handoff", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

            var slots = ReadSessions();
            return Results.Json(slots);
        });

        app.MapPost("/api/v1/handoff", async (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

            var incoming = await JsonSerializer.DeserializeAsync<List<SessionSlotDto>>(request.Body);
            if (incoming is null) return Results.BadRequest(new { error = "invalid sessions payload" });

            List<SessionSlotDto> merged;
            lock (Workbench.SessionSlotStore.SyncFileLock)
            {
                var current = ReadSessionsUnlocked();
                merged = TonewiseCoreClient.MergeSessions(current, incoming);
                WriteSessionsUnlocked(merged);
            }
            return Results.Json(merged);
        });

        app.MapGet("/api/v1/auto-eq", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

            var policy = ReadAutoEqPolicy();
            return Results.Text(SerializeAutoEqPolicy(policy), "application/json", Encoding.UTF8);
        });

        app.MapGet("/api/v1/lastfm/session", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;

            var cfg = Ledger.LastFmAccountConfig.Load(_tonewiseDir);
            if (cfg is null || !cfg.HasWriteSession)
            {
                return Results.Json(new { configured = false });
            }

            return Results.Json(new
            {
                configured = true,
                username = cfg.Username,
                api_key = cfg.ApiKey,
                shared_secret = cfg.ApiSecret,
                session_key = cfg.SessionKey,
            });
        });

        app.MapPost("/api/v1/auto-eq", async (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

            AutoEqPolicyDto? incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<AutoEqPolicyDto>(request.Body);
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid auto-eq policy json" });
            }
            if (incoming is null) return Results.BadRequest(new { error = "empty auto-eq policy" });

            var (winner, changed) = MergeAutoEqPolicyWithChangeFlag(incoming);
            string json = SerializeAutoEqPolicy(winner);
            if (changed)
            {
                AutoEqPolicyUpdated?.Invoke(json);
                SyncStateChanged?.Invoke();
            }
            return Results.Text(json, "application/json", Encoding.UTF8);
        });

        // === v2 LAN Sync Content & Auth Endpoints ===

        app.MapGet("/api/v2/ping", (HttpRequest request) => Results.Json(new
        {
            schema_version = 2,
            host_name = _deviceId,
            host_id = Store.HostId,
            port = ListeningPort,
            requires_pairing = true,
            transport_hint = IsLoopbackRequest(request) ? "usb_loopback" : "lan",
        }));

        app.MapPost("/api/v2/pair", async (HttpRequest request) =>
        {
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

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

            string clientIpKey = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var status = Store.TryPairWithStatus(
                body.Code,
                body.DeviceId,
                body.DeviceName,
                out string? issuedToken,
                out var device,
                out int retryAfterSeconds,
                clientKey: clientIpKey);
            if (status == PairAttemptStatus.LockedOut)
            {
                request.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
                return Results.Json(
                    new { error = "pairing_locked_out", retry_after_seconds = retryAfterSeconds },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            if (status != PairAttemptStatus.Success || device is null)
            {
                return Results.Json(new { error = "invalid_pairing_code" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            SyncStateChanged?.Invoke();
            return Results.Json(new
            {
                token = issuedToken,
                host_name = _deviceId,
                host_id = Store.HostId,
                device_id = device.DeviceId,
                device_name = device.DeviceName,
                paired_at_ms = device.PairedAtMs,
            });
        });

        app.MapPost("/api/v2/usb/pair", async (HttpRequest request) =>
        {
            if (!IsLoopbackRequest(request))
            {
                return Results.Json(
                    new { error = "usb_pair_requires_loopback" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

            UsbPairApiRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<UsbPairApiRequest>(request.Body);
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid usb pairing payload" });
            }

            if (body is null || string.IsNullOrWhiteSpace(body.Nonce) || string.IsNullOrWhiteSpace(body.DeviceId))
                return Results.BadRequest(new { error = "nonce and device_id are required" });

            if (!TryConsumeUsbSyncNonce(body.Nonce, requireAllowAutoPair: true))
            {
                return Results.Json(
                    new { error = "invalid_or_expired_usb_nonce" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            PairedDeviceRecord device;
            string issuedToken;
            try
            {
                device = Store.PairDeviceDirect(body.DeviceId, body.DeviceName, out issuedToken);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "invalid device_id" });
            }

            SyncStateChanged?.Invoke();
            return Results.Json(new
            {
                token = issuedToken,
                host_name = _deviceId,
                host_id = Store.HostId,
                device_id = device.DeviceId,
                device_name = device.DeviceName,
                paired_at_ms = device.PairedAtMs,
            });
        });

        app.MapPost("/api/v2/unpair", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out var device, out var errorResult))
                return errorResult!;

            Store.UnpairDevice(device!.DeviceId);
            SyncStateChanged?.Invoke();
            return Results.Json(new { ok = true, unpaired_device_id = device.DeviceId });
        });

        app.MapGet("/api/v2/outbox", (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out var device, out var errorResult))
                return errorResult!;

            bool pruneConfirmed = IsLargePruneConfirmed(device!.DeviceId);
            var payload = Store.ResolveOutbox(device.DeviceId, _deviceId, pruneConfirmedForSession: pruneConfirmed);
            return Results.Json(payload);
        });

        app.MapPost("/api/v2/sync-session/progress", async (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out var device, out var errorResult))
                return errorResult!;
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

            SyncSessionProgressReport? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SyncSessionProgressReport>(request.Body);
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid sync progress payload" });
            }
            if (body is null)
                return Results.BadRequest(new { error = "empty sync progress payload" });

            var normalized = body with { DeviceId = device!.DeviceId };
            bool cancelRequested;
            bool pruneConfirmed;
            lock (_usbSessionLock)
            {
                LatestSyncProgress = normalized;
                cancelRequested = _cancelActiveSync;
                pruneConfirmed = _largePruneConfirmedDevices.Contains(device.DeviceId);
                if (normalized.Completed || !normalized.IsRunning)
                {
                    _cancelActiveSync = false;
                    _largePruneConfirmedDevices.Remove(device.DeviceId);
                }
            }

            SyncSessionProgressChanged?.Invoke(normalized);
            return Results.Json(new
            {
                ok = true,
                cancel_requested = cancelRequested,
                prune_confirmed = pruneConfirmed,
            });
        });

        app.MapGet("/api/v2/catalog", (HttpRequest request, string? kind) =>
        {
            if (!TryAuthenticateV2(request, out _, out var errorResult))
                return errorResult!;

            string resolvedKind = string.IsNullOrWhiteSpace(kind) ? "playlists" : kind.Trim().ToLowerInvariant();
            var entries = Store.GetCatalog(resolvedKind);
            return Results.Json(new { kind = resolvedKind, items = entries });
        });

        app.MapPost("/api/v2/usb/push-tracks", async (HttpContext httpContext) =>
        {
            HttpRequest request = httpContext.Request;
            if (!IsLoopbackRequest(request))
            {
                return Results.Json(
                    new { error = "usb_push_requires_loopback" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (!TryAuthenticateV2(request, out _, out var authErr))
                return authErr!;
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

            UsbPushTracksApiRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<UsbPushTracksApiRequest>(
                    request.Body,
                    cancellationToken: httpContext.RequestAborted);
            }
            catch
            {
                return Results.Json(new { error = "invalid_json" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (body is null)
                return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
            if (!UsbAdbSyncClient.TryValidateRemoteMusicRoot(body.RemoteMusicRoot, out _))
                return Results.Json(new { error = "invalid_remote_music_root" }, statusCode: StatusCodes.Status400BadRequest);

            var handler = UsbDirectPushHandler;
            if (handler is null)
            {
                return Results.Json(
                    new { error = "usb_direct_push_unavailable" },
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            CancellationTokenSource linkedCts;
            lock (_usbSessionLock)
            {
                if (_cancelActiveSync)
                {
                    return Results.Json(
                        new { error = "sync_cancelled" },
                        statusCode: StatusCodes.Status409Conflict);
                }

                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
                _activeUsbDirectPushCts = linkedCts;
            }

            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-cache";
            await using var writer = new StreamWriter(
                httpContext.Response.Body,
                new UTF8Encoding(false),
                bufferSize: 16 * 1024,
                leaveOpen: true);
            using var writeGate = new SemaphoreSlim(1, 1);

            async Task WriteNdjsonLineAsync(string jsonLine, CancellationToken writeCt)
            {
                await writeGate.WaitAsync(writeCt).ConfigureAwait(false);
                try
                {
                    await writer.WriteLineAsync(jsonLine.AsMemory(), writeCt).ConfigureAwait(false);
                    await writer.FlushAsync(writeCt).ConfigureAwait(false);
                }
                finally
                {
                    writeGate.Release();
                }
            }

            UsbDirectPushSummary summary;
            try
            {
                summary = await handler(
                    body,
                    progress => WriteNdjsonLineAsync(
                        JsonSerializer.Serialize(new
                        {
                            @event = "progress",
                            stage = progress.Stage,
                            completed_tracks = progress.CompletedTracks,
                            total_tracks = progress.TotalTracks,
                            bytes_transferred = progress.BytesTransferred,
                            total_bytes = progress.TotalBytes,
                            speed_mbps = Math.Round(progress.SpeedMbps, 2),
                            current_rel_path = progress.CurrentRelPath
                        }),
                        httpContext.RequestAborted),
                    linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                bool cancelledByUi;
                lock (_usbSessionLock)
                {
                    cancelledByUi = _cancelActiveSync;
                }
                summary = new UsbDirectPushSummary(
                    UsedDirectPush: false,
                    PushedTracks: [],
                    FailedTrackUids: body.Tracks.Select(t => t.TrackUid).ToArray(),
                    CoversPushed: 0,
                    CoversSkipped: 0,
                    TotalBytesTransferred: 0,
                    ElapsedSeconds: 0,
                    AverageMbps: 0,
                    Error: cancelledByUi ? "sync_cancelled" : "request_cancelled");
            }
            catch (Exception ex)
            {
                summary = new UsbDirectPushSummary(
                    UsedDirectPush: false,
                    PushedTracks: [],
                    FailedTrackUids: body.Tracks.Select(t => t.TrackUid).ToArray(),
                    CoversPushed: 0,
                    CoversSkipped: 0,
                    TotalBytesTransferred: 0,
                    ElapsedSeconds: 0,
                    AverageMbps: 0,
                    Error: ex.Message);
            }
            finally
            {
                lock (_usbSessionLock)
                {
                    if (ReferenceEquals(_activeUsbDirectPushCts, linkedCts))
                    {
                        _activeUsbDirectPushCts = null;
                    }
                }
                linkedCts.Dispose();
            }

            if (!httpContext.RequestAborted.IsCancellationRequested)
            {
                string completeLine = JsonSerializer.Serialize(new
                {
                    @event = "complete",
                    used_direct_push = summary.UsedDirectPush,
                    pushed_tracks = summary.PushedTracks.Select(t => new
                    {
                        track_uid = t.TrackUid,
                        rel_path = t.RelPath,
                        file_size = t.FileSize
                    }),
                    failed_track_uids = summary.FailedTrackUids,
                    covers_pushed = summary.CoversPushed,
                    covers_skipped = summary.CoversSkipped,
                    total_bytes_transferred = summary.TotalBytesTransferred,
                    elapsed_seconds = Math.Round(summary.ElapsedSeconds, 3),
                    average_mbps = Math.Round(summary.AverageMbps, 2),
                    error = summary.Error
                });
                await WriteNdjsonLineAsync(completeLine, httpContext.RequestAborted).ConfigureAwait(false);
            }

            return Results.Empty;
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
                FileShare.Read,
                bufferSize: 256 * 1024,
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
                FileShare.Read,
                bufferSize: 32 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Results.File(stream, contentType: mime);
        });

        app.MapPost("/api/v2/report", async (HttpRequest request) =>
        {
            if (!TryAuthenticateV2(request, out var device, out var errorResult))
                return errorResult!;
            if (!RequireJsonContentType(request, out var ctErr))
                return ctErr!;

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
            List<string> unlovedUids = [];
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
                        mutResult = ApplyLwwMutationsToManifest(dbPath, body.Mutations, _tonewiseDir);
                        if (mutResult.AppliedCount > 0)
                        {
                            var (counts, loved) = LastFmLedgerService.ReadEffectiveCountsAndLoved(dbPath);
                            histResult = new IncomingHistoryMergeResult(histResult.MergedTracks, counts, loved);
                        }
                    }
                }

                if (File.Exists(dbPath))
                {
                    unlovedUids = LastFmLedgerService.ReadUnlovedTrackUids(dbPath);
                }

                if (body.CompletedOutboxIds is { Count: > 0 })
                {
                    completedOutbox = Store.MarkCopyItemsCompleted(effectiveDeviceId, body.CompletedOutboxIds);
                }
            }

            lock (Workbench.SessionSlotStore.SyncFileLock)
            {
                var currentSlots = ReadSessionsUnlocked();
                if (body.HandoffSlots is { Count: > 0 })
                {
                    mergedSlots = TonewiseCoreClient.MergeSessions(currentSlots, body.HandoffSlots);
                    WriteSessionsUnlocked(mergedSlots);
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
                unloved_uids = unlovedUids,
                applied_mutations = mutResult.AppliedCount,
                acknowledged_mutation_keys = mutResult.AcknowledgedKeys,
                unmatched_mutation_uids = mutResult.UnmatchedUids,
                completed_outbox_count = completedOutbox,
                handoff_slots = mergedSlots,
            });
        });
    }

    public string IssueUsbSyncNonce(string serial, bool allowAutoPair = true, bool pruneConfirmed = false)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_usbSessionLock)
        {
            _usbNonces.RemoveAll(n => n.ExpiresAtUtc <= nowUtc);
            while (_usbNonces.Count >= 16)
            {
                _usbNonces.RemoveAt(0);
            }
            _usbNonces.Add(new UsbNonceEntry(
                Nonce: nonce,
                Serial: serial ?? "",
                AllowAutoPair: allowAutoPair,
                PruneConfirmed: pruneConfirmed,
                IssuedAtUtc: nowUtc,
                ExpiresAtUtc: nowUtc + TimeSpan.FromSeconds(60)));
        }
        return nonce;
    }

    public void RequestCancelActiveSync()
    {
        CancellationTokenSource? activePushCts;
        lock (_usbSessionLock)
        {
            _cancelActiveSync = true;
            activePushCts = _activeUsbDirectPushCts;
        }

        try
        {
            activePushCts?.Cancel();
        }
        catch
        {
            // Best-effort cancellation of active direct push
        }
    }

    public void ClearCancelActiveSync()
    {
        lock (_usbSessionLock)
        {
            _cancelActiveSync = false;
        }
    }

    public void ConfirmLargePruneForDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        lock (_usbSessionLock)
        {
            _largePruneConfirmedDevices.Add(deviceId.Trim());
        }
    }

    public void ClearLargePruneConfirmation(string? deviceId = null)
    {
        lock (_usbSessionLock)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                _largePruneConfirmedDevices.Clear();
            else
                _largePruneConfirmedDevices.Remove(deviceId.Trim());
        }
    }

    private bool IsLargePruneConfirmed(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        lock (_usbSessionLock)
        {
            return _largePruneConfirmedDevices.Contains(deviceId.Trim());
        }
    }

    private bool TryConsumeUsbSyncNonce(string submittedNonce, bool requireAllowAutoPair)
    {
        byte[] submittedBytes = Encoding.UTF8.GetBytes(submittedNonce.Trim());
        var nowUtc = DateTimeOffset.UtcNow;
        lock (_usbSessionLock)
        {
            _usbNonces.RemoveAll(n => n.ExpiresAtUtc <= nowUtc);
            for (int i = 0; i < _usbNonces.Count; i++)
            {
                var entry = _usbNonces[i];
                byte[] candidateBytes = Encoding.UTF8.GetBytes(entry.Nonce);
                if (candidateBytes.Length == submittedBytes.Length
                    && CryptographicOperations.FixedTimeEquals(candidateBytes, submittedBytes))
                {
                    if (requireAllowAutoPair && !entry.AllowAutoPair)
                    {
                        return false;
                    }
                    _usbNonces.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }
    }

    private static bool IsLoopbackRequest(HttpRequest request)
    {
        var ip = request.HttpContext.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
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

    private static bool RequireJsonContentType(HttpRequest request, out IResult? errorResult)
    {
        if (request.HasJsonContentType())
        {
            errorResult = null;
            return true;
        }
        errorResult = Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        return false;
    }

    private ManifestSnapshotCache GetOrBuildManifestSnapshot(string dbPath)
    {
        lock (_manifestSnapshotLock)
        {
            TryPruneRetiredSnapshotsUnlocked();
            string stamp = ComputeManifestSourceStamp(dbPath);
            if (_manifestSnapshot is { } cached
                && string.Equals(cached.SourceStamp, stamp, StringComparison.Ordinal)
                && File.Exists(cached.FilePath))
            {
                return cached;
            }

            string snapPath = Path.Combine(
                Path.GetTempPath(),
                $"tonewise_manifest_cache_{Environment.ProcessId}_{Guid.NewGuid():N}.db");
            try
            {
                TonewiseSqlite.CreateConsistentSnapshot(dbPath, snapPath);
                string hex = ComputeSha256Hex(snapPath);
                long len = new FileInfo(snapPath).Length;
                // Re-read stamp after VACUUM INTO to detect concurrent mutations occurring mid-snapshot.
                string finalStamp = ComputeManifestSourceStamp(dbPath);
                var next = new ManifestSnapshotCache(snapPath, hex, len, finalStamp);
                if (_manifestSnapshot is { } prev && !string.Equals(prev.FilePath, snapPath, StringComparison.OrdinalIgnoreCase))
                {
                    _retiredManifestSnapshots.Add(prev.FilePath);
                }
                _manifestSnapshot = next;
                TryPruneRetiredSnapshotsUnlocked();
                return next;
            }
            catch
            {
                try { if (File.Exists(snapPath)) File.Delete(snapPath); } catch { }
                throw;
            }
        }
    }

    private void TryPruneRetiredSnapshotsUnlocked()
    {
        if (_retiredManifestSnapshots.Count == 0) return;
        for (int i = _retiredManifestSnapshots.Count - 1; i >= 0; i--)
        {
            string path = _retiredManifestSnapshots[i];
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                _retiredManifestSnapshots.RemoveAt(i);
            }
            catch
            {
                // Keep path queued if an active Range/stream reader still holds a handle on Windows.
            }
        }
    }

    private static string ComputeManifestSourceStamp(string dbPath)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> metaBuf = stackalloc byte[16];

        try
        {
            using var dbFs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long dbLen = dbFs.Length;
            long dbTicks = 0L;
            try { dbTicks = File.GetLastWriteTimeUtc(dbPath).Ticks; } catch { }
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(metaBuf[..8], dbLen);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(metaBuf.Slice(8, 8), dbTicks);
            hasher.AppendData(metaBuf);

            byte[] header = new byte[100];
            int n = dbFs.Read(header, 0, header.Length);
            if (n > 0) hasher.AppendData(header.AsSpan(0, n));
        }
        catch (FileNotFoundException)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(metaBuf[..8], -1L);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(metaBuf.Slice(8, 8), 0L);
            hasher.AppendData(metaBuf);
        }
        catch (DirectoryNotFoundException)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(metaBuf[..8], -1L);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(metaBuf.Slice(8, 8), 0L);
            hasher.AppendData(metaBuf);
        }

        string walPath = dbPath + "-wal";
        Span<byte> walMetaBuf = stackalloc byte[16];
        try
        {
            using var walFs = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long walLen = walFs.Length;
            long walTicks = 0L;
            if (walLen > 0)
            {
                try { walTicks = File.GetLastWriteTimeUtc(walPath).Ticks; } catch { }
            }
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf[..8], walLen);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf.Slice(8, 8), walTicks);
            hasher.AppendData(walMetaBuf);

            if (walLen > 0)
            {
                if (walLen <= 8 * 1024 * 1024)
                {
                    byte[] buf = new byte[64 * 1024];
                    int n;
                    while ((n = walFs.Read(buf, 0, buf.Length)) > 0)
                    {
                        hasher.AppendData(buf.AsSpan(0, n));
                    }
                }
                else
                {
                    byte[] edge = new byte[64 * 1024];
                    int headN = walFs.Read(edge, 0, edge.Length);
                    if (headN > 0) hasher.AppendData(edge.AsSpan(0, headN));
                    long tailPos = Math.Max(0, walLen - edge.Length);
                    walFs.Seek(tailPos, SeekOrigin.Begin);
                    int tailN = walFs.Read(edge, 0, edge.Length);
                    if (tailN > 0) hasher.AppendData(edge.AsSpan(0, tailN));
                }
            }
        }
        catch (FileNotFoundException)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf[..8], -1L);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf.Slice(8, 8), 0L);
            hasher.AppendData(walMetaBuf);
        }
        catch (DirectoryNotFoundException)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf[..8], -1L);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf.Slice(8, 8), 0L);
            hasher.AppendData(walMetaBuf);
        }
        catch (IOException) when (!File.Exists(walPath))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf[..8], -1L);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf.Slice(8, 8), 0L);
            hasher.AppendData(walMetaBuf);
        }
        catch (UnauthorizedAccessException) when (!File.Exists(walPath))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf[..8], -1L);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(walMetaBuf.Slice(8, 8), 0L);
            hasher.AppendData(walMetaBuf);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private string? GetCachedWaveformsSha256Hex(string wfPath)
    {
        lock (_fileLock)
        {
            if (!File.Exists(wfPath)) return null;
            var fi = new FileInfo(wfPath);
            long ticks = fi.LastWriteTimeUtc.Ticks;
            if (string.Equals(_waveformsHashPath, wfPath, StringComparison.OrdinalIgnoreCase)
                && _waveformsHashLen == fi.Length
                && _waveformsHashWriteTicks == ticks
                && _waveformsHashHex is not null)
            {
                return _waveformsHashHex;
            }

            string hex = ComputeSha256Hex(wfPath);
            _waveformsHashPath = wfPath;
            _waveformsHashLen = fi.Length;
            _waveformsHashWriteTicks = ticks;
            _waveformsHashHex = hex;
            return hex;
        }
    }

    private static string NormalizePlaylistRelPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string p = raw.Trim().Replace('\\', '/');
        if (p.StartsWith("../Tracks/", StringComparison.OrdinalIgnoreCase))
            p = p["../Tracks/".Length..];
        while (p.StartsWith("./", StringComparison.Ordinal) || p.StartsWith("../", StringComparison.Ordinal))
            p = p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p[3..];
        p = p.TrimStart('/');
        if (p.StartsWith("Music/Tonewise/", StringComparison.OrdinalIgnoreCase))
            p = p["Music/Tonewise/".Length..];
        else if (p.StartsWith("Tonewise/", StringComparison.OrdinalIgnoreCase))
            p = p["Tonewise/".Length..];
        else if (p.StartsWith("Music/", StringComparison.OrdinalIgnoreCase))
            p = p["Music/".Length..];
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
        lock (Workbench.SessionSlotStore.SyncFileLock)
        {
            return ReadSessionsUnlocked();
        }
    }

    private List<SessionSlotDto> ReadSessionsUnlocked()
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

    private void WriteSessionsUnlocked(List<SessionSlotDto> slots)
    {
        Directory.CreateDirectory(_tonewiseDir);
        string path = Path.Combine(_tonewiseDir, "sessions.json");
        string json = JsonSerializer.Serialize(slots, new JsonSerializerOptions { WriteIndented = true });
        string tmpPath = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmpPath, json + "\n", new UTF8Encoding(false));
            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            catch
            {
                // Ignore temp cleanup failure
            }
        }
    }

    private static void CheckpointWalIfPresent(string dbPath)
    {
        try
        {
            using var conn = TonewiseSqlite.OpenReadWrite(dbPath);
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

    private static LwwApplyResult ApplyLwwMutationsToManifest(
        string dbPath,
        IReadOnlyList<JsonElement> mutations,
        string? tonewiseDir = null)
    {
        int applied = 0;
        var acknowledged = new List<string>();
        var unmatched = new List<string>();

        using var conn = TonewiseSqlite.OpenReadWrite(dbPath);
        var resolver = LastFmLedgerService.TrackUidResolver.Build(conn);
        using var tx = TonewiseSqlite.BeginImmediateTransaction(conn);

        foreach (var m in mutations)
        {
            if (m.ValueKind != JsonValueKind.Object) continue;

            string rawUid = m.TryGetProperty("track_uid", out var uidEl) ? (uidEl.GetString() ?? "") : "";
            string? rawField = m.TryGetProperty("field", out var fEl) ? fEl.GetString()
                : (m.TryGetProperty("field_name", out var fnEl) ? fnEl.GetString() : null);

            long incomingTs = 0;
            if (m.TryGetProperty("lww_ts_ms", out var lwwEl) && lwwEl.TryGetInt64(out long t0)) incomingTs = t0;
            else if (m.TryGetProperty("ts", out var tsEl) && tsEl.TryGetInt64(out long t1)) incomingTs = t1;
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

            if (!TryNormalizeLwwValue(mapping.ValCol, m, out object dbVal))
            {
                if (!string.IsNullOrWhiteSpace(rawUid)) unmatched.Add(rawUid);
                continue;
            }

            long currentTs = 0;
            string existingTitle = "";
            string existingArtistsJson = "[]";
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = tx;
                checkCmd.CommandText = $"SELECT {mapping.TsCol}, title, artists_json FROM tracks WHERE track_uid = $uid;";
                checkCmd.Parameters.AddWithValue("$uid", resolvedUid);
                using var reader = checkCmd.ExecuteReader();
                if (!reader.Read())
                {
                    if (!string.IsNullOrWhiteSpace(rawUid)) unmatched.Add(rawUid);
                    continue;
                }
                currentTs = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
                existingTitle = reader.IsDBNull(1) ? "" : reader.GetString(1);
                existingArtistsJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
            }

            if (incomingTs > currentTs)
            {
                using var updCmd = conn.CreateCommand();
                updCmd.Transaction = tx;
                updCmd.CommandText = $"UPDATE tracks SET {mapping.ValCol} = $val, {mapping.TsCol} = $ts WHERE track_uid = $uid;";
                updCmd.Parameters.AddWithValue("$val", dbVal);
                updCmd.Parameters.AddWithValue("$ts", incomingTs);
                updCmd.Parameters.AddWithValue("$uid", resolvedUid);
                int rows = updCmd.ExecuteNonQuery();
                applied += rows;

                if (rows > 0 && mapping.ValCol == "rating" && !string.IsNullOrWhiteSpace(tonewiseDir) && dbVal is int newRating)
                {
                    string primaryArtist = "";
                    try
                    {
                        var artists = JsonSerializer.Deserialize<List<string>>(existingArtistsJson);
                        if (artists is { Count: > 0 }) primaryArtist = artists[0];
                    }
                    catch
                    {
                        // Ignore malformed artists_json
                    }
                    LastFmLedgerService.EnqueueLoveChange(
                        tonewiseDir: tonewiseDir,
                        trackUid: resolvedUid,
                        artist: primaryArtist,
                        title: existingTitle,
                        isLoved: newRating >= 5,
                        updatedAtUnixSec: Math.Max(1L, incomingTs / 1000L));
                }
            }

            // Track matched in DB: acknowledge both applied and superseded writes so client clears them
            acknowledged.Add(mutationKey);
        }

        tx.Commit();
        return new LwwApplyResult(applied, acknowledged, unmatched);
    }

    private static bool TryNormalizeLwwValue(string valCol, JsonElement m, out object dbVal)
    {
        dbVal = DBNull.Value;
        if (!m.TryGetProperty("value", out var valEl)
            || valEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            dbVal = valCol switch
            {
                "dj_scene_json" or "mood_json" => "[]",
                "rating" => 0,
                _ => DBNull.Value,
            };
            return true;
        }

        switch (valCol)
        {
            case "dj_scene_json" or "mood_json":
            {
                if (valEl.ValueKind == JsonValueKind.Array)
                {
                    dbVal = valEl.GetRawText();
                    return true;
                }
                string str = valEl.ValueKind == JsonValueKind.String ? (valEl.GetString() ?? "") : valEl.ToString();
                dbVal = str.TrimStart().StartsWith('[')
                    ? str
                    : JsonSerializer.Serialize(str.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                return true;
            }
            case "rating":
            {
                int rVal;
                if (valEl.ValueKind == JsonValueKind.Number && valEl.TryGetInt32(out int ri))
                    rVal = ri;
                else if (!int.TryParse(valEl.ToString(), out rVal))
                    return false;
                dbVal = Math.Clamp(rVal, 0, 5);
                return true;
            }
            case "tempo_bpm":
            {
                string rawBpm = valEl.ValueKind == JsonValueKind.String ? (valEl.GetString() ?? "") : valEl.ToString();
                if (valEl.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(rawBpm))
                {
                    dbVal = DBNull.Value;
                    return true;
                }
                double dVal;
                if (valEl.ValueKind == JsonValueKind.Number && valEl.TryGetDouble(out double d1))
                    dVal = d1;
                else if (!double.TryParse(rawBpm, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dVal))
                    return false;
                if (!double.IsFinite(dVal) || dVal <= 0.0 || dVal >= 400.0)
                    return false;
                dbVal = dVal;
                return true;
            }
            case "dj_vocals":
            {
                string s = (valEl.ValueKind == JsonValueKind.String ? (valEl.GetString() ?? "") : valEl.ToString()).Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(s))
                {
                    dbVal = DBNull.Value;
                    return true;
                }
                if (s is "vocal" or "instrumental" or "unknown")
                {
                    dbVal = s;
                    return true;
                }
                return false;
            }
            case "dj_energy":
            {
                string s = (valEl.ValueKind == JsonValueKind.String ? (valEl.GetString() ?? "") : valEl.ToString()).Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(s))
                {
                    dbVal = DBNull.Value;
                    return true;
                }
                if (s is "low" or "medium" or "high" or "unknown")
                {
                    dbVal = s;
                    return true;
                }
                return false;
            }
            case "tempo_bucket":
            {
                string s = (valEl.ValueKind == JsonValueKind.String ? (valEl.GetString() ?? "") : valEl.ToString()).Trim();
                if (string.IsNullOrEmpty(s))
                {
                    dbVal = DBNull.Value;
                    return true;
                }
                string? canonical = s.ToLowerInvariant() switch
                {
                    "very slow" => "Very Slow",
                    "slow" => "Slow",
                    "moderate" => "Moderate",
                    "fast" => "Fast",
                    "very fast" => "Very Fast",
                    _ => null,
                };
                if (canonical is null) return false;
                dbVal = canonical;
                return true;
            }
            default:
                dbVal = valEl.ValueKind == JsonValueKind.String ? (object)(valEl.GetString() ?? "") : valEl.ToString();
                return true;
        }
    }

    private static readonly IReadOnlyDictionary<string, (string ValCol, string TsCol)> LwwFieldColumnMap =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["dj_scene"] = ("dj_scene_json", "dj_scene_ts"),
            ["dj_scene_json"] = ("dj_scene_json", "dj_scene_ts"),
            ["dj_vocals"] = ("dj_vocals", "dj_vocals_ts"),
            ["dj_energy"] = ("dj_energy", "dj_energy_ts"),
            ["tempo_bpm"] = ("tempo_bpm", "tempo_ts"),
            ["tempo"] = ("tempo_bucket", "tempo_ts"),
            ["tempo_bucket"] = ("tempo_bucket", "tempo_ts"),
            ["mood"] = ("mood_json", "mood_ts"),
            ["mood_json"] = ("mood_json", "mood_ts"),
            ["rating"] = ("rating", "rating_ts"),
        };

    private static string SanitizeId(string id) =>
        string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));

    private static (string DisplayName, string FileName) NormalizePlaylistMergeName(string raw)
    {
        string display = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (display.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            display = display[..^5].Trim();
        else if (display.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
            display = display[..^4].Trim();

        if (string.IsNullOrWhiteSpace(display))
            return ("", "");

        string stem = Playlists.PlaylistStore.SanitizePlaylistFileName(display);
        return (display, stem + ".m3u8");
    }

    private static string ComputeSha256Hex(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static readonly Lazy<string> LazyDefaultScenesJson = new(() =>
    {
        var asm = typeof(SyncHostServer).Assembly;
        const string resourceName = "Tonewise.SyncHost.protocol.default_scenes_5d.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded scenes resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public static string DefaultScenesJson() => LazyDefaultScenesJson.Value;

    public static AutoEqPolicyDto DeserializeAutoEqPolicy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AutoEqPolicyDto();
        try
        {
            return JsonSerializer.Deserialize<AutoEqPolicyDto>(json) ?? new AutoEqPolicyDto();
        }
        catch
        {
            return new AutoEqPolicyDto();
        }
    }

    public static string SerializeAutoEqPolicy(AutoEqPolicyDto policy) =>
        JsonSerializer.Serialize(policy with { CustomRules = policy.CustomRules ?? [] });

    public AutoEqPolicyDto ReadAutoEqPolicy()
    {
        lock (AutoEqFileLock)
        {
            return ReadAutoEqPolicyUnlocked();
        }
    }

    public void WriteAutoEqPolicy(AutoEqPolicyDto policy)
    {
        lock (AutoEqFileLock)
        {
            WriteAutoEqPolicyUnlocked(policy);
        }
    }

    public (AutoEqPolicyDto Winner, bool Changed) MergeAutoEqPolicyWithChangeFlag(AutoEqPolicyDto incoming)
    {
        lock (AutoEqFileLock)
        {
            var local = ReadAutoEqPolicyUnlocked();
            bool incomingWins = incoming.UpdatedAtMs > local.UpdatedAtMs;
            if (!incomingWins)
            {
                return (local, false);
            }
            WriteAutoEqPolicyUnlocked(incoming);
            return (incoming, true);
        }
    }

    private AutoEqPolicyDto ReadAutoEqPolicyUnlocked()
    {
        string path = Path.Combine(_tonewiseDir, "auto_eq_policy.json");
        if (!File.Exists(path)) return new AutoEqPolicyDto();
        return DeserializeAutoEqPolicy(File.ReadAllText(path, Encoding.UTF8));
    }

    private void WriteAutoEqPolicyUnlocked(AutoEqPolicyDto policy)
    {
        Directory.CreateDirectory(_tonewiseDir);
        string path = Path.Combine(_tonewiseDir, "auto_eq_policy.json");
        string json = SerializeAutoEqPolicy(policy);
        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..6];
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

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
        lock (_manifestSnapshotLock)
        {
            if (_manifestSnapshot is { } current)
            {
                _retiredManifestSnapshots.Add(current.FilePath);
                _manifestSnapshot = null;
            }
            TryPruneRetiredSnapshotsUnlocked();
        }
    }
}
