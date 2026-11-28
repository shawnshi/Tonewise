namespace Tonewise.SyncHost.Workbench;

public sealed record ClassicalTrackSeed<TTrack>(
    TTrack Track,
    string TrackUid,
    string Title,
    string Album,
    string ArtistsDisplay,
    string ComposersDisplay,
    string ConductorsDisplay,
    string? WorkName,
    string? MovementName,
    int? MovementNumber,
    int DiscNumber,
    int TrackNumber,
    int? Year,
    string? Genre,
    long DurationMs
);

public sealed record ClassicalMovementNode<TTrack>(
    int Index,
    string RomanNumeral,
    string DisplayTitle,
    string DurationDisplay,
    string TrackUid,
    TTrack Track
);

public sealed record ClassicalRecordingNode<TTrack>(
    string ScopeKey,
    string BadgeLabel,
    string ConductorLabel,
    string EnsembleLabel,
    int? Year,
    IReadOnlyList<ClassicalMovementNode<TTrack>> Movements
);

public sealed record ClassicalWorkNode<TTrack>(
    string Key,
    string WorkTitle,
    string ComposerName,
    string Form,
    string VersionCountLabel,
    IReadOnlyList<ClassicalRecordingNode<TTrack>> Recordings
);

public sealed record ClassicalComposerNode<TTrack>(
    string ComposerName,
    string DisplayLowercase,
    string? Era,
    string Initial,
    int WorkCount,
    int RecordingCount,
    string StatsLine,
    string? LocalPortraitPath,
    IReadOnlyList<ClassicalWorkNode<TTrack>> Works
);

/// <summary>
/// Builds the two-stage classical Composer -> Work -> Recording -> Movement hierarchy cleanly,
/// matching Android `ClassicalHierarchyLogic` and `ClassicalMetadataEngine`.
/// </summary>
public static class ClassicalHierarchyBuilder
{
    public static readonly string[] EraKeys = ["baroque", "classical", "romantic", "modern"];
    public static readonly string[] FormFilters = ["all", "symphonies", "concertos", "chamber", "solo"];
    public static readonly string[] JumpMatrixKeys =
    [
        "#", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
    ];

    public static string NormalizeInitial(string? composerName)
    {
        string trimmed = (composerName ?? "").Trim();
        foreach (char ch in trimmed)
        {
            if (char.IsWhiteSpace(ch)) continue;
            char lower = char.ToLowerInvariant(ch);
            return lower is >= 'a' and <= 'z' ? lower.ToString() : "#";
        }
        return "#";
    }

