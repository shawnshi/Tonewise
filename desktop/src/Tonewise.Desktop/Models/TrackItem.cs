using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Tonewise.Desktop.Models;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        bool any = false;
        foreach (var item in items)
        {
            Items.Add(item);
            any = true;
        }
        if (!any) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void InsertRange(int index, IEnumerable<T> items)
    {
        CheckReentrancy();
        int cursor = Math.Clamp(index, 0, Items.Count);
        bool any = false;
        foreach (var item in items)
        {
            Items.Insert(cursor++, item);
            any = true;
        }
        if (!any) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed record TrackItem(
    string TrackUid,
    string AudioFrameBlake3Hex,
    string RelPath,
    string FullPath,
    string Title,
    string Album,
    string ArtistsDisplay,
    string ComposersDisplay,
    string ConductorsDisplay,
    string WorkDisplay,
    string Genre,
    int? Year,
    int SampleRateHz,
    int DurationMs,
    double? ReplayGainTrackDb,
    IReadOnlyList<string> DjScene,
    string? DjVocals,
    string? DjEnergy,
    double? TempoBpm,
    string? TempoBucket,
    IReadOnlyList<string> Moods,
    int Rating,
    long LastPlayedTsMs,
    string WorkName = "",
    string MovementName = "",
    string Publisher = "",
    int EffectivePlayCount = 0,
    string AlbumDirectory = "",
    string? CoverArtPath = null,
    string? BookletPdfPath = null,
    int? MovementNumber = null,
    double? ReplayGainAlbumDb = null,
    int DiscNumber = 0,
    int TrackNumber = 0
)
{
    public bool IsLoved => Rating >= 5;
    public static bool IsClassicalGenre(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return false;
        string g = genre.Trim();
        return g.Contains("Classical", StringComparison.OrdinalIgnoreCase)
               || g.Contains("古典", StringComparison.OrdinalIgnoreCase)
               || g.Contains("交响", StringComparison.OrdinalIgnoreCase)
               || g.Contains("协奏", StringComparison.OrdinalIgnoreCase)
               || g.Contains("奏鸣", StringComparison.OrdinalIgnoreCase)
               || g.Contains("室内乐", StringComparison.OrdinalIgnoreCase)
               || g.Contains("歌剧", StringComparison.OrdinalIgnoreCase)
               || g.Contains("巴洛克", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Orchestra", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Symphon", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Concerto", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Sonata", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Chamber", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Opera", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Baroque", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsClassicalByGenre => IsClassicalGenre(Genre);

    public string PlayCountDisplay =>
        Rating >= 5
            ? (EffectivePlayCount > 0 ? $"♥ {EffectivePlayCount}" : "♥")
            : (EffectivePlayCount > 0 ? $"{EffectivePlayCount}" : "");

    public string WorkAndConductorSubline =>
        !string.IsNullOrWhiteSpace(WorkDisplay) && !string.IsNullOrWhiteSpace(ConductorsDisplay)
            ? $"{WorkDisplay} · {ConductorsDisplay}"
            : (!string.IsNullOrWhiteSpace(WorkDisplay) ? WorkDisplay : ConductorsDisplay);

    public static bool IsExplicitNonClassicalGenre(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return false;
        string g = genre.Trim();
        return g.Contains("Pop", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Rock", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Metal", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Jazz", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Hip-Hop", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Hip Hop", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Rap", StringComparison.OrdinalIgnoreCase)
               || g.Contains("R&B", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Electronic", StringComparison.OrdinalIgnoreCase)
               || g.Contains("EDM", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Dance", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Country", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Folk", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Blues", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Reggae", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Indie", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Punk", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Soul", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Funk", StringComparison.OrdinalIgnoreCase)
               || g.Contains("K-Pop", StringComparison.OrdinalIgnoreCase)
               || g.Contains("J-Pop", StringComparison.OrdinalIgnoreCase)
               || g.Contains("C-Pop", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Mandopop", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Cantopop", StringComparison.OrdinalIgnoreCase)
               || g.Contains("ACG", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Anime", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Soundtrack", StringComparison.OrdinalIgnoreCase)
               || g.Contains("OST", StringComparison.OrdinalIgnoreCase)
               || g.Contains("Game", StringComparison.OrdinalIgnoreCase)
               || g.Contains("流行", StringComparison.OrdinalIgnoreCase)
               || g.Contains("摇滚", StringComparison.OrdinalIgnoreCase)
               || g.Contains("爵士", StringComparison.OrdinalIgnoreCase)
               || g.Contains("电子", StringComparison.OrdinalIgnoreCase)
               || g.Contains("民谣", StringComparison.OrdinalIgnoreCase)
               || g.Contains("说唱", StringComparison.OrdinalIgnoreCase)
               || g.Contains("嘻哈", StringComparison.OrdinalIgnoreCase)
               || g.Contains("原声", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ClassifyIsClassical(
        string? genre,
        string? workName,
        string? composersDisplay,
        string? conductorsDisplay)
    {
        if (IsClassicalGenre(genre)) return true;
        if (!string.IsNullOrWhiteSpace(workName)) return true;
        if (IsExplicitNonClassicalGenre(genre)) return false;
        if (!string.IsNullOrWhiteSpace(conductorsDisplay)) return true;
        return string.IsNullOrWhiteSpace(genre) && !string.IsNullOrWhiteSpace(composersDisplay);
    }

    public bool IsClassical =>
        ClassifyIsClassical(Genre, WorkName, ComposersDisplay, ConductorsDisplay);

    public string ClassicalFourLevelSummary =>
        IsClassical
            ? string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(ComposersDisplay) ? ArtistsDisplay : ComposersDisplay,
                string.IsNullOrWhiteSpace(WorkName) ? Album : WorkName,
                string.IsNullOrWhiteSpace(MovementName) ? Title : MovementName,
                ConductorsDisplay
            }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : $"{ArtistsDisplay} — {Album}";

    public string AlternateVersionLabel =>
        $"{(string.IsNullOrWhiteSpace(ConductorsDisplay) ? ArtistsDisplay : ConductorsDisplay)}"
        + (Year.HasValue ? $" ({Year.Value})" : "")
        + (string.IsNullOrWhiteSpace(Publisher) ? "" : $" · {Publisher}");

    public string SpecsDisplay =>
        string.IsNullOrWhiteSpace(FullPath) || SampleRateHz <= 0
            ? "[未匹配本地曲库]" + (string.IsNullOrWhiteSpace(Album) ? "" : $" · {Album}")
            : $"{SampleRateHz / 1000.0:0.#} kHz"
              + (TempoBucket is not null ? $" · {TempoBucket}" : "")
              + (DjEnergy is not null ? $" · {DjEnergy}" : "")
              + (DjVocals is not null ? $" · {DjVocals}" : "")
              + (DjScene.Count > 0 ? $" · {string.Join(", ", DjScene)}" : "")
              + (EffectivePlayCount > 0 ? $" · {EffectivePlayCount}x" : "");

    public string DurationDisplay
    {
        get
        {
            if (DurationMs <= 0 && string.IsNullOrWhiteSpace(FullPath))
                return "--:--";
            var ts = TimeSpan.FromMilliseconds(DurationMs);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }

    public string TitleAlbumLine => string.IsNullOrWhiteSpace(Album) ? Title : $"{Title} · {Album}";

    public string YearDisplay => Year.HasValue && Year.Value > 0 ? Year.Value.ToString() : "";

    public string EnergyBadge => (DjEnergy ?? "").ToLowerInvariant() switch
    {
        "low" => "低能",
        "medium" => "中能",
        "high" => "高能",
        _ => "",
    };

    public string SceneBadge => DjScene.Count > 0
        ? string.Join(" · ", DjScene.Select(LocalizeSceneToken))
        : "";

    private static string LocalizeSceneToken(string s) => s.Trim().ToLowerInvariant() switch
    {
        "focus" => "专注",
        "coding" => "编程",
        "relax" => "放松",
        "energy" => "活力",
        "pop" => "流行",
        _ => s.Trim(),
    };

    public string VocalsBadge => (DjVocals ?? "").ToLowerInvariant() switch
    {
        "instrumental" => "纯器乐",
        "vocal" => "含人声",
        _ => "",
    };

    public string TempoDisplay =>
        !string.IsNullOrWhiteSpace(TempoBucket)
            ? (TempoBucket.Trim() switch
            {
                "Very Slow" => "极慢",
                "Slow" => "慢速",
                "Moderate" => "中速",
                "Fast" => "快速",
                "Very Fast" => "极快",
                _ => TempoBucket.Trim(),
            })
            : (TempoBpm is > 0 ? $"{TempoBpm.Value:0} BPM" : "");

    public string MoodBadge => Moods.Count > 0 ? string.Join(" · ", Moods.Take(2)) : "";

    public string ReplayGainBadge =>
        ReplayGainTrackDb.HasValue
            ? $"RG2 {ReplayGainTrackDb.Value:+0.0;-0.0;0.0} dB"
            : "RG2 --";
}

public sealed record StagingProposalViewItem(
    string ProposalId,
    string TrackUid,
    string TrackDisplay,
    string FieldName,
    string ValueTransition,
    string SourceBadge,
    string Status
);

public sealed record EntityGroupItem(
    string Key,
    string Title,
    string Subtitle,
    string BadgeText,
    IReadOnlyList<TrackItem> Tracks
);

public sealed record SubTabOption(
    string Id,
    string Label,
    bool IsActive
);

public sealed record PairedDeviceViewItem(
    string DeviceId,
    string DeviceName,
    string LastSyncDisplay
);

public sealed record SyncOutboxViewItem(
    string Id,
    string KindLabel,
    string DisplayName,
    string ModeBadge,
    string DetailText
);

public sealed record QueueSlotChipItem(
    int SlotIndex,
    string Label,
    string TooltipText,
    bool IsActive,
    bool IsPopulated
);
