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
        foreach (string sub in new[] { "playlists", "scenes", "genres", "albums", "tracks", "artists", "classical", "conductors", "folders", "folder" })
        {
            Assert.True(ZuneNavigationRoutes.IsValidSubTab("collection", sub));
        }
        var expectedLabels = new[] { "playlists", "scenes", "genres", "albums", "tracks", "artists", "classical", "conductors", "folder" };
        Assert.Equal(expectedLabels, ZuneNavigationRoutes.CollectionSubTabs.Select(x => x.Label).ToArray());
        Assert.DoesNotContain(ZuneNavigationRoutes.CollectionSubTabs, x => x.Id == "composers");
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

    private static string ReadAllMainWindowSources(string desktopRoot) =>
        string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(desktopRoot, "MainWindow*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));

    [Fact]
    public void MainWindowXamlAndCodeBehind_PassZuneAuditRegressionGuards()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");
        string xaml = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml"));
        string cs = ReadAllMainWindowSources(desktopRoot);

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

        // Quickplay single-pass cover binding (redundant ElementPrepared tree traversal removed)
        Assert.Contains("CoverArtImageCache.GetQuickplayCover(CoverArtPath)", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnQuickplayRepeaterElementPrepared", xaml, StringComparison.Ordinal);

        // Collapsible Now Playing right queue panel
        Assert.Contains("x:Name=\"NowPlayingQueuePanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingQueueCollapseButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingQueueExpandButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToggleNowPlayingQueueCommand", xaml, StringComparison.Ordinal);

        // Layout rounding & Composition translation cleanup for crisp DirectWrite text
        Assert.Contains("UseLayoutRounding=\"True\"", xaml, StringComparison.Ordinal);
        string motionCs = File.ReadAllText(Path.Combine(desktopRoot, "Motion", "ZuneMotionHelper.cs"));
        Assert.Contains("SetIsTranslationEnabled(element, false)", motionCs, StringComparison.Ordinal);

        // Playlists deck visibility, expand toggle, scrollbar, and 2-line wrap
        Assert.Contains("PlaylistsCountBadge", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PlaylistsExpandToggleButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Visible\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PlaylistsDeckMaxHeight(ViewModel.IsPlaylistsExpanded)", xaml, StringComparison.Ordinal);

        // Inbox audit bucket counts & summary banner
        Assert.Contains("x:Name=\"InboxAuditChipsPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.AuditMissingGenreLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.AuditRediscovery90dLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InboxAuditResultSummaryBlock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SyncActiveInboxAuditButtons", cs, StringComparison.Ordinal);

        // Now Playing classical composer & conductor navigation actions
        Assert.Contains("x:Name=\"NowPlayingComposerButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingConductorButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnTransportComposerClick", cs, StringComparison.Ordinal);
        Assert.Contains("OnTransportConductorClick", cs, StringComparison.Ordinal);

        // SSOT 5D mood cluster alignment (zero legacy focus_deep/bright_energetic/melancholic_dark)
        Assert.DoesNotContain("focus_deep", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("bright_energetic", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("melancholic_dark", xaml, StringComparison.Ordinal);
        Assert.Contains("deep_introspective", xaml, StringComparison.Ordinal);
        Assert.Contains("bright_joyful", xaml, StringComparison.Ordinal);
        Assert.Contains("high_tension", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchAAndB_SecurityPairingStagingAndMoodSsot_PassRegressionGuards()
    {
        // 1. P0-S3: USB remote_music_root strict whitelist
        Assert.True(Tonewise.SyncHost.Usb.UsbAdbSyncClient.TryValidateRemoteMusicRoot(null, out string defRoot));
        Assert.Equal("/storage/emulated/0/Music", defRoot);
        Assert.True(Tonewise.SyncHost.Usb.UsbAdbSyncClient.TryValidateRemoteMusicRoot("/sdcard/Music/Tonewise", out _));
        Assert.False(Tonewise.SyncHost.Usb.UsbAdbSyncClient.TryValidateRemoteMusicRoot("/data/local/tmp", out _));
        Assert.False(Tonewise.SyncHost.Usb.UsbAdbSyncClient.TryValidateRemoteMusicRoot("/storage/emulated/0/Music/../../Download", out _));
        Assert.False(Tonewise.SyncHost.Usb.UsbAdbSyncClient.TryValidateRemoteMusicRoot("/storage/emulated/0/Music;rm -rf", out _));

        // 2. P0-S4: Pairing global failure threshold preserves per-client IP lockouts
        string root = Path.Combine(Path.GetTempPath(), $"tw_pair_lock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var pairStore = new SyncOutboxAndPairingStore(root);
            for (int i = 0; i < 5; i++)
            {
                pairStore.TryPairWithStatus("000000", "d1", "Phone 1", out _, out _, out _, "192.168.1.10");
            }
            Assert.Equal(PairAttemptStatus.LockedOut, pairStore.TryPairWithStatus("000000", "d1", "Phone 1", out _, out _, out _, "192.168.1.10"));

            // Trigger 15 global failures from different attackers
            for (int a = 0; a < 15; a++)
            {
                pairStore.TryPairWithStatus("000000", $"att_{a}", "Attacker", out _, out _, out _, $"192.168.1.{50 + a}");
            }
            // Original attacker IP MUST remain locked out!
            Assert.Equal(PairAttemptStatus.LockedOut, pairStore.TryPairWithStatus("000000", "d1", "Phone 1", out _, out _, out _, "192.168.1.10"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // 3. P0-C2: 5D mood clusters SSOT alignment + legacy normalization + Rust EvaluateScene
        Assert.Equal("deep_introspective", FiveDimQueryBuilder.NormalizeMoodClusterId("focus_deep"));
        Assert.Equal("bright_joyful", FiveDimQueryBuilder.NormalizeMoodClusterId("bright_energetic"));
        Assert.Equal("high_tension", FiveDimQueryBuilder.NormalizeMoodClusterId("melancholic_dark"));

        string sceneJson = FiveDimQueryBuilder.BuildSceneCardJson(new FiveDimFilterQuery(MoodCluster: "deep_introspective"));
        string tracksJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { track_uid = "t_deep", title = "A", dj_scene = Array.Empty<string>(), dj_vocals = "instrumental", dj_energy = "low", tempo_bucket = "Slow", moods = new[] { "Melancholic", "Contemplative" }, genre = "Classical", rating = 5, unplayed_days = 10 },
            new { track_uid = "t_bright", title = "B", dj_scene = Array.Empty<string>(), dj_vocals = "vocal", dj_energy = "high", tempo_bucket = "Fast", moods = new[] { "Cheerful" }, genre = "Pop", rating = 3, unplayed_days = 10 },
        });
        var matched = Tonewise.Interop.TonewiseCoreClient.EvaluateScene(sceneJson, tracksJson);
        Assert.Equal(["t_deep"], matched);

        // 4. P0-D2: StagingDiffStore pre-bounds physical track count so deferred tracks stay Approved (not silently Committed)
        string stageDir = Path.Combine(Path.GetTempPath(), $"tw_stage_cap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDir);
        string stageDb = Path.Combine(stageDir, "manifest.db");
        try
        {
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stageDb}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = schemaSql;
                setup.ExecuteNonQuery();
                for (int i = 1; i <= 3; i++)
                {
                    using var ins = conn.CreateCommand();
                    ins.CommandText = $"""
                        INSERT INTO tracks (
                            track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                            rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                        ) VALUES (
                            'u{i}', X'0{i}', 128, X'0{i}', 0, '0{i}.flac', 1024, 1000, 'flac', 44100, 1000, 'T{i}'
                        );
                        """;
                    ins.ExecuteNonQuery();
                }
            }
            var diffStore = new Tonewise.SyncHost.Curation.StagingDiffStore(stageDb);
            diffStore.EnqueueProposals([
                Tonewise.SyncHost.Curation.StagingDiffStore.TryCreateProposal("u1", "genre", null, "Classical", 0.95, "Test")!,
                Tonewise.SyncHost.Curation.StagingDiffStore.TryCreateProposal("u2", "genre", null, "Jazz", 0.95, "Test")!,
                Tonewise.SyncHost.Curation.StagingDiffStore.TryCreateProposal("u3", "genre", null, "Rock", 0.95, "Test")!,
            ]);
            var batch1 = diffStore.CommitApprovedBatch("cap-1", autoApproveThreshold: 0.90, maxProposals: 100, maxPhysicalFiles: 2);
            Assert.Equal(2, batch1.CommittedCount);
            var (approvedLeft, pendingLeft) = diffStore.GetPendingProposalCounts();
            Assert.Equal(0, approvedLeft);
            Assert.Equal(1, pendingLeft);
            var batch2 = diffStore.CommitApprovedBatch("cap-2", autoApproveThreshold: 0.90, maxProposals: 100, maxPhysicalFiles: 2);
            Assert.Equal(1, batch2.CommittedCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);
        }
    }

    [Fact]
    public void InboxAuditEvaluator_ComputesExactBucketCounts_MatchesModes_AndFormatsSummary()
    {
        long nowMs = 1_760_000_000_000L;
        long recentMs = nowMs - 5L * 86_400_000L;
        long staleMs = nowMs - 100L * 86_400_000L;

        var tracks = new[]
        {
            new InboxAuditTrackSeed("Classical", "Beethoven", "Karajan", "Sym 9", "DG", 2, "slow", 72.0, "c1.jpg", "instrumental", recentMs),
            new InboxAuditTrackSeed("", "", "", "Untitled", "", 0, null, null, null, "unknown", 0),
            new InboxAuditTrackSeed("Jazz", "", "", "", "Blue Note", 0, "medium", null, "", null, staleMs),
        };

        var counts = InboxAuditEvaluator.ComputeCounts(tracks, nowMs);
        Assert.Equal(1, counts.MissingGenre);
        Assert.Equal(2, counts.MissingComposer);
        Assert.Equal(2, counts.MissingConductor);
        Assert.Equal(1, counts.MissingAlbum);
        Assert.Equal(1, counts.MissingPublisher);
        Assert.Equal(2, counts.MissingScene);
        Assert.Equal(1, counts.MissingTempo);
        Assert.Equal(2, counts.MissingCover);
        Assert.Equal(2, counts.MissingVocals);
        Assert.Equal(2, counts.Rediscovery90d);

        int genreMatched = tracks.Count(t => InboxAuditEvaluator.MatchesMode(t, "audit_missing_genre", nowMs));
        Assert.Equal(counts.MissingGenre, genreMatched);
        Assert.Contains("命中 1 首", InboxAuditEvaluator.FormatResultSummary("audit_missing_genre", "体检 · 缺失流派", genreMatched, tracks.Length));
        Assert.Contains("0 首缺失", InboxAuditEvaluator.FormatResultSummary("audit_missing_genre", "体检 · 缺失流派", 0, tracks.Length));
    }

    [Fact]
    public void TrackItem_ClassifyIsClassical_DistinguishesClassicalVsPop_ForComposerConductorActions()
    {
        Assert.True(Tonewise.Desktop.Models.TrackItem.ClassifyIsClassical("Classical", "", "Beethoven", "Karajan"));
        Assert.True(Tonewise.Desktop.Models.TrackItem.ClassifyIsClassical("", "Symphony No. 9", "", ""));
        Assert.True(Tonewise.Desktop.Models.TrackItem.ClassifyIsClassical("", "", "", "Abbado"));
        Assert.False(Tonewise.Desktop.Models.TrackItem.ClassifyIsClassical("Pop", "", "Max Martin", ""));
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BatchCAndD_LastFmRedaction_WatcherAiff_PairingLruAndProtocolHygieneGuardsHold()
    {
        // C6.6: Last.fm secret redaction
        string rawUrl = "https://ws.audioscrobbler.com/2.0/?method=user.getrecenttracks&user=shawn&api_key=SECRET123&sk=SESS456&api_sig=SIG789&format=json";
        string redacted = Tonewise.SyncHost.Ledger.LastFmWebApiClient.RedactSensitiveQueryParams(rawUrl);
        Assert.DoesNotContain("SECRET123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("SESS456", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("SIG789", redacted, StringComparison.Ordinal);
        Assert.Contains("api_key=REDACTED", redacted, StringComparison.Ordinal);
        Assert.Contains("sk=REDACTED", redacted, StringComparison.Ordinal);
        Assert.Contains("api_sig=REDACTED", redacted, StringComparison.Ordinal);

        // C6.5: LibraryFileWatcherService recognizes AIFF/AIF extensions
        Assert.True(Tonewise.SyncHost.Curation.LibraryFileWatcherService.ShouldProcessPath(@"C:\Music\Symphony.aiff", isChangeOnly: false));
        Assert.True(Tonewise.SyncHost.Curation.LibraryFileWatcherService.ShouldProcessPath(@"C:\Music\Symphony.AIF", isChangeOnly: true));
        Assert.False(Tonewise.SyncHost.Curation.LibraryFileWatcherService.ShouldProcessPath(@"C:\Music\.tmp.aiff", isChangeOnly: false));

        // C6.3 + C6.2: Pairing device cache & DesktopStateStore read-only ListQuickplayPins
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tonewise_batch_cd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new SyncOutboxAndPairingStore(tempRoot);
            var rec = store.PairDeviceDirect("dev_cache_1", "Pixel 9", out string issuedToken);
            Assert.Equal("dev_cache_1", rec.DeviceId);
            Assert.True(store.ValidateBearerToken("Bearer " + issuedToken, out var hit1));
            Assert.Equal("Pixel 9", hit1?.DeviceName);
            Assert.True(store.ValidateBearerToken("Bearer " + issuedToken, out var hit2));
            Assert.Equal("dev_cache_1", hit2?.DeviceId);

            // TTL rotation source keeps active per-client lockouts (no _pairLockoutsByClient.Clear() inside RotatePairingCodeIfExpiredUnlocked)
            string repoRoot = FindRepoRoot();
            string pairingSrc = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.SyncHost", "SyncOutboxAndPairingStore.cs"));
            int rotIdx = pairingSrc.IndexOf("private void RotatePairingCodeIfExpiredUnlocked", StringComparison.Ordinal);
            Assert.True(rotIdx >= 0);
            string rotBody = pairingSrc.Substring(rotIdx, Math.Min(700, pairingSrc.Length - rotIdx));
            Assert.DoesNotContain("_pairLockoutsByClient.Clear()", rotBody, StringComparison.Ordinal);

            // DesktopStateStore.ListQuickplayPins succeeds repeatedly via EnsureSchemaOnce + OpenReadOnly
            string stateDbPath = DesktopStateStore.ResolveStateDbPath(Path.Combine(tempRoot, ".tonewise", "manifest.db"));
            DesktopStateStore.UpsertQuickplayPin(stateDbPath, new QuickplayPinRecord(
                PinId: "p1", Kind: "album", TargetKey: "Beethoven", Title: "Sym 9",
                Subtitle: "Karajan", CoverPath: null, SortOrder: 0, PinnedAtMs: 1234L));
            Assert.Single(DesktopStateStore.ListQuickplayPins(stateDbPath));
            Assert.Single(DesktopStateStore.ListQuickplayPins(stateDbPath));

            // C6.1: CoverArtImageCache checks Cache before File.Exists and uses LruOrder eviction
            string cacheSrc = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "Services", "CoverArtImageCache.cs"));
            Assert.DoesNotContain("Cache.Clear();\n                }", cacheSrc, StringComparison.Ordinal);
            Assert.Contains("LruOrder", cacheSrc, StringComparison.Ordinal);
            int cacheIdx = cacheSrc.IndexOf("Cache.TryGetValue(cacheKey", StringComparison.Ordinal);
            int existsIdx = cacheSrc.IndexOf("File.Exists(coverArtPath)", StringComparison.Ordinal);
            Assert.True(cacheIdx > 0 && (existsIdx < 0 || existsIdx > cacheIdx), "CoverArtImageCache must check memory cache before or instead of File.Exists");

            // C7: Protocol SSOT schema alignment
            string manifestSql = File.ReadAllText(Path.Combine(repoRoot, "protocol", "schemas", "manifest.sql"));
            Assert.Contains("(v2)", manifestSql, StringComparison.Ordinal);
            string scenesSchema = File.ReadAllText(Path.Combine(repoRoot, "protocol", "schemas", "scenes_5d.schema.json"));
            Assert.Contains("\"composer\"", scenesSchema, StringComparison.Ordinal);
            Assert.Contains("\"conductor\"", scenesSchema, StringComparison.Ordinal);
            Assert.Contains("\"work_name\"", scenesSchema, StringComparison.Ordinal);
            Assert.Contains("\"neq\"", scenesSchema, StringComparison.Ordinal);
            string historySchema = File.ReadAllText(Path.Combine(repoRoot, "protocol", "schemas", "history.schema.json"));
            Assert.Contains("\"tempo_bpm\"", historySchema, StringComparison.Ordinal);
            Assert.Contains("\"mutation_key\"", historySchema, StringComparison.Ordinal);

            // D2 + D3 + D4: Version alignment, fail-closed Release guard, and Core+Desktop CI workflow
            string cargoToml = File.ReadAllText(Path.Combine(repoRoot, "core", "Cargo.toml"));
            Assert.Contains("version = \"1.0.13\"", cargoToml, StringComparison.Ordinal);
            string directoryProps = File.ReadAllText(Path.Combine(repoRoot, "desktop", "Directory.Build.props"));
            Assert.Contains("<Version>1.0.13</Version>", directoryProps, StringComparison.Ordinal);
            string desktopCsproj = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "Tonewise.Desktop.csproj"));
            Assert.Contains("EnsureNativeCoreBinariesInRelease", desktopCsproj, StringComparison.Ordinal);
            Assert.Contains("tonewise-cli.exe", File.ReadAllText(Path.Combine(repoRoot, "scripts", "build-core-desktop.ps1")), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(repoRoot, ".github", "workflows", "ci-core-desktop.yml")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Stage1_SafeSyncDefaults_DryRunOrganize_AndDestructiveActionGuardsHold()
    {
        // PC-UX-01: Safe USB Mirror Prune defaults
        var settingsDefaults = new DesktopUserSettings();
        Assert.False(settingsDefaults.EnableMirrorPrune);
        Assert.Equal("managed_only", settingsDefaults.MirrorPruneScope);
        Assert.False(settingsDefaults.AllowAdbForeignDeleteFallback);

        var policyDefaults = new SyncPolicyConfig();
        Assert.False(policyDefaults.MirrorPruneEnabled);
        Assert.Equal("managed_only", policyDefaults.PruneScope);
        Assert.False(policyDefaults.AllowAdbForeignDeleteFallback);

        // PC-UX-03: OrganizeLibraryFolders(..., dryRun: true) reports changes without touching disk or DB
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tonewise_stage1_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string unsortedDir = Path.Combine(tempRoot, "Inbox");
            Directory.CreateDirectory(unsortedDir);
            string srcFile = Path.Combine(unsortedDir, "01 Symphony.flac");
            File.WriteAllBytes(srcFile, [1, 2, 3, 4]);

            string repoRoot = FindRepoRoot();
            string dbDir = Path.Combine(tempRoot, ".tonewise");
            Directory.CreateDirectory(dbDir);
            string dbPath = Path.Combine(dbDir, "manifest.db");
            string manifestSchemaSql = File.ReadAllText(Path.Combine(repoRoot, "protocol", "schemas", "manifest.sql"));
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using (var schemaCmd = conn.CreateCommand())
                {
                    schemaCmd.CommandText = manifestSchemaSql;
                    schemaCmd.ExecuteNonQuery();
                }
                ManifestSchemaProvider.EnsureSchemaV2(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, album_artist, artists_json
                    ) VALUES (
                        'uid_dry_1', X'0102', 0, X'0304', 0,
                        'Inbox/01 Symphony.flac', 4, 1000, 'flac', 44100, 180000,
                        'Symphony No. 9', 'Beethoven Box', 'Karajan', '["Karajan"]'
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            var preview = Tonewise.SyncHost.Curation.LibraryFolderOrganizerService.OrganizeLibraryFolders(
                tempRoot, dbPath, dryRun: true);
            Assert.Equal(1, preview.MovedCount);
            Assert.Single(preview.SampleMoves);
            Assert.True(File.Exists(srcFile), "Dry-run must never move source file");
            Assert.False(Directory.Exists(Path.Combine(tempRoot, "Karajan")), "Dry-run must not create target folders");

            using (var checkConn = TonewiseSqlite.OpenReadOnly(dbPath))
            using (var checkCmd = checkConn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT rel_path FROM tracks WHERE track_uid = 'uid_dry_1';";
                Assert.Equal("Inbox/01 Symphony.flac", checkCmd.ExecuteScalar()?.ToString());
            }

            // PC-UX-02 & PC-UX-03 source contracts
            string mainCs = ReadAllMainWindowSources(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop"));
            Assert.Contains("ShowUndoableNotification", mainCs, StringComparison.Ordinal);
            Assert.Contains("_inlineUnsavedDrafts", mainCs, StringComparison.Ordinal);
            Assert.Contains("OnMirrorPruneSwitchToggled", mainCs, StringComparison.Ordinal);
            Assert.Contains("OnOrganizeLibraryFoldersClick", mainCs, StringComparison.Ordinal);
            Assert.Contains("ConfirmDestructiveActionAsync", mainCs, StringComparison.Ordinal);
            string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "MainWindow.xaml"));
            Assert.Contains("GlobalNotificationUndoButton", mainXaml, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Stage2_AccessibilityContrastTokensAndContractsHold()
    {
        double darkOnBlack = ZuneColorMath.ComputeContrastRatio(ZuneColorMath.DefaultDarkAccentRgb, (0, 0, 0));
        double whiteOnDarkAccent = ZuneColorMath.ComputeContrastRatio((255, 255, 255), ZuneColorMath.DefaultDarkAccentRgb);
        Assert.True(darkOnBlack >= 4.5, $"Dark accent vs black must meet AA (got {darkOnBlack:F2})");
        Assert.True(whiteOnDarkAccent >= 4.5, $"White ink on dark accent must meet AA (got {whiteOnDarkAccent:F2})");

        Assert.Equal((255, 255, 255), ZuneColorMath.ContrastingOnColor(ZuneColorMath.DefaultDarkAccentRgb.R, ZuneColorMath.DefaultDarkAccentRgb.G, ZuneColorMath.DefaultDarkAccentRgb.B));
        Assert.Equal((0, 0, 0), ZuneColorMath.ContrastingOnColor(255, 230, 80));

        string repoRoot = FindRepoRoot();
        string tokensXaml = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "Themes", "ZuneTokens.xaml"));
        Assert.Contains("#E5006B", tokensXaml, StringComparison.Ordinal);
        Assert.Contains("#737373", tokensXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#FF1E7A", tokensXaml, StringComparison.Ordinal);

        string clockXaml = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "DeskClockWidgetWindow.xaml"));
        Assert.Contains("#E5006B", clockXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#1A202C", clockXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#FF1E7A", clockXaml, StringComparison.Ordinal);

        string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "MainWindow.xaml"));
        Assert.Contains("AccessibilitySummary", mainXaml, StringComparison.Ordinal);
        Assert.Contains("HeartAutomationName", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"28\"", mainXaml, StringComparison.Ordinal);

        string mainCs = ReadAllMainWindowSources(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop"));
        Assert.Contains("AutomationProperties.SetItemStatus", mainCs, StringComparison.Ordinal);
        Assert.Contains("AccessibilitySettings", mainCs, StringComparison.Ordinal);
        Assert.Contains("_accessibilitySettings.HighContrast", mainCs, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage3_VisualConvergence_MinSize_AlbumsWall_AndBackStackHold()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");

        Assert.False(File.Exists(Path.Combine(desktopRoot, "Themes", "Tokens.xaml")), "Legacy Tokens.xaml must be removed");

        foreach (string file in Directory.EnumerateFiles(desktopRoot, "*.xaml", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories))
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                 && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("#FF1E7A", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0x1E, 0x7A", text, StringComparison.OrdinalIgnoreCase);
        }

        string mainCs = ReadAllMainWindowSources(desktopRoot);
        Assert.Contains("PreferredMinimumWidth =", mainCs, StringComparison.Ordinal);
        Assert.Contains("PreferredMinimumHeight =", mainCs, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.GoBack", mainCs, StringComparison.Ordinal);

        string vmLibCs = File.ReadAllText(Path.Combine(desktopRoot, "ViewModels", "MainViewModel.Library.cs"));
        Assert.Contains("CloseSelectedAlbum", vmLibCs, StringComparison.Ordinal);
        Assert.Contains("IsAlbumsWallExpanded", vmLibCs, StringComparison.Ordinal);
        Assert.Contains("public void GoBack()", vmLibCs, StringComparison.Ordinal);

        string mainXaml = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml"));
        Assert.Contains("IsAlbumsWallExpanded", mainXaml, StringComparison.Ordinal);
        Assert.Contains("GoBackCommand", mainXaml, StringComparison.Ordinal);
        int albumsIdx = mainXaml.IndexOf("x:Name=\"AlbumsCoverGridView\"", StringComparison.Ordinal);
        Assert.True(albumsIdx > 0);
        string albumsTag = mainXaml.Substring(albumsIdx, Math.Min(600, mainXaml.Length - albumsIdx));
        Assert.DoesNotContain("MaxHeight=\"240\"", albumsTag, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage4_PerformanceTimers_OffUiIo_SelectRange_AndModularMainWindowHold()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");

        var partFiles = Directory.EnumerateFiles(desktopRoot, "MainWindow*.cs", SearchOption.TopDirectoryOnly).ToList();
        Assert.True(partFiles.Count >= 4, $"Expected >= 4 modular MainWindow partial files, found {partFiles.Count}");
        int coreLineCount = File.ReadAllLines(Path.Combine(desktopRoot, "MainWindow.xaml.cs")).Length;
        Assert.True(coreLineCount < 1600, $"MainWindow.xaml.cs should stay compact (<1600 lines), was {coreLineCount}");

        string allMainCs = ReadAllMainWindowSources(desktopRoot);
        Assert.Contains("UpdateMainWindowTimers", allMainCs, StringComparison.Ordinal);
        Assert.Contains("OverlappedPresenterState.Minimized", allMainCs, StringComparison.Ordinal);
        Assert.Contains("SelectRange(new", allMainCs, StringComparison.Ordinal);

        string cacheCs = File.ReadAllText(Path.Combine(desktopRoot, "Services", "CoverArtImageCache.cs"));
        Assert.DoesNotContain("File.Exists", cacheCs, StringComparison.Ordinal);

        string mainXaml = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml"));
        Assert.Contains("<ItemsRepeater ItemsSource=\"{x:Bind ViewModel.SyncOutboxItems", mainXaml, StringComparison.Ordinal);
        Assert.Contains("<ItemsRepeater ItemsSource=\"{x:Bind ViewModel.AlternateRecordings", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage5_PrimaryNavLatencyCache_AndNowPlayingZuneActionBarHold()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");

        string vmLib = File.ReadAllText(Path.Combine(desktopRoot, "ViewModels", "MainViewModel.Library.cs"));
        int coverIdx = vmLib.IndexOf("GetUniqueCoverArtPaths", StringComparison.Ordinal);
        int quickplayIdx = vmLib.IndexOf("public void RefreshQuickplayCollections", StringComparison.Ordinal);
        Assert.True(coverIdx > 0 && quickplayIdx > coverIdx);
        string coverMethod = vmLib.Substring(coverIdx, quickplayIdx - coverIdx);
        Assert.DoesNotContain("File.Exists", coverMethod, StringComparison.Ordinal);

        Assert.Contains("_lastCollectionDataKey", vmLib, StringComparison.Ordinal);
        Assert.Contains("_quickplayDirty", vmLib, StringComparison.Ordinal);
        int applyNavIdx = vmLib.IndexOf("private void ApplyActiveNavigationData()", StringComparison.Ordinal);
        int rebuildIdx = vmLib.IndexOf("private void RebuildEntityGroups()", StringComparison.Ordinal);
        Assert.True(applyNavIdx > 0 && rebuildIdx > applyNavIdx);
        string applyNavBlock = vmLib.Substring(applyNavIdx, rebuildIdx - applyNavIdx);
        Assert.DoesNotContain("PrimaryNavId is \"quickplay\" or \"now_playing\"", applyNavBlock, StringComparison.Ordinal);

        string mainCs = ReadAllMainWindowSources(desktopRoot);
        Assert.Contains("NavigationSettled", mainCs, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayStaggeredEntrance(PivotHeaderRow", mainCs, StringComparison.Ordinal);

        string mainXaml = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml"));
        Assert.Contains("ZuneGalleryActionButtonStyle", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"查看艺人\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"查看作曲\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"查看指挥\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"编辑属性\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingComposerButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingConductorButton\"", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassicalComposerDetail_SupportsWorkAndMovementSelection_ContextMenu_And_MetadataEditing()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");

        string vmClassical = File.ReadAllText(Path.Combine(desktopRoot, "ViewModels", "MainViewModel.Classical.cs"));
        Assert.Contains("IsWorkbenchCommandBarVisible", vmClassical, StringComparison.Ordinal);
        Assert.Contains("SelectClassicalWorkTracks", vmClassical, StringComparison.Ordinal);
        Assert.Contains("SelectClassicalMovementTrack", vmClassical, StringComparison.Ordinal);
        Assert.Contains("SelectAllVisibleClassicalTracks", vmClassical, StringComparison.Ordinal);
        Assert.Contains("RestoreClassicalSelection", vmClassical, StringComparison.Ordinal);
        Assert.Contains("SyncClassicalMultiSelectMode", vmClassical, StringComparison.Ordinal);

        string viewItems = File.ReadAllText(Path.Combine(desktopRoot, "Models", "ClassicalViewItems.cs"));
        Assert.Contains("UpdateSelectionState", viewItems, StringComparison.Ordinal);
        Assert.Contains("SetMultiSelectVisible", viewItems, StringComparison.Ordinal);
        Assert.Contains("PickSelectionBackground", viewItems, StringComparison.Ordinal);

        string mainXaml = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml"));
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IsWorkbenchCommandBarVisible, Mode=OneWay}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("OnClassicalWorkCheckboxClick", mainXaml, StringComparison.Ordinal);
        Assert.Contains("OnEditClassicalWorkClick", mainXaml, StringComparison.Ordinal);
        Assert.Contains("OnClassicalWorkRightTapped", mainXaml, StringComparison.Ordinal);
        Assert.Contains("OnClassicalMovementTapped", mainXaml, StringComparison.Ordinal);
        Assert.Contains("OnClassicalMovementDoubleTapped", mainXaml, StringComparison.Ordinal);
        Assert.Contains("OnClassicalMovementRightTapped", mainXaml, StringComparison.Ordinal);
        Assert.Contains("OnClassicalMovementCheckboxClick", mainXaml, StringComparison.Ordinal);

        string mainCs = ReadAllMainWindowSources(desktopRoot);
        Assert.Contains("IsClassicalComposerDetailSelectionActive", mainCs, StringComparison.Ordinal);
        Assert.Contains("ResolveTargetTracksForAction", mainCs, StringComparison.Ordinal);
        Assert.Contains("ClassicalWorkViewItem work", mainCs, StringComparison.Ordinal);
        Assert.Contains("ClassicalMovementViewItem mov", mainCs, StringComparison.Ordinal);
        Assert.Contains("ResolveClassicalBoundItem", mainCs, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{x:Bind}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("RestoreClassicalSelection(savedUids)", mainCs, StringComparison.Ordinal);
    }

    [Fact]
    public void NowPlaying_NonClassical_FormatsThreeLines_TitleArtistAlbumYear()
    {
        var full = NowPlayingFormatter.FormatNonClassicalNowPlaying(
            "So What",
            "Miles Davis",
            "Kind of Blue",
            1959);
        Assert.Equal("So What", full.Line1);
        Assert.Equal("Miles Davis", full.Line2);
        Assert.Equal("Kind of Blue · 1959", full.Line3);

        var noYear = NowPlayingFormatter.FormatNonClassicalNowPlaying(
            "Track A",
            "Artist A",
            "Album A",
            null);
        Assert.Equal("Track A", noYear.Line1);
        Assert.Equal("Artist A", noYear.Line2);
        Assert.Equal("Album A", noYear.Line3);

        var noAlbum = NowPlayingFormatter.FormatNonClassicalNowPlaying(
            "Single B",
            "Artist B",
            "",
            2024);
        Assert.Equal("Single B", noAlbum.Line1);
        Assert.Equal("Artist B", noAlbum.Line2);
        Assert.Equal("2024", noAlbum.Line3);

        var minimal = NowPlayingFormatter.FormatNonClassicalNowPlaying(
            "Loose Demo",
            "Artist C",
            "",
            0);
        Assert.Equal("Loose Demo", minimal.Line1);
        Assert.Equal("Artist C", minimal.Line2);
        Assert.Equal("", minimal.Line3);
    }

    [Fact]
    public void ZuneMosaicWallState_BuildsMultiScaleGrid_DriftsSeamlessly_AndCrossfadesIndividualTiles()
    {
        var wall = new ZuneMosaicWallState();
        wall.EnsureLayout(1920, 1080, coverCount: 24);

        Assert.True(wall.PatternCols >= 12);
        Assert.True(wall.PatternRows >= 7);
        Assert.Contains(wall.Cells, c => c.Span == 1);
        Assert.Contains(wall.Cells, c => c.Span == 2);

        bool[,] occupied = new bool[wall.PatternRows, wall.PatternCols];
        foreach (var cell in wall.Cells)
        {
            for (int dr = 0; dr < cell.Span; dr++)
            {
                for (int dc = 0; dc < cell.Span; dc++)
                {
                    Assert.False(occupied[cell.Row + dr, cell.Col + dc], $"Overlapping cell at ({cell.Row + dr},{cell.Col + dc})");
                    occupied[cell.Row + dr, cell.Col + dc] = true;
                }
            }
        }
        for (int r = 0; r < wall.PatternRows; r++)
        {
            for (int c = 0; c < wall.PatternCols; c++)
            {
                Assert.True(occupied[r, c], $"Gap in mosaic grid at ({r},{c})");
            }
        }

        Assert.True(wall.Advance(0.05, 24));
        Assert.True(wall.DriftXPx > 0f);
        Assert.True(wall.DriftXPx < wall.PatternWidthPx);

        for (int i = 0; i < 42; i++)
        {
            wall.Advance(0.05, 24);
        }

        int transitioningCount = wall.Cells.Count(c => c.IsTransitioning);
        Assert.InRange(transitioningCount, 1, 2);
        Assert.True(wall.DriftXPx >= 0f && wall.DriftXPx < wall.PatternWidthPx);

        var activeCell = wall.Cells.First(c => c.IsTransitioning);
        int targetIdx = activeCell.NextCoverIndex;
        for (int i = 0; i < 20; i++)
        {
            wall.Advance(0.05, 24);
        }
        Assert.False(activeCell.IsTransitioning);
        Assert.Equal(targetIdx, activeCell.CurrentCoverIndex);

        string repoRoot = FindRepoRoot();
        string mainCs = ReadAllMainWindowSources(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop"));
        Assert.Contains("_mosaicAnimTimer", mainCs, StringComparison.Ordinal);
        Assert.Contains("ZuneMotionHelper.IsMotionEnabled()", mainCs, StringComparison.Ordinal);
        Assert.Contains("_accessibilitySettings.HighContrast", mainCs, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopP0ThroughP3_AccentContrast_VolumeMemory_ClassicalParity_AndSmtcGuardsHold()
    {
        var inkRgb = ZuneColorMath.ClampAccentInkForTheme(0xFF, 0x1E, 0x7A, isDarkTheme: true);
        double cardContrast = ZuneColorMath.ComputeContrastRatio(inkRgb, (0x1C, 0x1C, 0x1C));
        Assert.True(cardContrast >= 4.5, $"Dark accent ink contrast on #1C1C1C card was {cardContrast:F2}");

        var defaults = new DesktopUserSettings();
        Assert.True(defaults.LockVolumeOnBitPerfect);
        Assert.True(defaults.RememberVolumePerEndpoint);

        var volumeMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["WASAPI: Topping D90"] = -6.0,
            ["Speakers (Realtek)"] = -22.5,
        };
        string json = DesktopSettingsStore.SerializeEndpointVolumes(volumeMap);
        var restored = DesktopSettingsStore.DeserializeEndpointVolumes(json);
        Assert.Equal(-6.0, restored["wasapi: topping d90"], 2);
        Assert.Equal(-22.5, restored["Speakers (Realtek)"], 2);

        Assert.Equal(42, ClassicalMetadataEngine.ParseRomanNumeral("XLII"));
        Assert.Equal(99, ClassicalMetadataEngine.ParseRomanNumeral("XCIX"));
        Assert.Null(ClassicalMetadataEngine.ParseRomanNumeral("XXXX"));
        Assert.Equal("XCIX", ClassicalMetadataEngine.FormatRomanNumeral(99));
        Assert.Equal("Vaughan Williams", ClassicalMetadataEngine.ShortSurname("Ralph Vaughan Williams"));
        Assert.Equal("de Falla", ClassicalMetadataEngine.ShortSurname("Manuel de Falla"));
        Assert.Equal("谭盾", ClassicalMetadataEngine.ShortSurname("谭盾"));
        Assert.Equal("陈其钢", ClassicalMetadataEngine.ShortSurname("陈 其钢"));
        Assert.Equal("Beethoven", ClassicalMetadataEngine.ShortSurname("Ludwig van Beethoven"));

        string repoRoot = FindRepoRoot();
        string mainXaml = File.ReadAllText(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop", "MainWindow.xaml"));
        string mainCs = ReadAllMainWindowSources(Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop"));
        Assert.Contains("EndpointVolumeSafetyBar", mainXaml, StringComparison.Ordinal);
        Assert.Contains("DspResponseCanvas", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SeekHoverTimePill", mainXaml, StringComparison.Ordinal);
        Assert.Contains("LanSyncTransferText", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("切换乐团 / 独奏", mainXaml, StringComparison.Ordinal);
        Assert.Contains("PlaybackPositionChangeRequested", mainCs, StringComparison.Ordinal);
        Assert.Contains("UpdateTimelineProperties", mainCs, StringComparison.Ordinal);
        Assert.DoesNotContain("HighContrastChanged +=", mainCs, StringComparison.Ordinal);
        Assert.Contains("ActualThemeChanged +=", mainCs, StringComparison.Ordinal);
    }
}
