using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tonewise.SyncHost.Playlists;

public enum PlaylistSourceKind
{
    Custom,
    AppleMusic,
    Billboard,
    Kworb,
    Spotify,
    TextFile,
}

public sealed record ImportedTrackSeed(
    string Title,
    string Artist,
    string? Album = null,
    string? FilePath = null,
    int DurationMs = 0
);

public sealed record ParsedExternalPlaylist(
    string Title,
    PlaylistSourceKind SourceKind,
    IReadOnlyList<ImportedTrackSeed> Tracks
);

public sealed record PlaylistEntry(
    string? RelPath,
    string Title,
    string Artist,
    string? Album,
    int DurationMs,
    bool IsLocalMatched,
    string? MatchedTrackUid = null
)
{
    public string DisplayLabel =>
        (IsLocalMatched ? "● " : "○ ")
        + (string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}");
}

public sealed record PlaylistDocument(
    string Name,
    string FilePath,
    IReadOnlyList<PlaylistEntry> Entries
);

public sealed record PlaylistSummary(
    string Id,
    string Name,
    string FilePath,
    int TotalCount,
    int LocalMatchedCount
)
{
    public string CountBadge =>
        LocalMatchedCount == TotalCount
            ? $"{TotalCount} 首"
            : $"{TotalCount} 首 (本地 {LocalMatchedCount})";
}

public sealed record PlaylistRematchResult(
    string Name,
    string FilePath,
    int TotalCount,
    int PreviousMatchedCount,
    int MatchedCount,
    int NewlyMatchedCount,
    int PathRepairedCount,
    bool WasSaved
);

public sealed record PlaylistBatchRematchSummary(
    int PlaylistsScanned,
    int PlaylistsUpdated,
    int TotalTracks,
    int TotalMatched,
    int NewlyMatchedCount,
    int PathRepairedCount
);

public sealed record LocalLibraryTrackRef(
    string TrackUid,
    string RelPath,
    string FullPath,
    string Title,
    string ArtistsDisplay,
    string Album,
    int DurationMs,
    int SampleRateHz,
    string ComposersDisplay = "",
    string ConductorsDisplay = "",
    string WorkName = "",
    string MovementName = ""
);

