using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Tonewise.SyncHost.Curation;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class CurationEngineP2Tests
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
    public void StagingDiffStore_EnforcesFillMissingOnly_And_TransactionalRollback()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p2_stage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manifest.db");

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
                        title, album, dj_scene_json, dj_vocals, tempo_bpm
                    ) VALUES (
                        'uid_1', X'01', 128, X'02', 4096,
                        'Artist/Album/01.flac', 1024, 1000, 'flac', 44100, 210000,
                        'Midnight Study', NULL, '["focus"]', 'unknown', NULL
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            var store = new StagingDiffStore(dbPath);

            // Iron Rule 1: Existing curated dj_scene_json=["focus"] must NOT be overwritten when unlocked=false
            Assert.Null(StagingDiffStore.TryCreateProposal("uid_1", "dj_scene", "[\"focus\"]", "[\"pop\"]", 0.95, "Test"));

            // Missing fields ("unknown" / NULL / empty) MUST produce proposals
            var pVocals = StagingDiffStore.TryCreateProposal("uid_1", "dj_vocals", "unknown", "instrumental", 0.92, "Acoustic5D-Vocals");
            var pTempo = StagingDiffStore.TryCreateProposal("uid_1", "tempo_bpm", null, "84.0", 0.90, "Acoustic5D-TempoCNN");
            var pBucket = StagingDiffStore.TryCreateProposal("uid_1", "tempo_bucket", null, "Slow", 0.90, "Acoustic5D-TempoBucket");
            var pPub = StagingDiffStore.TryCreateProposal("uid_1", "publisher", null, "Warp Records", 0.55, "AcoustID-NeedsReview");

            Assert.NotNull(pVocals);
            Assert.NotNull(pTempo);
            Assert.NotNull(pBucket);
            Assert.NotNull(pPub);

            int queued = store.EnqueueProposals([pVocals!, pTempo!, pBucket!, pPub!]);
            Assert.Equal(4, queued);

            // Low-confidence proposal stays Pending until approved; high-confidence (>=0.80) commits cleanly
            var commit = store.CommitApprovedBatch("batch-p2-test");
            Assert.Equal(3, commit.CommittedCount);
            Assert.Single(store.GetPendingProposals()); // pPub remains pending

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT dj_vocals, tempo_bpm, tempo_bucket, publisher FROM tracks WHERE track_uid = 'uid_1';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("instrumental", r.GetString(0));
                Assert.Equal(84.0, r.GetDouble(1), 2);
                Assert.Equal("Slow", r.GetString(2));
                Assert.True(r.IsDBNull(3));
            }

            // One-click batch rollback must restore original NULL / "unknown" state
            int rolledBack = store.RollbackBatch("batch-p2-test");
            Assert.Equal(3, rolledBack);
            Assert.Equal(0, store.RollbackBatch("batch-p2-test")); // Idempotent

            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var q = conn.CreateCommand();
                q.CommandText = "SELECT dj_vocals, tempo_bpm, tempo_bucket FROM tracks WHERE track_uid = 'uid_1';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("unknown", r.GetString(0));
                Assert.True(r.IsDBNull(1));
                Assert.True(r.IsDBNull(2));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CoverArtWaterfall_ValidatesDimensionsAspectRatio_AndSavesSingleFolderCover()
    {
        byte[] smallPng = BuildFakePng(400, 400);
        byte[] skewedPng = BuildFakePng(1200, 800);
        byte[] validJpeg = BuildFakeJpeg(1200, 1200);

        Assert.False(CoverArtWaterfallService.ValidateImageHeaderAndAspectRatio(smallPng, out _, out _));
        Assert.False(CoverArtWaterfallService.ValidateImageHeaderAndAspectRatio(skewedPng, out _, out _));
        Assert.True(CoverArtWaterfallService.ValidateImageHeaderAndAspectRatio(validJpeg, out int w, out int h));
        Assert.Equal(1200, w);
        Assert.Equal(1200, h);

        var handler = new StubCoverHttpHandler(smallPng, validJpeg);
        var service = new CoverArtWaterfallService(new HttpClient(handler));

        // CoverArtArchive -> 404; iTunes-CN -> 400x400 rejected; iTunes-JP -> 1200x1200 accepted!
        var fetched = await service.FetchBestCoverAsync("Sakamoto", "1996", releaseMbid: "mbid-missing");
        Assert.True(fetched.Success);
        Assert.Equal("iTunes-JP", fetched.SourceTier);
        Assert.Equal(1200, fetched.Width);

        string albumDir = Path.Combine(Path.GetTempPath(), "tw_p2_cov_" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstSave = service.SaveFolderCoverIfMissing(albumDir, fetched.ImageBytes!);
            Assert.True(firstSave.Saved);
            Assert.True(File.Exists(Path.Combine(albumDir, "cover.jpg")));

            // Fill-Missing Only guard skips overwriting existing cover.jpg
            var secondSave = service.SaveFolderCoverIfMissing(albumDir, fetched.ImageBytes!);
            Assert.False(secondSave.Saved);
            Assert.True(secondSave.SkippedAlreadyExists);
        }
        finally
        {
            if (Directory.Exists(albumDir)) Directory.Delete(albumDir, recursive: true);
        }
    }

    [Fact]
    public void EncyclopediaService_PersistsBiographies_CleansHtml_AndExtractsWikidataQid()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p2_bio_" + Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(tempDir, "encyclopedia.db");

        try
        {
            string rawLastFm = "Glenn Gould was a Canadian pianist. <a href=\"https://www.last.fm/music/Glenn+Gould\">Read more on Last.fm</a>. User-contributed text is available under the Creative Commons By-SA License.";
            string cleaned = EncyclopediaService.CleanLastFmBioText(rawLastFm);
            Assert.Equal("Glenn Gould was a Canadian pianist.", cleaned);

            const string mbJson = """
                {
                  "id": "mbid-gould",
                  "relations": [
                    { "type": "wikidata", "url": { "resource": "https://www.wikidata.org/wiki/Q216924" } }
                  ]
                }
                """;
            Assert.Equal("Q216924", EncyclopediaService.ExtractWikidataQidFromMusicBrainzJson(mbJson));

            var svc = new EncyclopediaService(dbPath);
            svc.UpsertBiography(new EntityBiographyEntry(
                EntityType: EncyclopediaEntityType.Artist,
                NormalizedName: "Glenn   Gould",
                DisplayName: "Glenn Gould",
                Mbid: "mbid-gould",
                WikidataQid: "Q216924",
                BirthDeathYears: "1932–1982",
                Era: "20th Century",
                SummaryZh: "加拿大钢琴家，以巴赫作品演绎闻名。",
                SummaryEn: cleaned,
                SourceUrl: "https://zh.wikipedia.org/wiki/Glenn_Gould",
                UpdatedAtMs: 1_763_251_200_000
            ));

            var loaded = svc.GetBiography("artist", "glenn gould");
            Assert.NotNull(loaded);
            Assert.Equal("Q216924", loaded!.WikidataQid);
            Assert.Single(svc.GetAllBiographies(query: "巴赫"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Acoustic5D_DeterministicRuleMatrix_AndReleaseDisambiguator_WorkAccurately()
    {
        // 1. Acoustic 5D inference: instrumental calm study track
        var vec = new AcousticFeatureVector(
            TrackUid: "u_study",
            InstrumentalProbability: 0.91,
            Bpm: 78.4,
            BpmConfidence: 0.88,
            Arousal: 0.28,
            Valence: 0.56,
            IntegratedLufs: -17.5,
            TopGenre: "Ambient",
            MoodHeads: [("Calm", 0.82), ("Energetic", 0.12)]
        );
        var res = Acoustic5DInferenceService.InferTrack5D(vec);
        Assert.Equal("instrumental", res.DjVocals);
        Assert.Equal("low", res.DjEnergy);
        Assert.Equal("Slow", res.TempoBucket);
        Assert.Contains("focus", res.DjScenes);
        Assert.Contains("coding", res.DjScenes);
        Assert.Contains("relax", res.DjScenes);

        // Uncertain vocal probability -> flagged low-confidence unknown
        var midVec = vec with { InstrumentalProbability = 0.52 };
        var midRes = Acoustic5DInferenceService.InferTrack5D(midVec);
        Assert.Equal("unknown", midRes.DjVocals);
        Assert.True(midRes.VocalsConfidence < 0.70);

        // 2. Release disambiguator: reject Various Artists compilation (2015), select original 1979 studio album
        MusicBrainzReleaseCandidate[] candidates =
        [
            new("mb-comp", "Best of 70s Hits", "Various Artists", "Album", ["Compilation"], "Official", 2015, "Sony Hits", 240_000, 0.96),
            new("mb-remaster", "Studio Album (2010 Remaster)", "Pink Floyd", "Album", [], "Official", 2010, "EMI", 241_000, 0.94),
            new("mb-orig", "The Wall", "Pink Floyd", "Album", [], "Official", 1979, "Harvest / Columbia", 240_500, 0.95),
        ];

        var existing = new TrackCurationSnapshot("u_wall", null, null, null, "Progressive Rock", "vocal", null, null, null, null, null);
        var proposals = ReleaseDisambiguatorService.GenerateMissingReleaseProposals(existing, candidates, 240_000);
        Assert.Equal(3, proposals.Count);
        Assert.Contains(proposals, p => p.FieldName == "album" && p.SuggestedValue == "The Wall");
        Assert.Contains(proposals, p => p.FieldName == "year" && p.SuggestedValue == "1979");
        Assert.Contains(proposals, p => p.FieldName == "publisher" && p.SuggestedValue == "Harvest / Columbia");
        Assert.All(proposals, p => Assert.True(p.Confidence >= 0.90));
    }

    private static byte[] BuildFakePng(int width, int height)
    {
        byte[] bytes = new byte[24];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static byte[] BuildFakeJpeg(int width, int height)
    {
        byte[] bytes = new byte[11];
        bytes[0] = 0xFF; bytes[1] = 0xD8; // SOI
        bytes[2] = 0xFF; bytes[3] = 0xC0; // SOF0
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 7);
        bytes[6] = 8; // precision
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(7, 2), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9, 2), (ushort)width);
        return bytes;
    }

    private sealed class StubCoverHttpHandler(byte[] lowResPng, byte[] validJpeg) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("coverartarchive.org"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            if (url.Contains("itunes.apple.com/search") && url.Contains("country=cn"))
            {
                string json = """{"results":[{"artworkUrl100":"https://cdn.example.test/cn/100x100bb.jpg"}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            if (url.Contains("cdn.example.test/cn/1200x1200bb.jpg"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(lowResPng)
                });
            }
            if (url.Contains("itunes.apple.com/search") && url.Contains("country=jp"))
            {
                string json = """{"results":[{"artworkUrl100":"https://cdn.example.test/jp/100x100bb.jpg"}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            if (url.Contains("cdn.example.test/jp/1200x1200bb.jpg"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(validJpeg)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task DoubanMusic_PopulatesBasicMetadataPublisherGenreYear_And_EncyclopediaChineseBio()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_p2_douban_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string bioDbPath = Path.Combine(tempDir, "encyclopedia.db");

        try
        {
            using var httpClient = new HttpClient(new StubDoubanHttpHandler());
            var lookup = new BasicMetadataLookupService(httpClient);

            var query = new BasicMetadataQuery(
                TrackUid: "uid_douban_1",
                FullPath: "",
                RelPath: "01 - 昨夜之灯.flac",
                Title: "昨夜之灯",
                Artists: "蔡琴",
                Album: "",
                Composers: "",
                Conductors: "",
                WorkName: "",
                MovementName: "",
                Genre: "",
                Year: null,
                Publisher: "",
                DurationMs: 177_000,
                AlbumDirectory: "",
                CoverArtPath: null
            );

            var (updates, savedCover) = await lookup.ResolveMissingMetadataAsync(query);
            Assert.False(savedCover);
            Assert.False(updates.ContainsKey("artists_json")); // Fill-Missing Only preserves non-empty Artists
            Assert.Equal("昨夜之灯", updates["album"]);
            Assert.Equal("1983", updates["year"]);
            Assert.Equal("流行", updates["genre"]);
            Assert.Equal("海山唱片股份有限公司", updates["publisher"]);

            var enc = new EncyclopediaService(bioDbPath);
            var bio = await enc.FetchAndCacheBiographyAsync(
                EncyclopediaEntityType.Album,
                "昨夜之灯",
                mbid: null,
                lastFmApiKey: null,
                httpClient: httpClient);

            Assert.NotNull(bio);
            Assert.Contains("海山唱片1983年发行", bio!.SummaryZh);
            Assert.Equal("流行 · 1983", bio.Era);
            Assert.Equal("https://music.douban.com/subject/2989949/", bio.SourceUrl);

            var cached = enc.GetBiography(EncyclopediaEntityType.Album, "昨夜之灯");
            Assert.NotNull(cached);
            Assert.Equal(bio.SummaryZh, cached!.SummaryZh);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubDoubanHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (url.StartsWith("https://m.douban.com/rexxar/api/v2/search/subjects", StringComparison.OrdinalIgnoreCase))
            {
                string json = """
                {
                  "subjects": {
                    "items": [
                      {
                        "target_type": "music",
                        "target_id": "2989949",
                        "target": {
                          "id": "2989949",
                          "title": "昨夜之灯",
                          "card_subtitle": "蔡琴 / 1983"
                        }
                      }
                    ]
                  }
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            if (url.StartsWith("https://m.douban.com/rexxar/api/v2/music/2989949", StringComparison.OrdinalIgnoreCase))
            {
                string json = """
                {
                  "id": "2989949",
                  "title": "昨夜之灯",
                  "singer": [{"name": "蔡琴"}],
                  "pubdate": ["1983"],
                  "genres": ["流行"],
                  "publisher": ["海山唱片股份有限公司"],
                  "version": ["专辑"],
                  "songs": [{"title": "昨夜之灯", "track_number": 1}],
                  "pic": {"large": "https://img3.doubanio.com/view/subject/m/public/s2928817.jpg"},
                  "intro": "蔡琴经典个人专辑《昨夜之灯》，海山唱片1983年发行。",
                  "url": "https://music.douban.com/subject/2989949/"
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
