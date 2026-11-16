using System.Globalization;
using System.Text.Json;
using Tonewise.Interop;

namespace Tonewise.SyncHost.Curation;

public sealed record AcousticFeatureVector(
    string TrackUid,
    double? InstrumentalProbability,
    double? Bpm,
    double? BpmConfidence,
    double? Arousal,
    double? Valence,
    double? IntegratedLufs,
    string? TopGenre = null,
    IReadOnlyList<(string Mood, double Score)>? MoodHeads = null
);

public sealed record Acoustic5DResult(
    string? DjVocals,
    double VocalsConfidence,
    double? TempoBpm,
    string? TempoBucket,
    double TempoConfidence,
    string? DjEnergy,
    double EnergyConfidence,
    IReadOnlyList<string> Moods,
    double MoodConfidence,
    string? Genre,
    IReadOnlyList<string> DjScenes,
    double SceneConfidence
);

public sealed record TrackCurationSnapshot(
    string TrackUid,
    string? Album,
    int? Year,
    string? Publisher,
    string? Genre,
    string? DjVocals,
    string? DjEnergy,
    double? TempoBpm,
    string? TempoBucket,
    string? MoodsJson,
    string? DjSceneJson
);

/// <summary>
/// Infers 5D extended metadata from local acoustic feature vectors and computes
/// `DJ_SCENE` deterministically from `(DJ_VOCALS, DJ_ENERGY, TempoBucket, Mood)` rather than black-box guessing.
/// </summary>
public static class Acoustic5DInferenceService
{
    private static readonly HashSet<string> CalmOrFocusMoods = new(StringComparer.OrdinalIgnoreCase)
    {
        "calm", "chill", "mellow", "soothing", "peaceful", "serene",
        "tranquil", "gentle", "relaxed", "ambient", "meditative", "warm", "contemplative", "introspective"
    };

    private static readonly HashSet<string> EnergeticMoods = new(StringComparer.OrdinalIgnoreCase)
    {
        "intense", "energetic", "dramatic", "driving", "epic", "aggressive", "passionate", "dynamic", "urgent"
    };

    private static readonly HashSet<string> PopBrightMoods = new(StringComparer.OrdinalIgnoreCase)
    {
        "cheerful", "happy", "upbeat", "bright", "sunny", "playful", "joyful", "optimistic", "uplifting"
    };

    public static string? MapBpmToTempoBucket(double? bpm)
    {
        if (bpm is null || !double.IsFinite(bpm.Value) || bpm.Value <= 0.0) return null;
        return bpm.Value switch
        {
            < 65.0 => "Very Slow",
            < 90.0 => "Slow",
            < 120.0 => "Moderate",
            < 150.0 => "Fast",
            _ => "Very Fast",
        };
    }

