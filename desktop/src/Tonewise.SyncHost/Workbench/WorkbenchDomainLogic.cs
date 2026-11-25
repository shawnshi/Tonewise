using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tonewise.Interop;

namespace Tonewise.SyncHost.Workbench;

public sealed record FiveDimFilterQuery(
    string Scene = "all",
    string Energy = "all",
    string Vocals = "all",
    bool ExcludeVocal = false,
    string TempoBucket = "all",
    string MoodCluster = "all",
    string SortArc = "flat",
    bool TempoNullPassthrough = true,
    IReadOnlyList<string>? Genres = null
);

public static class FiveDimQueryBuilder
{
    public static IReadOnlyList<string> NormalizeGenres(IReadOnlyList<string>? genres)
    {
        if (genres is null || genres.Count == 0)
        {
            return [];
        }

        var result = new List<string>(genres.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in genres)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string trimmed = raw.Trim();
            if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    public static bool RequiresFilterOrSort(FiveDimFilterQuery query) =>
        query.Scene != "all"
        || query.Energy != "all"
        || query.Vocals != "all"
        || query.ExcludeVocal
        || query.TempoBucket != "all"
        || query.MoodCluster != "all"
        || NormalizeGenres(query.Genres).Count > 0
        || !string.Equals(query.SortArc, "flat", StringComparison.OrdinalIgnoreCase);

    public static string BuildSceneCardJson(FiveDimFilterQuery query)
    {
        var predicates = new List<object>();

        var genres = NormalizeGenres(query.Genres);
        if (genres.Count > 0)
        {
            predicates.Add(new { predicate = new { field = "genre", op = "in", values = genres } });
        }
        if (!string.IsNullOrWhiteSpace(query.Scene) && query.Scene != "all")
        {
            predicates.Add(new { predicate = new { field = "dj_scene", op = "in", values = new[] { query.Scene } } });
        }
        if (!string.IsNullOrWhiteSpace(query.Energy) && query.Energy != "all")
        {
            predicates.Add(new { predicate = new { field = "dj_energy", op = "eq", values = new[] { query.Energy } } });
        }
        if (query.ExcludeVocal)
        {
            predicates.Add(new
            {
                not = new
                {
                    predicate = new { field = "dj_vocals", op = "eq", values = new[] { "vocal" } }
                }
            });
        }
        else if (!string.IsNullOrWhiteSpace(query.Vocals) && query.Vocals != "all")
        {
            predicates.Add(new { predicate = new { field = "dj_vocals", op = "eq", values = new[] { query.Vocals } } });
        }
        if (!string.IsNullOrWhiteSpace(query.TempoBucket) && query.TempoBucket != "all")
        {
            predicates.Add(new { predicate = new { field = "tempo_bucket", op = "in", values = new[] { query.TempoBucket } } });
        }
        if (!string.IsNullOrWhiteSpace(query.MoodCluster) && query.MoodCluster != "all")
        {
            predicates.Add(new { predicate = new { field = "mood_cluster", op = "in", values = new[] { query.MoodCluster } } });
        }

        object ruleObj = predicates.Count > 0
            ? new { all = predicates }
            : new { all = Array.Empty<object>() };

        return JsonSerializer.Serialize(new
        {
            id = "00000000-0000-4000-8000-000000000001",
            title = "Desktop Matrix Filter",
            updated_at_ms = 0UL,
            sort_arc = string.IsNullOrWhiteSpace(query.SortArc) ? "flat" : query.SortArc,
            tempo_null_passthrough = query.TempoNullPassthrough,
            rule = ruleObj,
        });
    }
}

public sealed record GenreFacetItem(string CanonicalName, int TrackCount);

public static class GenreCanonicalizer
{
    private static readonly char[] SplitSeparators = [';', ',', '|', '、'];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyAliases = new(() =>
    {
        var asm = typeof(GenreCanonicalizer).Assembly;
        const string resourceName = "Tonewise.SyncHost.protocol.genre_aliases.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded genre dictionary resource '{resourceName}' not found.");
        using var doc = JsonDocument.Parse(stream);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("canonical_aliases", out var cObj) && cObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in cObj.EnumerateObject())
            {
                string key = prop.Name.Trim();
                string? val = prop.Value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                {
                    map[key] = val;
                }
            }
        }
        return map;
    });

    public static IReadOnlyDictionary<string, string> CanonicalAliases => LazyAliases.Value;

    public static string NormalizeGenre(string? rawGenre)
    {
        if (string.IsNullOrWhiteSpace(rawGenre))
        {
            return "";
        }

        var map = LazyAliases.Value;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>();
        foreach (string part in rawGenre.Split(SplitSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            string canonical = map.TryGetValue(trimmed, out string? mapped) ? mapped : trimmed;
            if (seen.Add(canonical))
            {
                tokens.Add(canonical);
            }
        }

        return string.Join("; ", tokens);
    }
}

public static class GenreFacetHelper
{
    private static readonly char[] FacetSeparators = [';', ',', '|', '、'];

    public static IReadOnlyList<GenreFacetItem> BuildFacets(IEnumerable<string?> rawGenres)
    {
        var variantsByKey = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (string? raw in rawGenres)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var seenInTrack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in raw.Split(FacetSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                string canonical = GenreCanonicalizer.NormalizeGenre(trimmed);
                if (canonical.Length == 0 || !seenInTrack.Add(canonical))
                {
                    continue;
                }

                if (!variantsByKey.TryGetValue(canonical, out var variantCounts))
                {
                    variantCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    variantsByKey[canonical] = variantCounts;
                }

                variantCounts[canonical] = variantCounts.TryGetValue(canonical, out int existing) ? existing + 1 : 1;
            }
        }

        var facets = new List<GenreFacetItem>(variantsByKey.Count);
        foreach (var variantCounts in variantsByKey.Values)
        {
            int total = 0;
            string? bestName = null;
            int bestCount = -1;

            foreach (var (variant, count) in variantCounts)
            {
                total += count;
                if (count > bestCount
                    || (count == bestCount && IsBetterVariant(variant, bestName!)))
                {
                    bestCount = count;
                    bestName = variant;
                }
            }

            if (!string.IsNullOrEmpty(bestName))
            {
                facets.Add(new GenreFacetItem(bestName, total));
            }
        }

        facets.Sort((a, b) =>
        {
            int cmp = b.TrackCount.CompareTo(a.TrackCount);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.CanonicalName, b.CanonicalName);
        });
        return facets;
    }

    public static string FormatGenreChipLabel(IReadOnlyList<string> selectedGenres) => selectedGenres.Count switch
    {
        0 => "流派",
        1 => $"流派: {selectedGenres[0]}",
        _ => $"流派: {selectedGenres[0]} +{selectedGenres.Count - 1}",
    };

    private static bool IsBetterVariant(string candidate, string current)
    {
        bool candUpper = char.IsUpper(candidate[0]);
        bool currUpper = char.IsUpper(current[0]);
        if (candUpper != currUpper)
        {
            return candUpper;
        }
        return StringComparer.Ordinal.Compare(candidate, current) < 0;
    }
}

public static class ClassicalAbSwitchHelper
{
    public static IReadOnlyList<T> FindAlternateRecordings<T>(
        T current,
        IEnumerable<T> library,
        Func<T, string> uidSelector,
        Func<T, string?> workSelector,
        Func<T, string?> movementSelector) =>
        FindAlternateRecordings(current, library, uidSelector, workSelector, movementSelector, movementNumberSelector: null, albumScopeSelector: null);

