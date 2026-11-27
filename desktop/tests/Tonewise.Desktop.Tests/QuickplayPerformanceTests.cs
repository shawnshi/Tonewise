using System.Collections.ObjectModel;
using System.Diagnostics;
using Tonewise.Desktop.Models;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Workbench;
using Xunit;
using Xunit.Abstractions;

namespace Tonewise.Desktop.Tests;

public sealed class QuickplayPerformanceTests(ITestOutputHelper output)
{
    private static TrackItem MakeTrack(int i)
    {
        int albumIdx = i / 10;
        bool classical = i % 5 == 0;
        return new TrackItem(
            TrackUid: $"uid-{i}",
            AudioFrameBlake3Hex: $"blake-{i:D8}",
            RelPath: $"folder-{albumIdx % 50}/track-{i:D5}.flac",
            FullPath: $@"D:\Music\folder-{albumIdx % 50}\track-{i:D5}.flac",
            Title: classical ? $"I. Allegro {i}" : $"Song {i}",
            Album: $"Album {albumIdx}",
            ArtistsDisplay: classical ? "Berlin Philharmonic" : $"Artist {albumIdx % 100}",
            ComposersDisplay: classical ? $"Composer {albumIdx % 20}" : "",
            ConductorsDisplay: classical ? (albumIdx % 2 == 0 ? "Herbert von Karajan" : "Claudio Abbado") : "",
            WorkDisplay: classical ? $"Symphony No. {(albumIdx % 9) + 1} — Allegro {i % 4}" : "",
            Genre: classical ? "Classical" : "Jazz",
            Year: 1970 + (albumIdx % 50),
            SampleRateHz: 96000,
            DurationMs: 240_000,
            ReplayGainTrackDb: null,
            DjScene: [],
            DjVocals: null,
            DjEnergy: null,
            TempoBpm: null,
            TempoBucket: null,
            Moods: [],
            Rating: 4,
            LastPlayedTsMs: i % 3 == 0 ? 1_732_000_000_000L + i : 0L,
            WorkName: classical ? $"Symphony No. {(albumIdx % 9) + 1}" : "",
            MovementName: classical ? $"Allegro {i % 4}" : "",
            Publisher: "",
            EffectivePlayCount: i % 7,
            AlbumDirectory: $"folder-{albumIdx % 50}",
            CoverArtPath: $@"D:\Music\covers\{albumIdx}.jpg",
            BookletPdfPath: null,
            MovementNumber: classical ? (i % 4) + 1 : null,
            ReplayGainAlbumDb: null,
            DiscNumber: 1,
            TrackNumber: (i % 10) + 1,
            AlbumArtist: "",
            MtimeMs: 1_700_000_000_000L + i);
    }

    [Fact]
    public void QuickplayLookupIndex_MatchesLinearPinMatcher_AcrossAllPinKinds()
    {
        var tracks = Enumerable.Range(0, 500).Select(MakeTrack).ToList();
        var index = new QuickplayLookupIndex<TrackItem>(
            tracks,
            t => t.TrackUid,
            t => t.Album,
            t => t.WorkName,
            t => t.Genre,
            t => t.ArtistsDisplay,
            t => t.ComposersDisplay,
            t => t.ConductorsDisplay,
            t => t.AlbumDirectory,
            t => t.RelPath);

        string karajanKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
            "Symphony No. 1",
            "Album 0",
            WorkAwareShuffleEngine.BuildAlbumScope("Album 0", "Herbert von Karajan"))!;

        var pins = new[]
        {
            new QuickplayPinRecord("p1", "track", "uid-42", "Track 42", "", null, 0, 1),
            new QuickplayPinRecord("p2", "album", "Album 7", "Album 7", "", null, 1, 2),
            new QuickplayPinRecord("p3", "folder", "folder-3", "Folder 3", "", null, 2, 3),
            new QuickplayPinRecord("p4", "work", karajanKey, "Symphony No. 1", "Karajan", null, 3, 4),
            new QuickplayPinRecord("p5", "work", "Symphony No. 2", "Symphony No. 2", "", null, 4, 5),
            new QuickplayPinRecord("p6", "genre", "Classical", "Classical", "", null, 5, 6),
            new QuickplayPinRecord("p7", "entity", "Karajan", "Karajan", "", null, 6, 7),
        };

