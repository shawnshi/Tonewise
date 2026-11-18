using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using Tonewise.Audio;
using Tonewise.Interop;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class WorkbenchP4Tests
{
    [Fact]
    public void FiveDimQueryBuilder_ExcludeVocalNotRule_AndEnergyArc_EvaluateViaRustCore()
    {
        var query = new FiveDimFilterQuery(
            Scene: "focus",
            ExcludeVocal: true,
            SortArc: "energy_asc",
            TempoNullPassthrough: true
        );
        Assert.True(FiveDimQueryBuilder.RequiresFilterOrSort(query));

        string sceneJson = FiveDimQueryBuilder.BuildSceneCardJson(query);
        string tracksJson = JsonSerializer.Serialize(new object[]
        {
            new { track_uid = "t_vocal", title = "Focus Vocal", dj_scene = new[] { "focus" }, dj_vocals = "vocal", dj_energy = "medium", tempo_bucket = (string?)null, moods = new[] { "calm" } },
            new { track_uid = "t_inst_med", title = "Focus Inst Med", dj_scene = new[] { "focus" }, dj_vocals = "instrumental", dj_energy = "medium", tempo_bucket = (string?)null, moods = new[] { "calm" } },
            new { track_uid = "t_inst_low", title = "Focus Inst Low", dj_scene = new[] { "focus" }, dj_vocals = "instrumental", dj_energy = "low", tempo_bucket = "Slow", moods = new[] { "calm" } },
            new { track_uid = "t_unk_low", title = "Focus Unk Low", dj_scene = new[] { "focus" }, dj_vocals = "unknown", dj_energy = "low", tempo_bucket = (string?)null, moods = new[] { "calm" } },
        });

        var matched = TonewiseCoreClient.EvaluateScene(sceneJson, tracksJson);
        Assert.DoesNotContain("t_vocal", matched);
        Assert.Equal(["t_inst_low", "t_unk_low", "t_inst_med"], matched);
    }

    [Fact]
    public void ClassicalAbSwitchHelper_FindsSameWorkMovementRecordings_AndMapsProgressRatio()
    {
        var recordings = new[]
        {
            new { Uid = "rec_karajan_1", Work = "Symphony No. 5 in C minor, Op. 67", Movement = "I. Allegro con brio", Conductor = "Karajan", DurationMs = 440_000L },
            new { Uid = "rec_kleiber_1", Work = "Symphony No. 5 in C minor, Op. 67", Movement = "i. allegro  con brio", Conductor = "Kleiber", DurationMs = 420_000L },
            new { Uid = "rec_kleiber_2", Work = "Symphony No. 5 in C minor, Op. 67", Movement = "II. Andante con moto", Conductor = "Kleiber", DurationMs = 590_000L },
            new { Uid = "rec_gould_1", Work = "Goldberg Variations, BWV 988", Movement = "Aria", Conductor = "Gould", DurationMs = 185_000L },
        };

        var current = recordings[0];
        var alternates = ClassicalAbSwitchHelper.FindAlternateRecordings(
            current,
            recordings,
            r => r.Uid,
            r => r.Work,
            r => r.Movement
        );

        Assert.Single(alternates);
        Assert.Equal("rec_kleiber_1", alternates[0].Uid);

        // 50% into 440,000 ms movement -> 210,000 ms into 420,000 ms recording
        long mappedMs = ClassicalAbSwitchHelper.ComputeTargetPositionMs(
            currentPositionMs: 220_000L,
            currentDurationMs: current.DurationMs,
            targetDurationMs: alternates[0].DurationMs
        );
        Assert.Equal(210_000L, mappedMs);
    }

    [Fact]
    public void OledBurnInGuard_BoundsPixelOffsetsWithinTwoPixels_AndCyclesCleanly()
    {
        var visited = new HashSet<(int, int)>();
        for (int tick = 0; tick < 28; tick++)
        {
            var (dx, dy) = OledBurnInGuard.ComputePixelOffset(tick);
            Assert.InRange(dx, -2, 2);
            Assert.InRange(dy, -2, 2);
            visited.Add((dx, dy));
        }

        Assert.True(visited.Count >= 10, "OLED offset table should visit distinct anti-burn-in positions");
    }

    [Fact]
    public void SessionSlotStore_InitializesFiveNamedPresets_AndPersistsSavedState()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "tw_p4_sessions_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var initial = SessionSlotStore.LoadOrInitializeDefault(tempFile);
            Assert.Equal(5, initial.Count);
            Assert.Equal(["Focus", "Coding", "Relax", "Energy", "Classical"], initial.Select(s => s.Name).ToArray());

            var updatedSlot = new SessionSlotDto(2, "Coding", "coding", 1_763_251_500_000UL, ["uid_a", "uid_b"], 1, 95_000UL);
            SessionSlotStore.SaveSlot(tempFile, updatedSlot);

            var reloaded = SessionSlotStore.LoadOrInitializeDefault(tempFile);
            Assert.Equal(5, reloaded.Count);
            Assert.Equal(95_000UL, reloaded[1].PositionMs);
            Assert.Equal(["uid_a", "uid_b"], reloaded[1].QueueTrackUids);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task PlaylistImporter_ParsesAppleMusic_Billboard_Kworb_AndTextFormats_Accurately()
    {
        const string appleHtml = """
            <html>
              <head>
                <meta property="og:title" content="2026 年度最佳歌曲 - Apple Music" />
              </head>
              <body>
                <script id="serialized-server-data" type="application/json">
                {
                  "data": [
                    {
                      "title": "2026 年度最佳歌曲",
                      "subtitleLinks": [{"title": "Apple Music"}],
                      "tracks": [
                        {
                          "itemKind": "trackLockup",
                          "title": "hate that i made you love me",
                          "artistName": "Ariana Grande",
                          "tertiaryLinks": [{"title": "petal"}]
                        },
                        {
                          "itemKind": "trackLockup",
                          "title": "I Knew It, I Knew You (From \"Toy Story 5\")",
                          "subtitleLinks": [{"title": "Taylor Swift"}],
                          "tertiaryLinks": [{"title": "I Knew It, I Knew You - Single"}]
                        }
                      ]
                    }
                  ]
                }
                </script>
              </body>
            </html>
            """;

        const string billboardHtml = """
            <html>
              <head><title>Greatest of All Time Hot 100 Songs – Billboard</title></head>
              <body>
                <div class="o-chart-results-list-row-container">
                  <li class="lrv-u-width-100p">
                    <h3 id="title-of-a-story" class="c-title">Blinding Lights</h3>
                    <span class="c-label">The Weeknd</span>
                  </li>
                </div>
                <div class="o-chart-results-list-row-container">
                  <li class="lrv-u-width-100p">
                    <h3 id="title-of-a-story" class="c-title">Uptown Funk!</h3>
                    <span class="c-label">Mark Ronson Featuring Bruno Mars</span>
                  </li>
                </div>
              </body>
            </html>
            """;

        const string kworbHtml = """
            <html>
              <head><title>Cumulative Global Pop Chart</title></head>
              <body>
                <table>
                  <tr><th>Pos</th><th class="text">Artist and Title</th><th>Popularity</th></tr>
                  <tr><td>1</td><td class="text"><div>Lady Gaga &amp; Bradley Cooper - Shallow</div></td><td>1.00000</td></tr>
                  <tr><td>2</td><td class="text"><div>Adele - Hello</div></td><td>0.97148</td></tr>
                </table>
              </body>
            </html>
            """;

        using var http = new HttpClient(new StubPlaylistHttpHandler(appleHtml, billboardHtml, kworbHtml));

        var apple = await PlaylistImporter.FetchAndParseUrlAsync(
            "https://music.apple.com/cn/playlist/2026-test/pl.d398088b5b114e038297c05d152ba1e4",
            maxTracks: 100,
            httpClient: http
        );
        Assert.Equal(PlaylistSourceKind.AppleMusic, apple.SourceKind);
        Assert.Equal("2026 年度最佳歌曲", apple.Title);
        Assert.Equal(2, apple.Tracks.Count);
        Assert.Equal("hate that i made you love me", apple.Tracks[0].Title);
        Assert.Equal("Ariana Grande", apple.Tracks[0].Artist);
        Assert.Equal("petal", apple.Tracks[0].Album);
        Assert.Equal("Taylor Swift", apple.Tracks[1].Artist);

        var bb = await PlaylistImporter.FetchAndParseUrlAsync(
            "https://www.billboard.com/charts/greatest-hot-100-singles/",
            maxTracks: 100,
            httpClient: http
        );
        Assert.Equal(PlaylistSourceKind.Billboard, bb.SourceKind);
        Assert.Equal("Greatest of All Time Hot 100 Songs", bb.Title);
        Assert.Equal(2, bb.Tracks.Count);
        Assert.Equal("Blinding Lights", bb.Tracks[0].Title);
        Assert.Equal("The Weeknd", bb.Tracks[0].Artist);
        Assert.Equal("Mark Ronson Featuring Bruno Mars", bb.Tracks[1].Artist);

        var kworb = await PlaylistImporter.FetchAndParseUrlAsync(
            "https://kworb.net/pop/cumulative.html",
            maxTracks: 100,
            httpClient: http
        );
        Assert.Equal(PlaylistSourceKind.Kworb, kworb.SourceKind);
        Assert.Equal("Cumulative Global Pop Chart", kworb.Title);
        Assert.Equal(2, kworb.Tracks.Count);
        Assert.Equal("Lady Gaga & Bradley Cooper", kworb.Tracks[0].Artist);
        Assert.Equal("Shallow", kworb.Tracks[0].Title);
        Assert.Equal("Adele", kworb.Tracks[1].Artist);
        Assert.Equal("Hello", kworb.Tracks[1].Title);

        const string textInput = """
            #EXTM3U
            #PLAYLIST:Mixed Text List
            1. Adele - Hello
            2) The Weeknd - Blinding Lights
            Artists/Shallow.flac
            """;
        var parsedText = PlaylistImporter.ParseTextPlaylist(textInput);
        Assert.Equal(PlaylistSourceKind.TextFile, parsedText.SourceKind);
        Assert.Equal("Mixed Text List", parsedText.Title);
        Assert.Equal(3, parsedText.Tracks.Count);
        Assert.Equal("Adele", parsedText.Tracks[0].Artist);
        Assert.Equal("Hello", parsedText.Tracks[0].Title);
        Assert.Equal("The Weeknd", parsedText.Tracks[1].Artist);
        Assert.Equal("Blinding Lights", parsedText.Tracks[1].Title);
        Assert.Equal("Artists/Shallow.flac", parsedText.Tracks[2].FilePath);
    }

    [Fact]
    public void PlaylistStore_AndMatcher_SupportFullCrud_AndLocalTrackResolution()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_p4_playlists_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var library = new List<LocalLibraryTrackRef>
            {
                new("uid_hello", "Adele/25/01 Hello.flac", Path.Combine(tempRoot, "Adele", "25", "01 Hello.flac"), "Hello", "Adele", "25", 295_000, 44100),
                new("uid_lionel", "Lionel Richie/Can't Slow Down/01 Hello.flac", Path.Combine(tempRoot, "Lionel Richie", "Can't Slow Down", "01 Hello.flac"), "Hello", "Lionel Richie", "Can't Slow Down", 250_000, 44100),
                new("uid_uptown", "Mark Ronson/Uptown Special/04 Uptown Funk.flac", Path.Combine(tempRoot, "Mark Ronson", "Uptown Special", "04 Uptown Funk.flac"), "Uptown Funk!", "Mark Ronson, Bruno Mars", "Uptown Special", 270_000, 96000),
            };

            var seeds = new List<ImportedTrackSeed>
            {
                new("Hello", "Adele"),
                new("Uptown Funk! (feat. Bruno Mars)", "Mark Ronson Featuring Bruno Mars"),
                new("Missing Track", "Unknown Artist"),
            };

            var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, library, tempRoot);
            Assert.Equal(3, resolved.Count);
            Assert.True(resolved[0].IsLocalMatched);
            Assert.Equal("uid_hello", resolved[0].MatchedTrackUid);
            Assert.True(resolved[1].IsLocalMatched);
            Assert.Equal("uid_uptown", resolved[1].MatchedTrackUid);
            Assert.False(resolved[2].IsLocalMatched);
            Assert.Null(resolved[2].RelPath);

            // Create
            string savedPath = PlaylistStore.SavePlaylist(tempRoot, "Top Chart Mix", resolved);
            Assert.True(File.Exists(savedPath));
            var list = PlaylistStore.ListPlaylists(tempRoot, library);
            Assert.Single(list);
            Assert.Equal("Top Chart Mix", list[0].Name);
            Assert.Equal(3, list[0].TotalCount);
            Assert.Equal(2, list[0].LocalMatchedCount);

            // View / Load
            var loaded = PlaylistStore.LoadPlaylist(savedPath, library, tempRoot);
            Assert.Equal(3, loaded.Entries.Count);
            Assert.Equal("Adele/25/01 Hello.flac", loaded.Entries[0].RelPath);
            Assert.False(loaded.Entries[2].IsLocalMatched);

            // Edit (reorder, remove unmatched, rename)
            var editedEntries = new List<PlaylistEntry> { loaded.Entries[1], loaded.Entries[0] };
            string renamedPath = PlaylistStore.SavePlaylist(tempRoot, "Curated Top 2", editedEntries, savedPath);
            Assert.False(File.Exists(savedPath));
            Assert.True(File.Exists(renamedPath));

            var reloaded = PlaylistStore.LoadPlaylist(renamedPath, library, tempRoot);
            Assert.Equal("Curated Top 2", reloaded.Name);
            Assert.Equal(2, reloaded.Entries.Count);
            Assert.Equal("uid_uptown", reloaded.Entries[0].MatchedTrackUid);
            Assert.Equal("uid_hello", reloaded.Entries[1].MatchedTrackUid);

            // Delete
            PlaylistStore.DeletePlaylist(renamedPath);
            Assert.Empty(PlaylistStore.ListPlaylists(tempRoot, library));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void StagingDiffStore_ApplyDirectCuratedEdits_Updates5DCapsulesAndRating_Cleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p4_lexicon_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manifest.db");

        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? schemaPath = null;
            while (dir is not null)
            {
                string cand = Path.Combine(dir.FullName, "protocol", "schemas", "manifest.sql");
                if (File.Exists(cand))
                {
                    schemaPath = cand;
                    break;
                }
                dir = dir.Parent;
            }
            Assert.NotNull(schemaPath);

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = File.ReadAllText(schemaPath!);
                setup.ExecuteNonQuery();

                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, dj_scene_json, dj_vocals, dj_energy, mood_json, rating
                    ) VALUES (
                        'uid_lex_1', X'01', 100, X'02', 4096,
                        'Artist/01.flac', 2048, 1000, 'flac', 96000, 180000,
                        'Deep Study', '["relax"]', 'vocal', 'medium', '["Calm"]', 2
                    ), (
                        'uid_lex_2', X'03', 100, X'04', 4096,
                        'Artist/02.flac', 2048, 1000, 'flac', 96000, 200000,
                        'Late Night Code', '[]', NULL, NULL, '[]', 0
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            var store = new StagingDiffStore(dbPath);
            var updates = new Dictionary<string, string?>
            {
                ["dj_scene"] = "[\"focus\",\"coding\"]",
                ["dj_energy"] = "low",
                ["dj_vocals"] = "instrumental",
                ["mood"] = "[\"Calm\",\"Focus\"]",
                ["rating"] = "5",
            };

            var res = store.ApplyDirectCuratedEdits(["uid_lex_1", "uid_lex_2"], updates);
            Assert.Equal(10, res.CommittedCount);
            Assert.StartsWith("batch-lexicon-", res.BatchId);
            Assert.Empty(store.GetPendingProposals());

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT dj_scene_json, dj_energy, dj_vocals, mood_json, rating FROM tracks WHERE track_uid = 'uid_lex_1';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("[\"focus\",\"coding\"]", r.GetString(0));
                Assert.Equal("low", r.GetString(1));
                Assert.Equal("instrumental", r.GetString(2));
                Assert.Equal("[\"Calm\",\"Focus\"]", r.GetString(3));
                Assert.Equal(5, r.GetInt32(4));
            }

            int rolled = store.RollbackBatch(res.BatchId);
            Assert.Equal(10, rolled);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT dj_scene_json, dj_energy, dj_vocals, rating FROM tracks WHERE track_uid = 'uid_lex_1';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("[\"relax\"]", r.GetString(0));
                Assert.Equal("medium", r.GetString(1));
                Assert.Equal("vocal", r.GetString(2));
                Assert.Equal(2, r.GetInt32(3));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void StagingDiffStore_ApplyDirectCuratedEdits_UpdatesF2BasicFields_Cleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p4_f2_meta_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manifest.db");

        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? schemaPath = null;
            while (dir is not null)
            {
                string cand = Path.Combine(dir.FullName, "protocol", "schemas", "manifest.sql");
                if (File.Exists(cand))
                {
                    schemaPath = cand;
                    break;
                }
                dir = dir.Parent;
            }
            Assert.NotNull(schemaPath);

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = File.ReadAllText(schemaPath!);
                setup.ExecuteNonQuery();
            }

            var store = new StagingDiffStore(dbPath);

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, year, publisher, genre, work_name, movement_name
                    ) VALUES (
                        'uid_f2_1', X'11', 100, X'12', 4096,
                        'Classical/01.flac', 4096, 1000, 'flac', 96000, 440000,
                        'Old Movement Title', 'Old Album', 1975, 'Old Label', 'Unknown', 'Old Work', 'I. Old'
                    );
                    """;
                ins.ExecuteNonQuery();
            }
            var updates = new Dictionary<string, string?>
            {
                ["title"] = "Symphony No. 5: I. Allegro con brio",
                ["album"] = "Beethoven: Symphonies Nos. 5 & 7",
                ["year"] = "1977",
                ["publisher"] = "Deutsche Grammophon",
                ["genre"] = "Classical",
                ["work_name"] = "Symphony No. 5 in C minor, Op. 67",
                ["movement_name"] = "I. Allegro con brio",
            };

            var res = store.ApplyDirectCuratedEdits(["uid_f2_1"], updates);
            Assert.Equal(7, res.CommittedCount);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT title, album, year, publisher, genre, work_name, movement_name FROM tracks WHERE track_uid = 'uid_f2_1';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("Symphony No. 5: I. Allegro con brio", r.GetString(0));
                Assert.Equal("Beethoven: Symphonies Nos. 5 & 7", r.GetString(1));
                Assert.Equal(1977, r.GetInt32(2));
                Assert.Equal("Deutsche Grammophon", r.GetString(3));
                Assert.Equal("Classical", r.GetString(4));
                Assert.Equal("Symphony No. 5 in C minor, Op. 67", r.GetString(5));
                Assert.Equal("I. Allegro con brio", r.GetString(6));
            }

            int rolled = store.RollbackBatch(res.BatchId);
            Assert.Equal(7, rolled);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT title, album, year, publisher, work_name, movement_name FROM tracks WHERE track_uid = 'uid_f2_1';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("Old Movement Title", r.GetString(0));
                Assert.Equal("Old Album", r.GetString(1));
                Assert.Equal(1975, r.GetInt32(2));
                Assert.Equal("Old Label", r.GetString(3));
                Assert.Equal("Old Work", r.GetString(4));
                Assert.Equal("I. Old", r.GetString(5));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WorkbenchDomainLogic_ArtistSplitter_AndAdaptiveClassicalPolicy_WorkCleanly()
    {
        var split1 = ArtistEntitySplitter.SplitPerformers("Above & Beyond / Justine Suissa");
        Assert.Equal(["Above & Beyond", "Justine Suissa"], split1);

        var split2 = ArtistEntitySplitter.SplitPerformers("Herbert von Karajan; Berliner Philharmoniker");
        Assert.Equal(["Herbert von Karajan", "Berliner Philharmoniker"], split2);

        // Adaptive classical sub-items expand at >=20% when sidebar is expanded, collapse when rail is collapsed
        Assert.False(AdaptiveClassicalHierarchyPolicy.ShouldShowSidebarSubItems(19, 100, alwaysExpandUserSetting: false, isSidebarExpanded: true));
        Assert.True(AdaptiveClassicalHierarchyPolicy.ShouldShowSidebarSubItems(20, 100, alwaysExpandUserSetting: false, isSidebarExpanded: true));
        Assert.True(AdaptiveClassicalHierarchyPolicy.ShouldShowSidebarSubItems(1, 100, alwaysExpandUserSetting: true, isSidebarExpanded: true));
        Assert.False(AdaptiveClassicalHierarchyPolicy.ShouldShowSidebarSubItems(80, 100, alwaysExpandUserSetting: true, isSidebarExpanded: false));
    }

    [Fact]
    public void SearchQueryDslParser_ParsesSceneVocalsEnergyNavigationAndEntityPrefixes()
    {
        var s = SearchQueryDslParser.Parse("scene:focus · 深度专注场景");
        Assert.Equal("scene", s.Kind);
        Assert.Equal("focus", s.Value);

        var v = SearchQueryDslParser.Parse("vocals:instrumental");
        Assert.Equal("vocals", v.Kind);
        Assert.Equal("instrumental", v.Value);

        var e = SearchQueryDslParser.Parse("energy:low");
        Assert.Equal("energy", e.Kind);
        Assert.Equal("low", e.Value);

        var n = SearchQueryDslParser.Parse("classical:works · 古典作品聚合");
        Assert.Equal("nav", n.Kind);
        Assert.Equal("classical", n.Value);
        Assert.Equal("works", n.SubTab);

        var ent = SearchQueryDslParser.Parse("Work: Symphony No. 5 in C minor");
        Assert.Equal("text", ent.Kind);
        Assert.Equal("Symphony No. 5 in C minor", ent.Value);

        var raw = SearchQueryDslParser.Parse("Karajan");
        Assert.Equal("text", raw.Kind);
        Assert.Equal("Karajan", raw.Value);
    }

    [Fact]
    public void AudioEngineAndFirConvolver_SuspendActiveFile_AndReadImpulseOutsideLock_Cleanly()
    {
        string tempSofa = Path.Combine(Path.GetTempPath(), "tw_ir_" + Guid.NewGuid().ToString("N") + ".sofa");
        try
        {
            string sofaJson = """{"left_ir":[1.0,0.5,0.25,0.0],"right_ir":[1.0,0.4,0.2,0.0]}""";
            File.WriteAllText(tempSofa, sofaJson, Encoding.UTF8);

            var (left, right) = PartitionedFirConvolver.ReadImpulseArraysFromSofaOrWav(tempSofa);
            Assert.Equal(4, left.Length);
            Assert.Equal(4, right.Length);

            var dsp = new DspSampleProvider(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).ToSampleProvider());
            dsp.ConfigureFirFromWav(tempSofa, dryWetMix: 0.75);
            Assert.Equal(4, dsp.FirTapCount);

            using var engine = new TonewiseAudioEngine();
            Assert.Null(engine.CurrentFilePath);
            Assert.Equal(TimeSpan.Zero, engine.Position);
            Assert.Equal(TimeSpan.Zero, engine.Duration);

            // When idle or editing an unplayed path, suspend token is a safe no-op
            using (engine.SuspendIfPlayingFile([@"D:\nonexistent\test.flac"]))
            {
                Assert.False(engine.IsPlaying);
            }
        }
        finally
        {
            if (File.Exists(tempSofa)) File.Delete(tempSofa);
        }
    }

    [Fact]
    public void DesktopSettingsStore_PersistsAllAudioDspUiSettingsAndLastMusicRoot()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_settings_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "desktop_settings.json");

        try
        {
            var expected = new DesktopUserSettings(
                LastMusicRoot: @"D:\Music\Lossless",
                PreferWasapiExclusive: false,
                IsDacHardwareLocked: true,
                SelectedEndpointId: "{0.0.0.00000000}.{dac-test-id}",
                IsIso226Enabled: true,
                VolumeAttenuationDb: -12.5,
                IsTpdfDitherEnabled: false,
                FirDryWetMix: 0.65,
                ActiveEqFilePath: @"D:\EQ\hd800s.txt",
                FirImpulseFilePath: @"D:\IR\room.wav",
                AlwaysExpandClassicalSubItems: true,
                IsSidebarExpanded: false,
                IsInspectorOpen: false);

            DesktopSettingsStore.Save(expected, settingsPath);
            Assert.True(File.Exists(settingsPath));

            var restored = DesktopSettingsStore.Load(settingsPath);
            Assert.Equal(expected, restored);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void StagingDiffStore_SupportsAll17InspectorFields_BoundedApprovedCommit_AndNormalization()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p5_curation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manifest.db");

        try
        {
            var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
            while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "protocol", "schemas", "manifest.sql")))
            {
                repoRoot = repoRoot.Parent;
            }
            Assert.NotNull(repoRoot);
            string schemaSql = File.ReadAllText(Path.Combine(repoRoot!.FullName, "protocol", "schemas", "manifest.sql"));

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
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES (
                        'uid_full', X'01', 128, X'02', 4096,
                        'Classical/01.flac', 1024, 1000, 'flac', 96000, 320000, 'Old Title'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            var store = new StagingDiffStore(dbPath, tempDir);
            var edits = new Dictionary<string, string?>
            {
                ["title"] = "Symphony No. 5: I. Allegro con brio",
                ["artists_json"] = "Berliner Philharmoniker / Above & Beyond",
                ["album"] = "Beethoven: Symphonies Nos. 5 & 7",
                ["composers_json"] = "Ludwig van Beethoven",
                ["conductors_json"] = "Carlos Kleiber",
                ["work_name"] = "Symphony No. 5 in C minor, Op. 67",
                ["movement_name"] = "I. Allegro con brio",
                ["year"] = "1975",
                ["genre"] = "Classical",
                ["publisher"] = "Deutsche Grammophon",
                ["tempo_bpm"] = "108.0",
                ["tempo_bucket"] = "Medium", // Normalized cleanly to 'Moderate'
                ["dj_vocals"] = "instrumental",
                ["dj_energy"] = "high",
                ["dj_scene_json"] = "focus; symphony",
                ["mood_json"] = "dramatic; heroic",
                ["rating"] = "5",
            };

            var directResult = store.ApplyDirectCuratedEdits(["uid_full"], edits);
            Assert.Equal(17, directResult.CommittedCount);

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var check = conn.CreateCommand();
                check.CommandText = """
                    SELECT title, artists_json, composers_json, conductors_json, work_name, movement_name,
                           year, genre, publisher, tempo_bpm, tempo_bucket, dj_vocals, dj_energy,
                           dj_scene_json, mood_json, rating
                    FROM tracks WHERE track_uid = 'uid_full';
                    """;
                using var r = check.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("Symphony No. 5: I. Allegro con brio", r.GetString(0));
                Assert.Equal(["Berliner Philharmoniker", "Above & Beyond"], JsonSerializer.Deserialize<string[]>(r.GetString(1))!);
                Assert.Equal(["Ludwig van Beethoven"], JsonSerializer.Deserialize<string[]>(r.GetString(2))!);
                Assert.Equal(["Carlos Kleiber"], JsonSerializer.Deserialize<string[]>(r.GetString(3))!);
                Assert.Equal("Symphony No. 5 in C minor, Op. 67", r.GetString(4));
                Assert.Equal("I. Allegro con brio", r.GetString(5));
                Assert.Equal(1975, r.GetInt32(6));
                Assert.Equal("Classical", r.GetString(7));
                Assert.Equal("Deutsche Grammophon", r.GetString(8));
                Assert.Equal(108.0, r.GetDouble(9));
                Assert.Equal("Moderate", r.GetString(10));
                Assert.Equal("instrumental", r.GetString(11));
                Assert.Equal("high", r.GetString(12));
                Assert.Equal(["focus", "symphony"], JsonSerializer.Deserialize<string[]>(r.GetString(13))!);
                Assert.Equal(["dramatic", "heroic"], JsonSerializer.Deserialize<string[]>(r.GetString(14))!);
                Assert.Equal(5, r.GetInt32(15));
            }

            // Enqueue 3 Pending high-confidence proposals, mark ONLY 1 as Approved, commit with threshold > 1.0
            var proposals = new[]
            {
                new TagDiffProposal("p1", "uid_full", "genre", null, "Orchestral", 0.90, "Rule"),
                new TagDiffProposal("p2", "uid_full", "publisher", null, "DG Originals", 0.92, "Rule"),
                new TagDiffProposal("p3", "uid_full", "album", null, "B", 0.95, "Rule"),
            };
            Assert.Equal(3, store.EnqueueProposals(proposals, forceOverwriteUnlocked: true));
            store.SetProposalApproval("p2", approved: true);

            var (approvedCnt, pendingCnt) = store.GetPendingProposalCounts();
            Assert.Equal(1, approvedCnt);
            Assert.Equal(2, pendingCnt);

            var approvedOnlyCommit = store.CommitApprovedBatch("batch-approved-only", autoApproveThreshold: 1.01, maxProposals: 100, maxPhysicalFiles: 0);
            Assert.Equal(1, approvedOnlyCommit.CommittedCount);
            Assert.Equal(["uid_full"], approvedOnlyCommit.CommittedTrackUids);

            var (approvedAfter, pendingAfter) = store.GetPendingProposalCounts();
            Assert.Equal(0, approvedAfter);
            Assert.Equal(2, pendingAfter);

            Assert.Equal(2, store.CountPendingAtOrAbove(0.80));
            store.SetProposalApproval("p1", approved: false); // p1 -> Rejected
            Assert.Equal(1, store.ApproveAllHighConfidence(0.80)); // p3 -> Approved
            // Clear removes Pending/Rejected only (p1), preserving explicitly Approved p3
            Assert.Equal(1, store.ClearPendingProposals());
            var (approvedKept, pendingLeft) = store.GetPendingProposalCounts();
            Assert.Equal(1, approvedKept);
            Assert.Equal(0, pendingLeft);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TonewiseCoreClient_WriteAudioTags_DoesNotCrashOnMissingFile()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            TonewiseCoreClient.WriteAudioTags(
                @"D:\nonexistent\missing_track.flac",
                new Dictionary<string, string?> { ["title"] = "Safe Error" }));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void DesktopSettingsStore_Persists_ShuffleEnabled_And_RepeatMode()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p4_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "desktop_settings.json");

        try
        {
            var initial = new DesktopUserSettings
            {
                LastMusicRoot = @"D:\music",
                IsShuffleEnabled = true,
                RepeatMode = "one",
                VolumeAttenuationDb = -3.5,
            };
            DesktopSettingsStore.Save(initial, settingsPath);

            var loaded = DesktopSettingsStore.Load(settingsPath);
            Assert.NotNull(loaded);
            Assert.Equal(@"D:\music", loaded!.LastMusicRoot);
            Assert.True(loaded.IsShuffleEnabled);
            Assert.Equal("one", loaded.RepeatMode);
            Assert.Equal(-3.5, loaded.VolumeAttenuationDb);

            var defaults = new DesktopUserSettings();
            Assert.False(defaults.IsShuffleEnabled);
            Assert.Equal("all", defaults.RepeatMode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AutoDjPlaylistGenerator_Builds50Tracks_With70PercentFrequent_30PercentDiscovery_AndSmoothTransitions()
    {
        var candidates = new List<AutoDjTrackCandidate>(120);
        string[] calmMoods = ["Warm", "Calm", "Chill", "Mellow", "Dreamy"];
        string[] energeticMoods = ["Aggressive", "Energetic", "Dark"];

        // 60 frequently played tracks (most fit evening profile 78..110 BPM; 10 extreme 175 BPM outliers)
        for (int i = 0; i < 60; i++)
        {
            bool outlier = i >= 50;
            candidates.Add(new AutoDjTrackCandidate(
                TrackUid: $"freq_{i:D2}",
                Title: $"Frequent Song {i}",
                Artist: $"Artist {i % 25}",
                EffectivePlayCount: 5 + (i % 40),
                Rating: i % 3 == 0 ? 5 : 4,
                TempoBpm: outlier ? 175.0 : 80.0 + (i % 16) * 2.0,
                TempoBucket: outlier ? "Very Fast" : "Moderate",
                DjEnergy: outlier ? "high" : (i % 2 == 0 ? "low" : "medium"),
                DjVocals: "vocal",
                DjScene: outlier ? ["energy"] : ["relax"],
                Moods: outlier ? energeticMoods : [calmMoods[i % calmMoods.Length]]
            ));
        }

        // 60 unplayed / rarely played discovery tracks (50 fit evening profile; 10 extreme 178 BPM outliers)
        for (int i = 0; i < 60; i++)
        {
            bool outlier = i >= 50;
            candidates.Add(new AutoDjTrackCandidate(
                TrackUid: $"disc_{i:D2}",
                Title: $"Discovery Song {i}",
                Artist: $"DiscoveryArtist {i % 25}",
                EffectivePlayCount: i % 2, // 0 or 1 play
                Rating: 0,
                TempoBpm: outlier ? 178.0 : 82.0 + (i % 15) * 2.0,
                TempoBucket: outlier ? "Very Fast" : "Slow",
                DjEnergy: outlier ? "high" : "low",
                DjVocals: "instrumental",
                DjScene: outlier ? ["energy"] : ["relax", "focus"],
                Moods: outlier ? energeticMoods : [calmMoods[i % calmMoods.Length]]
            ));
        }

        var res = AutoDjPlaylistGenerator.Generate(
            candidates,
            localNow: new DateTime(2026, 11, 18, 20, 15, 0),
            targetCount: 50,
            randomSeed: 42);

        Assert.Equal("evening", res.Profile.PeriodId);
        Assert.Equal(50, res.OrderedTrackUids.Count);
        Assert.Equal(50, res.OrderedTrackUids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(35, res.FrequentCount);
        Assert.Equal(15, res.DiscoveryCount);

        var byUid = candidates.ToDictionary(c => c.TrackUid, StringComparer.Ordinal);
        double totalBpmStep = 0;
        for (int i = 0; i < res.OrderedTrackUids.Count; i++)
        {
            var c = byUid[res.OrderedTrackUids[i]];
            Assert.True(c.TempoBpm < 150.0, $"Outlier {c.TrackUid} with {c.TempoBpm} BPM should not be picked for evening AutoDJ.");
            if (i > 0)
            {
                totalBpmStep += Math.Abs(c.TempoBpm!.Value - byUid[res.OrderedTrackUids[i - 1]].TempoBpm!.Value);
            }
        }
        double avgBpmStep = totalBpmStep / (res.OrderedTrackUids.Count - 1);
        Assert.True(avgBpmStep <= 8.0, $"Expected smooth BPM transitions (avg step <= 8), got {avgBpmStep:0.00}");
    }

    [Fact]
    public void LibraryFolderOrganizer_MovesFlatAndUnprefixedAndOverlongFiles_ToCanonicalStructure_AndUpdatesDb()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p4_org_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manifest.db");

        try
        {
            string schemaPath = FindSchemaSqlPath();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = File.ReadAllText(schemaPath);
                setup.ExecuteNonQuery();

                void InsertTrack(string uid, byte hashByte, string relPath, string title, string? album, string? albumArtist, string artistsJson)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO tracks (
                            track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                            rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                            title, album, album_artist, artists_json
                        ) VALUES (
                            $uid, $af, 64, $hb, 0,
                            $rel, 512, 1000, 'flac', 44100, 180000,
                            $ti, $al, $aa, $ar
                        );
                        """;
                    byte[] h = new byte[32];
                    h[0] = hashByte;
                    cmd.Parameters.AddWithValue("$uid", uid);
                    cmd.Parameters.AddWithValue("$af", h);
                    cmd.Parameters.AddWithValue("$hb", h);
                    cmd.Parameters.AddWithValue("$rel", relPath);
                    cmd.Parameters.AddWithValue("$ti", title);
                    cmd.Parameters.AddWithValue("$al", (object?)album ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$aa", (object?)albumArtist ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$ar", artistsJson);
                    cmd.ExecuteNonQuery();
                }

                // 1. Flat file in root with embedded FLAC Vorbis Comment (disc=1, track=1)
                string flatPath = Path.Combine(tempDir, "Adele - Easy On Me.flac");
                WriteMinimalFlacWithVorbisComments(flatPath, ("ALBUMARTIST", "Adele"), ("ALBUM", "30"), ("TITLE", "Easy On Me"), ("DISCNUMBER", "1"), ("TRACKNUMBER", "1"));
                InsertTrack("u_flat", 1, "Adele - Easy On Me.flac", "Easy On Me", "30", null, "[\"Adele\"]");

                // 2. 3-segment folder missing <Disc-Track#> prefix (has "15 ...")
                string noDiscDir = Path.Combine(tempDir, "Various Artists", "Jazz Mix");
                Directory.CreateDirectory(noDiscDir);
                string noDiscPath = Path.Combine(noDiscDir, "15 When Will I See You Again.flac");
                File.WriteAllBytes(noDiscPath, [0x66, 0x4C, 0x61, 0x43]);
                InsertTrack("u_nodisc", 2, "Various Artists/Jazz Mix/15 When Will I See You Again.flac", "When Will I See You Again", "Jazz Mix", null, "[\"Tata\"]");

                // 3. Already compliant file (must stay untouched)
                string okDir = Path.Combine(tempDir, "The Beatles", "Abbey Road");
                Directory.CreateDirectory(okDir);
                string okPath = Path.Combine(okDir, "1-01 Come Together.flac");
                File.WriteAllBytes(okPath, [0x66, 0x4C, 0x61, 0x43]);
                InsertTrack("u_ok", 3, "The Beatles/Abbey Road/1-01 Come Together.flac", "Come Together", "Abbey Road", "The Beatles", "[\"The Beatles\"]");

                // 4. Overlong path (>255 chars) condensed safely within 235 chars
                string longArtist = "Yehudi Menuhin; Antonio Vivaldi; Antonin Dvorak; Claude Debussy; Cesar Franck; Felix Mendelssohn; Fritz Kreisler";
                string longAlbum = "The Complete Legendary Violin Concertos and Sonatas Recorded in London and Paris";
                string longFileName = "1-01 Violin Concerto in D Major Op 61 Movement I Allegro Ma Non Troppo Cadenza by Kreisler.flac";
                string longRel = $"{longArtist}/{longAlbum}/{longFileName}";
                string longFull = Path.Combine(tempDir, longArtist, longAlbum, longFileName);
                Assert.True(longFull.Length > 250);
                LongPathHelper.EnsureDirectory(Path.GetDirectoryName(longFull)!);
                File.WriteAllBytes(LongPathHelper.ToExtendedPath(longFull), [0x66, 0x4C, 0x61, 0x43]);
                InsertTrack("u_long", 4, longRel, "Violin Concerto in D Major Op. 61: I. Allegro Ma Non Troppo", longAlbum, longArtist, "[\"Yehudi Menuhin\"]");

                // 5. Stale track missing from disk
                InsertTrack("u_stale", 5, "Ghost/Missing/1-01 Gone.flac", "Gone", "Missing", "Ghost", "[\"Ghost\"]");
            }

            var summary = LibraryFolderOrganizerService.OrganizeLibraryFolders(tempDir, dbPath);
            Assert.Equal(5, summary.TotalChecked);
            Assert.Equal(3, summary.MovedCount);
            Assert.Equal(1, summary.LongPathFixedCount);
            Assert.Equal(1, summary.StaleRemovedCount);
            Assert.Equal(1, summary.SkippedCount);

            Assert.True(File.Exists(Path.Combine(tempDir, "Adele", "30", "1-01 Easy On Me.flac")));
            Assert.True(File.Exists(Path.Combine(tempDir, "Various Artists", "Jazz Mix", "1-15 When Will I See You Again.flac")));
            Assert.True(File.Exists(Path.Combine(tempDir, "The Beatles", "Abbey Road", "1-01 Come Together.flac")));
            Assert.False(File.Exists(Path.Combine(tempDir, "Adele - Easy On Me.flac")));

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q1 = conn.CreateCommand();
                q1.CommandText = "SELECT rel_path FROM tracks WHERE track_uid = 'u_flat';";
                Assert.Equal("Adele/30/1-01 Easy On Me.flac", q1.ExecuteScalar()?.ToString());

                using var q2 = conn.CreateCommand();
                q2.CommandText = "SELECT rel_path FROM tracks WHERE track_uid = 'u_long';";
                string newLongRel = q2.ExecuteScalar()?.ToString() ?? "";
                Assert.True(Path.Combine(tempDir, newLongRel.Replace('/', '\\')).Length <= 235);
                Assert.StartsWith("Yehudi Menuhin", newLongRel);

                using var q3 = conn.CreateCommand();
                q3.CommandText = "SELECT COUNT(*) FROM tracks WHERE track_uid = 'u_stale';";
                Assert.Equal(0L, Convert.ToInt64(q3.ExecuteScalar()));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(LongPathHelper.ToExtendedPath(tempDir)))
                Directory.Delete(LongPathHelper.ToExtendedPath(tempDir), recursive: true);
        }
    }

    [Fact]
    public void DspSampleProvider_SignalsReachedEndOfStream_WhenSourceReturnsZero()
    {
        var dsp = new DspSampleProvider(new FiniteSampleProvider(128));
        float[] buf = new float[256];

        int firstRead = dsp.Read(buf, 0, 128);
        Assert.Equal(128, firstRead);
        Assert.False(dsp.ReachedEndOfStream);

        int eofRead = dsp.Read(buf, 0, 128);
        Assert.Equal(0, eofRead);
        Assert.True(dsp.ReachedEndOfStream);

        dsp.ResetEndOfStream();
        Assert.False(dsp.ReachedEndOfStream);
    }

    [Fact]
    public async Task LibraryFileWatcher_DetectsAddedAndDeletedAudioFiles_IgnoresInternalDir_AndRespectsSuspension()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p4_watch_" + Guid.NewGuid().ToString("N"));
        string tonewiseDir = Path.Combine(tempDir, ".tonewise");
        Directory.CreateDirectory(tonewiseDir);
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");

        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = File.ReadAllText(FindSchemaSqlPath());
                setup.ExecuteNonQuery();
            }

            var callbackTcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            int fireCount = 0;

            using var watcher = new LibraryFileWatcherService(tempDir, async changed =>
            {
                Interlocked.Increment(ref fireCount);
                foreach (string p in changed)
                {
                    if (p.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                    {
                        var tags = LibraryFolderOrganizerService.ReadEmbeddedHeaderTags(p);
                        using var conn = new SqliteConnection($"Data Source={dbPath}");
                        conn.Open();
                        using var ins = conn.CreateCommand();
                        ins.CommandText = """
                            INSERT OR REPLACE INTO tracks (
                                track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                                rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                                title, album, album_artist
                            ) VALUES (
                                'u_watch_1', X'0102', 64, X'0304', 0,
                                $rel, 128, 1000, 'flac', 44100, 1000,
                                $ti, $al, $aa
                            );
                            """;
                        ins.Parameters.AddWithValue("$rel", Path.GetRelativePath(tempDir, p).Replace('\\', '/'));
                        ins.Parameters.AddWithValue("$ti", tags.Title ?? Path.GetFileNameWithoutExtension(p));
                        ins.Parameters.AddWithValue("$al", tags.Album ?? "Unknown Album");
                        ins.Parameters.AddWithValue("$aa", tags.AlbumArtist ?? tags.Artist ?? "Unknown Artist");
                        ins.ExecuteNonQuery();
                    }
                    else if (p.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) && !File.Exists(p))
                    {
                        LibraryFolderOrganizerService.OrganizeLibraryFolders(tempDir, dbPath);
                    }
                }
                callbackTcs.TrySetResult(changed);
                await Task.CompletedTask;
            }, debounceMs: 100);

            // 1. Internal .tonewise writes & .tmp writes must be ignored
            File.WriteAllText(Path.Combine(tonewiseDir, "manifest.db-wal"), "ignore");
            File.WriteAllText(Path.Combine(tempDir, "download.tmp"), "ignore");
            await Task.Delay(260);
            Assert.Equal(0, Volatile.Read(ref fireCount));

            // 2. Changes during SuspendWatching() must not fire
            using (watcher.SuspendWatching())
            {
                WriteMinimalFlacWithVorbisComments(Path.Combine(tempDir, "1-01 Suppressed.flac"), ("TITLE", "Suppressed"));
                await Task.Delay(120);
            }
            await Task.Delay(600);
            Assert.Equal(0, Volatile.Read(ref fireCount));

            // 3. New audio file created normally must trigger incremental sync into manifest.db
            string watchedFile = Path.Combine(tempDir, "1-01 Watched Song.flac");
            WriteMinimalFlacWithVorbisComments(
                watchedFile,
                ("TITLE", "Watched Song"),
                ("ARTIST", "Watcher Artist"),
                ("ALBUM", "Watcher Album"));

            var addCompleted = await Task.WhenAny(callbackTcs.Task, Task.Delay(5000));
            Assert.Same(callbackTcs.Task, addCompleted);
            var addPaths = await callbackTcs.Task;
            Assert.Contains(addPaths, p => p.EndsWith("1-01 Watched Song.flac", StringComparison.OrdinalIgnoreCase));

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT title FROM tracks WHERE track_uid = 'u_watch_1';";
                Assert.Equal("Watched Song", q.ExecuteScalar()?.ToString());
            }

            // 4. Delete the audio file and confirm deletion syncs cleanly
            callbackTcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            File.Delete(watchedFile);
            var delCompleted = await Task.WhenAny(callbackTcs.Task, Task.Delay(5000));
            Assert.Same(callbackTcs.Task, delCompleted);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT COUNT(*) FROM tracks WHERE track_uid = 'u_watch_1';";
                Assert.Equal(0L, Convert.ToInt64(q.ExecuteScalar()));
            }

            // 5. Verify EnableAutoWatchLibrary persists in DesktopSettingsStore
            string settingsPath = Path.Combine(tonewiseDir, "desktop_settings.json");
            DesktopSettingsStore.Save(new DesktopUserSettings(EnableAutoWatchLibrary: false), settingsPath);
            Assert.False(DesktopSettingsStore.Load(settingsPath).EnableAutoWatchLibrary);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string FindSchemaSqlPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "protocol", "schemas", "manifest.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate protocol/schemas/manifest.sql");
    }

    private static void WriteMinimalFlacWithVorbisComments(string path, params (string Key, string Value)[] entries)
    {
        using var ms = new MemoryStream();
        ms.Write("fLaC"u8);

        using var vc = new MemoryStream();
        vc.Write([4, 0, 0, 0]);
        vc.Write("test"u8);
        vc.Write(BitConverter.GetBytes((uint)entries.Length));
        foreach (var (k, v) in entries)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes($"{k}={v}");
            vc.Write(BitConverter.GetBytes((uint)utf8.Length));
            vc.Write(utf8);
        }
        byte[] vcBytes = vc.ToArray();
        ms.WriteByte(0x84); // Last metadata block + type 4 (VORBIS_COMMENT)
        ms.WriteByte((byte)((vcBytes.Length >> 16) & 0xFF));
        ms.WriteByte((byte)((vcBytes.Length >> 8) & 0xFF));
        ms.WriteByte((byte)(vcBytes.Length & 0xFF));
        ms.Write(vcBytes);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private sealed class FiniteSampleProvider(int totalSamples) : ISampleProvider
    {
        private int _remaining = totalSamples;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int n = Math.Min(_remaining, count);
            _remaining -= n;
            return n;
        }
    }

    private sealed class StubPlaylistHttpHandler(string appleHtml, string billboardHtml, string kworbHtml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri?.Host ?? "";
            string body = host.Contains("apple.com", StringComparison.OrdinalIgnoreCase) ? appleHtml
                : host.Contains("billboard.com", StringComparison.OrdinalIgnoreCase) ? billboardHtml
                : host.Contains("kworb.net", StringComparison.OrdinalIgnoreCase) ? kworbHtml
                : "";
            if (string.IsNullOrEmpty(body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            });
        }
    }
}
