using System.Text;
using System.Text.Json;
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
    bool TempoNullPassthrough = true
);

public static class FiveDimQueryBuilder
{
    public static bool RequiresFilterOrSort(FiveDimFilterQuery query) =>
        query.Scene != "all"
        || query.Energy != "all"
        || query.Vocals != "all"
        || query.ExcludeVocal
        || query.TempoBucket != "all"
        || query.MoodCluster != "all"
        || !string.Equals(query.SortArc, "flat", StringComparison.OrdinalIgnoreCase);

    public static string BuildSceneCardJson(FiveDimFilterQuery query)
    {
        var predicates = new List<object>();

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

public static class ClassicalAbSwitchHelper
{
    public static IReadOnlyList<T> FindAlternateRecordings<T>(
        T current,
        IEnumerable<T> library,
        Func<T, string> uidSelector,
        Func<T, string?> workSelector,
        Func<T, string?> movementSelector)
    {
        string targetUid = uidSelector(current);
        string normWork = NormalizeSegment(workSelector(current));
        if (string.IsNullOrEmpty(normWork)) return [];

        string normMovement = NormalizeSegment(movementSelector(current));
        var results = new List<T>();

        foreach (var candidate in library)
        {
            if (string.Equals(uidSelector(candidate), targetUid, StringComparison.Ordinal))
                continue;

            string candWork = NormalizeSegment(workSelector(candidate));
            if (!string.Equals(candWork, normWork, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrEmpty(normMovement))
            {
                string candMov = NormalizeSegment(movementSelector(candidate));
                if (!string.Equals(candMov, normMovement, StringComparison.Ordinal))
                    continue;
            }

            results.Add(candidate);
        }

        return results;
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
        (-2, 1), (-1, 0), (-2, -1), (-1, -2), (0, -1), (1, -2), (2, -1), (1, 0)
    ];

    public static (int Dx, int Dy) ComputePixelOffset(int tickCount)
    {
        int idx = ((tickCount % OrbitTable.Length) + OrbitTable.Length) % OrbitTable.Length;
        return OrbitTable[idx];
    }
}

public static class SessionSlotStore
{
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
        var all = LoadOrInitializeDefault(sessionsJsonPath).ToList();
        int idx = all.FindIndex(s => s.SlotIndex == updatedSlot.SlotIndex);
        if (idx >= 0)
        {
            all[idx] = updatedSlot;
        }
        else
        {
            all.Add(updatedSlot);
        }

        string? dir = Path.GetDirectoryName(sessionsJsonPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(all.OrderBy(s => s.SlotIndex).ToList(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(sessionsJsonPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return all.OrderBy(s => s.SlotIndex).ToList();
    }
}
