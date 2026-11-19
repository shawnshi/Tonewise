using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonewise.Interop;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class PhaseRaIntegrationTests
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

    private static void WriteMinimalBeatWav(string path, int sampleRate = 44100, int seconds = 5)
    {
        int totalSamples = sampleRate * seconds;
        int beatInterval = sampleRate / 2; // 120 BPM
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataBytes = totalSamples * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        for (int i = 0; i < totalSamples; i++)
        {
            int pos = i % beatInterval;
            short sample = pos < 1000
                ? (short)(0.6 * Math.Exp(-pos / 250.0) * Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate) * 32767.0)
                : (short)0;
            bw.Write(sample);
        }
    }

    [Fact]
    public void R1_And_R4_PhysicalTagWriteBack_AudioAnalyzeFfi_AndRollback_WorkOnRealWav()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_ra_r1_" + Guid.NewGuid().ToString("N"));
        string dotDir = Path.Combine(tempRoot, ".tonewise");
        string wavDir = Path.Combine(tempRoot, "Artists");
        Directory.CreateDirectory(dotDir);
        Directory.CreateDirectory(wavDir);

        try
        {
            string wavRel = "Artists/01_Pulse.wav";
            string wavAbs = Path.Combine(tempRoot, wavRel.Replace('/', Path.DirectorySeparatorChar));
            WriteMinimalBeatWav(wavAbs);

            // R4: Native Rust symphonia analysis + 5D inference
            var analysis = TonewiseCoreClient.AnalyzeAudioFile(wavAbs);
            Assert.False(string.IsNullOrWhiteSpace(analysis.Codec));
            Assert.Equal(44100u, analysis.SampleRateHz);
            Assert.Equal(150, analysis.Waveform150.Count);
            Assert.NotNull(analysis.EstimatedBpm);
            Assert.InRange(analysis.EstimatedBpm!.Value, 115f, 125f);

            var inferred = Acoustic5DInferenceService.AnalyzeFileAndInfer5D("uid_pulse", wavAbs);
            Assert.Equal("Fast", inferred.TempoBucket);

            // R1: Write tags + update manifest.db via StagingDiffStore Commit & Rollback
            var fpBefore = TonewiseCoreClient.ComputeFingerprint(wavAbs);
            string dbPath = Path.Combine(dotDir, "manifest.db");
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
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title
                    ) VALUES (
                        'uid_pulse', $af, $hsz, $hb, 0,
                        'Artists/01_Pulse.wav', $fsz, 1000, 'pcm_s16le', 44100, 5000, 'Pulse'
                    );
                    """;
                ins.Parameters.AddWithValue("$af", Convert.FromHexString(fpBefore.AudioFrameBlake3Hex));
                ins.Parameters.AddWithValue("$hsz", (long)fpBefore.HeaderSize);
                ins.Parameters.AddWithValue("$hb", Convert.FromHexString(fpBefore.HeaderBlake3Hex));
                ins.Parameters.AddWithValue("$fsz", (long)fpBefore.FileSize);
                ins.ExecuteNonQuery();
            }

            var store = new StagingDiffStore(dbPath, tempRoot);
            var pAlbum = StagingDiffStore.TryCreateProposal("uid_pulse", "album", null, "Studio Pulse", 0.95, "Test");
            var pGenre = StagingDiffStore.TryCreateProposal("uid_pulse", "genre", null, "Electronic", 0.95, "Test");
            var pScene = StagingDiffStore.TryCreateProposal("uid_pulse", "dj_scene", "[]", "[\"coding\",\"energy\"]", 0.95, "Test");
            store.EnqueueProposals([pAlbum!, pGenre!, pScene!]);

            var commit = store.CommitApprovedBatch("batch-r1-wav");
            Assert.Equal(3, commit.CommittedCount);
            Assert.Equal(1, commit.PhysicalFilesUpdated);

            var fpAfterWrite = TonewiseCoreClient.ComputeFingerprint(wavAbs);
            // Audio frames must remain 100% unchanged after metadata writeback!
            Assert.Equal(fpBefore.AudioFrameBlake3Hex, fpAfterWrite.AudioFrameBlake3Hex);
            Assert.True(fpAfterWrite.FileSize > fpBefore.FileSize);

            int rolled = store.RollbackBatch("batch-r1-wav");
            Assert.Equal(3, rolled);
            var fpAfterRollback = TonewiseCoreClient.ComputeFingerprint(wavAbs);
            Assert.Equal(fpBefore.AudioFrameBlake3Hex, fpAfterRollback.AudioFrameBlake3Hex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task R2_LastFmWebApiClient_RunsFullBidirectionalSync_WithSessionPullDedupAndScrobble()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_ra_r2_" + Guid.NewGuid().ToString("N"));
        string dotDir = Path.Combine(tempRoot, ".tonewise");
        Directory.CreateDirectory(dotDir);
        string dbPath = Path.Combine(dotDir, "manifest.db");

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
                        title, artists_json, rating
                    ) VALUES (
                        'uid_kindofblue', X'01', 10, X'02', 0,
                        'Miles/SoWhat.flac', 2000, 1000, 'flac', 96000, 540000,
                        'So What', '["Miles Davis"]', 0
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Write one local scrobble within 14 days & not on cloud, plus one stale >14 days ago
            File.WriteAllLines(Path.Combine(dotDir, "history.pc.jsonl"),
            [
                JsonSerializer.Serialize(new LedgerPlaybackEvent("uid_kindofblue", "Miles Davis", "So What", "Kind of Blue", nowSec - 1200, "pc")),
                JsonSerializer.Serialize(new LedgerPlaybackEvent("uid_kindofblue", "Miles Davis", "So What", "Kind of Blue", nowSec - 20L * 86400, "pc")),
            ]);

            var handler = new StubLastFmHttpHandler(nowSec - 7200);
            var client = new LastFmWebApiClient(new HttpClient(handler));

            string sessionKey = await client.AuthenticateMobileSessionAsync("test-key", "test-secret", "shawn", "pw");
            Assert.Equal("sk_mock_999", sessionKey);

            var cfg = new LastFmAccountConfig("test-key", "test-secret", "shawn", sessionKey);
            var summary = await client.RunFullBidirectionalSyncAsync(dbPath, dotDir, cfg);

            Assert.Equal(1, summary.PulledCloudPlays);
            Assert.Equal(1, summary.LovedTracksUpdated);
            Assert.Equal(1, summary.ExpiredArchivedCount);
            Assert.Equal(1, summary.PushedScrobbles);
            Assert.Single(handler.PushedBatches);
            Assert.NotNull(LastFmAccountConfig.Load(dotDir));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task R3_And_R4_EncyclopediaWaterfallAndAcoustIdParser_WorkCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_ra_r3_" + Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(tempDir, "encyclopedia.db");
        try
        {
            var svc = new EncyclopediaService(dbPath);
            var handler = new StubBioHttpHandler();
            using var http = new HttpClient(handler);

            var entry = await svc.FetchAndCacheBiographyAsync(
                EncyclopediaEntityType.Composer,
                "Johann Sebastian Bach",
                mbid: "mbid-bach",
                httpClient: http
            );
            Assert.NotNull(entry);
            Assert.Equal("Q1339", entry!.WikidataQid);
            Assert.Contains("巴赫", entry.SummaryZh!);
            Assert.Equal(2, handler.RequestCount);

            // Second lookup hits local SQLite cache cleanly with zero extra HTTP requests
            var cached = await svc.FetchAndCacheBiographyAsync(
                EncyclopediaEntityType.Composer,
                "Johann Sebastian Bach",
                mbid: "mbid-bach",
                httpClient: http
            );
            Assert.NotNull(cached);
            Assert.Equal(2, handler.RequestCount);

            const string acoustIdJson = """
                {
                  "status": "ok",
                  "results": [
                    {
                      "score": 0.94,
                      "recordings": [
                        {
                          "duration": 240.0,
                          "artists": [{ "name": "Pink Floyd" }],
                          "releasegroups": [
                            { "id": "rg-1", "title": "The Wall", "type": "Album", "secondarytypes": [] },
                            { "id": "rg-2", "title": "Echoes Best Of", "type": "Album", "secondarytypes": ["Compilation"] }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """;
            var parsedCandidates = ReleaseDisambiguatorService.ParseAcoustIdJsonResponse(acoustIdJson, 240_000);
            Assert.Equal(2, parsedCandidates.Count);
            Assert.Equal("Pink Floyd", parsedCandidates[0].ArtistCredit);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubLastFmHttpHandler(long cloudPlayedUts) : HttpMessageHandler
    {
        public List<string> PushedBatches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Post)
            {
                string form = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (form.Contains("auth.getMobileSession"))
                {
                    return OkJson("""{"session":{"name":"shawn","key":"sk_mock_999"}}""");
                }
                if (form.Contains("track.scrobble"))
                {
                    PushedBatches.Add(form);
                    return OkJson("""{"scrobbles":{"@attr":{"accepted":1,"ignored":0}}}""");
                }
            }
            if (url.Contains("user.getrecenttracks"))
            {
                string json = $"{{\"recenttracks\":{{\"track\":[{{\"artist\":{{\"#text\":\"Miles Davis\"}},\"name\":\"So What\",\"album\":{{\"#text\":\"Kind of Blue\"}},\"date\":{{\"uts\":\"{cloudPlayedUts}\"}}}}]}}}}";
                return OkJson(json);
            }
            if (url.Contains("user.getlovedtracks"))
            {
                string json = $"{{\"lovedtracks\":{{\"track\":[{{\"artist\":{{\"name\":\"Miles Davis\"}},\"name\":\"So What\",\"date\":{{\"uts\":\"{cloudPlayedUts}\"}}}}]}}}}";
                return OkJson(json);
            }
            if (url.Contains("user.gettoptracks"))
            {
                return OkJson("""{"toptracks":{"track":[]}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage OkJson(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubBioHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            string url = request.RequestUri!.ToString();
            if (url.Contains("musicbrainz.org"))
            {
                return Task.FromResult(OkJson("""
                    {"id":"mbid-bach","relations":[{"type":"wikidata","url":{"resource":"https://www.wikidata.org/wiki/Q1339"}}]}
                    """));
            }
            if (url.Contains("zh.wikipedia.org"))
            {
                return Task.FromResult(OkJson("""
                    {"description":"德国巴洛克时期作曲家","extract":"约翰·塞巴斯蒂安·巴赫是巴洛克时期的德国作曲家。","content_urls":{"desktop":{"page":"https://zh.wikipedia.org/wiki/Johann_Sebastian_Bach"}}}
                    """));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage OkJson(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
