using System.Collections.Specialized;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using Tonewise.Audio;
using Tonewise.Desktop.Models;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Playlists;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class Phase3ArchitectureTests
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

    [Fact]
    public void ManifestSchemaProvider_MatchesProtocolSchemaSql()
    {
        string embedded = ManifestSchemaProvider.ManifestSql.Replace("\r\n", "\n");
        string disk = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"))
            .Replace("\r\n", "\n");

        Assert.Contains("CREATE TABLE IF NOT EXISTS tracks", embedded);
        Assert.Contains("CREATE TABLE IF NOT EXISTS schema_meta", embedded);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_tracks_rel_path", embedded);
        Assert.Equal(disk, embedded);
    }

    [Fact]
    public void PlaylistTrackMatcher_LibraryIndex_MatchesUnindexedAcrossMultiplePlaylists()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_plidx_test_" + Guid.NewGuid().ToString("N"));
        string playlistsDir = Path.Combine(tempRoot, "Playlists");
        Directory.CreateDirectory(playlistsDir);

        try
        {
            var library = new List<LocalLibraryTrackRef>
            {
                new("uid_1", "ArtistA/Album1/01 Song One.flac", Path.Combine(tempRoot, "ArtistA/Album1/01 Song One.flac"),
                    "Song One", "Artist A", "Album 1", 210_000, 44100),
                new("uid_2", "ArtistB/Album2/02 Nocturne (Remastered 2011).flac", Path.Combine(tempRoot, "ArtistB/Album2/02 Nocturne (Remastered 2011).flac"),
                    "Nocturne (Remastered 2011)", "Artist B", "Album 2", 185_000, 96000),
                new("uid_3", "ArtistC/Album3/03 Finale.flac", Path.Combine(tempRoot, "ArtistC/Album3/03 Finale.flac"),
                    "Finale", "Artist C", "Album 3", 300_000, 48000),
            };

            string p1 = Path.Combine(playlistsDir, "Focus Mix.m3u8");
            File.WriteAllText(p1, """
                #EXTM3U
                #PLAYLIST:Focus Mix
                ArtistA/Album1/01 Song One.flac
                #EXTINF:185,Artist B - Nocturne
                #EXTTW-UNMATCHED:Artist B	Nocturne	Album 2
                #EXTINF:200,Missing Artist - Missing Track
                #EXTTW-UNMATCHED:Missing Artist	Missing Track	Missing Album
                """);

            string p2 = Path.Combine(playlistsDir, "Late Night.m3u8");
            File.WriteAllText(p2, """
                #EXTM3U
                #PLAYLIST:Late Night
                ArtistC/Album3/03 Finale.flac
                ArtistA/Album1/01 Song One.flac
                """);

            var index = PlaylistTrackMatcher.BuildIndex(library);

            var unindexedList = PlaylistStore.ListPlaylists(tempRoot, library);
            var indexedList = PlaylistStore.ListPlaylists(tempRoot, index);
            Assert.Equal(unindexedList, indexedList);

            var docUnindexed = PlaylistStore.LoadPlaylist(p1, library, tempRoot);
            var docIndexed = PlaylistStore.LoadPlaylist(p1, index, tempRoot);
            Assert.Equal(docUnindexed.Name, docIndexed.Name);
            Assert.Equal(docUnindexed.Entries, docIndexed.Entries);
            Assert.Equal(2, docIndexed.Entries.Count(e => e.IsLocalMatched));
            Assert.Equal(3, docIndexed.Entries.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void SyncOutboxAndPairingStore_TryResolveTrackPhysicalPath_ResolvesByBlake3HexCaseInsensitive_AndUsesIndex()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_blobidx_test_" + Guid.NewGuid().ToString("N"));
        string dotDir = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotDir);
        string audioRel = "Artist/Album/01 Track.flac";
        string audioFull = Path.Combine(tempRoot, "Artist", "Album", "01 Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audioFull)!);
        File.WriteAllBytes(audioFull, [0x66, 0x4C, 0x61, 0x43, 0, 1, 2, 3]);

        byte[] audioHash = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
        byte[] headerHash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        string lowerHex = Convert.ToHexStringLower(audioHash);
        string upperHex = Convert.ToHexString(audioHash);

        try
        {
            string dbPath = Path.Combine(dotDir, "manifest.db");
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using (var schemaCmd = conn.CreateCommand())
                {
                    schemaCmd.CommandText = ManifestSchemaProvider.ManifestSql;
                    schemaCmd.ExecuteNonQuery();
                }

                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = """
                        INSERT INTO tracks (
                            track_uid, audio_frame_blake3, header_size, header_blake3,
                            rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                        ) VALUES (
                            $uid, $af, 128, $hb,
                            $rel, 8, 1763251200000, 'flac', 44100, 1000, 'Test Track'
                        );
                        """;
                    ins.Parameters.AddWithValue("$uid", "legacy-mbid-uid-001");
                    ins.Parameters.AddWithValue("$af", audioHash);
                    ins.Parameters.AddWithValue("$hb", headerHash);
                    ins.Parameters.AddWithValue("$rel", audioRel);
                    ins.ExecuteNonQuery();
                }

                using var explain = conn.CreateCommand();
                explain.CommandText = "EXPLAIN QUERY PLAN SELECT rel_path FROM tracks WHERE track_uid = $uid OR audio_frame_blake3 = $b3 LIMIT 1;";
                explain.Parameters.AddWithValue("$uid", upperHex);
                explain.Parameters.AddWithValue("$b3", audioHash);
                var planLines = new List<string>();
                using var reader = explain.ExecuteReader();
                while (reader.Read())
                {
                    planLines.Add(reader.GetString(3));
                }
                string fullPlan = string.Join("\n", planLines);
                Assert.Contains("SEARCH tracks USING", fullPlan);
                Assert.DoesNotContain("SCAN tracks", fullPlan);
            }

            var store = new SyncOutboxAndPairingStore(tempRoot);

            Assert.True(store.TryResolveTrackPhysicalPath("legacy-mbid-uid-001", out string p1, out string r1));
            Assert.Equal(audioRel, r1);
            Assert.Equal(Path.GetFullPath(audioFull), Path.GetFullPath(p1));

            Assert.True(store.TryResolveTrackPhysicalPath(lowerHex, out _, out string r2));
            Assert.Equal(audioRel, r2);

            Assert.True(store.TryResolveTrackPhysicalPath(upperHex, out _, out string r3));
            Assert.Equal(audioRel, r3);

            Assert.False(store.TryResolveTrackPhysicalPath("nonexistent", out _, out _));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void BulkObservableCollection_ReplaceAllAddRangeInsertRange_EmitsSingleResetPerBatch()
    {
        var col = new BulkObservableCollection<string>();
        var events = new List<NotifyCollectionChangedAction>();
        col.CollectionChanged += (_, e) => events.Add(e.Action);

        col.ReplaceAll(["b", "c", "e"]);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
        Assert.Equal(["b", "c", "e"], col);

        events.Clear();
        col.AddRange(["f", "g"]);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
        Assert.Equal(["b", "c", "e", "f", "g"], col);

        events.Clear();
        col.InsertRange(0, ["a"]);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
        Assert.Equal(["a", "b", "c", "e", "f", "g"], col);

        events.Clear();
        col.InsertRange(3, ["d1", "d2"]);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
        Assert.Equal(["a", "b", "c", "d1", "d2", "e", "f", "g"], col);
    }

    [Fact]
    public void DspSampleProvider_GainMatchedBypass_TracksConfigAndReplayGainUpdates()
    {
        const string apo = """
            Preamp: -3.0 dB
            Filter: ON PK Fc 1000 Hz Gain 3.0 dB Q 1.0
            """;
        var profile = EqualizerApoProfile.Parse(apo);
        var source = new RepeatingFloatProvider(0.25f);
        var dsp = new DspSampleProvider(source) { IsTpdfDitherEnabled = false };

        dsp.Configure(profile, replayGainDb: -2.0);
        dsp.ConfigureExtendedDsp(
            gainMatchedBypass: true,
            rmsShiftDbOverride: 1.5,
            crossfeedEnabled: false,
            crossfeedCutoffHz: 700.0,
            crossfeedFeedDb: 4.5,
            crossfeedDelayMs: 0.28,
            balanceDb: 0.0,
            monoFold: false,
            limiterEnabled: false);

        float[] buf1 = new float[16];
        Assert.Equal(16, dsp.Read(buf1, 0, 16));
        double expected1 = 0.25 * Math.Pow(10.0, (-2.0 + 1.5) / 20.0);
        Assert.InRange(buf1[0], expected1 - 1e-4, expected1 + 1e-4);

        dsp.SetReplayGainDb(-4.0);
        float[] buf2 = new float[16];
        Assert.Equal(16, dsp.Read(buf2, 0, 16));
        double expected2 = 0.25 * Math.Pow(10.0, (-4.0 + 1.5) / 20.0);
        Assert.InRange(buf2[0], expected2 - 1e-4, expected2 + 1e-4);

        dsp.ConfigureExtendedDsp(
            gainMatchedBypass: true,
            rmsShiftDbOverride: 0.0,
            crossfeedEnabled: false,
            crossfeedCutoffHz: 700.0,
            crossfeedFeedDb: 4.5,
            crossfeedDelayMs: 0.28,
            balanceDb: 0.0,
            monoFold: false,
            limiterEnabled: false);
        float[] buf3 = new float[16];
        Assert.Equal(16, dsp.Read(buf3, 0, 16));
        double expected3 = 0.25 * Math.Pow(10.0, -4.0 / 20.0);
        Assert.InRange(buf3[0], expected3 - 1e-4, expected3 + 1e-4);
    }

    private sealed class RepeatingFloatProvider(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, value, offset, count);
            return count;
        }
    }
}