    public static IReadOnlyList<T> FindAlternateRecordings<T>(
        T current,
        IEnumerable<T> library,
        Func<T, string> uidSelector,
        Func<T, string?> workSelector,
        Func<T, string?> movementSelector,
        Func<T, int?>? movementNumberSelector,
        Func<T, string?>? albumScopeSelector = null)
    {
        string targetUid = uidSelector(current);
        string normWork = NormalizeSegment(workSelector(current));
        if (string.IsNullOrEmpty(normWork)) return [];

        string normMovement = NormalizeSegment(movementSelector(current));
        int? targetMovementNo = movementNumberSelector?.Invoke(current);
        if (targetMovementNo is <= 0) targetMovementNo = null;
        string targetScope = albumScopeSelector is null ? "" : NormalizeSegment(albumScopeSelector(current));
        var results = new List<T>();

        foreach (var candidate in library)
        {
            if (string.Equals(uidSelector(candidate), targetUid, StringComparison.Ordinal))
                continue;

            string candWork = NormalizeSegment(workSelector(candidate));
            if (!string.Equals(candWork, normWork, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrEmpty(targetScope))
            {
                string candScope = NormalizeSegment(albumScopeSelector!(candidate));
                if (!string.IsNullOrEmpty(candScope) && string.Equals(candScope, targetScope, StringComparison.Ordinal))
                    continue;
            }

            if (targetMovementNo.HasValue)
            {
                int? candMovNo = movementNumberSelector?.Invoke(candidate);
                if (candMovNo is > 0)
                {
                    if (candMovNo.Value != targetMovementNo.Value) continue;
                }
                else if (!string.IsNullOrEmpty(normMovement))
                {
                    string candMov = NormalizeSegment(movementSelector(candidate));
                    if (!string.Equals(candMov, normMovement, StringComparison.Ordinal))
                        continue;
                }
            }
            else if (!string.IsNullOrEmpty(normMovement))
            {
                string candMov = NormalizeSegment(movementSelector(candidate));
                if (!string.Equals(candMov, normMovement, StringComparison.Ordinal))
                    continue;
            }

            results.Add(candidate);
        }

        return results;
    }

    public static (List<T> UpdatedQueue, int NewCurrentIndex) ReplaceWorkBlockInQueue<T>(
        IReadOnlyList<T> currentQueue,
        int currentIndex,
        T targetTrack,
        IReadOnlyList<T> targetWorkOrderedTracks,
        Func<T, string> uidSelector,
        Func<T, string?> workSelector,
        Func<T, string?> albumScopeSelector)
    {
        IReadOnlyList<T> replacement = targetWorkOrderedTracks.Count > 0
            ? targetWorkOrderedTracks
            : [targetTrack];
        string targetUid = uidSelector(targetTrack);

        int FindTargetOffset(IReadOnlyList<T> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(uidSelector(list[i]), targetUid, StringComparison.Ordinal))
                    return i;
            }
            return 0;
        }

        if (currentQueue.Count == 0 || currentIndex < 0 || currentIndex >= currentQueue.Count)
        {
            var standalone = replacement.ToList();
            return (standalone, FindTargetOffset(standalone));
        }

        string curWork = NormalizeSegment(workSelector(currentQueue[currentIndex]));
        string curScope = NormalizeSegment(albumScopeSelector(currentQueue[currentIndex]));
        int blockStart = currentIndex;
        int blockEnd = currentIndex;

        if (!string.IsNullOrEmpty(curWork))
        {
            while (blockStart > 0
                   && string.Equals(NormalizeSegment(workSelector(currentQueue[blockStart - 1])), curWork, StringComparison.Ordinal)
                   && string.Equals(NormalizeSegment(albumScopeSelector(currentQueue[blockStart - 1])), curScope, StringComparison.Ordinal))
            {
                blockStart--;
            }

            while (blockEnd + 1 < currentQueue.Count
                   && string.Equals(NormalizeSegment(workSelector(currentQueue[blockEnd + 1])), curWork, StringComparison.Ordinal)
                   && string.Equals(NormalizeSegment(albumScopeSelector(currentQueue[blockEnd + 1])), curScope, StringComparison.Ordinal))
            {
                blockEnd++;
            }
        }

        var updated = new List<T>(currentQueue.Count - (blockEnd - blockStart + 1) + replacement.Count);
        for (int i = 0; i < blockStart; i++) updated.Add(currentQueue[i]);
        updated.AddRange(replacement);
        for (int i = blockEnd + 1; i < currentQueue.Count; i++) updated.Add(currentQueue[i]);

