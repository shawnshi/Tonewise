namespace Tonewise.SyncHost.Workbench;

/// <summary>
/// Supported vinyl platter finishes aligned across Tonewise Desktop and Android.
/// </summary>
public enum DesktopVinylStyle
{
    ClassicBlack = 0,
    CoverPearl = 1,
    GrooveDiffraction = 2,
}

/// <summary>
/// Framework-agnostic ARGB color value used by pure domain material math.
/// </summary>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B);

/// <summary>
/// Precomputed pearlescent nacre color stops and groove tones for <see cref="DesktopVinylStyle.CoverPearl"/>.
/// </summary>
public sealed record PearlVinylPalette(
    ArgbColor[] BodyStops,
    ArgbColor SwirlWarm,
    ArgbColor SwirlCool,
    ArgbColor GrooveDark,
    ArgbColor GrooveLight,
    ArgbColor OuterRim,
    ArgbColor DropShadow
);

/// <summary>
/// Wire serialization and cycling helpers for desktop Now Playing stage mode and vinyl finishes.
/// </summary>
public static class DesktopVinylStyleCodec
{
    public const string StageGallery = "gallery";
    public const string StageVinyl = "vinyl";

    public static string NormalizeStageMode(string? mode) =>
        string.Equals(mode?.Trim(), StageVinyl, StringComparison.OrdinalIgnoreCase)
            ? StageVinyl
            : StageGallery;

    public static DesktopVinylStyle ParseStyle(string? wireKey) =>
        wireKey?.Trim().ToLowerInvariant() switch
        {
            "pearl" => DesktopVinylStyle.CoverPearl,
            "diffraction" => DesktopVinylStyle.GrooveDiffraction,
            _ => DesktopVinylStyle.ClassicBlack,
        };

    public static string ToWireKey(DesktopVinylStyle style) =>
        style switch
        {
            DesktopVinylStyle.CoverPearl => "pearl",
            DesktopVinylStyle.GrooveDiffraction => "diffraction",
            _ => "classic",
        };

    public static DesktopVinylStyle Next(DesktopVinylStyle style) =>
        style switch
        {
            DesktopVinylStyle.ClassicBlack => DesktopVinylStyle.CoverPearl,
            DesktopVinylStyle.CoverPearl => DesktopVinylStyle.GrooveDiffraction,
            _ => DesktopVinylStyle.ClassicBlack,
        };

    public static string DisplayName(DesktopVinylStyle style) =>
        style switch
        {
            DesktopVinylStyle.CoverPearl => "封面珠光彩胶",
            DesktopVinylStyle.GrooveDiffraction => "沟槽微衍射黑胶",
            _ => "经典哑光黑胶",
        };
}

/// <summary>
/// Pure math helpers for pearlescent nacre palettes, dual-lobe groove diffraction stops,
/// pointer micro-parallax clamping, and damped mechanical motion.
/// </summary>
public static class VinylMaterialMath
{
    private static readonly float[] DiffractionPositions =
    [
        0f,
        0.036f,
        0.072f,
        0.088f,
        0.460f,
        0.496f,
        0.532f,
        0.568f,
        0.584f,
        0.960f,
        1f,
    ];

    private static readonly ArgbColor[] DiffractionStops =
    [
        new(64, 138, 198, 255),
        new(78, 186, 152, 245),
        new(62, 242, 168, 198),
        new(0, 0, 0, 0),
        new(0, 0, 0, 0),
        new(58, 126, 224, 212),
        new(76, 152, 186, 255),
        new(60, 238, 174, 156),
        new(0, 0, 0, 0),
        new(0, 0, 0, 0),
        new(64, 138, 198, 255),
    ];

