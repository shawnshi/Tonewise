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

public static class ArtistEntitySplitter
{
    private static readonly HashSet<string> ProtectedBandWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Above & Beyond",
        "Simon & Garfunkel",
        "Hall & Oates",
        "Iron & Wine",
        "Angus & Julia Stone",
        "Florence + The Machine",
        "Mumford & Sons",
        "Belle & Sebastian",
    };

    public static IReadOnlyList<string> SplitPerformers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        string trimmed = raw.Trim();
        if (ProtectedBandWhitelist.Contains(trimmed))
        {
            return [trimmed];
        }

        var result = new List<string>();
        foreach (string part in trimmed.Split(['/', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (ProtectedBandWhitelist.Contains(part))
            {
                result.Add(part);
                continue;
            }
            if (part.Contains(" & ", StringComparison.Ordinal) &&
                !ProtectedBandWhitelist.Any(b => part.Contains(b, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string sub in part.Split([" & "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!string.IsNullOrWhiteSpace(sub)) result.Add(sub);
                }
            }
            else
            {
                result.Add(part);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
    string? DeviceDspProfilesJson = null
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
    long LastPlayedTsMs = 0
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
            .Select(g => BuildFeatureNode(g.First(), profile, rng))
            .ToList();

        total = Math.Min(total, features.Count);
        targetFrequent = (int)Math.Round(total * 0.70);
        targetDiscovery = total - targetFrequent;

        // Partition frequent (>=2 plays, or fallback >=1 plays) vs discovery (unplayed / low-play).
        List<FeatureNode> freqPool;
        List<FeatureNode> discPool;
        int freqAtLeast2 = features.Count(f => f.Candidate.EffectivePlayCount >= 2);
        int freqAtLeast1 = features.Count(f => f.Candidate.EffectivePlayCount >= 1);

        if (freqAtLeast2 >= targetFrequent)
        {
            freqPool = features.Where(f => f.Candidate.EffectivePlayCount >= 2).ToList();
            discPool = features.Where(f => f.Candidate.EffectivePlayCount <= 1).ToList();
        }
        else if (freqAtLeast1 >= targetFrequent)
        {
            freqPool = features.Where(f => f.Candidate.EffectivePlayCount >= 1).ToList();
            discPool = features.Where(f => f.Candidate.EffectivePlayCount == 0).ToList();
        }
        else if (freqAtLeast1 > 0)
        {
            freqPool = features.Where(f => f.Candidate.EffectivePlayCount >= 1).ToList();
            discPool = features.Where(f => f.Candidate.EffectivePlayCount == 0).ToList();
        }
        else
        {
            var orderedFallback = features.OrderByDescending(f => f.Candidate.Rating).ThenBy(f => f.Jitter).ToList();
            freqPool = orderedFallback.Take(targetFrequent).ToList();
            discPool = orderedFallback.Skip(targetFrequent).ToList();
        }

        foreach (var f in freqPool)
        {
            f.PoolWeight = f.BaseCompatibilityScore
                           + Math.Log10(1.0 + f.Candidate.EffectivePlayCount) * 1.15
                           + f.Candidate.Rating * 0.12
                           + f.Jitter * 0.40;
            f.IsFrequent = true;
        }

        foreach (var f in discPool)
        {
            double rarityBonus = f.Candidate.EffectivePlayCount == 0 ? 1.0 : 0.65;
            f.PoolWeight = f.BaseCompatibilityScore
                           + rarityBonus
                           + (f.Candidate.Rating == 5 ? 0.25 : 0.0)
                           + f.Jitter * 0.45;
            f.IsFrequent = false;
        }

        var selected = new List<FeatureNode>(total);
        var usedUids = new HashSet<string>(StringComparer.Ordinal);
        var artistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int freqPicked = PickCohesiveSubset(freqPool, targetFrequent, selected, usedUids, artistCounts, maxPerArtist: 2);
        int discPicked = PickCohesiveSubset(discPool, targetDiscovery, selected, usedUids, artistCounts, maxPerArtist: 2);

        if (selected.Count < total)
        {
            var remainingPool = features
                .Where(f => !usedUids.Contains(f.Candidate.TrackUid))
                .OrderByDescending(f => f.BaseCompatibilityScore + f.Jitter * 0.35)
                .ToList();
            PickCohesiveSubset(remainingPool, total - selected.Count, selected, usedUids, artistCounts, maxPerArtist: 4);
        }

        // Sequence selected items along a smooth BPM + arousal + valence curve.
        var sequenced = SequenceSmoothly(selected, profile, ratioTarget: 0.70);
        ApplyLocalSmoothingSwaps(sequenced);

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
        public double Bpm { get; init; }
        public double Arousal { get; init; }
        public double Valence { get; init; }
        public double BaseCompatibilityScore { get; init; }
        public double Jitter { get; init; }
        public double PoolWeight { get; set; }
        public bool IsFrequent { get; set; }
        public string PrimaryArtist { get; init; } = "";
    }

    private static FeatureNode BuildFeatureNode(AutoDjTrackCandidate c, AutoDjTimeProfile profile, Random rng)
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
                "Very Slow" => 66.0,
                "Slow" => 80.0,
                "Moderate" => 100.0,
                "Fast" => 122.0,
                "Very Fast" => 144.0,
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

    private static int PickCohesiveSubset(
        List<FeatureNode> pool,
        int needed,
        List<FeatureNode> dest,
        HashSet<string> usedUids,
        Dictionary<string, int> artistCounts,
        int maxPerArtist)
    {
        if (needed <= 0 || pool.Count == 0) return 0;
        var ranked = pool.OrderByDescending(f => f.PoolWeight).ToList();
        int picked = 0;

        for (int pass = 0; pass < 2 && picked < needed; pass++)
        {
            bool enforceArtistCap = pass == 0;
            foreach (var node in ranked)
            {
                if (picked >= needed) break;
                if (usedUids.Contains(node.Candidate.TrackUid)) continue;
                if (enforceArtistCap && !string.IsNullOrEmpty(node.PrimaryArtist)
                    && artistCounts.TryGetValue(node.PrimaryArtist, out int cnt)
                    && cnt >= maxPerArtist)
                {
                    continue;
                }

                usedUids.Add(node.Candidate.TrackUid);
                if (!string.IsNullOrEmpty(node.PrimaryArtist))
                {
                    artistCounts[node.PrimaryArtist] = artistCounts.GetValueOrDefault(node.PrimaryArtist) + 1;
                }
                dest.Add(node);
                picked++;
            }
        }
        return picked;
    }

    private static List<FeatureNode> SequenceSmoothly(
        List<FeatureNode> selected,
        AutoDjTimeProfile profile,
        double ratioTarget)
    {
        if (selected.Count <= 2) return [.. selected];

        var remaining = new List<FeatureNode>(selected);
        var result = new List<FeatureNode>(selected.Count);

        var first = remaining
            .OrderBy(n => n.IsFrequent ? 0 : 1)
            .ThenBy(n => Math.Abs(n.Bpm - profile.TargetBpmCenter) + 12.0 * Math.Abs(n.Arousal - profile.TargetArousal))
            .First();
        remaining.Remove(first);
        result.Add(first);
        int freqSoFar = first.IsFrequent ? 1 : 0;

        while (remaining.Count > 0)
        {
            var prev = result[^1];
            double nextTargetRatio = ratioTarget * (result.Count + 1);
            FeatureNode? best = null;
            double bestCost = double.MaxValue;

            foreach (var cand in remaining)
            {
                int projFreq = freqSoFar + (cand.IsFrequent ? 1 : 0);
                double cadencePenalty = Math.Abs(projFreq - nextTargetRatio) * 0.45;
                double cost = ComputeTransitionCost(prev, cand) + cadencePenalty;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = cand;
                }
            }

            result.Add(best!);
            remaining.Remove(best!);
            if (best!.IsFrequent) freqSoFar++;
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

    private static void ApplyLocalSmoothingSwaps(List<FeatureNode> seq)
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
                        SquaredPairCost(seq[i - 1], seq[i])
                        + (j == i + 1
                            ? SquaredPairCost(seq[i], seq[j])
                            : SquaredPairCost(seq[i], seq[i + 1]) + SquaredPairCost(seq[j - 1], seq[j]))
                        + SquaredPairCost(seq[j], seq[j + 1]);

                    double after =
                        SquaredPairCost(seq[i - 1], seq[j])
                        + (j == i + 1
                            ? SquaredPairCost(seq[j], seq[i])
                            : SquaredPairCost(seq[j], seq[i + 1]) + SquaredPairCost(seq[j - 1], seq[i]))
                        + SquaredPairCost(seq[i], seq[j + 1]);

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
    string RelPath
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
            string work = t.WorkName?.Trim().ToLowerInvariant() ?? "";
            string key = string.IsNullOrEmpty(work)
                ? $"track::{i}"
                : $"work::{(t.Album ?? "").Trim().ToLowerInvariant()}::{work}";
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
                    int ma = tracks[a].MovementNumber ?? int.MaxValue;
                    int mb = tracks[b].MovementNumber ?? int.MaxValue;
                    int cmp = ma.CompareTo(mb);
                    if (cmp != 0) return cmp;
                    cmp = string.Compare(tracks[a].RelPath, tracks[b].RelPath, StringComparison.OrdinalIgnoreCase);
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
}

public static class ReplayGainResolver
{
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
