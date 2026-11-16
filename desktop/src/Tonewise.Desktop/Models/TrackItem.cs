namespace Tonewise.Desktop.Models;

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
    string? BookletPdfPath = null
)
{
    public bool IsClassical =>
        !string.IsNullOrWhiteSpace(ComposersDisplay)
        || !string.IsNullOrWhiteSpace(WorkName)
        || Genre.Contains("Classical", StringComparison.OrdinalIgnoreCase);

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
        $"{SampleRateHz / 1000.0:0.#} kHz"
        + (TempoBucket is not null ? $" · {TempoBucket}" : "")
        + (DjEnergy is not null ? $" · {DjEnergy}" : "")
        + (DjVocals is not null ? $" · {DjVocals}" : "")
        + (DjScene.Count > 0 ? $" · {string.Join(", ", DjScene)}" : "")
        + (EffectivePlayCount > 0 ? $" · {EffectivePlayCount}x" : "");

    public string DurationDisplay
    {
        get
        {
            var ts = TimeSpan.FromMilliseconds(DurationMs);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }
}
