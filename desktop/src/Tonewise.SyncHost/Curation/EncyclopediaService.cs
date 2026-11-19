using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Tonewise.SyncHost.Curation;

public static class EncyclopediaEntityType
{
    public const string Artist = "artist";
    public const string Composer = "composer";
    public const string Conductor = "conductor";
    public const string Work = "work";
    public const string Album = "album";
}

public sealed record EntityBiographyEntry(
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("normalized_name")] string NormalizedName,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("mbid")] string? Mbid,
    [property: JsonPropertyName("wikidata_qid")] string? WikidataQid,
    [property: JsonPropertyName("birth_death_years")] string? BirthDeathYears,
    [property: JsonPropertyName("era")] string? Era,
    [property: JsonPropertyName("summary_zh")] string? SummaryZh,
    [property: JsonPropertyName("summary_en")] string? SummaryEn,
    [property: JsonPropertyName("source_url")] string? SourceUrl,
    [property: JsonPropertyName("updated_at_ms")] long UpdatedAtMs
);

/// <summary>
/// Manages `/Music/.tonewise/encyclopedia.db` storing artist/composer/conductor/album/work biographies
/// via Last.fm (Pop), Wikipedia / Douban Music (豆瓣音乐), and MusicBrainz -> Wikidata -> Wikipedia REST Summary (Classical).
/// Never pollutes audio file COMMENT headers.
/// </summary>
public sealed partial class EncyclopediaService
{
    public string DbPath { get; }

    public EncyclopediaService(string encyclopediaDbPath)
    {
        DbPath = encyclopediaDbPath;
        EnsureSchema();
    }

    public static string NormalizeEntityName(string rawName)
    {
        string trimmed = rawName.Trim().ToLowerInvariant();
        return MultiSpaceRegex().Replace(trimmed, " ");
    }

    public static string CleanLastFmBioText(string rawWikiSummary)
    {
        if (string.IsNullOrWhiteSpace(rawWikiSummary)) return "";
        string noAnchor = HtmlAnchorRegex().Replace(rawWikiSummary, "");
        string noFooter = LastFmLicenseFooterRegex().Replace(noAnchor, "");
        return noFooter.Trim();
    }

    public static string? ExtractWikidataQidFromMusicBrainzJson(string mbJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(mbJson);
            if (!doc.RootElement.TryGetProperty("relations", out var rels)) return null;
            foreach (var rel in rels.EnumerateArray())
            {
                if (!rel.TryGetProperty("url", out var urlObj)) continue;
                if (!urlObj.TryGetProperty("resource", out var resProp)) continue;
                string? resource = resProp.GetString();
                if (string.IsNullOrWhiteSpace(resource)) continue;

                var match = WikidataQidRegex().Match(resource);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Malformed JSON yields null cleanly
        }
        return null;
    }

