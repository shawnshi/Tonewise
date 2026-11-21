using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Tonewise.Audio;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Playlists;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class StabilityAndSyncRegressionTests
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

    private sealed class SyntheticRampWaveStream : WaveStream, ISampleProvider
    {
        private readonly int _totalFrames;
        private readonly int _channels;
        private readonly int _readDelayMs;
        private int _frameCursor;

        public SyntheticRampWaveStream(int sampleRate = 48000, int channels = 2, int totalFrames = 48000, int readDelayMs = 0)
        {
            _channels = channels;
            _totalFrames = totalFrames;
            _readDelayMs = readDelayMs;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public override WaveFormat WaveFormat { get; }

        public override long Length => (long)_totalFrames * _channels * sizeof(float);

        public override long Position
        {
            get => (long)_frameCursor * _channels * sizeof(float);
            set => _frameCursor = Math.Clamp((int)(value / (_channels * sizeof(float))), 0, _totalFrames);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public int Read(float[] buffer, int offset, int count)
        {
            if (_readDelayMs > 0)
            {
                Thread.Sleep(_readDelayMs);
            }
            int framesRequested = count / _channels;
            int remaining = _totalFrames - _frameCursor;
            if (remaining <= 0) return 0;
            int framesToWrite = Math.Min(framesRequested, remaining);
            for (int f = 0; f < framesToWrite; f++)
            {
                int frameIdx = _frameCursor + f;
                float val = frameIdx / (float)_totalFrames;
                for (int c = 0; c < _channels; c++)
                {
                    buffer[offset + f * _channels + c] = val;
                }
            }
            _frameCursor += framesToWrite;
            return framesToWrite * _channels;
        }
    }

    [Fact]
    public void DecodeReadAheadSampleProvider_StreamsContiguously_SeeksAtomically_AndTracksPositionWithoutLockContention()
    {
        const int sr = 48000;
        const int ch = 2;
        const int totalFrames = sr; // 1.0s
        using var stream = new SyntheticRampWaveStream(sr, ch, totalFrames, readDelayMs: 2);
        using var readAhead = new DecodeReadAheadSampleProvider(stream, stream, disposable: null, bufferDurationMs: 250);

        Assert.InRange(readAhead.Duration.TotalSeconds, 0.99, 1.01);

        // Give producer thread a moment to fill ahead
        Thread.Sleep(40);
        var buf = new float[2048];
        int firstRead = readAhead.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, firstRead);
        Assert.Equal(0.0f, buf[0], 5);
        Assert.Equal(1023f / totalFrames, buf[^1], 5);

        // Position polling remains instantaneous & non-blocking
        var posBeforeSeek = readAhead.Position;
        Assert.InRange(posBeforeSeek.TotalMilliseconds, 15.0, 30.0);

        // Seek to exact midpoint (0.5s = frame 24000)
        readAhead.Seek(0.5);
        Assert.InRange(readAhead.Position.TotalSeconds, 0.49, 0.51);

        // Wait briefly so post-seek decoded frames arrive cleanly
        Thread.Sleep(40);
        var postSeekBuf = new float[1024];
        int postSeekRead = readAhead.Read(postSeekBuf, 0, postSeekBuf.Length);
        Assert.Equal(postSeekBuf.Length, postSeekRead);
        Assert.InRange(postSeekBuf[0], 0.499f, 0.501f);
        Assert.InRange(postSeekBuf[^1], (24000 + 511f) / totalFrames - 0.001f, (24000 + 511f) / totalFrames + 0.001f);
    }

    [Fact]
    public void GaplessSampleProvider_AndAudioEngine_IsolateSubscriberExceptions_AndNeverTouchComDeviceOnTransition()
    {
        using var streamA = new SyntheticRampWaveStream(48000, 2, 1024);
        using var streamB = new SyntheticRampWaveStream(48000, 2, 1024);
        using var raA = new DecodeReadAheadSampleProvider(streamA, streamA, null, 200);
        using var raB = new DecodeReadAheadSampleProvider(streamB, streamB, null, 200);
        Thread.Sleep(30);

        using var gapless = new GaplessSampleProvider("a.flac", streamA, raA, raA);
        var transitioned = new ManualResetEventSlim(false);
        gapless.TrackTransitioned += _ =>
        {
            transitioned.Set();
            throw new InvalidCastException("Simulated subscriber exception on ThreadPool");
        };

        Assert.True(gapless.TryStageNext("b.flac", streamB, raB, raB, 0.0));

        float[] outBuf = new float[4096];
        int total = 0;
        while (total < 4096)
        {
            int n = gapless.Read(outBuf, total, 4096 - total);
            if (n <= 0) break;
            total += n;
        }
        Assert.Equal(4096, total);
        Assert.True(transitioned.Wait(2000));
        Thread.Sleep(50);

        // Structural contract check: UpdateSignalPathSnapshotLocked must use cached strings & APO status
        string engineSrc = File.ReadAllText(Path.Combine(FindRepoRoot(), "desktop", "src", "Tonewise.Audio", "TonewiseAudioEngine.cs"));
        Assert.Contains("_cachedDeviceName", engineSrc);
        Assert.Contains("_cachedDeviceId", engineSrc);
        Assert.Contains("_cachedApoStatus", engineSrc);
        int snapStart = engineSrc.IndexOf("private DesktopSignalPathState UpdateSignalPathSnapshotLocked(", StringComparison.Ordinal);
        int snapEnd = engineSrc.IndexOf("private readonly record struct DetachedPlaybackHandles", snapStart, StringComparison.Ordinal);
        Assert.True(snapStart > 0 && snapEnd > snapStart);
        string snapBody = engineSrc[snapStart..snapEnd];
        Assert.DoesNotContain("_device?", snapBody);
        Assert.DoesNotContain("SystemApoConflictDetector.CheckEndpointConflict", snapBody);
    }

    [Fact]
    public void DspSampleProvider_AtomicSeekResetFadeIn_AndMonotonicDequeLimiter_BoundPeaksAt192kHz()
    {
        const int sr = 192_000;
        const int ch = 2;
        const int frames = 4096;
        var hotSource = new HotTransientWaveProvider(sr, ch, frames);
        using var dsp = new DspSampleProvider(hotSource) { IsTpdfDitherEnabled = true };
        dsp.Configure(EqualizerApoProfile.Parse("Filter: ON PK Fc 1000 Hz Gain 4.0 dB Q 1.0"), replayGainDb: 6.0);
        dsp.ConfigureExtendedDsp(
            gainMatchedBypass: false,
            rmsShiftDbOverride: null,
            crossfeedEnabled: false,
            crossfeedCutoffHz: 700.0,
            crossfeedFeedDb: 4.5,
            crossfeedDelayMs: 0.28,
            balanceDb: 0.0,
            monoFold: false,
            limiterEnabled: true,
            limiterCeilingDb: -0.2);

        double ceilingLinear = Math.Pow(10.0, -0.2 / 20.0);
        float[] buf = new float[2048];
        int read1 = dsp.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, read1);
        for (int i = 0; i < read1; i++)
        {
            Assert.True(float.IsFinite(buf[i]), $"Non-finite sample at {i}");
            Assert.InRange(Math.Abs(buf[i]), 0.0f, (float)(ceilingLinear + 1e-3));
        }

        // During seek silence hold, output must be exact 0.0 without clicks
        dsp.BeginSeekSilenceHold();
        float[] silenceBuf = new float[512];
        int silenceRead = dsp.Read(silenceBuf, 0, silenceBuf.Length);
        Assert.Equal(silenceBuf.Length, silenceRead);
        Assert.All(silenceBuf, s => Assert.Equal(0.0f, s));

        // Atomic filter reset + fade-in ramps cleanly from near zero
        dsp.ResetFilterStatesAndBeginFadeIn(durationMs: 12.0, startFromZero: true);
        float[] fadeBuf = new float[2048];
        int fadeRead = dsp.Read(fadeBuf, 0, fadeBuf.Length);
        Assert.Equal(fadeBuf.Length, fadeRead);
        Assert.True(Math.Abs(fadeBuf[0]) < 0.02f, $"Expected gentle fade-in start, got {fadeBuf[0]}");
        for (int i = 0; i < fadeRead; i++)
        {
            Assert.True(float.IsFinite(fadeBuf[i]));
            Assert.InRange(Math.Abs(fadeBuf[i]), 0.0f, (float)(ceilingLinear + 1e-3));
        }
    }

    [Fact]
    public void SyncOutboxAndPairingStore_FilterAndRankLanIpv4Candidates_ExcludesNexGenMetaTunnel198_18()
    {
        var candidates = new[]
        {
            new SyncOutboxAndPairingStore.LanInterfaceCandidate(
                Name: "NexGen",
                Description: "Meta Tunnel",
                InterfaceType: (NetworkInterfaceType)53,
                IsUp: true,
                HasIpv4Gateway: true,
                Address: IPAddress.Parse("198.18.0.1")),
            new SyncOutboxAndPairingStore.LanInterfaceCandidate(
                Name: "vEthernet (WSL)",
                Description: "Hyper-V Virtual Ethernet Adapter",
                InterfaceType: NetworkInterfaceType.Ethernet,
                IsUp: true,
                HasIpv4Gateway: false,
                Address: IPAddress.Parse("172.24.16.1")),
            new SyncOutboxAndPairingStore.LanInterfaceCandidate(
                Name: "ZeroTier One",
                Description: "ZeroTier Virtual Port",
                InterfaceType: NetworkInterfaceType.Ethernet,
                IsUp: true,
                HasIpv4Gateway: false,
                Address: IPAddress.Parse("10.147.20.5")),
            new SyncOutboxAndPairingStore.LanInterfaceCandidate(
                Name: "Wi-Fi 2",
                Description: "Microsoft Wi-Fi Direct Virtual Adapter",
                InterfaceType: NetworkInterfaceType.Wireless80211,
                IsUp: false,
                HasIpv4Gateway: false,
                Address: IPAddress.Parse("169.254.0.13")),
            new SyncOutboxAndPairingStore.LanInterfaceCandidate(
                Name: "Ethernet",
                Description: "Realtek PCIe GbE Family Controller",
                InterfaceType: NetworkInterfaceType.Ethernet,
                IsUp: true,
                HasIpv4Gateway: false,
                Address: IPAddress.Parse("10.0.0.50")),
            new SyncOutboxAndPairingStore.LanInterfaceCandidate(
                Name: "Wi-Fi",
                Description: "MediaTek Wi-Fi 7 MT7925 Wireless LAN Card",
                InterfaceType: NetworkInterfaceType.Wireless80211,
                IsUp: true,
                HasIpv4Gateway: true,
                Address: IPAddress.Parse("192.168.8.242")),
        };

        var ranked = SyncOutboxAndPairingStore.FilterAndRankLanIpv4Candidates(candidates);
        Assert.Equal(2, ranked.Count);
        Assert.Equal("192.168.8.242", ranked[0].ToString());
        Assert.Equal("10.0.0.50", ranked[1].ToString());
    }

    [Fact]
    public void PlaylistTrackMatcher_BitmaskFuzzyArtistRecall_MatchesLegacySemantics_AndOutboxCacheInvalidatesOnM3u8Edit()
    {
        (string Query, string Key, bool Expected)[] cases =
        [
            ("rachmaninoff", "rachmaninov", true),
            ("tchaikovsky", "chaikovsky", true),
            ("claudio abado", "claudio abbado", true),
            ("beethoven", "ludwig van beethoven", true),
            ("ludwig van beethoven", "beethoven", true),
            ("sergei rachmaninoff", "s. rachmaninov", true),
            ("mozart", "mahler", false),
            ("beethoven", "bach", false),
            ("12345", "abcde", false),
            ("ab", "abcde", false),
            ("周杰伦", "周杰伦同学", false),
        ];

        Type matcherType = typeof(PlaylistTrackMatcher);
        Type indexedTrackType = matcherType.GetNestedType("IndexedTrack", BindingFlags.NonPublic)!;
        object emptyTrackList = Activator.CreateInstance(typeof(List<>).MakeGenericType(indexedTrackType))!;
        MethodInfo mLegacy = matcherType.GetMethod("IsFuzzyArtistTokenMatch", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo mBuildEntry = matcherType.GetMethod("BuildFuzzyArtistKeyEntry", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo mQueryMatch = matcherType.GetMethod("IsFuzzyArtistQueryMatchEntry", BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var (q, key, expected) in cases)
        {
            bool legacy = (bool)mLegacy.Invoke(null, [q, key])!;
            Assert.Equal(expected, legacy);

            object entry = mBuildEntry.Invoke(null, [key, emptyTrackList])!;
            object qEntry = mBuildEntry.Invoke(null, [q, emptyTrackList])!;
            Type entryType = entry.GetType();
            bool qHasSpace = (bool)entryType.GetProperty("HasSpace")!.GetValue(qEntry)!;
            uint qMask = (uint)entryType.GetProperty("CharMask")!.GetValue(qEntry)!;
            string[] qLongWords = (string[])entryType.GetProperty("LongWords")!.GetValue(qEntry)!;
            uint[] qLongWordMasks = (uint[])entryType.GetProperty("LongWordMasks")!.GetValue(qEntry)!;
            string qSurname = (string)entryType.GetProperty("Surname")!.GetValue(qEntry)!;
            uint qSurnameMask = (uint)entryType.GetProperty("SurnameMask")!.GetValue(qEntry)!;

            bool fast = (bool)mQueryMatch.Invoke(
                null,
                [q, qHasSpace, qMask, qLongWords, qLongWordMasks, qSurname, qSurnameMask, entry])!;
            Assert.Equal(legacy, fast);
        }

        // Verify cold + warm FuzzyTokenQueryCache via public ResolveSeeds
        var lib = new[]
        {
            new LocalLibraryTrackRef("u_rach", "Rachmaninov/01.flac", @"C:\Music\Rachmaninov\01.flac", "Piano Concerto No. 2", "Sergei Rachmaninov", "Concertos", 600_000, 96_000),
            new LocalLibraryTrackRef("u_bach", "Bach/01.flac", @"C:\Music\Bach\01.flac", "Goldberg Variations", "Johann Sebastian Bach", "Goldberg", 240_000, 44_100),
        };
        var libIndex = PlaylistTrackMatcher.BuildIndex(lib);
        var seeds = new[]
        {
            new ImportedTrackSeed("Piano Concreto No. 2", "Sergei Rachmaninoff", null, null, 600_000),
            new ImportedTrackSeed("Concerto No. 9", "Wolfgang Amadeus Mozart", null, null, 300_000),
        };
        var resolved1 = PlaylistTrackMatcher.ResolveSeeds(seeds, libIndex);
        var resolved2 = PlaylistTrackMatcher.ResolveSeeds(seeds, libIndex);
        Assert.Equal("u_rach", resolved1[0].MatchedTrackUid);
        Assert.Null(resolved1[1].MatchedTrackUid);
        Assert.Equal("u_rach", resolved2[0].MatchedTrackUid);
        Assert.Null(resolved2[1].MatchedTrackUid);

        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_outbox_cache_" + Guid.NewGuid().ToString("N"));
        string tonewiseDir = Path.Combine(tempRoot, ".tonewise");
        string dbPath = Path.Combine(tonewiseDir, "manifest.db");
        try
        {
            string schemaPath = Path.Combine(FindRepoRoot(), "protocol", "schemas", "manifest.sql");
            Directory.CreateDirectory(tonewiseDir);

            string f1 = Path.Combine(tempRoot, "ArtistA", "01.flac");
            string f2 = Path.Combine(tempRoot, "ArtistA", "02.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(f1)!);
            File.WriteAllBytes(f1, [0x66, 0x4C, 0x61, 0x43, 1, 2, 3, 4]);
            File.WriteAllBytes(f2, [0x66, 0x4C, 0x61, 0x43, 5, 6, 7, 8]);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var setup = conn.CreateCommand();
                setup.CommandText = File.ReadAllText(schemaPath);
                setup.ExecuteNonQuery();

                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO tracks (
                        track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                        rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms,
                        title, album, album_artist, artists_json, composers_json, conductors_json, genre
                    ) VALUES
                    ('u_1', X'AA01', 4, X'BB01', 0, 'ArtistA/01.flac', 8, 1000, 'flac', 44100, 180000, 'First Song', 'AlbumA', 'ArtistA', '["ArtistA"]', '[]', '[]', 'Rock'),
                    ('u_2', X'AA02', 4, X'BB02', 0, 'ArtistA/02.flac', 8, 2000, 'flac', 44100, 190000, 'Second Song', 'AlbumA', 'ArtistA', '["ArtistA"]', '[]', '[]', 'Rock');
                    """;
                ins.ExecuteNonQuery();
            }

            string playlistsDir = Path.Combine(tempRoot, "Playlists");
            Directory.CreateDirectory(playlistsDir);
            string m3uPath = Path.Combine(playlistsDir, "Mix.m3u8");
            File.WriteAllText(
                m3uPath,
                "#EXTM3U\n#PLAYLIST:Mix\n#EXTINF:180,ArtistA - First Song\nArtistA/01.flac\n",
                new UTF8Encoding(false));

            var store = new SyncOutboxAndPairingStore(tempRoot);
            store.EnqueuePlaylist("Mix");

            var first = store.ResolveOutbox();
            Assert.Single(first.Tracks);
            Assert.Equal("u_1", first.Tracks[0].TrackUid);

            var cached = store.ResolveOutbox();
            Assert.Single(cached.Tracks);
            Assert.Equal("u_1", cached.Tracks[0].TrackUid);

            File.AppendAllText(
                m3uPath,
                "#EXTINF:190,ArtistA - Second Song\nArtistA/02.flac\n",
                new UTF8Encoding(false));

            var invalidated = store.ResolveOutbox();
            Assert.Equal(2, invalidated.Tracks.Count);
            Assert.Equal(["u_1", "u_2"], invalidated.Playlists.Single().TrackUids);
        }
        finally
        {
            var roCsb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 5
            };
            using (var roConn = new SqliteConnection(roCsb.ToString()))
            {
                SqliteConnection.ClearPool(roConn);
            }
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class HotTransientWaveProvider : ISampleProvider
    {
        private readonly int _channels;
        private readonly int _totalFrames;
        private int _pos;

        public HotTransientWaveProvider(int sampleRate, int channels, int totalFrames)
        {
            _channels = channels;
            _totalFrames = totalFrames;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = Math.Min(count / _channels, _totalFrames - _pos);
            if (frames <= 0) return 0;
            for (int f = 0; f < frames; f++)
            {
                int idx = _pos + f;
                float val = (idx % 512) < 64 ? 0.95f : 0.25f * MathF.Sin(2.0f * MathF.PI * 1000.0f * idx / WaveFormat.SampleRate);
                for (int c = 0; c < _channels; c++)
                {
                    buffer[offset + f * _channels + c] = val;
                }
            }
            _pos += frames;
            return frames * _channels;
        }
    }

    private sealed class BlockingFirstReadWaveStream : WaveStream, ISampleProvider
    {
        private readonly ManualResetEventSlim _release;
        private bool _blockedOnce;

        public BlockingFirstReadWaveStream(ManualResetEventSlim release)
        {
            _release = release;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        }

        public override WaveFormat WaveFormat { get; }
        public override long Length => 48000L * 2 * sizeof(float);
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public int Read(float[] buffer, int offset, int count)
        {
            if (!_blockedOnce)
            {
                _blockedOnce = true;
                _release.Wait(2000);
            }
            return 0;
        }
    }

    [Fact]
    public void DecodeReadAheadAndDsp_StreamAndSilenceHold_ThroughNAudioSampleToWaveProviderWaveBuffer()
    {
        const int sr = 48000;
        const int ch = 2;
        const int totalFrames = sr * 2; // 2.0s ramp
        using var stream = new SyntheticRampWaveStream(sr, ch, totalFrames, readDelayMs: 0);
        using var readAhead = new DecodeReadAheadSampleProvider(stream, stream, disposable: null, bufferDurationMs: 200);
        using var gapless = new GaplessSampleProvider("ramp.flac", stream, readAhead, readAhead);
        using var dsp = new DspSampleProvider(gapless) { IsTpdfDitherEnabled = false };
        var waveProvider = new SampleToWaveProvider(dsp);

        // Pull > 330ms across 200ms ring buffer wrap-arounds via SampleToWaveProvider (WaveBuffer byte[] alias)
        byte[] chunk = new byte[16384];
        int totalBytes = 0;
        float prevSample = -1.0f;
        while (totalBytes < 128_000)
        {
            int n = waveProvider.Read(chunk, 0, chunk.Length);
            Assert.Equal(chunk.Length, n);
            for (int b = 0; b < n; b += sizeof(float))
            {
                float sample = BitConverter.ToSingle(chunk, b);
                Assert.True(float.IsFinite(sample));
                Assert.True(sample >= prevSample - 1e-6f, $"Expected monotonic ramp, got {sample} after {prevSample}");
                prevSample = sample;
            }
            totalBytes += n;
        }
        Assert.True(readAhead.Position > TimeSpan.FromMilliseconds(200));

        // Verify non-zero byte offset copy via SampleToWaveProvider preserves surrounding guard bytes
        byte[] offsetChunk = new byte[4096];
        Array.Fill(offsetChunk, (byte)0x5A);
        int offsetRead = waveProvider.Read(offsetChunk, 64, 2048);
        Assert.Equal(2048, offsetRead);
        for (int i = 0; i < 64; i++) Assert.Equal((byte)0x5A, offsetChunk[i]);
        for (int i = 64 + 2048; i < offsetChunk.Length; i++) Assert.Equal((byte)0x5A, offsetChunk[i]);
        Assert.True(BitConverter.ToSingle(offsetChunk, 64) > 0.0f);

        // During seek silence hold, DspSampleProvider must zero ALL 4 bytes per float on WaveBuffer
        dsp.BeginSeekSilenceHold();
        byte[] silenceBytes = new byte[4096];
        Array.Fill(silenceBytes, (byte)0x7F);
        int silenceRead = waveProvider.Read(silenceBytes, 64, 2048);
        Assert.Equal(2048, silenceRead);
        for (int i = 0; i < 64; i++) Assert.Equal((byte)0x7F, silenceBytes[i]);
        for (int i = 64; i < 64 + 2048; i++) Assert.Equal((byte)0x00, silenceBytes[i]);
        for (int i = 64 + 2048; i < silenceBytes.Length; i++) Assert.Equal((byte)0x7F, silenceBytes[i]);

        // Verify DecodeReadAheadSampleProvider.Read bounded silence hold on a WaveBuffer alias zeroes all 4 bytes per float
        using var gate = new ManualResetEventSlim(false);
        using var blockingStream = new BlockingFirstReadWaveStream(gate);
        using var stalledReadAhead = new DecodeReadAheadSampleProvider(blockingStream, blockingStream, disposable: null, bufferDurationMs: 200);
        try
        {
            byte[] directBytes = new byte[4096];
            Array.Fill(directBytes, (byte)0x7F);
            var wb = new WaveBuffer(directBytes);
            int holdSamples = stalledReadAhead.Read(wb.FloatBuffer, 16, 256);
            Assert.Equal(256, holdSamples);
            int startByte = 16 * sizeof(float);
            int endByte = startByte + holdSamples * sizeof(float);
            for (int i = 0; i < startByte; i++) Assert.Equal((byte)0x7F, directBytes[i]);
            for (int i = startByte; i < endByte; i++) Assert.Equal((byte)0x00, directBytes[i]);
            for (int i = endByte; i < directBytes.Length; i++) Assert.Equal((byte)0x7F, directBytes[i]);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void TonewiseAudioEngine_PlaybackStopped_OffloadsErrorTeardownOffAudioThread()
    {
        string engineSrc = File.ReadAllText(Path.Combine(FindRepoRoot(), "desktop", "src", "Tonewise.Audio", "TonewiseAudioEngine.cs"));
        int start = engineSrc.IndexOf("player.PlaybackStopped +=", StringComparison.Ordinal);
        int end = engineSrc.IndexOf("if (initialPosition.HasValue", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        string handlerBody = engineSrc[start..end];

        Assert.DoesNotContain("DisposeDetachedHandles(errorDetach);", handlerBody);
        Assert.Contains("Task.Run(() => DisposeDetachedHandles(handlesToDispose))", handlerBody);
        Assert.Contains("_pendingErrorTeardown", handlerBody);
    }
}
