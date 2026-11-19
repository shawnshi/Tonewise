using Tonewise.Desktop.Models;
using Tonewise.Desktop.Services;
using Tonewise.Interop;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class ClassicalPlaylistAndPlaybackTests
{
    [Fact]
    public void AutoDj_KeepsMultiMovementSymphonyAtomicAcrossPoolsAndSmoothing()
    {
        var candidates = new List<AutoDjTrackCandidate>
        {
            // Beethoven Symphony No. 5 across 4 movements with conflicting play counts and tempos,
            // same conductor (normally maxPerArtist = 2 would chop off movements 3 & 4).
            new(
                "sym_m3",
                "Symphony No. 5 - III. Allegro",
                "Herbert von Karajan",
                EffectivePlayCount: 0,
                Rating: 5,
                TempoBpm: 145,
                TempoBucket: "Fast",
                DjEnergy: "high",
                DjVocals: "instrumental",
                ["focus"],
                ["Energetic"],
                WorkKey: "symphony no. 5 (karajan)|herbert von karajan::symphony no. 5",
                MovementNumber: 3,
                DiscNumber: 1,
                TrackNumber: 3,
                RelPath: "Karajan/03.flac"),
            new(
                "sym_m1",
                "Symphony No. 5 - I. Allegro con brio",
                "Herbert von Karajan",
                EffectivePlayCount: 8,
                Rating: 5,
                TempoBpm: 108,
                TempoBucket: "Moderate",
                DjEnergy: "high",
                DjVocals: "instrumental",
                ["focus"],
                ["Dramatic"],
                WorkKey: "symphony no. 5 (karajan)|herbert von karajan::symphony no. 5",
                MovementNumber: 1,
                DiscNumber: 1,
                TrackNumber: 1,
                RelPath: "Karajan/01.flac"),
            new(
                "sym_m2",
                "Symphony No. 5 - II. Andante con moto",
                "Herbert von Karajan",
                EffectivePlayCount: 1,
                Rating: 5,
                TempoBpm: 62,
                TempoBucket: "Slow",
                DjEnergy: "low",
                DjVocals: "instrumental",
                ["focus"],
                ["Calm"],
                WorkKey: "symphony no. 5 (karajan)|herbert von karajan::symphony no. 5",
                MovementNumber: 2,
                DiscNumber: 1,
                TrackNumber: 2,
                RelPath: "Karajan/02.flac"),
            new(
                "sym_m4",
                "Symphony No. 5 - IV. Allegro",
                "Herbert von Karajan",
                EffectivePlayCount: 4,
                Rating: 5,
                TempoBpm: 132,
                TempoBucket: "Fast",
                DjEnergy: "high",
                DjVocals: "instrumental",
                ["focus"],
                ["Triumphant"],
                WorkKey: "symphony no. 5 (karajan)|herbert von karajan::symphony no. 5",
                MovementNumber: 4,
                DiscNumber: 1,
                TrackNumber: 4,
                RelPath: "Karajan/04.flac"),
            new("pop_1", "Pop One", "Artist A", 5, 4, 100, "Moderate", "medium", "vocal", ["pop"], ["Calm"]),
            new("pop_2", "Pop Two", "Artist B", 5, 4, 102, "Moderate", "medium", "vocal", ["pop"], ["Calm"]),
            new("pop_3", "Pop Three", "Artist C", 0, 4, 98, "Moderate", "medium", "vocal", ["pop"], ["Calm"]),
            new("pop_4", "Pop Four", "Artist D", 0, 4, 104, "Moderate", "medium", "vocal", ["pop"], ["Calm"]),
        };

        var result = AutoDjPlaylistGenerator.Generate(
            candidates,
            new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Local),
            targetCount: 8,
            randomSeed: 7);

        Assert.Equal(8, result.OrderedTrackUids.Count);
        int m1Pos = result.OrderedTrackUids.ToList().IndexOf("sym_m1");
        Assert.True(m1Pos >= 0);
        Assert.Equal(
            new[] { "sym_m1", "sym_m2", "sym_m3", "sym_m4" },
            result.OrderedTrackUids.Skip(m1Pos).Take(4).ToArray());
    }

    [Fact]
    public void ReplayGainDynamic_PrefersAlbumGainWithinContiguousClassicalWorkEvenWhenShuffled()
    {
        var workAlb = "Beethoven 5 (Karajan)";
        var workKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
            "Symphony No. 5", workAlb, WorkAwareShuffleEngine.BuildAlbumScope(workAlb, "Karajan"));

        var queue = new[]
        {
            (Album: "Pop Singles", WorkKey: (string?)null),
            (Album: workAlb, WorkKey: workKey),
            (Album: workAlb, WorkKey: workKey),
            (Album: "Rock Mix", WorkKey: (string?)null),
        };

        // Playing Mvt 1 inside WorkShuffle (isShuffleEnabled = true) -> uses Album Gain
        Assert.True(ReplayGainResolver.ShouldUseAlbumReplayGain(
            isShuffleEnabled: true,
            queueCount: queue.Length,
            currentIndex: 1,
            albumAt: i => queue[i].Album,
            workKeyAt: i => queue[i].WorkKey,
            currentAlbum: workAlb,
            currentWorkKey: workKey));

        // Playing standalone Pop Singles in mixed queue -> uses Track Gain
        Assert.False(ReplayGainResolver.ShouldUseAlbumReplayGain(
            isShuffleEnabled: true,
            queueCount: queue.Length,
            currentIndex: 0,
            albumAt: i => queue[i].Album,
            workKeyAt: i => queue[i].WorkKey,
            currentAlbum: "Pop Singles",
            currentWorkKey: null));

        // Playing single pop album unshuffled -> Album Gain; shuffled -> Track Gain
        var singlePopAlbum = new[] { ("Abbey Road", (string?)null), ("Abbey Road", (string?)null) };
        Assert.True(ReplayGainResolver.ShouldUseAlbumReplayGain(
            false, 2, 0, i => singlePopAlbum[i].Item1, i => singlePopAlbum[i].Item2, "Abbey Road", null));
        Assert.False(ReplayGainResolver.ShouldUseAlbumReplayGain(
            true, 2, 0, i => singlePopAlbum[i].Item1, i => singlePopAlbum[i].Item2, "Abbey Road", null));
    }

    [Fact]
    public void WorkAwareShuffleEngine_SeparatesIdenticalAlbumTitlesByConductorAndOrdersByDiscAndNaturalPath()
    {
        string scopeKarajan = WorkAwareShuffleEngine.BuildAlbumScope("Symphony No. 5", "Herbert von Karajan");
        string scopeBernstein = WorkAwareShuffleEngine.BuildAlbumScope("Symphony No. 5", "Leonard Bernstein");
        Assert.NotEqual(scopeKarajan, scopeBernstein);

        var list = new List<ShuffleTrackDescriptor>
        {
            new("k_10", "Symphony No. 5", "Symphony No. 5", MovementNumber: null, "CD2/10 Finale.flac", scopeKarajan, DiscNumber: 2, TrackNumber: 10),
            new("k_2", "Symphony No. 5", "Symphony No. 5", MovementNumber: null, "CD2/2 Scherzo.flac", scopeKarajan, DiscNumber: 2, TrackNumber: 2),
            new("k_1", "Symphony No. 5", "Symphony No. 5", MovementNumber: null, "CD1/01 Allegro.flac", scopeKarajan, DiscNumber: 1, TrackNumber: 1),
            new("b_1", "Symphony No. 5", "Symphony No. 5", MovementNumber: 1, "Bernstein/01.flac", scopeBernstein, DiscNumber: 1, TrackNumber: 1),
            new("b_2", "Symphony No. 5", "Symphony No. 5", MovementNumber: 2, "Bernstein/02.flac", scopeBernstein, DiscNumber: 1, TrackNumber: 2),
        };

        int[] order = WorkAwareShuffleEngine.BuildWorkAwareOrder(list, anchorIndex: 2, new Random(19));
        var uids = order.Select(i => list[i].TrackUid).ToArray();

        // Anchor block (Karajan) stays together and in Disc -> NaturalPath/Track order (1 -> 2 -> 10),
        // strictly separate from Bernstein's identically titled album.
        Assert.Equal(new[] { "k_1", "k_2", "k_10", "b_1", "b_2" }, uids);
    }

    [Fact]
    public void ClassicalAbSwitch_MatchesByMovementNumberAndReplacesWorkBlockInQueue()
    {
        var queue = new[]
        {
            new TrackItem("pop0", "h0", "p.flac", "/p.flac", "Intro Pop", "Alb0", "Pop", "", "", "", "Pop", 2020, 44100, 180_000, null, [], null, null, null, null, [], 0, 0),
            new TrackItem("k_m1", "h1", "k1.flac", "/k1.flac", "I. Allegro", "Beethoven 5 (Karajan)", "BPO", "Beethoven", "Karajan", "Sym 5 — I", "Classical", 1963, 44100, 420_000, null, [], null, null, null, null, [], 5, 0, WorkName: "Symphony No. 5", MovementName: "I. Allegro", MovementNumber: 1),
            new TrackItem("k_m2", "h2", "k2.flac", "/k2.flac", "II. Andante", "Beethoven 5 (Karajan)", "BPO", "Beethoven", "Karajan", "Sym 5 — II", "Classical", 1963, 44100, 600_000, null, [], null, null, null, null, [], 5, 0, WorkName: "Symphony No. 5", MovementName: "II. Andante", MovementNumber: 2),
            new TrackItem("pop1", "h3", "p1.flac", "/p1.flac", "Outro Pop", "Alb1", "Pop", "", "", "", "Pop", 2021, 44100, 200_000, null, [], null, null, null, null, [], 0, 0),
        };
        var bernsteinWork = new[]
        {
            new TrackItem("b_m1", "h4", "b1.flac", "/b1.flac", "I. Allegro con brio", "Beethoven 5 (Bernstein)", "VPO", "Beethoven", "Bernstein", "Sym 5 — I", "Classical", 1980, 44100, 440_000, null, [], null, null, null, null, [], 5, 0, WorkName: "Symphony No. 5", MovementName: "I. Allegro con brio", MovementNumber: 1),
            new TrackItem("b_m2", "h5", "b2.flac", "/b2.flac", "II. Andante con moto", "Beethoven 5 (Bernstein)", "VPO", "Beethoven", "Bernstein", "Sym 5 — II", "Classical", 1980, 44100, 610_000, null, [], null, null, null, null, [], 5, 0, WorkName: "Symphony No. 5", MovementName: "II. Andante con moto", MovementNumber: 2),
        };

        var library = queue.Concat(bernsteinWork).ToList();
        var alts = ClassicalAbSwitchHelper.FindAlternateRecordings(
            queue[2],
            library,
            t => t.TrackUid,
            t => t.WorkName,
            t => t.MovementName,
            t => t.MovementNumber,
            t => WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay));

        Assert.Single(alts);
        Assert.Equal("b_m2", alts[0].TrackUid);

        var (updated, newIdx) = ClassicalAbSwitchHelper.ReplaceWorkBlockInQueue(
            queue,
            currentIndex: 2,
            targetTrack: alts[0],
            targetWorkOrderedTracks: bernsteinWork,
            t => t.TrackUid,
            t => t.WorkName,
            t => WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay));

        Assert.Equal(2, newIdx);
        Assert.Equal(new[] { "pop0", "b_m1", "b_m2", "pop1" }, updated.Select(t => t.TrackUid).ToArray());
    }

    [Fact]
    public void TrackItem_ClassifyIsClassical_DoesNotMisclassifyPopOrSoundtrackWithComposer()
    {
        Assert.False(TrackItem.ClassifyIsClassical("Pop", "", "Max Martin", ""));
        Assert.False(TrackItem.ClassifyIsClassical("Anime; Soundtrack", "", "Hiroyuki Sawano", ""));
        Assert.True(TrackItem.ClassifyIsClassical("Classical", "", "", ""));
        Assert.True(TrackItem.ClassifyIsClassical("Pop", "Goldberg Variations", "J.S. Bach", ""));
        Assert.True(TrackItem.ClassifyIsClassical("", "", "Johannes Brahms", ""));
        Assert.True(TrackItem.ClassifyIsClassical("Orchestral", "", "", "Carlos Kleiber"));
    }

    [Fact]
    public void PlaylistMatcher_And_PathParser_HandleClassicalFormats()
    {
        Assert.Equal((2, 3), LibraryStore.ParseDiscAndTrackFromRelPath("Beethoven/CD2/03 Scherzo.flac"));
        Assert.Equal((1, 2), LibraryStore.ParseDiscAndTrackFromRelPath("Karajan/1-02 Adagio.flac"));

        var library = new[]
        {
            new LocalLibraryTrackRef(
                TrackUid: "u1",
                RelPath: "Beethoven/01.flac",
                FullPath: @"D:\Music\Beethoven\01.flac",
                Title: "I. Allegro con brio",
                ArtistsDisplay: "Berliner Philharmoniker",
                Album: "Symphony No. 5",
                DurationMs: 420_000,
                SampleRateHz: 96000,
                ComposersDisplay: "Ludwig van Beethoven",
                ConductorsDisplay: "Herbert von Karajan",
                WorkName: "Symphony No. 5 in C Minor",
                MovementName: "I. Allegro con brio"),
        };

        var seeds = new[]
        {
            new ImportedTrackSeed(
                Title: "Beethoven: Symphony No. 5 in C Minor - I. Allegro con brio",
                Artist: "Ludwig van Beethoven; Herbert von Karajan",
                Album: "Symphony No. 5"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, library);
        Assert.Single(resolved);
        Assert.True(resolved[0].IsLocalMatched);
        Assert.Equal("u1", resolved[0].MatchedTrackUid);
    }

    [Fact]
    public void CoreFfi_EvaluateScene_KeepsClassicalWorksAtomicUnderEnergyArc()
    {
        const string sceneJson = """
            {
              "id": "classical_arc",
              "title": "Classical Energy Arc",
              "updated_at_ms": 0,
              "sort_arc": "energy_asc",
              "tempo_null_passthrough": true,
              "rule": {
                "predicate": {
                  "field": "composer",
                  "op": "in",
                  "values": ["Beethoven", "Chopin"]
                }
              }
            }
            """;
        const string tracksJson = """
            [
              {
                "track_uid": "sym_m2",
                "title": "Sym 5 II",
                "dj_energy": "low",
                "work_key": "beethoven_sym5",
                "movement_order": 2,
                "composers": ["Ludwig van Beethoven"]
              },
              {
                "track_uid": "chopin_mid",
                "title": "Ballade",
                "dj_energy": "medium",
                "composers": ["Frederic Chopin"]
              },
              {
                "track_uid": "sym_m1",
                "title": "Sym 5 I",
                "dj_energy": "high",
                "work_key": "beethoven_sym5",
                "movement_order": 1,
                "composers": ["Ludwig van Beethoven"]
              }
            ]
            """;

        var uids = TonewiseCoreClient.EvaluateScene(sceneJson, tracksJson);
        Assert.Equal(3, uids.Count);
        int posM1 = uids.ToList().IndexOf("sym_m1");
        Assert.True(posM1 >= 0);
        Assert.Equal("sym_m2", uids[posM1 + 1]);
    }
}
