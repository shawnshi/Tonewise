using System.Text.Json;
using Tonewise.SyncHost.Workbench;
using Xunit;

namespace Tonewise.Desktop.Tests;

public sealed class DesktopVinylStageTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "desktop", "src", "Tonewise.Desktop", "MainWindow.xaml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Tonewise repository root.");
    }

    [Fact]
    public void DesktopVinylStyleCodec_NormalizesParsesCyclesAndFormatsDisplayNames()
    {
        Assert.Equal("gallery", DesktopVinylStyleCodec.NormalizeStageMode(null));
        Assert.Equal("gallery", DesktopVinylStyleCodec.NormalizeStageMode("unexpected"));
        Assert.Equal("vinyl", DesktopVinylStyleCodec.NormalizeStageMode("VINYL"));

        Assert.Equal(DesktopVinylStyle.ClassicBlack, DesktopVinylStyleCodec.ParseStyle(null));
        Assert.Equal(DesktopVinylStyle.ClassicBlack, DesktopVinylStyleCodec.ParseStyle("unknown"));
        Assert.Equal(DesktopVinylStyle.CoverPearl, DesktopVinylStyleCodec.ParseStyle("PEARL"));
        Assert.Equal(DesktopVinylStyle.GrooveDiffraction, DesktopVinylStyleCodec.ParseStyle("diffraction"));

        Assert.Equal("classic", DesktopVinylStyleCodec.ToWireKey(DesktopVinylStyle.ClassicBlack));
        Assert.Equal("pearl", DesktopVinylStyleCodec.ToWireKey(DesktopVinylStyle.CoverPearl));
        Assert.Equal("diffraction", DesktopVinylStyleCodec.ToWireKey(DesktopVinylStyle.GrooveDiffraction));

        Assert.Equal(DesktopVinylStyle.CoverPearl, DesktopVinylStyleCodec.Next(DesktopVinylStyle.ClassicBlack));
        Assert.Equal(DesktopVinylStyle.GrooveDiffraction, DesktopVinylStyleCodec.Next(DesktopVinylStyle.CoverPearl));
        Assert.Equal(DesktopVinylStyle.ClassicBlack, DesktopVinylStyleCodec.Next(DesktopVinylStyle.GrooveDiffraction));

        Assert.Equal("经典哑光黑胶", DesktopVinylStyleCodec.DisplayName(DesktopVinylStyle.ClassicBlack));
        Assert.Equal("封面珠光彩胶", DesktopVinylStyleCodec.DisplayName(DesktopVinylStyle.CoverPearl));
        Assert.Equal("沟槽微衍射黑胶", DesktopVinylStyleCodec.DisplayName(DesktopVinylStyle.GrooveDiffraction));
    }

    [Fact]
    public void VinylMaterialMath_DerivePearlPalette_ClampsNeonAndFallsBackToWarmChampagne()
    {
        var neonSeeds = new (byte R, byte G, byte B)[]
        {
            (255, 0, 255),
            (0, 255, 0),
            (255, 26, 0),
        };

        foreach (var seed in neonSeeds)
        {
            foreach (bool dark in new[] { false, true })
            {
                var palette = VinylMaterialMath.DerivePearlPalette(seed, dark);
                Assert.Equal(4, palette.BodyStops.Length);
                foreach (var stop in palette.BodyStops)
                {
                    var (_, s, _) = ZuneColorMath.RgbToHsl(stop.R, stop.G, stop.B);
                    Assert.True(s <= 0.43, $"Pearl stop saturation {s:F3} exceeded 0.43 for seed {seed} (dark={dark})");
                }
                if (!dark)
                {
                    double rimLum = ZuneColorMath.ComputeRelativeLuminance(palette.OuterRim.R, palette.OuterRim.G, palette.OuterRim.B);
                    double innerLum = ZuneColorMath.ComputeRelativeLuminance(palette.BodyStops[0].R, palette.BodyStops[0].G, palette.BodyStops[0].B);
                    Assert.True(rimLum < innerLum, "Light surface outer bevel must be darker than inner pearl body");
                }
            }
        }

        foreach ((byte R, byte G, byte B)? mono in new (byte R, byte G, byte B)?[] { null, (128, 128, 128), (15, 15, 15) })
        {
            var palette = VinylMaterialMath.DerivePearlPalette(mono, isDarkTheme: false);
            var (hNorm, s, _) = ZuneColorMath.RgbToHsl(palette.BodyStops[1].R, palette.BodyStops[1].G, palette.BodyStops[1].B);
            double hueDeg = hNorm * 360.0;
            Assert.True(hueDeg is >= 20.0 and <= 55.0, $"Monochrome fallback hue {hueDeg:F1} should stay warm champagne");
            Assert.True(s is >= 0.10 and <= 0.35, $"Monochrome fallback sat {s:F3} should stay pearlescent");
        }
    }

    [Fact]
    public void VinylMaterialMath_DiffractionStopsAndMotionMath_RespectOpticalAndDampingBounds()
    {
        var stops = VinylMaterialMath.DiffractionSpectrumStops();
        var positions = VinylMaterialMath.DiffractionSpectrumPositions();
        Assert.Equal(stops.Length, positions.Length);
        Assert.Equal(0f, positions[0], 6);
        Assert.Equal(1f, positions[^1], 6);
        for (int i = 1; i < positions.Length; i++)
        {
            Assert.True(positions[i] >= positions[i - 1], "Diffraction positions must be monotonic");
        }
        foreach (var stop in stops)
        {
            Assert.True(stop.A <= 96, $"Diffraction alpha {stop.A} must remain <= 96");
        }

        Assert.Equal(0.0, VinylMaterialMath.ComputePointerParallaxDeg(double.NaN, 0.5), 6);
        Assert.Equal(0.0, VinylMaterialMath.ComputePointerParallaxDeg(0.5, double.PositiveInfinity), 6);
        double clampedPos = VinylMaterialMath.ComputePointerParallaxDeg(10.0, -10.0, maxDeg: 8.0);
        double clampedNeg = VinylMaterialMath.ComputePointerParallaxDeg(-10.0, 10.0, maxDeg: 8.0);
        Assert.True(clampedPos is > 0.0 and <= 8.0);
        Assert.True(clampedNeg is < 0.0 and >= -8.0);

        float arm = -22f;
        float ejection = 0.34f;
        for (int i = 0; i < 40; i++)
        {
            arm = VinylMaterialMath.StepTonearmAngle(arm, isPlaying: true, dtSeconds: 0.016);
            ejection = VinylMaterialMath.StepSleeveEjection(ejection, isPlaying: true, dtSeconds: 0.016);
        }
        Assert.True(MathF.Abs(arm - 0f) < 0.6f, $"Tonearm should approach 0 deg when playing (was {arm})");
        Assert.True(MathF.Abs(ejection - 0.62f) < 0.02f, $"Sleeve ejection should approach 0.62 when playing (was {ejection})");
    }

    [Fact]
    public void DesktopUserSettings_VinylStageSettings_DefaultCleanlyAndRoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"tonewise_vinyl_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string settingsPath = Path.Combine(tempDir, "settings.json");
            File.WriteAllText(settingsPath, """{"AppTheme": "dark"}""");
            var legacy = DesktopSettingsStore.Load(settingsPath);
            Assert.Equal("gallery", legacy.NowPlayingStageMode);
            Assert.Equal("classic", legacy.VinylFinishStyle);
            Assert.True(legacy.VinylUseAmbientBackdrop);

            var updated = legacy with
            {
                NowPlayingStageMode = "vinyl",
                VinylFinishStyle = "pearl",
                VinylUseAmbientBackdrop = false,
            };
            DesktopSettingsStore.Save(updated, settingsPath);

            var reloaded = DesktopSettingsStore.Load(settingsPath);
            Assert.Equal("vinyl", reloaded.NowPlayingStageMode);
            Assert.Equal("pearl", reloaded.VinylFinishStyle);
            Assert.False(reloaded.VinylUseAmbientBackdrop);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DesktopVinylIntegration_SourceAndXamlContracts_HoldAcrossMainWindowAndOledClock()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");
        string mainXaml = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml"));
        string mainCs = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(desktopRoot, "MainWindow*.cs", SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));
        string oledXaml = File.ReadAllText(Path.Combine(desktopRoot, "DeskClockWidgetWindow.xaml"));
        string oledCs = File.ReadAllText(Path.Combine(desktopRoot, "DeskClockWidgetWindow.xaml.cs"));

        Assert.Contains("x:Name=\"NowPlayingVinylHost\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingCoverHost\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingHeroGrid\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingVinylCanvas\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingVinylFlyout\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingStageModeButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NowPlayingVinylFinishButton\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"OnNowPlayingStageSizeChanged\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("NowPlayingStageModeCombo", mainXaml, StringComparison.Ordinal);
        Assert.Contains("VinylFinishStyleCombo", mainXaml, StringComparison.Ordinal);

        string mainVmCs = File.ReadAllText(Path.Combine(desktopRoot, "ViewModels", "MainViewModel.cs"));
        Assert.Contains("private bool _isNowPlayingQueueOpen;", mainVmCs, StringComparison.Ordinal);

        Assert.Contains("CompositionTarget.Rendering -= OnVinylCompositionRendering", mainCs, StringComparison.Ordinal);
        Assert.Contains("_vinylRenderer.Dispose()", mainCs, StringComparison.Ordinal);
        Assert.Contains("e.Key == VirtualKey.V", mainCs, StringComparison.Ordinal);
        Assert.Contains("RenderAmbientBackdrop", mainCs, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"OledVinylCanvas\"", oledXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VinylToggleButton\"", oledXaml, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering -= OnOledVinylCompositionRendering", oledCs, StringComparison.Ordinal);
        Assert.Contains("_oledVinylRenderer.Dispose()", oledCs, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopSmtcIdentity_AumidRegistration_WindowInterop_AndProductMetadataHold()
    {
        string repoRoot = FindRepoRoot();
        string desktopRoot = Path.Combine(repoRoot, "desktop", "src", "Tonewise.Desktop");
        string appCs = File.ReadAllText(Path.Combine(desktopRoot, "App.xaml.cs"));
        string registrarCs = File.ReadAllText(Path.Combine(desktopRoot, "Services", "ShellAppIdentityRegistrar.cs"));
        string mediaCs = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.PlaybackAndMedia.cs"));
        string csproj = File.ReadAllText(Path.Combine(desktopRoot, "Tonewise.Desktop.csproj"));

        Assert.Contains("ShawnShi.Tonewise.Desktop", appCs, StringComparison.Ordinal);
        Assert.Contains("ShellAppIdentityRegistrar.Register", appCs, StringComparison.Ordinal);
        Assert.Contains("SetCurrentProcessExplicitAppUserModelID", registrarCs, StringComparison.Ordinal);
        Assert.Contains("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", registrarCs, StringComparison.Ordinal);
        Assert.Contains("IShellLinkW", registrarCs, StringComparison.Ordinal);
        Assert.Contains("IPropertyStore", registrarCs, StringComparison.Ordinal);
        Assert.Contains("IPersistFile", registrarCs, StringComparison.Ordinal);
        Assert.Contains("SHChangeNotify", registrarCs, StringComparison.Ordinal);
        Assert.Contains(@"Software\Classes\AppUserModelId\", registrarCs, StringComparison.Ordinal);
        Assert.Contains("DisplayName", registrarCs, StringComparison.Ordinal);
        Assert.Contains("IconUri", registrarCs, StringComparison.Ordinal);

        Assert.Contains("SystemMediaTransportControlsInterop.GetForWindow", mediaCs, StringComparison.Ordinal);
        Assert.Contains("<Product>Tonewise</Product>", csproj, StringComparison.Ordinal);
        Assert.Contains("<AssemblyTitle>Tonewise</AssemblyTitle>", csproj, StringComparison.Ordinal);
    }
}
