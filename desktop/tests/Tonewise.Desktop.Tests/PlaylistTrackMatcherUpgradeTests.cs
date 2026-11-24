using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Playlists;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class PlaylistTrackMatcherUpgradeTests
{
    private static LocalLibraryTrackRef MakeTrack(
        string uid,
        string title,
        string artist,
        string album = "Studio Album",
        int durationMs = 220_000,
        int sampleRateHz = 44_100,
        string ext = ".flac",
        string composers = "",
        string conductors = "",
        string work = "",
        string movement = "")
    {
        string safeArtist = string.IsNullOrWhiteSpace(artist) ? "Unknown" : artist.Replace('/', '_');
        string safeTitle = title.Replace('/', '_').Replace(':', '_');
        string rel = $"{safeArtist}/{album}/{safeTitle}{ext}";
        return new LocalLibraryTrackRef(
            TrackUid: uid,
            RelPath: rel,
            FullPath: $@"D:\Music\{rel.Replace('/', '\\')}",
            Title: title,
            ArtistsDisplay: artist,
            Album: album,
            DurationMs: durationMs,
            SampleRateHz: sampleRateHz,
            ComposersDisplay: composers,
            ConductorsDisplay: conductors,
            WorkName: work,
            MovementName: movement);
    }

    [Fact]
    public void UnicodeNormalization_FoldsDiacritics_SpecialLatinChars_AndRomanNumerals()
    {
        var library = new List<LocalLibraryTrackRef>
        {
            MakeTrack("u_dvorak", "Symphony No. 9", "Orchestra", composers: "Antonin Dvorak"),
            MakeTrack("u_chopin", "Etude Op. 10 No. 3", "Frederic Chopin"),
            MakeTrack("u_strauss", "An der schonen blauen Donau", "Johann Strauss"),
            MakeTrack("u_cafe", "Cafe Del Mar", "Energy 52"),
            MakeTrack("u_luto", "Concerto for Orchestra", "Witold Lutoslawski"),
            MakeTrack("u_roman", "I. Allegro", "Ensemble", work: "Serenade", movement: "I. Allegro"),
            MakeTrack("u_roman2", "II. Allegretto", "Ensemble", work: "Serenade", movement: "II. Allegretto"),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("Symphony No. 9", "Antonín Dvořák"),
            new("Étude Op. 10 No. 3", "Frédéric Chopin"),
            new("An der schönen blauen Donau", "Johann Strauß"),
            new("Café Del Mar", "Energy 52"),
            new("Concerto for Orchestra", "Witold Lutosławski"),
            new("Ⅰ. Allegro", "Ensemble"),
            new("Ⅱ. Allegretto", "Ensemble"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, library);
        Assert.Equal("u_dvorak", resolved[0].MatchedTrackUid);
        Assert.Equal("u_chopin", resolved[1].MatchedTrackUid);
        Assert.Equal("u_strauss", resolved[2].MatchedTrackUid);
        Assert.Equal("u_cafe", resolved[3].MatchedTrackUid);
        Assert.Equal("u_luto", resolved[4].MatchedTrackUid);
        Assert.Equal("u_roman", resolved[5].MatchedTrackUid);
        Assert.Equal("u_roman2", resolved[6].MatchedTrackUid);
    }

    [Fact]
    public void JapaneseKanaDakuten_AndKoreanHangul_ArePreservedIntact()
    {
        string zankoku = PlaylistTrackMatcher.NormalizeTitle("残酷な天使のテーゼ");
        Assert.Equal("残酷な天使のテーゼ", zankoku);
        Assert.Contains("ゼ", zankoku, StringComparison.Ordinal);
        Assert.Equal("ざんこく", PlaylistTrackMatcher.NormalizeTitle("ざんこく"));

        Assert.NotEqual(PlaylistTrackMatcher.NormalizeTitle("が"), PlaylistTrackMatcher.NormalizeTitle("か"));
        Assert.NotEqual(PlaylistTrackMatcher.NormalizeTitle("ぱ"), PlaylistTrackMatcher.NormalizeTitle("は"));
        Assert.Equal(PlaylistTrackMatcher.NormalizeTitle("ガ"), PlaylistTrackMatcher.NormalizeTitle("ｶﾞ"));
        Assert.Equal("사랑해요", PlaylistTrackMatcher.NormalizeTitle("사랑해요"));

        var library = new List<LocalLibraryTrackRef>
        {
            MakeTrack("jp_ka", "かっこうぐらし", "Artist JP"),
            MakeTrack("kr_1", "사랑해요", "Artist KR"),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("がっこうぐらし", "Artist JP"),
            new("사랑해요", "Artist KR"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, library);
        Assert.False(resolved[0].IsLocalMatched);
        Assert.True(resolved[1].IsLocalMatched);
        Assert.Equal("kr_1", resolved[1].MatchedTrackUid);
    }

    [Fact]
    public void ChineseTraditionalAndFullwidthBrackets_MatchSimplifiedLibraryTracks()
    {
        var library = new List<LocalLibraryTrackRef>
        {
            MakeTrack("cn_qingtian", "晴天", "周杰伦"),
            MakeTrack("cn_pingfan", "平凡之路", "朴树"),
            MakeTrack("cn_yequ", "夜曲", "周杰伦"),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("晴天（現場版）", "周杰倫"),
            new("平凡之路【電影《後會無期》主題曲】", "朴樹"),
            new("夜曲 - 2023重製版", "周杰倫"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, library);
        Assert.Equal("cn_qingtian", resolved[0].MatchedTrackUid);
        Assert.Equal("cn_pingfan", resolved[1].MatchedTrackUid);
        Assert.Equal("cn_yequ", resolved[2].MatchedTrackUid);
    }

    [Fact]
    public void DerivativeVersionGuard_BlocksMismatches_WhileAllowingSameDerivativeVariant()
    {
        var library = new List<LocalLibraryTrackRef>
        {
            MakeTrack("tr_ibiza", "I Took A Pill In Ibiza", "Mike Posner"),
            MakeTrack("tr_qingtian", "晴天", "周杰伦"),
            MakeTrack("tr_hello_studio", "Hello", "Adele"),
            MakeTrack("tr_hello_acoustic", "Hello (Acoustic Version)", "Adele"),
            MakeTrack("tr_studio_only", "Rolling in the Deep", "Adele"),
            MakeTrack("tr_inst_only", "Skyfall (Instrumental Version)", "Adele"),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("I Took A Pill In Ibiza - Seeb Remix", "Mike Posner, Seeb"),
            new("晴天（伴奏）", "周杰伦"),
            new("晴天【纯音乐】", "周杰伦"),
            new("Hello (Acoustic Version)", "Adele"), // Must NOT hit studio "Hello", hits "tr_hello_acoustic"
            new("Hello (Instrumental Version)", "Adele"),
            new("Hello (Karaoke)", "Adele"),
            new("Rolling in the Deep (Cover)", "Adele"),
            new("Skyfall", "Adele"), // Studio seed must NOT hit Instrumental local track
            new("Hello (Acoustic)", "Adele"), // Positive derivative match via fuzzy
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, library);
        Assert.False(resolved[0].IsLocalMatched);
        Assert.False(resolved[1].IsLocalMatched);
        Assert.False(resolved[2].IsLocalMatched);
        Assert.Equal("tr_hello_acoustic", resolved[3].MatchedTrackUid);
        Assert.False(resolved[4].IsLocalMatched);
        Assert.False(resolved[5].IsLocalMatched);
        Assert.False(resolved[6].IsLocalMatched);
        Assert.False(resolved[7].IsLocalMatched);
        Assert.Equal("tr_hello_acoustic", resolved[8].MatchedTrackUid);

        // Ensure ordinary words containing "cover", "live", "demo" are not false-flagged
        var benignLib = new List<LocalLibraryTrackRef>
        {
            MakeTrack("benign_1", "Discover Me", "Artist A"),
            MakeTrack("benign_2", "Alive and Well", "Artist B"),
        };
        var benignResolved = PlaylistTrackMatcher.ResolveSeeds(
            [
                new("Discover Me (2024 Remaster)", "Artist A"),
                new("Alive and Well (Radio Edit)", "Artist B"),
            ],
            benignLib);
        Assert.Equal("benign_1", benignResolved[0].MatchedTrackUid);
        Assert.Equal("benign_2", benignResolved[1].MatchedTrackUid);
    }

    [Fact]
    public void ProtectedArtistWhitelist_PreservesCompoundBandNames()
    {
        var simonTokens = PlaylistTrackMatcher.ExtractArtistTokens("Simon & Garfunkel");
        Assert.Contains("simon garfunkel", simonTokens);
        Assert.DoesNotContain("simon", simonTokens);

        var acdcTokens = PlaylistTrackMatcher.ExtractArtistTokens("AC/DC, Stevie Wonder");
        Assert.Contains("ac dc", acdcTokens);
        Assert.DoesNotContain("ac", acdcTokens);

        var library = new List<LocalLibraryTrackRef>
        {
            MakeTrack("sg_sound", "The Sound of Silence", "Simon & Garfunkel"),
            MakeTrack("acdc_back", "Back In Black", "AC/DC"),
            MakeTrack("ewf_sep", "September", "Earth, Wind & Fire"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(
            [
                new("The Sound of Silence", "Paul Simon"),
                new("The Sound of Silence", "Simon & Garfunkel"),
                new("Back In Black", "AC/DC"),
                new("September", "Earth, Wind & Fire"),
            ],
            library);

        Assert.False(resolved[0].IsLocalMatched);
        Assert.Equal("sg_sound", resolved[1].MatchedTrackUid);
        Assert.Equal("acdc_back", resolved[2].MatchedTrackUid);
        Assert.Equal("ewf_sep", resolved[3].MatchedTrackUid);
    }

    [Fact]
    public void ClassicalMatching_HandlesConductorDisambiguation_MultiColon_CompactOpus_AndStructuralGuards()
    {
        var library = new List<LocalLibraryTrackRef>
        {
            // Bernstein listed first and higher sample rate; seed specifies Karajan
            MakeTrack(
                "sym5_bern",
                "I. Allegro con brio",
                "Wiener Philharmoniker",
                album: "Beethoven: Symphonies",
                sampleRateHz: 192_000,
                composers: "Ludwig van Beethoven",
                conductors: "Leonard Bernstein",
                work: "Symphony No. 5 in C Minor",
                movement: "I. Allegro con brio"),
            MakeTrack(
                "sym5_kara",
                "I. Allegro con brio",
                "Berliner Philharmoniker",
                album: "Beethoven: Symphonies",
                sampleRateHz: 44_100,
                composers: "Ludwig van Beethoven",
                conductors: "Herbert von Karajan",
                work: "Symphony No. 5 in C Minor",
                movement: "I. Allegro con brio"),
            MakeTrack(
                "sonata8_m1",
                "I. Grave - Allegro di molto e con brio",
                "Maurizio Pollini",
                composers: "Ludwig van Beethoven",
                work: "Piano Sonata No. 8",
                movement: "I. Adagio sostenuto"),
            MakeTrack(
                "sonata14_m1",
                "I. Adagio sostenuto",
                "Maurizio Pollini",
                composers: "Ludwig van Beethoven",
                work: "Piano Sonata No. 14",
                movement: "I. Adagio sostenuto"),
            MakeTrack(
                "nocturne_9_2",
                "Nocturne Op. 9 No. 2",
                "Arthur Rubinstein",
                composers: "Frederic Chopin"),
            MakeTrack(
                "bwv_1007",
                "Cello Suite No. 1 BWV 1007",
                "Yo-Yo Ma",
                composers: "Johann Sebastian Bach"),
            MakeTrack(
                "cn_c1",
                "月光曲第一乐章",
                "Lang Lang"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(
            [
                new("Beethoven: Symphony No. 5 in C Minor - I. Allegro con brio", "Ludwig van Beethoven, Herbert von Karajan"),
                new("Beethoven: Piano Sonata No. 14: I. Adagio sostenuto", "Beethoven, Maurizio Pollini"),
                new("Nocturne in E-Flat Major, Op. 9, No. 2", "Frederic Chopin"),
                new("Cello Suite No.1 BWV1007", "J.S. Bach"),
                // Negative structural guards
                new("Nocturne Op. 9 No. 1", "Frederic Chopin"),
                new("Beethoven: Symphony No. 6 - I. Allegro", "Ludwig van Beethoven"),
                new("Beethoven: Piano Sonata No. 14: II. Allegretto", "Beethoven"),
                new("月光曲第二乐章", "Lang Lang"),
                new("Cello Suite No. 1 BWV 1007", "C.P.E. Bach"),
            ],
            library);

        Assert.Equal("sym5_kara", resolved[0].MatchedTrackUid);
        Assert.Equal("sonata14_m1", resolved[1].MatchedTrackUid);
        Assert.Equal("nocturne_9_2", resolved[2].MatchedTrackUid);
        Assert.Equal("bwv_1007", resolved[3].MatchedTrackUid);

        Assert.False(resolved[4].IsLocalMatched);
        Assert.False(resolved[5].IsLocalMatched);
        Assert.False(resolved[6].IsLocalMatched);
        Assert.False(resolved[7].IsLocalMatched);
        Assert.False(resolved[8].IsLocalMatched);
    }

    [Fact]
    public void FuzzySpelling_AndTransliteration_MatchWithShortTitleNegativeGuards()
    {
        var library = new List<LocalLibraryTrackRef>
        {
            MakeTrack("sp_color", "Color of the Wind", "Vanessa Williams"),
            MakeTrack("sp_bohemian", "Bohemian Rhapsody", "Queen"),
            MakeTrack("tr_rach", "Piano Concerto No. 2 in C Minor, Op. 18", "Vladimir Ashkenazy", composers: "Sergei Rachmaninov"),
            MakeTrack("tr_lyap", "Symphonic Etude", "Artist L", composers: "Sergei Lyapunov"),
            MakeTrack("tr_tchaik", "Swan Lake Suite", "Orchestra", composers: "Peter Iljitsch Tschaikowsky"),
            MakeTrack("short_yintian", "阴天", "莫文蔚"),
            MakeTrack("short_on", "On", "Artist Short"),
            MakeTrack("short_come", "Come", "Artist Home"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(
            [
                new("Colour of the Wind", "Vanessa Williams"),
                new("Bohemian Rapsody", "Queen"),
                new("Piano Concerto No. 2 Op. 18", "Sergei Rachmaninoff"),
                new("Swan Lake", "Pyotr Ilyich Tchaikovsky"),
                // Negative short-title fuzzy guards
                new("晴天", "莫文蔚"),
                new("One", "Artist Short"),
                new("Home", "Artist Home"),
            ],
            library);

        Assert.Equal("sp_color", resolved[0].MatchedTrackUid);
        Assert.Equal("sp_bohemian", resolved[1].MatchedTrackUid);
        Assert.Equal("tr_rach", resolved[2].MatchedTrackUid);
        Assert.Equal("tr_tchaik", resolved[3].MatchedTrackUid);
        Assert.False(resolved[4].IsLocalMatched);
        Assert.False(resolved[5].IsLocalMatched);
        Assert.False(resolved[6].IsLocalMatched);
    }

    [Fact]
    public void MultiFactorTieBreaking_UsesLiveAlignment_Album_Duration_AndAudioQuality()
    {
        // 1. Live vs Studio alignment
        var liveLib = new List<LocalLibraryTrackRef>
        {
            MakeTrack("h_live", "Hotel California (Live)", "Eagles", album: "Hell Freezes Over", sampleRateHz: 192_000),
            MakeTrack("h_studio", "Hotel California", "Eagles", album: "Hotel California", sampleRateHz: 44_100),
        };
        var liveRes = PlaylistTrackMatcher.ResolveSeeds(
            [
                new("Hotel California", "Eagles"),
                new("Hotel California (Live At Forum)", "Eagles"),
            ],
            liveLib);
        Assert.Equal("h_studio", liveRes[0].MatchedTrackUid);
        Assert.Equal("h_live", liveRes[1].MatchedTrackUid);

        // 2. Album tie-breaker beats higher sample rate & earlier index
        var albumLib = new List<LocalLibraryTrackRef>
        {
            MakeTrack("q_hits", "Bohemian Rhapsody", "Queen", album: "Greatest Hits", sampleRateHz: 96_000),
            MakeTrack("q_opera", "Bohemian Rhapsody", "Queen", album: "A Night at the Opera", sampleRateHz: 44_100),
        };
        var albumRes = PlaylistTrackMatcher.ResolveSeeds(
            [new("Bohemian Rhapsody", "Queen", Album: "A Night at the Opera (Deluxe Edition)")],
            albumLib);
        Assert.Equal("q_opera", albumRes[0].MatchedTrackUid);

        // 3. Duration tie-breaker (direct, Spotify JSON, and M3U #EXTINF)
        var durLib = new List<LocalLibraryTrackRef>
        {
            MakeTrack("calvin_ext", "Feel So Close", "Calvin Harris", durationMs: 390_000, sampleRateHz: 96_000),
            MakeTrack("calvin_radio", "Feel So Close", "Calvin Harris", durationMs: 206_000, sampleRateHz: 44_100),
        };

        const string spotifyHtml = """
            <!DOCTYPE html>
            <html><body><script id="__NEXT_DATA__" type="application/json">
            {
              "props":{"pageProps":{"state":{"data":{"entity":{
                "name":"Dur Mix",
                "trackList":[{"entityType":"track","title":"Feel So Close - Radio Edit","subtitle":"Calvin Harris","duration":206413}]
              }}}}}
            }
            </script></body></html>
            """;
        var spotifyParsed = PlaylistImporter.ParseSpotifyEmbedHtml(spotifyHtml);
        Assert.Equal(206_413, spotifyParsed.Tracks[0].DurationMs);
        var spotifyResolved = PlaylistTrackMatcher.ResolveSeeds(spotifyParsed.Tracks, durLib);
        Assert.Equal("calvin_radio", spotifyResolved[0].MatchedTrackUid);

        const string m3uText = """
            #EXTM3U
            #EXTINF:206,Calvin Harris - Feel So Close
            /foreign/path/Feel So Close.flac
            """;
        var m3uParsed = PlaylistImporter.ParseTextPlaylist(m3uText);
        Assert.Equal(206_000, m3uParsed.Tracks[0].DurationMs);
        var m3uResolved = PlaylistTrackMatcher.ResolveSeeds(m3uParsed.Tracks, durLib);
        Assert.Equal("calvin_radio", m3uResolved[0].MatchedTrackUid);

        const string appleHtml = """
            <html><body><script id="serialized-server-data" type="application/json">
            {"data":[{"itemKind":"trackLockup","title":"Feel So Close","artistName":"Calvin Harris","durationInMillis":206123}]}
            </script></body></html>
            """;
        var appleParsed = PlaylistImporter.ParseAppleMusicHtml(appleHtml);
        Assert.Equal(206_123, appleParsed.Tracks[0].DurationMs);

        // 4. Audio quality fallback (Lossless FLAC 96k > FLAC 44.1k > MP3 44.1k)
        var qualityLib = new List<LocalLibraryTrackRef>
        {
            MakeTrack("qual_mp3", "Time", "Pink Floyd", ext: ".mp3", sampleRateHz: 44_100),
            MakeTrack("qual_flac44", "Time", "Pink Floyd", ext: ".flac", sampleRateHz: 44_100),
            MakeTrack("qual_flac96", "Time", "Pink Floyd", ext: ".flac", sampleRateHz: 96_000),
        };
        var qualityRes = PlaylistTrackMatcher.ResolveSeeds([new("Time", "Pink Floyd")], qualityLib);
        Assert.Equal("qual_flac96", qualityRes[0].MatchedTrackUid);
    }

    [Fact]
    public void EmptyAndPlaceholderArtist_GuardAgainstAmbiguousCrossArtistCollisions()
    {
        var library = new List<LocalLibraryTrackRef>
        {
            MakeTrack("unique_bohemian", "Bohemian Rhapsody", "Queen", album: "A Night at the Opera"),
            MakeTrack("dup_mp3", "Love Story", "Taylor Swift", album: "Fearless", ext: ".mp3", sampleRateHz: 44_100),
            MakeTrack("dup_flac", "Love Story", "Taylor Swift", album: "Fearless (Platinum)", ext: ".flac", sampleRateHz: 96_000),
            MakeTrack("hello_adele", "Hello", "Adele", album: "25", durationMs: 295_000, sampleRateHz: 44_100),
            MakeTrack("hello_lionel", "Hello", "Lionel Richie", album: "Can't Slow Down", durationMs: 250_000, sampleRateHz: 96_000),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(
            [
                new("Bohemian Rhapsody", ""),
                new("Love Story", "Various Artists"),
                new("Hello", ""),
                new("Hello", "Various Artists"),
                new("Hello", "群星"),
                new("Hello", "", Album: "25"),
                new("Hello", "Various Artists", DurationMs: 295_000),
            ],
            library);

        Assert.Equal("unique_bohemian", resolved[0].MatchedTrackUid);
        Assert.Equal("dup_flac", resolved[1].MatchedTrackUid);
        Assert.False(resolved[2].IsLocalMatched);
        Assert.False(resolved[3].IsLocalMatched);
        Assert.False(resolved[4].IsLocalMatched);
        Assert.Equal("hello_adele", resolved[5].MatchedTrackUid);
        Assert.Equal("hello_adele", resolved[6].MatchedTrackUid);
    }

    [Fact]
    public void IndexParity_AndScaleBenchmark_TenThousandTracksAndTwoHundredSeedsFinishUnderOneSecond()
    {
        var library = new List<LocalLibraryTrackRef>(10_000);
        for (int i = 0; i < 9_950; i++)
        {
            string artist = $"Artist_{i / 25:D3}";
            string title = $"Synthetic Symphony Piece Number {i:D4}";
            library.Add(MakeTrack(
                $"uid_{i:D5}",
                title,
                artist,
                album: $"Album_{i % 50:D2}",
                durationMs: 180_000 + (i % 120) * 1000,
                sampleRateHz: i % 2 == 0 ? 44_100 : 96_000));
        }

        // Add realistic edge tracks into the 10k library
        for (int i = 0; i < 50; i++)
        {
            library.Add(MakeTrack(
                $"edge_{i:D2}",
                $"Étude Speciale No. {i + 1}",
                i % 2 == 0 ? "Frédéric Chopin" : "Antonin Dvorak",
                album: $"Classical Vol {i}",
                durationMs: 210_000));
        }

        var seeds = new List<ImportedTrackSeed>(200);
        for (int i = 0; i < 100; i++)
        {
            int idx = i * 97;
            seeds.Add(new($"Synthetic Symphony Piece Number {idx:D4}", $"Artist_{idx / 25:D3}"));
        }
        for (int i = 0; i < 50; i++)
        {
            seeds.Add(new(
                $"Etude Specale No. {i + 1}", // 1 typo + stripped diacritic -> forces Stage 2B fuzzy path
                i % 2 == 0 ? "Frederic Chopin" : "Antonín Dvořák"));
        }
        for (int i = 0; i < 50; i++)
        {
            seeds.Add(new($"Nonexistent Unmatched Song {i}", $"Unknown Band {i % 5}"));
        }

        // Warm up JIT once
        _ = PlaylistTrackMatcher.ResolveSeeds([seeds[0]], [library[0]]);

        var index = PlaylistTrackMatcher.BuildIndex(library);
        var sw = Stopwatch.StartNew();
        var fromIndex = PlaylistTrackMatcher.ResolveSeeds(seeds, index);
        sw.Stop();

        var fromList = PlaylistTrackMatcher.ResolveSeeds(seeds, library);
        Assert.Equal(fromIndex, fromList);

        Assert.Equal(200, fromIndex.Count);
        for (int i = 0; i < 150; i++)
        {
            Assert.True(fromIndex[i].IsLocalMatched, $"Seed {i} ({seeds[i].Title}) failed to match");
        }
        for (int i = 150; i < 200; i++)
        {
            Assert.False(fromIndex[i].IsLocalMatched, $"Unmatched seed {i} falsely matched");
        }

        Assert.True(sw.ElapsedMilliseconds < 1_000, $"Matching 200 seeds over 10k library took {sw.ElapsedMilliseconds}ms (>= 1000ms budget)");
    }

    [Fact]
    public void PlaylistStore_RematchPlaylist_PersistsNewMatchesAndRepairedPaths_AndIsIdempotent()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_rematch_pl_" + Guid.NewGuid().ToString("N"));
        try
        {
            string playlistsDir = Path.Combine(tempRoot, "Playlists");
            Directory.CreateDirectory(playlistsDir);

            var library = new List<LocalLibraryTrackRef>
            {
                MakeTrack("cn_qingtian", "晴天", "周杰伦", album: "叶惠美", durationMs: 269_000),
                new(
                    TrackUid: "u_hello",
                    RelPath: "Adele/25/01 Hello.flac",
                    FullPath: Path.Combine(tempRoot, "Adele", "25", "01 Hello.flac"),
                    Title: "Hello",
                    ArtistsDisplay: "Adele",
                    Album: "25",
                    DurationMs: 295_000,
                    SampleRateHz: 44_100),
            };

            string mixPath = Path.Combine(playlistsDir, "Mixed Sync.m3u8");
            const string initialContent =
                "#EXTM3U\n"
                + "#PLAYLIST:Mixed Sync\n"
                + "#EXTINF:0,周杰倫 - 晴天（現場版）\n"
                + "#EXTTW-UNMATCHED:周杰倫\t晴天（現場版）\t葉惠美\n"
                + "#EXTINF:295,Adele - Hello\n"
                + "OldDir/Hello.flac\n"
                + "#EXTINF:0,Unknown - Missing Track\n"
                + "#EXTTW-UNMATCHED:Unknown\tMissing Track\tMissing Album\n";
            File.WriteAllText(mixPath, initialContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var first = PlaylistStore.RematchPlaylist(mixPath, library, tempRoot);
            Assert.True(first.WasSaved);
            Assert.Equal("Mixed Sync", first.Name);
            Assert.Equal(3, first.TotalCount);
            Assert.Equal(1, first.PreviousMatchedCount);
            Assert.Equal(2, first.MatchedCount);
            Assert.Equal(1, first.NewlyMatchedCount);
            Assert.Equal(1, first.PathRepairedCount);

            string updatedDisk = File.ReadAllText(mixPath, Encoding.UTF8);
            Assert.Contains("周杰伦/叶惠美/晴天.flac", updatedDisk, StringComparison.Ordinal);
            Assert.Contains("Adele/25/01 Hello.flac", updatedDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("OldDir/Hello.flac", updatedDisk, StringComparison.Ordinal);
            Assert.Contains("#EXTTW-UNMATCHED:Unknown\tMissing Track\tMissing Album", updatedDisk, StringComparison.Ordinal);

            // Second run must be a strict no-op without rewriting file
            var second = PlaylistStore.RematchPlaylist(mixPath, library, tempRoot);
            Assert.False(second.WasSaved);
            Assert.Equal(2, second.PreviousMatchedCount);
            Assert.Equal(2, second.MatchedCount);
            Assert.Equal(0, second.NewlyMatchedCount);
            Assert.Equal(0, second.PathRepairedCount);

            // Unmatched-only playlist with zero improvements must not be rewritten
            string unmatchedPath = Path.Combine(playlistsDir, "Still Missing.m3u8");
            const string unmatchedOriginal =
                "#EXTM3U\n"
                + "#PLAYLIST:Still Missing\n"
                + "#EXTINF:0,Phantom - Nowhere\n"
                + "#EXTTW-UNMATCHED:Phantom\tNowhere\tGhost Album\n";
            File.WriteAllText(unmatchedPath, unmatchedOriginal, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var unmatchedRes = PlaylistStore.RematchPlaylist(unmatchedPath, library, tempRoot);
            Assert.False(unmatchedRes.WasSaved);
            Assert.Equal(0, unmatchedRes.MatchedCount);
            Assert.Equal(unmatchedOriginal, File.ReadAllText(unmatchedPath, Encoding.UTF8));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void PlaylistStore_RematchAllPlaylists_AggregatesBatchCountsAccurately()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_rematch_all_" + Guid.NewGuid().ToString("N"));
        try
        {
            string playlistsDir = Path.Combine(tempRoot, "Playlists");
            Directory.CreateDirectory(playlistsDir);

            var library = new List<LocalLibraryTrackRef>
            {
                MakeTrack("t_cn", "晴天", "周杰伦", album: "叶惠美", durationMs: 269_000),
                new(
                    TrackUid: "t_hello",
                    RelPath: "Adele/25/01 Hello.flac",
                    FullPath: Path.Combine(tempRoot, "Adele", "25", "01 Hello.flac"),
                    Title: "Hello",
                    ArtistsDisplay: "Adele",
                    Album: "25",
                    DurationMs: 295_000,
                    SampleRateHz: 44_100),
            };

            File.WriteAllText(
                Path.Combine(playlistsDir, "01 Needs Repair.m3u8"),
                "#EXTM3U\n#PLAYLIST:01 Needs Repair\n#EXTINF:0,周杰倫 - 晴天（現場版）\n#EXTTW-UNMATCHED:周杰倫\t晴天（現場版）\t葉惠美\n#EXTINF:295,Adele - Hello\nOldDir/Hello.flac\n",
                new UTF8Encoding(false));

            File.WriteAllText(
                Path.Combine(playlistsDir, "02 Already Clean.m3u8"),
                "#EXTM3U\n#PLAYLIST:02 Already Clean\n#EXTINF:295,Adele - Hello\nAdele/25/01 Hello.flac\n",
                new UTF8Encoding(false));

            var batch = PlaylistStore.RematchAllPlaylists(tempRoot, library);
            Assert.Equal(2, batch.PlaylistsScanned);
            Assert.Equal(1, batch.PlaylistsUpdated);
            Assert.Equal(3, batch.TotalTracks);
            Assert.Equal(3, batch.TotalMatched);
            Assert.Equal(1, batch.NewlyMatchedCount);
            Assert.Equal(1, batch.PathRepairedCount);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SyncOutboxAndPairingStore_PlaylistCatalogAndResolve_MatchesClassicalMetadataFromManifest()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_sync_classical_pl_" + Guid.NewGuid().ToString("N"));
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

            string tonewiseDir = Path.Combine(tempRoot, ".tonewise");
            Directory.CreateDirectory(tonewiseDir);
            string dbPath = Path.Combine(tonewiseDir, "manifest.db");

            string physPath = Path.Combine(tempRoot, "Beethoven", "Symphony5", "01.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(physPath)!);
            File.WriteAllBytes(physPath, [0x66, 0x4C, 0x61, 0x43, 1, 2, 3, 4]);

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
                        title, album, album_artist, artists_json, composers_json, conductors_json,
                        work_name, movement_name, genre
                    ) VALUES (
                        'u_cls_sym5', X'AA11', 4, X'BB22', 0,
                        'Beethoven/Symphony5/01.flac', 8, 2000, 'flac', 96000, 420000,
                        'I. Allegro con brio', 'Symphony No. 5', 'Berliner Philharmoniker',
                        '["Berliner Philharmoniker"]',
                        '["Ludwig van Beethoven"]',
                        '["Herbert von Karajan"]',
                        'Symphony No. 5 in C Minor', 'I. Allegro con brio', 'Classical'
                    );
                    """;
                ins.ExecuteNonQuery();
            }

            string playlistsDir = Path.Combine(tempRoot, "Playlists");
            Directory.CreateDirectory(playlistsDir);
            File.WriteAllText(
                Path.Combine(playlistsDir, "Classical Test.m3u8"),
                "#EXTM3U\n#PLAYLIST:Classical Test\n#EXTINF:0,Ludwig van Beethoven, Herbert von Karajan - Beethoven: Symphony No. 5 in C Minor - I. Allegro con brio\n#EXTTW-UNMATCHED:Ludwig van Beethoven, Herbert von Karajan\tBeethoven: Symphony No. 5 in C Minor - I. Allegro con brio\tSymphony No. 5\n",
                new UTF8Encoding(false));

            var store = new SyncOutboxAndPairingStore(tempRoot);
            var catalog = store.GetCatalog("playlists");
            var entry = Assert.Single(catalog);
            Assert.Equal("Classical Test", entry.Name);
            Assert.Equal(1, entry.TrackCount);

            store.EnqueuePlaylist("Classical Test");
            var payload = store.ResolveOutbox();
            Assert.Single(payload.Tracks);
            Assert.Equal("u_cls_sym5", payload.Tracks[0].TrackUid);
            var resolvedPlaylist = Assert.Single(payload.Playlists);
            Assert.Equal(["u_cls_sym5"], resolvedPlaylist.TrackUids);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveSeeds_ChinesePlaylists_MatchBilingualTraditionalVariantsAndRejectDerivatives()
    {
        var lib = new List<LocalLibraryTrackRef>
        {
            new("u1", "Jay/01.flac", "D:\\Music\\Jay\\01.flac", "晴天", "周杰伦", "叶惠美", 269000, 44100),
            new("u2", "Eason/02.flac", "D:\\Music\\Eason\\02.flac", "十年", "陈奕迅", "黑白灰", 205000, 44100),
            new("u3", "Faye/03.flac", "D:\\Music\\Faye\\03.flac", "红豆", "王菲", "唱游", 259000, 44100),
            new("u4", "PuShu/04.flac", "D:\\Music\\PuShu\\04.flac", "平凡之路", "朴树", "猎户星座", 301000, 44100),
            new("u5", "Jay/05.flac", "D:\\Music\\Jay\\05.flac", "发如雪", "周杰伦", "十一月的萧邦", 299000, 44100),
            new("u6", "Jay/06.flac", "D:\\Music\\Jay\\06.flac", "珊瑚海", "周杰伦; 梁心颐", "十一月的萧邦", 256000, 44100),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("晴天 (Sunny Day)", "周杰伦 (Jay Chou)"),
            new("十年 (国语)", "Eason Chan"),
            new("紅豆", "Faye Wong"),
            new("平凡之途", "朴树"), // 1-char diff on 4-char CJK title
            new("髮如雪 (影视主题曲)", "周杰倫"),
            new("珊瑚海", "周杰伦×梁心颐"),
            new("晴天 (伴奏)", "周杰伦"), // Hard-block derivative must NOT match studio track
            new("晴天 (翻唱)", "周杰伦"), // Hard-block cover must NOT match studio track
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, lib);
        Assert.Equal(8, resolved.Count);
        Assert.Equal("u1", resolved[0].MatchedTrackUid);
        Assert.Equal("u2", resolved[1].MatchedTrackUid);
        Assert.Equal("u3", resolved[2].MatchedTrackUid);
        Assert.Equal("u4", resolved[3].MatchedTrackUid);
        Assert.Equal("u5", resolved[4].MatchedTrackUid);
        Assert.Equal("u6", resolved[5].MatchedTrackUid);
        Assert.False(resolved[6].IsLocalMatched);
        Assert.False(resolved[7].IsLocalMatched);
    }

    [Fact]
    public void ParseTextPlaylist_ChineseNumberingUnspacedDashAndSwappedTitleArtist_ResolvesMatched()
    {
        var lib = new List<LocalLibraryTrackRef>
        {
            new("u1", "Jay/01.flac", "D:\\Music\\Jay\\01.flac", "晴天", "周杰伦", "叶惠美", 269000, 44100),
            new("u2", "Eason/02.flac", "D:\\Music\\Eason\\02.flac", "浮夸", "陈奕迅", "U87", 285000, 44100),
        };

        string rawText = """
            1、周杰伦-晴天
            02. 浮誇 - 陈奕迅
            """;
        var parsed = PlaylistImporter.ParseTextPlaylist(rawText, "华语精选");
        var resolved = PlaylistTrackMatcher.ResolveSeeds(parsed.Tracks, lib);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("u1", resolved[0].MatchedTrackUid);
        Assert.Equal("u2", resolved[1].MatchedTrackUid);
    }

    [Fact]
    public void ResolveSeeds_ChinesePinyin_SlashMedley_DiscPrefix_BandSuffixAndConjunction_MatchesAccurately()
    {
        var lib = new List<LocalLibraryTrackRef>
        {
            MakeTrack("p1", "Hua Xin", "周華健"),
            MakeTrack("p2", "Wéi Yī", "王力宏"),
            MakeTrack("p3", "Ai Hen Jian Dan", "陶喆"),
            MakeTrack("p4", "Chi Xin Jue Dui", "李聖傑"),
            MakeTrack("p5", "唯一", "五月天"),
            MakeTrack("m1", "償還 / 紅豆", "王菲", album: "菲比尋常Live!"),
            MakeTrack("m2", "Chang Huan", "那英"),
            MakeTrack("d1", "我要我們在一起", "范曉萱"),
            MakeTrack("a1", "如風往事", "李宗盛與盧冠廷"),
            MakeTrack("a2", "Don't Break My Heart", "黑豹"),
            MakeTrack("a3", "海阔天空", "信"),
            MakeTrack("n1", "AI", "王菲"),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("花心", "周华健"),
            new("唯一", "王力宏"),
            new("爱，很简单", "陶喆"),
            new("痴心绝对", "李圣杰"),
            new("Wei Yi", "五月天"), // Reverse Pinyin seed -> Hanzi track
            new("唯一", "张学友"), // Wrong artist must NOT match
            new("爱", "王菲"), // 1-char Hanzi must NOT false-match 2-char "AI"
            new("红豆", "王菲"), // CJK/CJK slash medley
            new("偿还", "那英"), // Polyphone 还 -> huan hits "Chang Huan"
            new("1-01 我要我們在一起", ""), // Disc-track prefix on uncredited title
            new("如风往事", "李宗盛"),
            new("如风往事", "卢冠廷"),
            new("Don't Break My Heart", "黑豹乐队"),
            new("海阔天空", "信乐团"), // 1-char prefix must NOT strip 乐团 -> 信
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, lib);
        Assert.Equal("p1", resolved[0].MatchedTrackUid);
        Assert.Equal("p2", resolved[1].MatchedTrackUid);
        Assert.Equal("p3", resolved[2].MatchedTrackUid);
        Assert.Equal("p4", resolved[3].MatchedTrackUid);
        Assert.Equal("p5", resolved[4].MatchedTrackUid);
        Assert.False(resolved[5].IsLocalMatched);
        Assert.False(resolved[6].IsLocalMatched);
        Assert.Equal("m1", resolved[7].MatchedTrackUid);
        Assert.Equal("m2", resolved[8].MatchedTrackUid);
        Assert.Equal("d1", resolved[9].MatchedTrackUid);
        Assert.Equal("a1", resolved[10].MatchedTrackUid);
        Assert.Equal("a1", resolved[11].MatchedTrackUid);
        Assert.Equal("a2", resolved[12].MatchedTrackUid);
        Assert.False(resolved[13].IsLocalMatched);
    }

    [Fact]
    public void PlaylistStore_LoadPlaylist_DecodesLegacyGb18030M3uAndMatchesTrack()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "tw_gbk_pl_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string m3uPath = Path.Combine(tempDir, "legacy.m3u");
            // GB18030 bytes for:
            // "#EXTM3U\n#PLAYLIST:中文GBK歌单\n#EXTINF:0,周杰伦 - 晴天\n#EXTTW-UNMATCHED:周杰伦\t晴天\t\n"
            byte[] gb18030Bytes =
            [
                0x23, 0x45, 0x58, 0x54, 0x4D, 0x33, 0x55, 0x0A, 0x23, 0x50, 0x4C, 0x41, 0x59, 0x4C, 0x49, 0x53,
                0x54, 0x3A, 0xD6, 0xD0, 0xCE, 0xC4, 0x47, 0x42, 0x4B, 0xB8, 0xE8, 0xB5, 0xA5, 0x0A, 0x23, 0x45,
                0x58, 0x54, 0x49, 0x4E, 0x46, 0x3A, 0x30, 0x2C, 0xD6, 0xDC, 0xBD, 0xDC, 0xC2, 0xD7, 0x20, 0x2D,
                0x20, 0xC7, 0xE7, 0xCC, 0xEC, 0x0A, 0x23, 0x45, 0x58, 0x54, 0x54, 0x57, 0x2D, 0x55, 0x4E, 0x4D,
                0x41, 0x54, 0x43, 0x48, 0x45, 0x44, 0x3A, 0xD6, 0xDC, 0xBD, 0xDC, 0xC2, 0xD7, 0x09, 0xC7, 0xE7,
                0xCC, 0xEC, 0x09, 0x0A,
            ];
            File.WriteAllBytes(m3uPath, gb18030Bytes);

            var lib = new List<LocalLibraryTrackRef>
            {
                MakeTrack("u_jay", "晴天", "周杰伦", album: "叶惠美"),
            };

            var doc = PlaylistStore.LoadPlaylist(m3uPath, lib);
            Assert.Equal("中文GBK歌单", doc.Name);
            var entry = Assert.Single(doc.Entries);
            Assert.True(entry.IsLocalMatched);
            Assert.Equal("u_jay", entry.MatchedTrackUid);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void NonChineseUpgrades_StandaloneWorkName_SingleWordPrefix_Cyrillic_SoftRemixFallback_AndStrictPerformer()
    {
        var lib = new List<LocalLibraryTrackRef>
        {
            // Standalone workName when movementName is empty
            MakeTrack(
                "cls_brahms",
                "Klarinettenquintett in h-Moll, op. 115: I. Allegro",
                "Johannes Brahms",
                composers: "Johannes Brahms",
                work: "Quintet for Clarinet, 2 Violins, Viola, and Cello in B minor, op. 115: I. Allegro"),
            // Cyrillic composer artist tag
            MakeTrack(
                "cls_tchaik",
                "The Swan Lake (Suite), Op. 20a: I. Scene",
                "Пётр Ильич Чайковский",
                composers: ""),
            // Strategy 1 OFF: Glenn Gould must NOT satisfy Dinu Lipatti
            MakeTrack(
                "cls_bach_gould",
                "Partita No. 1 in B-Flat Major, BWV 825: I. Praeludium",
                "Glenn Gould",
                composers: "Johann Sebastian Bach"),
            // Single-word prefix matches
            MakeTrack("pop_sunflower", "Sunflower", "Post Malone, Swae Lee"),
            MakeTrack("pop_bailando", "Bailando", "Enrique Iglesias"),
            MakeTrack("cls_bolero", "Boléro, M. 81", "Maurice Ravel"),
            // Bilingual Japanese - English dash split
            MakeTrack("jp_peace", "ピースサイン", "米津玄師"),
            // Strategy 2 soft Remix / Acoustic fallback + preference for studio original
            MakeTrack("pop_despacito_rmx", "Despacito (remix)", "Luis Fonsi, Daddy Yankee, Justin Bieber"),
            MakeTrack("pop_someday_unp", "Someday (MTV Unplugged)", "Mariah Carey"),
            MakeTrack("pop_calma_alb", "Calma", "Pedro Capó", album: "Calma (Alan Walker Remix)"),
            MakeTrack("pop_dual_studio", "Closer", "The Chainsmokers, Halsey"),
            MakeTrack("pop_dual_rmx", "Closer (R3hab Remix)", "The Chainsmokers, Halsey"),
            // Hard block guard: Instrumental still blocked for studio seed
            MakeTrack("pop_coldplay_inst", "Adventure of a Lifetime (instrumental)", "Coldplay"),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("Clarinet Quintet in B minor, Op. 115 I. Allegro", "Brahms"),
            new("Swan Lake (Suite), Op. 20a: I. Scene", "Tchaikovsky"),
            new("J.S. Bach: Partita No. 1 in B-Flat Major, BWV 825: I. Praeludium", "Dinu Lipatti"), // Strategy 1 OFF
            new("Sunflower - Spider-Man: Into the Spider-Verse", "Post Malone & Swae Lee"),
            new("Bailando - Spanish Version", "Enrique Iglesias, Descemer Bueno"),
            new("Boléro", "Ravel"),
            new("ピースサイン - Peace Sign", "米津玄師"),
            new("Despacito", "Luis Fonsi & Daddy Yankee Featuring Justin Bieber"),
            new("Someday", "Mariah Carey"),
            new("Calma - Remix", "Pedro Capó, Farruko"),
            new("Closer", "The Chainsmokers"), // Studio original MUST beat Remix when both exist
            new("Adventure Of A Lifetime (Official Video)", "Coldplay"), // Instrumental must remain blocked
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, lib);
        Assert.Equal("cls_brahms", resolved[0].MatchedTrackUid);
        Assert.Equal("cls_tchaik", resolved[1].MatchedTrackUid);
        Assert.False(resolved[2].IsLocalMatched);
        Assert.Equal("pop_sunflower", resolved[3].MatchedTrackUid);
        Assert.Equal("pop_bailando", resolved[4].MatchedTrackUid);
        Assert.Equal("cls_bolero", resolved[5].MatchedTrackUid);
        Assert.Equal("jp_peace", resolved[6].MatchedTrackUid);
        Assert.Equal("pop_despacito_rmx", resolved[7].MatchedTrackUid);
        Assert.Equal("pop_someday_unp", resolved[8].MatchedTrackUid);
        Assert.Equal("pop_calma_alb", resolved[9].MatchedTrackUid);
        Assert.Equal("pop_dual_studio", resolved[10].MatchedTrackUid);
        Assert.False(resolved[11].IsLocalMatched);
    }

    [Fact]
    public void ClassicalGuard_RejectsCrossWorkMovementAndTempoCollisions()
    {
        var lib = new List<LocalLibraryTrackRef>
        {
            MakeTrack(
                "haydn_45",
                "Haydn:Symphony No. 45 in F Sharp Minor \"Farewell\", Hob. 1/45 - II. Adagio",
                "Leningrad Chamber Orchestra",
                composers: "Joseph Haydn"),
            MakeTrack(
                "lalo_cello",
                "Cello Concerto in D minor: I. Prélude. Lento - Allegro maestoso (Live)",
                "Jacqueline Du Pré",
                composers: "Édouard Lalo",
                conductors: "Daniel Barenboim",
                work: "Concerto pour violoncelle et orchestre en ré majeur : I. Allegro"),
            MakeTrack(
                "bizet_pearls",
                "Les Pêcheurs de perles: Prélude",
                "Georges Bizet, Pierrette Alarie",
                composers: "Georges Bizet"),
            MakeTrack(
                "tchaik_swan",
                "Swan Lake Act IV No. 26 Scène",
                "Пётр Ильич Чайковский",
                composers: "Pyotr Ilyich Tchaikovsky",
                movement: "Allegro non troppo"),
        };

        var seeds = new List<ImportedTrackSeed>
        {
            new("Haydn: Cello Concerto No. 1 in C Major, Hob. VIIb:1: II. Adagio", "Mstislav Rostropovich, English Chamber Orchestra"),
            new("Lalo: Symphonie espagnole in D Minor, Op. 21: I. Allegro non troppo", "Itzhak Perlman, Daniel Barenboim, Orchestre de Paris"),
            new("Carmen: Prelude. Allegro giocoso - Andante moderato", "Georges Bizet, Herbert von Karajan"),
            new("Allegro non troppo", "Tchaikovsky"),
        };

        var resolved = PlaylistTrackMatcher.ResolveSeeds(seeds, lib);
        Assert.False(resolved[0].IsLocalMatched);
        Assert.False(resolved[1].IsLocalMatched);
        Assert.False(resolved[2].IsLocalMatched);
        Assert.False(resolved[3].IsLocalMatched);
    }
}
