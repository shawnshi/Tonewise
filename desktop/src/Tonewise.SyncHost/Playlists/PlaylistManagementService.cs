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
    TextFile,
}

public sealed record ImportedTrackSeed(
    string Title,
    string Artist,
    string? Album = null,
    string? FilePath = null
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

public sealed record LocalLibraryTrackRef(
    string TrackUid,
    string RelPath,
    string FullPath,
    string Title,
    string ArtistsDisplay,
    string Album,
    int DurationMs,
    int SampleRateHz
);

public static partial class PlaylistImporter
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".wav", ".m4a", ".alac", ".aac", ".ogg", ".opus", ".mp4", ".aiff", ".dsf", ".dff"
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
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Please provide a valid http/https playlist URL.", nameof(url));
        }

        var client = httpClient ?? SharedClient.Value;
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        string fallbackTitle = DeriveTitleFromUri(uri);
        string host = uri.Host;

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

        return ParseTextPlaylist(html, fallbackTitle, maxTracks);
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

    public static ParsedExternalPlaylist ParseTextPlaylist(
        string content,
        string? fallbackTitle = null,
        int maxTracks = 5000)
    {
        string title = string.IsNullOrWhiteSpace(fallbackTitle) ? "Imported Playlist" : fallbackTitle.Trim();
        var seeds = new List<ImportedTrackSeed>();

        string? pendingArtist = null;
        string? pendingTitle = null;

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
                if (comma >= 0 && comma + 1 < line.Length)
                {
                    string display = line[(comma + 1)..].Trim();
                    var (art, ttl) = SplitArtistTitle(display);
                    pendingArtist = art;
                    pendingTitle = ttl;
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
                    seeds.Add(new ImportedTrackSeed(ttl, art, alb));
                }
                pendingArtist = null;
                pendingTitle = null;
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
                    FilePath: line
                ));
                pendingArtist = null;
                pendingTitle = null;
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
                seeds.Add(new ImportedTrackSeed(parsedTitle, parsedArtist));
            }
            pendingArtist = null;
            pendingTitle = null;
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
                    seeds.Add(new ImportedTrackSeed(title!, artist!, album));
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
        if (trimmed.Contains('/') || trimmed.Contains('\\')) return true;
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
    public static IReadOnlyList<PlaylistEntry> ResolveSeeds(
        IReadOnlyList<ImportedTrackSeed> seeds,
        IReadOnlyList<LocalLibraryTrackRef> library,
        string? musicRoot = null)
    {
        var byRelPath = new Dictionary<string, LocalLibraryTrackRef>(StringComparer.OrdinalIgnoreCase);
        var byFullPath = new Dictionary<string, LocalLibraryTrackRef>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, List<LocalLibraryTrackRef>>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, List<LocalLibraryTrackRef>>(StringComparer.Ordinal);
        var candidateArtistTokens = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var item in library)
        {
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

            string normTitle = NormalizeTitle(item.Title);
            if (!string.IsNullOrEmpty(normTitle))
            {
                if (!byTitle.TryGetValue(normTitle, out var list))
                {
                    list = [];
                    byTitle[normTitle] = list;
                }
                list.Add(item);
            }

            candidateArtistTokens[item.TrackUid] = ExtractArtistTokens(item.ArtistsDisplay);
        }

        string? normRoot = !string.IsNullOrWhiteSpace(musicRoot)
            ? musicRoot.Replace('\\', '/').TrimEnd('/') + "/"
            : null;

        var entries = new List<PlaylistEntry>(seeds.Count);
        foreach (var seed in seeds)
        {
            LocalLibraryTrackRef? matched = null;

            if (!string.IsNullOrWhiteSpace(seed.FilePath))
            {
                string rawPath = seed.FilePath.Trim().Trim('"').Replace('\\', '/');
                if (byFullPath.TryGetValue(rawPath, out var exactFull))
                {
                    matched = exactFull;
                }
                else
                {
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
                    if (byRelPath.TryGetValue(candidateRel, out var exactRel))
                    {
                        matched = exactRel;
                    }
                    else
                    {
                        string fn = Path.GetFileName(rawPath);
                        if (!string.IsNullOrEmpty(fn) && byFileName.TryGetValue(fn, out var fnMatches) && fnMatches.Count == 1)
                        {
                            matched = fnMatches[0];
                        }
                    }
                }
            }

            if (matched is null && !string.IsNullOrWhiteSpace(seed.Title))
            {
                string normTitle = NormalizeTitle(seed.Title);
                if (!string.IsNullOrEmpty(normTitle) && byTitle.TryGetValue(normTitle, out var candidates))
                {
                    if (string.IsNullOrWhiteSpace(seed.Artist))
                    {
                        matched = candidates[0];
                    }
                    else
                    {
                        var seedTokens = ExtractArtistTokens(seed.Artist);
                        string seedArtistFlat = NormalizeTokenText(seed.Artist);
                        int bestScore = 0;
                        LocalLibraryTrackRef? best = null;

                        foreach (var cand in candidates)
                        {
                            var candTokens = candidateArtistTokens[cand.TrackUid];
                            int overlap = 0;
                            foreach (string st in seedTokens)
                            {
                                if (candTokens.Contains(st)) overlap += 2;
                            }

                            if (overlap == 0 && !string.IsNullOrEmpty(seedArtistFlat))
                            {
                                string candFlat = NormalizeTokenText(cand.ArtistsDisplay);
                                if (!string.IsNullOrEmpty(candFlat)
                                    && (candFlat.Contains(seedArtistFlat, StringComparison.Ordinal)
                                        || seedArtistFlat.Contains(candFlat, StringComparison.Ordinal)))
                                {
                                    overlap = 1;
                                }
                            }

                            if (overlap > bestScore)
                            {
                                bestScore = overlap;
                                best = cand;
                            }
                        }

                        if (best is not null)
                        {
                            matched = best;
                        }
                        else if (candidates.Count == 1 && string.IsNullOrWhiteSpace(candidates[0].ArtistsDisplay))
                        {
                            matched = candidates[0];
                        }
                    }
                }
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

    public static string NormalizeTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.ToLowerInvariant().Trim();
        s = FeaturingParenRegex().Replace(s, " ");
        s = RemasterSuffixRegex().Replace(s, " ");
        s = SingleSuffixRegex().Replace(s, " ");
        return NormalizeTokenText(s);
    }

    public static HashSet<string> ExtractArtistTokens(string? raw)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return tokens;

        string normalized = raw.ToLowerInvariant();
        string[] parts = ArtistSplitRegex().Split(normalized);
        foreach (string part in parts)
        {
            string clean = NormalizeTokenText(part);
            if (!string.IsNullOrEmpty(clean))
            {
                tokens.Add(clean);
            }
        }
        return tokens;
    }

    private static string NormalizeTokenText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        bool prevSpace = true;
        foreach (char c in raw.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static string NormalizePathKey(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').TrimStart('/');

    [GeneratedRegex("""[\(\[]\s*(?:feat\.?|ft\.?|featuring|from\s+[^\)\]]+)\s*[^\)\]]*[\)\]]""", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingParenRegex();

    [GeneratedRegex("""[\(\[]\s*[^\)\]]*(?:remaster(?:ed)?|version|radio\s+edit|album\s+version|live|deluxe|mono|stereo)\s*[^\)\]]*[\)\]]""", RegexOptions.IgnoreCase)]
    private static partial Regex RemasterSuffixRegex();

    [GeneratedRegex("""\s+-\s+single\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex SingleSuffixRegex();

    [GeneratedRegex("""(?:\s*[,&/;]\s*|\s+(?:feat\.?|ft\.?|featuring|with|x|and)\s+)""", RegexOptions.IgnoreCase)]
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
        IReadOnlyList<LocalLibraryTrackRef> library)
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
                var doc = LoadPlaylist(file, library, musicRoot);
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
        string? musicRoot = null)
    {
        string text = File.ReadAllText(filePath, Encoding.UTF8);
        string fallbackName = Path.GetFileNameWithoutExtension(filePath);
        var parsed = PlaylistImporter.ParseTextPlaylist(text, fallbackName, maxTracks: 50_000);
        var resolved = PlaylistTrackMatcher.ResolveSeeds(parsed.Tracks, library, musicRoot);
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

        string fileName = SanitizeFileName(cleanName) + ".m3u8";
        string outPath = Path.Combine(dir, fileName);

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
                sb.Append(entry.RelPath.Replace('\\', '/')).Append('\n');
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

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!string.IsNullOrWhiteSpace(previousFilePath)
            && !string.Equals(Path.GetFullPath(previousFilePath), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase)
            && File.Exists(previousFilePath))
        {
            File.Delete(previousFilePath);
        }

        return outPath;
    }

    public static void DeletePlaylist(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static string SanitizeFileName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(raw.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }

    private static string SanitizeTab(string raw) =>
        raw.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string SanitizeInline(string raw) =>
        raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
