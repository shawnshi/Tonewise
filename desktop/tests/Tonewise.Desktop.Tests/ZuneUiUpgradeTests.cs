using System.Security.Cryptography;
using System.Xml.Linq;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class ZuneUiUpgradeTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "desktop", "src", "Tonewise.Desktop", "MainWindow.xaml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Tonewise repository root.");
    }

    [Fact]
    public void ZuneColorMath_ClampsDynamicCoverColors_ToWcagAaContrast_OnDarkAndLight()
    {
        var darkDefaultContrast = ZuneColorMath.ComputeContrastRatio(ZuneColorMath.DefaultDarkAccentRgb, (0, 0, 0));
        var lightDefaultContrast = ZuneColorMath.ComputeContrastRatio(ZuneColorMath.DefaultLightAccentRgb, (255, 255, 255));
        Assert.True(darkDefaultContrast >= 4.5, $"Dark default accent contrast was {darkDefaultContrast:F2}");
        Assert.True(lightDefaultContrast >= 4.5, $"Light default accent contrast was {lightDefaultContrast:F2}");

        var clampedDark = ZuneColorMath.ClampAccentForTheme(24, 12, 36, isDarkTheme: true);
        var darkRatio = ZuneColorMath.ComputeContrastRatio(clampedDark, (0, 0, 0));
        Assert.True(darkRatio >= 4.5, $"Clamped dark accent contrast was {darkRatio:F2}");

        var clampedLight = ZuneColorMath.ClampAccentForTheme(245, 232, 130, isDarkTheme: false);
        var lightRatio = ZuneColorMath.ComputeContrastRatio(clampedLight, (255, 255, 255));
        Assert.True(lightRatio >= 4.5, $"Clamped light accent contrast was {lightRatio:F2}");

        var neutralFallback = ZuneColorMath.ClampAccentForTheme(128, 128, 128, isDarkTheme: true);
        Assert.Equal(ZuneColorMath.DefaultDarkAccentRgb, neutralFallback);

        var pixels = new (byte R, byte G, byte B)[]
        {
            (0, 0, 0),
            (255, 255, 255),
            (18, 18, 18),
            (235, 30, 125),
            (232, 32, 122),
            (230, 28, 120),
        };
        var vibrant = ZuneColorMath.SelectDominantVibrantRgb(pixels);
        Assert.NotNull(vibrant);
        Assert.True(vibrant!.Value.R > 180 && vibrant.Value.B > 80);
    }

    [Fact]
    public void DesktopStateStore_QuickplayPins_CrudAndOrdering_PreservesManifestHash()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"tonewise_qp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string manifestPath = Path.Combine(tempDir, "manifest.db");
            byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes("immutable-manifest-sentinel-v1");
            File.WriteAllBytes(manifestPath, manifestBytes);
            string hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)));

            string stateDbPath = Path.Combine(tempDir, "desktop_state.db");
            var pinB = new QuickplayPinRecord(
                PinId: "album::b",
                Kind: "album",
                TargetKey: "album::b",
                Title: "Symphony No. 9",
                Subtitle: "Berlin Philharmonic",
                CoverPath: "cover_b.jpg",
                SortOrder: 2,
                PinnedAtMs: 1_700_000_000_000L);
            var pinA = new QuickplayPinRecord(
                PinId: "album::a",
                Kind: "album",
                TargetKey: "album::a",
                Title: "Goldberg Variations",
                Subtitle: "Glenn Gould",
                CoverPath: "cover_a.jpg",
                SortOrder: 1,
                PinnedAtMs: 1_700_000_001_000L);

            DesktopStateStore.UpsertQuickplayPin(stateDbPath, pinB);
            DesktopStateStore.UpsertQuickplayPin(stateDbPath, pinA);

            var listed = DesktopStateStore.ListQuickplayPins(stateDbPath);
            Assert.Equal(2, listed.Count);
            Assert.Equal("album::a", listed[0].PinId);
            Assert.Equal("album::b", listed[1].PinId);

            bool removed = DesktopStateStore.RemoveQuickplayPin(stateDbPath, "album::a");
            Assert.True(removed);

            var afterRemove = DesktopStateStore.ListQuickplayPins(stateDbPath);
            Assert.Single(afterRemove);
            Assert.Equal("album::b", afterRemove[0].PinId);

            string hashAfter = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)));
            Assert.Equal(hashBefore, hashAfter);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void QuickplayAggregator_BuildsHistoryNewestAndRediscovery_WithoutDuplicates()
    {
        long nowMs = 1_760_000_000_000L;
        long oneHundredTwentyDaysMs = 120L * 24 * 60 * 60 * 1000;

        var tracks = new[]
        {
            new QuickplayTrackSeed("t1", "Mvt I", "Kind of Blue", "Miles Davis", "", "", 5, 12, nowMs - 10_000, nowMs - 50_000, 1959, "c1.jpg"),
            new QuickplayTrackSeed("t2", "Mvt II", "Kind of Blue", "Miles Davis", "", "", 4, 9, nowMs - 5_000, nowMs - 50_000, 1959, "c1.jpg"),
            new QuickplayTrackSeed("t3", "Track A", "Blue Train", "John Coltrane", "", "", 5, 6, nowMs - 20_000, nowMs - 1_000, 1957, "c2.jpg"),
            new QuickplayTrackSeed("t4", "Track B", "Fresh Arrival", "Hiromi", "", "", 3, 0, 0, nowMs - 100, 2025, "c3.jpg"),
            new QuickplayTrackSeed("t5", "Sleeping Track", "Forgotten Gem", "Bill Evans", "", "", 5, 20, nowMs - oneHundredTwentyDaysMs, nowMs - 500_000, 1961, "c4.jpg"),
        };

        var history = QuickplayAggregator.BuildHistoryAlbums(tracks, maxCount: 6);
        Assert.Equal(3, history.Count);
        Assert.Equal("Kind of Blue", history[0].Title);
        Assert.Equal("Blue Train", history[1].Title);
        Assert.Equal(history.Count, history.Select(x => x.TargetKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var newest = QuickplayAggregator.BuildNewlyAddedAlbums(tracks, maxCount: 6);
        Assert.Equal(4, newest.Count);
        Assert.Equal("Fresh Arrival", newest[0].Title);
        Assert.Equal(newest.Count, newest.Select(x => x.TargetKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var rediscovery = QuickplayAggregator.BuildRediscoveryAlbums(tracks, nowMs, maxCount: 6);
        Assert.Equal(2, rediscovery.Count);
        Assert.Equal("Forgotten Gem", rediscovery[0].Title);
        Assert.Equal(rediscovery.Count, rediscovery.Select(x => x.TargetKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DesktopUserSettings_EnableDynamicCoverAccent_RoundTripsAndDefaultsFalseForLegacyJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"tonewise_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string settingsPath = Path.Combine(tempDir, "settings.json");
            File.WriteAllText(settingsPath, """
                {
                  "ThemePreference": "dark",
                  "AudioBackend": "WasapiShared",
                  "BufferMs": 64
                }
                """);

            var legacy = DesktopSettingsStore.Load(settingsPath);
            Assert.False(legacy.EnableDynamicCoverAccent);

            var updated = legacy with { EnableDynamicCoverAccent = true };
            DesktopSettingsStore.Save(updated, settingsPath);

            var reloaded = DesktopSettingsStore.Load(settingsPath);
            Assert.True(reloaded.EnableDynamicCoverAccent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ZuneTokensXaml_HasCompleteDarkLightHighContrastKeyParity_AndMainWindowHasNoLegacyTwHexBrushes()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");
        string zuneTokensPath = Path.Combine(desktopRoot, "Themes", "ZuneTokens.xaml");

        var doc = XDocument.Load(zuneTokensPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace pres = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var cornerRadius = doc.Root!
            .Elements(pres + "CornerRadius")
            .FirstOrDefault(e => (string?)e.Attribute(x + "Key") == "ControlCornerRadius");
        Assert.NotNull(cornerRadius);
        Assert.Equal("0", cornerRadius!.Value.Trim());

        var themeDicts = doc.Root!
            .Element(pres + "ResourceDictionary.ThemeDictionaries")!
            .Elements(pres + "ResourceDictionary")
            .ToDictionary(
                e => (string)e.Attribute(x + "Key")!,
                e => e.Elements()
                    .Select(el => (string?)el.Attribute(x + "Key"))
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Cast<string>()
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        Assert.True(themeDicts.ContainsKey("Default"));
        Assert.True(themeDicts.ContainsKey("Light"));
        Assert.True(themeDicts.ContainsKey("HighContrast"));
        Assert.NotEmpty(themeDicts["Default"]);
        Assert.Equal(themeDicts["Default"], themeDicts["Light"]);
        Assert.Equal(themeDicts["Default"], themeDicts["HighContrast"]);

        foreach (string rel in new[] { "MainWindow.xaml", "DeskClockWidgetWindow.xaml", "App.xaml" })
        {
            string xamlText = File.ReadAllText(Path.Combine(desktopRoot, rel));
            Assert.DoesNotContain("TwBg", xamlText, StringComparison.Ordinal);
            Assert.DoesNotContain("TwFg", xamlText, StringComparison.Ordinal);
            Assert.DoesNotContain("TwLine", xamlText, StringComparison.Ordinal);
            Assert.DoesNotContain("Themes/Tokens.xaml", xamlText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ZuneNavigationRoutes_StudioDefaultsToSync_PreservingFilteredTracksForUsbExport()
    {
        Assert.Equal("sync", ZuneNavigationRoutes.GetDefaultSubTab("studio"));
        Assert.Equal("tracks", ZuneNavigationRoutes.GetDefaultSubTab("collection"));
        Assert.Equal("home", ZuneNavigationRoutes.GetDefaultSubTab("quickplay"));
        Assert.Equal("gallery", ZuneNavigationRoutes.GetDefaultSubTab("now_playing"));
        Assert.Equal("audio", ZuneNavigationRoutes.GetDefaultSubTab("settings"));

        foreach (string sub in new[] { "sync", "lastfm", "usb", "inbox" })
        {
            Assert.True(ZuneNavigationRoutes.IsValidSubTab("studio", sub));
        }
        foreach (string sub in new[] { "playlists", "scenes", "genres", "albums", "tracks", "artists", "classical", "composers", "conductors", "folders", "folder" })
        {
            Assert.True(ZuneNavigationRoutes.IsValidSubTab("collection", sub));
        }
        var expectedLabels = new[] { "playlists", "scenes", "genres", "albums", "tracks", "artists", "classical", "composers", "conductors", "folder" };
        Assert.Equal(expectedLabels, ZuneNavigationRoutes.CollectionSubTabs.Select(x => x.Label).ToArray());
        foreach (string sub in new[] { "audio", "storage", "apis_ui" })
        {
            Assert.True(ZuneNavigationRoutes.IsValidSubTab("settings", sub));
        }
        Assert.False(ZuneNavigationRoutes.IsValidSubTab("studio", "albums"));
    }

    [Fact]
    public void QuickplayPinMatcher_ResolvesFolderEmptyAlbumAndSingleTrackPins_Accurately()
    {
        var folderPin = new QuickplayPinRecord("folder::classical/beethoven", "folder", "Classical/Beethoven", "Beethoven", "物理存储目录", null, 0, 1);
        Assert.True(QuickplayPinMatcher.MatchesTrack(
            folderPin, "u1", "Sym 9", "Symphony No. 9", "Classical", "Karajan", "Beethoven", "Karajan",
            @"Classical\Beethoven", @"Classical\Beethoven\01.flac"));
        Assert.False(QuickplayPinMatcher.MatchesTrack(
            folderPin, "u2", "Other", "", "Jazz", "Evans", "", "",
            @"Jazz\Evans", @"Jazz\Evans\01.flac"));

        var emptyAlbumPin = new QuickplayPinRecord("album::__empty_album__", "album", QuickplayPinMatcher.EmptyAlbumSentinel, "(未分配专辑)", "Track", null, 0, 1);
        Assert.True(QuickplayPinMatcher.MatchesTrack(emptyAlbumPin, "u3", "", "", "", "Artist", "", "", "", "01.flac"));
        Assert.False(QuickplayPinMatcher.MatchesTrack(emptyAlbumPin, "u4", "Real Album", "", "", "Artist", "", "", "", "02.flac"));

        var trackPin = new QuickplayPinRecord("track::u5", "track", "u5", "Loose Demo", "Artist", null, 0, 1);
        Assert.True(QuickplayPinMatcher.MatchesTrack(trackPin, "u5", "", "", "", "Artist", "", "", "", "demo.flac"));
        Assert.False(QuickplayPinMatcher.MatchesTrack(trackPin, "u6", "", "", "", "Artist", "", "", "", "other.flac"));

        var workPin = new QuickplayPinRecord("work::goldberg", "work", "Goldberg Variations", "Goldberg Variations", "Bach", null, 0, 1);
        Assert.True(QuickplayPinMatcher.MatchesTrack(workPin, "u7", "Album", "Goldberg Variations", "Classical", "Gould", "Bach", "", "", ""));

        var genrePin = new QuickplayPinRecord("genre::jazz", "genre", "Jazz", "Jazz", "流派切片", null, 0, 1);
        Assert.True(QuickplayPinMatcher.MatchesTrack(genrePin, "u8", "Kind of Blue", "", "Jazz", "Miles Davis", "", "", "", ""));
    }

    [Fact]
    public void MainWindowXamlAndCodeBehind_PassZuneAuditRegressionGuards()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");
        string xaml = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml"));
        string cs = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml.cs"));

        Assert.DoesNotContain("PivotInkBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("PivotInkOpacity", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AlwaysExpandClassicalSubItems", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsScenesPresetSubTab", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsScenesAutoDjSubTab", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsScenesStudioSubTab", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsScenesArcSubTab", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InspectorLexiconTagButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TransportHeartIcon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DjFreezeButton\"", xaml, StringComparison.Ordinal);

        Assert.DoesNotContain("Application.Current.Resources[\"ZuneInk", cs, StringComparison.Ordinal);
        Assert.Contains("RootWorkbench.AddHandler(UIElement.PointerMovedEvent", cs, StringComparison.Ordinal);
        Assert.Contains("InspectorLexiconTagButton", cs, StringComparison.Ordinal);

        // Top nav order: quickplay -> collection -> now_playing -> studio
        int idxQuickplay = xaml.IndexOf("Tag=\"quickplay\"", StringComparison.Ordinal);
        int idxCollection = xaml.IndexOf("Tag=\"collection\"", StringComparison.Ordinal);
        int idxNowPlaying = xaml.IndexOf("Tag=\"now_playing\"", StringComparison.Ordinal);
        int idxStudio = xaml.IndexOf("Tag=\"studio\"", StringComparison.Ordinal);
        Assert.True(idxQuickplay > 0 && idxQuickplay < idxCollection && idxCollection < idxNowPlaying && idxNowPlaying < idxStudio);
        Assert.Contains("VirtualKey.Number3 => (\"now_playing\"", cs, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Number4 => (\"studio\"", cs, StringComparison.Ordinal);

        // Quickplay cover binding + fallback logical-tree resolution
        Assert.Contains("CoverArtImageCache.GetQuickplayCover(CoverArtPath)", xaml, StringComparison.Ordinal);
        Assert.Contains("FindLogicalOrVisualImage", cs, StringComparison.Ordinal);

        // Collapsible Now Playing right queue panel
        Assert.Contains("x:Name=\"NowPlayingQueuePanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingQueueCollapseButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingQueueExpandButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToggleNowPlayingQueueCommand", xaml, StringComparison.Ordinal);

        // Layout rounding & Composition translation cleanup for crisp DirectWrite text
        Assert.Contains("UseLayoutRounding=\"True\"", xaml, StringComparison.Ordinal);
        string motionCs = File.ReadAllText(Path.Combine(desktopRoot, "Motion", "ZuneMotionHelper.cs"));
        Assert.Contains("SetIsTranslationEnabled(element, false)", motionCs, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickplayAggregator_BuildAll_MatchesIndividualBuilders_AndPreservesCoverPaths()
    {
        long nowMs = 1_760_000_000_000L;
        long oldMs = nowMs - 120L * 86_400_000L;
        var tracks = new[]
        {
            new QuickplayTrackSeed("t1", "Mvt I", "Kind of Blue", "Miles Davis", "", "", 5, 12, nowMs - 10_000, nowMs - 50_000, 1959, "c1.jpg"),
            new QuickplayTrackSeed("t2", "Track A", "Blue Train", "John Coltrane", "", "", 5, 6, nowMs - 20_000, nowMs - 1_000, 1957, "c2.jpg"),
            new QuickplayTrackSeed("t3", "Track B", "Fresh Arrival", "Hiromi", "", "", 3, 0, 0, nowMs - 100, 2025, "c3.jpg"),
            new QuickplayTrackSeed("t4", "Sleeping", "Forgotten Gem", "Bill Evans", "", "", 5, 20, oldMs, nowMs - 500_000, 1961, "c4.jpg"),
        };

        var (history, newest, rediscovery) = QuickplayAggregator.BuildAll(tracks, nowMs, maxCount: 8);
        Assert.Equal(QuickplayAggregator.BuildHistoryAlbums(tracks, 8).Select(x => x.TileId), history.Select(x => x.TileId));
        Assert.Equal(QuickplayAggregator.BuildNewlyAddedAlbums(tracks, 8).Select(x => x.TileId), newest.Select(x => x.TileId));
        Assert.Equal(QuickplayAggregator.BuildRediscoveryAlbums(tracks, nowMs, 8).Select(x => x.TileId), rediscovery.Select(x => x.TileId));
        Assert.All(history, item => Assert.False(string.IsNullOrWhiteSpace(item.CoverArtPath)));
        Assert.All(newest, item => Assert.False(string.IsNullOrWhiteSpace(item.CoverArtPath)));
    }

    [Fact]
    public void LibraryStore_DiscoverAlbumAssets_ResolvesExtendedCoverNames_DiscParentFallback_AndBookletPdf()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tonewise_assets_{Guid.NewGuid():N}");
        string albumDir = Path.Combine(tempRoot, "Beethoven");
        string cd1Dir = Path.Combine(albumDir, "CD1");
        string frontOnlyDir = Path.Combine(tempRoot, "FrontOnly");
        Directory.CreateDirectory(cd1Dir);
        Directory.CreateDirectory(frontOnlyDir);

        try
        {
            string parentCover = Path.Combine(albumDir, "cover.jpeg");
            string booklet = Path.Combine(cd1Dir, "booklet.pdf");
            string frontCover = Path.Combine(frontOnlyDir, "front.png");
            File.WriteAllBytes(parentCover, [1, 2, 3]);
            File.WriteAllBytes(booklet, [4, 5, 6]);
            File.WriteAllBytes(frontCover, [7, 8, 9]);

            var cd1Assets = Tonewise.Desktop.Services.LibraryStore.DiscoverAlbumAssets(cd1Dir, tempRoot);
            Assert.Equal(parentCover, cd1Assets.Cover);
            Assert.Equal(booklet, cd1Assets.Booklet);

            var frontAssets = Tonewise.Desktop.Services.LibraryStore.DiscoverAlbumAssets(frontOnlyDir, tempRoot);
            Assert.Equal(frontCover, frontAssets.Cover);
            Assert.Null(frontAssets.Booklet);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
