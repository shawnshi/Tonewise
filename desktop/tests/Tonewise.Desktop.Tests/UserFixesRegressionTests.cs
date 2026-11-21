using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using Tonewise.Audio;
using Tonewise.SyncHost;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class UserFixesRegressionTests
{
    private sealed class ConstantStereoSampleProvider(float amplitude) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int FramesRead { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] = amplitude;
            }
            FramesRead += count / WaveFormat.Channels;
            return count;
        }
    }

    [Fact]
    public void DspSampleProvider_TransportFadeOut_RampsSmoothlyAndFreezesSourceInSilenceHold()
    {
        var source = new ConstantStereoSampleProvider(0.8f);
        var dsp = new DspSampleProvider(source)
        {
            IsTpdfDitherEnabled = false,
            IsBypassed = true, // Bit-Perfect mode must STILL click-free fade on pause/resume
        };

        // SteadyFull stays strictly bit-exact in bypass
        var steady = new float[20];
        Assert.Equal(20, dsp.Read(steady, 0, steady.Length));
        Assert.Equal(0.8f, steady[0]);
        Assert.Equal(0.8f, steady[19]);
        Assert.Equal(10, source.FramesRead);

        // 15ms @ 48kHz = 720 frames
        dsp.BeginTransportFadeOut(durationMs: 15.0);
        var fadeOutBuf = new float[720 * 2];
        int nOut = dsp.Read(fadeOutBuf, 0, fadeOutBuf.Length);
        Assert.Equal(fadeOutBuf.Length, nOut);

        float prevL = 0.8f;
        for (int frame = 0; frame < 720; frame++)
        {
            float sL = fadeOutBuf[frame * 2];
            float sR = fadeOutBuf[frame * 2 + 1];
            Assert.Equal(sL, sR, 6);
            Assert.True(sL <= prevL + 1e-6f, $"Fade-out must be non-increasing at frame {frame}: prev={prevL}, cur={sL}");
            prevL = sL;
        }
        Assert.Equal(0.0f, fadeOutBuf[^2], 5);
        Assert.Equal(0.0f, fadeOutBuf[^1], 5);

        int framesReadAtFadeEnd = source.FramesRead;
        // Now drain silence hold buffers; source position MUST freeze while outputting zero samples
        var holdBuf = new float[1920 * 2];
        int nHold = dsp.Read(holdBuf, 0, holdBuf.Length);
        Assert.Equal(holdBuf.Length, nHold);
        Assert.All(holdBuf, s => Assert.Equal(0.0f, s));
        Assert.Equal(framesReadAtFadeEnd, source.FramesRead);
        Assert.True(dsp.HasDrainedPauseSilence(1920));

        // Resume fades smoothly up from 0.0f back to 0.8f
        dsp.BeginTransportFadeIn(durationMs: 15.0, startFromZero: true);
        var fadeInBuf = new float[720 * 2];
        int nIn = dsp.Read(fadeInBuf, 0, fadeInBuf.Length);
        Assert.Equal(fadeInBuf.Length, nIn);
        Assert.InRange(fadeInBuf[0], 0.0f, 0.01f);
        float prevIn = fadeInBuf[0];
        for (int frame = 1; frame < 720; frame++)
        {
            float sL = fadeInBuf[frame * 2];
            Assert.True(sL >= prevIn - 1e-6f, $"Fade-in must be non-decreasing at frame {frame}: prev={prevIn}, cur={sL}");
            prevIn = sL;
        }
        Assert.Equal(0.8f, fadeInBuf[^1], 5);
    }

    [Fact]
    public async Task SyncHostServer_DefaultRejectsPhonePlaylistUpload_WritesZeroFiles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tw_oneway_pl_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            await using var server = new SyncHostServer(tempRoot, "pc-oneway");
            int port = await server.StartAsync(port: 0, enableMdns: false);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var pairResp = await http.PostAsJsonAsync("/api/v2/pair", new
            {
                code = server.Store.CurrentPairingCode,
                device_id = "phone-oneway",
                device_name = "Phone",
            });
            pairResp.EnsureSuccessStatusCode();
            using var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync());
            string token = pairDoc.RootElement.GetProperty("token").GetString()!;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await http.PostAsJsonAsync("/api/v1/playlists/merge", new
            {
                playlist_name = "Phone Local List",
                @base = Array.Empty<string>(),
                ours = Array.Empty<string>(),
                theirs = new[] { "Artist/Album/01.flac" },
            });
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

            string playlistsDir = Path.Combine(tempRoot, "Playlists");
            Assert.False(Directory.Exists(playlistsDir));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