        int newIdx = blockStart + FindTargetOffset(replacement);
        return (updated, newIdx);
    }

    public static long ComputeTargetPositionMs(long currentPositionMs, long currentDurationMs, long targetDurationMs)
    {
        if (currentDurationMs <= 0 || targetDurationMs <= 0) return 0;
        double ratio = Math.Clamp(currentPositionMs / (double)currentDurationMs, 0.0, 1.0);
        return (long)Math.Round(targetDurationMs * ratio);
    }

    private static string NormalizeSegment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return string.Join(' ', raw.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

public static class OledBurnInGuard
{
    private static readonly (int Dx, int Dy)[] OrbitTable =
    [
        (0, 0), (1, 0), (2, 1), (1, 2), (0, 1), (-1, 2),
        (-2, 1), (-1, 0), (-2, -1), (-1, -2), (0, -1), (1, -2), (2, -1), (1, 1)
    ];

    private static readonly (double X, double Y)[] NormalizedOrbit16 =
    [
        (0.0, 0.0), (0.5, 0.0), (1.0, 0.33), (0.83, 0.83),
        (0.33, 1.0), (-0.33, 1.0), (-0.83, 0.83), (-1.0, 0.33),
        (-1.0, -0.33), (-0.83, -0.83), (-0.33, -1.0), (0.33, -1.0),
        (0.83, -0.83), (1.0, -0.33), (0.5, -0.5), (-0.5, 0.5)
    ];

    public static (int Dx, int Dy) ComputePixelOffset(int tickCount)
    {
        int idx = ((tickCount % OrbitTable.Length) + OrbitTable.Length) % OrbitTable.Length;
        return OrbitTable[idx];
    }

    public static (int Dx, int Dy) ComputeOrbitOffset(int tickCount, int amplitudeDip = 12)
    {
        int amp = Math.Max(0, amplitudeDip);
        int idx = ((tickCount % NormalizedOrbit16.Length) + NormalizedOrbit16.Length) % NormalizedOrbit16.Length;
        var (nx, ny) = NormalizedOrbit16[idx];
        return ((int)Math.Round(nx * amp), (int)Math.Round(ny * amp));
    }
}

public static class SessionSlotStore
{
    public static readonly Lock SyncFileLock = new();

    private static readonly (int SlotIndex, string Name, string SceneId)[] Presets =
    [
        (1, "Focus", "focus"),
        (2, "Coding", "coding"),
        (3, "Relax", "relax"),
        (4, "Energy", "energy"),
        (5, "Classical", "all"),
    ];

    public static IReadOnlyList<SessionSlotDto> LoadOrInitializeDefault(string sessionsJsonPath)
    {
        lock (SyncFileLock)
        {
            return LoadOrInitializeDefaultUnlocked(sessionsJsonPath);
        }
    }

    private static IReadOnlyList<SessionSlotDto> LoadOrInitializeDefaultUnlocked(string sessionsJsonPath)
    {
        var byIndex = new Dictionary<int, SessionSlotDto>();
        foreach (var (idx, name, sceneId) in Presets)
        {
            byIndex[idx] = new SessionSlotDto(idx, name, sceneId, 0, [], 0, 0);
        }

        if (File.Exists(sessionsJsonPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<SessionSlotDto>>(File.ReadAllText(sessionsJsonPath, Encoding.UTF8));
                if (loaded is not null)
                {
                    foreach (var s in loaded)
                    {
                        if (s.SlotIndex is >= 1 and <= 5)
                        {
                            byIndex[s.SlotIndex] = s;
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed sessions file and return defaults
            }
        }

        return Enumerable.Range(1, 5).Select(i => byIndex[i]).ToList();
    }

    public static IReadOnlyList<SessionSlotDto> SaveSlot(string sessionsJsonPath, SessionSlotDto updatedSlot)
    {
        lock (SyncFileLock)
        {
            var all = LoadOrInitializeDefaultUnlocked(sessionsJsonPath).ToList();
            int idx = all.FindIndex(s => s.SlotIndex == updatedSlot.SlotIndex);
            if (idx >= 0)
            {
                all[idx] = updatedSlot;
            }
            else
            {
                all.Add(updatedSlot);
            }

            var ordered = all.OrderBy(s => s.SlotIndex).ToList();
            string? dir = Path.GetDirectoryName(sessionsJsonPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
            string tmpPath = sessionsJsonPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
            try
            {
                File.WriteAllText(tmpPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tmpPath, sessionsJsonPath, overwrite: true);
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
            return ordered;
        }
    }
}

public static class ArtistEntitySplitter
{
    private static readonly Regex JrSrSuffixRegex = new(
        @",\s*(?:Jr\.?|Sr\.?)(?=\s*(?:$|[,;/&\0]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed record AliasTable(
        Dictionary<string, string> CanonicalAliases,
        Dictionary<string, IReadOnlyList<string>> MultiValueAliases,
        Dictionary<string, string> ComposerShortAliases);

    private static readonly Lazy<IReadOnlyList<string>> LazyWhitelist = new(() =>
    {
        var asm = typeof(ArtistEntitySplitter).Assembly;
        const string resourceName = "Tonewise.SyncHost.protocol.artist_whitelist.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded whitelist resource '{resourceName}' not found.");
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.TryGetProperty("protected_names", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            return arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderByDescending(s => s.Length)
                .ToList();
        }
        return [];
    });

    private static readonly Lazy<AliasTable> LazyAliases = new(() =>
    {
        var asm = typeof(ArtistEntitySplitter).Assembly;
        const string resourceName = "Tonewise.SyncHost.protocol.artist_aliases.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded alias dictionary resource '{resourceName}' not found.");
        using var doc = JsonDocument.Parse(stream);

        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("canonical_aliases", out var cObj) && cObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in cObj.EnumerateObject())
            {
                string key = NormalizePunctuationAndHygiene(prop.Name);
                string val = NormalizePunctuationAndHygiene(prop.Value.GetString());
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                {
                    canonical[key] = val;
                }
            }
        }

        var multi = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("multi_value_aliases", out var mObj) && mObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in mObj.EnumerateObject())
            {
                string key = NormalizePunctuationAndHygiene(prop.Name);
                if (string.IsNullOrEmpty(key) || prop.Value.ValueKind != JsonValueKind.Array) continue;
                var vals = prop.Value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => NormalizePunctuationAndHygiene(e.GetString()))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
                if (vals.Count > 0)
                {
                    multi[key] = vals;
                }
            }
        }

        var composerShort = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("composer_short_aliases", out var csObj) && csObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in csObj.EnumerateObject())
            {
                string key = NormalizePunctuationAndHygiene(prop.Name);
                string val = NormalizePunctuationAndHygiene(prop.Value.GetString());
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                {
                    composerShort[key] = val;
                }
            }
        }

        return new AliasTable(canonical, multi, composerShort);
    });

    public static IReadOnlyList<string> Whitelist => LazyWhitelist.Value;
    private static AliasTable Aliases => LazyAliases.Value;

    public static IReadOnlyList<IReadOnlyList<string>> GetCanonicalAliasGroups()
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Aliases.CanonicalAliases)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
            {
                continue;
            }
            if (!grouped.TryGetValue(kv.Value, out var list))
            {
                list = [kv.Value];
                grouped[kv.Value] = list;
            }
            if (!list.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(kv.Key);
            }
        }
        return grouped.Values.Where(g => g.Count >= 2).Select(IReadOnlyList<string> (g) => g).ToList();
    }

    public static string NormalizePunctuationAndWhitespace(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        bool prevSpace = true;
        foreach (char ch in raw)
        {
            switch (ch)
            {
                case '\u200B':
                case '\uFEFF':
                    break;
                case '\u2010':
                case '\u2011':
                case '\u2012':
                case '\u2013':
                case '\u2212':
                case '\uFE63':
                case '\uFF0D':
                    sb.Append('-');
                    prevSpace = false;
                    break;
                case '\u2018':
                case '\u2019':
                case '\u201B':
                case '\u02BC':
                case '\uFF07':
                    sb.Append('\'');
                    prevSpace = false;
                    break;
                default:
                    if (char.IsWhiteSpace(ch))
                    {
                        if (!prevSpace)
                        {
                            sb.Append(' ');
                            prevSpace = true;
                        }
                    }
                    else
                    {
                        sb.Append(ch);
                        prevSpace = false;
                    }
                    break;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }
        return sb.ToString();
    }

    public static string NormalizePunctuationAndHygiene(string? raw)
    {
        string s = NormalizePunctuationAndWhitespace(raw);
        if (s.Length == 0) return "";
        while (s.EndsWith("(writer)", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^"(writer)".Length].TrimEnd();
        }
        return s.Trim();
    }

    public static string NormalizePerformerName(string? raw)
    {
        string clean = NormalizePunctuationAndHygiene(raw);
        if (clean.Length == 0) return "";
        return Aliases.CanonicalAliases.TryGetValue(clean, out string? canonical) ? canonical : clean;
    }

    public static string NormalizeComposerName(string? raw)
    {
        string clean = NormalizePunctuationAndHygiene(raw);
        if (clean.Length == 0) return "";
        if (Aliases.ComposerShortAliases.TryGetValue(clean, out string? expanded))
        {
            return Aliases.CanonicalAliases.TryGetValue(expanded, out string? c) ? c : expanded;
        }
        return Aliases.CanonicalAliases.TryGetValue(clean, out string? canonical) ? canonical : clean;
    }

    public static bool TryExpandMultiValueAlias(string? raw, bool composerMode, out IReadOnlyList<string> expanded)
    {
        string clean = NormalizePunctuationAndHygiene(raw);
        if (clean.Length > 0 && Aliases.MultiValueAliases.TryGetValue(clean, out var rawItems))
        {
            var list = new List<string>(rawItems.Count);
            foreach (string item in rawItems)
            {
                string norm = composerMode ? NormalizeComposerName(item) : NormalizePerformerName(item);
                if (!string.IsNullOrEmpty(norm)) list.Add(norm);
            }
            expanded = list;
            return list.Count > 0;
        }
        expanded = [];
        return false;
    }

    public static IReadOnlyList<string> NormalizeEntityArray(IEnumerable<string?> items, bool composerMode = false)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in items)
        {
            string clean = NormalizePunctuationAndHygiene(raw);
            if (clean.Length == 0) continue;
            if (TryExpandMultiValueAlias(clean, composerMode, out var expanded))
            {
                foreach (string e in expanded)
                {
                    if (seen.Add(e)) result.Add(e);
                }
            }
            else
            {
                string norm = composerMode ? NormalizeComposerName(clean) : NormalizePerformerName(clean);
                if (norm.Length > 0 && seen.Add(norm)) result.Add(norm);
            }
        }
        return result;
    }

    private static (string Working, List<(string Token, string Original)> Replacements) ProtectWhitelistAndJrSr(string working)
    {
        var replacements = new List<(string Token, string Original)>();
        int seq = 0;
        var whitelist = Whitelist;
        for (int i = 0; i < whitelist.Count; i++)
        {
            string protectedName = whitelist[i];
            int idx;
            while ((idx = working.IndexOf(protectedName, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                string token = $"__TW_PROTECTED_{i}_{seq++}__";
                string originalSlice = working.Substring(idx, protectedName.Length);
                working = string.Concat(working.AsSpan(0, idx), token, working.AsSpan(idx + protectedName.Length));
                replacements.Add((token, originalSlice));
            }
        }

        working = JrSrSuffixRegex.Replace(working, m =>
        {
            string token = $"__TW_JRSR_{seq++}__";
            replacements.Add((token, m.Value));
            return token;
        });

        return (working, replacements);
    }

    private static string RestorePlaceholders(string s, List<(string Token, string Original)> map)
    {
        string res = s;
        for (int i = map.Count - 1; i >= 0; i--)
        {
            var (token, original) = map[i];
            if (res.Contains(token, StringComparison.Ordinal))
            {
                res = res.Replace(token, original, StringComparison.Ordinal);
            }
        }
        return res.Trim();
    }

    public static string NormalizeAlbumArtist(string? raw)
    {
        string clean = NormalizePunctuationAndHygiene(raw);
        if (clean.Length == 0) return "";
        if (TryExpandMultiValueAlias(clean, composerMode: false, out var fullExpanded))
        {
            return string.Join(", ", fullExpanded);
        }
        if (Aliases.CanonicalAliases.TryGetValue(clean, out string? fullCanonical))
        {
            return fullCanonical;
        }

        var (working, replacements) = ProtectWhitelistAndJrSr(clean);
        string[] delimiters = ["; ", ", ", " & ", ";", ","];
        var sb = new StringBuilder(clean.Length);
        int pos = 0;
        while (pos <= working.Length)
        {
            int bestIdx = -1;
            string? bestDelim = null;
            foreach (string delim in delimiters)
            {
                int found = working.IndexOf(delim, pos, StringComparison.Ordinal);
                if (found >= 0 && (bestIdx < 0 || found < bestIdx || (found == bestIdx && delim.Length > bestDelim!.Length)))
                {
                    bestIdx = found;
                    bestDelim = delim;
                }
            }

            int segEnd = bestIdx >= 0 ? bestIdx : working.Length;
            string segRaw = RestorePlaceholders(working[pos..segEnd], replacements);
            string segClean = NormalizePunctuationAndHygiene(segRaw);
            if (segClean.Length > 0)
            {
                if (TryExpandMultiValueAlias(segClean, composerMode: false, out var segExpanded))
                {
                    sb.Append(string.Join(", ", segExpanded));
                }
                else
                {
                    sb.Append(NormalizePerformerName(segClean));
                }
            }

            if (bestDelim is null) break;
            sb.Append(bestDelim);
            pos = bestIdx + bestDelim.Length;
        }
        return sb.ToString().Trim();
    }

    public static IReadOnlyList<string> SplitPerformers(string? raw) =>
        SplitEntitiesInternal(raw, composerMode: false);

    public static IReadOnlyList<string> SplitComposers(string? raw) =>
        SplitEntitiesInternal(raw, composerMode: true);

    private static IReadOnlyList<string> SplitEntitiesInternal(string? raw, bool composerMode)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        string working = NormalizePunctuationAndWhitespace(raw);
        if (working.Length == 0) return [];

        foreach (string w in Whitelist)
        {
            if (string.Equals(working, w, StringComparison.OrdinalIgnoreCase))
            {
                return [working];
            }
        }

        var (protectedWorking, replacements) = ProtectWhitelistAndJrSr(working);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRestoredSegment(string rawPart)
        {
            string restored = RestorePlaceholders(rawPart, replacements);
            string cleaned = NormalizePunctuationAndHygiene(restored);
            if (cleaned.Length == 0) return;

            if (TryExpandMultiValueAlias(cleaned, composerMode, out var expanded))
            {
                foreach (string item in expanded)
                {
                    if (seen.Add(item)) result.Add(item);
                }
            }
            else
            {
                string norm = composerMode ? NormalizeComposerName(cleaned) : NormalizePerformerName(cleaned);
                if (norm.Length > 0 && seen.Add(norm)) result.Add(norm);
            }
        }

        foreach (string part in protectedWorking.Split(['/', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (part.Contains(" & ", StringComparison.Ordinal))
            {
                foreach (string sub in part.Split([" & "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddRestoredSegment(sub);
                }
            }
            else
            {
                AddRestoredSegment(part);
            }
        }
        return result;
    }
}

public static class AdaptiveClassicalHierarchyPolicy
{
    public const double AutoExpandRatioThreshold = 0.20;

    public static bool ShouldShowSidebarSubItems(
        int classicalTrackCount,
        int totalTrackCount,
        bool alwaysExpandUserSetting,
        bool isSidebarExpanded)
    {
        if (!isSidebarExpanded) return false;
        if (alwaysExpandUserSetting) return true;
        if (totalTrackCount <= 0) return false;
        double ratio = (double)classicalTrackCount / totalTrackCount;
        return ratio >= AutoExpandRatioThreshold;
    }
}

public sealed record ParsedSearchDsl(
    string Kind,
    string Value,
    string? SubTab = null
);

public static class SearchQueryDslParser
{
    public static readonly (string Token, string Description)[] Presets =
    [
        ("scene:focus", "深度专注场景"),
        ("scene:coding", "心流编程场景"),
        ("scene:relax", "午后放松场景"),
        ("scene:energy", "高能律动场景"),
        ("vocals:instrumental", "纯器乐无词"),
        ("vocals:vocal", "含人声曲目"),
        ("energy:low", "低能量环境"),
        ("energy:high", "高能量唤醒"),
        ("classical:works", "古典作品聚合"),
        ("classical:composers", "作曲家索引"),
        ("library:albums", "专辑切片视图"),
    ];

    public static ParsedSearchDsl Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ParsedSearchDsl("text", "");
        }

        string clean = raw.Trim();
        int hintSep = clean.IndexOf(" · ", StringComparison.Ordinal);
        if (hintSep > 0 && clean[..hintSep].Contains(':', StringComparison.Ordinal))
        {
            clean = clean[..hintSep].Trim();
        }

        if (clean.StartsWith("scene:", StringComparison.OrdinalIgnoreCase))
        {
            string val = clean[6..].Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(val)) return new ParsedSearchDsl("scene", val);
        }
        if (clean.StartsWith("vocals:", StringComparison.OrdinalIgnoreCase))
        {
            string val = clean[7..].Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(val)) return new ParsedSearchDsl("vocals", val);
        }
        if (clean.StartsWith("energy:", StringComparison.OrdinalIgnoreCase))
        {
            string val = clean[7..].Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(val)) return new ParsedSearchDsl("energy", val);
        }
        if (clean.StartsWith("classical:", StringComparison.OrdinalIgnoreCase))
        {
            string sub = clean[10..].Trim().ToLowerInvariant();
            return new ParsedSearchDsl("nav", "classical", string.IsNullOrEmpty(sub) ? "works" : sub);
        }
        if (clean.StartsWith("library:", StringComparison.OrdinalIgnoreCase))
        {
            string sub = clean[8..].Trim().ToLowerInvariant();
            return new ParsedSearchDsl("nav", "library", string.IsNullOrEmpty(sub) ? "tracks" : sub);
        }

        foreach (string prefix in new[] { "Work: ", "Composer: ", "Artist: ", "Album: ", "Track: " })
        {
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedSearchDsl("text", clean[prefix.Length..].Trim());
            }
        }

        return new ParsedSearchDsl("text", clean);
    }
}