    public void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entity_biographies (
                entity_type       TEXT NOT NULL,
                normalized_name   TEXT NOT NULL,
                display_name      TEXT NOT NULL,
                mbid              TEXT,
                wikidata_qid      TEXT,
                birth_death_years TEXT,
                era               TEXT,
                summary_zh        TEXT,
                summary_en        TEXT,
                source_url        TEXT,
                updated_at_ms     INTEGER NOT NULL,
                PRIMARY KEY (entity_type, normalized_name)
            );
            CREATE INDEX IF NOT EXISTS idx_bio_mbid ON entity_biographies (mbid);
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertBiography(EntityBiographyEntry entry)
    {
        string normType = entry.EntityType.Trim().ToLowerInvariant();
        string normName = NormalizeEntityName(string.IsNullOrWhiteSpace(entry.NormalizedName) ? entry.DisplayName : entry.NormalizedName);
        long ts = entry.UpdatedAtMs > 0 ? entry.UpdatedAtMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entity_biographies (
                entity_type, normalized_name, display_name, mbid, wikidata_qid,
                birth_death_years, era, summary_zh, summary_en, source_url, updated_at_ms
            ) VALUES ($type, $norm, $disp, $mbid, $qid, $bd, $era, $zh, $en, $url, $ts)
            ON CONFLICT(entity_type, normalized_name) DO UPDATE SET
                display_name = excluded.display_name,
                mbid = COALESCE(excluded.mbid, entity_biographies.mbid),
                wikidata_qid = COALESCE(excluded.wikidata_qid, entity_biographies.wikidata_qid),
                birth_death_years = COALESCE(excluded.birth_death_years, entity_biographies.birth_death_years),
                era = COALESCE(excluded.era, entity_biographies.era),
                summary_zh = COALESCE(excluded.summary_zh, entity_biographies.summary_zh),
                summary_en = COALESCE(excluded.summary_en, entity_biographies.summary_en),
                source_url = COALESCE(excluded.source_url, entity_biographies.source_url),
                updated_at_ms = excluded.updated_at_ms;
            """;
        cmd.Parameters.AddWithValue("$type", normType);
        cmd.Parameters.AddWithValue("$norm", normName);
        cmd.Parameters.AddWithValue("$disp", entry.DisplayName);
        cmd.Parameters.AddWithValue("$mbid", (object?)entry.Mbid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$qid", (object?)entry.WikidataQid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bd", (object?)entry.BirthDeathYears ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$era", (object?)entry.Era ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$zh", (object?)entry.SummaryZh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$en", (object?)entry.SummaryEn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)entry.SourceUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.ExecuteNonQuery();
    }

    public EntityBiographyEntry? GetBiography(string entityType, string name)
    {
        string normType = entityType.Trim().ToLowerInvariant();
        string normName = NormalizeEntityName(name);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT entity_type, normalized_name, display_name, mbid, wikidata_qid,
                   birth_death_years, era, summary_zh, summary_en, source_url, updated_at_ms
            FROM entity_biographies
            WHERE entity_type = $type AND normalized_name = $norm;
            """;
        cmd.Parameters.AddWithValue("$type", normType);
        cmd.Parameters.AddWithValue("$norm", normName);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public IReadOnlyList<EntityBiographyEntry> GetAllBiographies(string? entityType = null, string? query = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            clauses.Add("entity_type = $type");
            cmd.Parameters.AddWithValue("$type", entityType.Trim().ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            clauses.Add("(display_name LIKE $q OR summary_zh LIKE $q OR summary_en LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
        }

        string where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        cmd.CommandText = $"""
            SELECT entity_type, normalized_name, display_name, mbid, wikidata_qid,
                   birth_death_years, era, summary_zh, summary_en, source_url, updated_at_ms
            FROM entity_biographies
            {where}
            ORDER BY entity_type, display_name;
            """;

        var list = new List<EntityBiographyEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadRow(reader));
        }
        return list;
    }

