using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tonewise.SyncHost.Playlists;

namespace Tonewise.SyncHost;

public sealed record PairedDeviceRecord(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("token_hash_sha256")] string TokenHashSha256,
    [property: JsonPropertyName("paired_at_ms")] long PairedAtMs,
    [property: JsonPropertyName("last_sync_ms")] long LastSyncMs
);

public sealed record SyncOutboxItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("target_key")] string TargetKey,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("track_uids")] IReadOnlyList<string> TrackUids,
    [property: JsonPropertyName("created_at_ms")] long CreatedAtMs,
    [property: JsonPropertyName("completed_device_ids")] IReadOnlyList<string>? CompletedDeviceIds = null
);

public sealed record ResolvedSyncTrack(
    [property: JsonPropertyName("track_uid")] string TrackUid,
    [property: JsonPropertyName("rel_path")] string RelPath,
    [property: JsonPropertyName("target_rel_path")] string TargetRelPath,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("album_artist")] string AlbumArtist,
    [property: JsonPropertyName("genre")] string Genre,
    [property: JsonPropertyName("duration_ms")] int DurationMs,
    [property: JsonPropertyName("file_size")] long FileSize,
    [property: JsonPropertyName("mtime_ms")] long MtimeMs,
    [property: JsonPropertyName("has_cover")] bool HasCover,
    [property: JsonPropertyName("source_outbox_ids")] IReadOnlyList<string> SourceOutboxIds,
    [property: JsonPropertyName("header_blake3")] string HeaderBlake3 = "",
    [property: JsonPropertyName("audio_frame_blake3")] string AudioFrameBlake3 = ""
);

public sealed record ResolvedSyncPlaylist(
    [property: JsonPropertyName("outbox_id")] string OutboxId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rel_paths")] IReadOnlyList<string> RelPaths,
    [property: JsonPropertyName("track_uids")] IReadOnlyList<string> TrackUids
);

public sealed record SyncPolicyConfig(
    [property: JsonPropertyName("mirror_prune_enabled")] bool MirrorPruneEnabled = false,
    [property: JsonPropertyName("prune_scope")] string PruneScope = "managed_only",
    [property: JsonPropertyName("target_root")] string TargetRoot = "Music",
    [property: JsonPropertyName("allow_adb_foreign_delete_fallback")] bool AllowAdbForeignDeleteFallback = false,
    [property: JsonPropertyName("large_prune_confirm_threshold_count")] int LargePruneConfirmThresholdCount = 20,
    [property: JsonPropertyName("large_prune_confirm_threshold_ratio")] double LargePruneConfirmThresholdRatio = 0.30
);

public sealed record ResolvedSyncPolicy(
    [property: JsonPropertyName("mirror_prune_enabled")] bool MirrorPruneEnabled,
    [property: JsonPropertyName("prune_scope")] string PruneScope,
    [property: JsonPropertyName("target_root")] string TargetRoot,
    [property: JsonPropertyName("outbox_authoritative")] bool OutboxAuthoritative,
    [property: JsonPropertyName("missing_source_count")] int MissingSourceCount,
    [property: JsonPropertyName("expected_playlist_names")] IReadOnlyList<string> ExpectedPlaylistNames,
    [property: JsonPropertyName("large_prune_confirm_threshold_count")] int LargePruneConfirmThresholdCount = 20,
    [property: JsonPropertyName("large_prune_confirm_threshold_ratio")] double LargePruneConfirmThresholdRatio = 0.30,
    [property: JsonPropertyName("prune_confirmed_for_session")] bool PruneConfirmedForSession = false
);

public sealed record ResolvedOutboxPayload(
    [property: JsonPropertyName("host_name")] string HostName,
    [property: JsonPropertyName("generated_at_ms")] long GeneratedAtMs,
    [property: JsonPropertyName("items")] IReadOnlyList<SyncOutboxItem> Items,
    [property: JsonPropertyName("tracks")] IReadOnlyList<ResolvedSyncTrack> Tracks,
    [property: JsonPropertyName("playlists")] IReadOnlyList<ResolvedSyncPlaylist> Playlists,
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("duplicate_track_count")] int DuplicateTrackCount = 0,
    [property: JsonPropertyName("sync_policy")] ResolvedSyncPolicy? SyncPolicy = null
);