    public static Acoustic5DResult InferTrack5D(AcousticFeatureVector f)
    {
        // 1. DJ_VOCALS via probability thresholds (> 0.80 instrumental, < 0.25 vocal, middle uncertain)
        string? djVocals = null;
        double vocalsConf = 0.0;
        if (f.InstrumentalProbability is { } pInst)
        {
            if (pInst > 0.80)
            {
                djVocals = "instrumental";
                vocalsConf = Math.Clamp(pInst, 0.85, 0.99);
            }
            else if (pInst < 0.25)
            {
                djVocals = "vocal";
                vocalsConf = Math.Clamp(1.0 - pInst, 0.85, 0.99);
            }
            else
            {
                djVocals = "unknown";
                vocalsConf = 0.55; // Flagged for human staging review
            }
        }

        // 2. Tempo BPM + Canonical Bucket (`Very Slow`..`Very Fast`)
        double? tempoBpm = null;
        string? tempoBucket = null;
        double tempoConf = 0.0;
        if (f.Bpm is > 20.0 and < 300.0 && (f.BpmConfidence ?? 0.0) >= 0.75)
        {
            tempoBpm = Math.Round(f.Bpm.Value, 1);
            tempoBucket = MapBpmToTempoBucket(tempoBpm);
            tempoConf = Math.Clamp(f.BpmConfidence!.Value, 0.75, 0.99);
        }

        // 3. DJ_ENERGY from Arousal + Integrated LUFS
        string? djEnergy = null;
        double energyConf = 0.0;
        if (f.Arousal is { } arousal)
        {
            double lufs = f.IntegratedLufs ?? -14.0;
            if (arousal >= 0.68 || (arousal >= 0.55 && lufs >= -9.5))
            {
                djEnergy = "high";
                energyConf = 0.88;
            }
            else if (arousal <= 0.35 && lufs <= -14.0)
            {
                djEnergy = "low";
                energyConf = 0.88;
            }
            else
            {
                djEnergy = "medium";
                energyConf = 0.84;
            }
        }

        // 4. Moods (top mood heads >= 0.65, else Arousal/Valence quadrant)
        var moods = new List<string>();
        double moodConf = 0.0;
        if (f.MoodHeads is { Count: > 0 })
        {
            foreach (var (m, score) in f.MoodHeads.OrderByDescending(x => x.Score))
            {
                if (score >= 0.65 && !string.IsNullOrWhiteSpace(m))
                {
                    moods.Add(m.Trim());
                    moodConf = Math.Max(moodConf, score);
                }
            }
        }
        if (moods.Count == 0 && f.Arousal is { } a && f.Valence is { } v)
        {
            moodConf = 0.72;
            if (a <= 0.42 && v >= 0.45) moods.Add("Calm");
            else if (a <= 0.42 && v < 0.45) moods.Add("Contemplative");
            else if (a >= 0.68) moods.Add("Energetic");
            else if (v >= 0.65) moods.Add("Cheerful");
            else moods.Add("Warm");
        }

        // 5. Deterministic DJ_SCENE derivation matrix
        var scenes = DeriveDjScenesDeterministic(djVocals, djEnergy, tempoBucket, moods);
        double sceneConf = scenes.Count > 0 ? Math.Min(0.92, Math.Max(vocalsConf, energyConf)) : 0.0;

        return new Acoustic5DResult(
            DjVocals: djVocals,
            VocalsConfidence: vocalsConf,
            TempoBpm: tempoBpm,
            TempoBucket: tempoBucket,
            TempoConfidence: tempoConf,
            DjEnergy: djEnergy,
            EnergyConfidence: energyConf,
            Moods: moods,
            MoodConfidence: moodConf,
            Genre: f.TopGenre,
            DjScenes: scenes,
            SceneConfidence: sceneConf
        );
    }