        foreach (var pin in pins)
        {
            var expected = tracks
                .Where(t => QuickplayPinMatcher.MatchesTrack(
                    pin,
                    t.TrackUid,
                    t.Album,
                    t.WorkName,
                    t.Genre,
                    t.ArtistsDisplay,
                    t.ComposersDisplay,
                    t.ConductorsDisplay,
                    t.AlbumDirectory,
                    t.RelPath))
                .Select(t => t.TrackUid)
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToList();

            var actual = index.MatchTracks(pin)
                .Select(t => t.TrackUid)
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToList();

            Assert.NotEmpty(actual);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void QuickplayTileCollectionHelper_ApplyDiff_PreservesUnchangedInstances_AndMinimizesEvents()
    {
        var t1 = MakeTrack(1);
        var t2 = MakeTrack(2);
        var t3 = MakeTrack(3);

        var tileA1 = new QuickplayTileItem("history::a", "album", "a", "Album A", "Artist A", "1 首", "a.jpg", [t1]);
        var tileB1 = new QuickplayTileItem("history::b", "album", "b", "Album B", "Artist B", "1 首", "b.jpg", [t2]);
        var dest = new ObservableCollection<QuickplayTileItem> { tileA1, tileB1 };

        int changedEvents = 0;
        dest.CollectionChanged += (_, _) => changedEvents++;

        // Identical second snapshot (with fresh QuickplayTileItem wrappers) should emit 0 events.
        var tileA2 = tileA1 with { Tracks = [t1] };
        var tileB2 = tileB1 with { Tracks = [t2] };
        int diffCount = QuickplayTileCollectionHelper.ApplyDiff(dest, [tileA2, tileB2]);

        Assert.Equal(0, diffCount);
        Assert.Equal(0, changedEvents);
        Assert.Same(tileA1, dest[0]);
        Assert.Same(tileB1, dest[1]);

        // Modifying only slot 0 should emit exactly 1 Replace event and keep slot 1 intact.
        var tileA3 = tileA1 with { Tracks = [t1, t3], BadgeText = "2 首" };
        diffCount = QuickplayTileCollectionHelper.ApplyDiff(dest, [tileA3, tileB2]);
        Assert.Equal(1, diffCount);
        Assert.Equal(1, changedEvents);
        Assert.Same(tileA3, dest[0]);
        Assert.Same(tileB1, dest[1]);
    }

    [Fact]
    public void QuickplayUiApplyDiff_10kTracks_12Pins_StaysUnderFrameBudget()
    {
        var tracks = Enumerable.Range(0, 10_000).Select(MakeTrack).ToList();
        var index = new QuickplayLookupIndex<TrackItem>(
            tracks,
            t => t.TrackUid,
            t => t.Album,
            t => t.WorkName,
            t => t.Genre,
            t => t.ArtistsDisplay,
            t => t.ComposersDisplay,
            t => t.ConductorsDisplay,
            t => t.AlbumDirectory,
            t => t.RelPath);

        var pins = Enumerable.Range(0, 12)
            .Select(i => new QuickplayPinRecord($"pin::{i}", "album", $"Album {i * 10}", $"Album {i * 10}", "Artist", null, i, i))
            .ToList();

        var resolvedPins = pins
            .Select(p =>
            {
                var matched = index.MatchTracks(p).ToList();
                return new QuickplayTileItem(p.PinId, p.Kind, p.TargetKey, p.Title, p.Subtitle, $"{matched.Count} 首", null, matched, IsPinned: true);
            })
            .ToList();

        var history = Enumerable.Range(0, 8)
            .Select(i => new QuickplayTileItem($"h::{i}", "album", $"Album {i}", $"Album {i}", "Artist", "10 首", null, tracks.GetRange(i * 10, 10)))
            .ToList();

        var destPins = new ObservableCollection<QuickplayTileItem>(resolvedPins);
        var destHistory = new ObservableCollection<QuickplayTileItem>(history);

        // Warmup
        QuickplayTileCollectionHelper.ApplyDiff(destPins, resolvedPins);
        QuickplayTileCollectionHelper.ApplyDiff(destHistory, history);

        var samplesUs = new List<double>(20);
        for (int run = 0; run < 20; run++)
        {
            var updatedHistory = history.ToList();
            updatedHistory[0] = updatedHistory[0] with { BadgeText = $"{10 + run} 首" };

            long t0 = Stopwatch.GetTimestamp();
            QuickplayTileCollectionHelper.ApplyDiff(destPins, resolvedPins);
            QuickplayTileCollectionHelper.ApplyDiff(destHistory, updatedHistory);
            long t1 = Stopwatch.GetTimestamp();
            samplesUs.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
        }

        samplesUs.Sort();
        double p50Ms = samplesUs[samplesUs.Count / 2] / 1000.0;
        double p95Ms = samplesUs[(int)Math.Ceiling(samplesUs.Count * 0.95) - 1] / 1000.0;
        output.WriteLine($"Quickplay UI Diff Apply (10k tracks, 12 pins): p50={p50Ms:F3} ms, p95={p95Ms:F3} ms");

        Assert.True(p50Ms < 4.0, $"Expected p50 < 4.0 ms, got {p50Ms:F3} ms");
        Assert.True(p95Ms < 10.0, $"Expected p95 < 10.0 ms, got {p95Ms:F3} ms");
    }
}
