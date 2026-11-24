using System.Diagnostics;
using System.Text.Json;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.SyncHost.Curation;

public sealed record MusicBrainzReleaseCandidate(
    string ReleaseMbid,
    string AlbumTitle,
    string ArtistCredit,
    string PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string Status,
    int? OriginalYear,
    string? Publisher,
    int DurationMs,
    double AcoustIdScore
);

public sealed record DisambiguatedReleaseResult(
    MusicBrainzReleaseCandidate SelectedRelease,
    double Confidence,
    bool RequiresManualReview,
    string SourceTag
);

/// <summary>
/// Resolves Chromaprint/AcoustID -> MusicBrainz Release-Group candidates to canonical earliest official
/// studio releases while filtering out Various Artists & compilation noise.
/// </summary>
public static class ReleaseDisambiguatorService
{
    private static readonly HashSet<string> ExcludedSecondaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compilation", "Live", "DJ-mix", "Remix", "Mixtape/Street", "Demo"
    };

    public static DisambiguatedReleaseResult? SelectCanonicalStudioRelease(
        IReadOnlyList<MusicBrainzReleaseCandidate> candidates,
        int trackDurationMs)
    {
        if (candidates.Count == 0) return null;

        var filteredStudio = candidates
            .Where(c =>
                !c.ArtistCredit.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.PrimaryType, "Album", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Status, "Official", StringComparison.OrdinalIgnoreCase)
                && c.OriginalYear is >= 1900
                && !c.SecondaryTypes.Any(st => ExcludedSecondaryTypes.Contains(st)))
            .OrderBy(c => c.OriginalYear)
            .ThenBy(c => Math.Abs(c.DurationMs - trackDurationMs))
            .ToList();

        MusicBrainzReleaseCandidate chosen;
        bool cleanMatch;
        if (filteredStudio.Count > 0)
        {
            chosen = filteredStudio[0];
            cleanMatch = chosen.AcoustIdScore > 0.88 && Math.Abs(chosen.DurationMs - trackDurationMs) <= 3000;
        }
        else
        {
            chosen = candidates
                .OrderByDescending(c => c.AcoustIdScore)
                .ThenBy(c => c.OriginalYear ?? int.MaxValue)
                .First();
            cleanMatch = false;
        }

        double confidence = cleanMatch
            ? Math.Clamp(chosen.AcoustIdScore, 0.90, 0.99)
            : 0.60;

        return new DisambiguatedReleaseResult(
            SelectedRelease: chosen,
            Confidence: confidence,
            RequiresManualReview: !cleanMatch,
            SourceTag: cleanMatch ? "AcoustID-OriginalRelease" : "AcoustID-NeedsReview"
        );
    }

    public static IReadOnlyList<TagDiffProposal> GenerateMissingReleaseProposals(
        TrackCurationSnapshot existing,
        IReadOnlyList<MusicBrainzReleaseCandidate> candidates,
        int trackDurationMs,
        bool forceOverwriteUnlocked = false)
    {
        var disambiguated = SelectCanonicalStudioRelease(candidates, trackDurationMs);
        if (disambiguated is null) return [];

        var rel = disambiguated.SelectedRelease;
        var proposals = new List<TagDiffProposal>();

        void AddIfMissing(string field, string? orig, string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            var p = StagingDiffStore.TryCreateProposal(
                existing.TrackUid,
                field,
                orig,
                val,
                disambiguated.Confidence,
                disambiguated.SourceTag,
                forceOverwriteUnlocked
            );
            if (p is not null) proposals.Add(p);
        }

        AddIfMissing("album", existing.Album, rel.AlbumTitle);
        if (rel.OriginalYear is { } year)
        {
            AddIfMissing("year", existing.Year?.ToString(), year.ToString());
        }
        AddIfMissing("publisher", existing.Publisher, rel.Publisher);

        return proposals;
    }

    public static IReadOnlyList<MusicBrainzReleaseCandidate> ParseAcoustIdJsonResponse(
        string acoustIdJson,
        int fallbackDurationMs)
    {
        var results = new List<MusicBrainzReleaseCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(acoustIdJson);
            if (!doc.RootElement.TryGetProperty("results", out var resArr)) return results;

            foreach (var r in resArr.EnumerateArray())
            {
                double score = r.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0.0;
                if (!r.TryGetProperty("recordings", out var recs)) continue;

                foreach (var rec in recs.EnumerateArray())
                {
                    int durationMs = rec.TryGetProperty("duration", out var dur)
                        ? (int)(dur.GetDouble() * 1000.0)
                        : fallbackDurationMs;
                    string artistCredit = ExtractArtistCredit(rec);
                    if (!rec.TryGetProperty("releasegroups", out var rgs)) continue;

                    foreach (var rg in rgs.EnumerateArray())
                    {
                        string rgId = rg.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                        string title = rg.TryGetProperty("title", out var tiEl) ? (tiEl.GetString() ?? "") : "";
                        string type = rg.TryGetProperty("type", out var tyEl) ? (tyEl.GetString() ?? "Album") : "Album";
                        var secondaryTypes = new List<string>();
                        if (rg.TryGetProperty("secondarytypes", out var secArr))
                        {
                            foreach (var st in secArr.EnumerateArray())
                            {
                                string? val = st.GetString();
                                if (!string.IsNullOrWhiteSpace(val)) secondaryTypes.Add(val);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            results.Add(new MusicBrainzReleaseCandidate(
                                ReleaseMbid: rgId,
                                AlbumTitle: title,
                                ArtistCredit: artistCredit,
                                PrimaryType: type,
                                SecondaryTypes: secondaryTypes,
                                Status: "Official",
                                OriginalYear: null,
                                Publisher: null,
                                DurationMs: durationMs,
                                AcoustIdScore: score
                            ));
                        }
                    }
                }
            }
        }
        catch
        {
            // Malformed response yields partial results safely
        }
        return results;
    }

    public static async Task<IReadOnlyList<MusicBrainzReleaseCandidate>> LookupAcoustIdCandidatesAsync(
        string audioFilePath,
        string acoustIdApiKey,
        HttpClient? httpClient = null,
        string? fpcalcExePath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(acoustIdApiKey) || !File.Exists(audioFilePath))
            return [];

        string exe = string.IsNullOrWhiteSpace(fpcalcExePath) ? "fpcalc" : fpcalcExePath;
        int durationSec;
        string fingerprint;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "-json", audioFilePath },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return [];
            string jsonOut = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(jsonOut)) return [];

            using var fpDoc = JsonDocument.Parse(jsonOut);
            durationSec = (int)Math.Round(fpDoc.RootElement.GetProperty("duration").GetDouble());
            fingerprint = fpDoc.RootElement.GetProperty("fingerprint").GetString() ?? "";
        }
        catch
        {
            return [];
        }

        if (durationSec <= 0 || string.IsNullOrWhiteSpace(fingerprint)) return [];

        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        string url =
            $"https://api.acoustid.org/v2/lookup?client={Uri.EscapeDataString(acoustIdApiKey)}&meta=recordings+releasegroups+compress&duration={durationSec}&fingerprint={Uri.EscapeDataString(fingerprint)}";
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return [];
        string body = await resp.Content.ReadAsStringAsync(ct);
        return ParseAcoustIdJsonResponse(body, durationSec * 1000);
    }

    private static string ExtractArtistCredit(JsonElement recording)
    {
        if (!recording.TryGetProperty("artists", out var artists)) return "";
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in artists.EnumerateArray())
        {
            if (a.TryGetProperty("name", out var n))
            {
                string norm = ArtistEntitySplitter.NormalizePerformerName(n.GetString());
                if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
                {
                    names.Add(norm);
                }
            }
        }
        return string.Join(" & ", names);
    }
}
