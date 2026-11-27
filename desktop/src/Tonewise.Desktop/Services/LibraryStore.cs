using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Desktop.Models;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;

namespace Tonewise.Desktop.Services;

public sealed class LibraryStore
{
    private static readonly string[] AudioExtensions =
        [".flac", ".mp3", ".wav", ".m4a", ".mp4", ".alac", ".aac", ".ogg", ".opus", ".dsf", ".dff", ".aiff", ".aif"];

    public string? MusicRoot { get; private set; }
    public string? WaveformsBinPath { get; private set; }
    public string? ManifestDbPath { get; private set; }
    public string? EncyclopediaDbPath { get; private set; }
    public string? SessionsJsonPath { get; private set; }

    private StagingDiffStore? _cachedStagingDiffStore;
    private string? _cachedStagingManifestPath;
    private string? _cachedStagingMusicRoot;

    public StagingDiffStore? GetStagingDiffStore()
    {
        if (string.IsNullOrWhiteSpace(ManifestDbPath) || !File.Exists(ManifestDbPath))
        {
            return null;
        }
        if (_cachedStagingDiffStore is not null
            && string.Equals(_cachedStagingManifestPath, ManifestDbPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_cachedStagingMusicRoot, MusicRoot, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedStagingDiffStore;
        }
        _cachedStagingManifestPath = ManifestDbPath;
        _cachedStagingMusicRoot = MusicRoot;
        _cachedStagingDiffStore = new StagingDiffStore(ManifestDbPath, MusicRoot, ensureSchemaImmediately: false);
        return _cachedStagingDiffStore;
    }

    public EncyclopediaService? GetEncyclopediaService() =>
        !string.IsNullOrWhiteSpace(EncyclopediaDbPath)
            ? new EncyclopediaService(EncyclopediaDbPath)
            : null;

    public bool ScanAndBuildManifestIfMissing(string musicRoot, bool forceRebuild = false, bool fastMode = true)
    {
        if (string.IsNullOrWhiteSpace(musicRoot) || !LongPathHelper.DirectoryExists(musicRoot))
            return false;

        string tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        if (!forceRebuild && HasPopulatedManifest(dbPath))
            return false;

        if (forceRebuild && HasPopulatedManifest(dbPath))
        {
            BuildManifestWithFfiFallback(musicRoot, dbPath, fastMode);
            return HasPopulatedManifest(dbPath);
        }

        string? cliExe = FindTonewiseCliExecutable();
        if (cliExe is not null)
        {
            using var proc = StartScanCliProcess(cliExe, musicRoot, tonewiseDir, fastMode);
            if (proc is not null)
            {
                if (!proc.WaitForExit(300_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                else if (proc.HasExited && proc.ExitCode == 0 && HasPopulatedManifest(dbPath))
                {
                    return true;
                }
            }
        }

        // Fallback: in-process Rust FFI scan + minimal SQLite manifest build
        Directory.CreateDirectory(tonewiseDir);
        BuildManifestWithFfiFallback(musicRoot, dbPath, fastMode);
        return HasPopulatedManifest(dbPath);
    }

    public async Task<bool> ScanAndBuildManifestIfMissingAsync(
        string musicRoot,
        bool forceRebuild = false,
        bool fastMode = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(musicRoot) || !LongPathHelper.DirectoryExists(musicRoot))
            return false;

        string tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        if (!forceRebuild && HasPopulatedManifest(dbPath))
            return false;

        if (forceRebuild && HasPopulatedManifest(dbPath))
        {
            await Task.Run(() => BuildManifestWithFfiFallback(musicRoot, dbPath, fastMode), ct).ConfigureAwait(false);
            return HasPopulatedManifest(dbPath);
        }

        string? cliExe = FindTonewiseCliExecutable();
        if (cliExe is not null)
        {
            using var proc = StartScanCliProcess(cliExe, musicRoot, tonewiseDir, fastMode);
            if (proc is not null)
            {
                try
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    if (proc.HasExited && proc.ExitCode == 0 && HasPopulatedManifest(dbPath))
                    {
                        return true;
                    }
                }
                finally
                {
                    if (!proc.HasExited)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            Directory.CreateDirectory(tonewiseDir);
            BuildManifestWithFfiFallback(musicRoot, dbPath, fastMode);
        }, ct).ConfigureAwait(false);
        return HasPopulatedManifest(dbPath);
    }

    private static Process? StartScanCliProcess(string cliExe, string musicRoot, string tonewiseDir, bool fastMode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliExe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("scan");
        psi.ArgumentList.Add(musicRoot);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(tonewiseDir);
        if (fastMode)
        {
            psi.ArgumentList.Add("--fast");
        }
        return Process.Start(psi);
    }

    private static bool HasPopulatedManifest(string dbPath)
    {
        if (!File.Exists(dbPath))
            return false;

        try
        {
            using var conn = TonewiseSqlite.OpenReadOnly(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM tracks LIMIT 1);";
            object? res = cmd.ExecuteScalar();
            return res is not null && res != DBNull.Value && Convert.ToInt64(res) > 0;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<TrackItem> LoadFromMusicRoot(string musicRoot)
    {
        MusicRoot = musicRoot;
        ScanAndBuildManifestIfMissing(musicRoot, forceRebuild: false, fastMode: true);
        string tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        string wfPath = Path.Combine(tonewiseDir, "waveforms.bin");
        WaveformsBinPath = File.Exists(wfPath) ? wfPath : null;
        ManifestDbPath = File.Exists(dbPath) ? dbPath : null;
        EncyclopediaDbPath = Path.Combine(tonewiseDir, "encyclopedia.db");
        SessionsJsonPath = Path.Combine(tonewiseDir, "sessions.json");

        if (!File.Exists(dbPath))
        {
            return [];
        }

        var albumAssetsCache = new Dictionary<string, (string? Cover, string? Booklet)>(StringComparer.OrdinalIgnoreCase);
        using var conn = TonewiseSqlite.OpenReadOnly(dbPath);

        var cols = ReadExistingColumns(conn);
        string selectSql = BuildTrackSelectSql(cols, "ORDER BY album, rel_path");
        return ReadTracksFromSql(conn, musicRoot, selectSql, albumAssetsCache);
    }

    public IReadOnlyDictionary<string, TrackItem> LoadTracksByUids(string musicRoot, IReadOnlyCollection<string> trackUids)
    {
        var result = new Dictionary<string, TrackItem>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(musicRoot) || trackUids.Count == 0)
            return result;

        string dbPath = Path.Combine(musicRoot, ".tonewise", "manifest.db");
        if (!File.Exists(dbPath))
            return result;

        var albumAssetsCache = new Dictionary<string, (string? Cover, string? Booklet)>(StringComparer.OrdinalIgnoreCase);
        using var conn = TonewiseSqlite.OpenReadOnly(dbPath);
        var cols = ReadExistingColumns(conn);

        var distinctUids = trackUids.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToList();
        const int batchSize = 500;
        for (int offset = 0; offset < distinctUids.Count; offset += batchSize)
        {
            var chunk = distinctUids.Skip(offset).Take(batchSize).ToList();
            var paramNames = Enumerable.Range(0, chunk.Count).Select(i => $"$u{i}").ToArray();
            string sql = BuildTrackSelectSql(cols, $"WHERE track_uid IN ({string.Join(", ", paramNames)})");
            var loaded = ReadTracksFromSql(conn, musicRoot, sql, albumAssetsCache, cmd =>
            {
                for (int i = 0; i < chunk.Count; i++)
                {
                    cmd.Parameters.AddWithValue(paramNames[i], chunk[i]);
                }
            });
            foreach (var item in loaded)
            {
                result[item.TrackUid] = item;
            }
        }

        return result;
    }

    private static string BuildTrackSelectSql(HashSet<string> cols, string tailClause)
    {
        bool hasPublisher = cols.Contains("publisher");
        bool hasPlayCountTotal = cols.Contains("play_count_total");
        bool hasMovementNumber = cols.Contains("movement_number");
        bool hasReplayGainAlbum = cols.Contains("replaygain_album_db");
        bool hasDiscNumber = cols.Contains("disc_number");
        bool hasTrackNumber = cols.Contains("track_number");
        bool hasAlbumArtist = cols.Contains("album_artist");
        bool hasMtimeMs = cols.Contains("mtime_ms");
        bool hasLedgerCols = cols.Contains("legacy_play_count")
                             && cols.Contains("lastfm_pulled_count")
                             && cols.Contains("local_unsynced_expired_count");

        return $"""
            SELECT
                track_uid, audio_frame_blake3, rel_path, title, album,
                artists_json, composers_json, conductors_json, work_name, movement_name,
                genre, year, sample_rate_hz, duration_ms, replaygain_track_db,
                dj_scene_json, dj_vocals, dj_energy, tempo_bpm, tempo_bucket, mood_json,
                rating, last_played_ts,
                {(hasPublisher ? "publisher" : "NULL")} AS publisher,
                {(hasPlayCountTotal ? "play_count_total" : "0")} AS play_count_total,
                {(hasLedgerCols ? "legacy_play_count" : "0")} AS legacy_play_count,
                {(hasLedgerCols ? "lastfm_pulled_count" : "0")} AS lastfm_pulled_count,
                {(hasLedgerCols ? "local_unsynced_expired_count" : "0")} AS local_unsynced_expired_count,
                {(hasMovementNumber ? "movement_number" : "NULL")} AS movement_number,
                {(hasReplayGainAlbum ? "replaygain_album_db" : "NULL")} AS replaygain_album_db,
                {(hasDiscNumber ? "disc_number" : "0")} AS disc_number,
                {(hasTrackNumber ? "track_number" : "0")} AS track_number,
                {(hasAlbumArtist ? "album_artist" : "NULL")} AS album_artist,
                {(hasMtimeMs ? "mtime_ms" : "0")} AS mtime_ms
            FROM tracks
            {tailClause}
            """;
    }

    private static List<TrackItem> ReadTracksFromSql(
        SqliteConnection conn,
        string musicRoot,
        string selectSql,
        Dictionary<string, (string? Cover, string? Booklet)> albumAssetsCache,
        Action<SqliteCommand>? bindParameters = null)
    {
        var items = new List<TrackItem>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = selectSql;
        bindParameters?.Invoke(cmd);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            byte[] blake3Bytes = (byte[])reader["audio_frame_blake3"];
            string hex = Convert.ToHexStringLower(blake3Bytes);
            string relPath = reader.GetString(2);
            string fullPath = Path.Combine(musicRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            string albumDir = Path.GetDirectoryName(fullPath) ?? musicRoot;

            if (!albumAssetsCache.TryGetValue(albumDir, out var assets))
            {
                assets = DiscoverAlbumAssets(albumDir, musicRoot);
                albumAssetsCache[albumDir] = assets;
            }

            var artists = ParseStringArray(reader.GetString(5));
            var composers = ParseStringArray(reader.GetString(6));
            var conductors = ParseStringArray(reader.GetString(7));
            string workName = reader.IsDBNull(8) ? "" : reader.GetString(8);
            string movement = reader.IsDBNull(9) ? "" : reader.GetString(9);
            string workDisplay = string.IsNullOrEmpty(workName)
                ? ""
                : (string.IsNullOrEmpty(movement) ? workName : $"{workName} — {movement}");

            string publisher = reader.IsDBNull(23) ? "" : reader.GetString(23);
            int total = reader.IsDBNull(24) ? 0 : reader.GetInt32(24);
            int legacy = reader.IsDBNull(25) ? 0 : reader.GetInt32(25);
            int pulled = reader.IsDBNull(26) ? 0 : reader.GetInt32(26);
            int expired = reader.IsDBNull(27) ? 0 : reader.GetInt32(27);
            int effectiveCount = Math.Max(total, LastFmLedgerService.ComputeEffectivePlayCount(legacy, pulled, expired));
            int? movementNumber = reader.IsDBNull(28) ? null : reader.GetInt32(28);
            double? rgAlbumDb = reader.IsDBNull(29) ? null : reader.GetDouble(29);
            int dbDisc = reader.IsDBNull(30) ? 0 : reader.GetInt32(30);
            int dbTrack = reader.IsDBNull(31) ? 0 : reader.GetInt32(31);
            string albumArtist = reader.IsDBNull(32) ? "" : reader.GetString(32);
            long mtimeMs = reader.IsDBNull(33) ? 0 : reader.GetInt64(33);
            var (pathDisc, pathTrack) = ParseDiscAndTrackFromRelPath(relPath);
            int discNumber = dbDisc > 0 ? dbDisc : pathDisc;
            int trackNumber = dbTrack > 0 ? dbTrack : pathTrack;

            items.Add(
                new TrackItem(
                    TrackUid: reader.GetString(0),
                    AudioFrameBlake3Hex: hex,
                    RelPath: relPath,
                    FullPath: fullPath,
                    Title: reader.GetString(3),
                    Album: reader.IsDBNull(4) ? "" : reader.GetString(4),
                    ArtistsDisplay: string.Join(", ", artists),
                    ComposersDisplay: string.Join(", ", composers),
                    ConductorsDisplay: string.Join(", ", conductors),
                    WorkDisplay: workDisplay,
                    Genre: reader.IsDBNull(10) ? "" : reader.GetString(10),
                    Year: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    SampleRateHz: reader.GetInt32(12),
                    DurationMs: reader.GetInt32(13),
                    ReplayGainTrackDb: reader.IsDBNull(14) ? null : reader.GetDouble(14),
                    DjScene: ParseStringArray(reader.GetString(15)),
                    DjVocals: reader.IsDBNull(16) ? null : reader.GetString(16),
                    DjEnergy: reader.IsDBNull(17) ? null : reader.GetString(17),
                    TempoBpm: reader.IsDBNull(18) ? null : reader.GetDouble(18),
                    TempoBucket: reader.IsDBNull(19) ? null : reader.GetString(19),
                    Moods: ParseStringArray(reader.GetString(20)),
                    Rating: reader.IsDBNull(21) ? 0 : reader.GetInt32(21),
                    LastPlayedTsMs: reader.IsDBNull(22) ? 0 : reader.GetInt64(22),
                    WorkName: workName,
                    MovementName: movement,
                    Publisher: publisher,
                    EffectivePlayCount: effectiveCount,
                    AlbumDirectory: albumDir,
                    CoverArtPath: assets.Cover,
                    BookletPdfPath: assets.Booklet,
                    MovementNumber: movementNumber,
                    ReplayGainAlbumDb: rgAlbumDb,
                    DiscNumber: discNumber,
                    TrackNumber: trackNumber,
                    AlbumArtist: albumArtist,
                    MtimeMs: mtimeMs
                )
            );
        }

        return items;
    }

    public int ScanMissingMetadataAndEnqueue(IReadOnlyList<TrackItem> tracks)
    {
        var store = GetStagingDiffStore();
        if (store is null) return 0;

        var proposals = new List<TagDiffProposal>();
        int realAnalyzed = 0;
        const int maxRealAnalysisPerBatch = 64;

        foreach (var t in tracks)
        {
            var snap = new TrackCurationSnapshot(
                TrackUid: t.TrackUid,
                Album: string.IsNullOrWhiteSpace(t.Album) ? null : t.Album,
                Year: t.Year,
                Publisher: string.IsNullOrWhiteSpace(t.Publisher) ? null : t.Publisher,
                Genre: string.IsNullOrWhiteSpace(t.Genre) ? null : t.Genre,
                DjVocals: t.DjVocals,
                DjEnergy: t.DjEnergy,
                TempoBpm: t.TempoBpm,
                TempoBucket: t.TempoBucket,
                MoodsJson: t.Moods.Count > 0 ? JsonSerializer.Serialize(t.Moods) : "[]",
                DjSceneJson: t.DjScene.Count > 0 ? JsonSerializer.Serialize(t.DjScene) : "[]"
            );

            bool needsAcoustic = !t.TempoBpm.HasValue || t.DjScene.Count == 0 || string.IsNullOrWhiteSpace(t.DjVocals);
            if (needsAcoustic && realAnalyzed < maxRealAnalysisPerBatch && File.Exists(t.FullPath))
            {
                realAnalyzed++;
                try
                {
                    var inferred = Acoustic5DInferenceService.AnalyzeFileAndInfer5D(t.TrackUid, t.FullPath, t.IsClassical);
                    proposals.AddRange(Acoustic5DInferenceService.GenerateMissing5DProposalsFromResult(snap, inferred));
                    continue;
                }
                catch
                {
                    // Fall back to metadata prior vector if decoder fails
                }
            }

            // Fallback baseline priors when audio file is absent or beyond batch cap (capped below auto-approve threshold)
            double? instProb = t.IsClassical ? 0.90 : null;
            double? bpm = t.TempoBpm;
            double? bpmConf = bpm.HasValue ? 0.80 : null;
            double arousal = t.DjEnergy?.Equals("high", StringComparison.OrdinalIgnoreCase) == true ? 0.78
                : t.DjEnergy?.Equals("low", StringComparison.OrdinalIgnoreCase) == true ? 0.28 : 0.45;

            var vec = new AcousticFeatureVector(
                TrackUid: t.TrackUid,
                InstrumentalProbability: instProb,
                Bpm: bpm,
                BpmConfidence: bpmConf,
                Arousal: arousal,
                Valence: 0.52,
                IntegratedLufs: t.ReplayGainTrackDb.HasValue ? -18.0 - t.ReplayGainTrackDb.Value : -15.0,
                TopGenre: string.IsNullOrWhiteSpace(t.Genre) && t.IsClassical ? "Classical" : null,
                MoodHeads: t.IsClassical ? [("Contemplative", 0.80), ("Calm", 0.74)] : null
            );

            proposals.AddRange(Acoustic5DInferenceService.GenerateMissing5DProposalsFromResult(
                snap,
                Acoustic5DInferenceService.InferTrack5D(vec),
                confidenceCap: 0.55));
        }

        return store.EnqueueProposals(proposals);
    }

    private static string? FindTonewiseCliExecutable()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "tonewise-cli.exe");
        if (File.Exists(local)) return local;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "core", "target", "release", "tonewise-cli.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed record PendingTrackUpsert(
        string? PrevUidForRel,
        string TrackUid,
        byte[] AudioHash,
        long HeaderSize,
        byte[] HeaderHash,
        long PaddingBytes,
        string RelPath,
        long FileSize,
        long MtimeMs,
        string Codec,
        int SampleRateHz,
        int? BitDepth,
        int? BitrateKbps,
        long DurationMs,
        string Title,
        string? Album,
        string? AlbumArtist,
        string ArtistsJson,
        string ComposersJson,
        string ConductorsJson,
        string? WorkName,
        string? MovementName,
        int? MovementNumber,
        int DiscNo,
        int TrackNo,
        string? Publisher,
        string? Genre,
        int? Year,
        string? OriginalDate,
        string? ReleaseType,
        double? RgTrack,
        double? RgAlbum,
        double? RgPeak,
        string DjSceneJson,
        long DjSceneTs,
        string? DjVocals,
        long DjVocalsTs,
        string? DjEnergy,
        long DjEnergyTs,
        double? EstBpm,
        string? TempoBucket,
        long TempoTs,
        string MoodJson,
        long MoodTs
    );

    private static void FlushManifestUpsertBatch(SqliteConnection conn, List<PendingTrackUpsert> batch)
    {
        if (batch.Count == 0) return;

        using var tx = TonewiseSqlite.BeginImmediateTransaction(conn);
        foreach (var item in batch)
        {
            PrepareUidMigrationAndMergeLedger(conn, tx, item);

            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO tracks (
                    track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                    rel_path, file_size, mtime_ms, codec, sample_rate_hz, bit_depth,
                    bitrate_kbps, duration_ms, title, album, album_artist, artists_json,
                    composers_json, conductors_json, work_name, movement_name, movement_number,
                    disc_number, track_number, publisher, genre, year, original_date, release_type,
                    replaygain_track_db, replaygain_album_db, replaygain_peak,
                    dj_scene_json, dj_scene_ts, dj_vocals, dj_vocals_ts, dj_energy, dj_energy_ts,
                    tempo_bpm, tempo_bucket, tempo_ts, mood_json, mood_ts
                ) VALUES (
                    $uid, $af, $hsz, $hb, $pad,
                    $rel, $fsz, $mt, $cdc, $sr, $bd,
                    $br, $dur, $ti, $al, $aa, $ar,
                    $cmp, $cnd, $wk, $mvn, $mvi,
                    $dsc, $trk, $pub, $gen, $yr, $odt, $rtyp,
                    $rg, $rga, $rgp,
                    $djs, $djst, $djv, $djvt, $dje, $djet,
                    $bpm, $bkt, $bmpt, $md, $mdt
                )
                ON CONFLICT(track_uid) DO UPDATE SET
                    audio_frame_blake3 = excluded.audio_frame_blake3,
                    header_size = excluded.header_size,
                    header_blake3 = excluded.header_blake3,
                    padding_bytes = excluded.padding_bytes,
                    rel_path = excluded.rel_path,
                    file_size = excluded.file_size,
                    mtime_ms = excluded.mtime_ms,
                    codec = excluded.codec,
                    sample_rate_hz = CASE WHEN excluded.sample_rate_hz > 0 THEN excluded.sample_rate_hz ELSE tracks.sample_rate_hz END,
                    bit_depth = COALESCE(excluded.bit_depth, tracks.bit_depth),
                    bitrate_kbps = COALESCE(excluded.bitrate_kbps, tracks.bitrate_kbps),
                    duration_ms = CASE WHEN excluded.duration_ms > 0 THEN excluded.duration_ms ELSE tracks.duration_ms END,
                    title = CASE WHEN length(trim(excluded.title)) > 0 THEN excluded.title ELSE tracks.title END,
                    album = COALESCE(NULLIF(excluded.album, ''), tracks.album),
                    album_artist = COALESCE(excluded.album_artist, tracks.album_artist),
                    artists_json = CASE WHEN excluded.artists_json <> '[]' THEN excluded.artists_json ELSE tracks.artists_json END,
                    composers_json = CASE WHEN excluded.composers_json <> '[]' THEN excluded.composers_json ELSE tracks.composers_json END,
                    conductors_json = CASE WHEN excluded.conductors_json <> '[]' THEN excluded.conductors_json ELSE tracks.conductors_json END,
                    work_name = COALESCE(NULLIF(excluded.work_name, ''), tracks.work_name),
                    movement_name = COALESCE(NULLIF(excluded.movement_name, ''), tracks.movement_name),
                    movement_number = COALESCE(excluded.movement_number, tracks.movement_number),
                    disc_number = CASE WHEN excluded.disc_number > 0 THEN excluded.disc_number ELSE tracks.disc_number END,
                    track_number = CASE WHEN excluded.track_number > 0 THEN excluded.track_number ELSE tracks.track_number END,
                    publisher = COALESCE(NULLIF(excluded.publisher, ''), tracks.publisher),
                    genre = COALESCE(NULLIF(excluded.genre, ''), tracks.genre),
                    year = COALESCE(excluded.year, tracks.year),
                    original_date = COALESCE(NULLIF(excluded.original_date, ''), tracks.original_date),
                    release_type = COALESCE(NULLIF(excluded.release_type, ''), tracks.release_type),
                    replaygain_track_db = COALESCE(excluded.replaygain_track_db, tracks.replaygain_track_db),
                    replaygain_album_db = COALESCE(excluded.replaygain_album_db, tracks.replaygain_album_db),
                    replaygain_peak = COALESCE(excluded.replaygain_peak, tracks.replaygain_peak),
                    dj_scene_json = CASE WHEN tracks.dj_scene_ts = 0 THEN excluded.dj_scene_json ELSE tracks.dj_scene_json END,
                    dj_scene_ts = CASE WHEN tracks.dj_scene_ts = 0 THEN excluded.dj_scene_ts ELSE tracks.dj_scene_ts END,
                    dj_vocals = CASE WHEN tracks.dj_vocals_ts = 0 THEN excluded.dj_vocals ELSE tracks.dj_vocals END,
                    dj_vocals_ts = CASE WHEN tracks.dj_vocals_ts = 0 THEN excluded.dj_vocals_ts ELSE tracks.dj_vocals_ts END,
                    dj_energy = CASE WHEN tracks.dj_energy_ts = 0 THEN excluded.dj_energy ELSE tracks.dj_energy END,
                    dj_energy_ts = CASE WHEN tracks.dj_energy_ts = 0 THEN excluded.dj_energy_ts ELSE tracks.dj_energy_ts END,
                    tempo_bpm = CASE WHEN tracks.tempo_ts = 0 THEN excluded.tempo_bpm ELSE tracks.tempo_bpm END,
                    tempo_bucket = CASE WHEN tracks.tempo_ts = 0 THEN excluded.tempo_bucket ELSE tracks.tempo_bucket END,
                    tempo_ts = CASE WHEN tracks.tempo_ts = 0 THEN excluded.tempo_ts ELSE tracks.tempo_ts END,
                    mood_json = CASE WHEN tracks.mood_ts = 0 THEN excluded.mood_json ELSE tracks.mood_json END,
                    mood_ts = CASE WHEN tracks.mood_ts = 0 THEN excluded.mood_ts ELSE tracks.mood_ts END;
                """;
            ins.Parameters.AddWithValue("$uid", item.TrackUid);
            ins.Parameters.AddWithValue("$af", item.AudioHash);
            ins.Parameters.AddWithValue("$hsz", item.HeaderSize);
            ins.Parameters.AddWithValue("$hb", item.HeaderHash);
            ins.Parameters.AddWithValue("$pad", item.PaddingBytes);
            ins.Parameters.AddWithValue("$rel", item.RelPath);
            ins.Parameters.AddWithValue("$fsz", item.FileSize);
            ins.Parameters.AddWithValue("$mt", item.MtimeMs);
            ins.Parameters.AddWithValue("$cdc", item.Codec);
            ins.Parameters.AddWithValue("$sr", item.SampleRateHz);
            ins.Parameters.AddWithValue("$bd", item.BitDepth.HasValue ? item.BitDepth.Value : DBNull.Value);
            ins.Parameters.AddWithValue("$br", item.BitrateKbps.HasValue ? item.BitrateKbps.Value : DBNull.Value);
            ins.Parameters.AddWithValue("$dur", item.DurationMs);
            ins.Parameters.AddWithValue("$ti", item.Title);
            ins.Parameters.AddWithValue("$al", (object?)item.Album ?? DBNull.Value);
            ins.Parameters.AddWithValue("$aa", (object?)item.AlbumArtist ?? DBNull.Value);
            ins.Parameters.AddWithValue("$ar", item.ArtistsJson);
            ins.Parameters.AddWithValue("$cmp", item.ComposersJson);
            ins.Parameters.AddWithValue("$cnd", item.ConductorsJson);
            ins.Parameters.AddWithValue("$wk", (object?)item.WorkName ?? DBNull.Value);
            ins.Parameters.AddWithValue("$mvn", (object?)item.MovementName ?? DBNull.Value);
            ins.Parameters.AddWithValue("$mvi", item.MovementNumber.HasValue ? item.MovementNumber.Value : DBNull.Value);
            ins.Parameters.AddWithValue("$dsc", item.DiscNo);
            ins.Parameters.AddWithValue("$trk", item.TrackNo);
            ins.Parameters.AddWithValue("$pub", (object?)item.Publisher ?? DBNull.Value);
            ins.Parameters.AddWithValue("$gen", (object?)item.Genre ?? DBNull.Value);
            ins.Parameters.AddWithValue("$yr", item.Year.HasValue ? item.Year.Value : DBNull.Value);
            ins.Parameters.AddWithValue("$odt", (object?)item.OriginalDate ?? DBNull.Value);
            ins.Parameters.AddWithValue("$rtyp", (object?)item.ReleaseType ?? DBNull.Value);
            ins.Parameters.AddWithValue("$rg", item.RgTrack.HasValue ? item.RgTrack.Value : DBNull.Value);
            ins.Parameters.AddWithValue("$rga", item.RgAlbum.HasValue ? item.RgAlbum.Value : DBNull.Value);
            ins.Parameters.AddWithValue("$rgp", item.RgPeak.HasValue ? item.RgPeak.Value : DBNull.Value);
            ins.Parameters.AddWithValue("$djs", item.DjSceneJson);
            ins.Parameters.AddWithValue("$djst", item.DjSceneTs);
            ins.Parameters.AddWithValue("$djv", (object?)item.DjVocals ?? DBNull.Value);
            ins.Parameters.AddWithValue("$djvt", item.DjVocalsTs);
            ins.Parameters.AddWithValue("$dje", (object?)item.DjEnergy ?? DBNull.Value);
            ins.Parameters.AddWithValue("$djet", item.DjEnergyTs);
            ins.Parameters.AddWithValue("$bpm", item.EstBpm.HasValue ? item.EstBpm.Value : DBNull.Value);
            ins.Parameters.AddWithValue("$bkt", (object?)item.TempoBucket ?? DBNull.Value);
            ins.Parameters.AddWithValue("$bmpt", item.TempoTs);
            ins.Parameters.AddWithValue("$md", item.MoodJson);
            ins.Parameters.AddWithValue("$mdt", item.MoodTs);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        batch.Clear();
    }

    private sealed record LedgerAndLwwRow(
        string TrackUid,
        double? Rating,
        long RatingTs,
        int PlayCountTotal,
        long LastPlayedTs,
        int LegacyPlayCount,
        int LastfmPulledCount,
        int LocalUnsyncedExpiredCount,
        string DjSceneJson,
        long DjSceneTs,
        string? DjVocals,
        long DjVocalsTs,
        string? DjEnergy,
        long DjEnergyTs,
        double? TempoBpm,
        string? TempoBucket,
        long TempoTs,
        string MoodJson,
        long MoodTs);

    private static void PrepareUidMigrationAndMergeLedger(SqliteConnection conn, SqliteTransaction tx, PendingTrackUpsert item)
    {
        var matches = new List<LedgerAndLwwRow>();
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT track_uid,
                       rating, rating_ts, play_count_total, last_played_ts,
                       legacy_play_count, lastfm_pulled_count, local_unsynced_expired_count,
                       dj_scene_json, dj_scene_ts, dj_vocals, dj_vocals_ts, dj_energy, dj_energy_ts,
                       tempo_bpm, tempo_bucket, tempo_ts, mood_json, mood_ts
                FROM tracks
                WHERE track_uid = $uid
                   OR audio_frame_blake3 = $af
                   OR ($oldUid IS NOT NULL AND track_uid = $oldUid);
                """;
            sel.Parameters.AddWithValue("$uid", item.TrackUid);
            sel.Parameters.AddWithValue("$af", item.AudioHash);
            sel.Parameters.AddWithValue("$oldUid", (object?)item.PrevUidForRel ?? DBNull.Value);

            using var reader = sel.ExecuteReader();
            while (reader.Read())
            {
                matches.Add(new LedgerAndLwwRow(
                    TrackUid: reader.GetString(0),
                    Rating: reader.IsDBNull(1) ? null : reader.GetDouble(1),
                    RatingTs: reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    PlayCountTotal: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    LastPlayedTs: reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    LegacyPlayCount: reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    LastfmPulledCount: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    LocalUnsyncedExpiredCount: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    DjSceneJson: reader.IsDBNull(8) ? "[]" : reader.GetString(8),
                    DjSceneTs: reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                    DjVocals: reader.IsDBNull(10) ? null : reader.GetString(10),
                    DjVocalsTs: reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
                    DjEnergy: reader.IsDBNull(12) ? null : reader.GetString(12),
                    DjEnergyTs: reader.IsDBNull(13) ? 0 : reader.GetInt64(13),
                    TempoBpm: reader.IsDBNull(14) ? null : reader.GetDouble(14),
                    TempoBucket: reader.IsDBNull(15) ? null : reader.GetString(15),
                    TempoTs: reader.IsDBNull(16) ? 0 : reader.GetInt64(16),
                    MoodJson: reader.IsDBNull(17) ? "[]" : reader.GetString(17),
                    MoodTs: reader.IsDBNull(18) ? 0 : reader.GetInt64(18)));
            }
        }

        if (matches.Count == 0)
            return;
        if (matches.Count == 1 && string.Equals(matches[0].TrackUid, item.TrackUid, StringComparison.Ordinal))
            return;

        LedgerAndLwwRow keeper = matches.FirstOrDefault(m => string.Equals(m.TrackUid, item.TrackUid, StringComparison.Ordinal))
            ?? matches[0];
        var merged = MergeLedgerAndLww(matches);

        foreach (var row in matches)
        {
            if (string.Equals(row.TrackUid, keeper.TrackUid, StringComparison.Ordinal))
                continue;

            using var delOther = conn.CreateCommand();
            delOther.Transaction = tx;
            delOther.CommandText = "DELETE FROM tracks WHERE track_uid = $delUid;";
            delOther.Parameters.AddWithValue("$delUid", row.TrackUid);
            delOther.ExecuteNonQuery();
        }

        UpdateTrackUidAndMergedLedger(conn, tx, keeper.TrackUid, item.TrackUid, item.AudioHash, merged);
    }

    private static void MergeDuplicateIntoCanonicalAndDelete(SqliteConnection conn, SqliteTransaction tx, string oldUid, string canonicalUid)
    {
        if (string.Equals(oldUid, canonicalUid, StringComparison.Ordinal))
            return;

        var rows = new List<LedgerAndLwwRow>(2);
        byte[]? canonicalAudioHash = null;
        bool hasOld = false;
        bool hasCanonical = false;

        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT track_uid, audio_frame_blake3,
                       rating, rating_ts, play_count_total, last_played_ts,
                       legacy_play_count, lastfm_pulled_count, local_unsynced_expired_count,
                       dj_scene_json, dj_scene_ts, dj_vocals, dj_vocals_ts, dj_energy, dj_energy_ts,
                       tempo_bpm, tempo_bucket, tempo_ts, mood_json, mood_ts
                FROM tracks
                WHERE track_uid = $oldUid OR track_uid = $canonUid;
                """;
            sel.Parameters.AddWithValue("$oldUid", oldUid);
            sel.Parameters.AddWithValue("$canonUid", canonicalUid);

            using var reader = sel.ExecuteReader();
            while (reader.Read())
            {
                string uid = reader.GetString(0);
                if (string.Equals(uid, oldUid, StringComparison.Ordinal))
                    hasOld = true;
                if (string.Equals(uid, canonicalUid, StringComparison.Ordinal))
                {
                    hasCanonical = true;
                    canonicalAudioHash = (byte[])reader.GetValue(1);
                }

                rows.Add(new LedgerAndLwwRow(
                    TrackUid: uid,
                    Rating: reader.IsDBNull(2) ? null : reader.GetDouble(2),
                    RatingTs: reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    PlayCountTotal: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    LastPlayedTs: reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    LegacyPlayCount: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    LastfmPulledCount: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    LocalUnsyncedExpiredCount: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    DjSceneJson: reader.IsDBNull(9) ? "[]" : reader.GetString(9),
                    DjSceneTs: reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                    DjVocals: reader.IsDBNull(11) ? null : reader.GetString(11),
                    DjVocalsTs: reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                    DjEnergy: reader.IsDBNull(13) ? null : reader.GetString(13),
                    DjEnergyTs: reader.IsDBNull(14) ? 0 : reader.GetInt64(14),
                    TempoBpm: reader.IsDBNull(15) ? null : reader.GetDouble(15),
                    TempoBucket: reader.IsDBNull(16) ? null : reader.GetString(16),
                    TempoTs: reader.IsDBNull(17) ? 0 : reader.GetInt64(17),
                    MoodJson: reader.IsDBNull(18) ? "[]" : reader.GetString(18),
                    MoodTs: reader.IsDBNull(19) ? 0 : reader.GetInt64(19)));
            }
        }

        if (!hasOld || !hasCanonical || canonicalAudioHash is null)
            return;

        var merged = MergeLedgerAndLww(rows);

        using (var delOld = conn.CreateCommand())
        {
            delOld.Transaction = tx;
            delOld.CommandText = "DELETE FROM tracks WHERE track_uid = $oldUid;";
            delOld.Parameters.AddWithValue("$oldUid", oldUid);
            delOld.ExecuteNonQuery();
        }

        UpdateTrackUidAndMergedLedger(conn, tx, canonicalUid, canonicalUid, canonicalAudioHash, merged);
    }

    private static LedgerAndLwwRow MergeLedgerAndLww(IReadOnlyList<LedgerAndLwwRow> rows)
    {
        double? rating = null;
        long ratingTs = 0;
        int playCountTotal = 0;
        long lastPlayedTs = 0;
        int legacyPlayCount = 0;
        int lastfmPulledCount = 0;
        int localUnsyncedExpiredCount = 0;
        string djSceneJson = "[]";
        long djSceneTs = 0;
        string? djVocals = null;
        long djVocalsTs = 0;
        string? djEnergy = null;
        long djEnergyTs = 0;
        double? tempoBpm = null;
        string? tempoBucket = null;
        long tempoTs = 0;
        string moodJson = "[]";
        long moodTs = 0;

        foreach (var row in rows)
        {
            if (row.RatingTs > ratingTs || (row.RatingTs == ratingTs && rating is null && row.Rating.HasValue))
            {
                rating = row.Rating;
                ratingTs = row.RatingTs;
            }

            playCountTotal = Math.Max(playCountTotal, row.PlayCountTotal);
            lastPlayedTs = Math.Max(lastPlayedTs, row.LastPlayedTs);
            legacyPlayCount = Math.Max(legacyPlayCount, row.LegacyPlayCount);
            lastfmPulledCount = Math.Max(lastfmPulledCount, row.LastfmPulledCount);
            localUnsyncedExpiredCount = Math.Max(localUnsyncedExpiredCount, row.LocalUnsyncedExpiredCount);

            if (row.DjSceneTs > djSceneTs || (row.DjSceneTs == djSceneTs && djSceneJson == "[]" && row.DjSceneJson != "[]"))
            {
                djSceneJson = row.DjSceneJson;
                djSceneTs = row.DjSceneTs;
            }
            if (row.DjVocalsTs > djVocalsTs || (row.DjVocalsTs == djVocalsTs && djVocals is null && row.DjVocals is not null))
            {
                djVocals = row.DjVocals;
                djVocalsTs = row.DjVocalsTs;
            }
            if (row.DjEnergyTs > djEnergyTs || (row.DjEnergyTs == djEnergyTs && djEnergy is null && row.DjEnergy is not null))
            {
                djEnergy = row.DjEnergy;
                djEnergyTs = row.DjEnergyTs;
            }
            if (row.TempoTs > tempoTs || (row.TempoTs == tempoTs && tempoBpm is null && row.TempoBpm.HasValue))
            {
                tempoBpm = row.TempoBpm;
                tempoBucket = row.TempoBucket;
                tempoTs = row.TempoTs;
            }
            if (row.MoodTs > moodTs || (row.MoodTs == moodTs && moodJson == "[]" && row.MoodJson != "[]"))
            {
                moodJson = row.MoodJson;
                moodTs = row.MoodTs;
            }
        }

        return new LedgerAndLwwRow(
            TrackUid: rows[0].TrackUid,
            Rating: rating,
            RatingTs: ratingTs,
            PlayCountTotal: playCountTotal,
            LastPlayedTs: lastPlayedTs,
            LegacyPlayCount: legacyPlayCount,
            LastfmPulledCount: lastfmPulledCount,
            LocalUnsyncedExpiredCount: localUnsyncedExpiredCount,
            DjSceneJson: djSceneJson,
            DjSceneTs: djSceneTs,
            DjVocals: djVocals,
            DjVocalsTs: djVocalsTs,
            DjEnergy: djEnergy,
            DjEnergyTs: djEnergyTs,
            TempoBpm: tempoBpm,
            TempoBucket: tempoBucket,
            TempoTs: tempoTs,
            MoodJson: moodJson,
            MoodTs: moodTs);
    }

    private static void UpdateTrackUidAndMergedLedger(
        SqliteConnection conn,
        SqliteTransaction tx,
        string keeperUid,
        string targetUid,
        byte[] targetAudioHash,
        LedgerAndLwwRow merged)
    {
        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE tracks
            SET track_uid = $targetUid,
                audio_frame_blake3 = $targetAf,
                rating = $rat,
                rating_ts = $ratt,
                play_count_total = $pct,
                last_played_ts = $lpt,
                legacy_play_count = $lpc,
                lastfm_pulled_count = $lfc,
                local_unsynced_expired_count = $luec,
                dj_scene_json = $djs,
                dj_scene_ts = $djst,
                dj_vocals = $djv,
                dj_vocals_ts = $djvt,
                dj_energy = $dje,
                dj_energy_ts = $djet,
                tempo_bpm = $bpm,
                tempo_bucket = $bkt,
                tempo_ts = $bmpt,
                mood_json = $md,
                mood_ts = $mdt
            WHERE track_uid = $keeperUid;
            """;
        upd.Parameters.AddWithValue("$targetUid", targetUid);
        upd.Parameters.AddWithValue("$targetAf", targetAudioHash);
        upd.Parameters.AddWithValue("$rat", merged.Rating.HasValue ? merged.Rating.Value : DBNull.Value);
        upd.Parameters.AddWithValue("$ratt", merged.RatingTs);
        upd.Parameters.AddWithValue("$pct", merged.PlayCountTotal);
        upd.Parameters.AddWithValue("$lpt", merged.LastPlayedTs);
        upd.Parameters.AddWithValue("$lpc", merged.LegacyPlayCount);
        upd.Parameters.AddWithValue("$lfc", merged.LastfmPulledCount);
        upd.Parameters.AddWithValue("$luec", merged.LocalUnsyncedExpiredCount);
        upd.Parameters.AddWithValue("$djs", merged.DjSceneJson);
        upd.Parameters.AddWithValue("$djst", merged.DjSceneTs);
        upd.Parameters.AddWithValue("$djv", (object?)merged.DjVocals ?? DBNull.Value);
        upd.Parameters.AddWithValue("$djvt", merged.DjVocalsTs);
        upd.Parameters.AddWithValue("$dje", (object?)merged.DjEnergy ?? DBNull.Value);
        upd.Parameters.AddWithValue("$djet", merged.DjEnergyTs);
        upd.Parameters.AddWithValue("$bpm", merged.TempoBpm.HasValue ? merged.TempoBpm.Value : DBNull.Value);
        upd.Parameters.AddWithValue("$bkt", (object?)merged.TempoBucket ?? DBNull.Value);
        upd.Parameters.AddWithValue("$bmpt", merged.TempoTs);
        upd.Parameters.AddWithValue("$md", merged.MoodJson);
        upd.Parameters.AddWithValue("$mdt", merged.MoodTs);
        upd.Parameters.AddWithValue("$keeperUid", keeperUid);
        upd.ExecuteNonQuery();
    }

    private sealed record ExistingTrackRow(
        string Uid,
        long FileSize,
        long MtimeMs,
        string Codec,
        long DurationMs,
        int SampleRateHz,
        int? BitDepth,
        int TrackNumber,
        int DiscNumber);

    private static Dictionary<string, ExistingTrackRow> LoadExistingTrackStates(SqliteConnection conn)
    {
        var existingByRel = new Dictionary<string, ExistingTrackRow>(StringComparer.OrdinalIgnoreCase);
        using var existingCmd = conn.CreateCommand();
        existingCmd.CommandText = """
            SELECT track_uid, rel_path, file_size, mtime_ms, codec, duration_ms, sample_rate_hz, bit_depth, track_number, disc_number
            FROM tracks;
            """;
        using var reader = existingCmd.ExecuteReader();
        while (reader.Read())
        {
            existingByRel[reader.GetString(1)] = new ExistingTrackRow(
                Uid: reader.GetString(0),
                FileSize: reader.GetInt64(2),
                MtimeMs: reader.GetInt64(3),
                Codec: reader.IsDBNull(4) ? "" : reader.GetString(4),
                DurationMs: reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                SampleRateHz: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                BitDepth: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                TrackNumber: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                DiscNumber: reader.IsDBNull(9) ? 0 : reader.GetInt32(9));
        }
        return existingByRel;
    }

    private static bool NeedsSelfHealRescan(ExistingTrackRow prev, string rel, string ext)
    {
        if (!string.Equals(prev.Codec, prev.Codec.ToLowerInvariant(), StringComparison.Ordinal))
            return true;
        if (prev.SampleRateHz <= 0)
            return true;
        if (prev.DurationMs <= 0
            && !ext.Equals(".dsf", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".dff", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase) && (prev.BitDepth is null or <= 0))
            return true;
        var (pathDisc, pathTrack) = ParseDiscAndTrackFromRelPath(rel);
        if (prev.TrackNumber <= 0 && pathTrack > 0)
            return true;
        if (prev.DiscNumber <= 0 && pathDisc > 0)
            return true;
        return false;
    }

    private sealed record KnownSkipEntry(long FileSize, long MtimeMs, string Reason, string? CanonicalTrackUid = null);

    private static string ResolveKnownSkipsPath(string dbPath) =>
        Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "scan_known_skips.json");

