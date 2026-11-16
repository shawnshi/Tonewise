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
    long LastPlayedTsMs
)
{
    public string SpecsDisplay =>
        $"{SampleRateHz / 1000.0:0.#} kHz"
        + (TempoBucket is not null ? $" · {TempoBucket}" : "")
        + (DjEnergy is not null ? $" · {DjEnergy}" : "")
        + (DjVocals is not null ? $" · {DjVocals}" : "")
        + (DjScene.Count > 0 ? $" · {string.Join(", ", DjScene)}" : "");

    public string DurationDisplay
    {
        get
        {
            var ts = TimeSpan.FromMilliseconds(DurationMs);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }
}