    public static PearlVinylPalette DerivePearlPalette((byte R, byte G, byte B)? coverSeedRgb, bool isDarkTheme)
    {
        double hueDeg = 36.0;
        double sat = 0.22;
        if (coverSeedRgb is { } seed)
        {
            var (hNorm, s, _) = ZuneColorMath.RgbToHsl(seed.R, seed.G, seed.B);
            if (s >= 0.10)
            {
                hueDeg = hNorm * 360.0;
                sat = Math.Clamp(s * 0.48, 0.18, 0.38);
            }
        }

        double innerL = isDarkTheme ? 0.77 : 0.87;
        double bodyL = isDarkTheme ? 0.68 : 0.78;
        double outerL = isDarkTheme ? 0.59 : 0.70;
        double rimBaseL = isDarkTheme ? 0.44 : 0.56;

        static double WrapHueNorm(double deg) => (((deg % 360.0) + 360.0) % 360.0) / 360.0;
        static ArgbColor FromHsl(byte alpha, double hDeg, double sVal, double lVal)
        {
            var (r, g, b) = ZuneColorMath.HslToRgb(WrapHueNorm(hDeg), sVal, lVal);
            return new ArgbColor(alpha, r, g, b);
        }

        var inner = FromHsl(255, hueDeg, sat * 0.88, innerL);
        var mid = FromHsl(255, hueDeg + 10.0, sat, bodyL);
        var outer = FromHsl(255, hueDeg + 346.0, Math.Min(0.40, sat * 1.04), outerL);
        var rim = FromHsl(255, hueDeg, Math.Min(0.38, sat * 0.92), rimBaseL);

        byte warmAlpha = isDarkTheme ? (byte)48 : (byte)52;
        byte coolAlpha = isDarkTheme ? (byte)44 : (byte)48;
        var swirlWarm = FromHsl(warmAlpha, hueDeg + 28.0, Math.Min(0.40, sat * 1.10), isDarkTheme ? 0.78 : 0.84);
        var swirlCool = FromHsl(coolAlpha, hueDeg + 328.0, Math.Min(0.40, sat * 1.05), isDarkTheme ? 0.75 : 0.82);

        byte grooveDarkAlpha = isDarkTheme ? (byte)40 : (byte)34;
        var grooveDark = FromHsl(grooveDarkAlpha, hueDeg, Math.Clamp(sat * 0.55, 0.08, 0.24), 0.28);

        byte grooveLightAlpha = isDarkTheme ? (byte)42 : (byte)54;
        var grooveLight = new ArgbColor(grooveLightAlpha, 255, 255, 255);

        var outerRim = isDarkTheme
            ? new ArgbColor(95, 248, 245, 240)
            : FromHsl(115, hueDeg, Math.Clamp(sat * 0.55, 0.10, 0.24), 0.34);

        var dropShadow = new ArgbColor(isDarkTheme ? (byte)64 : (byte)42, 0, 0, 0);

        return new PearlVinylPalette(
            BodyStops: [inner, mid, outer, rim],
            SwirlWarm: swirlWarm,
            SwirlCool: swirlCool,
            GrooveDark: grooveDark,
            GrooveLight: grooveLight,
            OuterRim: outerRim,
            DropShadow: dropShadow);
    }

    public static ArgbColor[] DiffractionSpectrumStops() => (ArgbColor[])DiffractionStops.Clone();

    public static float[] DiffractionSpectrumPositions() => (float[])DiffractionPositions.Clone();

    public static double ComputePointerParallaxDeg(double normalizedX, double normalizedY, double maxDeg = 8.0)
    {
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY) || !double.IsFinite(maxDeg) || maxDeg <= 0.0)
        {
            return 0.0;
        }
        double nx = Math.Clamp(normalizedX, -1.0, 1.0);
        double ny = Math.Clamp(normalizedY, -1.0, 1.0);
        double combined = Math.Clamp(nx * 0.75 - ny * 0.40, -1.0, 1.0);
        return combined * maxDeg;
    }

    public static float StepDampedValue(float current, float target, double dtSeconds, float halfLifeSec = 0.12f)
    {
        if (!float.IsFinite(current) || !float.IsFinite(target))
        {
            return float.IsFinite(target) ? target : 0f;
        }
        if (dtSeconds <= 0.0 || !double.IsFinite(dtSeconds) || halfLifeSec <= 0f)
        {
            return current;
        }
        if (MathF.Abs(target - current) < 0.005f)
        {
            return target;
        }
        float decay = MathF.Pow(0.5f, (float)(dtSeconds / halfLifeSec));
        float next = target + (current - target) * decay;
        return MathF.Abs(target - next) < 0.005f ? target : next;
    }

    public static float StepSleeveEjection(float current, bool isPlaying, double dtSeconds)
    {
        float target = isPlaying ? 0.62f : 0.34f;
        return StepDampedValue(current, target, dtSeconds, halfLifeSec: 0.14f);
    }

    public static float StepTonearmAngle(float currentDeg, bool isPlaying, double dtSeconds)
    {
        float target = isPlaying ? 0f : -22f;
        return StepDampedValue(currentDeg, target, dtSeconds, halfLifeSec: 0.11f);
    }
}
