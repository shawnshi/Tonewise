using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Desktop.Models;
using Tonewise.Interop;

namespace Tonewise.Desktop.Services;

public sealed class LibraryStore
{
    public string? MusicRoot { get; private set; }
    public string? WaveformsBinPath { get; private set; }

    public IReadOnlyList<TrackItem> LoadFromMusicRoot(string musicRoot)
    {
        MusicRoot = musicRoot;
        string tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        string wfPath = Path.Combine(tonewiseDir, "waveforms.bin");
        WaveformsBinPath = File.Exists(wfPath) ? wfPath : null;

        if (!File.Exists(dbPath))
        {
            return [];
        }

        var items = new List<TrackItem>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                track_uid, audio_frame_blake3, rel_path, title, album,
                artists_json, composers_json, conductors_json, work_name, movement_name,
                genre, year, sample_rate_hz, duration_ms, replaygain_track_db,
                dj_scene_json, dj_vocals, dj_energy, tempo_bpm, tempo_bucket, mood_json,
                rating, last_played_ts
            FROM tracks
            ORDER BY album, rel_path
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            byte[] blake3Bytes = (byte[])reader["audio_frame_blake3"];
            string hex = Convert.ToHexStringLower(blake3Bytes);
            string relPath = reader.GetString(2);
            string fullPath = Path.Combine(musicRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

            var artists = ParseStringArray(reader.GetString(5));
            var composers = ParseStringArray(reader.GetString(6));
            var conductors = ParseStringArray(reader.GetString(7));
            string workName = reader.IsDBNull(8) ? "" : reader.GetString(8);
            string movement = reader.IsDBNull(9) ? "" : reader.GetString(9);
            string workDisplay = string.IsNullOrEmpty(workName)
                ? ""
                : (string.IsNullOrEmpty(movement) ? workName : $"{workName} — {movement}");

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
                    LastPlayedTsMs: reader.IsDBNull(22) ? 0 : reader.GetInt64(22)
                )
            );
        }

        return items;
    }

    public float[]? LoadWaveform(TrackItem track)
    {
        if (WaveformsBinPath is null || !File.Exists(WaveformsBinPath))
        {
            return null;
        }
        return TonewiseCoreClient.LookupWaveform(WaveformsBinPath, track.AudioFrameBlake3Hex);
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