    public static IReadOnlyList<string> DeriveDjScenesDeterministic(
        string? vocals,
        string? energy,
        string? tempoBucket,
        IReadOnlyList<string> moods)
    {
        var scenes = new List<string>();
        bool hasCalmFocusMood = moods.Any(m => CalmOrFocusMoods.Contains(m));
        bool hasEnergeticMood = moods.Any(m => EnergeticMoods.Contains(m));
        bool hasBrightPopMood = moods.Any(m => PopBrightMoods.Contains(m));

        // Focus & Coding: instrumental + low/medium energy + non-frenetic tempo + calm/introspective mood
        if (string.Equals(vocals, "instrumental", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(energy, "low", StringComparison.OrdinalIgnoreCase)
                || string.Equals(energy, "medium", StringComparison.OrdinalIgnoreCase))
            && tempoBucket is null or "Very Slow" or "Slow" or "Moderate"
            && (hasCalmFocusMood || moods.Count == 0))
        {
            scenes.Add("focus");
            scenes.Add("coding");
        }

        // Relax: low energy + slow/very slow tempo or calm/relaxed moods
        if (string.Equals(energy, "low", StringComparison.OrdinalIgnoreCase)
            && (tempoBucket is "Very Slow" or "Slow" || hasCalmFocusMood))
        {
            scenes.Add("relax");
        }

        // Energy: high energy + fast/very fast tempo or high-tension mood
        if (string.Equals(energy, "high", StringComparison.OrdinalIgnoreCase)
            && (tempoBucket is "Fast" or "Very Fast" || hasEnergeticMood))
        {
            scenes.Add("energy");
        }

        // Pop: vocal + medium/high energy + bright/joyful mood
        if (string.Equals(vocals, "vocal", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(energy, "medium", StringComparison.OrdinalIgnoreCase)
                || string.Equals(energy, "high", StringComparison.OrdinalIgnoreCase))
            && hasBrightPopMood)
        {
            scenes.Add("pop");
        }

        return scenes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<TagDiffProposal> GenerateMissing5DProposals(
        TrackCurationSnapshot existing,
        AcousticFeatureVector features,
        bool forceOverwriteUnlocked = false)
    {
        var inferred = InferTrack5D(features);
        var list = new List<TagDiffProposal>();

        void AddIfProposed(string field, string? orig, string? suggested, double conf, string src)
        {
            if (string.IsNullOrWhiteSpace(suggested)) return;
            var p = StagingDiffStore.TryCreateProposal(
                existing.TrackUid, field, orig, suggested, conf, src, forceOverwriteUnlocked
            );
            if (p is not null) list.Add(p);
        }

        AddIfProposed("dj_vocals", existing.DjVocals, inferred.DjVocals, inferred.VocalsConfidence, "Acoustic5D-Vocals");
        if (inferred.TempoBpm is { } bpm)
        {
            AddIfProposed("tempo_bpm", existing.TempoBpm?.ToString(CultureInfo.InvariantCulture), bpm.ToString("0.0", CultureInfo.InvariantCulture), inferred.TempoConfidence, "Acoustic5D-TempoCNN");
        }
        AddIfProposed("tempo_bucket", existing.TempoBucket, inferred.TempoBucket, inferred.TempoConfidence, "Acoustic5D-TempoBucket");
        AddIfProposed("dj_energy", existing.DjEnergy, inferred.DjEnergy, inferred.EnergyConfidence, "Acoustic5D-EnergyLUFS");
        if (inferred.Moods.Count > 0)
        {
            AddIfProposed("mood_json", existing.MoodsJson, JsonSerializer.Serialize(inferred.Moods), inferred.MoodConfidence, "Acoustic5D-Mood");
        }
        AddIfProposed("genre", existing.Genre, inferred.Genre, 0.85, "Acoustic5D-Genre");
        if (inferred.DjScenes.Count > 0)
        {
            AddIfProposed("dj_scene_json", existing.DjSceneJson, JsonSerializer.Serialize(inferred.DjScenes), inferred.SceneConfidence, "Acoustic5D-RuleMatrix");
        }

        return list;
    }

    /// <summary>
    /// Decodes a real audio file via Rust `tonewise-dsp-core` (`symphonia`) to extract BPM,
    /// ReplayGain LUFS proxy, and 150-point waveform dynamics before running 5D inference.
    /// </summary>
    public static Acoustic5DResult AnalyzeFileAndInfer5D(
        string trackUid,
        string audioFilePath,
        bool isClassicalHint = false)
    {
        var analysis = TonewiseCoreClient.AnalyzeAudioFile(audioFilePath);
        double rgDb = analysis.ReplayGainTrackDb ?? 0.0;
        double lufs = Math.Clamp(-18.0 - rgDb, -30.0, -4.0);

        double crestVariance = 0.0;
        if (analysis.Waveform150.Count > 2)
        {
            double mean = analysis.Waveform150.Average(b => b / 255.0);
            double variance = analysis.Waveform150.Average(b => Math.Pow(b / 255.0 - mean, 2.0));
            crestVariance = Math.Sqrt(variance);
        }

        double arousal = Math.Clamp(0.45 + (lufs + 15.0) * 0.035 + crestVariance * 0.6, 0.15, 0.95);
        double? instProb = isClassicalHint ? 0.90 : null;
        double? bpmConf = analysis.EstimatedBpm.HasValue ? 0.85 : null;
        string? topGenre = isClassicalHint ? "Classical" : null;

        var vec = new AcousticFeatureVector(
            TrackUid: trackUid,
            InstrumentalProbability: instProb,
            Bpm: analysis.EstimatedBpm,
            BpmConfidence: bpmConf,
            Arousal: arousal,
            Valence: 0.52,
            IntegratedLufs: lufs,
            TopGenre: topGenre
        );
        return InferTrack5D(vec);
    }
}
