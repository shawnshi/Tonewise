using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class ArtistNormalizationTests
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
    public void PunctuationAndHygiene_NormalizesHyphensApostrophesWhitespaceAndWriterSuffix()
    {
        Assert.Equal("Camille Saint-Saëns", ArtistEntitySplitter.NormalizePerformerName("Camille Saint\u2010Saëns"));
        Assert.Equal("Yo-Yo Ma", ArtistEntitySplitter.NormalizePerformerName("Yo\u2010Yo Ma"));
        Assert.Equal("Ne-Yo", ArtistEntitySplitter.NormalizePerformerName("Ne\u2010Yo"));
        Assert.Equal("Destiny's Child", ArtistEntitySplitter.NormalizePerformerName("Destiny\u2019s Child"));
        Assert.Equal("Mark O'Connor", ArtistEntitySplitter.NormalizePerformerName("Mark O\u2019Connor"));
        Assert.Equal("Fritz Reiner", ArtistEntitySplitter.NormalizePerformerName("  Fritz\u00A0  Reiner  "));
        Assert.Equal("Beyoncé", ArtistEntitySplitter.NormalizeComposerName("Beyoncé (writer)"));
        Assert.Equal("Chris Martin", ArtistEntitySplitter.NormalizeComposerName("Chris Martin (WRITER)"));
        Assert.Equal("Royce da 5\u20329\u2033", ArtistEntitySplitter.NormalizeComposerName("Royce da 5\u20329\u2033"));
    }

    [Fact]
    public void SplitPerformers_ProtectsJrSrAndPreservesWhitelist()
    {
        Assert.Equal(["Johann Strauss II"], ArtistEntitySplitter.SplitPerformers("Johann Strauss, Jr."));
        Assert.Equal(["Grover Washington, Jr.", "Bill Withers"], ArtistEntitySplitter.SplitPerformers("Grover Washington, Jr. / Bill Withers"));
        Assert.Equal(["Above & Beyond", "Justine Suissa"], ArtistEntitySplitter.SplitPerformers("Above  &  Beyond, Justine Suissa"));
        Assert.Equal(["AC/DC"], ArtistEntitySplitter.SplitPerformers("AC/DC"));
    }

    [Fact]
    public void CrossScriptAndOrchestraAliases_ConvergeToCanonicalEnglish()
    {
        Assert.Equal(
            ["Dmitri Shostakovich", "Nikolai Kapustin", "Aram Khachaturian", "Seiji Ozawa"],
            ArtistEntitySplitter.SplitPerformers("Дмитрий Дмитриевич Шостакович; Николaй Кaпустин / Արամ Խաչատրյան / 小澤征爾")
        );
        Assert.Equal(
            ["Berlin Philharmonic", "Vienna Philharmonic", "Vienna Symphony Orchestra", "Bavarian State Orchestra"],
            ArtistEntitySplitter.SplitPerformers("Berliner Philharmoniker; Wiener Philharmoniker; Wiener Symphoniker; Bayerisches Staatsorchester")
        );
        Assert.Equal(
            ["Frédéric Chopin", "Béla Bartók", "Israel Philharmonic Orchestra", "Giya Kancheli"],
            ArtistEntitySplitter.SplitPerformers("Fryderyk Chopin; Bartók Béla; התזמורת הפילהרמונית הישראלית; გია ყანჩელი")
        );
        Assert.Equal(["AUN J-CLASSIC ORCHESTRA"], ArtistEntitySplitter.SplitPerformers("AUN J クラシック・オーケストラ"));
        Assert.Equal(["Herbert von Karajan"], ArtistEntitySplitter.SplitPerformers("Herbert Von Karajan Condu"));
        Assert.Equal(["Otto Klemperer"], ArtistEntitySplitter.SplitPerformers("Otto Kempler"));
        Assert.Equal(["Georges Bizet"], ArtistEntitySplitter.SplitPerformers("George Bizet"));
    }

    [Fact]
    public void MultiValueAliases_ExpandCleanly_And_ComposerShortAliasesRemainRoleIsolated()
    {
        Assert.Equal(
            ["Modest Mussorgsky", "Maurice Ravel"],
            ArtistEntitySplitter.SplitPerformers("Модест Петрович Мусоргский-Maurice Ravel")
        );
        Assert.Equal(
            ["Tugan Sokhiev", "Vienna Philharmonic"],
            ArtistEntitySplitter.SplitPerformers("Tugan Sokhiev: Vienna Philharmonic Orchestra")
        );

        Assert.Equal(["Monk"], ArtistEntitySplitter.SplitPerformers("Monk"));
        Assert.Equal(["Thelonious Monk"], ArtistEntitySplitter.SplitComposers("Monk"));
        Assert.Equal(
            ["Johann Sebastian Bach", "Philip Lawrence", "Anton Webern"],
            ArtistEntitySplitter.SplitComposers("Bach; P. Lawrence; Philip Lawrence; Webern")
        );
    }

    [Fact]
    public void FalsePositiveProtection_KeepsDistinctArtistsSeparate()
    {
        Assert.Equal(["LiSA"], ArtistEntitySplitter.SplitPerformers("LiSA (织部里沙)"));
        Assert.Equal(["LISA"], ArtistEntitySplitter.SplitPerformers("LISA"));
        Assert.Equal(["BoA"], ArtistEntitySplitter.SplitPerformers("BoA"));
        Assert.Equal(["bôa"], ArtistEntitySplitter.SplitPerformers("bôa"));
        Assert.Equal(["Misia"], ArtistEntitySplitter.SplitPerformers("Misia"));
        Assert.Equal(["MISIA"], ArtistEntitySplitter.SplitPerformers("MISIA"));
        Assert.Equal(["Éric Serra"], ArtistEntitySplitter.SplitPerformers("Éric Serra"));
        Assert.Equal(["Éric Serra-Tosio"], ArtistEntitySplitter.SplitPerformers("Éric Serra\u2010Tosio"));
    }

    [Fact]
    public void NormalizeAlbumArtist_PreservesDelimitersAndProtectsWhitelist()
    {
        Assert.Equal(
            "Ludwig van Beethoven; Bavarian State Orchestra, Carlos Kleiber",
            ArtistEntitySplitter.NormalizeAlbumArtist("Ludwig van Beethoven; Bayerisches Staatsorchester, Carlos Kleiber")
        );
        Assert.Equal(
            "Duke Ellington and His Orchestra",
            ArtistEntitySplitter.NormalizeAlbumArtist("Duke Ellington & His Orchestra")
        );
        Assert.Equal(
            "Above & Beyond, Vienna Philharmonic",
            ArtistEntitySplitter.NormalizeAlbumArtist("Above & Beyond, Wiener Philharmoniker")
        );
        Assert.Equal(
            "Modest Mussorgsky, Maurice Ravel",
            ArtistEntitySplitter.NormalizeAlbumArtist("Модест Петрович Мусоргский-Maurice Ravel")
        );
    }

    [Fact]
    public void AuditService_ScansReadOnly_And_StagingDiffStoreCoercesCanonicalValues()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_norm_audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "manifest.db");

        try
        {
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using (var setup = conn.CreateCommand())
                {
                    setup.CommandText = schemaSql;
                    setup.ExecuteNonQuery();
                }
                ManifestSchemaProvider.EnsureSchemaV2(conn);

                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, album_artist, artists_json, composers_json, conductors_json
                    ) VALUES
                    (
                        'uid_dirty', X'01', 128, X'02', 4096,
                        'Classical/Dirty/01.flac', 1024, 1000, 'flac', 44100, 210000,
                        'Pictures', 'Pictures',
                        'Berliner Philharmoniker, Герберт',
                        '["Модест Петрович Мусоргский-Maurice Ravel","Camille Saint‐Saëns"]',
                        '["Bach","Beyoncé (writer)"]',
                        '["Михаил Плетнёв"]'
                    ),
                    (
                        'uid_clean', X'03', 128, X'04', 4096,
                        'Pop/Clean/02.flac', 1024, 1000, 'flac', 44100, 180000,
                        'Clean Song', 'Clean Album',
                        'Above & Beyond',
                        '["Above & Beyond"]',
                        '["Johann Sebastian Bach"]',
                        '["Herbert von Karajan"]'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            var report = ArtistNormalizationAuditService.ScanManifest(dbPath);
            Assert.Equal(2, report.TotalTracksScanned);
            Assert.Equal(1, report.TracksWithChanges);
            Assert.Equal(4, report.TotalProposals);

            var store = new StagingDiffStore(dbPath);
            store.EnqueueProposals(report.Proposals, forceOverwriteUnlocked: true);
            var commit = store.CommitApprovedBatch(autoApproveThreshold: 0.90, maxPhysicalFiles: 0);
            Assert.Equal(4, commit.CommittedCount);

            using (var checkConn = TonewiseSqlite.OpenReadOnly(dbPath))
            using (var cmd = checkConn.CreateCommand())
            {
                cmd.CommandText = "SELECT album_artist, artists_json, composers_json, conductors_json FROM tracks WHERE track_uid = 'uid_dirty';";
                using var r = cmd.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("Berlin Philharmonic, Герберт", r.GetString(0));
                Assert.Equal(["Modest Mussorgsky", "Maurice Ravel", "Camille Saint-Saëns"], JsonSerializer.Deserialize<List<string>>(r.GetString(1)));
                Assert.Equal(["Johann Sebastian Bach", "Beyoncé"], JsonSerializer.Deserialize<List<string>>(r.GetString(2)));
                Assert.Equal(["Mikhail Pletnev"], JsonSerializer.Deserialize<List<string>>(r.GetString(3)));
            }

            var cleanReport = ArtistNormalizationAuditService.ScanManifest(dbPath);
            Assert.Equal(0, cleanReport.TotalProposals);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class MockLookupHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri?.AbsoluteUri ?? "");
            return Task.FromResult(responder(request));
        }
    }

    private static byte[] BuildMinimalFlacBytes()
    {
        using var ms = new MemoryStream();
        ms.Write("fLaC"u8);
        ms.WriteByte(0x00); // STREAMINFO (not last)
        ms.Write([0x00, 0x00, 0x22]); // 34 bytes
        var streamInfo = new byte[34];
        streamInfo[10] = 0x0A;
        streamInfo[11] = 0xC4;
        streamInfo[12] = 0x42;
        streamInfo[13] = 0xF0;
        ms.Write(streamInfo);
        ms.WriteByte(0x81); // PADDING (last)
        ms.Write([0x00, 0x00, 0x80]); // 128 bytes
        ms.Write(new byte[128]);
        var audio = new byte[4096];
        Array.Fill(audio, (byte)0xAA);
        ms.Write(audio);
        return ms.ToArray();
    }

    [Fact]
    public async Task BasicMetadataLookupService_NormalizesOnlineArtistsComposersAndStorefrontOrder()
    {
        var handler = new MockLookupHttpHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("itunes.apple.com", StringComparison.Ordinal))
            {
                if (url.Contains("term=Bach", StringComparison.Ordinal) && url.Contains("country=us", StringComparison.Ordinal))
                {
                    string json = """
                        {
                          "results": [
                            {
                              "trackName": "Violin Concerto",
                              "artistName": "Berliner Philharmoniker / Camille Saint\u2010Sa\u00EBns",
                              "collectionName": "Concertos",
                              "primaryGenreName": "Classical",
                              "releaseDate": "1982-01-01T00:00:00Z",
                              "trackTimeMillis": 210000
                            }
                          ]
                        }
                        """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                if (url.Contains("%E6%98%A5", StringComparison.OrdinalIgnoreCase) && url.Contains("country=cn", StringComparison.Ordinal))
                {
                    string json = """
                        {
                          "results": [
                            {
                              "trackName": "\u6625\u3088\u3001\u6765\u3044",
                              "artistName": "AUN J \u30AF\u30E9\u30B7\u30C3\u30AF\u30FB\u30AA\u30FC\u30B1\u30B9\u30C8\u30E9",
                              "collectionName": "\u685C-SAKURA-",
                              "primaryGenreName": "Instrumental",
                              "releaseDate": "2012-03-07T00:00:00Z",
                              "trackTimeMillis": 240000
                            }
                          ]
                        }
                        """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"results":[]}""", Encoding.UTF8, "application/json")
                };
            }

            if (url.Contains("musicbrainz.org", StringComparison.Ordinal))
            {
                string mbJson = """
                    {
                      "recordings": [
                        {
                          "length": 210000,
                          "artist-credit": [
                            {"name": "Дмитрий Дмитриевич Шостакович"}
                          ],
                          "releases": [
                            {
                              "title": "Symphony No. 5",
                              "date": "1982",
                              "label-info": [{"label": {"name": "Deutsche Grammophon"}}]
                            }
                          ],
                          "tags": [{"name": "classical"}]
                        }
                      ]
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(mbJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new BasicMetadataLookupService(http);

        var westernQuery = new BasicMetadataQuery(
            TrackUid: "u1",
            FullPath: "",
            RelPath: "Bach/Unknown/01 - Violin Concerto.flac",
            Title: "Violin Concerto",
            Artists: "",
            Album: "",
            Composers: "",
            Conductors: "",
            WorkName: "",
            MovementName: "",
            Genre: "",
            Year: null,
            Publisher: "",
            DurationMs: 210000,
            AlbumDirectory: "",
            CoverArtPath: "dummy.jpg");

        var (updates, _) = await svc.ResolveMissingMetadataAsync(westernQuery);
        Assert.Contains("country=us", handler.RequestedUrls.First(u => u.Contains("itunes.apple.com", StringComparison.Ordinal)));
        Assert.Equal("[\"Berlin Philharmonic\",\"Camille Saint-Sa\\u00EBns\"]", updates["artists_json"]);
        Assert.Equal("[\"Johann Sebastian Bach\"]", updates["composers_json"]);

        // MusicBrainz fallback path (iTunes returns empty) normalizes Cyrillic artist
        handler.RequestedUrls.Clear();
        var mbOnlyQuery = westernQuery with { RelPath = "Unknown/Unknown/01.flac", Title = "Symphony No. 5", Artists = "" };
        var (mbUpdates, _) = await svc.ResolveMissingMetadataAsync(mbOnlyQuery);
        Assert.Equal("[\"Dmitri Shostakovich\"]", mbUpdates["artists_json"]);

        // CJK query hits country=cn first and normalizes Japanese ensemble
        handler.RequestedUrls.Clear();
        var cjkQuery = westernQuery with { RelPath = "Unknown/Unknown/01.flac", Title = "春よ、来い", Artists = "" };
        var (cjkUpdates, _) = await svc.ResolveMissingMetadataAsync(cjkQuery);
        Assert.Contains("country=cn", handler.RequestedUrls.First(u => u.Contains("itunes.apple.com", StringComparison.Ordinal)));
        Assert.Equal("[\"AUN J-CLASSIC ORCHESTRA\"]", cjkUpdates["artists_json"]);
    }

    [Fact]
    public void StagingDiffStore_ApplyDirectCuratedEdits_NormalizesPhysicalFlacTagsAndDbTogether()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_phys_norm_" + Guid.NewGuid().ToString("N"));
        string tonewiseDir = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(tonewiseDir);
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        string audioRel = "Classical/01.flac";
        string audioFull = Path.Combine(tempRoot, "Classical", "01.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audioFull)!);
        File.WriteAllBytes(audioFull, BuildMinimalFlacBytes());

        try
        {
            string schemaSql = File.ReadAllText(Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql"));
            using (var conn = TonewiseSqlite.OpenReadWrite(dbPath))
            {
                using (var setup = conn.CreateCommand())
                {
                    setup.CommandText = schemaSql;
                    setup.ExecuteNonQuery();
                }
                ManifestSchemaProvider.EnsureSchemaV2(conn);

                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album
                    ) VALUES (
                        'uid_phys', X'01', 170, X'02', 128,
                        $rel, $fsize, 1000, 'flac', 44100, 1000,
                        'Track 1', 'Album 1'
                    );
                    """;
                ins.Parameters.AddWithValue("$rel", audioRel);
                ins.Parameters.AddWithValue("$fsize", new FileInfo(audioFull).Length);
                ins.ExecuteNonQuery();
            }

            var store = new StagingDiffStore(dbPath, tempRoot);
            var res = store.ApplyDirectCuratedEdits(
                ["uid_phys"],
                new Dictionary<string, string?>
                {
                    ["album_artist"] = "Wiener Philharmoniker",
                    ["artists_json"] = "Berliner Philharmoniker / Camille Saint\u2010Sa\u00EBns",
                    ["composers_json"] = "Bach; Beyonc\u00E9 (writer)",
                    ["conductors_json"] = "小澤征爾",
                });

            Assert.Equal(4, res.CommittedCount);
            Assert.Equal(1, res.PhysicalFilesUpdated);

            using (var checkConn = TonewiseSqlite.OpenReadOnly(dbPath))
            using (var cmd = checkConn.CreateCommand())
            {
                cmd.CommandText = "SELECT album_artist, artists_json, composers_json, conductors_json FROM tracks WHERE track_uid = 'uid_phys';";
                using var r = cmd.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("Vienna Philharmonic", r.GetString(0));
                Assert.Equal(["Berlin Philharmonic", "Camille Saint-Saëns"], JsonSerializer.Deserialize<List<string>>(r.GetString(1)));
                Assert.Equal(["Johann Sebastian Bach", "Beyoncé"], JsonSerializer.Deserialize<List<string>>(r.GetString(2)));
                Assert.Equal(["Seiji Ozawa"], JsonSerializer.Deserialize<List<string>>(r.GetString(3)));
            }

            var embedded = LibraryFolderOrganizerService.ReadEmbeddedHeaderTags(audioFull);
            Assert.Equal("Vienna Philharmonic", embedded.AlbumArtist);
            Assert.Equal(["Berlin Philharmonic", "Camille Saint-Saëns"], ArtistEntitySplitter.SplitPerformers(embedded.Artist));
            Assert.Equal(["Johann Sebastian Bach", "Beyoncé"], ArtistEntitySplitter.SplitComposers(embedded.Composer));
            Assert.Equal(["Seiji Ozawa"], ArtistEntitySplitter.SplitPerformers(embedded.Conductor));
            Assert.DoesNotContain("Berliner Philharmoniker", embedded.Artist ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("\u2010", embedded.Artist ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("(writer)", embedded.Composer ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("小澤征爾", embedded.Conductor ?? "", StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ReleaseDisambiguatorService_ParseAcoustIdJsonResponse_NormalizesArtistCredit()
    {
        string json = """
            {
              "results": [
                {
                  "score": 0.95,
                  "recordings": [
                    {
                      "duration": 200.0,
                      "artists": [
                        {"name": "Berliner Philharmoniker"},
                        {"name": "Camille Saint\u2010Sa\u00EBns"}
                      ],
                      "releasegroups": [
                        {"id": "rg-1", "title": "Carnival", "type": "Album", "secondarytypes": []}
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var candidates = ReleaseDisambiguatorService.ParseAcoustIdJsonResponse(json, 200_000);
        Assert.Single(candidates);
        Assert.Equal("Berlin Philharmonic & Camille Saint-Saëns", candidates[0].ArtistCredit);
    }

    [Fact]
    public void EncyclopediaService_NormalizesArtistAndComposerCacheKeys_WithoutCrossPollutingAlbums()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_enc_norm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "encyclopedia.db");

        try
        {
            var svc = new EncyclopediaService(dbPath);
            svc.UpsertBiography(new EntityBiographyEntry(
                EntityType: EncyclopediaEntityType.Artist,
                NormalizedName: "",
                DisplayName: "Berlin Philharmonic",
                Mbid: "mbid-bpo",
                WikidataQid: null,
                BirthDeathYears: null,
                Era: "Classical",
                SummaryZh: "柏林爱乐乐团",
                SummaryEn: null,
                SourceUrl: null,
                UpdatedAtMs: 1000));

            var hit = svc.GetBiography(EncyclopediaEntityType.Artist, "Berliner Philharmoniker");
            Assert.NotNull(hit);
            Assert.Equal("mbid-bpo", hit!.Mbid);

            Assert.Equal("camille saint-saëns", EncyclopediaService.NormalizeEntityName("Camille Saint\u2010Sa\u00EBns", EncyclopediaEntityType.Artist));
            Assert.Equal("johann sebastian bach", EncyclopediaService.NormalizeEntityName("Bach", EncyclopediaEntityType.Composer));
            Assert.Equal("bach", EncyclopediaService.NormalizeEntityName("Bach", EncyclopediaEntityType.Album));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