public sealed record DesktopUserSettings(
    string? LastMusicRoot = null,
    bool PreferWasapiExclusive = true,
    bool IsDacHardwareLocked = false,
    string? SelectedEndpointId = null,
    bool IsIso226Enabled = false,
    double VolumeAttenuationDb = 0.0,
    bool IsTpdfDitherEnabled = true,
    double FirDryWetMix = 1.0,
    string? ActiveEqFilePath = null,
    string? FirImpulseFilePath = null,
    bool AlwaysExpandClassicalSubItems = false,
    bool IsSidebarExpanded = true,
    bool IsInspectorOpen = true,
    string AppTheme = "dark",
    bool IsShuffleEnabled = false,
    string RepeatMode = "all",
    bool EnableAutoWatchLibrary = true,
    string ReplayGainMode = "dynamic",
    double ReplayGainPreAmpWithTagDb = 0.0,
    double ReplayGainPreAmpWithoutTagDb = 0.0,
    bool RewindOnPrevious = true,
    bool RememberPauseOnSkip = false,
    bool PauseOnSingleRepeat = false,
    int ExclusiveIdleReleaseMinutes = 15,
    string? DeviceDspProfilesJson = null,
    int? DeskClockX = null,
    int? DeskClockY = null,
    int? DeskClockWidth = null,
    int? DeskClockHeight = null,
    bool DeskClockAlwaysOnTop = true,
    bool EnableUsbAutoSync = true,
    bool EnableMirrorPrune = true,
    string MirrorPruneScope = "music_folder_all",
    bool AllowAdbForeignDeleteFallback = true,
    string? CustomAdbPath = null
);

public static class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tonewise",
            "desktop_settings.json");

    public static DesktopUserSettings Load(string? settingsPath = null)
    {
        string path = string.IsNullOrWhiteSpace(settingsPath) ? DefaultSettingsPath : settingsPath;
        if (!File.Exists(path))
        {
            return new DesktopUserSettings();
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<DesktopUserSettings>(json, JsonOptions)
                ?? new DesktopUserSettings();
        }
        catch
        {
            return new DesktopUserSettings();
        }
    }

    public static void Save(DesktopUserSettings settings, string? settingsPath = null)
    {
        string path = string.IsNullOrWhiteSpace(settingsPath) ? DefaultSettingsPath : settingsPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..6];
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed record AutoDjTrackCandidate(
    string TrackUid,
    string Title,
    string Artist,
    int EffectivePlayCount,
    int Rating,
    double? TempoBpm,
    string? TempoBucket,
    string? DjEnergy,
    string? DjVocals,
    IReadOnlyList<string> DjScene,
    IReadOnlyList<string> Moods,
    long LastPlayedTsMs = 0,
    string? WorkKey = null,
    int? MovementNumber = null,
    int DiscNumber = 0,
    int TrackNumber = 0,
    string RelPath = ""
);

public sealed record AutoDjTimeProfile(
    string PeriodId,
    string PeriodLabel,
    string Description,
    double TargetBpmCenter,
    double TargetBpmMin,
    double TargetBpmMax,
    string TargetEnergy,
    double TargetValence,
    double TargetArousal
);

public sealed record AutoDjGenerationResult(
    AutoDjTimeProfile Profile,
    IReadOnlyList<string> OrderedTrackUids,
    int FrequentCount,
    int DiscoveryCount,
    double MaxAdjacentBpmDelta,
    string SummaryText
);