    private static Dictionary<string, KnownSkipEntry> LoadKnownSkips(string dbPath)
    {
        string path = ResolveKnownSkipsPath(dbPath);
        if (!File.Exists(path))
            return new Dictionary<string, KnownSkipEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, KnownSkipEntry>>(json);
            return parsed is null
                ? new Dictionary<string, KnownSkipEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, KnownSkipEntry>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, KnownSkipEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveKnownSkips(string dbPath, Dictionary<string, KnownSkipEntry> skips)
    {
        string path = ResolveKnownSkipsPath(dbPath);
        try
        {
            if (skips.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            string json = JsonSerializer.Serialize(skips, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Non-fatal: scan skips optimization cache
        }
    }

    private static bool IsActiveKnownSkip(
        Dictionary<string, KnownSkipEntry> knownSkips,
        string rel,
        long fileSize,
        long mtimeMs,
        HashSet<string> existingUids)
    {
        if (!knownSkips.TryGetValue(rel, out var skip))
            return false;
        if (skip.FileSize != fileSize || Math.Abs(skip.MtimeMs - mtimeMs) > 2000)
            return false;
        if (string.Equals(skip.Reason, "duplicate_audio", StringComparison.Ordinal))
            return !string.IsNullOrEmpty(skip.CanonicalTrackUid) && existingUids.Contains(skip.CanonicalTrackUid);
        return true;
    }

    private static bool IsIndexedOwnerStillValidOnDisk(
        string cleanRoot,
        string ownerRel,
        Dictionary<string, ExistingTrackRow> existingByRel)
    {
        if (!existingByRel.TryGetValue(ownerRel, out var ownerRow))
            return false;
        string full = Path.Combine(cleanRoot, ownerRel.Replace('/', Path.DirectorySeparatorChar));
        if (!LongPathHelper.FileExists(full))
            return false;
        try
        {
            var fi = new FileInfo(LongPathHelper.ToExtendedIfLong(full));
            long mt = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            return fi.Length == ownerRow.FileSize && Math.Abs(mt - ownerRow.MtimeMs) <= 2000;
        }
        catch
        {
            return false;
        }
    }

    public bool HasPendingDiskChanges(string musicRoot)
    {
        if (string.IsNullOrWhiteSpace(musicRoot) || !LongPathHelper.DirectoryExists(musicRoot))
            return false;

        string dbPath = Path.Combine(musicRoot, ".tonewise", "manifest.db");
        if (!File.Exists(dbPath))
            return false;

        try
        {
            using var conn = TonewiseSqlite.OpenReadOnly(dbPath);
            var existingByRel = LoadExistingTrackStates(conn);
            if (existingByRel.Count == 0)
                return false;

            var existingUids = existingByRel.Values.Select(v => v.Uid).ToHashSet(StringComparer.Ordinal);
            var knownSkips = LoadKnownSkips(dbPath);

            string cleanRoot = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(musicRoot)).TrimEnd('\\', '/');
            var seenRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in LongPathHelper.SafeEnumerateFiles(cleanRoot, "*.*", recursive: true, skipHiddenDirectories: true))
            {
                string ext = Path.GetExtension(file);
                if (!AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

                string cleanFile = LongPathHelper.StripExtendedPrefix(file);
                string rel = Path.GetRelativePath(cleanRoot, cleanFile).Replace('\\', '/');
                seenRelPaths.Add(rel);

                string ioPath = LongPathHelper.ToExtendedIfLong(cleanFile);
                var fi = new FileInfo(ioPath);
                long mt = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();

                if (!existingByRel.TryGetValue(rel, out var prev))
                {
                    if (IsActiveKnownSkip(knownSkips, rel, fi.Length, mt, existingUids))
                        continue;
                    return true;
                }

                if (prev.FileSize != fi.Length || Math.Abs(prev.MtimeMs - mt) > 2000)
                    return true;

                if (NeedsSelfHealRescan(prev, rel, ext) && !IsActiveKnownSkip(knownSkips, rel, fi.Length, mt, existingUids))
                    return true;
            }

            foreach (string rel in existingByRel.Keys)
            {
                if (!seenRelPaths.Contains(rel))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void BuildManifestWithFfiFallback(string musicRoot, string dbPath, bool fastMode = true)
    {
        using var conn = TonewiseSqlite.OpenReadWrite(dbPath);
        using (var init = conn.CreateCommand())
        {
            init.CommandText = ManifestSchemaProvider.ManifestSql;
            init.ExecuteNonQuery();
        }
        ManifestSchemaProvider.EnsureSchemaV2(conn);

        var existingByRel = LoadExistingTrackStates(conn);
        var existingRelByUid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rel, row) in existingByRel)
        {
            existingRelByUid[row.Uid] = rel;
        }

        var seenRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeUids = new HashSet<string>(StringComparer.Ordinal);
        var claimedUidToRel = new Dictionary<string, string>(StringComparer.Ordinal);
        var newKnownSkips = new Dictionary<string, KnownSkipEntry>(StringComparer.OrdinalIgnoreCase);
        var duplicateRowRetirements = new List<(string OldUid, string CanonicalUid)>();
        string cleanRoot = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(musicRoot)).TrimEnd('\\', '/');
        const int batchCommitSize = 150;
        var pendingBatch = new List<PendingTrackUpsert>(batchCommitSize);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Process indexed relative paths before unindexed paths so duplicate copies never displace canonical entries.
        var existingFiles = new List<(string CleanFile, string Rel, string Ext)>();
        var newFiles = new List<(string CleanFile, string Rel, string Ext)>();
        foreach (string file in LongPathHelper.SafeEnumerateFiles(cleanRoot, "*.*", recursive: true, skipHiddenDirectories: true))
        {
            string ext = Path.GetExtension(file);
            if (!AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

            string cleanFile = LongPathHelper.StripExtendedPrefix(file);
            string rel = Path.GetRelativePath(cleanRoot, cleanFile).Replace('\\', '/');
            if (existingByRel.ContainsKey(rel))
                existingFiles.Add((cleanFile, rel, ext));
            else
                newFiles.Add((cleanFile, rel, ext));
        }

        var orderedFiles = new List<(string CleanFile, string Rel, string Ext)>(existingFiles.Count + newFiles.Count);
        orderedFiles.AddRange(existingFiles);
        orderedFiles.AddRange(newFiles);

        // Perform all disk traversal & Rust FFI work outside SQLite transactions; commit in short 150-track batches.
        foreach (var (cleanFile, rel, ext) in orderedFiles)
        {
            seenRelPaths.Add(rel);
            string? prevUidForRel = null;
            long? observedSize = null;
            long? observedMtime = null;

            try
            {
                string ioPath = LongPathHelper.ToExtendedIfLong(cleanFile);
                var fi = new FileInfo(ioPath);
                long fsz = fi.Length;
                long mt = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                observedSize = fsz;
                observedMtime = mt;

                if (existingByRel.TryGetValue(rel, out var prev))
                {
                    if (prev.FileSize == fsz && Math.Abs(prev.MtimeMs - mt) <= 2000 && !NeedsSelfHealRescan(prev, rel, ext))
                    {
                        activeUids.Add(prev.Uid);
                        claimedUidToRel[prev.Uid] = rel;
                        continue;
                    }
                    prevUidForRel = prev.Uid;
                }

                var probe = TonewiseCoreClient.ProbeTrack(ioPath);
                byte[] audioHash = Convert.FromHexString(probe.AudioFrameBlake3Hex);
                byte[] headerHash = Convert.FromHexString(probe.HeaderBlake3Hex);
                long headerSize = (long)probe.HeaderSize;
                long paddingBytes = (long)probe.PaddingBytes;
                fsz = (long)probe.FileSize;
                observedSize = fsz;
                string trackUid = probe.AudioFrameBlake3Hex;

                if (claimedUidToRel.TryGetValue(trackUid, out string? claimedRel)
                    && !string.Equals(claimedRel, rel, StringComparison.OrdinalIgnoreCase))
                {
                    if (prevUidForRel is not null && !string.Equals(prevUidForRel, trackUid, StringComparison.Ordinal))
                    {
                        duplicateRowRetirements.Add((prevUidForRel, trackUid));
                    }
                    activeUids.Add(trackUid);
                    newKnownSkips[rel] = new KnownSkipEntry(fsz, mt, "duplicate_audio", trackUid);
                    continue;
                }

                if (existingRelByUid.TryGetValue(trackUid, out string? prevOwnerRel)
                    && !string.Equals(prevOwnerRel, rel, StringComparison.OrdinalIgnoreCase)
                    && IsIndexedOwnerStillValidOnDisk(cleanRoot, prevOwnerRel, existingByRel))
                {
                    if (prevUidForRel is not null && !string.Equals(prevUidForRel, trackUid, StringComparison.Ordinal))
                    {
                        duplicateRowRetirements.Add((prevUidForRel, trackUid));
                    }
                    claimedUidToRel[trackUid] = prevOwnerRel;
                    activeUids.Add(trackUid);
                    newKnownSkips[rel] = new KnownSkipEntry(fsz, mt, "duplicate_audio", trackUid);
                    continue;
                }

                claimedUidToRel[trackUid] = rel;
                activeUids.Add(trackUid);

                string codec = !string.IsNullOrWhiteSpace(probe.Codec)
                    ? probe.Codec
                    : ext.TrimStart('.').ToLowerInvariant();
                int sampleRateHz = (int)probe.SampleRateHz;
                int? bitDepth = probe.BitDepth.HasValue ? (int)probe.BitDepth.Value : null;
                int? bitrateKbps = probe.BitrateKbps.HasValue ? (int)probe.BitrateKbps.Value : null;
                long durationMs = (long)probe.DurationMs;
                double? rgTrack = probe.ReplayGainTrackDb;
                double? rgAlbum = probe.ReplayGainAlbumDb;
                double? rgPeak = probe.ReplayGainPeak;
                double? estBpm = probe.TempoBpm;
                string? tempoBucket = probe.TempoBucket;

                if (!fastMode)
                {
                    var analysis = TonewiseCoreClient.AnalyzeAudioFile(ioPath);
                    if (analysis.SampleRateHz > 0) sampleRateHz = (int)analysis.SampleRateHz;
                    if (analysis.BitDepth.HasValue) bitDepth = (int)analysis.BitDepth.Value;
                    if (analysis.BitrateKbps.HasValue) bitrateKbps = (int)analysis.BitrateKbps.Value;
                    if (analysis.DurationMs > 0) durationMs = (long)analysis.DurationMs;
                    rgTrack ??= analysis.ReplayGainTrackDb;
                    estBpm ??= analysis.EstimatedBpm;
                    tempoBucket ??= analysis.TempoBucket;
                }

                bool isDsd = ext.Equals(".dsf", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".dff", StringComparison.OrdinalIgnoreCase);
                if ((!isDsd && durationMs <= 0)
                    || sampleRateHz <= 0
                    || (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase) && (bitDepth is null or <= 0)))
                {
                    newKnownSkips[rel] = new KnownSkipEntry(fsz, mt, "unhealable_probe", trackUid);
                }

                string title = !string.IsNullOrWhiteSpace(probe.Title)
                    ? probe.Title.Trim()
                    : Path.GetFileNameWithoutExtension(cleanFile);
                string? album = !string.IsNullOrWhiteSpace(probe.Album) ? probe.Album.Trim() : null;
                string? albumArtist = !string.IsNullOrWhiteSpace(probe.AlbumArtist) ? probe.AlbumArtist.Trim() : null;

                IReadOnlyList<string> artists = probe.Artists.Count > 0
                    ? probe.Artists
                    : (!string.IsNullOrWhiteSpace(albumArtist)
                        ? Tonewise.SyncHost.Workbench.ArtistEntitySplitter.SplitPerformers(albumArtist)
                        : []);
                string artistsJson = artists.Count > 0 ? JsonSerializer.Serialize(artists) : "[]";
                string composersJson = probe.Composers.Count > 0 ? JsonSerializer.Serialize(probe.Composers) : "[]";
                string conductorsJson = probe.Conductors.Count > 0 ? JsonSerializer.Serialize(probe.Conductors) : "[]";

                string? workName = !string.IsNullOrWhiteSpace(probe.WorkName) ? probe.WorkName.Trim() : null;
                string? movementName = !string.IsNullOrWhiteSpace(probe.MovementName) ? probe.MovementName.Trim() : null;
                int? movementNumber = probe.MovementNumber is > 0 ? (int)probe.MovementNumber.Value : null;
                var (pathDiscNo, pathTrackNo) = ParseDiscAndTrackFromRelPath(rel);
                int discNo = probe.DiscNumber > 0 ? (int)probe.DiscNumber : pathDiscNo;
                int trackNo = probe.TrackNumber > 0 ? (int)probe.TrackNumber : pathTrackNo;
                string? publisher = !string.IsNullOrWhiteSpace(probe.Publisher) ? probe.Publisher.Trim() : null;
                string? genre = !string.IsNullOrWhiteSpace(probe.Genre) ? probe.Genre.Trim() : null;
                int? year = probe.Year;
                string? originalDate = !string.IsNullOrWhiteSpace(probe.OriginalDate) ? probe.OriginalDate.Trim() : null;
                string? releaseType = !string.IsNullOrWhiteSpace(probe.ReleaseType) ? probe.ReleaseType.Trim() : null;

                string djSceneJson = probe.DjScene.Count > 0 ? JsonSerializer.Serialize(probe.DjScene) : "[]";
                long djSceneTs = probe.DjScene.Count > 0 ? nowMs : 0;
                string? djVocals = !string.IsNullOrWhiteSpace(probe.DjVocals) ? probe.DjVocals : null;
                long djVocalsTs = djVocals is not null ? nowMs : 0;
                string? djEnergy = !string.IsNullOrWhiteSpace(probe.DjEnergy) ? probe.DjEnergy : null;
                long djEnergyTs = djEnergy is not null ? nowMs : 0;
                long tempoTs = estBpm.HasValue ? nowMs : 0;
                string moodJson = probe.Moods.Count > 0 ? JsonSerializer.Serialize(probe.Moods) : "[]";
                long moodTs = probe.Moods.Count > 0 ? nowMs : 0;

                pendingBatch.Add(new PendingTrackUpsert(
                    PrevUidForRel: prevUidForRel,
                    TrackUid: trackUid,
                    AudioHash: audioHash,
                    HeaderSize: headerSize,
                    HeaderHash: headerHash,
                    PaddingBytes: paddingBytes,
                    RelPath: rel,
                    FileSize: fsz,
                    MtimeMs: mt,
                    Codec: codec,
                    SampleRateHz: sampleRateHz,
                    BitDepth: bitDepth,
                    BitrateKbps: bitrateKbps,
                    DurationMs: durationMs,
                    Title: title,
                    Album: album,
                    AlbumArtist: albumArtist,
                    ArtistsJson: artistsJson,
                    ComposersJson: composersJson,
                    ConductorsJson: conductorsJson,
                    WorkName: workName,
                    MovementName: movementName,
                    MovementNumber: movementNumber,
                    DiscNo: discNo,
                    TrackNo: trackNo,
                    Publisher: publisher,
                    Genre: genre,
                    Year: year,
                    OriginalDate: originalDate,
                    ReleaseType: releaseType,
                    RgTrack: rgTrack,
                    RgAlbum: rgAlbum,
                    RgPeak: rgPeak,
                    DjSceneJson: djSceneJson,
                    DjSceneTs: djSceneTs,
                    DjVocals: djVocals,
                    DjVocalsTs: djVocalsTs,
                    DjEnergy: djEnergy,
                    DjEnergyTs: djEnergyTs,
                    EstBpm: estBpm,
                    TempoBucket: tempoBucket,
                    TempoTs: tempoTs,
                    MoodJson: moodJson,
                    MoodTs: moodTs));

                if (pendingBatch.Count >= batchCommitSize)
                {
                    FlushManifestUpsertBatch(conn, pendingBatch);
                }
            }
            catch
            {
                if (observedSize.HasValue && observedMtime.HasValue)
                {
                    newKnownSkips[rel] = new KnownSkipEntry(observedSize.Value, observedMtime.Value, "unreadable_probe", prevUidForRel);
                }
            }
        }

        FlushManifestUpsertBatch(conn, pendingBatch);

        if (duplicateRowRetirements.Count > 0)
        {
            using var retireTx = TonewiseSqlite.BeginImmediateTransaction(conn);
            foreach (var (oldUid, canonicalUid) in duplicateRowRetirements)
            {
                MergeDuplicateIntoCanonicalAndDelete(conn, retireTx, oldUid, canonicalUid);
            }
            retireTx.Commit();
        }

        var staleUids = new List<string>();
        foreach (var (rel, prev) in existingByRel)
        {
            if (seenRelPaths.Contains(rel) || activeUids.Contains(prev.Uid)) continue;
            string expectedFull = Path.Combine(cleanRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!LongPathHelper.FileExists(expectedFull))
            {
                staleUids.Add(prev.Uid);
            }
        }
        if (staleUids.Count > 0)
        {
            using var delTx = TonewiseSqlite.BeginImmediateTransaction(conn);
            foreach (string uid in staleUids)
            {
                using var del = conn.CreateCommand();
                del.Transaction = delTx;
                del.CommandText = "DELETE FROM tracks WHERE track_uid = $uid;";
                del.Parameters.AddWithValue("$uid", uid);
                del.ExecuteNonQuery();
            }
            delTx.Commit();
        }

        SaveKnownSkips(dbPath, newKnownSkips);
    }

    public float[]? LoadWaveform(TrackItem track)
    {
        if (WaveformsBinPath is null || !File.Exists(WaveformsBinPath))
        {
            return null;
        }
        return TonewiseCoreClient.LookupWaveform(WaveformsBinPath, track.AudioFrameBlake3Hex);
    }

    public static (int DiscNumber, int TrackNumber) ParseDiscAndTrackFromRelPath(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return (0, 0);
        string norm = relPath.Replace('\\', '/');
        string fileStem = Path.GetFileNameWithoutExtension(norm);

        var discTrack = System.Text.RegularExpressions.Regex.Match(
            fileStem,
            @"^0*(?<disc>\d{1,2})-0*(?<track>\d{1,3})(?:[\s._-]+|$)");
        if (discTrack.Success
            && int.TryParse(discTrack.Groups["disc"].Value, out int d1)
            && int.TryParse(discTrack.Groups["track"].Value, out int t1))
        {
            return (Math.Max(0, d1), Math.Max(0, t1));
        }

        int trackNo = 0;
        var trackOnly = System.Text.RegularExpressions.Regex.Match(
            fileStem,
            @"^0*(?<track>\d{1,3})(?:[\s._-]+|$)");
        if (trackOnly.Success && int.TryParse(trackOnly.Groups["track"].Value, out int t2))
        {
            trackNo = Math.Max(0, t2);
        }

        int discNo = 0;
        int lastSlash = norm.LastIndexOf('/');
        if (lastSlash > 0)
        {
            string parentDir = norm[..lastSlash];
            var discDir = System.Text.RegularExpressions.Regex.Match(
                parentDir,
                @"(?:^|/)(?:CD|Disc|Disk)\s*0*(?<disc>\d{1,2})(?=$|[/\s_-])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (discDir.Success && int.TryParse(discDir.Groups["disc"].Value, out int d2))
            {
                discNo = Math.Max(0, d2);
            }
        }
        return (discNo, trackNo);
    }

    private static HashSet<string> ReadExistingColumns(SqliteConnection conn)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('tracks');";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            cols.Add(reader.GetString(0));
        }
        return cols;
    }

    private static readonly string[] CoverPriorityNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png", "cover.webp",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.jpeg", "front.png",
        "album.jpg", "album.png",
    ];

