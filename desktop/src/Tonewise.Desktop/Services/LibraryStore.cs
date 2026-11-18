using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Desktop.Models;
using Tonewise.Interop;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;

namespace Tonewise.Desktop.Services;

public sealed class LibraryStore
{
    private static readonly string[] AudioExtensions =
        [".flac", ".mp3", ".wav", ".m4a", ".alac", ".aac", ".ogg", ".opus"];

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
        if (string.IsNullOrWhiteSpace(musicRoot) || !Directory.Exists(musicRoot))
            return false;

        string tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        if (!forceRebuild && HasPopulatedManifest(dbPath))
            return false;

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
        BuildManifestWithFfiFallback(musicRoot, dbPath);
        return HasPopulatedManifest(dbPath);
    }

    public async Task<bool> ScanAndBuildManifestIfMissingAsync(
        string musicRoot,
        bool forceRebuild = false,
        bool fastMode = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(musicRoot) || !Directory.Exists(musicRoot))
            return false;

        string tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        if (!forceRebuild && HasPopulatedManifest(dbPath))
            return false;

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
            BuildManifestWithFfiFallback(musicRoot, dbPath);
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
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
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
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

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
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
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
                {(hasLedgerCols ? "local_unsynced_expired_count" : "0")} AS local_unsynced_expired_count
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
                    BookletPdfPath: assets.Booklet
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

    private static void BuildManifestWithFfiFallback(string musicRoot, string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using (var init = conn.CreateCommand())
        {
            init.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS tracks (
                    track_uid TEXT PRIMARY KEY,
                    audio_frame_blake3 BLOB NOT NULL UNIQUE,
                    header_size INTEGER NOT NULL DEFAULT 0,
                    header_blake3 BLOB NOT NULL,
                    padding_bytes INTEGER NOT NULL DEFAULT 0,
                    rel_path TEXT NOT NULL,
                    file_size INTEGER NOT NULL,
                    mtime_ms INTEGER NOT NULL,
                    codec TEXT NOT NULL,
                    sample_rate_hz INTEGER NOT NULL,
                    bit_depth INTEGER,
                    bitrate_kbps INTEGER,
                    duration_ms INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    album TEXT,
                    album_artist TEXT,
                    artists_json TEXT NOT NULL DEFAULT '[]',
                    composers_json TEXT NOT NULL DEFAULT '[]',
                    conductors_json TEXT NOT NULL DEFAULT '[]',
                    work_name TEXT,
                    movement_name TEXT,
                    movement_number INTEGER,
                    genre TEXT,
                    year INTEGER,
                    original_date TEXT,
                    release_type TEXT,
                    replaygain_track_db REAL,
                    replaygain_album_db REAL,
                    replaygain_peak REAL,
                    dj_scene_json TEXT NOT NULL DEFAULT '[]',
                    dj_scene_ts INTEGER NOT NULL DEFAULT 0,
                    dj_vocals TEXT,
                    dj_vocals_ts INTEGER NOT NULL DEFAULT 0,
                    dj_energy TEXT,
                    dj_energy_ts INTEGER NOT NULL DEFAULT 0,
                    tempo_bpm REAL,
                    tempo_bucket TEXT,
                    tempo_ts INTEGER NOT NULL DEFAULT 0,
                    mood_json TEXT NOT NULL DEFAULT '[]',
                    mood_ts INTEGER NOT NULL DEFAULT 0,
                    rating INTEGER DEFAULT 0,
                    rating_ts INTEGER NOT NULL DEFAULT 0,
                    play_count_total INTEGER NOT NULL DEFAULT 0,
                    last_played_ts INTEGER NOT NULL DEFAULT 0,
                    waveform_offset INTEGER
                );
                """;
            init.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        foreach (string file in Directory.EnumerateFiles(musicRoot, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal)) continue;

            try
            {
                var fp = TonewiseCoreClient.ComputeFingerprint(file);
                var analysis = TonewiseCoreClient.AnalyzeAudioFile(file);
                string rel = Path.GetRelativePath(musicRoot, file).Replace('\\', '/');
                string title = Path.GetFileNameWithoutExtension(file);
                string album = new DirectoryInfo(Path.GetDirectoryName(file)!).Name;

                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR IGNORE INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, bit_depth,
                        bitrate_kbps, duration_ms, title, album, replaygain_track_db,
                        tempo_bpm, tempo_bucket
                    ) VALUES (
                        $uid, $af, $hsz, $hb,
                        $rel, $fsz, $mt, $cdc, $sr, $bd,
                        $br, $dur, $ti, $al, $rg,
                        $bpm, $bkt
                    );
                    """;
                ins.Parameters.AddWithValue("$uid", fp.AudioFrameBlake3Hex);
                ins.Parameters.AddWithValue("$af", Convert.FromHexString(fp.AudioFrameBlake3Hex));
                ins.Parameters.AddWithValue("$hsz", (long)fp.HeaderSize);
                ins.Parameters.AddWithValue("$hb", Convert.FromHexString(fp.HeaderBlake3Hex));
                ins.Parameters.AddWithValue("$rel", rel);
                ins.Parameters.AddWithValue("$fsz", (long)fp.FileSize);
                ins.Parameters.AddWithValue("$mt", new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds());
                ins.Parameters.AddWithValue("$cdc", analysis.Codec);
                ins.Parameters.AddWithValue("$sr", (int)analysis.SampleRateHz);
                ins.Parameters.AddWithValue("$bd", analysis.BitDepth.HasValue ? (int)analysis.BitDepth.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$br", analysis.BitrateKbps.HasValue ? (int)analysis.BitrateKbps.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$dur", (long)analysis.DurationMs);
                ins.Parameters.AddWithValue("$ti", title);
                ins.Parameters.AddWithValue("$al", album);
                ins.Parameters.AddWithValue("$rg", analysis.ReplayGainTrackDb.HasValue ? analysis.ReplayGainTrackDb.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$bpm", analysis.EstimatedBpm.HasValue ? analysis.EstimatedBpm.Value : DBNull.Value);
                ins.Parameters.AddWithValue("$bkt", (object?)analysis.TempoBucket ?? DBNull.Value);
                ins.ExecuteNonQuery();
            }
            catch
            {
                // Skip unreadable/corrupt individual audio file cleanly
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
        if (!Directory.Exists(dir)) return null;
        foreach (string n in names)
        {
            string p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? FindBookletPdf(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        string? explicitBooklet = FindFirstExisting(dir, "booklet.pdf", "liner.pdf");
        if (explicitBooklet is not null) return explicitBooklet;
        try
        {
            return Directory.EnumerateFiles(dir, "*.pdf").FirstOrDefault();
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
