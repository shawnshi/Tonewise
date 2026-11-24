using System.Collections.Specialized;
using Microsoft.Data.Sqlite;
using Tonewise.Desktop.Models;
using Tonewise.SyncHost.Curation;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class DesktopEditAndUndoTests
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

    private static byte[] BuildMinimalWav(int dataBytes = 4096)
    {
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVEfmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(8000);
        bw.Write(8000);
        bw.Write((short)1);
        bw.Write((short)8);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        bw.Write(new byte[dataBytes]);
        return ms.ToArray();
    }

    [Fact]
    public void ApplyDirectCuratedEdits_UpdatesMtimeMs_And_RollbackBatchDetailed_RoundtripsUndoRedo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_undo_test_" + Guid.NewGuid().ToString("N"));
        string tonewiseDir = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(tonewiseDir);
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        string audioRel = "Artist/track01.wav";
        string audioFull = Path.Combine(tempRoot, "Artist", "track01.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(audioFull)!);
        File.WriteAllBytes(audioFull, BuildMinimalWav());

        try
        {
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = schemaSql;
                setup.ExecuteNonQuery();

                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, artists_json, genre
                    ) VALUES (
                        'u_edit_1', X'01', 44, X'02', 0,
                        $rel, $fsize, 1000, 'wav', 8000, 512,
                        'Original Title', 'Original Album', '["Original Artist"]', 'Pop'
                    );
                    """;
                ins.Parameters.AddWithValue("$rel", audioRel);
                ins.Parameters.AddWithValue("$fsize", new FileInfo(audioFull).Length);
                ins.ExecuteNonQuery();
            }

            var store = new StagingDiffStore(dbPath);

            // Edit 1
            var res1 = store.ApplyDirectCuratedEdits(
                ["u_edit_1"],
                new Dictionary<string, string?> { ["title"] = "Edited Title 1", ["genre"] = "Rock" });
            Assert.Equal(2, res1.CommittedCount);
            Assert.Equal(1, res1.PhysicalFilesUpdated);

            // Verify mtime_ms in manifest.db was updated to match actual file mtime (no stale scan mismatch)
            long actualMtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(audioFull)).ToUnixTimeMilliseconds();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT title, genre, mtime_ms FROM tracks WHERE track_uid = 'u_edit_1';";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("Edited Title 1", reader.GetString(0));
                Assert.Equal("Rock", reader.GetString(1));
                long dbMtime = reader.GetInt64(2);
                Assert.True(Math.Abs(dbMtime - actualMtimeMs) <= 2000, $"Expected mtime_ms ~{actualMtimeMs}, got {dbMtime}");
            }

            // Edit 2 (multi-step undo chain)
            var res2 = store.ApplyDirectCuratedEdits(
                ["u_edit_1"],
                new Dictionary<string, string?> { ["title"] = "Edited Title 2" });
            Assert.Equal(1, res2.CommittedCount);

            // Undo Edit 2 -> back to Edited Title 1
            var rb2 = store.RollbackBatchDetailed(res2.BatchId);
            Assert.Equal(1, rb2.RolledBackCount);
            Assert.Equal(["u_edit_1"], rb2.AffectedTrackUids);
            Assert.Equal("Edited Title 2", rb2.ForwardUpdatesByTrack["u_edit_1"]["title"]);

            // Undo Edit 1 -> back to Original Title & Pop
            var rb1 = store.RollbackBatchDetailed(res1.BatchId);
            Assert.Equal(2, rb1.RolledBackCount);
            Assert.Equal("Edited Title 1", rb1.ForwardUpdatesByTrack["u_edit_1"]["title"]);
            Assert.Equal("Rock", rb1.ForwardUpdatesByTrack["u_edit_1"]["genre"]);

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT title, genre FROM tracks WHERE track_uid = 'u_edit_1';";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("Original Title", reader.GetString(0));
                Assert.Equal("Pop", reader.GetString(1));
            }

            // Redo Edit 1 using ForwardUpdatesByTrack
            var redo1 = store.ApplyDirectCuratedEdits(["u_edit_1"], rb1.ForwardUpdatesByTrack["u_edit_1"]);
            Assert.Equal(2, redo1.CommittedCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LibraryFileWatcherService_SuppressPathsTemporarily_SuppressesTargetOnlyInsideWindow()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_watch_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var watcher = new LibraryFileWatcherService(tempRoot, _ => Task.CompletedTask);
            string editedPath = Path.Combine(tempRoot, "Artist", "song.flac");
            string otherPath = Path.Combine(tempRoot, "Artist", "new.flac");

            watcher.SuppressPathsTemporarily([editedPath], durationMs: 4000);

            long insideWindow = DateTime.UtcNow.AddMilliseconds(500).Ticks;
            long outsideWindow = DateTime.UtcNow.AddMilliseconds(5000).Ticks;

            Assert.True(watcher.IsPathSuppressed(editedPath, insideWindow));
            Assert.False(watcher.IsPathSuppressed(otherPath, insideWindow));
            Assert.False(watcher.IsPathSuppressed(editedPath, outsideWindow));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BulkObservableCollection_IndexReplacement_EmitsReplaceNotReset()
    {
        var list = new BulkObservableCollection<TrackItem>
        {
            new("u1", "h1", "a.flac", "D:\\a.flac", "Song 1", "Album", "Artist", "", "", "", "Pop", 2020, 44100, 180000, null, [], null, null, null, null, [], 0, 0),
            new("u2", "h2", "b.flac", "D:\\b.flac", "Song 2", "Album", "Artist", "", "", "", "Pop", 2020, 44100, 180000, null, [], null, null, null, null, [], 0, 0),
        };

        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);

        list[1] = list[1] with { Title = "Song 2 Edited" };

        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Replace, actions[0]);
        Assert.Equal("Song 2 Edited", list[1].Title);
    }
}