    public static (string? Cover, string? Booklet) DiscoverAlbumAssets(string albumDir, string? musicRoot = null)
    {
        if (string.IsNullOrWhiteSpace(albumDir) || !LongPathHelper.DirectoryExists(albumDir))
        {
            return (null, null);
        }

        var (cover, booklet) = ScanDirectoryAssets(albumDir);

        if (cover is null && IsDiscSubfolder(albumDir))
        {
            string? parentDir = Path.GetDirectoryName(albumDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parentDir) && IsWithinMusicRoot(parentDir, musicRoot) && LongPathHelper.DirectoryExists(parentDir))
            {
                var (parentCover, parentBooklet) = ScanDirectoryAssets(parentDir);
                cover = parentCover;
                booklet ??= parentBooklet;
            }
        }

        return (cover, booklet);
    }

    private static (string? Cover, string? Booklet) ScanDirectoryAssets(string dir)
    {
        int bestCoverRank = int.MaxValue;
        string? bestCover = null;
        int bestBookletRank = int.MaxValue;
        string? bestBooklet = null;

        try
        {
            foreach (string file in LongPathHelper.SafeEnumerateFiles(dir, "*", recursive: false))
            {
                string name = Path.GetFileName(file);
                if (string.IsNullOrEmpty(name)) continue;

                int coverRank = Array.FindIndex(CoverPriorityNames, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                if (coverRank >= 0 && coverRank < bestCoverRank)
                {
                    bestCoverRank = coverRank;
                    bestCover = file;
                }
                else if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    int rank = string.Equals(name, "booklet.pdf", StringComparison.OrdinalIgnoreCase) ? 0
                        : string.Equals(name, "liner.pdf", StringComparison.OrdinalIgnoreCase) ? 1
                        : 2;
                    if (rank < bestBookletRank)
                    {
                        bestBookletRank = rank;
                        bestBooklet = file;
                    }
                }
            }
        }
        catch
        {
            // Ignore unreadable directory entries during fast asset discovery
        }

        return (bestCover, bestBooklet);
    }

    private static bool IsDiscSubfolder(string dir)
    {
        string folderName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(
            folderName,
            @"^(?:CD|Disc|Disk)\s*\d{1,2}(?:\b|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsWithinMusicRoot(string candidateDir, string? musicRoot)
    {
        if (string.IsNullOrWhiteSpace(musicRoot)) return true;
        try
        {
            string normRoot = Path.GetFullPath(musicRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normCandidate = Path.GetFullPath(candidateDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normCandidate, normRoot, StringComparison.OrdinalIgnoreCase)
                || normCandidate.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindFirstExisting(string dir, params string[] names)
    {
        if (!LongPathHelper.DirectoryExists(dir)) return null;
        foreach (string n in names)
        {
            string p = Path.Combine(dir, n);
            if (LongPathHelper.FileExists(p)) return p;
        }
        return null;
    }

    private static string? FindBookletPdf(string dir) =>
        DiscoverAlbumAssets(dir).Booklet;

    private static IReadOnlyList<string> ParseStringArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