public static partial class PlaylistImporter
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".wav", ".m4a", ".alac", ".aac", ".ogg", ".opus", ".mp4", ".aiff", ".dsf", ".dff",
        ".wma", ".ape", ".wv", ".m4b", ".mpc", ".tak"
    };

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    });

    public static async Task<ParsedExternalPlaylist> FetchAndParseUrlAsync(
        string url,
        int maxTracks = 200,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        string trimmed = (url ?? "").Trim();
        var client = httpClient ?? SharedClient.Value;

        if (TryExtractSpotifyPlaylistId(trimmed, out string spotifyId))
        {
            var embedUri = new Uri($"https://open.spotify.com/embed/playlist/{spotifyId}");
            var (embedHtml, _, _) = await FetchHtmlResponseAsync(client, embedUri, ct).ConfigureAwait(false);
            return ParseSpotifyEmbedHtml(embedHtml, fallbackTitle: spotifyId, maxTracks: maxTracks);
        }

        if (string.IsNullOrWhiteSpace(trimmed) || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Please provide a valid http/https playlist URL.", nameof(url));
        }

        var (html, finalUri, mediaType) = await FetchHtmlResponseAsync(client, uri, ct).ConfigureAwait(false);

        if (!IsSpotifyEmbedUri(finalUri)
            && TryExtractSpotifyPlaylistId(finalUri.AbsoluteUri, out string redirectedSpotifyId))
        {
            var embedUri = new Uri($"https://open.spotify.com/embed/playlist/{redirectedSpotifyId}");
            var (embedHtml, _, _) = await FetchHtmlResponseAsync(client, embedUri, ct).ConfigureAwait(false);
            return ParseSpotifyEmbedHtml(embedHtml, fallbackTitle: redirectedSpotifyId, maxTracks: maxTracks);
        }

        string fallbackTitle = DeriveTitleFromUri(finalUri);
        string host = finalUri.Host;

        if (host.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase) && IsSpotifyEmbedUri(finalUri))
        {
            return ParseSpotifyEmbedHtml(html, fallbackTitle, maxTracks);
        }
        if (host.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAppleMusicHtml(html, fallbackTitle, maxTracks);
        }
        if (host.Contains("billboard.com", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBillboardHtml(html, fallbackTitle, maxTracks);
        }
        if (host.Contains("kworb.net", StringComparison.OrdinalIgnoreCase))
        {
            return ParseKworbHtml(html, fallbackTitle, maxTracks);
        }

        if (html.Contains("__NEXT_DATA__", StringComparison.OrdinalIgnoreCase)
            && html.Contains("\"trackList\"", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSpotifyEmbedHtml(html, fallbackTitle, maxTracks);
        }
        if (html.Contains("serialized-server-data", StringComparison.OrdinalIgnoreCase))
        {
            var apple = ParseAppleMusicHtml(html, fallbackTitle, maxTracks);
            if (apple.Tracks.Count > 0) return apple;
        }
        if (html.Contains("o-chart-results-list-row-container", StringComparison.OrdinalIgnoreCase))
        {
            var bb = ParseBillboardHtml(html, fallbackTitle, maxTracks);
            if (bb.Tracks.Count > 0) return bb;
        }
        if (html.Contains("<td", StringComparison.OrdinalIgnoreCase) && html.Contains("class=\"text\"", StringComparison.OrdinalIgnoreCase))
        {
            var kw = ParseKworbHtml(html, fallbackTitle, maxTracks);
            if (kw.Tracks.Count > 0) return kw;
        }

        if (LooksLikeHtmlDocument(mediaType, html))
        {
            throw new NotSupportedException(
                "暂不支持解析该网页链接。当前支持 Spotify、Apple Music、Billboard、Kworb 公开歌单/榜单，或纯文本 / .m3u8 链接。");
        }

        return ParseTextPlaylist(html, fallbackTitle, maxTracks);
    }

    private static async Task<(string Html, Uri FinalUri, string? MediaType)> FetchHtmlResponseAsync(
        HttpClient client,
        Uri uri,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Uri finalUri = resp.RequestMessage?.RequestUri ?? uri;
        string? mediaType = resp.Content.Headers.ContentType?.MediaType;
        return (html, finalUri, mediaType);
    }

    private static bool IsSpotifyEmbedUri(Uri uri) =>
        uri.Host.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase)
        && (uri.AbsolutePath.Contains("/embed/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/embed-legacy/", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeHtmlDocument(string? mediaType, string content)
    {
        if (!string.IsNullOrWhiteSpace(mediaType)
            && (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string sample = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (sample.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
            || sample.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return sample.Contains("<head", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("<body", StringComparison.OrdinalIgnoreCase)
            || sample.Contains("<script", StringComparison.OrdinalIgnoreCase);
    }

    public static ParsedExternalPlaylist ParseAppleMusicHtml(
        string html,
        string? fallbackTitle = null,
        int maxTracks = 500)
    {
        string rawTitle = ExtractMetaOgTitle(html) ?? ExtractHtmlTitle(html) ?? fallbackTitle ?? "Apple Music Playlist";
        string title = CleanExternalTitle(rawTitle, " on Apple Music", " - Apple Music", " - 播放列表 - Apple Music", "‎");

        var seeds = new List<ImportedTrackSeed>();
        var match = AppleSerializedDataRegex().Match(html);
        if (match.Success)
        {
            string jsonText = WebUtility.HtmlDecode(match.Groups["json"].Value.Trim());
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                WalkAppleJsonElement(doc.RootElement, seeds, maxTracks);
            }
            catch
            {
                // Fall through to LD+JSON fallback if serialized payload is malformed
            }
        }

        if (seeds.Count == 0)
        {
            foreach (Match ldMatch in LdJsonRegex().Matches(html))
            {
                try
                {
                    using var doc = JsonDocument.Parse(ldMatch.Groups["json"].Value.Trim());
                    ExtractFromLdJson(doc.RootElement, seeds, maxTracks);
                }
                catch
                {
                    // Ignore malformed LD+JSON script block
                }
            }
        }

        var deduped = DeduplicateConsecutive(seeds, maxTracks);
        return new ParsedExternalPlaylist(
            string.IsNullOrWhiteSpace(title) ? "Apple Music Playlist" : title,
            PlaylistSourceKind.AppleMusic,
            deduped
        );
    }

    public static ParsedExternalPlaylist ParseBillboardHtml(
        string html,
        string? fallbackTitle = null,
        int maxTracks = 500)
    {
        string rawTitle = ExtractMetaOgTitle(html) ?? ExtractHtmlTitle(html) ?? fallbackTitle ?? "Billboard Chart";
        string title = CleanExternalTitle(rawTitle, " – Billboard", " - Billboard", " | Billboard");

        var seeds = new List<ImportedTrackSeed>();
        string[] segments = html.Split("o-chart-results-list-row-container", StringSplitOptions.None);
        for (int i = 1; i < segments.Length; i++)
        {
            if (maxTracks > 0 && seeds.Count >= maxTracks) break;
            var m = BillboardRowRegex().Match(segments[i]);
            if (!m.Success) continue;

            string trackTitle = CleanHtmlText(m.Groups["title"].Value);
            string artist = CleanHtmlText(m.Groups["artist"].Value);
            if (!string.IsNullOrWhiteSpace(trackTitle))
            {
                seeds.Add(new ImportedTrackSeed(trackTitle, artist));
            }
        }

        return new ParsedExternalPlaylist(
            string.IsNullOrWhiteSpace(title) ? "Billboard Chart" : title,
            PlaylistSourceKind.Billboard,
            seeds
        );
    }

    public static ParsedExternalPlaylist ParseKworbHtml(
        string html,
        string? fallbackTitle = null,
        int maxTracks = 500)
    {
        string rawTitle = ExtractHtmlTitle(html) ?? fallbackTitle ?? "Kworb Chart";
        string title = CleanExternalTitle(rawTitle, " - Kworb", " | Kworb");

        var seeds = new List<ImportedTrackSeed>();
        foreach (Match tr in HtmlTableRowRegex().Matches(html))
        {
            if (maxTracks > 0 && seeds.Count >= maxTracks) break;
            var td = KworbTextCellRegex().Match(tr.Groups["row"].Value);
            if (!td.Success) continue;

            string cellText = CleanHtmlText(td.Groups["cell"].Value);
            if (string.IsNullOrWhiteSpace(cellText)) continue;

            var (artist, songTitle) = SplitArtistTitle(cellText);
            if (!string.IsNullOrWhiteSpace(songTitle))
            {
                seeds.Add(new ImportedTrackSeed(songTitle, artist));
            }
        }

        return new ParsedExternalPlaylist(
            string.IsNullOrWhiteSpace(title) ? "Kworb Chart" : title,
            PlaylistSourceKind.Kworb,
            seeds
        );
    }

    public static bool TryExtractSpotifyPlaylistId(string? input, out string playlistId)
    {
        playlistId = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        string trimmed = input.Trim();

        var uriMatch = SpotifyUriPlaylistRegex().Match(trimmed);
        if (uriMatch.Success)
        {
            playlistId = uriMatch.Groups["id"].Value;
            return true;
        }

        var urlMatch = SpotifyUrlPlaylistRegex().Match(trimmed);
        if (urlMatch.Success)
        {
            playlistId = urlMatch.Groups["id"].Value;
            return true;
        }

        return false;
    }

    public static ParsedExternalPlaylist ParseSpotifyEmbedHtml(
        string html,
        string? fallbackTitle = null,
        int maxTracks = 500)
    {
        var match = SpotifyNextDataRegex().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "未能从 Spotify 歌单页面解析到曲目，请确认歌单为公开状态且链接有效。");
        }

        string jsonText = WebUtility.HtmlDecode(match.Groups["json"].Value.Trim());
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonText);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "未能从 Spotify 歌单页面解析到曲目，请确认歌单为公开状态且链接有效。",
                ex);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (!TryGetPropertyPath(root, out JsonElement entity, "props", "pageProps", "state", "data", "entity")
                || entity.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "未能从 Spotify 歌单页面解析到曲目，请确认歌单为公开状态且链接有效。");
            }

            string? rawTitle = GetOptionalStringProperty(entity, "name")
                ?? GetOptionalStringProperty(entity, "title")
                ?? ExtractMetaOgTitle(html)
                ?? ExtractHtmlTitle(html)
                ?? fallbackTitle;
            string cleanedTitle = CleanExternalTitle(
                NormalizeSpotifyText(rawTitle),
                " | Spotify",
                " - Spotify");

            var seeds = new List<ImportedTrackSeed>();
            if (entity.TryGetProperty("trackList", out JsonElement trackList)
                && trackList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in trackList.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    string? entityType = GetOptionalStringProperty(item, "entityType");
                    if (!string.IsNullOrWhiteSpace(entityType)
                        && !string.Equals(entityType, "track", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string trackTitle = NormalizeSpotifyText(
                        GetOptionalStringProperty(item, "title")
                        ?? GetOptionalStringProperty(item, "name"));
                    if (string.IsNullOrWhiteSpace(trackTitle)) continue;

                    string artist = NormalizeSpotifyText(GetOptionalStringProperty(item, "subtitle"));
                    if (string.IsNullOrWhiteSpace(artist)
                        && item.TryGetProperty("artists", out JsonElement artistsProp)
                        && artistsProp.ValueKind == JsonValueKind.Array)
                    {
                        var artistNames = new List<string>();
                        foreach (JsonElement artistObj in artistsProp.EnumerateArray())
                        {
                            string? an = NormalizeSpotifyText(GetOptionalStringProperty(artistObj, "name"));
                            if (!string.IsNullOrWhiteSpace(an))
                            {
                                artistNames.Add(an);
                            }
                        }
                        artist = string.Join(", ", artistNames);
                    }

                    int durationMs = 0;
                    if (item.TryGetProperty("duration", out JsonElement durProp)
                        && durProp.ValueKind == JsonValueKind.Number
                        && durProp.TryGetInt64(out long rawDur)
                        && rawDur > 0)
                    {
                        durationMs = (int)Math.Min(rawDur, int.MaxValue);
                    }

                    seeds.Add(new ImportedTrackSeed(trackTitle, artist, DurationMs: durationMs));
                }
            }

            var deduped = DeduplicateConsecutive(seeds, maxTracks);
            if (deduped.Count == 0)
            {
                throw new InvalidOperationException(
                    "未能从 Spotify 歌单页面解析到曲目，请确认歌单为公开状态且链接有效。");
            }

            return new ParsedExternalPlaylist(
                string.IsNullOrWhiteSpace(cleanedTitle) ? "Spotify Playlist" : cleanedTitle,
                PlaylistSourceKind.Spotify,
                deduped
            );
        }
    }

    private static bool TryGetPropertyPath(JsonElement current, out JsonElement target, params string[] path)
    {
        target = current;
        foreach (string key in path)
        {
            if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty(key, out JsonElement next))
            {
                target = default;
                return false;
            }
            target = next;
        }
        return true;
    }

    private static string? GetOptionalStringProperty(JsonElement obj, string propName) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(propName, out JsonElement prop)
        && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string NormalizeSpotifyText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string decoded = raw.Replace('\u00A0', ' ');
        return MultiWhitespaceRegex().Replace(decoded, " ").Trim();
    }

    public static ParsedExternalPlaylist ParseTextPlaylist(
        string content,
        string? fallbackTitle = null,
        int maxTracks = 5000)
    {
        string title = string.IsNullOrWhiteSpace(fallbackTitle) ? "Imported Playlist" : fallbackTitle.Trim();
        var seeds = new List<ImportedTrackSeed>();

        string? pendingArtist = null;
        string? pendingTitle = null;
        int pendingDurationMs = 0;

        string normalized = content.TrimStart('\uFEFF');
        using var reader = new StringReader(normalized);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            if (maxTracks > 0 && seeds.Count >= maxTracks) break;
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase))
            {
                string declared = line["#PLAYLIST:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(declared))
                {
                    title = declared;
                }
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = line.IndexOf(',');
                if (comma >= 0)
                {
                    string durPart = line["#EXTINF:".Length..comma].Trim();
                    int spaceIdx = durPart.IndexOfAny([' ', '\t']);
                    if (spaceIdx > 0) durPart = durPart[..spaceIdx];
                    if (long.TryParse(
                            durPart,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out long secs)
                        && secs > 0)
                    {
                        pendingDurationMs = (int)Math.Min(secs * 1000L, int.MaxValue);
                    }
                    else
                    {
                        pendingDurationMs = 0;
                    }

                    if (comma + 1 < line.Length)
                    {
                        string display = line[(comma + 1)..].Trim();
                        var (art, ttl) = SplitArtistTitle(display);
                        pendingArtist = art;
                        pendingTitle = ttl;
                    }
                }
                continue;
            }

            if (line.StartsWith("#EXTTW-UNMATCHED:", StringComparison.OrdinalIgnoreCase))
            {
                string payload = line["#EXTTW-UNMATCHED:".Length..];
                string[] parts = payload.Split('\t');
                string art = parts.Length > 0 ? parts[0].Trim() : (pendingArtist ?? "");
                string ttl = parts.Length > 1 ? parts[1].Trim() : (pendingTitle ?? "");
                string? alb = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null;
                if (!string.IsNullOrWhiteSpace(ttl))
                {
                    seeds.Add(new ImportedTrackSeed(ttl, art, alb, DurationMs: pendingDurationMs));
                }
                pendingArtist = null;
                pendingTitle = null;
                pendingDurationMs = 0;
                continue;
            }

            if (line.StartsWith('#')) continue;

            bool looksLikePath = LooksLikeAudioFilePath(line);
            if (looksLikePath)
            {
                string fallbackName = Path.GetFileNameWithoutExtension(line);
                seeds.Add(new ImportedTrackSeed(
                    Title: string.IsNullOrWhiteSpace(pendingTitle) ? fallbackName : pendingTitle!,
                    Artist: pendingArtist ?? "",
                    Album: null,
                    FilePath: line,
                    DurationMs: pendingDurationMs
                ));
                pendingArtist = null;
                pendingTitle = null;
                pendingDurationMs = 0;
                continue;
            }

            if (line.Contains('\t'))
            {
                string[] cols = line.Split('\t');
                int startCol = cols.Length >= 3 && int.TryParse(cols[0].Trim(), out _) ? 1 : 0;
                if (startCol + 1 < cols.Length)
                {
                    string art = cols[startCol].Trim();
                    string ttl = cols[startCol + 1].Trim();
                    string? alb = startCol + 2 < cols.Length && !string.IsNullOrWhiteSpace(cols[startCol + 2])
                        ? cols[startCol + 2].Trim()
                        : null;
                    if (!string.IsNullOrWhiteSpace(ttl) || !string.IsNullOrWhiteSpace(art))
                    {
                        seeds.Add(new ImportedTrackSeed(string.IsNullOrWhiteSpace(ttl) ? art : ttl, string.IsNullOrWhiteSpace(ttl) ? "" : art, alb));
                    }
                    pendingArtist = null;
                    pendingTitle = null;
                    pendingDurationMs = 0;
                    continue;
                }
            }

            string cleanedLine = StripChartNumberPrefix(line);
            var (parsedArtist, parsedTitle) = SplitArtistTitle(cleanedLine);
            if (string.IsNullOrWhiteSpace(parsedTitle) && !string.IsNullOrWhiteSpace(pendingTitle))
            {
                parsedTitle = pendingTitle!;
                if (string.IsNullOrWhiteSpace(parsedArtist)) parsedArtist = pendingArtist ?? "";
            }

            if (!string.IsNullOrWhiteSpace(parsedTitle))
            {
                seeds.Add(new ImportedTrackSeed(parsedTitle, parsedArtist, DurationMs: pendingDurationMs));
            }
            pendingArtist = null;
            pendingTitle = null;
            pendingDurationMs = 0;
        }

        return new ParsedExternalPlaylist(title, PlaylistSourceKind.TextFile, seeds);
    }

    private static void WalkAppleJsonElement(JsonElement el, List<ImportedTrackSeed> seeds, int maxTracks)
    {
        if (maxTracks > 0 && seeds.Count >= maxTracks) return;

        if (el.ValueKind == JsonValueKind.Object)
        {
            string? title = el.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String
                ? tProp.GetString()?.Trim()
                : null;

            if (!string.IsNullOrWhiteSpace(title))
            {
                string? artist = null;
                if (el.TryGetProperty("artistName", out var aProp) && aProp.ValueKind == JsonValueKind.String)
                {
                    artist = aProp.GetString()?.Trim();
                }

                bool isTrackLockup = el.TryGetProperty("itemKind", out var ikProp)
                    && ikProp.ValueKind == JsonValueKind.String
                    && (ikProp.GetString() is "trackLockup" or "songLockup");

                if (string.IsNullOrWhiteSpace(artist) && isTrackLockup)
                {
                    artist = ExtractFirstLinkTitle(el, "subtitleLinks");
                }

                if (!string.IsNullOrWhiteSpace(artist))
                {
                    string? album = ExtractFirstLinkTitle(el, "tertiaryLinks");
                    int durationMs = 0;
                    if (el.TryGetProperty("durationInMillis", out var durProp)
                        && durProp.ValueKind == JsonValueKind.Number
                        && durProp.TryGetInt64(out long rawDur)
                        && rawDur > 0)
                    {
                        durationMs = (int)Math.Min(rawDur, int.MaxValue);
                    }
                    seeds.Add(new ImportedTrackSeed(title!, artist!, album, DurationMs: durationMs));
                    if (maxTracks > 0 && seeds.Count >= maxTracks) return;
                }
            }

            foreach (var prop in el.EnumerateObject())
            {
                WalkAppleJsonElement(prop.Value, seeds, maxTracks);
                if (maxTracks > 0 && seeds.Count >= maxTracks) return;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                WalkAppleJsonElement(item, seeds, maxTracks);
                if (maxTracks > 0 && seeds.Count >= maxTracks) return;
            }
        }
    }

    private static string? ExtractFirstLinkTitle(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("title", out var tp)
                && tp.ValueKind == JsonValueKind.String)
            {
                string? val = tp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        return null;
    }

    private static void ExtractFromLdJson(JsonElement root, List<ImportedTrackSeed> seeds, int maxTracks)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("track", out var tracks) || tracks.ValueKind != JsonValueKind.Array) return;

        foreach (var tr in tracks.EnumerateArray())
        {
            if (maxTracks > 0 && seeds.Count >= maxTracks) break;
            if (tr.ValueKind != JsonValueKind.Object) continue;
            string? name = tr.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String
                ? np.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            string artist = "";
            if (tr.TryGetProperty("byArtist", out var ba))
            {
                if (ba.ValueKind == JsonValueKind.String)
                {
                    artist = ba.GetString()?.Trim() ?? "";
                }
                else if (ba.ValueKind == JsonValueKind.Object && ba.TryGetProperty("name", out var an))
                {
                    artist = an.GetString()?.Trim() ?? "";
                }
                else if (ba.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var a in ba.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var an2))
                        {
                            string? v = an2.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(v)) names.Add(v);
                        }
                    }
                    artist = string.Join(", ", names);
                }
            }
            seeds.Add(new ImportedTrackSeed(name, artist));
        }
    }

    private static List<ImportedTrackSeed> DeduplicateConsecutive(List<ImportedTrackSeed> raw, int maxTracks)
    {
        var result = new List<ImportedTrackSeed>(raw.Count);
        foreach (var item in raw)
        {
            if (result.Count > 0)
            {
                var prev = result[^1];
                if (string.Equals(prev.Title, item.Title, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(prev.Artist, item.Artist, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            result.Add(item);
            if (maxTracks > 0 && result.Count >= maxTracks) break;
        }
        return result;
    }

    private static bool LooksLikeAudioFilePath(string line)
    {
        string trimmed = line.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        if (trimmed.Contains('<') || trimmed.Contains('>')
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string ext = Path.GetExtension(trimmed);
        return !string.IsNullOrEmpty(ext) && AudioExtensions.Contains(ext);
    }

    private static string StripChartNumberPrefix(string line)
    {
        string stripped = ChartNumberPrefixRegex().Replace(line, "");
        var m = LeadingDashRankRegex().Match(stripped);
        if (m.Success)
        {
            stripped = stripped[m.Length..];
        }
        return stripped.Trim();
    }

    private static (string Artist, string Title) SplitArtistTitle(string raw)
    {
        foreach (string sep in new[] { " - ", " – ", " — " })
        {
            int idx = raw.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0 && idx + sep.Length < raw.Length)
            {
                string left = raw[..idx].Trim();
                string right = raw[(idx + sep.Length)..].Trim();
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
                {
                    return (left, right);
                }
            }
        }
        return ("", raw.Trim());
    }

    private static string? ExtractMetaOgTitle(string html)
    {
        var m1 = OgTitleContentAfterRegex().Match(html);
        if (m1.Success) return WebUtility.HtmlDecode(m1.Groups["val"].Value.Trim());
        var m2 = OgTitleContentBeforeRegex().Match(html);
        if (m2.Success) return WebUtility.HtmlDecode(m2.Groups["val"].Value.Trim());
        return null;
    }

    private static string? ExtractHtmlTitle(string html)
    {
        var m = HtmlTitleRegex().Match(html);
        return m.Success ? CleanHtmlText(m.Groups["val"].Value) : null;
    }

    private static string CleanExternalTitle(string title, params string[] suffixes)
    {
        string cleaned = title.Trim();
        foreach (string suffix in suffixes)
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^suffix.Length].Trim();
            }
            cleaned = cleaned.Replace("\u200E", "", StringComparison.Ordinal);
        }
        return cleaned.Trim();
    }

    private static string CleanHtmlText(string raw)
    {
        string noTags = HtmlTagRegex().Replace(raw, " ");
        string decoded = WebUtility.HtmlDecode(noTags);
        return MultiWhitespaceRegex().Replace(decoded, " ").Trim();
    }

    private static string DeriveTitleFromUri(Uri uri)
    {
        string last = uri.Segments.LastOrDefault()?.Trim('/') ?? uri.Host;
        string unescaped = Uri.UnescapeDataString(last);
        if (string.IsNullOrWhiteSpace(unescaped) || unescaped.StartsWith("pl.", StringComparison.OrdinalIgnoreCase))
        {
            string prev = uri.Segments.Length >= 2 ? Uri.UnescapeDataString(uri.Segments[^2].Trim('/')) : uri.Host;
            return string.IsNullOrWhiteSpace(prev) ? uri.Host : prev.Replace('-', ' ');
        }
        return Path.GetFileNameWithoutExtension(unescaped).Replace('-', ' ');
    }

    [GeneratedRegex("""^spotify:playlist:(?<id>[A-Za-z0-9]{10,40})(?:[?#].*)?$""", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyUriPlaylistRegex();

    [GeneratedRegex("""^https?://open\.spotify\.com/(?:[^/?#\s]+/)*(?:embed(?:-legacy)?/)?playlist/(?<id>[A-Za-z0-9]{10,40})(?:[/?#].*)?$""", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyUrlPlaylistRegex();

    [GeneratedRegex("""<script[^>]*id=["']?__NEXT_DATA__["']?[^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyNextDataRegex();

    [GeneratedRegex("""<script[^>]*id=["']?serialized-server-data["']?[^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AppleSerializedDataRegex();

    [GeneratedRegex("""<script[^>]*type=["']application/ld\+json["'][^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LdJsonRegex();

    [GeneratedRegex("""<h3[^>]*id=["']title-of-a-story["'][^>]*>\s*(?<title>.*?)\s*</h3>\s*<span[^>]*>\s*(?<artist>.*?)\s*</span>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BillboardRowRegex();

    [GeneratedRegex("""<tr[^>]*>(?<row>.*?)</tr>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTableRowRegex();

    [GeneratedRegex("""<td[^>]*class=["'][^"']*\btext\b[^"']*["'][^>]*>(?<cell>.*?)</td>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex KworbTextCellRegex();

    [GeneratedRegex("""<meta[^>]*property=["']og:title["'][^>]*content=["'](?<val>[^"']*)["']""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleContentAfterRegex();

    [GeneratedRegex("""<meta[^>]*content=["'](?<val>[^"']*)["'][^>]*property=["']og:title["']""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleContentBeforeRegex();

    [GeneratedRegex("""<title[^>]*>(?<val>.*?)</title>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTitleRegex();

    [GeneratedRegex("""<[^>]+>""")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("""\s+""")]
    private static partial Regex MultiWhitespaceRegex();

    [GeneratedRegex("""^\s*\d+\s*[\.\)]\s+""")]
    private static partial Regex ChartNumberPrefixRegex();

    [GeneratedRegex("""^\s*\d{1,4}\s+[-–]\s+(?=.*?\s+[-–]\s+)""")]
    private static partial Regex LeadingDashRankRegex();
}

public static partial class PlaylistTrackMatcher
{
    public sealed class LibraryIndex
    {
        internal IReadOnlyList<IndexedTrack> Tracks { get; }
        internal Dictionary<string, LocalLibraryTrackRef> ByRelPath { get; }
        internal Dictionary<string, LocalLibraryTrackRef> ByFullPath { get; }
        internal Dictionary<string, List<LocalLibraryTrackRef>> ByFileName { get; }
        internal Dictionary<string, List<(IndexedTrack Track, int TitleTier)>> ByTitle { get; }
        internal Dictionary<string, List<(IndexedTrack Track, int TitleTier)>> ByCompactTitle { get; }
        internal Dictionary<string, List<IndexedTrack>> ByArtistToken { get; }
        internal Dictionary<string, List<IndexedTrack>> ByArtistWord { get; }
        internal IReadOnlyList<FuzzyArtistKeyEntry> LongLatinArtistTokenEntries { get; }
        internal IReadOnlyList<FuzzyArtistKeyEntry> LongLatinArtistWordEntries { get; }
        internal ConcurrentDictionary<string, IReadOnlyList<IndexedTrack>> FuzzyTokenQueryCache { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, IReadOnlyList<IndexedTrack>> FuzzyWordQueryCache { get; } = new(StringComparer.Ordinal);

        internal LibraryIndex(
            IReadOnlyList<IndexedTrack> tracks,
            Dictionary<string, LocalLibraryTrackRef> byRelPath,
            Dictionary<string, LocalLibraryTrackRef> byFullPath,
            Dictionary<string, List<LocalLibraryTrackRef>> byFileName,
            Dictionary<string, List<(IndexedTrack Track, int TitleTier)>> byTitle,
            Dictionary<string, List<(IndexedTrack Track, int TitleTier)>> byCompactTitle,
            Dictionary<string, List<IndexedTrack>> byArtistToken,
            Dictionary<string, List<IndexedTrack>> byArtistWord,
            IReadOnlyList<FuzzyArtistKeyEntry> longLatinArtistTokenEntries,
            IReadOnlyList<FuzzyArtistKeyEntry> longLatinArtistWordEntries)
        {
            Tracks = tracks;
            ByRelPath = byRelPath;
            ByFullPath = byFullPath;
            ByFileName = byFileName;
            ByTitle = byTitle;
            ByCompactTitle = byCompactTitle;
            ByArtistToken = byArtistToken;
            ByArtistWord = byArtistWord;
            LongLatinArtistTokenEntries = longLatinArtistTokenEntries;
            LongLatinArtistWordEntries = longLatinArtistWordEntries;
        }
    }

    public static LibraryIndex BuildIndex(IReadOnlyList<LocalLibraryTrackRef> library)
    {
        var indexedTracks = new List<IndexedTrack>(library.Count);
        var byRelPath = new Dictionary<string, LocalLibraryTrackRef>(StringComparer.OrdinalIgnoreCase);
        var byFullPath = new Dictionary<string, LocalLibraryTrackRef>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, List<LocalLibraryTrackRef>>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, List<(IndexedTrack Track, int TitleTier)>>(StringComparer.Ordinal);
        var byCompactTitle = new Dictionary<string, List<(IndexedTrack Track, int TitleTier)>>(StringComparer.Ordinal);
        var byArtistToken = new Dictionary<string, List<IndexedTrack>>(StringComparer.Ordinal);
        var byArtistWord = new Dictionary<string, List<IndexedTrack>>(StringComparer.Ordinal);

        static void AddToTieredTitleIndex(
            Dictionary<string, List<(IndexedTrack Track, int TitleTier)>> indexMap,
            string key,
            IndexedTrack indexed,
            int tier)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!indexMap.TryGetValue(key, out var list))
            {
                list = [];
                indexMap[key] = list;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i].Track, indexed)
                    || string.Equals(list[i].Track.Track.TrackUid, indexed.Track.TrackUid, StringComparison.Ordinal))
                {
                    if (tier > list[i].TitleTier)
                    {
                        list[i] = (indexed, tier);
                    }
                    return;
                }
            }
            list.Add((indexed, tier));
        }

        static void AddToArtistIndex(
            Dictionary<string, List<IndexedTrack>> indexMap,
            string key,
            IndexedTrack indexed)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!indexMap.TryGetValue(key, out var list))
            {
                list = [];
                indexMap[key] = list;
            }
            if (list.Count == 0 || !ReferenceEquals(list[^1], indexed))
            {
                list.Add(indexed);
            }
        }

        var indexedArray = new IndexedTrack[library.Count];
        if (library.Count >= 512)
        {
            Parallel.For(0, library.Count, i => indexedArray[i] = new IndexedTrack(library[i], i));
        }
        else
        {
            for (int i = 0; i < library.Count; i++)
            {
                indexedArray[i] = new IndexedTrack(library[i], i);
            }
        }

        for (int idx = 0; idx < library.Count; idx++)
        {
            var item = library[idx];
            string normRel = NormalizePathKey(item.RelPath);
            if (!string.IsNullOrEmpty(normRel)) byRelPath.TryAdd(normRel, item);

            if (!string.IsNullOrWhiteSpace(item.FullPath))
            {
                byFullPath.TryAdd(item.FullPath.Replace('\\', '/'), item);
                string fn = Path.GetFileName(item.FullPath);
                if (!string.IsNullOrEmpty(fn))
                {
                    if (!byFileName.TryGetValue(fn, out var fnList))
                    {
                        fnList = [];
                        byFileName[fn] = fnList;
                    }
                    fnList.Add(item);
                }
            }

            var indexed = indexedArray[idx];
            indexedTracks.Add(indexed);

            foreach (var v in indexed.TitleVariantInfos)
            {
                AddToTieredTitleIndex(byTitle, v.Normalized, indexed, v.Tier);
                AddToTieredTitleIndex(byCompactTitle, v.Compact, indexed, v.Tier);
            }

            foreach (string artistToken in indexed.AllCreditTokens)
            {
                if (string.IsNullOrEmpty(artistToken) || PlaceholderArtists.Contains(artistToken)) continue;
                AddToArtistIndex(byArtistToken, artistToken, indexed);
            }

            foreach (string word in ExtractSignificantCreditWords(indexed.AllCreditTokens))
            {
                AddToArtistIndex(byArtistWord, word, indexed);
            }
        }

        static List<FuzzyArtistKeyEntry> BuildLongLatinEntries(Dictionary<string, List<IndexedTrack>> map)
        {
            var entries = new List<FuzzyArtistKeyEntry>();
            foreach (var kv in map)
            {
                if (kv.Key.Length >= 5 && !ContainsEastAsianScript(kv.Key))
                {
                    entries.Add(BuildFuzzyArtistKeyEntry(kv.Key, kv.Value));
                }
            }
            return entries;
        }

        return new LibraryIndex(
            indexedTracks,
            byRelPath,
            byFullPath,
            byFileName,
            byTitle,
            byCompactTitle,
            byArtistToken,
            byArtistWord,
            BuildLongLatinEntries(byArtistToken),
            BuildLongLatinEntries(byArtistWord));
    }

    public static IReadOnlyList<string> NormalizeClassicalTitleVariants(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        string preNorm = PreNormalizeUnicode(raw);
        if (string.IsNullOrEmpty(preNorm)) return [];

        string cleaned = StripEditionDecorations(preNorm);
        if (string.IsNullOrEmpty(NormalizePreNormalizedTokenText(cleaned)))
        {
            cleaned = preNorm;
        }

        var variants = new List<string>(3);
        string full = NormalizePreNormalizedTokenText(cleaned);
        if (!string.IsNullOrEmpty(full)) variants.Add(full);

        int firstColon = cleaned.IndexOfAny([':', '：']);
        if (firstColon > 0 && firstColon + 1 < cleaned.Length)
        {
            string afterFirst = NormalizePreNormalizedTokenText(cleaned[(firstColon + 1)..]);
            if (!string.IsNullOrEmpty(afterFirst) && !variants.Contains(afterFirst, StringComparer.Ordinal))
            {
                variants.Add(afterFirst);
            }

            int lastColon = cleaned.LastIndexOfAny([':', '：']);
            if (lastColon > firstColon && lastColon + 1 < cleaned.Length)
            {
                string afterLast = NormalizePreNormalizedTokenText(cleaned[(lastColon + 1)..]);
                if (!string.IsNullOrEmpty(afterLast) && !variants.Contains(afterLast, StringComparer.Ordinal))
                {
                    variants.Add(afterLast);
                }
            }
        }

        return variants;
    }

    public static IReadOnlyList<PlaylistEntry> ResolveSeeds(
        IReadOnlyList<ImportedTrackSeed> seeds,
        IReadOnlyList<LocalLibraryTrackRef> library,
        string? musicRoot = null) =>
        ResolveSeeds(seeds, BuildIndex(library), musicRoot);

    public static IReadOnlyList<PlaylistEntry> ResolveSeeds(
        IReadOnlyList<ImportedTrackSeed> seeds,
        LibraryIndex index,
        string? musicRoot = null)
    {
        string? normRoot = !string.IsNullOrWhiteSpace(musicRoot)
            ? musicRoot.Replace('\\', '/').TrimEnd('/') + "/"
            : null;

        var entries = new List<PlaylistEntry>(seeds.Count);
        foreach (var seed in seeds)
        {
            LocalLibraryTrackRef? matched = ResolveSingleSeedByPath(seed.FilePath, index, normRoot);

            if (matched is null && !string.IsNullOrWhiteSpace(seed.Title))
            {
                matched = ResolveSingleSeedByMetadata(seed, index);
            }

            if (matched is not null)
            {
                entries.Add(new PlaylistEntry(
                    RelPath: matched.RelPath,
                    Title: matched.Title,
                    Artist: matched.ArtistsDisplay,
                    Album: matched.Album,
                    DurationMs: matched.DurationMs,
                    IsLocalMatched: true,
                    MatchedTrackUid: matched.TrackUid
                ));
            }
            else
            {
                entries.Add(new PlaylistEntry(
                    RelPath: null,
                    Title: seed.Title,
                    Artist: seed.Artist,
                    Album: seed.Album,
                    DurationMs: 0,
                    IsLocalMatched: false,
                    MatchedTrackUid: null
                ));
            }
        }

        return entries;
    }

    private static LocalLibraryTrackRef? ResolveSingleSeedByPath(
        string? rawFilePath,
        LibraryIndex index,
        string? normRoot)
    {
        if (string.IsNullOrWhiteSpace(rawFilePath)) return null;

        string rawPath = rawFilePath.Trim().Trim('"').Replace('\\', '/');
        if (index.ByFullPath.TryGetValue(rawPath, out var exactFull))
        {
            return exactFull;
        }

        string candidateRel = rawPath;
        if (normRoot is not null && rawPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
        {
            candidateRel = rawPath[normRoot.Length..];
        }
        else if (candidateRel.StartsWith("../Tracks/", StringComparison.OrdinalIgnoreCase))
        {
            candidateRel = candidateRel["../Tracks/".Length..];
        }

        candidateRel = NormalizePathKey(candidateRel);
        if (index.ByRelPath.TryGetValue(candidateRel, out var exactRel))
        {
            return exactRel;
        }

        string fn = Path.GetFileName(rawPath);
        if (!string.IsNullOrEmpty(fn)
            && index.ByFileName.TryGetValue(fn, out var fnMatches)
            && fnMatches.Count == 1)
        {
            return fnMatches[0];
        }

        return null;
    }

    private static LocalLibraryTrackRef? ResolveSingleSeedByMetadata(
        ImportedTrackSeed seed,
        LibraryIndex index)
    {
        var seedVariants = NormalizeClassicalTitleVariants(seed.Title);
        string seedTitleFull = seedVariants.Count > 0 ? seedVariants[0] : NormalizeTitle(seed.Title);
        if (seedVariants.Count == 0 && string.IsNullOrEmpty(seedTitleFull)) return null;

        string seedPreNorm = PreNormalizeUnicode(seed.Title);
        VersionQualifiers seedQualifiers = ExtractVersionQualifiersFromPreNorm(seedPreNorm);
        VersionQualifiers seedHardQualifiers = seedQualifiers & HardBlockQualifierMask;
        bool seedIsLive = (seedQualifiers & VersionQualifiers.Live) != 0;
        StructuralMarkers seedMarkers = ExtractStructuralMarkers(seedTitleFull);

        HashSet<string> seedArtistTokens = ExtractArtistTokens(seed.Artist);
        bool artistMissingOrPlaceholder = IsPlaceholderOnlyArtist(seedArtistTokens);
        string seedArtistFlat = artistMissingOrPlaceholder ? "" : NormalizeTokenText(seed.Artist);
        string seedAlbumNorm = NormalizeAlbum(seed.Album);
        int seedDurationMs = seed.DurationMs;

        // Stage 2A: Exact normalized & compact title lookup
        var stage2ACandidates = CollectStage2ACandidates(
            seedVariants,
            seedHardQualifiers,
            seedMarkers,
            index);

        if (stage2ACandidates.Count > 0)
        {
            if (artistMissingOrPlaceholder)
            {
                return SelectUncreditedStage2ACandidate(
                    stage2ACandidates,
                    seedIsLive,
                    seedAlbumNorm,
                    seedDurationMs);
            }

            IndexedTrack? bestTrack = null;
            CandidateRank bestRank = default;
            bool hasBest = false;

            foreach (var (cand, titleScore) in stage2ACandidates)
            {
                int artistScore = ScoreArtistMatch(seedArtistTokens, seedArtistFlat, cand);
                if (artistScore <= 0) continue;

                var rank = ComputeCandidateRank(
                    titleScore,
                    artistScore,
                    seedIsLive,
                    seedAlbumNorm,
                    seedDurationMs,
                    cand);

                if (!hasBest || rank.CompareTo(bestRank) > 0)
                {
                    hasBest = true;
                    bestRank = rank;
                    bestTrack = cand;
                }
            }

            if (bestTrack is not null)
            {
                return bestTrack.Track;
            }

            if (stage2ACandidates.Count == 1)
            {
                var solo = stage2ACandidates[0].Track.Track;
                if (string.IsNullOrWhiteSpace(solo.ArtistsDisplay)
                    && string.IsNullOrWhiteSpace(solo.ComposersDisplay)
                    && string.IsNullOrWhiteSpace(solo.ConductorsDisplay))
                {
                    return solo;
                }
            }
        }

        // Stage 2B: Artist-anchored controlled fuzzy title recall
        if (artistMissingOrPlaceholder || string.IsNullOrEmpty(seedTitleFull))
        {
            return null;
        }

        IReadOnlyList<string> effectiveVariants = seedVariants.Count > 0 ? seedVariants : [seedTitleFull];
        return ResolveFuzzyArtistAnchored(
            effectiveVariants,
            seedMarkers,
            seedHardQualifiers,
            seedIsLive,
            seedArtistTokens,
            seedArtistFlat,
            seedAlbumNorm,
            seedDurationMs,
            index);
    }

    private static List<(IndexedTrack Track, int TitleScore)> CollectStage2ACandidates(
        IReadOnlyList<string> seedVariants,
        VersionQualifiers seedHardQualifiers,
        StructuralMarkers seedMarkers,
        LibraryIndex index)
    {
        var candidates = new List<(IndexedTrack Track, int TitleScore)>();
        var uidToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        void UpsertCandidate(IndexedTrack cand, int titleScore)
        {
            if (uidToIndex.TryGetValue(cand.Track.TrackUid, out int existingIdx))
            {
                if (titleScore > candidates[existingIdx].TitleScore)
                {
                    candidates[existingIdx] = (cand, titleScore);
                }
                return;
            }
            uidToIndex[cand.Track.TrackUid] = candidates.Count;
            candidates.Add((cand, titleScore));
        }

        for (int vi = 0; vi < seedVariants.Count; vi++)
        {
            if (index.ByTitle.TryGetValue(seedVariants[vi], out var found))
            {
                foreach (var (cand, candTier) in found)
                {
                    if (cand.HardQualifiers != seedHardQualifiers) continue;
                    if ((vi > 0 || candTier < 990) && !AreStructurallyCompatible(seedMarkers, cand.WorkAndTitleMarkers))
                    {
                        continue;
                    }

                    int effectiveScore = (vi, candTier) switch
                    {
                        (0, >= 1000) => 1000,
                        (<= 1, >= 1000) => 995,
                        (<= 1, >= 990) => 990,
                        _ => 960,
                    };
                    UpsertCandidate(cand, effectiveScore);
                }
            }

            string compactKey = CompactText(seedVariants[vi]);
            if (!string.IsNullOrEmpty(compactKey)
                && index.ByCompactTitle.TryGetValue(compactKey, out var compactHits))
            {
                foreach (var (cand, candTier) in compactHits)
                {
                    if (cand.HardQualifiers != seedHardQualifiers) continue;
                    if ((vi > 0 || candTier < 990) && !AreStructurallyCompatible(seedMarkers, cand.WorkAndTitleMarkers))
                    {
                        continue;
                    }

                    int effectiveScore = vi == 0 && candTier >= 990 ? 980 : 955;
                    UpsertCandidate(cand, effectiveScore);
                }
            }
        }

        return candidates;
    }

    private static LocalLibraryTrackRef? SelectUncreditedStage2ACandidate(
        List<(IndexedTrack Track, int TitleScore)> stage2ACandidates,
        bool seedIsLive,
        string seedAlbumNorm,
        int seedDurationMs)
    {
        if (stage2ACandidates.Count == 1)
        {
            return stage2ACandidates[0].Track.Track;
        }

        var rankedList = new List<(IndexedTrack Cand, CandidateRank Rank)>(stage2ACandidates.Count);
        IndexedTrack? bestCand = null;
        CandidateRank bestRank = default;

        foreach (var (cand, titleScore) in stage2ACandidates)
        {
            var rank = ComputeCandidateRank(
                titleScore,
                artistScore: 0,
                seedIsLive,
                seedAlbumNorm,
                seedDurationMs,
                cand);
            rankedList.Add((cand, rank));

            if (bestCand is null || rank.CompareTo(bestRank) > 0)
            {
                bestCand = cand;
                bestRank = rank;
            }
        }

        if (bestCand is null) return null;

        bool allSameArtist = bestCand.PrimaryArtistKey.Length > 0
            && stage2ACandidates.All(x => string.Equals(x.Track.PrimaryArtistKey, bestCand.PrimaryArtistKey, StringComparison.Ordinal));
        if (allSameArtist)
        {
            return bestCand.Track;
        }

        bool uniqueByAlbum = bestRank.AlbumScore > 0;
        bool uniqueByDuration = bestRank.DurationScore >= 2;

        foreach (var (cand, rank) in rankedList)
        {
            if (ReferenceEquals(cand, bestCand)) continue;
            bool sameArtist = bestCand.PrimaryArtistKey.Length > 0
                && string.Equals(cand.PrimaryArtistKey, bestCand.PrimaryArtistKey, StringComparison.Ordinal);
            if (sameArtist) continue;

            if (rank.AlbumScore >= bestRank.AlbumScore) uniqueByAlbum = false;
            if (rank.DurationScore >= bestRank.DurationScore) uniqueByDuration = false;
        }

        return (uniqueByAlbum || uniqueByDuration) ? bestCand.Track : null;
    }

    private static LocalLibraryTrackRef? ResolveFuzzyArtistAnchored(
        IReadOnlyList<string> seedVariants,
        StructuralMarkers seedMarkers,
        VersionQualifiers seedHardQualifiers,
        bool seedIsLive,
        HashSet<string> seedArtistTokens,
        string seedArtistFlat,
        string seedAlbumNorm,
        int seedDurationMs,
        LibraryIndex index)
    {
        const int maxFuzzyPool = 2000;
        var seenUids = new HashSet<string>(StringComparer.Ordinal);
        var exactPool = new List<IndexedTrack>();

        void AddListToPool(List<IndexedTrack> targetPool, List<IndexedTrack> source)
        {
            foreach (var cand in source)
            {
                if (targetPool.Count >= maxFuzzyPool) break;
                if (seenUids.Add(cand.Track.TrackUid))
                {
                    targetPool.Add(cand);
                }
            }
        }

        foreach (string st in seedArtistTokens)
        {
            if (string.IsNullOrEmpty(st) || PlaceholderArtists.Contains(st)) continue;
            if (index.ByArtistToken.TryGetValue(st, out var hits))
            {
                AddListToPool(exactPool, hits);
            }
        }

        foreach (string word in ExtractSignificantCreditWords(seedArtistTokens))
        {
            if (exactPool.Count >= maxFuzzyPool) break;
            if (index.ByArtistWord.TryGetValue(word, out var hits))
            {
                AddListToPool(exactPool, hits);
            }
        }

        IndexedTrack? EvaluatePool(List<IndexedTrack> pool)
        {
            IndexedTrack? winner = null;
            CandidateRank winnerRank = default;

            foreach (var cand in pool)
            {
                if (cand.HardQualifiers != seedHardQualifiers) continue;

                int artistScore = ScoreArtistMatch(seedArtistTokens, seedArtistFlat, cand);
                if (artistScore <= 0) continue;

                double fuzzySim = ComputeFuzzyTitleSimilarity(seedVariants, seedMarkers, cand);
                if (fuzzySim <= 0.0) continue;

                int fuzzyTitleScore = (int)Math.Round(fuzzySim * 900.0);
                var rank = ComputeCandidateRank(
                    fuzzyTitleScore,
                    artistScore,
                    seedIsLive,
                    seedAlbumNorm,
                    seedDurationMs,
                    cand);

                if (winner is null || rank.CompareTo(winnerRank) > 0)
                {
                    winner = cand;
                    winnerRank = rank;
                }
            }

            return winner;
        }

        if (exactPool.Count > 0)
        {
            var hit = EvaluatePool(exactPool);
            if (hit is not null) return hit.Track;
        }

        // Bounded fuzzy-artist fallback for transliteration differences (e.g. Rachmaninoff vs Rachmaninov)
        var longLatinTokens = seedArtistTokens
            .Where(t => t.Length >= 5 && !ContainsEastAsianScript(t) && !PlaceholderArtists.Contains(t))
            .ToList();
        var longLatinWords = ExtractSignificantCreditWords(seedArtistTokens)
            .Where(w => w.Length >= 5 && !ContainsEastAsianScript(w))
            .ToList();

        if (longLatinTokens.Count == 0 && longLatinWords.Count == 0)
        {
            return null;
        }

        var fuzzyArtistPool = new List<IndexedTrack>();
        int remainingBudget = Math.Max(0, maxFuzzyPool - exactPool.Count);
        if (remainingBudget == 0) return null;

        static IReadOnlyList<IndexedTrack> QueryFuzzyEntriesCached(
            IReadOnlyList<FuzzyArtistKeyEntry> entries,
            ConcurrentDictionary<string, IReadOnlyList<IndexedTrack>> cache,
            string query)
        {
            return cache.GetOrAdd(query, static (q, stateEntries) =>
            {
                bool qHasSpace = q.Contains(' ', StringComparison.Ordinal);
                uint qMask = ComputeAsciiLetterMask(q);
                string[] qLongWords = [];
                uint[] qLongWordMasks = [];
                string qSurname = "";
                uint qSurnameMask = 0;

                if (qHasSpace)
                {
                    string[] rawWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var longWordList = new List<string>(rawWords.Length);
                    for (int i = 0; i < rawWords.Length; i++)
                    {
                        if (rawWords[i].Length >= 5) longWordList.Add(rawWords[i]);
                    }
                    qLongWords = longWordList.Count > 0 ? [.. longWordList] : [];
                    qLongWordMasks = qLongWords.Length > 0
                        ? Array.ConvertAll(qLongWords, static w => ComputeAsciiLetterMask(w))
                        : [];

                    int idx = q.LastIndexOf(' ');
                    string sur = q[(idx + 1)..];
                    if (sur.Length >= 7)
                    {
                        qSurname = sur;
                        qSurnameMask = ComputeAsciiLetterMask(sur);
                    }
                }

                List<IndexedTrack>? matched = null;
                HashSet<string>? perQuerySeen = null;
                for (int i = 0; i < stateEntries.Count; i++)
                {
                    FuzzyArtistKeyEntry entry = stateEntries[i];
                    if (!IsFuzzyArtistQueryMatchEntry(
                        q, qHasSpace, qMask, qLongWords, qLongWordMasks, qSurname, qSurnameMask, in entry))
                    {
                        continue;
                    }

                    matched ??= [];
                    perQuerySeen ??= new HashSet<string>(StringComparer.Ordinal);
                    foreach (var cand in entry.Tracks)
                    {
                        if (perQuerySeen.Add(cand.Track.TrackUid))
                        {
                            matched.Add(cand);
                        }
                    }
                }

                return matched is null ? Array.Empty<IndexedTrack>() : matched;
            }, entries);
        }

        void CollectFuzzyKeysCached(
            IReadOnlyList<FuzzyArtistKeyEntry> entries,
            ConcurrentDictionary<string, IReadOnlyList<IndexedTrack>> cache,
            IReadOnlyList<string> queries)
        {
            if (fuzzyArtistPool.Count >= remainingBudget || queries.Count == 0) return;
            foreach (string q in queries)
            {
                if (fuzzyArtistPool.Count >= remainingBudget) break;
                var hits = QueryFuzzyEntriesCached(entries, cache, q);
                for (int i = 0; i < hits.Count; i++)
                {
                    if (fuzzyArtistPool.Count >= remainingBudget) break;
                    var cand = hits[i];
                    if (seenUids.Add(cand.Track.TrackUid))
                    {
                        fuzzyArtistPool.Add(cand);
                    }
                }
            }
        }

        CollectFuzzyKeysCached(index.LongLatinArtistTokenEntries, index.FuzzyTokenQueryCache, longLatinTokens);
        CollectFuzzyKeysCached(index.LongLatinArtistWordEntries, index.FuzzyWordQueryCache, longLatinWords);

        return fuzzyArtistPool.Count > 0 ? EvaluatePool(fuzzyArtistPool)?.Track : null;
    }

    public static string NormalizeTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string preNorm = PreNormalizeUnicode(raw);
        if (string.IsNullOrEmpty(preNorm)) return "";
        string stripped = StripEditionDecorations(preNorm);
        string norm = NormalizePreNormalizedTokenText(stripped);
        return string.IsNullOrEmpty(norm) ? NormalizePreNormalizedTokenText(preNorm) : norm;
    }

    public static HashSet<string> ExtractArtistTokens(string? raw)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return tokens;

        string working = PreNormalizeUnicode(raw);
        if (string.IsNullOrEmpty(working)) return tokens;

        List<(string Marker, string Original)>? replacements = null;
        var whitelist = NormalizedWhitelist.Value;
        int seq = 0;

        for (int i = 0; i < whitelist.Count; i++)
        {
            string protectedName = whitelist[i];
            if (!working.Contains(protectedName, StringComparison.Ordinal)) continue;

            int searchStart = 0;
            while (searchStart < working.Length)
            {
                int idx = working.IndexOf(protectedName, searchStart, StringComparison.Ordinal);
                if (idx < 0) break;

                bool leftBoundary = idx == 0 || !char.IsLetterOrDigit(working[idx - 1]);
                int end = idx + protectedName.Length;
                bool rightBoundary = end == working.Length || !char.IsLetterOrDigit(working[end]);
                if (!leftBoundary || !rightBoundary)
                {
                    searchStart = idx + 1;
                    continue;
                }

                replacements ??= [];
                string marker = $"__twprot{seq++}__";
                working = string.Concat(working.AsSpan(0, idx), marker, working.AsSpan(end));
                replacements.Add((marker, protectedName));
                searchStart = idx + marker.Length;
            }
        }

        string[] parts = ArtistSplitRegex().Split(working);
        foreach (string part in parts)
        {
            string restored = part;
            if (replacements is not null)
            {
                foreach (var (marker, original) in replacements)
                {
                    if (restored.Contains(marker, StringComparison.Ordinal))
                    {
                        restored = restored.Replace(marker, original, StringComparison.Ordinal);
                    }
                }
            }

            string clean = NormalizePreNormalizedTokenText(restored);
            if (!string.IsNullOrEmpty(clean))
            {
                tokens.Add(clean);
            }
        }

        return tokens;
    }

    private static string NormalizeTokenText(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : NormalizePreNormalizedTokenText(PreNormalizeUnicode(raw));

    private static string NormalizePathKey(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').TrimStart('/');

    [GeneratedRegex("""[\(\[【]\s*(?:feat\.?|ft\.?|featuring|with|from\s+[^\)\]】]+)(?:\s+[^\)\]】]*)?[\)\]】]""", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingParenRegex();

    [GeneratedRegex("""[\(\[【]\s*(?!.*?\b(?:remix(?:es|ed)?|rework|bootleg|instrumental|off\s+vocal|minus\s+one|karaoke|ktv|acoustic|unplugged|a\s*capp?ell?a|demo|cover|mashup|medley|sped\s+up|speed\s+up|slowed|nightcore)\b)(?!.*?(?:伴奏|纯音乐|消音|无人声|乐器版|卡拉ok|翻唱|不插电|木吉他版|清唱|阿卡贝拉|小样|混音|变速|加速版|降调))[^\)\]】]*(?:remaster(?:ed)?|version|radio\s+edit|album\s+version|live|deluxe|mono|stereo|重制|重置|完整版|正式版|专辑版|单曲版|原版|现场|演唱会|音乐会|母带|纯净版|无损|主题曲|插曲|片尾曲|片头曲|推广曲|影视原声)\s*[^\)\]】]*[\)\]】]""", RegexOptions.IgnoreCase)]
    private static partial Regex RemasterSuffixRegex();

    [GeneratedRegex("""(?:\s+[-–—]\s+(?!.*?\b(?:remix(?:es|ed)?|rework|bootleg|instrumental|off\s+vocal|minus\s+one|karaoke|ktv|acoustic|unplugged|a\s*capp?ell?a|demo|cover|mashup|medley|sped\s+up|speed\s+up|slowed|nightcore)\b)(?!.*?(?:伴奏|纯音乐|消音|无人声|乐器版|卡拉ok|翻唱|不插电|木吉他版|清唱|阿卡贝拉|小样|混音|变速|加速版|降调))(?:(?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?(?:\s+(?:version|edition))?|radio\s+edit|album\s+version|single\s+version|single|original\s+mix|live(?:\s+[^\n]*)?|mono|stereo|from\s+[^\n]*|[^\n]*\bsoundtrack\b[^\n]*|(?:\d{4}\s*)?(?:重制版?|重置版?)(?:\s*\d{4})?|完整版|正式版|专辑版|单曲版|原版|(?:\d{4}\s*)?(?:现场版?|live版)|[^\n]*(?:演唱会|音乐会)[^\n]*现场[^\n]*|母带版|纯净版|无损版?|电影[^\n]*(?:主题曲|插曲|片尾曲|片头曲|推广曲)|电视剧[^\n]*(?:主题曲|插曲|片尾曲|片头曲)|影视原声))+$""", RegexOptions.IgnoreCase)]
    private static partial Regex DashEditionSuffixRegex();

    [GeneratedRegex("""\s+-\s+single\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex SingleSuffixRegex();

    [GeneratedRegex("""(?:\s*[,&/;+、，；／＋]\s*|\s+(?:feat\.?|ft\.?|featuring|with|x|and|vs\.?)\s+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplitRegex();
}

public static class PlaylistStore
{
    public static string GetPlaylistsDirectory(string? musicRoot) =>
        !string.IsNullOrWhiteSpace(musicRoot)
            ? Path.Combine(musicRoot, "Playlists")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tonewise", "Playlists");

    public static IReadOnlyList<PlaylistSummary> ListPlaylists(
        string? musicRoot,
        IReadOnlyList<LocalLibraryTrackRef> library) =>
        ListPlaylists(musicRoot, PlaylistTrackMatcher.BuildIndex(library));

    public static IReadOnlyList<PlaylistSummary> ListPlaylists(
        string? musicRoot,
        PlaylistTrackMatcher.LibraryIndex index)
    {
        string dir = GetPlaylistsDirectory(musicRoot);
        if (!Directory.Exists(dir)) return [];

        var files = Directory.EnumerateFiles(dir, "*.m3u8")
            .Concat(Directory.EnumerateFiles(dir, "*.m3u"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summaries = new List<PlaylistSummary>(files.Count);
        foreach (string file in files)
        {
            try
            {
                var doc = LoadPlaylist(file, index, musicRoot);
                int matched = doc.Entries.Count(e => e.IsLocalMatched);
                summaries.Add(new PlaylistSummary(file, doc.Name, file, doc.Entries.Count, matched));
            }
            catch
            {
                // Skip unreadable/corrupt playlist file
            }
        }
        return summaries.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static PlaylistDocument LoadPlaylist(
        string filePath,
        IReadOnlyList<LocalLibraryTrackRef> library,
        string? musicRoot = null) =>
        LoadPlaylist(filePath, PlaylistTrackMatcher.BuildIndex(library), musicRoot);

    public static PlaylistDocument LoadPlaylist(
        string filePath,
        PlaylistTrackMatcher.LibraryIndex index,
        string? musicRoot = null)
    {
        string text = File.ReadAllText(filePath, Encoding.UTF8);
        string fallbackName = Path.GetFileNameWithoutExtension(filePath);
        var parsed = PlaylistImporter.ParseTextPlaylist(text, fallbackName, maxTracks: 50_000);
        var resolved = PlaylistTrackMatcher.ResolveSeeds(parsed.Tracks, index, musicRoot);
        return new PlaylistDocument(parsed.Title, filePath, resolved);
    }

    public static string SavePlaylist(
        string? musicRoot,
        string name,
        IReadOnlyList<PlaylistEntry> entries,
        string? previousFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Playlist name is required.", nameof(name));

        string cleanName = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        string dir = GetPlaylistsDirectory(musicRoot);
        Directory.CreateDirectory(dir);

        string fileName = SanitizePlaylistFileName(cleanName) + ".m3u8";
        string outPath = Path.Combine(dir, fileName);

        WritePlaylistAtomic(outPath, cleanName, entries);

        if (!string.IsNullOrWhiteSpace(previousFilePath)
            && !string.Equals(Path.GetFullPath(previousFilePath), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase)
            && File.Exists(previousFilePath))
        {
            File.Delete(previousFilePath);
        }

        return outPath;
    }

    public static PlaylistRematchResult RematchPlaylist(
        string filePath,
        IReadOnlyList<LocalLibraryTrackRef> library,
        string? musicRoot = null,
        bool persistIfChanged = true) =>
        RematchPlaylist(filePath, PlaylistTrackMatcher.BuildIndex(library), musicRoot, persistIfChanged);

    public static PlaylistRematchResult RematchPlaylist(
        string filePath,
        PlaylistTrackMatcher.LibraryIndex index,
        string? musicRoot = null,
        bool persistIfChanged = true)
    {
        string text = File.ReadAllText(filePath, Encoding.UTF8);
        string fallbackName = Path.GetFileNameWithoutExtension(filePath);
        var parsed = PlaylistImporter.ParseTextPlaylist(text, fallbackName, maxTracks: 50_000);
        var resolved = PlaylistTrackMatcher.ResolveSeeds(parsed.Tracks, index, musicRoot);

        int matchedCount = 0;
        int newlyMatchedCount = 0;
        int pathRepairedCount = 0;

        for (int i = 0; i < resolved.Count && i < parsed.Tracks.Count; i++)
        {
            var seed = parsed.Tracks[i];
            var entry = resolved[i];
            bool hadPersistedPath = !string.IsNullOrWhiteSpace(seed.FilePath);
            bool nowMatched = entry.IsLocalMatched && !string.IsNullOrWhiteSpace(entry.RelPath);
            if (!nowMatched) continue;

            matchedCount++;
            if (!hadPersistedPath)
            {
                newlyMatchedCount++;
            }
            else
            {
                string persistedClean = seed.FilePath!.Trim().Trim('"').Replace('\\', '/');
                string canonicalRel = entry.RelPath!.Replace('\\', '/').TrimStart('/');
                if (!string.Equals(persistedClean, canonicalRel, StringComparison.OrdinalIgnoreCase))
                {
                    pathRepairedCount++;
                }
            }
        }

        int previousMatchedCount = matchedCount - newlyMatchedCount;
        bool needsSave = newlyMatchedCount > 0 || pathRepairedCount > 0;
        bool wasSaved = false;

        if (persistIfChanged && needsSave)
        {
            string cleanName = parsed.Title.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                cleanName = string.IsNullOrWhiteSpace(fallbackName) ? "Untitled" : fallbackName;
            }
            WritePlaylistAtomic(filePath, cleanName, resolved);
            wasSaved = true;
        }

        return new PlaylistRematchResult(
            Name: parsed.Title,
            FilePath: filePath,
            TotalCount: resolved.Count,
            PreviousMatchedCount: previousMatchedCount,
            MatchedCount: matchedCount,
            NewlyMatchedCount: newlyMatchedCount,
            PathRepairedCount: pathRepairedCount,
            WasSaved: wasSaved
        );
    }

    public static PlaylistBatchRematchSummary RematchAllPlaylists(
        string? musicRoot,
        IReadOnlyList<LocalLibraryTrackRef> library,
        bool persistIfChanged = true) =>
        RematchAllPlaylists(musicRoot, PlaylistTrackMatcher.BuildIndex(library), persistIfChanged);

    public static PlaylistBatchRematchSummary RematchAllPlaylists(
        string? musicRoot,
        PlaylistTrackMatcher.LibraryIndex index,
        bool persistIfChanged = true)
    {
        string dir = GetPlaylistsDirectory(musicRoot);
        if (!Directory.Exists(dir))
        {
            return new PlaylistBatchRematchSummary(0, 0, 0, 0, 0, 0);
        }

        var files = Directory.EnumerateFiles(dir, "*.m3u8")
            .Concat(Directory.EnumerateFiles(dir, "*.m3u"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int scanned = 0;
        int updated = 0;
        int totalTracks = 0;
        int totalMatched = 0;
        int newlyMatched = 0;
        int pathRepaired = 0;

        foreach (string file in files)
        {
            try
            {
                var res = RematchPlaylist(file, index, musicRoot, persistIfChanged);
                scanned++;
                if (res.WasSaved) updated++;
                totalTracks += res.TotalCount;
                totalMatched += res.MatchedCount;
                newlyMatched += res.NewlyMatchedCount;
                pathRepaired += res.PathRepairedCount;
            }
            catch
            {
                // Skip unreadable/corrupt playlist file
            }
        }

        return new PlaylistBatchRematchSummary(
            PlaylistsScanned: scanned,
            PlaylistsUpdated: updated,
            TotalTracks: totalTracks,
            TotalMatched: totalMatched,
            NewlyMatchedCount: newlyMatched,
            PathRepairedCount: pathRepaired
        );
    }

    private static void WritePlaylistAtomic(
        string outPath,
        string cleanName,
        IReadOnlyList<PlaylistEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#PLAYLIST:").Append(cleanName).Append('\n');

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.RelPath))
            {
                int sec = Math.Max(0, entry.DurationMs / 1000);
                sb.Append("#EXTINF:").Append(sec).Append(',');
                if (!string.IsNullOrWhiteSpace(entry.Artist))
                {
                    sb.Append(SanitizeInline(entry.Artist)).Append(" - ");
                }
                sb.Append(SanitizeInline(entry.Title)).Append('\n');
                sb.Append(entry.RelPath.Replace('\\', '/').TrimStart('/')).Append('\n');
            }
            else
            {
                sb.Append("#EXTINF:0,");
                if (!string.IsNullOrWhiteSpace(entry.Artist))
                {
                    sb.Append(SanitizeInline(entry.Artist)).Append(" - ");
                }
                sb.Append(SanitizeInline(entry.Title)).Append('\n');
                sb.Append("#EXTTW-UNMATCHED:")
                  .Append(SanitizeTab(entry.Artist)).Append('\t')
                  .Append(SanitizeTab(entry.Title)).Append('\t')
                  .Append(SanitizeTab(entry.Album ?? "")).Append('\n');
            }
        }

        string tmpPath = outPath + ".twtmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmpPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmpPath, outPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            catch
            {
                // Ignore temp cleanup failure
            }
        }
    }

    public static void DeletePlaylist(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    public static string SanitizePlaylistFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Untitled";
        string display = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(display)) return "Untitled";

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sb = new StringBuilder(display.Length);
        bool prevSpace = false;
        foreach (char c in display)
        {
            if (c == '\t' || c == ' ')
            {
                if (!prevSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else if (invalid.Contains(c) || c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                sb.Append('_');
                prevSpace = false;
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }

        string stem = sb.ToString().Trim().TrimEnd('.').Trim();
        return string.IsNullOrWhiteSpace(stem) || stem is "." or ".." ? "Untitled" : stem;
    }

    private static string SanitizeTab(string raw) =>
        raw.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string SanitizeInline(string raw) =>
        raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
