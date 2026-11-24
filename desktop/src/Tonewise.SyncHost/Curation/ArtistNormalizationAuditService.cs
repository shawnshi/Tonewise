using System.Text.Json;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.SyncHost.Curation;

public sealed record ArtistNormalizationTransition(
    string FieldName,
    string OriginalToken,
    string CanonicalToken,
    int TrackCount);

public sealed record ArtistNormalizationAuditReport(
    int TotalTracksScanned,
    int TracksWithChanges,
    int TotalProposals,
    IReadOnlyDictionary<string, int> ProposalCountsByField,
    IReadOnlyList<ArtistNormalizationTransition> Transitions,
    IReadOnlyList<TagDiffProposal> Proposals);

/// <summary>
/// Read-only auditor that scans `manifest.db` and produces reversible `TagDiffProposal`
/// entries for artist, album_artist, composer, and conductor normalization without mutating any state.
/// </summary>
public static class ArtistNormalizationAuditService
{
    public const string DefaultSourceTag = "ArtistNorm-Canonical";

    public static ArtistNormalizationAuditReport ScanManifest(string manifestDbPath)
    {
        if (string.IsNullOrWhiteSpace(manifestDbPath))
            throw new ArgumentException("Manifest DB path cannot be empty.", nameof(manifestDbPath));
        if (!File.Exists(manifestDbPath))
            throw new FileNotFoundException($"Manifest DB not found: {manifestDbPath}", manifestDbPath);

        using var conn = TonewiseSqlite.OpenReadOnly(manifestDbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT track_uid, album_artist, artists_json, composers_json, conductors_json
            FROM tracks
            ORDER BY rel_path;
            """;

        int totalTracks = 0;
        int tracksWithChanges = 0;
        var proposals = new List<TagDiffProposal>();
        var countsByField = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["album_artist"] = 0,
            ["artists_json"] = 0,
            ["composers_json"] = 0,
            ["conductors_json"] = 0,
        };
        var transitionCounts = new Dictionary<(string Field, string Orig, string Canon), int>();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        void BumpTransition(string field, string orig, string canon)
        {
            var key = (field, orig, canon);
            transitionCounts[key] = transitionCounts.TryGetValue(key, out int prev) ? prev + 1 : 1;
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            totalTracks++;
            string trackUid = reader.GetString(0);
            string? albumArtist = reader.IsDBNull(1) ? null : reader.GetString(1);
            string artistsJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
            string composersJson = reader.IsDBNull(3) ? "[]" : reader.GetString(3);
            string conductorsJson = reader.IsDBNull(4) ? "[]" : reader.GetString(4);

            bool trackChanged = false;

            if (!string.IsNullOrWhiteSpace(albumArtist))
            {
                string normAa = ArtistEntitySplitter.NormalizeAlbumArtist(albumArtist);
                if (!string.IsNullOrEmpty(normAa) && !string.Equals(albumArtist, normAa, StringComparison.Ordinal))
                {
                    proposals.Add(new TagDiffProposal(
                        ProposalId: $"artistnorm:{trackUid}:album_artist",
                        TrackUid: trackUid,
                        FieldName: "album_artist",
                        OriginalValue: albumArtist,
                        SuggestedValue: normAa,
                        Confidence: 0.98,
                        SourceTag: DefaultSourceTag,
                        Status: ProposalStatus.Pending,
                        BatchId: null,
                        CreatedAtMs: nowMs));
                    countsByField["album_artist"]++;
                    BumpTransition("album_artist", albumArtist, normAa);
                    trackChanged = true;
                }
            }

            ProcessJsonArrayField("artists_json", artistsJson, composerMode: false);
            ProcessJsonArrayField("composers_json", composersJson, composerMode: true);
            ProcessJsonArrayField("conductors_json", conductorsJson, composerMode: false);

            if (trackChanged)
            {
                tracksWithChanges++;
            }

            void ProcessJsonArrayField(string fieldName, string rawJson, bool composerMode)
            {
                var origList = ParseStringArray(rawJson);
                if (origList.Count == 0) return;

                var normList = ArtistEntitySplitter.NormalizeEntityArray(origList, composerMode);
                if (origList.SequenceEqual(normList, StringComparer.Ordinal))
                {
                    return;
                }

                string suggestedJson = JsonSerializer.Serialize(normList);
                proposals.Add(new TagDiffProposal(
                    ProposalId: $"artistnorm:{trackUid}:{fieldName}",
                    TrackUid: trackUid,
                    FieldName: fieldName,
                    OriginalValue: rawJson,
                    SuggestedValue: suggestedJson,
                    Confidence: 0.98,
                    SourceTag: DefaultSourceTag,
                    Status: ProposalStatus.Pending,
                    BatchId: null,
                    CreatedAtMs: nowMs));
                countsByField[fieldName]++;
                trackChanged = true;

                foreach (string origToken in origList)
                {
                    string canonToken;
                    if (ArtistEntitySplitter.TryExpandMultiValueAlias(origToken, composerMode, out var expanded))
                    {
                        canonToken = string.Join(" + ", expanded);
                    }
                    else
                    {
                        canonToken = composerMode
                            ? ArtistEntitySplitter.NormalizeComposerName(origToken)
                            : ArtistEntitySplitter.NormalizePerformerName(origToken);
                    }

                    if (!string.Equals(origToken, canonToken, StringComparison.Ordinal))
                    {
                        BumpTransition(fieldName, origToken, canonToken);
                    }
                }
            }
        }

        var transitions = transitionCounts
            .Select(kv => new ArtistNormalizationTransition(kv.Key.Field, kv.Key.Orig, kv.Key.Canon, kv.Value))
            .OrderByDescending(t => t.TrackCount)
            .ThenBy(t => t.FieldName, StringComparer.Ordinal)
            .ThenBy(t => t.OriginalToken, StringComparer.Ordinal)
            .ToList();

        return new ArtistNormalizationAuditReport(
            TotalTracksScanned: totalTracks,
            TracksWithChanges: tracksWithChanges,
            TotalProposals: proposals.Count,
            ProposalCountsByField: countsByField,
            Transitions: transitions,
            Proposals: proposals);
    }

    private static List<string> ParseStringArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
