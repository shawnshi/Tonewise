using System.Text.Json;
using System.Text.RegularExpressions;
using Tonewise.Interop;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.SyncHost.Curation;

public sealed record BasicMetadataQuery(
    string TrackUid,
    string FullPath,
    string RelPath,
    string Title,
    string Artists,
    string Album,
    string Composers,
    string Conductors,
    string WorkName,
    string MovementName,
    string Genre,
    int? Year,
    string Publisher,
    int DurationMs,
    string AlbumDirectory,
    string? CoverArtPath
);

/// <summary>
/// Resolves missing basic track metadata (`Album`, `Artists`, `Composers`, `Work`, `Movement`,
/// `Genre`, `Year`, `Publisher`, `CoverArt`) using filename/folder heuristics, audio analysis hints,
/// iTunes Search API, MusicBrainz Recording Search, and optional Last.fm `track.getInfo`.
/// Strictly adheres to Fill-Missing Only law (never proposes values for non-empty fields).
/// </summary>
public sealed partial class BasicMetadataLookupService
{
    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private readonly HttpClient _http;
    private readonly CoverArtWaterfallService _coverService;

    public BasicMetadataLookupService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? SharedHttp;
        _coverService = new CoverArtWaterfallService(_http);
    }

    public async Task<(Dictionary<string, string?> FieldUpdates, bool SavedCover)> ResolveMissingMetadataAsync(
        BasicMetadataQuery q,
        string? lastFmApiKey = null,
        CancellationToken ct = default)
    {
        var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
        bool savedCover = false;

        // 1) Local path & filename heuristics (instant, offline-safe)
        var (pathArtist, pathAlbum, pathYear, pathTitle) = InferFromPath(q.RelPath, q.AlbumDirectory);

        string effectiveArtist = !string.IsNullOrWhiteSpace(q.Artists) ? q.Artists.Trim() : (pathArtist ?? "");
        string effectiveTitle = CleanTrackTitle(!string.IsNullOrWhiteSpace(q.Title) ? q.Title : (pathTitle ?? Path.GetFileNameWithoutExtension(q.RelPath)));
        string effectiveAlbum = !string.IsNullOrWhiteSpace(q.Album) ? q.Album.Trim() : (pathAlbum ?? "");
        int? effectiveYear = q.Year is > 1000 and <= 2100 ? q.Year : pathYear;
        string? inferredGenre = InferClassicalGenreFromKeywords(effectiveTitle, effectiveAlbum, q.RelPath);
        string? inferredWork = InferClassicalWorkFromTitle(effectiveTitle, effectiveAlbum);
        string? inferredComposer = InferClassicalComposerFromPath(effectiveArtist, effectiveTitle, q.RelPath);

        string? onlineArtist = null;
        string? onlineAlbum = null;
        int? onlineYear = null;
        string? onlineGenre = null;
        string? onlinePublisher = null;
        string? onlineCoverUrl = null;

        // 2) Online iTunes Search API (no API key required)
        if (NeedOnlineLookup(q, effectiveAlbum, effectiveYear, q.Genre, q.Publisher, q.CoverArtPath))
        {
            string term = string.IsNullOrWhiteSpace(effectiveArtist)
                ? effectiveTitle
                : $"{PrimaryToken(effectiveArtist)} {effectiveTitle}".Trim();

            if (!string.IsNullOrWhiteSpace(term))
            {
                (onlineArtist, onlineAlbum, onlineYear, onlineGenre, onlineCoverUrl) =
                    await QueryItunesTrackAsync(term, effectiveTitle, q.DurationMs, ct);
            }
        }

        // 3) Online MusicBrainz fallback for publisher / year / genre / album
        if (string.IsNullOrWhiteSpace(onlinePublisher)
            || (string.IsNullOrWhiteSpace(q.Genre) && string.IsNullOrWhiteSpace(onlineGenre))
            || (!effectiveYear.HasValue && !onlineYear.HasValue))
        {
            string queryTitle = effectiveTitle;
            string queryArtist = !string.IsNullOrWhiteSpace(effectiveArtist) ? PrimaryToken(effectiveArtist) : (onlineArtist ?? "");
            if (!string.IsNullOrWhiteSpace(queryTitle))
            {
                var mb = await QueryMusicBrainzRecordingAsync(queryArtist, queryTitle, q.DurationMs, ct);
                onlineArtist ??= mb.Artist;
                onlineAlbum ??= mb.Album;
                onlineYear ??= mb.Year;
                onlineGenre ??= mb.Genre;
                onlinePublisher ??= mb.Publisher;
            }
        }

        // 4) Optional Last.fm track.getInfo fallback for genre / album
        if (!string.IsNullOrWhiteSpace(lastFmApiKey)
            && !string.IsNullOrWhiteSpace(effectiveTitle)
            && (!string.IsNullOrWhiteSpace(effectiveArtist) || !string.IsNullOrWhiteSpace(onlineArtist))
            && (string.IsNullOrWhiteSpace(q.Genre) && string.IsNullOrWhiteSpace(onlineGenre)
                || string.IsNullOrWhiteSpace(effectiveAlbum) && string.IsNullOrWhiteSpace(onlineAlbum)))
        {
            string lfmArtist = !string.IsNullOrWhiteSpace(effectiveArtist) ? PrimaryToken(effectiveArtist) : onlineArtist!;
            var (lfmAlbum, lfmGenre) = await QueryLastFmTrackInfoAsync(lastFmApiKey, lfmArtist, effectiveTitle, ct);
            onlineAlbum ??= lfmAlbum;
            onlineGenre ??= lfmGenre;
        }

        // 5) Collect strictly Fill-Missing field updates
        if (string.IsNullOrWhiteSpace(q.Artists))
        {
            string? val = !string.IsNullOrWhiteSpace(onlineArtist) ? onlineArtist : pathArtist;
            if (!string.IsNullOrWhiteSpace(val)) updates["artists_json"] = val.Trim();
        }
        if (string.IsNullOrWhiteSpace(q.Album))
        {
            string? val = !string.IsNullOrWhiteSpace(onlineAlbum) ? onlineAlbum : pathAlbum;
            if (!string.IsNullOrWhiteSpace(val)) updates["album"] = val.Trim();
        }
        if (q.Year is null or <= 0)
        {
            int? y = onlineYear ?? pathYear;
            if (y is >= 1900 and <= 2100) updates["year"] = y.Value.ToString();
        }
        if (string.IsNullOrWhiteSpace(q.Genre))
        {
            string? g = !string.IsNullOrWhiteSpace(onlineGenre) ? onlineGenre : inferredGenre;
            if (!string.IsNullOrWhiteSpace(g)) updates["genre"] = g.Trim();
        }
        if (string.IsNullOrWhiteSpace(q.Publisher) && !string.IsNullOrWhiteSpace(onlinePublisher))
        {
            updates["publisher"] = onlinePublisher.Trim();
        }
        if (string.IsNullOrWhiteSpace(q.Composers) && !string.IsNullOrWhiteSpace(inferredComposer))
        {
            updates["composers_json"] = inferredComposer;
        }
        if (string.IsNullOrWhiteSpace(q.WorkName) && !string.IsNullOrWhiteSpace(inferredWork))
        {
            updates["work_name"] = inferredWork;
        }

        // 6) Save folder cover.jpg if missing and directory is writable
        if (string.IsNullOrWhiteSpace(q.CoverArtPath) && !string.IsNullOrWhiteSpace(q.AlbumDirectory) && Directory.Exists(q.AlbumDirectory))
        {
            string coverArtist = !string.IsNullOrWhiteSpace(q.Artists) ? q.Artists : (updates.GetValueOrDefault("artists_json") ?? "");
            string coverAlbum = !string.IsNullOrWhiteSpace(q.Album) ? q.Album : (updates.GetValueOrDefault("album") ?? "");
            if (!string.IsNullOrWhiteSpace(onlineCoverUrl))
            {
                try
                {
                    byte[] bytes = await _http.GetByteArrayAsync(onlineCoverUrl, ct);
                    var outcome = _coverService.SaveFolderCoverIfMissing(q.AlbumDirectory, bytes);
                    if (outcome.Saved) savedCover = true;
                }
                catch { /* Fall back to CoverArtWaterfallService */ }
            }
            if (!savedCover && (!string.IsNullOrWhiteSpace(coverArtist) || !string.IsNullOrWhiteSpace(coverAlbum)))
            {
                try
                {
                    var res = await _coverService.FetchBestCoverAsync(coverArtist, coverAlbum, null, lastFmApiKey, ct);
                    if (res.Success && res.ImageBytes is not null)
                    {
                        var outcome = _coverService.SaveFolderCoverIfMissing(q.AlbumDirectory, res.ImageBytes);
                        if (outcome.Saved) savedCover = true;
                    }
                }
                catch { /* Non-fatal */ }
            }
        }

        return (updates, savedCover);
    }

    private static bool NeedOnlineLookup(
        BasicMetadataQuery q,
        string effectiveAlbum,
        int? effectiveYear,
        string? genre,
        string? publisher,
        string? coverArtPath) =>
        string.IsNullOrWhiteSpace(q.Artists)
        || string.IsNullOrWhiteSpace(effectiveAlbum)
        || !effectiveYear.HasValue
        || string.IsNullOrWhiteSpace(genre)
        || string.IsNullOrWhiteSpace(publisher)
        || string.IsNullOrWhiteSpace(coverArtPath);

    private static (string? Artist, string? Album, int? Year, string? Title) InferFromPath(string relPath, string albumDirectory)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return (null, null, null, null);
        string[] segs = relPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string fileStem = segs.Length > 0 ? Path.GetFileNameWithoutExtension(segs[^1]) : "";
        string? folder1 = segs.Length >= 2 ? segs[^2] : null;
        string? folder2 = segs.Length >= 3 ? segs[^3] : null;

        if (string.IsNullOrWhiteSpace(folder1) && !string.IsNullOrWhiteSpace(albumDirectory))
        {
            folder1 = new DirectoryInfo(albumDirectory).Name;
        }

        int? year = null;
        if (!string.IsNullOrWhiteSpace(folder1))
        {
            var ym = YearRegex().Match(folder1);
            if (ym.Success && int.TryParse(ym.Groups[1].Value, out int parsedYear))
            {
                year = parsedYear;
                folder1 = YearTrimRegex().Replace(folder1, "").Trim(' ', '-', '_');
            }
        }

        string? album = !string.IsNullOrWhiteSpace(folder1)
                        && !folder1.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
                        && !folder1.Equals("Music", StringComparison.OrdinalIgnoreCase)
            ? folder1
            : null;

        string? artist = !string.IsNullOrWhiteSpace(folder2)
                         && !folder2.Contains("Various", StringComparison.OrdinalIgnoreCase)
                         && !folder2.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
            ? folder2
            : null;

        string? titleFromFile = null;
        string cleanStem = TrackNumberPrefixRegex().Replace(fileStem, "").Trim();
        int dashIdx = cleanStem.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 1 && dashIdx < cleanStem.Length - 3)
        {
            artist ??= cleanStem[..dashIdx].Trim();
            titleFromFile = cleanStem[(dashIdx + 3)..].Trim();
        }
        else if (!string.IsNullOrWhiteSpace(cleanStem))
        {
            titleFromFile = cleanStem;
        }

        return (artist, album, year, titleFromFile);
    }

    private static string? InferClassicalGenreFromKeywords(string title, string album, string relPath)
    {
        string combined = $"{title} {album} {relPath}";
        if (ClassicalKeywordRegex().IsMatch(combined))
        {
            return "Classical";
        }
        return null;
    }

    private static string? InferClassicalWorkFromTitle(string title, string album)
    {
        int colon = title.IndexOf(':');
        if (colon > 2 && colon < title.Length - 2 && ClassicalKeywordRegex().IsMatch(title))
        {
            return title[..colon].Trim();
        }
        if (ClassicalKeywordRegex().IsMatch(album))
        {
            return album;
        }
        return null;
    }

    private static string? InferClassicalComposerFromPath(string artist, string title, string relPath)
    {
        if (!ClassicalKeywordRegex().IsMatch($"{title} {relPath}")) return null;
        string[] segs = relPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length >= 3 && !segs[^3].Contains("Various", StringComparison.OrdinalIgnoreCase))
        {
            return segs[^3].Trim();
        }
        if (!string.IsNullOrWhiteSpace(artist) && (artist.Contains("Bach", StringComparison.OrdinalIgnoreCase)
            || artist.Contains("Mozart", StringComparison.OrdinalIgnoreCase)
            || artist.Contains("Beethoven", StringComparison.OrdinalIgnoreCase)
            || artist.Contains("Chopin", StringComparison.OrdinalIgnoreCase)
            || artist.Contains("Schubert", StringComparison.OrdinalIgnoreCase)
            || artist.Contains("Mahler", StringComparison.OrdinalIgnoreCase)
            || artist.Contains("Brahms", StringComparison.OrdinalIgnoreCase)
            || artist.Contains("Tchaikovsky", StringComparison.OrdinalIgnoreCase)))
        {
            return PrimaryToken(artist);
        }
        return null;
    }

    private async Task<(string? Artist, string? Album, int? Year, string? Genre, string? CoverUrl)> QueryItunesTrackAsync(
        string term,
        string expectedTitle,
        int expectedDurationMs,
        CancellationToken ct)
    {
        foreach (string country in new[] { "cn", "us", "jp" })
        {
            try
            {
                string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&entity=song&limit=5&country={country}";
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("results", out var arr)) continue;

                JsonElement? best = null;
                double bestScore = -1.0;
                foreach (var item in arr.EnumerateArray())
                {
                    string trackName = item.TryGetProperty("trackName", out var tn) ? (tn.GetString() ?? "") : "";
                    int durMs = item.TryGetProperty("trackTimeMillis", out var tm) && tm.TryGetInt32(out int d) ? d : 0;
                    double score = 0;
                    if (trackName.Equals(expectedTitle, StringComparison.OrdinalIgnoreCase)) score += 3.0;
                    else if (trackName.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase) || expectedTitle.Contains(trackName, StringComparison.OrdinalIgnoreCase)) score += 1.5;
                    if (expectedDurationMs > 0 && durMs > 0)
                    {
                        int diffMs = Math.Abs(durMs - expectedDurationMs);
                        if (diffMs <= 3000) score += 2.0;
                        else if (diffMs <= 12000) score += 1.0;
                        else score -= 1.0;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = item;
                    }
                }

                if (best is { } hit && bestScore >= 1.5)
                {
                    string? artist = hit.TryGetProperty("artistName", out var ar) ? ar.GetString() : null;
                    string? album = hit.TryGetProperty("collectionName", out var col) ? col.GetString() : null;
                    string? genre = hit.TryGetProperty("primaryGenreName", out var gen) ? gen.GetString() : null;
                    int? year = null;
                    if (hit.TryGetProperty("releaseDate", out var rd) && rd.GetString() is { Length: >= 4 } rs && int.TryParse(rs[..4], out int py))
                    {
                        year = py;
                    }
                    string? art = hit.TryGetProperty("artworkUrl100", out var aw) ? aw.GetString() : null;
                    string? hiResArt = string.IsNullOrWhiteSpace(art)
                        ? null
                        : art.Replace("100x100bb", "1200x1200bb", StringComparison.OrdinalIgnoreCase)
                             .Replace("60x60bb", "1200x1200bb", StringComparison.OrdinalIgnoreCase);
                    return (artist, album, year, genre, hiResArt);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Try next storefront */ }
        }
        return (null, null, null, null, null);
    }

    private async Task<(string? Artist, string? Album, int? Year, string? Genre, string? Publisher)> QueryMusicBrainzRecordingAsync(
        string artist,
        string title,
        int expectedDurationMs,
        CancellationToken ct)
    {
        try
        {
            string query = string.IsNullOrWhiteSpace(artist)
                ? $"recording:\"{title}\""
                : $"recording:\"{title}\" AND artist:\"{artist}\"";
            string url = $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=4";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return (null, null, null, null, null);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("recordings", out var recs)) return (null, null, null, null, null);

            foreach (var rec in recs.EnumerateArray())
            {
                int durMs = rec.TryGetProperty("length", out var len) && len.TryGetInt32(out int l) ? l : 0;
                if (expectedDurationMs > 0 && durMs > 0 && Math.Abs(durMs - expectedDurationMs) > 15_000) continue;

                string? mbArtist = null;
                if (rec.TryGetProperty("artist-credit", out var ac) && ac.ValueKind == JsonValueKind.Array)
                {
                    var names = ac.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Cast<string>()
                        .ToList();
                    if (names.Count > 0) mbArtist = string.Join(" / ", names);
                }

                string? mbAlbum = null;
                int? mbYear = null;
                string? mbPublisher = null;
                if (rec.TryGetProperty("releases", out var rels) && rels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rel in rels.EnumerateArray())
                    {
                        string? relTitle = rel.TryGetProperty("title", out var rt) ? rt.GetString() : null;
                        int? relYear = null;
                        if (rel.TryGetProperty("date", out var dt) && dt.GetString() is { Length: >= 4 } ds && int.TryParse(ds[..4], out int ry))
                        {
                            relYear = ry;
                        }
                        string? relLabel = null;
                        if (rel.TryGetProperty("label-info", out var li) && li.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var linfo in li.EnumerateArray())
                            {
                                if (linfo.TryGetProperty("label", out var lbl) && lbl.TryGetProperty("name", out var ln))
                                {
                                    relLabel = ln.GetString();
                                    if (!string.IsNullOrWhiteSpace(relLabel)) break;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(relTitle) && (mbAlbum is null || (relYear.HasValue && (!mbYear.HasValue || relYear.Value < mbYear.Value))))
                        {
                            mbAlbum = relTitle;
                            mbYear = relYear ?? mbYear;
                            mbPublisher = !string.IsNullOrWhiteSpace(relLabel) ? relLabel : mbPublisher;
                        }
                    }
                }

                string? mbGenre = null;
                if (rec.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                {
                    mbGenre = tags.EnumerateArray()
                        .Select(t => t.TryGetProperty("name", out var tn) ? tn.GetString() : null)
                        .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                }

                return (mbArtist, mbAlbum, mbYear, mbGenre, mbPublisher);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Graceful offline/rate-limit fallback */ }
        return (null, null, null, null, null);
    }

    private async Task<(string? Album, string? Genre)> QueryLastFmTrackInfoAsync(
        string apiKey,
        string artist,
        string title,
        CancellationToken ct)
    {
        try
        {
            string url = $"https://ws.audioscrobbler.com/2.0/?method=track.getInfo&api_key={Uri.EscapeDataString(apiKey)}&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}&format=json";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return (null, null);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("track", out var tr)) return (null, null);

            string? album = tr.TryGetProperty("album", out var al) && al.TryGetProperty("title", out var at)
                ? at.GetString()
                : null;
            string? genre = null;
            if (tr.TryGetProperty("toptags", out var tt) && tt.TryGetProperty("tag", out var tarr) && tarr.ValueKind == JsonValueKind.Array)
            {
                genre = tarr.EnumerateArray()
                    .Select(t => t.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            }
            return (album, genre);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, null); }
    }

    private static string PrimaryToken(string artists)
    {
        if (string.IsNullOrWhiteSpace(artists)) return "";
        return ArtistEntitySplitter.SplitPerformers(artists).FirstOrDefault() ?? artists.Split(',', ';')[0].Trim();
    }

    private static string CleanTrackTitle(string raw)
    {
        string s = TrackNumberPrefixRegex().Replace(raw.Trim(), "");
        return s.Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TonewiseDesktop/1.0 (https://github.com/shawnshi/Tonewise)");
        return c;
    }

    [GeneratedRegex(@"^\s*\d{1,3}[\.\-\s]+")]
    private static partial Regex TrackNumberPrefixRegex();

    [GeneratedRegex(@"[\[\(]?\b((?:19|20)\d{2})\b[\]\)]?")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"[\[\(]?\b(?:19|20)\d{2}\b[\]\)]?")]
    private static partial Regex YearTrimRegex();

    [GeneratedRegex(@"\b(?:Symphon|Concerto|Sonata|Quartet|Quintet|Prelude|Fugue|Etude|Nocturne|Requiem|Cantata|Op\.|BWV|KV|K\.|Hob\.)\b|交响|协奏|奏鸣|古典|巴洛克", RegexOptions.IgnoreCase)]
    private static partial Regex ClassicalKeywordRegex();
}