public sealed record CatalogEntry(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("track_count")] int TrackCount,
    [property: JsonPropertyName("total_bytes")] long TotalBytes
);

public sealed record OutboxSummary(int ItemCount, int TrackCount, long TotalBytes, int DuplicateTrackCount = 0);

public enum PairAttemptStatus
{
    Success,
    InvalidCode,
    LockedOut
}

public sealed partial class SyncOutboxAndPairingStore
{
    private static readonly TimeSpan PairingCodeTtl = TimeSpan.FromMinutes(10);
    private const int MaxFailedPairAttempts = 5;
    private const int MaxGlobalFailedPairAttempts = 15;
    private static readonly TimeSpan PairLockoutDuration = TimeSpan.FromSeconds(60);

    private static readonly string[] CoverCandidateNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png"
    ];

    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    private readonly string _musicRoot;
    private readonly string _tonewiseDir;
    private readonly string _devicesPath;
    private readonly string _outboxPath;
    private readonly string _identityPath;
    private readonly string _syncPolicyPath;
    private readonly Lock _gate = new();
    private readonly Lock _resolveCacheLock = new();
    private readonly record struct ManifestFileStamp(bool Exists, long DbTicks, long DbLength, long WalTicks, long WalLength);
    private readonly record struct PlaylistFileStamp(long LastWriteTicks, long Length);
    private ManifestFileStamp _cachedManifestStamp;
    private List<ManifestTrackRow>? _cachedManifestRows;
    private PlaylistTrackMatcher.LibraryIndex? _cachedLibraryIndex;
    private readonly Dictionary<string, (PlaylistFileStamp Stamp, PlaylistDocument Doc)> _cachedPlaylists =
        new(StringComparer.OrdinalIgnoreCase);
    private long _outboxMutationVersion;
    private long _syncPolicyVersion;
    private string? _cachedHostId;
    private SyncPolicyConfig? _cachedSyncPolicy;
    private Dictionary<string, ManifestTrackRow>? _cachedRowsByUid;
    private sealed record CachedResolvedOutbox(
        ManifestFileStamp ManifestStamp,
        long OutboxFileTicks,
        long OutboxFileLength,
        long OutboxMutationVersion,
        long SyncPolicyVersion,
        string PlaylistStampsFingerprint,
        DateTime ComputedAtUtc,
        ResolvedOutboxPayload Payload);
    private readonly Dictionary<string, CachedResolvedOutbox> _cachedResolvedOutboxes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ResolvedOutboxCacheTtl = TimeSpan.FromSeconds(15);
    private string _pairingCode;
    private DateTimeOffset _pairingCodeIssuedAtUtc;
    private int _globalFailedPairAttempts;
    private readonly Dictionary<string, (int FailedAttempts, DateTimeOffset LockedUntilUtc, DateTimeOffset LastFailedUtc)> _pairLockoutsByClient =
        new(StringComparer.OrdinalIgnoreCase);
    private List<PairedDeviceRecord>? _cachedDevices;
    private long _cachedDevicesWriteTicks = -1;
    private long _cachedDevicesLength = -1;

    public SyncOutboxAndPairingStore(string musicRoot)
    {
        _musicRoot = musicRoot;
        _tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        _devicesPath = Path.Combine(_tonewiseDir, "paired_devices.json");
        _outboxPath = Path.Combine(_tonewiseDir, "sync_outbox.json");
        _identityPath = Path.Combine(_tonewiseDir, "host_identity.json");
        _syncPolicyPath = Path.Combine(_tonewiseDir, "sync_policy.json");
        _pairingCode = GenerateSixDigitCode();
        _pairingCodeIssuedAtUtc = DateTimeOffset.UtcNow;
    }

    public string HostId
    {
        get
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_cachedHostId))
                    return _cachedHostId;

                if (File.Exists(_identityPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(_identityPath, Encoding.UTF8));
                        if (doc.RootElement.TryGetProperty("host_id", out var idEl))
                        {
                            string? existing = idEl.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(existing))
                            {
                                _cachedHostId = existing;
                                return existing;
                            }
                        }
                    }
                    catch
                    {
                        // Regenerate corrupt host identity file
                    }
                }

                string generated = Guid.NewGuid().ToString("D");
                Directory.CreateDirectory(_tonewiseDir);
                string payload = JsonSerializer.Serialize(new { host_id = generated }, JsonWriteOptions);
                WriteTextAtomic(_identityPath, payload + "\n");
                _cachedHostId = generated;
                return generated;
            }
        }
    }

    public SyncPolicyConfig GetSyncPolicy()
    {
        lock (_gate)
        {
            if (_cachedSyncPolicy is not null)
                return _cachedSyncPolicy;

            if (File.Exists(_syncPolicyPath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<SyncPolicyConfig>(File.ReadAllText(_syncPolicyPath, Encoding.UTF8));
                    if (loaded is not null)
                    {
                        _cachedSyncPolicy = NormalizeSyncPolicy(loaded);
                        return _cachedSyncPolicy;
                    }
                }
                catch
                {
                    // Fall back to default policy if corrupt
                }
            }

            _cachedSyncPolicy = new SyncPolicyConfig();
            return _cachedSyncPolicy;
        }
    }

    public SyncPolicyConfig SetSyncPolicy(SyncPolicyConfig policy)
    {
        var normalized = NormalizeSyncPolicy(policy ?? new SyncPolicyConfig());
        lock (_gate)
        {
            Directory.CreateDirectory(_tonewiseDir);
            string json = JsonSerializer.Serialize(normalized, JsonWriteOptions);
            WriteTextAtomic(_syncPolicyPath, json + "\n");
            _cachedSyncPolicy = normalized;
            Interlocked.Increment(ref _syncPolicyVersion);
            return normalized;
        }
    }

    private static SyncPolicyConfig NormalizeSyncPolicy(SyncPolicyConfig raw)
    {
        string scope = string.Equals(raw.PruneScope?.Trim(), "managed_only", StringComparison.OrdinalIgnoreCase)
            ? "managed_only"
            : "music_folder_all";
        int thresholdCount = Math.Max(1, raw.LargePruneConfirmThresholdCount);
        double thresholdRatio = double.IsFinite(raw.LargePruneConfirmThresholdRatio)
            ? Math.Clamp(raw.LargePruneConfirmThresholdRatio, 0.05, 1.0)
            : 0.30;
        return raw with
        {
            PruneScope = scope,
            TargetRoot = "Music",
            LargePruneConfirmThresholdCount = thresholdCount,
            LargePruneConfirmThresholdRatio = thresholdRatio,
        };
    }

    public string CurrentPairingCode
    {
        get
        {
            lock (_gate)
            {
                RotatePairingCodeIfExpiredUnlocked(DateTimeOffset.UtcNow);
                return _pairingCode;
            }
        }
    }

    public string RegeneratePairingCode()
    {
        lock (_gate)
        {
            _pairingCode = GenerateSixDigitCode();
            _pairingCodeIssuedAtUtc = DateTimeOffset.UtcNow;
            _globalFailedPairAttempts = 0;
            _pairLockoutsByClient.Clear();
            return _pairingCode;
        }
    }

    public bool TryPair(
        string? code,
        string? deviceId,
        string? deviceName,
        out string? issuedToken,
        out PairedDeviceRecord? device,
        string? clientKey = null) =>
        TryPairWithStatus(code, deviceId, deviceName, out issuedToken, out device, out _, clientKey) == PairAttemptStatus.Success;

    public PairAttemptStatus TryPairWithStatus(
        string? code,
        string? deviceId,
        string? deviceName,
        out string? issuedToken,
        out PairedDeviceRecord? device,
        out int retryAfterSeconds,
        string? clientKey = null)
    {
        issuedToken = null;
        device = null;
        retryAfterSeconds = 0;

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(deviceId))
            return PairAttemptStatus.InvalidCode;

        string submitted = code.Trim();
        string cleanDeviceId = SanitizeDeviceId(deviceId);
        if (string.IsNullOrEmpty(cleanDeviceId)) return PairAttemptStatus.InvalidCode;
        string cleanName = string.IsNullOrWhiteSpace(deviceName) ? cleanDeviceId : deviceName.Trim();
        string bucketKey = string.IsNullOrWhiteSpace(clientKey) ? "global" : clientKey.Trim();

        lock (_gate)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            if (_pairLockoutsByClient.Count > 256)
            {
                var staleKeys = _pairLockoutsByClient
                    .Where(kv =>
                        (kv.Value.LockedUntilUtc != DateTimeOffset.MinValue && kv.Value.LockedUntilUtc <= nowUtc)
                        || (kv.Value.LockedUntilUtc == DateTimeOffset.MinValue && kv.Value.LastFailedUtc <= nowUtc - PairLockoutDuration))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (string k in staleKeys) _pairLockoutsByClient.Remove(k);

                if (_pairLockoutsByClient.Count > 256)
                {
                    int overflow = _pairLockoutsByClient.Count - 256;
                    var oldestKeys = _pairLockoutsByClient
                        .OrderBy(kv => kv.Value.LastFailedUtc)
                        .Take(overflow)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (string k in oldestKeys) _pairLockoutsByClient.Remove(k);
                }
            }

            if (_pairLockoutsByClient.TryGetValue(bucketKey, out var state) && nowUtc < state.LockedUntilUtc)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((state.LockedUntilUtc - nowUtc).TotalSeconds));
                return PairAttemptStatus.LockedOut;
            }

            RotatePairingCodeIfExpiredUnlocked(nowUtc);

            if (!ConstantTimeEqualsUtf8(submitted, _pairingCode))
            {
                _globalFailedPairAttempts++;
                if (_globalFailedPairAttempts >= MaxGlobalFailedPairAttempts)
                {
                    _pairingCode = GenerateSixDigitCode();
                    _pairingCodeIssuedAtUtc = nowUtc;
                    _globalFailedPairAttempts = 0;
                    // Rotate global code on brute-force threshold, but preserve all existing IP lockouts
                    // and lock out the triggering client instead of clearing _pairLockoutsByClient.
                    DateTimeOffset lockedUntilGlobal = nowUtc + PairLockoutDuration;
                    _pairLockoutsByClient[bucketKey] = (0, lockedUntilGlobal, nowUtc);
                    retryAfterSeconds = (int)PairLockoutDuration.TotalSeconds;
                    return PairAttemptStatus.LockedOut;
                }

                int failed = (state.LockedUntilUtc <= nowUtc ? state.FailedAttempts : 0) + 1;
                if (failed >= MaxFailedPairAttempts)
                {
                    DateTimeOffset lockedUntil = nowUtc + PairLockoutDuration;
                    _pairLockoutsByClient[bucketKey] = (0, lockedUntil, nowUtc);
                    retryAfterSeconds = (int)PairLockoutDuration.TotalSeconds;
                    return PairAttemptStatus.LockedOut;
                }
                _pairLockoutsByClient[bucketKey] = (failed, DateTimeOffset.MinValue, nowUtc);
                return PairAttemptStatus.InvalidCode;
            }

            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            string tokenHash = ComputeTokenHashHex(token);
            long now = nowUtc.ToUnixTimeMilliseconds();

            var devices = LoadDevicesUnlocked();
            devices.RemoveAll(d => string.Equals(d.DeviceId, cleanDeviceId, StringComparison.OrdinalIgnoreCase));
            var record = new PairedDeviceRecord(cleanDeviceId, cleanName, tokenHash, now, 0);
            devices.Add(record);
            SaveDevicesUnlocked(devices);

            // Rotate pairing code after successful pairing to prevent stale code reuse
            _pairingCode = GenerateSixDigitCode();
            _pairingCodeIssuedAtUtc = nowUtc;
            _globalFailedPairAttempts = 0;
            _pairLockoutsByClient.Remove(bucketKey);

            issuedToken = token;
            device = record;
            return PairAttemptStatus.Success;
        }
    }

    public PairedDeviceRecord PairDeviceDirect(
        string deviceId,
        string? deviceName,
        out string issuedToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device ID is required.", nameof(deviceId));
        string cleanDeviceId = SanitizeDeviceId(deviceId);
        if (string.IsNullOrEmpty(cleanDeviceId))
            throw new ArgumentException("Device ID is invalid.", nameof(deviceId));
        string cleanName = string.IsNullOrWhiteSpace(deviceName) ? cleanDeviceId : deviceName.Trim();

        lock (_gate)
        {
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            string tokenHash = ComputeTokenHashHex(token);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var devices = LoadDevicesUnlocked();
            devices.RemoveAll(d => string.Equals(d.DeviceId, cleanDeviceId, StringComparison.OrdinalIgnoreCase));
            var record = new PairedDeviceRecord(cleanDeviceId, cleanName, tokenHash, now, 0);
            devices.Add(record);
            SaveDevicesUnlocked(devices);

            issuedToken = token;
            return record;
        }
    }

    private void RotatePairingCodeIfExpiredUnlocked(DateTimeOffset nowUtc)
    {
        if (nowUtc - _pairingCodeIssuedAtUtc >= PairingCodeTtl)
        {
            _pairingCode = GenerateSixDigitCode();
            _pairingCodeIssuedAtUtc = nowUtc;
            _globalFailedPairAttempts = 0;
            foreach (var kv in _pairLockoutsByClient.ToList())
            {
                bool lockoutExpired =
                    (kv.Value.LockedUntilUtc != DateTimeOffset.MinValue && kv.Value.LockedUntilUtc <= nowUtc)
                    || (kv.Value.LockedUntilUtc == DateTimeOffset.MinValue && nowUtc - kv.Value.LastFailedUtc >= PairLockoutDuration);
                if (lockoutExpired)
                {
                    _pairLockoutsByClient.Remove(kv.Key);
                }
            }
        }
    }

    public bool ValidateBearerToken(string? authorizationHeader, out PairedDeviceRecord? device)
    {
        device = null;
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;

        string trimmed = authorizationHeader.Trim();
        if (!trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        string token = trimmed["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token)) return false;

        string candidateHash = ComputeTokenHashHex(token);
        lock (_gate)
        {
            var devices = LoadDevicesUnlocked();
            foreach (var d in devices)
            {
                if (ConstantTimeEqualsUtf8(candidateHash, d.TokenHashSha256))
                {
                    device = d;
                    return true;
                }
            }
        }
        return false;
    }

    public IReadOnlyList<PairedDeviceRecord> GetPairedDevices()
    {
        lock (_gate)
        {
            return LoadDevicesUnlocked();
        }
    }

    public void TouchDeviceSync(string deviceId, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        lock (_gate)
        {
            var devices = LoadDevicesUnlocked();
            int idx = devices.FindIndex(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                devices[idx] = devices[idx] with { LastSyncMs = nowMs };
                SaveDevicesUnlocked(devices);
            }
        }
    }

    public bool UnpairDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        lock (_gate)
        {
            var devices = LoadDevicesUnlocked();
            int removed = devices.RemoveAll(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveDevicesUnlocked(devices);
            }

            var items = LoadOutboxUnlocked();
            int removedOutbox = items.RemoveAll(i => string.Equals(i.DeviceId, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removedOutbox > 0)
            {
                SaveOutboxUnlocked(items);
            }

            return removed > 0 || removedOutbox > 0;
        }
    }

    public IReadOnlyList<SyncOutboxItem> GetOutboxItems(string? deviceId = null)
    {
        lock (_gate)
        {
            return FilterOutboxForDevice(LoadOutboxUnlocked(), deviceId);
        }
    }

    public SyncOutboxItem EnqueuePlaylist(string playlistName, string deviceId = "*", string mode = "subscribe") =>
        UpsertEntityOutboxItem("playlist", playlistName, $"歌单：{playlistName}", NormalizeMode(mode, "subscribe"), deviceId);

    public IReadOnlyList<SyncOutboxItem> EnqueuePlaylists(
        IEnumerable<string> playlistNames,
        string deviceId = "*",
        string mode = "subscribe")
    {
        var distinctNames = playlistNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (distinctNames.Count == 0) return [];

        string cleanDevice = string.IsNullOrWhiteSpace(deviceId) ? "*" : deviceId.Trim();
        string resolvedMode = NormalizeMode(mode, "subscribe");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            var upserted = new List<SyncOutboxItem>(distinctNames.Count);
            foreach (string name in distinctNames)
            {
                upserted.Add(UpsertEntityOutboxItemUnlocked(
                    items, "playlist", name, $"歌单：{name}", resolvedMode, cleanDevice, now));
            }
            SaveOutboxUnlocked(items);
            return upserted;
        }
    }

    public SyncOutboxItem EnqueueGenre(string genre, string deviceId = "*", string mode = "subscribe") =>
        UpsertEntityOutboxItem("genre", genre, $"流派：{genre}", NormalizeMode(mode, "subscribe"), deviceId);

    public SyncOutboxItem EnqueueArtist(string artist, string deviceId = "*", string mode = "subscribe") =>
        UpsertEntityOutboxItem("artist", artist, $"艺人：{artist}", NormalizeMode(mode, "subscribe"), deviceId);

    public SyncOutboxItem EnqueueTracks(
        IReadOnlyList<string> trackUids,
        string displayName,
        string deviceId = "*",
        string mode = "copy")
    {
        var distinctUids = trackUids
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string normalizedDevice = string.IsNullOrWhiteSpace(deviceId) ? "*" : deviceId.Trim();
        string resolvedMode = NormalizeMode(mode, "copy");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            var entry = new SyncOutboxItem(
                Id: "ob_" + Guid.NewGuid().ToString("N")[..12],
                DeviceId: normalizedDevice,
                Kind: "tracks",
                TargetKey: distinctUids.Count == 1 ? distinctUids[0] : $"batch:{distinctUids.Count}",
                DisplayName: string.IsNullOrWhiteSpace(displayName) ? $"自选 {distinctUids.Count} 首曲目" : displayName.Trim(),
                Mode: resolvedMode,
                TrackUids: distinctUids,
                CreatedAtMs: now
            );
            items.Add(entry);
            SaveOutboxUnlocked(items);
            return entry;
        }
    }

    public bool RemoveOutboxItem(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            int removed = items.RemoveAll(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveOutboxUnlocked(items);
                return true;
            }
            return false;
        }
    }

    public void ClearOutbox()
    {
        lock (_gate)
        {
            SaveOutboxUnlocked([]);
        }
    }

    public int PromoteCopyItemsToSubscribe()
    {
        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            int converted = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].Mode, "copy", StringComparison.OrdinalIgnoreCase))
                {
                    items[i] = items[i] with { Mode = "subscribe" };
                    converted++;
                }
            }
            if (converted > 0)
            {
                SaveOutboxUnlocked(items);
            }
            return converted;
        }
    }

    public int MarkCopyItemsCompleted(string? deviceId, IEnumerable<string> completedOutboxIds)
    {
        var ids = completedOutboxIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids is null || ids.Count == 0) return 0;

        string? cleanDevice = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();

        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            var pairedDevices = LoadDevicesUnlocked();
            bool changed = false;
            int completedCount = 0;

            for (int idx = items.Count - 1; idx >= 0; idx--)
            {
                var item = items[idx];
                if (!ids.Contains(item.Id) || !string.Equals(item.Mode, "copy", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool targetMatches =
                    string.Equals(item.DeviceId, "*", StringComparison.OrdinalIgnoreCase)
                    || cleanDevice is null
                    || string.Equals(item.DeviceId, cleanDevice, StringComparison.OrdinalIgnoreCase);
                if (!targetMatches) continue;

                if (!string.Equals(item.DeviceId, "*", StringComparison.OrdinalIgnoreCase)
                    || cleanDevice is null
                    || pairedDevices.Count <= 1)
                {
                    items.RemoveAt(idx);
                    changed = true;
                    completedCount++;
                }
                else
                {
                    var completedSet = (item.CompletedDeviceIds ?? [])
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .Select(d => d.Trim())
                        .ToList();
                    if (!completedSet.Contains(cleanDevice, StringComparer.OrdinalIgnoreCase))
                    {
                        completedSet.Add(cleanDevice);
                        changed = true;
                    }

                    bool allPairedDone = pairedDevices.All(p =>
                        completedSet.Contains(p.DeviceId, StringComparer.OrdinalIgnoreCase));
                    if (allPairedDone)
                    {
                        items.RemoveAt(idx);
                        changed = true;
                    }
                    else
                    {
                        items[idx] = item with { CompletedDeviceIds = completedSet };
                    }
                    completedCount++;
                }
            }

            if (changed)
            {
                SaveOutboxUnlocked(items);
            }
            return completedCount;
        }
    }

    private SyncOutboxItem UpsertEntityOutboxItem(
        string kind,
        string targetKey,
        string displayName,
        string mode,
        string deviceId)
    {
        string cleanDevice = string.IsNullOrWhiteSpace(deviceId) ? "*" : deviceId.Trim();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var items = LoadOutboxUnlocked();
            var updated = UpsertEntityOutboxItemUnlocked(items, kind, targetKey, displayName, mode, cleanDevice, now);
            SaveOutboxUnlocked(items);
            return updated;
        }
    }

    private static SyncOutboxItem UpsertEntityOutboxItemUnlocked(
        List<SyncOutboxItem> items,
        string kind,
        string targetKey,
        string displayName,
        string mode,
        string cleanDevice,
        long now)
    {
        string cleanKey = string.IsNullOrWhiteSpace(targetKey) ? "" : targetKey.Trim();
        int existingIdx = items.FindIndex(i =>
            string.Equals(i.DeviceId, cleanDevice, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.TargetKey, cleanKey, StringComparison.OrdinalIgnoreCase));

        var updated = new SyncOutboxItem(
            Id: existingIdx >= 0 ? items[existingIdx].Id : "ob_" + Guid.NewGuid().ToString("N")[..12],
            DeviceId: cleanDevice,
            Kind: kind,
            TargetKey: cleanKey,
            DisplayName: displayName.Trim(),
            Mode: mode,
            TrackUids: [],
            CreatedAtMs: now
        );

        if (existingIdx >= 0) items[existingIdx] = updated;
        else items.Add(updated);
        return updated;
    }

    private static List<SyncOutboxItem> FilterOutboxForDevice(List<SyncOutboxItem> all, string? deviceId) =>
        all.Where(i =>
            (string.Equals(i.DeviceId, "*", StringComparison.OrdinalIgnoreCase)
             || string.IsNullOrWhiteSpace(deviceId)
             || string.Equals(i.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(deviceId)
                || i.CompletedDeviceIds is null
                || !i.CompletedDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(i => i.CreatedAtMs)
            .ToList();

    private List<PairedDeviceRecord> LoadDevicesUnlocked()
    {
        if (!File.Exists(_devicesPath))
        {
            _cachedDevices = null;
            _cachedDevicesWriteTicks = -1;
            _cachedDevicesLength = -1;
            return [];
        }
        try
        {
            var fi = new FileInfo(_devicesPath);
            long writeTicks = fi.LastWriteTimeUtc.Ticks;
            long len = fi.Length;
            if (_cachedDevices is not null
                && _cachedDevicesWriteTicks == writeTicks
                && _cachedDevicesLength == len)
            {
                return new List<PairedDeviceRecord>(_cachedDevices);
            }
            var parsed = JsonSerializer.Deserialize<List<PairedDeviceRecord>>(File.ReadAllText(_devicesPath, Encoding.UTF8)) ?? [];
            _cachedDevices = parsed;
            _cachedDevicesWriteTicks = writeTicks;
            _cachedDevicesLength = len;
            return new List<PairedDeviceRecord>(parsed);
        }
        catch
        {
            return [];
        }
    }

    private void SaveDevicesUnlocked(List<PairedDeviceRecord> devices)
    {
        Directory.CreateDirectory(_tonewiseDir);
        string json = JsonSerializer.Serialize(devices, JsonWriteOptions);
        WriteTextAtomic(_devicesPath, json + "\n");
        try
        {
            var fi = new FileInfo(_devicesPath);
            _cachedDevices = new List<PairedDeviceRecord>(devices);
            _cachedDevicesWriteTicks = fi.LastWriteTimeUtc.Ticks;
            _cachedDevicesLength = fi.Length;
        }
        catch
        {
            _cachedDevices = null;
            _cachedDevicesWriteTicks = -1;
            _cachedDevicesLength = -1;
        }
    }

    private List<SyncOutboxItem> LoadOutboxUnlocked()
    {
        if (!File.Exists(_outboxPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SyncOutboxItem>>(File.ReadAllText(_outboxPath, Encoding.UTF8)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveOutboxUnlocked(List<SyncOutboxItem> items)
    {
        Directory.CreateDirectory(_tonewiseDir);
        string json = JsonSerializer.Serialize(items, JsonWriteOptions);
        WriteTextAtomic(_outboxPath, json + "\n");
        Interlocked.Increment(ref _outboxMutationVersion);
    }

    private static void WriteTextAtomic(string path, string content)
    {
        string tmpPath = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmpPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private static string NormalizeMode(string mode, string defaultMode)
    {
        string clean = mode?.Trim().ToLowerInvariant() ?? "";
        return clean is "subscribe" or "copy" ? clean : defaultMode;
    }

    private static string GenerateSixDigitCode() =>
        RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString("D6");

    private static string ComputeTokenHashHex(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool ConstantTimeEqualsUtf8(string a, string b)
    {
        byte[] ba = Encoding.UTF8.GetBytes(a);
        byte[] bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string SanitizeDeviceId(string id)
    {
        string cleaned = string.Concat(id.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