    public async Task<EntityBiographyEntry?> FetchAndCacheBiographyAsync(
        string entityType,
        string displayName,
        string? mbid = null,
        string? lastFmApiKey = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var existing = GetBiography(entityType, displayName);
        if (existing is not null && (!string.IsNullOrWhiteSpace(existing.SummaryZh) || !string.IsNullOrWhiteSpace(existing.SummaryEn)))
        {
            return existing;
        }

        HttpClient client = httpClient ?? SharedHttpClient;
        string? qid = null;
        string? summaryZh = null;
        string? summaryEn = null;
        string? era = null;
        string? sourceUrl = null;
        bool preferClassicalGraph =
            entityType.Equals(EncyclopediaEntityType.Composer, StringComparison.OrdinalIgnoreCase)
            || entityType.Equals(EncyclopediaEntityType.Conductor, StringComparison.OrdinalIgnoreCase)
            || entityType.Equals(EncyclopediaEntityType.Work, StringComparison.OrdinalIgnoreCase);

        if (preferClassicalGraph && !string.IsNullOrWhiteSpace(mbid))
        {
            string mbUrl = $"https://musicbrainz.org/ws/2/{Uri.EscapeDataString(entityType)}/{Uri.EscapeDataString(mbid)}?inc=url-rels&fmt=json";
            string? mbJson = await TryGetStringAsync(client, mbUrl, acceptLanguage: null, ct);
            if (!string.IsNullOrWhiteSpace(mbJson))
            {
                qid = ExtractWikidataQidFromMusicBrainzJson(mbJson);
            }
        }

        if (!preferClassicalGraph && !string.IsNullOrWhiteSpace(lastFmApiKey))
        {
            string method = entityType.Equals(EncyclopediaEntityType.Album, StringComparison.OrdinalIgnoreCase)
                ? "album.getinfo"
                : "artist.getinfo";
            string param = entityType.Equals(EncyclopediaEntityType.Album, StringComparison.OrdinalIgnoreCase)
                ? $"album={Uri.EscapeDataString(displayName)}"
                : $"artist={Uri.EscapeDataString(displayName)}";
            string lfmUrl = $"https://ws.audioscrobbler.com/2.0/?method={method}&{param}&lang=zh&api_key={Uri.EscapeDataString(lastFmApiKey)}&format=json";
            string? lfmJson = await TryGetStringAsync(client, lfmUrl, acceptLanguage: null, ct);
            if (!string.IsNullOrWhiteSpace(lfmJson))
            {
                using var doc = JsonDocument.Parse(lfmJson);
                if (doc.RootElement.TryGetProperty("artist", out var artObj)
                    && artObj.TryGetProperty("bio", out var bioObj)
                    && bioObj.TryGetProperty("summary", out var sumEl))
                {
                    string cleaned = CleanLastFmBioText(sumEl.GetString() ?? "");
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        summaryZh = cleaned;
                        sourceUrl = artObj.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    }
                }
                else if (doc.RootElement.TryGetProperty("album", out var albObj)
                         && albObj.TryGetProperty("wiki", out var wikiObj)
                         && wikiObj.TryGetProperty("summary", out var albSumEl))
                {
                    string cleaned = CleanLastFmBioText(albSumEl.GetString() ?? "");
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        summaryZh = cleaned;
                        sourceUrl = albObj.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(summaryZh))
        {
            string wikiTitle = Uri.EscapeDataString(displayName.Trim().Replace(' ', '_'));
            string zhWikiUrl = $"https://zh.wikipedia.org/api/rest_v1/page/summary/{wikiTitle}";
            string? zhJson = await TryGetStringAsync(client, zhWikiUrl, "zh-cn,zh;q=0.9", ct);
            if (!string.IsNullOrWhiteSpace(zhJson))
            {
                (summaryZh, era, sourceUrl) = ParseWikipediaSummaryJson(zhJson);
            }

            if (string.IsNullOrWhiteSpace(summaryZh))
            {
                var douban = await DoubanMusicClient.TryFetchBioAsync(client, displayName, ct);
                if (!string.IsNullOrWhiteSpace(douban.SummaryZh))
                {
                    summaryZh = douban.SummaryZh;
                    era ??= douban.Era;
                    sourceUrl ??= douban.SourceUrl;
                }
            }

            if (string.IsNullOrWhiteSpace(summaryZh))
            {
                string enWikiUrl = $"https://en.wikipedia.org/api/rest_v1/page/summary/{wikiTitle}";
                string? enJson = await TryGetStringAsync(client, enWikiUrl, "en-US,en;q=0.9", ct);
                if (!string.IsNullOrWhiteSpace(enJson))
                {
                    (summaryEn, era, sourceUrl) = ParseWikipediaSummaryJson(enJson);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(summaryZh) && string.IsNullOrWhiteSpace(summaryEn))
        {
            return null;
        }

        var entry = new EntityBiographyEntry(
            EntityType: entityType,
            NormalizedName: NormalizeEntityName(displayName),
            DisplayName: displayName.Trim(),
            Mbid: mbid,
            WikidataQid: qid,
            BirthDeathYears: null,
            Era: era,
            SummaryZh: summaryZh,
            SummaryEn: summaryEn,
            SourceUrl: sourceUrl,
            UpdatedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
        UpsertBiography(entry);
        return entry;
    }

    private static (string? Extract, string? Description, string? PageUrl) ParseWikipediaSummaryJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? extract = root.TryGetProperty("extract", out var ex) ? ex.GetString() : null;
            string? desc = root.TryGetProperty("description", out var d) ? d.GetString() : null;
            string? pageUrl = null;
            if (root.TryGetProperty("content_urls", out var cu)
                && cu.TryGetProperty("desktop", out var desk)
                && desk.TryGetProperty("page", out var pg))
            {
                pageUrl = pg.GetString();
            }
            return (extract, desc, pageUrl);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static async Task<string?> TryGetStringAsync(
        HttpClient client,
        string url,
        string? acceptLanguage,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!req.Headers.UserAgent.Any())
            {
                req.Headers.UserAgent.ParseAdd("TonewiseDesktop/1.0 (https://github.com/shawnshi/Tonewise)");
            }
            if (!string.IsNullOrWhiteSpace(acceptLanguage))
            {
                req.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
            }
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static EntityBiographyEntry ReadRow(SqliteDataReader reader) =>
        new(
            EntityType: reader.GetString(0),
            NormalizedName: reader.GetString(1),
            DisplayName: reader.GetString(2),
            Mbid: reader.IsDBNull(3) ? null : reader.GetString(3),
            WikidataQid: reader.IsDBNull(4) ? null : reader.GetString(4),
            BirthDeathYears: reader.IsDBNull(5) ? null : reader.GetString(5),
            Era: reader.IsDBNull(6) ? null : reader.GetString(6),
            SummaryZh: reader.IsDBNull(7) ? null : reader.GetString(7),
            SummaryEn: reader.IsDBNull(8) ? null : reader.GetString(8),
            SourceUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
            UpdatedAtMs: reader.GetInt64(10)
        );

    private SqliteConnection OpenConnection() => TonewiseSqlite.OpenReadWrite(DbPath);

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"<a\s+href=[^>]+>Read more on Last\.fm</a>\.?", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAnchorRegex();

    [GeneratedRegex(@"User-contributed text is available under the Creative Commons.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LastFmLicenseFooterRegex();

    [GeneratedRegex(@"wikidata\.org/wiki/(Q\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WikidataQidRegex();
}
