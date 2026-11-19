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
        [".flac", ".mp3", ".wav", ".m4a", ".alac", ".aac", ".ogg", ".opus", ".dsf", ".dff", ".aiff", ".aif"];

    public string? MusicRoot { get; private set; }
    public string? WaveformsBinPath { get; private set; }
    public string? ManifestDbPath { get; private set; }
    public string? EncyclopediaDbPath { get; private set; }
    public string? SessionsJsonPath { get; private set; }

    public StagingDiffStore? GetStagingDiffStore() =>
        !string.IsNullOrWhiteSpace(ManifestDbPath) && File.Exists(ManifestDbPath)
            ? new StagingDiffStore(ManifestDbPath, MusicRoot)
            : null;

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
                {(hasTrackNumber ? "track_number" : "0")} AS track_number
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
                assets = (
                    FindFirstExisting(albumDir, "cover.jpg", "cover.png", "folder.jpg"),
                    FindBookletPdf(albumDir)
                );
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
                    TrackNumber: trackNumber
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
                    var realVec = new AcousticFeatureVector(
                        TrackUid: t.TrackUid,
                        InstrumentalProbability: t.IsClassical ? 0.90 : null,
                        Bpm: inferred.TempoBpm ?? t.TempoBpm,
                        BpmConfidence: inferred.TempoConfidence > 0 ? inferred.TempoConfidence : null,
                        Arousal: inferred.DjEnergy == "high" ? 0.80 : (inferred.DjEnergy == "low" ? 0.25 : 0.50),
                        Valence: 0.52,
                        IntegratedLufs: t.ReplayGainTrackDb.HasValue ? -18.0 - t.ReplayGainTrackDb.Value : -15.0,
                        TopGenre: inferred.Genre,
                        MoodHeads: inferred.Moods.Select(m => (m, 0.78)).ToList()
                    );
                    proposals.AddRange(Acoustic5DInferenceService.GenerateMissing5DProposals(snap, realVec));
                    continue;
                }
                catch
                {
                    // Fall back to metadata prior vector if decoder fails
                }
            }

            // Fallback baseline priors when audio file is absent (e.g., offline manifest preview)
            double? instProb = t.IsClassical ? 0.90 : null;
            double? bpm = t.TempoBpm ?? (t.IsClassical ? 74.0 : null);
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

            proposals.AddRange(Acoustic5DInferenceService.GenerateMissing5DProposals(snap, vec));
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

    private static void BuildManifestWithFfiFallback(string musicRoot, string dbPath, bool fastMode = true)
    {
        using var conn = TonewiseSqlite.OpenReadWrite(dbPath);
        using (var init = conn.CreateCommand())
        {
            init.CommandText = ManifestSchemaProvider.ManifestSql;
            init.ExecuteNonQuery();
        }
        ManifestSchemaProvider.EnsureSchemaV2(conn);

        var existingByRel = new Dictionary<string, (string Uid, long FileSize, long MtimeMs)>(StringComparer.OrdinalIgnoreCase);
        using (var existingCmd = conn.CreateCommand())
        {
            existingCmd.CommandText = "SELECT track_uid, rel_path, file_size, mtime_ms FROM tracks;";
            using var reader = existingCmd.ExecuteReader();
            while (reader.Read())
            {
                existingByRel[reader.GetString(1)] = (reader.GetString(0), reader.GetInt64(2), reader.GetInt64(3));
            }
        }

        var seenRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string cleanRoot = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(musicRoot)).TrimEnd('\\', '/');

        using var tx = TonewiseSqlite.BeginImmediateTransaction(conn);
        foreach (string file in LongPathHelper.SafeEnumerateFiles(cleanRoot, "*.*", recursive: true, skipHiddenDirectories: true))
        {
            string ext = Path.GetExtension(file);
            if (!AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

            try
            {
                string cleanFile = LongPathHelper.StripExtendedPrefix(file);
                string rel = Path.GetRelativePath(cleanRoot, cleanFile).Replace('\\', '/');
                seenRelPaths.Add(rel);

                string ioPath = LongPathHelper.ToExtendedIfLong(cleanFile);
                var fi = new FileInfo(ioPath);
                long fsz = fi.Length;
                long mt = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();

                string? prevUidForRel = null;
                if (existingByRel.TryGetValue(rel, out var prev))
                {
                    if (prev.FileSize == fsz && Math.Abs(prev.MtimeMs - mt) <= 2000)
                    {
                        continue;
                    }
                    prevUidForRel = prev.Uid;
                }

                byte[] audioHash;
                byte[] headerHash;
                long headerSize = 0;
                string codec;
                int sampleRateHz;
                int? bitDepth = null;
                int? bitrateKbps = null;
                long durationMs;
                double? rgTrack = null;
                double? estBpm = null;
                string? tempoBucket = null;

                var embedded = LibraryFolderOrganizerService.ReadEmbeddedHeaderTags(cleanFile);
                string title = !string.IsNullOrWhiteSpace(embedded.Title)
                    ? embedded.Title!
                    : Path.GetFileNameWithoutExtension(cleanFile);
                string album = !string.IsNullOrWhiteSpace(embedded.Album)
                    ? embedded.Album!
                    : (Path.GetFileName(Path.GetDirectoryName(cleanFile) ?? "") is { Length: > 0 } dirName
                        ? dirName
                        : "Unknown Album");
                string? albumArtist = !string.IsNullOrWhiteSpace(embedded.AlbumArtist) ? embedded.AlbumArtist : null;
                var splitArtists = !string.IsNullOrWhiteSpace(embedded.Artist)
                    ? Tonewise.SyncHost.Workbench.ArtistEntitySplitter.SplitPerformers(embedded.Artist!)
                    : (!string.IsNullOrWhiteSpace(albumArtist)
                        ? Tonewise.SyncHost.Workbench.ArtistEntitySplitter.SplitPerformers(albumArtist!)
                        : []);
                string artistsJson = splitArtists.Count > 0
                    ? JsonSerializer.Serialize(splitArtists)
                    : "[]";
                var splitComposers = !string.IsNullOrWhiteSpace(embedded.Composer)
                    ? Tonewise.SyncHost.Workbench.ArtistEntitySplitter.SplitPerformers(embedded.Composer!)
                    : [];
                var splitConductors = !string.IsNullOrWhiteSpace(embedded.Conductor)
                    ? Tonewise.SyncHost.Workbench.ArtistEntitySplitter.SplitPerformers(embedded.Conductor!)
                    : [];
                string composersJson = splitComposers.Count > 0
                    ? JsonSerializer.Serialize(splitComposers)
                    : "[]";
                string conductorsJson = splitConductors.Count > 0
                    ? JsonSerializer.Serialize(splitConductors)
                    : "[]";
                string? workName = !string.IsNullOrWhiteSpace(embedded.WorkName) ? embedded.WorkName.Trim() : null;
                string? movementName = !string.IsNullOrWhiteSpace(embedded.MovementName) ? embedded.MovementName.Trim() : null;
                int? movementNumber = embedded.MovementNumber is > 0 ? embedded.MovementNumber : null;
                var (pathDiscNo, pathTrackNo) = ParseDiscAndTrackFromRelPath(rel);
                int discNo = embedded.DiscNumber is > 0 ? embedded.DiscNumber.Value : pathDiscNo;
                int trackNo = embedded.TrackNumber is > 0 ? embedded.TrackNumber.Value : pathTrackNo;
                string? genre = !string.IsNullOrWhiteSpace(embedded.Genre) ? embedded.Genre.Trim() : null;
                double? rgAlbum = embedded.ReplayGainAlbumDb;
                rgTrack = embedded.ReplayGainTrackDb;

                if (fastMode)
                {
                    var fp = TonewiseCoreClient.ComputeFingerprint(ioPath);
                    audioHash = Convert.FromHexString(fp.AudioFrameBlake3Hex);
                    headerHash = Convert.FromHexString(fp.HeaderBlake3Hex);
                    headerSize = (long)fp.HeaderSize;
                    fsz = (long)fp.FileSize;
                    codec = ext.TrimStart('.').ToLowerInvariant();
                    sampleRateHz = 44100;
                    durationMs = 0;
                }
                else
                {
                    var fp = TonewiseCoreClient.ComputeFingerprint(ioPath);
                    var analysis = TonewiseCoreClient.AnalyzeAudioFile(ioPath);
                    audioHash = Convert.FromHexString(fp.AudioFrameBlake3Hex);
                    headerHash = Convert.FromHexString(fp.HeaderBlake3Hex);
                    headerSize = (long)fp.HeaderSize;
                    fsz = (long)fp.FileSize;
                    codec = analysis.Codec;
                    sampleRateHz = (int)analysis.SampleRateHz;
                    bitDepth = analysis.BitDepth.HasValue ? (int)analysis.BitDepth.Value : null;
                    bitrateKbps = analysis.BitrateKbps.HasValue ? (int)analysis.BitrateKbps.Value : null;
                    durationMs = (long)analysis.DurationMs;
                    rgTrack = analysis.ReplayGainTrackDb;
                    estBpm = analysis.EstimatedBpm;
                    tempoBucket = analysis.TempoBucket;
                }

                string trackUid = Convert.ToHexStringLower(audioHash);

                rgTrack ??= embedded.ReplayGainTrackDb;
                if (prevUidForRel is not null && !string.Equals(prevUidForRel, trackUid, StringComparison.Ordinal))
                {
                    using var delOld = conn.CreateCommand();
                    delOld.Transaction = tx;
                    delOld.CommandText = "DELETE FROM tracks WHERE track_uid = $oldUid AND rel_path = $rel;";
                    delOld.Parameters.AddWithValue("$oldUid", prevUidForRel);
                    delOld.Parameters.AddWithValue("$rel", rel);
                    delOld.ExecuteNonQuery();
                }

                using (var dedupAf = conn.CreateCommand())
                {
                    dedupAf.Transaction = tx;
                    dedupAf.CommandText = "DELETE FROM tracks WHERE audio_frame_blake3 = $af AND track_uid <> $uid;";
                    dedupAf.Parameters.AddWithValue("$af", audioHash);
                    dedupAf.Parameters.AddWithValue("$uid", trackUid);
                    dedupAf.ExecuteNonQuery();
                }

                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, bit_depth,
                        bitrate_kbps, duration_ms, title, album, album_artist, artists_json,
                        composers_json, conductors_json, work_name, movement_name, movement_number, disc_number, track_number, genre,
                        replaygain_track_db, replaygain_album_db, tempo_bpm, tempo_bucket
                    ) VALUES (
                        $uid, $af, $hsz, $hb,
                        $rel, $fsz, $mt, $cdc, $sr, $bd,
                        $br, $dur, $ti, $al, $aa, $ar,
                        $cmp, $cnd, $wk, $mvn, $mvi, $dsc, $trk, $gen,
                        $rg, $rga, $bpm, $bkt
                    )
                    ON CONFLICT(track_uid) DO UPDATE SET
                        audio_frame_blake3 = excluded.audio_frame_blake3,
                        header_size = excluded.header_size,
                        header_blake3 = excluded.header_blake3,
                        rel_path = excluded.rel_path,
                        file_size = excluded.file_size,
                        mtime_ms = excluded.mtime_ms,
                        codec = excluded.codec,
                        title = CASE WHEN length(trim(excluded.title)) > 0 THEN excluded.title ELSE tracks.title END,
                        album = CASE WHEN length(trim(excluded.album)) > 0 THEN excluded.album ELSE tracks.album END,
                        album_artist = COALESCE(excluded.album_artist, tracks.album_artist),
                        artists_json = CASE WHEN excluded.artists_json <> '[]' THEN excluded.artists_json ELSE tracks.artists_json END,
                        composers_json = CASE WHEN excluded.composers_json <> '[]' THEN excluded.composers_json ELSE tracks.composers_json END,
                        conductors_json = CASE WHEN excluded.conductors_json <> '[]' THEN excluded.conductors_json ELSE tracks.conductors_json END,
                        work_name = COALESCE(NULLIF(excluded.work_name, ''), tracks.work_name),
                        movement_name = COALESCE(NULLIF(excluded.movement_name, ''), tracks.movement_name),
                        movement_number = COALESCE(excluded.movement_number, tracks.movement_number),
                        disc_number = CASE WHEN excluded.disc_number > 0 THEN excluded.disc_number ELSE tracks.disc_number END,
                        track_number = CASE WHEN excluded.track_number > 0 THEN excluded.track_number ELSE tracks.track_number END,
                        genre = COALESCE(NULLIF(excluded.genre, ''), tracks.genre),
                        replaygain_track_db = COALESCE(excluded.replaygain_track_db, tracks.replaygain_track_db),
                        replaygain_album_db = COALESCE(excluded.replaygain_album_db, tracks.replaygain_album_db);
                    """;
                ins.Parameters.AddWithValue("$uid", trackUid);
                ins.Parameters.AddWithValue("$af", audioHash);
                ins.Parameters.AddWithValue("$hsz", headerSize);
                ins.Parameters.AddWithValue("$hb", headerHash);
                ins.Parameters.AddWithValue("$rel", rel);
                ins.Parameters.AddWithValue("$fsz", fsz);
                ins.Parameters.AddWithValue("$mt", mt);
                ins.Parameters.AddWithValue("$cdc", codec);
                ins.Parameters.AddWithValue("$sr", sampleRateHz);
                ins.Parameters.AddWithValue("$bd", bitDepth.HasValue ? bitDepth.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$br", bitrateKbps.HasValue ? bitrateKbps.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$dur", durationMs);
                ins.Parameters.AddWithValue("$ti", title);
                ins.Parameters.AddWithValue("$al", album);
                ins.Parameters.AddWithValue("$aa", (object?)albumArtist ?? DBNull.Value);
                ins.Parameters.AddWithValue("$ar", artistsJson);
                ins.Parameters.AddWithValue("$cmp", composersJson);
                ins.Parameters.AddWithValue("$cnd", conductorsJson);
                ins.Parameters.AddWithValue("$wk", (object?)workName ?? DBNull.Value);
                ins.Parameters.AddWithValue("$mvn", (object?)movementName ?? DBNull.Value);
                ins.Parameters.AddWithValue("$mvi", movementNumber.HasValue ? movementNumber.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$dsc", discNo);
                ins.Parameters.AddWithValue("$trk", trackNo);
                ins.Parameters.AddWithValue("$gen", (object?)genre ?? DBNull.Value);
                ins.Parameters.AddWithValue("$rg", rgTrack.HasValue ? rgTrack.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$rga", rgAlbum.HasValue ? rgAlbum.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$bpm", estBpm.HasValue ? estBpm.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$bkt", (object?)tempoBucket ?? DBNull.Value);
                ins.ExecuteNonQuery();
            }
            catch
            {
                // Skip unreadable/corrupt individual audio file cleanly
            }
        }

        foreach (var (rel, prev) in existingByRel)
        {
            if (seenRelPaths.Contains(rel)) continue;
            string expectedFull = Path.Combine(cleanRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!LongPathHelper.FileExists(expectedFull))
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM tracks WHERE track_uid = $uid;";
                del.Parameters.AddWithValue("$uid", prev.Uid);
                del.ExecuteNonQuery();
            }
        }
        tx.Commit();
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

    private static string? FindBookletPdf(string dir)
    {
        if (!LongPathHelper.DirectoryExists(dir)) return null;
        string? explicitBooklet = FindFirstExisting(dir, "booklet.pdf", "liner.pdf");
        if (explicitBooklet is not null) return explicitBooklet;
        try
        {
            return LongPathHelper.SafeEnumerateFiles(dir, "*.pdf", recursive: false).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

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
