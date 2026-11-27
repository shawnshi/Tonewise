using System.Text.Json;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class ClassicalMetadataEngineTests
{
    private static JsonDocument LoadGoldenDocument()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "protocol", "fixtures", "classical_golden.json");
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate));
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException("Could not locate protocol/fixtures/classical_golden.json from test output dir.");
    }

    [Fact]
    public void PassesAllClassicalGoldenFixtures()
    {
        using var doc = LoadGoldenDocument();
        var root = doc.RootElement;

        foreach (var c in root.GetProperty("is_classical_cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            bool actual = ClassicalMetadataEngine.IsClassicalTrack(
                c.GetProperty("genre").GetString(),
                c.GetProperty("work").GetString(),
                c.GetProperty("composer").GetString(),
                c.GetProperty("conductor").GetString(),
                c.GetProperty("title").GetString());
            Assert.True(actual == c.GetProperty("expected").GetBoolean(), $"is_classical case failed: {name}");
        }

        foreach (var c in root.GetProperty("split_work_cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            int? expMvtNumInput = c.GetProperty("explicit_mvt_num").ValueKind == JsonValueKind.Number
                ? c.GetProperty("explicit_mvt_num").GetInt32()
                : null;
            var actual = ClassicalMetadataEngine.SplitWorkAndMovement(
                c.GetProperty("raw_title").GetString(),
                c.GetProperty("explicit_work").GetString(),
                c.GetProperty("explicit_movement").GetString(),
                expMvtNumInput);

            Assert.Equal(c.GetProperty("expected_work").GetString(), actual.Work);
            Assert.Equal(c.GetProperty("expected_movement").GetString(), actual.Movement);
            int? expectedMvtNum = c.GetProperty("expected_mvt_num").ValueKind == JsonValueKind.Number
                ? c.GetProperty("expected_mvt_num").GetInt32()
                : null;
            Assert.Equal(expectedMvtNum, actual.MovementNumber);
            _ = name;
        }

        foreach (var c in root.GetProperty("era_cases").EnumerateArray())
        {
            string? expected = c.GetProperty("expected").ValueKind == JsonValueKind.String
                ? c.GetProperty("expected").GetString()
                : null;
            string? actual = ClassicalMetadataEngine.ClassifyComposerEra(
                c.GetProperty("composer").GetString(),
                c.GetProperty("genre").GetString());
            Assert.Equal(expected, actual);
        }

        foreach (var c in root.GetProperty("form_cases").EnumerateArray())
        {
            string? actual = ClassicalMetadataEngine.ClassifyWorkForm(
                c.GetProperty("work").GetString(),
                c.GetProperty("genre").GetString());
            Assert.Equal(c.GetProperty("expected").GetString(), actual);
        }

        foreach (var c in root.GetProperty("performer_separation_cases").EnumerateArray())
        {
            var artists = c.GetProperty("artists").EnumerateArray().Select(e => e.GetString()!).ToList();
            var conductors = c.GetProperty("conductors").EnumerateArray().Select(e => e.GetString()!).ToList();
            var expSolo = c.GetProperty("explicit_soloists").EnumerateArray().Select(e => e.GetString()!).ToList();
            var actual = ClassicalMetadataEngine.SeparatePerformers(
                artists,
                c.GetProperty("album_artist").GetString(),
                conductors,
                c.GetProperty("explicit_orchestra").GetString(),
                expSolo);
            var expectedSolo = c.GetProperty("expected_soloists").EnumerateArray().Select(e => e.GetString()!).ToList();
            string? expectedEnsemble = c.GetProperty("expected_ensemble").ValueKind == JsonValueKind.String
                ? c.GetProperty("expected_ensemble").GetString()
                : null;
            Assert.Equal(expectedSolo, actual.Soloists);
            Assert.Equal(expectedEnsemble, actual.Ensemble);
        }

        foreach (var c in root.GetProperty("credit_tier_cases").EnumerateArray())
        {
            var conductors = c.GetProperty("conductors").EnumerateArray().Select(e => e.GetString()!).ToList();
            var soloists = c.GetProperty("soloists").EnumerateArray().Select(e => e.GetString()!).ToList();
            int? year = c.GetProperty("year").ValueKind == JsonValueKind.Number
                ? c.GetProperty("year").GetInt32()
                : null;
            var actual = ClassicalMetadataEngine.BuildClassicalCreditTiers(
                c.GetProperty("composer").GetString(),
                conductors,
                soloists,
                c.GetProperty("ensemble").GetString(),
                year);
            var expectedTiers = c.GetProperty("expected_tiers").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Equal(expectedTiers, actual);
        }
    }
}
