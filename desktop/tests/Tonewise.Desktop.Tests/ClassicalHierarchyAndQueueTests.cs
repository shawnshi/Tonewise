using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class ClassicalHierarchyAndQueueTests
{
    private static ClassicalTrackSeed<string> MakeSeed(
        string uid,
        string title,
        string album,
        string artists,
        string composers,
        string conductors,
        string? work = null,
        string? movement = null,
        int? mvtNum = null,
        int? year = null,
        string? genre = "Classical",
        long durationMs = 300_000) =>
        new(
            Track: uid,
            TrackUid: uid,
            Title: title,
            Album: album,
            ArtistsDisplay: artists,
            ComposersDisplay: composers,
            ConductorsDisplay: conductors,
            WorkName: work,
            MovementName: movement,
            MovementNumber: mvtNum,
            DiscNumber: 1,
            TrackNumber: mvtNum ?? 1,
            Year: year,
            Genre: genre,
            DurationMs: durationMs);

    [Fact]
    public void ClassicalHierarchyBuilder_GroupsUntaggedCatalogColonWorks_FiltersEraAndForm_AndSeparatesRecordings()
    {
        var seeds = new[]
        {
            MakeSeed("s1", "Piano Sonata No. 52 in E-flat major, Hob. XVI:52: I. Allegro", "Haydn Sonatas", "Marc-Andre Hamelin", "Joseph Haydn", "", year: 2015),
            MakeSeed("s2", "Piano Sonata No. 52 in E-flat major, Hob. XVI:52: II. Adagio", "Haydn Sonatas", "Marc-Andre Hamelin", "Joseph Haydn", "", year: 2015),
            MakeSeed("k1", "Symphony No. 9 in D minor, Op. 125", "Beethoven: Symphony No. 9 (1963)", "Berlin Philharmonic", "Ludwig van Beethoven", "Herbert von Karajan", work: "Symphony No. 9 in D minor, Op. 125", movement: "Allegro ma non troppo", mvtNum: 1, year: 1963, durationMs: 902_000),
            MakeSeed("a1", "Symphony No. 9 in D minor, Op. 125", "Beethoven: Symphony No. 9 (2000)", "Berlin Philharmonic", "Ludwig van Beethoven", "Claudio Abbado", work: "Symphony No. 9 in D minor, Op. 125", movement: "Allegro ma non troppo", mvtNum: 1, year: 2000, durationMs: 880_000),
            MakeSeed("p1", "Bohemian Rhapsody", "A Night at the Opera", "Queen", "Freddie Mercury", "", genre: "Rock", year: 1975),
        };

        var composers = ClassicalHierarchyBuilder.Build(
            seeds,
            ArtistEntitySplitter.SplitComposers,
            ArtistEntitySplitter.SplitPerformers,
            (album, cond) => WorkAwareShuffleEngine.BuildAlbumScope(album, cond));

        Assert.Equal(["joseph haydn", "ludwig van beethoven"], composers.Select(c => c.DisplayLowercase).ToArray());
        Assert.Equal("classical · 1 works · 1 recordings", composers[0].StatsLine);
        Assert.Equal("romantic · 1 works · 2 recordings", composers[1].StatsLine);
        Assert.Null(composers[0].LocalPortraitPath);

        var haydnWork = Assert.Single(composers[0].Works);
        Assert.Equal("Piano Sonata No. 52 in E-flat major, Hob. XVI:52", haydnWork.WorkTitle);
        Assert.Equal("solo", haydnWork.Form);
        Assert.Equal("1 version", haydnWork.VersionCountLabel);
        var haydnRec = Assert.Single(haydnWork.Recordings);
        Assert.Equal(["I.", "II."], haydnRec.Movements.Select(m => m.RomanNumeral).ToArray());
        Assert.Equal(["Allegro", "Adagio"], haydnRec.Movements.Select(m => m.DisplayTitle).ToArray());

        var beethovenWork = Assert.Single(composers[1].Works);
        Assert.Equal("2 versions", beethovenWork.VersionCountLabel);
        Assert.Equal(["karajan · 1963", "abbado · 2000"], beethovenWork.Recordings.Select(r => r.BadgeLabel).ToArray());
        Assert.Equal("15:02", beethovenWork.Recordings[0].Movements[0].DurationDisplay);

        var romanticOnly = ClassicalHierarchyBuilder.FilterByEra(composers, "romantic");
        Assert.Single(romanticOnly);
        Assert.Equal("Ludwig van Beethoven", romanticOnly[0].ComposerName);

        Assert.Single(ClassicalHierarchyBuilder.FilterByForm(composers[1].Works, "symphonies"));
        Assert.Empty(ClassicalHierarchyBuilder.FilterByForm(composers[1].Works, "concertos"));
    }

    [Fact]
    public void ClassicalSessionShuffleLock_SlicesSingleRecording_AndRestoresGlobalShuffleOnNonClassical()
    {
        var movements = new[] { "m1", "m2", "m3", "m4" };
        Assert.Equal(["m3", "m4"], ClassicalSessionShuffleLock.SliceRecordingMovements(movements, startIndex: 2));
        Assert.Equal(["m1", "m2", "m3", "m4"], ClassicalSessionShuffleLock.SliceRecordingMovements(movements, startIndex: 0));

        var shuffleLock = new ClassicalSessionShuffleLock();
        Assert.False(shuffleLock.Engage(currentGlobalShuffle: true));
        Assert.True(shuffleLock.IsLocked);
        Assert.True(shuffleLock.SavedGlobalShuffle);

        // Starting a second movement while locked must not clobber SavedGlobalShuffle
        Assert.False(shuffleLock.Engage(currentGlobalShuffle: false));
        Assert.True(shuffleLock.SavedGlobalShuffle);

        var (wasLocked, restored) = shuffleLock.ReleaseOnNonClassical();
        Assert.True(wasLocked);
        Assert.True(restored);
        Assert.False(shuffleLock.IsLocked);
    }

    [Fact]
    public void NowPlayingPresentation_SelectBestClassicalCreditTier_PreservesComposerAndConductor()
    {
        var tiers = ClassicalMetadataEngine.BuildClassicalCreditTiers(
            composer: "Ludwig van Beethoven",
            conductors: ["Herbert von Karajan"],
            soloists: ["Anne-Sophie Mutter", "Yo-Yo Ma"],
            ensemble: "Berliner Philharmoniker",
            year: 1980);

        string wide = ClassicalMetadataEngine.SelectBestClassicalCreditTier(tiers, maxWidthPx: 900);
        Assert.Contains("Berliner Philharmoniker", wide);

        string medium = ClassicalMetadataEngine.SelectBestClassicalCreditTier(tiers, maxWidthPx: 580);
        Assert.Contains("BPO", medium);

        string narrow = ClassicalMetadataEngine.SelectBestClassicalCreditTier(tiers, maxWidthPx: 220);
        Assert.Equal("Beethoven · Karajan · 1980", narrow);
    }

    [Fact]
    public void NavigationContract_ApplyActiveNavigationData_PopulatesClassicalBeforeEntityBrowserGuard()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "Tonewise.sln")) && !File.Exists(Path.Combine(dir, "desktop", "Tonewise.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.False(string.IsNullOrEmpty(dir));
        string repoRoot = File.Exists(Path.Combine(dir!, "desktop", "Tonewise.sln")) ? dir! : Path.GetDirectoryName(dir!)!;
        string vmLibSrc = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "ViewModels", "MainViewModel.Library.cs"));

        int navStart = vmLibSrc.IndexOf("private void ApplyActiveNavigationData()", StringComparison.Ordinal);
        Assert.True(navStart > 0);
        int classicalIdx = vmLibSrc.IndexOf("if (IsNavClassicalWorks)", navStart, StringComparison.Ordinal);
        int entityIdx = vmLibSrc.IndexOf("if (IsEntityBrowserVisible)", navStart, StringComparison.Ordinal);
        Assert.True(classicalIdx > navStart && entityIdx > classicalIdx,
            "ApplyActiveNavigationData must populate ClassicalComposers on IsNavClassicalWorks before IsEntityBrowserVisible check");
        Assert.Contains("RebuildClassicalHierarchyIndex();", vmLibSrc.AsSpan(classicalIdx, entityIdx - classicalIdx).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NowPlayingPresentation_FormatClassicalNowPlaying_UsesComposerWork_Movement_ArtistsConductorYear()
    {
        var formatted = ClassicalMetadataEngine.FormatClassicalNowPlaying(
            rawTitle: "Symphony No. 9 in D minor, Op. 125: IV. Presto",
            explicitWork: "Symphony No. 9 in D minor, Op. 125",
            explicitMovement: "Presto",
            explicitMovementNumber: 4,
            album: "Beethoven 9",
            composers: ["Ludwig van Beethoven"],
            artists: ["Berliner Philharmoniker", "Herbert von Karajan"],
            conductors: ["Herbert von Karajan"],
            year: 1963);

        Assert.Equal("Ludwig van Beethoven - Symphony No. 9 in D minor, Op. 125", formatted.Line1);
        Assert.Equal("IV. Presto", formatted.Line2);
        Assert.Equal("Berliner Philharmoniker · Herbert von Karajan · 1963", formatted.Line3Tiers[0]);
        Assert.Equal("BPO · Herbert von Karajan · 1963", ClassicalMetadataEngine.SelectBestClassicalCreditTier(formatted.Line3Tiers, 300));

        var soloOnly = ClassicalMetadataEngine.FormatClassicalNowPlaying(
            rawTitle: "Symphonic Etudes, Op. 13: Thema. Andante",
            explicitWork: "Symphonic Etudes, Op. 13",
            explicitMovement: "Thema. Andante",
            explicitMovementNumber: null,
            album: "Pogorelich Complete Recordings",
            composers: ["Robert Schumann"],
            artists: ["Ivo Pogorelich"],
            conductors: [],
            year: 2015);

        Assert.Equal("Robert Schumann - Symphonic Etudes, Op. 13", soloOnly.Line1);
        Assert.Equal("Thema. Andante", soloOnly.Line2);
        Assert.Equal("Ivo Pogorelich · 2015", soloOnly.Line3Tiers[0]);

        string alreadyPrefixed = ClassicalMetadataEngine.FormatClassicalComposerWorkLine(
            "Ludwig van Beethoven",
            "Beethoven: Symphony No. 5");
        Assert.Equal("Beethoven: Symphony No. 5", alreadyPrefixed);
    }

    [Fact]
    public void ClassicalHierarchyBuilder_MicroBenchmark14kTracks_BuildsQuicklyAcrossEras()
    {
        var composers = new[]
        {
            "Johann Sebastian Bach", "Wolfgang Amadeus Mozart", "Ludwig van Beethoven",
            "Frédéric Chopin", "Johannes Brahms", "Pyotr Ilyich Tchaikovsky",
            "Claude Debussy", "Dmitri Shostakovich", "Gustav Mahler", "Franz Schubert",
        };
        var genres = new[] { "Classical", "Baroque", "Opera", "Pop", "Rock", "Jazz" };
        var seeds = new List<ClassicalTrackSeed<string>>(14_000);
        for (int i = 0; i < 14_000; i++)
        {
            bool isClassical = i < 5_600;
            string composer = isClassical ? composers[i % composers.Length] : $"Artist {i % 120}";
            int workIdx = (i / composers.Length) % 25;
            string workName = isClassical ? $"Symphony No. {(workIdx % 9) + 1} in C minor, Op. {workIdx + 10}" : "";
            seeds.Add(new ClassicalTrackSeed<string>(
                Track: $"uid_{i}",
                TrackUid: $"uid_{i}",
                Title: isClassical ? $"{workName}: {(i % 4) + 1}. Movement {(i % 4) + 1}" : $"Song {i}",
                Album: $"Album {i % 300}",
                ArtistsDisplay: "Berliner Philharmoniker / Herbert von Karajan",
                ComposersDisplay: isClassical ? composer : "",
                ConductorsDisplay: isClassical ? "Herbert von Karajan" : "",
                WorkName: string.IsNullOrEmpty(workName) ? null : workName,
                MovementName: isClassical ? $"Movement {(i % 4) + 1}" : null,
                MovementNumber: isClassical ? (i % 4) + 1 : null,
                DiscNumber: 1,
                TrackNumber: (i % 12) + 1,
                Year: 1975 + (i % 30),
                Genre: genres[isClassical ? (i % 3) : (3 + (i % 3))],
                DurationMs: 240_000));
        }

        IReadOnlyList<ClassicalComposerNode<string>> WarmRun() =>
            ClassicalHierarchyBuilder.Build(
                seeds,
                ArtistEntitySplitter.SplitComposers,
                ArtistEntitySplitter.SplitPerformers,
                (album, cond) => WorkAwareShuffleEngine.BuildAlbumScope(album, cond),
                []);

        var baseline = WarmRun();
        Assert.Equal(composers.Length, baseline.Count);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int iter = 0; iter < 3; iter++)
        {
            var built = WarmRun();
            Assert.Equal(composers.Length, built.Count);
        }
        sw.Stop();
        double avgMs = sw.Elapsed.TotalMilliseconds / 3.0;
        Assert.True(avgMs < 1500.0, $"Expected 14k-track classical hierarchy build < 1500ms avg, actual={avgMs:F1}ms");

        var swFilter = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(6, ClassicalHierarchyBuilder.FilterByEra(baseline, "romantic").Count);
        }
        swFilter.Stop();
        Assert.True(swFilter.Elapsed.TotalMilliseconds < 100.0, $"Expected 1000 era filter calls < 100ms, actual={swFilter.Elapsed.TotalMilliseconds:F2}ms");
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "desktop", "Tonewise.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [Fact]
    public void ClassicalAllErasButton_AndStartupThemePreload_RegressionGuardsHold()
    {
        string repoRoot = FindRepoRoot();
        string xaml = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "MainWindow.xaml"));
        string xamlCs = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "MainWindow.xaml.cs"));
        string vmClassical = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "ViewModels", "MainViewModel.Classical.cs"));
        string appCs = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "App.xaml.cs"));

        Assert.Contains("Tag=\"all\" Click=\"OnClassicalEraFilterClick\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"\" Click=\"OnClassicalEraFilterClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("fe.Tag as string ?? \"\"", xamlCs, StringComparison.Ordinal);
        Assert.Contains("if (clean is \"all\" or \"all eras\") clean = \"\";", vmClassical, StringComparison.Ordinal);
        Assert.Contains("DesktopSettingsStore.Load()", appCs, StringComparison.Ordinal);
        int preloadIdx = xamlCs.IndexOf("PreloadInitialAppTheme", StringComparison.Ordinal);
        int initIdx = xamlCs.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        Assert.True(preloadIdx > 0 && preloadIdx < initIdx, "PreloadInitialAppTheme must execute before InitializeComponent()");
    }

    [Fact]
    public void BuildByEnsemble_FiltersPopAndComposers_AndUnifiesClassicalPanelRouting()
    {
        var seeds = new[]
        {
            new ClassicalTrackSeed<string>(
                Track: "b1", TrackUid: "b1",
                Title: "Piano Concerto No. 3 in C minor, Op. 37: I. Allegro con brio",
                Album: "Beethoven Piano Concertos",
                ArtistsDisplay: "Ludwig van Beethoven, Martha Argerich, Wiener Philharmoniker",
                ComposersDisplay: "Ludwig van Beethoven",
                ConductorsDisplay: "Claudio Abbado",
                WorkName: "Piano Concerto No. 3 in C minor, Op. 37",
                MovementName: "Allegro con brio",
                MovementNumber: 1,
                DiscNumber: 1, TrackNumber: 1, Year: 1995, Genre: "Classical", DurationMs: 900_000),
            new ClassicalTrackSeed<string>(
                Track: "j1", TrackUid: "j1",
                Title: "夜曲",
                Album: "十一月的萧邦",
                ArtistsDisplay: "周杰伦",
                ComposersDisplay: "周杰伦",
                ConductorsDisplay: "",
                WorkName: "十一月的萧邦组曲",
                MovementName: "",
                MovementNumber: null,
                DiscNumber: 1, TrackNumber: 1, Year: 2005, Genre: "Mandopop", DurationMs: 225_000),
            new ClassicalTrackSeed<string>(
                Track: "r1", TrackUid: "r1",
                Title: "Wish You Were Here",
                Album: "Wish You Were Here",
                ArtistsDisplay: "Pink Floyd",
                ComposersDisplay: "David Gilmour",
                ConductorsDisplay: "",
                WorkName: "Shine On",
                MovementName: "",
                MovementNumber: null,
                DiscNumber: 1, TrackNumber: 4, Year: 1975, Genre: "Progressive Rock", DurationMs: 334_000),
            new ClassicalTrackSeed<string>(
                Track: "p1", TrackUid: "p1",
                Title: "Anti-Hero",
                Album: "Midnights",
                ArtistsDisplay: "Taylor Swift",
                ComposersDisplay: "Taylor Swift",
                ConductorsDisplay: "",
                WorkName: "Midnights Act I",
                MovementName: "",
                MovementNumber: null,
                DiscNumber: 1, TrackNumber: 3, Year: 2022, Genre: "Pop", DurationMs: 200_000),
        };

        var ensembles = ClassicalHierarchyBuilder.BuildByEnsemble(
            seeds,
            ArtistEntitySplitter.SplitComposers,
            ArtistEntitySplitter.SplitPerformers,
            (album, cond) => WorkAwareShuffleEngine.BuildAlbumScope(album, cond),
            []);

        var names = ensembles.Select(e => e.ComposerName).ToList();
        Assert.Contains("Martha Argerich", names);
        Assert.Contains("Vienna Philharmonic", names);
        Assert.DoesNotContain("Ludwig van Beethoven", names);
        Assert.DoesNotContain("周杰伦", names);
        Assert.DoesNotContain("Pink Floyd", names);
        Assert.DoesNotContain("Taylor Swift", names);

        var argerich = ensembles.Single(e => e.ComposerName == "Martha Argerich");
        Assert.Equal("Beethoven — Piano Concerto No. 3 in C minor, Op. 37", argerich.Works.Single().WorkTitle);
        Assert.Contains("1 composers · 1 works · 1 recordings", argerich.StatsLine);

        string repoRoot = FindRepoRoot();
        string vmLib = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "ViewModels", "MainViewModel.Library.cs"));
        Assert.Contains("IsClassicalComposerViewVisible = IsNavClassicalWorks;", vmLib, StringComparison.Ordinal);
        Assert.Contains("IsEntityBrowserVisible = entityMode && !IsNavClassicalWorks;", vmLib, StringComparison.Ordinal);
    }
}