/// <summary>
/// Generates a cohesive 50-track AutoDJ mix keyed to local time-of-day acoustic mood/tempo targets:
/// 70% frequently played tracks + 30% unplayed/rarely played tracks, sequenced to avoid jarring tempo or mood jumps.
/// </summary>
public static class AutoDjPlaylistGenerator
{
    public static AutoDjTimeProfile ResolveTimeProfile(DateTime localNow)
    {
        int hour = localNow.Hour;
        return hour switch
        {
            >= 5 and <= 8 => new AutoDjTimeProfile(
                "morning",
                "清晨唤醒",
                "05:00–08:59 · 明快温和开启（中等偏舒缓节奏 · 暖调情绪）",
                TargetBpmCenter: 96,
                TargetBpmMin: 76,
                TargetBpmMax: 116,
                TargetEnergy: "low",
                TargetValence: 0.68,
                TargetArousal: 0.38),
            >= 9 and <= 13 => new AutoDjTimeProfile(
                "day_focus",
                "日间专注",
                "09:00–13:59 · 稳定心流推动（均速稳定律动 · 克制情绪起伏）",
                TargetBpmCenter: 102,
                TargetBpmMin: 82,
                TargetBpmMax: 122,
                TargetEnergy: "medium",
                TargetValence: 0.58,
                TargetArousal: 0.46),
            >= 14 and <= 17 => new AutoDjTimeProfile(
                "afternoon",
                "午后律动",
                "14:00–17:59 · 舒适松弛律动（温和明快 · 中等能量延续）",
                TargetBpmCenter: 108,
                TargetBpmMin: 86,
                TargetBpmMax: 126,
                TargetEnergy: "medium",
                TargetValence: 0.70,
                TargetArousal: 0.52),
            >= 18 and <= 21 => new AutoDjTimeProfile(
                "evening",
                "晚间漫游",
                "18:00–21:59 · 舒缓放松过渡（柔缓中慢板 · 温暖沉浸氛围）",
                TargetBpmCenter: 92,
                TargetBpmMin: 72,
                TargetBpmMax: 114,
                TargetEnergy: "low",
                TargetValence: 0.64,
                TargetArousal: 0.40),
            _ => new AutoDjTimeProfile(
                "late_night",
                "深夜静谧",
                "22:00–04:59 · 沉静低扰伴随（低能量慢板 · 低刺激平滑衔接）",
                TargetBpmCenter: 74,
                TargetBpmMin: 54,
                TargetBpmMax: 96,
                TargetEnergy: "low",
                TargetValence: 0.52,
                TargetArousal: 0.24),
        };
    }

    public static AutoDjGenerationResult Generate(
        IReadOnlyList<AutoDjTrackCandidate> candidates,
        DateTime localNow,
        int targetCount = 50,
        int? randomSeed = null)
    {
        var profile = ResolveTimeProfile(localNow);
        if (candidates.Count == 0 || targetCount <= 0)
        {
            return new AutoDjGenerationResult(
                profile,
                [],
                0,
                0,
                0,
                $"{profile.PeriodLabel} AutoDJ：当前曲库无可编排的本地曲目。");
        }

        int total = Math.Min(targetCount, candidates.Count);
        int targetFrequent = (int)Math.Round(total * 0.70);
        int targetDiscovery = total - targetFrequent;

        var rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        var features = candidates
            .GroupBy(c => c.TrackUid, StringComparer.Ordinal)
            .Select((g, idx) => BuildFeatureNode(g.First(), profile, rng, idx))
            .ToList();

        total = Math.Min(total, features.Count);
        targetFrequent = (int)Math.Round(total * 0.70);
        targetDiscovery = total - targetFrequent;

        var units = BuildAtomicUnits(features);

        // Partition atomic units (solo tracks or complete classical works) so multi-movement works stay together.
        List<UnitNode> freqPool;
        List<UnitNode> discPool;
        int freqAtLeast2 = units.Where(u => u.EffectivePlayCount >= 2).Sum(u => u.Nodes.Count);
        int freqAtLeast1 = units.Where(u => u.EffectivePlayCount >= 1).Sum(u => u.Nodes.Count);

        if (freqAtLeast2 >= targetFrequent)
        {
            freqPool = units.Where(u => u.EffectivePlayCount >= 2).ToList();
            discPool = units.Where(u => u.EffectivePlayCount <= 1).ToList();
        }
        else if (freqAtLeast1 >= targetFrequent)
        {
            freqPool = units.Where(u => u.EffectivePlayCount >= 1).ToList();
            discPool = units.Where(u => u.EffectivePlayCount == 0).ToList();
        }
        else if (freqAtLeast1 > 0)
        {
            freqPool = units.Where(u => u.EffectivePlayCount >= 1).ToList();
            discPool = units.Where(u => u.EffectivePlayCount == 0).ToList();
        }
        else
        {
            var orderedFallback = units.OrderByDescending(u => u.AverageRating).ThenBy(u => u.Jitter).ToList();
            freqPool = [];
            discPool = [];
            int freqTracks = 0;
            foreach (var u in orderedFallback)
            {
                if (freqTracks < targetFrequent && freqTracks + u.Nodes.Count <= total)
                {
                    freqPool.Add(u);
                    freqTracks += u.Nodes.Count;
                }
                else
                {
                    discPool.Add(u);
                }
            }
        }

        foreach (var u in freqPool)
        {
            u.PoolWeight = u.AverageCompatibilityScore
                           + Math.Log10(1.0 + u.EffectivePlayCount) * 1.15
                           + u.AverageRating * 0.12
                           + u.Jitter * 0.40;
            u.IsFrequent = true;
            foreach (var n in u.Nodes)
            {
                n.PoolWeight = u.PoolWeight;
                n.IsFrequent = true;
            }
        }

        foreach (var u in discPool)
        {
            double rarityBonus = u.EffectivePlayCount == 0 ? 1.0 : 0.65;
            u.PoolWeight = u.AverageCompatibilityScore
                           + rarityBonus
                           + (u.AverageRating >= 4.5 ? 0.25 : 0.0)
                           + u.Jitter * 0.45;
            u.IsFrequent = false;
            foreach (var n in u.Nodes)
            {
                n.PoolWeight = u.PoolWeight;
                n.IsFrequent = false;
            }
        }

        var selectedUnits = new List<UnitNode>(units.Count);
        var usedUnitKeys = new HashSet<string>(StringComparer.Ordinal);
        var artistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int selectedTracks = PickCohesiveUnitSubset(
            freqPool, targetFrequent, maxTotalTracks: total,
            selectedUnits, usedUnitKeys, artistCounts, maxPerArtist: 2);
        selectedTracks += PickCohesiveUnitSubset(
            discPool, Math.Min(targetDiscovery, Math.Max(0, total - selectedTracks)), maxTotalTracks: total - selectedTracks,
            selectedUnits, usedUnitKeys, artistCounts, maxPerArtist: 2);

        if (selectedTracks < total)
        {
            var remainingPool = units
                .Where(u => !usedUnitKeys.Contains(u.UnitKey))
                .OrderByDescending(u => u.AverageCompatibilityScore + u.Jitter * 0.35)
                .ToList();
            selectedTracks += PickCohesiveUnitSubset(
                remainingPool, total - selectedTracks, maxTotalTracks: total - selectedTracks,
                selectedUnits, usedUnitKeys, artistCounts, maxPerArtist: 4);
        }

        if (selectedUnits.Count == 0 && units.Count > 0)
        {
            selectedUnits.Add(units.OrderBy(u => u.Nodes.Count).First());
        }

        // Sequence atomic work blocks / solo tracks smoothly without splitting classical movements.
        var sequencedUnits = SequenceUnitsSmoothly(selectedUnits, profile, ratioTarget: 0.70);
        ApplyLocalUnitSmoothingSwaps(sequencedUnits);

        var sequenced = sequencedUnits.SelectMany(u => u.Nodes).ToList();
        if (sequenced.Count > total)
        {
            sequenced = sequenced.Take(total).ToList();
        }

        double maxBpmDelta = 0.0;
        for (int i = 1; i < sequenced.Count; i++)
        {
            double delta = Math.Abs(sequenced[i].Bpm - sequenced[i - 1].Bpm);
            if (delta > maxBpmDelta) maxBpmDelta = delta;
        }

        int finalFreq = sequenced.Count(f => f.IsFrequent);
        int finalDisc = sequenced.Count - finalFreq;
        string summary =
            $"AutoDJ 已按「{profile.PeriodLabel}」生成 {sequenced.Count} 首（常听 {finalFreq} 首 · 探索少听/未听 {finalDisc} 首 · 最大邻曲 BPM 差 {maxBpmDelta:0.#}）";

        return new AutoDjGenerationResult(
            profile,
            sequenced.Select(s => s.Candidate.TrackUid).ToList(),
            finalFreq,
            finalDisc,
            Math.Round(maxBpmDelta, 1),
            summary);
    }

