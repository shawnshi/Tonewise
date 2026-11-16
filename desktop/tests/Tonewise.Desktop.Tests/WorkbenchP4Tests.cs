using System.Text.Json;
using Tonewise.Interop;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class WorkbenchP4Tests
{
    [Fact]
    public void FiveDimQueryBuilder_ExcludeVocalNotRule_AndEnergyArc_EvaluateViaRustCore()
    {
        var query = new FiveDimFilterQuery(
            Scene: "focus",
            ExcludeVocal: true,
            SortArc: "energy_asc",
            TempoNullPassthrough: true
        );
        Assert.True(FiveDimQueryBuilder.RequiresFilterOrSort(query));

        string sceneJson = FiveDimQueryBuilder.BuildSceneCardJson(query);
        string tracksJson = JsonSerializer.Serialize(new object[]
        {
            new { track_uid = "t_vocal", title = "Focus Vocal", dj_scene = new[] { "focus" }, dj_vocals = "vocal", dj_energy = "medium", tempo_bucket = (string?)null, moods = new[] { "calm" } },
            new { track_uid = "t_inst_med", title = "Focus Inst Med", dj_scene = new[] { "focus" }, dj_vocals = "instrumental", dj_energy = "medium", tempo_bucket = (string?)null, moods = new[] { "calm" } },
            new { track_uid = "t_inst_low", title = "Focus Inst Low", dj_scene = new[] { "focus" }, dj_vocals = "instrumental", dj_energy = "low", tempo_bucket = "Slow", moods = new[] { "calm" } },
            new { track_uid = "t_unk_low", title = "Focus Unk Low", dj_scene = new[] { "focus" }, dj_vocals = "unknown", dj_energy = "low", tempo_bucket = (string?)null, moods = new[] { "calm" } },
        });

        var matched = TonewiseCoreClient.EvaluateScene(sceneJson, tracksJson);
        Assert.DoesNotContain("t_vocal", matched);
        Assert.Equal(["t_inst_low", "t_unk_low", "t_inst_med"], matched);
    }

    [Fact]
    public void ClassicalAbSwitchHelper_FindsSameWorkMovementRecordings_AndMapsProgressRatio()
    {
        var recordings = new[]
        {
            new { Uid = "rec_karajan_1", Work = "Symphony No. 5 in C minor, Op. 67", Movement = "I. Allegro con brio", Conductor = "Karajan", DurationMs = 440_000L },
            new { Uid = "rec_kleiber_1", Work = "Symphony No. 5 in C minor, Op. 67", Movement = "i. allegro  con brio", Conductor = "Kleiber", DurationMs = 420_000L },
            new { Uid = "rec_kleiber_2", Work = "Symphony No. 5 in C minor, Op. 67", Movement = "II. Andante con moto", Conductor = "Kleiber", DurationMs = 590_000L },
            new { Uid = "rec_gould_1", Work = "Goldberg Variations, BWV 988", Movement = "Aria", Conductor = "Gould", DurationMs = 185_000L },
        };

        var current = recordings[0];
        var alternates = ClassicalAbSwitchHelper.FindAlternateRecordings(
            current,
            recordings,
            r => r.Uid,
            r => r.Work,
            r => r.Movement
        );

        Assert.Single(alternates);
        Assert.Equal("rec_kleiber_1", alternates[0].Uid);

        // 50% into 440,000 ms movement -> 210,000 ms into 420,000 ms recording
        long mappedMs = ClassicalAbSwitchHelper.ComputeTargetPositionMs(
            currentPositionMs: 220_000L,
            currentDurationMs: current.DurationMs,
            targetDurationMs: alternates[0].DurationMs
        );
        Assert.Equal(210_000L, mappedMs);
    }

    [Fact]
    public void OledBurnInGuard_BoundsPixelOffsetsWithinTwoPixels_AndCyclesCleanly()
    {
        var visited = new HashSet<(int, int)>();
        for (int tick = 0; tick < 28; tick++)
        {
            var (dx, dy) = OledBurnInGuard.ComputePixelOffset(tick);
            Assert.InRange(dx, -2, 2);
            Assert.InRange(dy, -2, 2);
            visited.Add((dx, dy));
        }

        Assert.True(visited.Count >= 10, "OLED offset table should visit distinct anti-burn-in positions");
    }

    [Fact]
    public void SessionSlotStore_InitializesFiveNamedPresets_AndPersistsSavedState()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "tw_p4_sessions_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var initial = SessionSlotStore.LoadOrInitializeDefault(tempFile);
            Assert.Equal(5, initial.Count);
            Assert.Equal(["Focus", "Coding", "Relax", "Energy", "Classical"], initial.Select(s => s.Name).ToArray());

            var updatedSlot = new SessionSlotDto(2, "Coding", "coding", 1_763_251_500_000UL, ["uid_a", "uid_b"], 1, 95_000UL);
            SessionSlotStore.SaveSlot(tempFile, updatedSlot);

            var reloaded = SessionSlotStore.LoadOrInitializeDefault(tempFile);
            Assert.Equal(5, reloaded.Count);
            Assert.Equal(95_000UL, reloaded[1].PositionMs);
            Assert.Equal(["uid_a", "uid_b"], reloaded[1].QueueTrackUids);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
