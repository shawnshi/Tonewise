using Tonewise.SyncHost.Workbench;

namespace Tonewise.SyncHost.Curation;

public sealed record GenreNormalizationTransition(
    string OriginalGenre,
    string CanonicalGenre,
    int TrackCount);

public sealed record GenreNormalizationAuditReport(
    int TotalTracksScanned,
    int TracksWithChanges,
    IReadOnlyList<GenreNormalizationTransition> Transitions,
    IReadOnlyList<TagDiffProposal> Proposals);

/// <summary>
/// Read-only auditor that scans `manifest.db` and produces reversible `TagDiffProposal`
/// entries for genre canonicalization against `genre_aliases.json` without mutating state.
/// </summary>
public static class GenreNormalizationAuditService
{
    public const string DefaultSourceTag = "GenreNorm-Canonical";

    public static GenreNormalizationAuditReport ScanManifest(string manifestDbPath)
    {
        if (string.IsNullOrWhiteSpace(manifestDbPath))
            throw new ArgumentException("Manifest DB path cannot be empty.", nameof(manifestDbPath));
        if (!File.Exists(manifestDbPath))
            throw new FileNotFoundException($"Manifest DB not found: {manifestDbPath}", manifestDbPath);

        using var conn = TonewiseSqlite.OpenReadOnly(manifestDbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT track_uid, genre
            FROM tracks
            WHERE genre IS NOT NULL AND trim(genre) != ''
            ORDER BY rel_path;
            """;

        int totalTracks = 0;
        var proposals = new List<TagDiffProposal>();
        var transitionCounts = new Dictionary<(string Orig, string Canon), int>();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            totalTracks++;
            string trackUid = reader.GetString(0);
            string origGenre = reader.GetString(1).Trim();
            string canonicalGenre = GenreCanonicalizer.NormalizeGenre(origGenre);
            if (string.IsNullOrEmpty(canonicalGenre) || string.Equals(origGenre, canonicalGenre, StringComparison.Ordinal))
            {
                continue;
            }

            proposals.Add(new TagDiffProposal(
                ProposalId: $"genrenorm:{trackUid}:genre",
                TrackUid: trackUid,
                FieldName: "genre",
                OriginalValue: origGenre,
                SuggestedValue: canonicalGenre,
                Confidence: 0.99,
                SourceTag: DefaultSourceTag,
                Status: ProposalStatus.Pending,
                BatchId: null,
                CreatedAtMs: nowMs));

            var key = (origGenre, canonicalGenre);
            transitionCounts[key] = transitionCounts.TryGetValue(key, out int prev) ? prev + 1 : 1;
        }

        var transitions = transitionCounts
            .Select(kv => new GenreNormalizationTransition(kv.Key.Orig, kv.Key.Canon, kv.Value))
            .OrderByDescending(t => t.TrackCount)
            .ThenBy(t => t.CanonicalGenre, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GenreNormalizationAuditReport(
            TotalTracksScanned: totalTracks,
            TracksWithChanges: proposals.Count,
            Transitions: transitions,
            Proposals: proposals);
    }
}