    private sealed class FeatureNode
    {
        public required AutoDjTrackCandidate Candidate { get; init; }
        public int OriginalIndex { get; init; }
        public double Bpm { get; init; }
        public double Arousal { get; init; }
        public double Valence { get; init; }
        public double BaseCompatibilityScore { get; init; }
        public double Jitter { get; init; }
        public double PoolWeight { get; set; }
        public bool IsFrequent { get; set; }
        public string PrimaryArtist { get; init; } = "";
    }

    private sealed class UnitNode
    {
        public required string UnitKey { get; init; }
        public required List<FeatureNode> Nodes { get; init; }
        public FeatureNode EntryNode => Nodes[0];
        public FeatureNode ExitNode => Nodes[^1];
        public int EffectivePlayCount { get; init; }
        public double AverageRating { get; init; }
        public double AverageCompatibilityScore { get; init; }
        public double Jitter { get; init; }
        public double PoolWeight { get; set; }
        public bool IsFrequent { get; set; }
        public string PrimaryArtist { get; init; } = "";
    }

    private static List<UnitNode> BuildAtomicUnits(List<FeatureNode> features)
    {
        var blocksByKey = new Dictionary<string, List<FeatureNode>>(StringComparer.Ordinal);
        var order = new List<string>(features.Count);

        for (int i = 0; i < features.Count; i++)
        {
            var node = features[i];
            string work = node.Candidate.WorkKey?.Trim().ToLowerInvariant() ?? "";
            string unitKey = string.IsNullOrEmpty(work) ? $"track::{node.Candidate.TrackUid}" : $"work::{work}";
            if (!blocksByKey.TryGetValue(unitKey, out var list))
            {
                list = [];
                blocksByKey[unitKey] = list;
                order.Add(unitKey);
            }
            list.Add(node);
        }

        var result = new List<UnitNode>(order.Count);
        foreach (string key in order)
        {
            var list = blocksByKey[key];
            if (list.Count > 1)
            {
                list.Sort((a, b) =>
                {
                    int ma = a.Candidate.MovementNumber is > 0 ? a.Candidate.MovementNumber.Value : int.MaxValue;
                    int mb = b.Candidate.MovementNumber is > 0 ? b.Candidate.MovementNumber.Value : int.MaxValue;
                    int cmp = ma.CompareTo(mb);
                    if (cmp != 0) return cmp;
                    int da = a.Candidate.DiscNumber > 0 ? a.Candidate.DiscNumber : int.MaxValue;
                    int db = b.Candidate.DiscNumber > 0 ? b.Candidate.DiscNumber : int.MaxValue;
                    cmp = da.CompareTo(db);
                    if (cmp != 0) return cmp;
                    int ta = a.Candidate.TrackNumber > 0 ? a.Candidate.TrackNumber : int.MaxValue;
                    int tb = b.Candidate.TrackNumber > 0 ? b.Candidate.TrackNumber : int.MaxValue;
                    cmp = ta.CompareTo(tb);
                    if (cmp != 0) return cmp;
                    cmp = WorkAwareShuffleEngine.NaturalPathCompare(a.Candidate.RelPath, b.Candidate.RelPath);
                    return cmp != 0 ? cmp : a.OriginalIndex.CompareTo(b.OriginalIndex);
                });
            }

            result.Add(new UnitNode
            {
                UnitKey = key,
                Nodes = list,
                EffectivePlayCount = list.Max(n => n.Candidate.EffectivePlayCount),
                AverageRating = list.Average(n => (double)n.Candidate.Rating),
                AverageCompatibilityScore = list.Average(n => n.BaseCompatibilityScore),
                Jitter = list[0].Jitter,
                PrimaryArtist = list[0].PrimaryArtist,
            });
        }
        return result;
    }

    private static FeatureNode BuildFeatureNode(AutoDjTrackCandidate c, AutoDjTimeProfile profile, Random rng, int index)
    {
        double bpm = EstimateBpm(c, profile);
        double arousal = EstimateArousal(c, bpm);
        double valence = EstimateValence(c);

        double bpmDelta = Math.Abs(bpm - profile.TargetBpmCenter);
        double bandPenalty = bpm < profile.TargetBpmMin - 12 || bpm > profile.TargetBpmMax + 12 ? 7.5 : 0.0;
        double compat = 6.0
                        - (bpmDelta / 16.0)
                        - bandPenalty
                        - 4.0 * Math.Abs(arousal - profile.TargetArousal)
                        - 3.5 * Math.Abs(valence - profile.TargetValence);

        string primaryArtist = string.IsNullOrWhiteSpace(c.Artist)
            ? ""
            : ArtistEntitySplitter.SplitPerformers(c.Artist).FirstOrDefault() ?? c.Artist.Trim();

        return new FeatureNode
        {
            Candidate = c,
            OriginalIndex = index,
            Bpm = bpm,
            Arousal = arousal,
            Valence = valence,
            BaseCompatibilityScore = compat,
            Jitter = rng.NextDouble(),
            PrimaryArtist = primaryArtist,
        };
    }

    private static double EstimateBpm(AutoDjTrackCandidate c, AutoDjTimeProfile profile)
    {
        if (c.TempoBpm is >= 30 and <= 240)
            return c.TempoBpm.Value;

        if (!string.IsNullOrWhiteSpace(c.TempoBucket))
        {
            return c.TempoBucket.Trim() switch
            {
                "Very Slow" => 58.0,
                "Slow" => 78.0,
                "Moderate" => 105.0,
                "Fast" => 135.0,
                "Very Fast" => 160.0,
                _ => profile.TargetBpmCenter,
            };
        }

        return (c.DjEnergy ?? "").ToLowerInvariant() switch
        {
            "low" => profile.TargetBpmCenter - 8.0,
            "high" => profile.TargetBpmCenter + 12.0,
            _ => profile.TargetBpmCenter,
        };
    }

    private static double EstimateArousal(AutoDjTrackCandidate c, double bpm)
    {
        double energyScore = (c.DjEnergy ?? "").ToLowerInvariant() switch
        {
            "low" => 0.24,
            "medium" => 0.52,
            "high" => 0.84,
            _ => 0.48,
        };
        double bpmNorm = Math.Clamp((bpm - 55.0) / 100.0, 0.0, 1.0);
        return Math.Clamp(energyScore * 0.60 + bpmNorm * 0.40, 0.0, 1.0);
    }