    public static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0) return "0:00";
        long totalSeconds = durationMs / 1000L;
        long hours = totalSeconds / 3600L;
        long minutes = (totalSeconds % 3600L) / 60L;
        long seconds = totalSeconds % 60L;
        return hours > 0
            ? $"{hours}:{minutes:D2}:{seconds:D2}"
            : $"{minutes}:{seconds:D2}";
    }

    public static string? ResolveLocalComposerPortrait(string? composerName, IEnumerable<string>? candidateDirs)
    {
        if (string.IsNullOrWhiteSpace(composerName) || candidateDirs is null) return null;
        string clean = composerName.Trim();
        string slug = new string(clean.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        string[] exts = [".jpg", ".jpeg", ".png", ".webp"];
        foreach (string dir in candidateDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (string ext in exts)
            {
                string exact = Path.Combine(dir, clean + ext);
                if (File.Exists(exact)) return exact;
                if (!string.IsNullOrEmpty(slug))
                {
                    string slugFile = Path.Combine(dir, slug + ext);
                    if (File.Exists(slugFile)) return slugFile;
                }
            }
        }
        return null;
    }

    public static IReadOnlyList<ClassicalComposerNode<TTrack>> Build<TTrack>(
        IEnumerable<ClassicalTrackSeed<TTrack>> tracks,
        Func<string?, IReadOnlyList<string>> splitComposers,
        Func<string?, IReadOnlyList<string>> splitPerformers,
        Func<string, string, string> buildRecordingScope,
        IEnumerable<string>? portraitDirectories = null)
    {
        var composerSplitCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        IReadOnlyList<string> SplitComposersCached(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            if (!composerSplitCache.TryGetValue(raw, out var cached))
            {
                cached = splitComposers(raw);
                composerSplitCache[raw] = cached;
            }
            return cached;
        }

        var performerSplitCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        IReadOnlyList<string> SplitPerformersCached(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            if (!performerSplitCache.TryGetValue(raw, out var cached))
            {
                cached = splitPerformers(raw);
                performerSplitCache[raw] = cached;
            }
            return cached;
        }

        List<string>? existingPortraitDirs = null;
        if (portraitDirectories is not null)
        {
            foreach (string dir in portraitDirectories)
            {
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    existingPortraitDirs ??= [];
                    if (!existingPortraitDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    {
                        existingPortraitDirs.Add(dir);
                    }
                }
            }
        }

        var byWorkKey = new Dictionary<string, (string WorkTitle, string Composer, string? Genre, List<(ClassicalTrackSeed<TTrack> Seed, ParsedWorkMovement Parsed)> Items)>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in tracks)
        {
            var composers = SplitComposersCached(seed.ComposersDisplay);
            string? firstComposer = composers.FirstOrDefault();
            var conductors = SplitPerformersCached(seed.ConductorsDisplay);
            string? firstConductor = conductors.FirstOrDefault();

            if (!ClassicalMetadataEngine.IsClassicalTrack(
                    seed.Genre,
                    seed.WorkName,
                    firstComposer,
                    firstConductor,
                    seed.Title))
            {
                continue;
            }

            var parsed = ClassicalMetadataEngine.SplitWorkAndMovement(
                seed.Title,
                seed.WorkName,
                seed.MovementName,
                seed.MovementNumber);
            string workTitle = !string.IsNullOrWhiteSpace(parsed.Work)
                ? parsed.Work
                : (!string.IsNullOrWhiteSpace(seed.Title) ? seed.Title : seed.Album);
            workTitle = workTitle.Trim();
            if (workTitle.Length == 0) continue;

            string composer = firstComposer
                ?? SplitPerformersCached(seed.ArtistsDisplay).FirstOrDefault()
                ?? "Unknown";
            composer = composer.Trim();
            string workKey = $"{composer.ToLowerInvariant()}::{workTitle.ToLowerInvariant()}";

            if (!byWorkKey.TryGetValue(workKey, out var bucket))
            {
                bucket = (workTitle, composer, seed.Genre, []);
                byWorkKey[workKey] = bucket;
            }
            bucket.Items.Add((seed, parsed));
        }

        var worksByComposer = new Dictionary<string, List<ClassicalWorkNode<TTrack>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in byWorkKey)
        {
            var (workTitle, composer, genre, items) = kv.Value;
            var recordings = BuildRecordings(items, buildRecordingScope, SplitPerformersCached);
            string form = ClassicalMetadataEngine.ClassifyWorkForm(workTitle, genre);
            string versionLabel = recordings.Count > 1 ? $"{recordings.Count} versions" : "1 version";
            var workNode = new ClassicalWorkNode<TTrack>(
                Key: kv.Key,
                WorkTitle: workTitle,
                ComposerName: composer,
                Form: form,
                VersionCountLabel: versionLabel,
                Recordings: recordings);

            if (!worksByComposer.TryGetValue(composer, out var wlist))
            {
                wlist = [];
                worksByComposer[composer] = wlist;
            }
            wlist.Add(workNode);
        }

        var result = new List<ClassicalComposerNode<TTrack>>(worksByComposer.Count);
        foreach (var kv in worksByComposer.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sortedWorks = kv.Value.OrderBy(w => w.WorkTitle, StringComparer.OrdinalIgnoreCase).ToList();
            string? era = ClassicalMetadataEngine.ClassifyComposerEra(kv.Key, null);
            int totalRecs = sortedWorks.Sum(w => Math.Max(1, w.Recordings.Count));
            string stats = $"{sortedWorks.Count} works · {totalRecs} recordings";
            string statsLine = string.IsNullOrEmpty(era) ? stats : $"{era} · {stats}";
            string? portrait = existingPortraitDirs is not null
                ? ResolveLocalComposerPortrait(kv.Key, existingPortraitDirs)
                : null;

            result.Add(new ClassicalComposerNode<TTrack>(
                ComposerName: kv.Key,
                DisplayLowercase: kv.Key.ToLowerInvariant(),
                Era: era,
                Initial: NormalizeInitial(kv.Key),
                WorkCount: sortedWorks.Count,
                RecordingCount: totalRecs,
                StatsLine: statsLine,
                LocalPortraitPath: portrait,
                Works: sortedWorks));
        }

        return result;
    }

    /// <summary>
    /// Builds an equivalent two-stage classical hierarchy keyed by performing ensemble or soloist,
    /// stripping non-classical tracks and composer credits mistakenly tagged as track artists.
    /// </summary>
    public static IReadOnlyList<ClassicalComposerNode<TTrack>> BuildByEnsemble<TTrack>(
        IEnumerable<ClassicalTrackSeed<TTrack>> tracks,
        Func<string?, IReadOnlyList<string>> splitComposers,
        Func<string?, IReadOnlyList<string>> splitPerformers,
        Func<string, string, string> buildRecordingScope,
        IEnumerable<string>? portraitDirectories = null)
    {
        var composerSplitCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        IReadOnlyList<string> SplitComposersCached(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            if (!composerSplitCache.TryGetValue(raw, out var cached))
            {
                cached = splitComposers(raw);
                composerSplitCache[raw] = cached;
            }
            return cached;
        }

        var performerSplitCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        IReadOnlyList<string> SplitPerformersCached(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            if (!performerSplitCache.TryGetValue(raw, out var cached))
            {
                cached = splitPerformers(raw);
                performerSplitCache[raw] = cached;
            }
            return cached;
        }

        var byEnsembleWorkKey = new Dictionary<string, (
            string Ensemble,
            string WorkKey,
            string WorkTitle,
            string Composer,
            string? Genre,
            List<(ClassicalTrackSeed<TTrack> Seed, ParsedWorkMovement Parsed)> Items)>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in tracks)
        {
            var composers = SplitComposersCached(seed.ComposersDisplay);
            string? firstComposer = composers.FirstOrDefault();
            var conductors = SplitPerformersCached(seed.ConductorsDisplay);
            string? firstConductor = conductors.FirstOrDefault();

            if (!ClassicalMetadataEngine.IsClassicalTrack(
                    seed.Genre,
                    seed.WorkName,
                    firstComposer,
                    firstConductor,
                    seed.Title))
            {
                continue;
            }

            var performers = SplitPerformersCached(seed.ArtistsDisplay);
            if (performers.Count == 0) continue;

            var composerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in composers)
            {
                string trimmedC = c.Trim();
                if (trimmedC.Length > 0)
                {
                    composerKeys.Add(trimmedC);
                    string surname = ClassicalMetadataEngine.ShortSurname(trimmedC);
                    if (!string.IsNullOrWhiteSpace(surname))
                    {
                        composerKeys.Add(surname);
                    }
                }
            }

            var nonComposerPerformers = new List<string>(performers.Count);
            foreach (string rawPerf in performers)
            {
                string p = rawPerf.Trim();
                if (p.Length == 0) continue;
                bool isComposerCredit = composerKeys.Contains(p)
                    || (composers.Count > 0
                        && composerKeys.Contains(ClassicalMetadataEngine.ShortSurname(p))
                        && !string.IsNullOrEmpty(ClassicalMetadataEngine.ClassifyComposerEra(p, null)))
                    || !string.IsNullOrEmpty(ClassicalMetadataEngine.ClassifyComposerEra(p, null));
                if (!isComposerCredit)
                {
                    nonComposerPerformers.Add(p);
                }
            }
            if (nonComposerPerformers.Count == 0) continue;

            List<string> survivingEnsembles;
            if (conductors.Count > 0)
            {
                var conductorSet = new HashSet<string>(
                    conductors.Select(c => c.Trim()).Where(c => c.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                var withoutConductors = nonComposerPerformers.Where(p => !conductorSet.Contains(p)).ToList();
                survivingEnsembles = withoutConductors.Count > 0 ? withoutConductors : nonComposerPerformers;
            }
            else
            {
                survivingEnsembles = nonComposerPerformers;
            }

            var parsed = ClassicalMetadataEngine.SplitWorkAndMovement(
                seed.Title,
                seed.WorkName,
                seed.MovementName,
                seed.MovementNumber);
            string workTitle = !string.IsNullOrWhiteSpace(parsed.Work)
                ? parsed.Work
                : (!string.IsNullOrWhiteSpace(seed.Title) ? seed.Title : seed.Album);
            workTitle = workTitle.Trim();
            if (workTitle.Length == 0) continue;

            string composer = !string.IsNullOrWhiteSpace(firstComposer) ? firstComposer.Trim() : "Unknown";
            string workKey = $"{composer.ToLowerInvariant()}::{workTitle.ToLowerInvariant()}";

            var seenPerformer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string ensemble in survivingEnsembles)
            {
                if (!seenPerformer.Add(ensemble)) continue;
                string bucketKey = $"{ensemble.ToLowerInvariant()}||{workKey}";
                if (!byEnsembleWorkKey.TryGetValue(bucketKey, out var bucket))
                {
                    bucket = (ensemble, workKey, workTitle, composer, seed.Genre, []);
                    byEnsembleWorkKey[bucketKey] = bucket;
                }
                bucket.Items.Add((seed, parsed));
            }
        }

        var worksByEnsemble = new Dictionary<string, (string EnsembleName, List<ClassicalWorkNode<TTrack>> Works)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in byEnsembleWorkKey)
        {
            var (ensemble, workKey, workTitle, composer, genre, items) = kv.Value;
            var recordings = BuildRecordings(items, buildRecordingScope, SplitPerformersCached);
            string form = ClassicalMetadataEngine.ClassifyWorkForm(workTitle, genre);
            string versionLabel = recordings.Count > 1 ? $"{recordings.Count} versions" : "1 version";

            string displayWorkTitle = workTitle;
            if (!string.Equals(composer, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                string shortComposer = ClassicalMetadataEngine.ShortSurname(composer);
                bool alreadyAttributed = (!string.IsNullOrWhiteSpace(shortComposer)
                        && workTitle.StartsWith(shortComposer, StringComparison.OrdinalIgnoreCase))
                    || workTitle.StartsWith(composer, StringComparison.OrdinalIgnoreCase);
                if (!alreadyAttributed && !string.IsNullOrWhiteSpace(shortComposer))
                {
                    displayWorkTitle = $"{shortComposer} — {workTitle}";
                }
            }

            var workNode = new ClassicalWorkNode<TTrack>(
                Key: $"{ensemble.ToLowerInvariant()}::{workKey}",
                WorkTitle: displayWorkTitle,
                ComposerName: composer,
                Form: form,
                VersionCountLabel: versionLabel,
                Recordings: recordings);

            if (!worksByEnsemble.TryGetValue(ensemble, out var entry))
            {
                entry = (ensemble, []);
                worksByEnsemble[ensemble] = entry;
            }
            entry.Works.Add(workNode);
        }

        var result = new List<ClassicalComposerNode<TTrack>>(worksByEnsemble.Count);
        foreach (var kv in worksByEnsemble.OrderBy(k => k.Value.EnsembleName, StringComparer.OrdinalIgnoreCase))
        {
            string ensembleName = kv.Value.EnsembleName;
            var sortedWorks = kv.Value.Works.OrderBy(w => w.WorkTitle, StringComparer.OrdinalIgnoreCase).ToList();
            int composerCount = sortedWorks
                .Select(w => w.ComposerName)
                .Where(c => !string.IsNullOrWhiteSpace(c) && !string.Equals(c, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int totalRecs = sortedWorks.Sum(w => Math.Max(1, w.Recordings.Count));
            string statsLine = composerCount > 0
                ? $"{composerCount} composers · {sortedWorks.Count} works · {totalRecs} recordings"
                : $"{sortedWorks.Count} works · {totalRecs} recordings";

            result.Add(new ClassicalComposerNode<TTrack>(
                ComposerName: ensembleName,
                DisplayLowercase: ensembleName.ToLowerInvariant(),
                Era: null,
                Initial: NormalizeInitial(ensembleName),
                WorkCount: sortedWorks.Count,
                RecordingCount: totalRecs,
                StatsLine: statsLine,
                LocalPortraitPath: null,
                Works: sortedWorks));
        }

        return result;
    }

    private static List<ClassicalRecordingNode<TTrack>> BuildRecordings<TTrack>(
        List<(ClassicalTrackSeed<TTrack> Seed, ParsedWorkMovement Parsed)> items,
        Func<string, string, string> buildRecordingScope,
        Func<string?, IReadOnlyList<string>> splitPerformersCached)
    {
        var recMap = new Dictionary<string, List<(ClassicalTrackSeed<TTrack> Seed, ParsedWorkMovement Parsed)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            string scope = buildRecordingScope(item.Seed.Album, item.Seed.ConductorsDisplay);
            if (!recMap.TryGetValue(scope, out var list))
            {
                list = [];
                recMap[scope] = list;
            }
            list.Add(item);
        }

        var recordings = new List<ClassicalRecordingNode<TTrack>>(recMap.Count);
        foreach (var recKv in recMap)
        {
            var ordered = recKv.Value
                .OrderBy(x => x.Seed.DiscNumber > 0 ? x.Seed.DiscNumber : int.MaxValue)
                .ThenBy(x => x.Parsed.MovementNumber ?? int.MaxValue)
                .ThenBy(x => x.Seed.TrackNumber > 0 ? x.Seed.TrackNumber : int.MaxValue)
                .ThenBy(x => x.Seed.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var first = ordered[0].Seed;
            string conductorLabel = string.Join(", ", ordered
                .SelectMany(x => splitPerformersCached(x.Seed.ConductorsDisplay))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            string ensembleLabel = string.Join(", ", ordered
                .SelectMany(x => splitPerformersCached(x.Seed.ArtistsDisplay))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            int? year = ordered.Select(x => x.Seed.Year).Where(y => y is > 0).Min();

            string who = !string.IsNullOrWhiteSpace(conductorLabel)
                ? conductorLabel
                : (!string.IsNullOrWhiteSpace(ensembleLabel) ? ensembleLabel : first.Album);
            var badgeParts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(who)) badgeParts.Add(ClassicalMetadataEngine.ShortSurname(who));
            if (year is > 0) badgeParts.Add(year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string badgeLabel = string.Join(" · ", badgeParts).ToLowerInvariant();
            if (string.IsNullOrEmpty(badgeLabel)) badgeLabel = first.Album.ToLowerInvariant();

            var movements = new List<ClassicalMovementNode<TTrack>>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var (seed, parsed) = ordered[i];
                int num = parsed.MovementNumber ?? (i + 1);
                string roman = $"{ClassicalMetadataEngine.FormatRomanNumeral(num)}.";
                string displayTitle = !string.IsNullOrWhiteSpace(parsed.Movement) ? parsed.Movement : seed.Title;
                movements.Add(new ClassicalMovementNode<TTrack>(
                    Index: i,
                    RomanNumeral: roman,
                    DisplayTitle: displayTitle,
                    DurationDisplay: FormatDuration(seed.DurationMs),
                    TrackUid: seed.TrackUid,
                    Track: seed.Track));
            }

            recordings.Add(new ClassicalRecordingNode<TTrack>(
                ScopeKey: recKv.Key,
                BadgeLabel: badgeLabel,
                ConductorLabel: conductorLabel,
                EnsembleLabel: ensembleLabel,
                Year: year,
                Movements: movements));
        }

        recordings.Sort((a, b) =>
        {
            int yrCmp = (a.Year ?? int.MaxValue).CompareTo(b.Year ?? int.MaxValue);
            if (yrCmp != 0) return yrCmp;
            return StringComparer.OrdinalIgnoreCase.Compare(a.BadgeLabel, b.BadgeLabel);
        });
        return recordings;
    }

    public static IReadOnlyList<ClassicalComposerNode<TTrack>> FilterByEra<TTrack>(
        IReadOnlyList<ClassicalComposerNode<TTrack>> composers,
        string? era)
    {
        if (string.IsNullOrWhiteSpace(era)) return composers;
        string norm = era.Trim().ToLowerInvariant();
        return composers.Where(c => string.Equals(c.Era, norm, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static IReadOnlyList<ClassicalWorkNode<TTrack>> FilterByForm<TTrack>(
        IReadOnlyList<ClassicalWorkNode<TTrack>> works,
        string? form)
    {
        if (string.IsNullOrWhiteSpace(form) || string.Equals(form.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return works;
        }
        string norm = form.Trim().ToLowerInvariant();
        return works.Where(w => string.Equals(w.Form, norm, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}

/// <summary>
/// Pure session-scoped classical shuffle lock & recording queue slicer matching Android `ClassicalSessionShuffleLock`.
/// </summary>
public sealed class ClassicalSessionShuffleLock
{
    public bool IsLocked { get; private set; }
    public bool? SavedGlobalShuffle { get; private set; }

    public bool Engage(bool currentGlobalShuffle)
    {
        if (!IsLocked)
        {
            SavedGlobalShuffle = currentGlobalShuffle;
            IsLocked = true;
        }
        return false;
    }

    public (bool WasLocked, bool RestoredShuffle) ReleaseOnNonClassical()
    {
        if (!IsLocked) return (false, false);
        bool restored = SavedGlobalShuffle ?? false;
        IsLocked = false;
        SavedGlobalShuffle = null;
        return (true, restored);
    }

    public void ClearOnUserToggle()
    {
        IsLocked = false;
        SavedGlobalShuffle = null;
    }

    public static IReadOnlyList<T> SliceRecordingMovements<T>(IReadOnlyList<T> movements, int startIndex = 0)
    {
        if (movements.Count == 0) return [];
        int safeStart = Math.Clamp(startIndex, 0, movements.Count - 1);
        return movements.Skip(safeStart).ToList();
    }
}
