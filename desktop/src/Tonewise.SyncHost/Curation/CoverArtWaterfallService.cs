using System.Buffers.Binary;
using System.Text.Json;

namespace Tonewise.SyncHost.Curation;

public sealed record CoverFetchResult(
    bool Success,
    string SourceTier,
    byte[]? ImageBytes,
    int Width,
    int Height,
    string? ErrorMessage = null
);

public sealed record CoverSaveOutcome(
    bool Saved,
    bool SkippedAlreadyExists,
    string OutputPath,
    int Width,
    int Height,
    string? Reason = null
);

/// <summary>
/// 3-tier album cover waterfall fetcher (`Cover Art Archive -> iTunes cn/jp/us -> Last.fm`)
/// enforcing >= 600x600 square aspect gates and folder-level single `cover.jpg` persistence.
/// </summary>
public sealed class CoverArtWaterfallService
{
    public const int MinimumDimensionPx = 600;
    public const double MaxAspectRatioDeviation = 0.05;

    private readonly HttpClient _http;

    public CoverArtWaterfallService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? CreateDefaultHttpClient();
    }

    public static bool ValidateImageHeaderAndAspectRatio(
        ReadOnlySpan<byte> imageBytes,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (!TryReadImageDimensions(imageBytes, out width, out height))
        {
            return false;
        }
        if (width < MinimumDimensionPx || height < MinimumDimensionPx)
        {
            return false;
        }
        double deviation = Math.Abs(width - height) / (double)Math.Max(width, height);
        return deviation <= MaxAspectRatioDeviation;
    }

    public CoverSaveOutcome SaveFolderCoverIfMissing(
        string albumDirectory,
        byte[] imageBytes,
        bool overwriteExisting = false)
    {
        Directory.CreateDirectory(albumDirectory);
        string coverPath = Path.Combine(albumDirectory, "cover.jpg");

        if (!overwriteExisting && File.Exists(coverPath) && new FileInfo(coverPath).Length > 0)
        {
            return new CoverSaveOutcome(false, true, coverPath, 0, 0, "Folder cover.jpg already exists (Fill-Missing Only)");
        }

        if (!ValidateImageHeaderAndAspectRatio(imageBytes, out int w, out int h))
        {
            return new CoverSaveOutcome(
                false,
                false,
                coverPath,
                w,
                h,
                $"Rejected low-resolution or skewed image ({w}x{h}; required >= {MinimumDimensionPx}x{MinimumDimensionPx} square)"
            );
        }

        string tempPath = coverPath + ".tmp";
        File.WriteAllBytes(tempPath, imageBytes);
        File.Move(tempPath, coverPath, overwrite: true);
        return new CoverSaveOutcome(true, false, coverPath, w, h);
    }

    public async Task<CoverFetchResult> FetchBestCoverAsync(
        string artist,
        string album,
        string? releaseMbid = null,
        string? lastFmApiKey = null,
        CancellationToken ct = default)
    {
        // Tier 1: Cover Art Archive
        if (!string.IsNullOrWhiteSpace(releaseMbid))
        {
            string caaUrl = $"https://coverartarchive.org/release/{Uri.EscapeDataString(releaseMbid.Trim())}/front-1200";
            var caa = await TryDownloadAndValidateAsync(caaUrl, "CoverArtArchive", ct);
            if (caa.Success) return caa;
        }

        // Tier 2: iTunes Search API across cn -> jp -> us storefronts
        string query = Uri.EscapeDataString($"{artist} {album}".Trim());
        foreach (string country in new[] { "cn", "jp", "us" })
        {
            string itunesUrl = $"https://itunes.apple.com/search?term={query}&entity=album&limit=3&country={country}";
            var itunes = await TryFetchFromItunesAsync(itunesUrl, $"iTunes-{country.ToUpperInvariant()}", ct);
            if (itunes.Success) return itunes;
        }

        // Tier 3: Last.fm fallback
        if (!string.IsNullOrWhiteSpace(lastFmApiKey))
        {
            string lfmUrl =
                $"https://ws.audioscrobbler.com/2.0/?method=album.getinfo&artist={Uri.EscapeDataString(artist)}&album={Uri.EscapeDataString(album)}&api_key={Uri.EscapeDataString(lastFmApiKey)}&format=json";
            var lfm = await TryFetchFromLastFmAsync(lfmUrl, ct);
            if (lfm.Success) return lfm;
        }

        return new CoverFetchResult(false, "None", null, 0, 0, "No qualifying >= 600x600 cover found across 3 waterfall tiers");
    }

    private async Task<CoverFetchResult> TryFetchFromItunesAsync(string searchUrl, string tierName, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(searchUrl, ct);
            if (!resp.IsSuccessStatusCode) return Fail(tierName);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return Fail(tierName);

            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("artworkUrl100", out var artProp)) continue;
                string? rawUrl = artProp.GetString();
                if (string.IsNullOrWhiteSpace(rawUrl)) continue;
                string hiResUrl = rawUrl
                    .Replace("100x100bb", "1200x1200bb", StringComparison.OrdinalIgnoreCase)
                    .Replace("60x60bb", "1200x1200bb", StringComparison.OrdinalIgnoreCase);

                var fetched = await TryDownloadAndValidateAsync(hiResUrl, tierName, ct);
                if (fetched.Success) return fetched;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Try next waterfall tier */ }

        return Fail(tierName);
    }

    private async Task<CoverFetchResult> TryFetchFromLastFmAsync(string url, CancellationToken ct)
    {
        const string tier = "LastFm";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Fail(tier);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("album", out var albumEl)) return Fail(tier);
            if (!albumEl.TryGetProperty("image", out var images)) return Fail(tier);

            string? bestUrl = null;
            foreach (var img in images.EnumerateArray())
            {
                string? candidate = img.GetProperty("#text").GetString();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    bestUrl = candidate;
                }
            }
            if (!string.IsNullOrWhiteSpace(bestUrl))
            {
                return await TryDownloadAndValidateAsync(bestUrl, tier, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Exhausted fallback */ }

        return Fail(tier);
    }

    private async Task<CoverFetchResult> TryDownloadAndValidateAsync(string url, string tierName, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Fail(tierName);
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!ValidateImageHeaderAndAspectRatio(bytes, out int w, out int h))
            {
                return new CoverFetchResult(false, tierName, null, w, h, $"Dimension gate failed ({w}x{h})");
            }
            return new CoverFetchResult(true, tierName, bytes, w, h);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Fail(tierName); }
    }

    private static bool TryReadImageDimensions(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        // PNG signature + IHDR chunk (width at 16..20, height at 20..24)
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
            return width > 0 && height > 0;
        }

        // JPEG SOI (FF D8) -> scan SOF0..SOF3 / SOF5..SOF7 / SOF9..SOF11 markers
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            int pos = 2;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] != 0xFF) break;
                byte marker = data[pos + 1];
                pos += 2;

                if (marker == 0xD8 || marker == 0xD9 || marker == 0x00) continue;
                if (marker == 0xDA) break; // SOS reached

                if (pos + 2 > data.Length) break;
                int segLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
                if (segLen < 2 || pos + segLen > data.Length) break;

                bool isSof = marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB;
                if (isSof && segLen >= 7)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 5, 2));
                    return width > 0 && height > 0;
                }
                pos += segLen;
            }
        }

        return false;
    }

    private static CoverFetchResult Fail(string tier) => new(false, tier, null, 0, 0);

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TonewiseDesktop/1.0 (https://github.com/shawnshi/Tonewise)");
        return client;
    }
}