    private static double EstimateValence(AutoDjTrackCandidate c)
    {
        double v = 0.55;
        int moodHits = 0;
        foreach (string raw in c.Moods.Concat(c.DjScene))
        {
            string m = raw.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(m)) continue;
            if (m is "warm" or "romantic" or "cheerful" or "bright" or "uplifting" or "groovy" or "happy")
            {
                v += 0.22;
                moodHits++;
            }
            else if (m is "calm" or "chill" or "mellow" or "soothing" or "peaceful" or "relax" or "dreamy")
            {
                v += 0.10;
                moodHits++;
            }
            else if (m is "focus" or "coding" or "contemplative")
            {
                v += 0.02;
                moodHits++;
            }
            else if (m is "melancholic" or "sad" or "dark" or "somber" or "tragic" or "aggressive" or "dramatic" or "energetic")
            {
                v -= 0.18;
                moodHits++;
            }
        }
        return moodHits > 0 ? Math.Clamp(v, 0.15, 0.95) : 0.55;
    }

    private static int PickCohesiveUnitSubset(
        List<UnitNode> pool,
        int neededTracks,
        int maxTotalTracks,
        List<UnitNode> dest,
        HashSet<string> usedUnitKeys,
        Dictionary<string, int> artistCounts,
        int maxPerArtist)
    {
        int cap = Math.Min(neededTracks, maxTotalTracks);
        if (cap <= 0 || pool.Count == 0) return 0;
        var ranked = pool.OrderByDescending(u => u.PoolWeight).ToList();
        int pickedTracks = 0;

        for (int pass = 0; pass < 2 && pickedTracks < cap; pass++)
        {
            bool enforceArtistCap = pass == 0;
            foreach (var unit in ranked)
            {
                if (pickedTracks >= cap) break;
                if (usedUnitKeys.Contains(unit.UnitKey)) continue;
                int unitLen = unit.Nodes.Count;
                if (pickedTracks + unitLen > maxTotalTracks) continue;
                if (pickedTracks > 0 && pickedTracks + unitLen > cap && unitLen > 1 && pickedTracks + 1 >= cap) continue;
                if (enforceArtistCap && !string.IsNullOrEmpty(unit.PrimaryArtist)
                    && artistCounts.TryGetValue(unit.PrimaryArtist, out int cnt)
                    && cnt >= maxPerArtist)
                {
                    continue;
                }

                usedUnitKeys.Add(unit.UnitKey);
                if (!string.IsNullOrEmpty(unit.PrimaryArtist))
                {
                    artistCounts[unit.PrimaryArtist] = artistCounts.GetValueOrDefault(unit.PrimaryArtist) + 1;
                }
                dest.Add(unit);
                pickedTracks += unitLen;
            }
        }
        return pickedTracks;
    }

    private static List<UnitNode> SequenceUnitsSmoothly(
        List<UnitNode> selected,
        AutoDjTimeProfile profile,
        double ratioTarget)
    {
        if (selected.Count <= 2) return [.. selected];

        var remaining = new List<UnitNode>(selected);
        var result = new List<UnitNode>(selected.Count);

        var first = remaining
            .OrderBy(u => u.IsFrequent ? 0 : 1)
            .ThenBy(u => Math.Abs(u.EntryNode.Bpm - profile.TargetBpmCenter) + 12.0 * Math.Abs(u.EntryNode.Arousal - profile.TargetArousal))
            .First();
        remaining.Remove(first);
        result.Add(first);
        int tracksSoFar = first.Nodes.Count;
        int freqSoFar = first.IsFrequent ? first.Nodes.Count : 0;

        while (remaining.Count > 0)
        {
            var prev = result[^1];
            UnitNode? best = null;
            double bestCost = double.MaxValue;

            foreach (var cand in remaining)
            {
                int projTotal = tracksSoFar + cand.Nodes.Count;
                int projFreq = freqSoFar + (cand.IsFrequent ? cand.Nodes.Count : 0);
                double cadencePenalty = Math.Abs(projFreq - (ratioTarget * projTotal)) * 0.45;
                double cost = ComputeTransitionCost(prev.ExitNode, cand.EntryNode) + cadencePenalty;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = cand;
                }
            }

            result.Add(best!);
            remaining.Remove(best!);
            tracksSoFar += best!.Nodes.Count;
            if (best.IsFrequent) freqSoFar += best.Nodes.Count;
        }

        return result;
    }

    private static double ComputeTransitionCost(FeatureNode a, FeatureNode b)
    {
        double bpmDiff = Math.Abs(a.Bpm - b.Bpm);
        double bpmCost = (bpmDiff / 10.0) + (bpmDiff > 14.0 ? (bpmDiff - 14.0) * 0.35 : 0.0);
        double arousalCost = Math.Abs(a.Arousal - b.Arousal) * 4.5;
        double valenceCost = Math.Abs(a.Valence - b.Valence) * 4.0;
        double sameArtistCost = !string.IsNullOrEmpty(a.PrimaryArtist)
                                && string.Equals(a.PrimaryArtist, b.PrimaryArtist, StringComparison.OrdinalIgnoreCase)
            ? 2.8
            : 0.0;
        return bpmCost + arousalCost + valenceCost + sameArtistCost;
    }

    private static double SquaredPairCost(FeatureNode a, FeatureNode b)
    {
        double c = ComputeTransitionCost(a, b);
        return c * c;
    }

    private static void ApplyLocalUnitSmoothingSwaps(List<UnitNode> seq)
    {
        int n = seq.Count;
        if (n < 4) return;
        for (int pass = 0; pass < 3; pass++)
        {
            bool improved = false;
            for (int i = 1; i < n - 2; i++)
            {
                for (int j = i + 1; j < n - 1; j++)
                {
                    if (seq[i].IsFrequent != seq[j].IsFrequent) continue;

                    double before =
                        SquaredPairCost(seq[i - 1].ExitNode, seq[i].EntryNode)
                        + (j == i + 1
                            ? SquaredPairCost(seq[i].ExitNode, seq[j].EntryNode)
                            : SquaredPairCost(seq[i].ExitNode, seq[i + 1].EntryNode) + SquaredPairCost(seq[j - 1].ExitNode, seq[j].EntryNode))
                        + SquaredPairCost(seq[j].ExitNode, seq[j + 1].EntryNode);

                    double after =
                        SquaredPairCost(seq[i - 1].ExitNode, seq[j].EntryNode)
                        + (j == i + 1
                            ? SquaredPairCost(seq[j].ExitNode, seq[i].EntryNode)
                            : SquaredPairCost(seq[j].ExitNode, seq[i + 1].EntryNode) + SquaredPairCost(seq[j - 1].ExitNode, seq[i].EntryNode))
                        + SquaredPairCost(seq[i].ExitNode, seq[j + 1].EntryNode);

                    if (after + 1e-6 < before)
                    {
                        (seq[i], seq[j]) = (seq[j], seq[i]);
                        improved = true;
                    }
                }
            }
            if (!improved) break;
        }
    }
}

public sealed record PlaybackSessionSnapshot(
    IReadOnlyList<string> QueueTrackUids,
    string? CurrentTrackUid,
    double PositionSeconds,
    string QueueTitle,
    bool IsShuffled,
    string RepeatMode,
    int ActiveSlotIndex,
    long SavedAtUnixMs
);

public static class PlaybackSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetSessionFilePath(string musicRoot) =>
        Path.Combine(musicRoot, ".tonewise", "desktop_playback_session.json");

    public static PlaybackSessionSnapshot? Load(string musicRoot)
    {
        if (string.IsNullOrWhiteSpace(musicRoot)) return null;
        string path = GetSessionFilePath(musicRoot);
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<PlaybackSessionSnapshot>(json, JsonOptions);
            return parsed is not null && parsed.QueueTrackUids is { Count: > 0 } ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string musicRoot, PlaybackSessionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(musicRoot)) return;
        string path = GetSessionFilePath(musicRoot);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..6];
        string json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(tmp, json + "\n", new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Clear(string musicRoot)
    {
        if (string.IsNullOrWhiteSpace(musicRoot)) return;
        string path = GetSessionFilePath(musicRoot);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }
}

public sealed record ShuffleTrackDescriptor(
    string TrackUid,
    string Album,
    string WorkName,
    int? MovementNumber,
    string RelPath,
    string AlbumScope = "",
    int DiscNumber = 0,
    int TrackNumber = 0
);

/// <summary>
/// Work-preserving deterministic/Fisher-Yates shuffle engine matching Android `WorkShuffleOrder`.
/// Classical works (`WorkName` on the same `Album`) stay grouped and movement-ordered as atomic blocks.
/// </summary>
public static class WorkAwareShuffleEngine
{
    public static int[] BuildWorkAwareOrder(
        IReadOnlyList<ShuffleTrackDescriptor> tracks,
        int anchorIndex,
        Random? random = null)
    {
        if (tracks.Count == 0) return [];
        var rng = random ?? Random.Shared;

        var blockOrder = new List<string>();
        var blocksByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            string? blockKey = BuildWorkBlockKey(t.WorkName, t.Album, t.AlbumScope);
            string key = blockKey ?? $"track::{i}";
            if (!blocksByKey.TryGetValue(key, out var list))
            {
                list = [];
                blocksByKey[key] = list;
                blockOrder.Add(key);
            }
            list.Add(i);
        }

        var orderedBlocks = new List<List<int>>(blockOrder.Count);
        foreach (string key in blockOrder)
        {
            var indices = blocksByKey[key];
            if (indices.Count > 1)
            {
                indices.Sort((a, b) =>
                {
                    int ma = tracks[a].MovementNumber is > 0 ? tracks[a].MovementNumber!.Value : int.MaxValue;
                    int mb = tracks[b].MovementNumber is > 0 ? tracks[b].MovementNumber!.Value : int.MaxValue;
                    int cmp = ma.CompareTo(mb);
                    if (cmp != 0) return cmp;
                    int da = tracks[a].DiscNumber > 0 ? tracks[a].DiscNumber : int.MaxValue;
                    int db = tracks[b].DiscNumber > 0 ? tracks[b].DiscNumber : int.MaxValue;
                    cmp = da.CompareTo(db);
                    if (cmp != 0) return cmp;
                    int ta = tracks[a].TrackNumber > 0 ? tracks[a].TrackNumber : int.MaxValue;
                    int tb = tracks[b].TrackNumber > 0 ? tracks[b].TrackNumber : int.MaxValue;
                    cmp = ta.CompareTo(tb);
                    if (cmp != 0) return cmp;
                    cmp = NaturalPathCompare(tracks[a].RelPath, tracks[b].RelPath);
                    return cmp != 0 ? cmp : a.CompareTo(b);
                });
            }
            orderedBlocks.Add(indices);
        }

        List<int>? anchorBlock = null;
        if (anchorIndex >= 0 && anchorIndex < tracks.Count)
        {
            int anchorBlockIdx = orderedBlocks.FindIndex(b => b.Contains(anchorIndex));
            if (anchorBlockIdx >= 0)
            {
                anchorBlock = orderedBlocks[anchorBlockIdx];
                orderedBlocks.RemoveAt(anchorBlockIdx);
            }
        }

        for (int i = orderedBlocks.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (orderedBlocks[i], orderedBlocks[j]) = (orderedBlocks[j], orderedBlocks[i]);
        }

        var flattened = new List<int>(tracks.Count);
        if (anchorBlock is not null)
        {
            flattened.AddRange(anchorBlock);
        }
        foreach (var block in orderedBlocks)
        {
            flattened.AddRange(block);
        }
        return [.. flattened];
    }

    public static string BuildAlbumScope(string? album, string? conductor = null, string? albumArtist = null)
    {
        string normAlbum = (album ?? "").Trim().ToLowerInvariant();
        string normConductor = (conductor ?? "").Trim().ToLowerInvariant();
        string normArtist = (albumArtist ?? "").Trim().ToLowerInvariant();
        string discriminator = !string.IsNullOrEmpty(normConductor) ? normConductor : normArtist;
        if (string.IsNullOrEmpty(discriminator)) return normAlbum;
        return string.IsNullOrEmpty(normAlbum) ? discriminator : $"{normAlbum}|{discriminator}";
    }

    public static string? BuildWorkBlockKey(string? workName, string? album, string? albumScope = null)
    {
        string work = (workName ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(work)) return null;
        string scope = !string.IsNullOrWhiteSpace(albumScope)
            ? albumScope.Trim().ToLowerInvariant()
            : (album ?? "").Trim().ToLowerInvariant();
        return $"{scope}::{work}";
    }

    public static int NaturalPathCompare(string? a, string? b)
    {
        string sa = a ?? "";
        string sb = b ?? "";
        int ia = 0, ib = 0;
        while (ia < sa.Length && ib < sb.Length)
        {
            if (char.IsDigit(sa[ia]) && char.IsDigit(sb[ib]))
            {
                int startA = ia, startB = ib;
                while (ia < sa.Length && char.IsDigit(sa[ia])) ia++;
                while (ib < sb.Length && char.IsDigit(sb[ib])) ib++;
                string digitsA = sa[startA..ia].TrimStart('0');
                string digitsB = sb[startB..ib].TrimStart('0');
                int lenCmp = digitsA.Length.CompareTo(digitsB.Length);
                if (lenCmp != 0) return lenCmp;
                int numCmp = string.Compare(digitsA, digitsB, StringComparison.Ordinal);
                if (numCmp != 0) return numCmp;
                int zeroCmp = (ia - startA).CompareTo(ib - startB);
                if (zeroCmp != 0) return zeroCmp;
            }
            else
            {
                int charCmp = char.ToLowerInvariant(sa[ia]).CompareTo(char.ToLowerInvariant(sb[ib]));
                if (charCmp != 0) return charCmp;
                ia++;
                ib++;
            }
        }
        return (sa.Length - ia).CompareTo(sb.Length - ib);
    }

    public static void AvoidLeadingRepeatPreservingWorkBlock<T>(
        List<T> ordered,
        string? previousTrackUid,
        Func<T, string> uidSelector,
        Func<T, string?> albumSelector,
        Func<T, string?> workSelector,
        Func<T, string?>? albumScopeSelector = null,
        string? previousWorkKey = null)
    {
        if (ordered.Count <= 1 || string.IsNullOrWhiteSpace(previousTrackUid)) return;
        string? leadWorkKey = BuildWorkBlockKey(
            workSelector(ordered[0]),
            albumSelector(ordered[0]),
            albumScopeSelector?.Invoke(ordered[0]));
        bool sameLeadTrack = string.Equals(uidSelector(ordered[0]), previousTrackUid, StringComparison.Ordinal);
        bool sameLeadWork = !string.IsNullOrWhiteSpace(previousWorkKey)
            && !string.IsNullOrEmpty(leadWorkKey)
            && string.Equals(leadWorkKey, previousWorkKey.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!sameLeadTrack && !sameLeadWork) return;

        string firstWork = (workSelector(ordered[0]) ?? "").Trim();
        string firstScope = ((albumScopeSelector?.Invoke(ordered[0]) ?? albumSelector(ordered[0])) ?? "").Trim();
        int blockLen = 1;
        if (!string.IsNullOrEmpty(firstWork))
        {
            while (blockLen < ordered.Count
                   && string.Equals((workSelector(ordered[blockLen]) ?? "").Trim(), firstWork, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(((albumScopeSelector?.Invoke(ordered[blockLen]) ?? albumSelector(ordered[blockLen])) ?? "").Trim(), firstScope, StringComparison.OrdinalIgnoreCase))
            {
                blockLen++;
            }
        }

        if (blockLen >= ordered.Count)
        {
            // Entire queue is one single multi-movement work; move only the lead item if unavoidable
            var single = ordered[0];
            ordered.RemoveAt(0);
            ordered.Add(single);
            return;
        }

        var moved = ordered.GetRange(0, blockLen);
        ordered.RemoveRange(0, blockLen);
        ordered.AddRange(moved);
    }
}

public static class ReplayGainResolver
{
    public static bool ShouldUseAlbumReplayGain(
        bool isShuffleEnabled,
        int queueCount,
        int currentIndex,
        Func<int, string?> albumAt,
        Func<int, string?> workKeyAt,
        string? currentAlbum,
        string? currentWorkKey,
        bool isSingleAlbumParent = false)
    {
        if (queueCount <= 0) return false;
        string normAlbum = (currentAlbum ?? "").Trim();
        string normWork = (currentWorkKey ?? "").Trim();
        if (string.IsNullOrEmpty(normAlbum)) return false;

        if (!string.IsNullOrEmpty(normWork))
        {
            if (currentIndex >= 0 && currentIndex < queueCount)
            {
                bool prevSame = currentIndex > 0
                    && string.Equals((workKeyAt(currentIndex - 1) ?? "").Trim(), normWork, StringComparison.OrdinalIgnoreCase);
                bool nextSame = currentIndex + 1 < queueCount
                    && string.Equals((workKeyAt(currentIndex + 1) ?? "").Trim(), normWork, StringComparison.OrdinalIgnoreCase);
                if (prevSame || nextSame) return true;
            }
            else
            {
                int sameWorkCount = 0;
                int cap = Math.Min(queueCount, 256);
                for (int i = 0; i < cap; i++)
                {
                    if (string.Equals((workKeyAt(i) ?? "").Trim(), normWork, StringComparison.OrdinalIgnoreCase))
                    {
                        sameWorkCount++;
                        if (sameWorkCount >= 2) return true;
                    }
                }
            }
        }

        if (isShuffleEnabled) return false;
        if (isSingleAlbumParent) return true;

        int checkCap = Math.Min(queueCount, 256);
        int sameAlbumCount = 0;
        for (int i = 0; i < checkCap; i++)
        {
            if (string.Equals((albumAt(i) ?? "").Trim(), normAlbum, StringComparison.OrdinalIgnoreCase))
                sameAlbumCount++;
        }
        return sameAlbumCount >= checkCap;
    }

    public static double ResolveGainDb(
        string? mode,
        double? trackDb,
        double? albumDb,
        bool isAlbumContext,
        double preAmpWithTagDb = 0.0,
        double preAmpWithoutTagDb = 0.0)
    {
        string normalized = (mode ?? "dynamic").Trim().ToLowerInvariant();
        if (normalized == "off") return 0.0;

        double? resolved = normalized switch
        {
            "track" => trackDb ?? albumDb,
            "album" => albumDb ?? trackDb,
            _ => isAlbumContext ? (albumDb ?? trackDb) : (trackDb ?? albumDb),
        };

        return resolved.HasValue
            ? resolved.Value + preAmpWithTagDb
            : preAmpWithoutTagDb;
    }
}
