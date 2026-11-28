using SkiaSharp;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.Desktop.Rendering;

/// <summary>
/// Renders Tonewise desktop vinyl platters using SkiaSharp with offscreen platter-base caching,
/// smooth tonearm/sleeve mechanics, three material finishes, and subtle pointer reflection parallax.
/// </summary>
public sealed class VinylTurntableRenderer : IDisposable
{
    public enum VinylLayoutMode
    {
        FullTurntable,
        HalfEjectedSleeve,
    }

    private sealed class CoverImageSnapshot(SKImage image)
    {
        private int _refCount = 1;
        public SKImage Image { get; } = image;

        public bool TryAddRef()
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                    return true;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                Image.Dispose();
            }
        }
    }

    private const int CoverDecodeSize = 512;
    private const float TonearmPlayingDeg = 0f;
    private const float TonearmParkedDeg = -22f;
    private static readonly SKSamplingOptions HighSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly float[] ClassicGrooveFracs = [0.42f, 0.50f, 0.58f, 0.66f, 0.74f, 0.82f, 0.90f];
    private static readonly float[] PearlGrooveFracs = [0.42f, 0.48f, 0.54f, 0.60f, 0.66f, 0.72f, 0.78f, 0.85f, 0.91f];
    private static readonly float[] DiffractionGrooveFracs =
    [
        0.41f, 0.46f, 0.50f, 0.55f, 0.60f, 0.64f,
        0.69f, 0.74f, 0.79f, 0.84f, 0.89f, 0.93f,
    ];

    private readonly object _lock = new();
    private CoverImageSnapshot? _coverSnapshot;
    private string? _loadedCoverPath;
    private CancellationTokenSource? _loadCts;
    private int _disposed;

    private float _discAngleDeg;
    private float _tonearmAngleDeg = TonearmParkedDeg;
    private float _sleeveEjection = 0.34f;
    private float _parallaxDeg;
    private float _targetParallaxDeg;

    // Offscreen platter cache keyed on rounded pixel radius + style + cover seed + surface polarity
    private SKImage? _cachedPlatterBase;
    private int _cachedPlatterRadiusPx = -1;
    private DesktopVinylStyle _cachedPlatterStyle;
    private (byte R, byte G, byte B)? _cachedPlatterSeed;
    private bool _cachedPlatterDark;

    // Reusable dynamic overlay shaders keyed on center/radius/style/seed/dark
    private SKShader? _cachedNacreSwirlShader;
    private SKShader? _cachedSheenShader;
    private SKShader? _cachedDiffractionPrimaryShader;
    private SKShader? _cachedDiffractionSecondaryShader;
    private SKShader? _cachedArmTubeShader;
    private float _cachedOverlayCx = -1f;
    private float _cachedOverlayCy = -1f;
    private float _cachedOverlayRadius = -1f;
    private DesktopVinylStyle _cachedOverlayStyle;
    private (byte R, byte G, byte B)? _cachedOverlaySeed;
    private bool _cachedOverlayDark;
    private float _cachedArmPivotX = -1f;
    private float _cachedArmTipX = -1f;

    private readonly SKPaint _bitmapPaint = new() { IsAntialias = true };
    private readonly SKPaint _overlayPaint = new() { IsAntialias = true };
    private readonly SKPaint _labelFallbackPaint = new() { IsAntialias = true, Color = new SKColor(0x2A, 0x24, 0x1B) };
    private readonly SKPaint _labelBorderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0, 0, 0, 165) };
    private readonly SKPaint _spindlePaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x0B, 0x0C, 0x10) };
    private readonly SKPaint _spindleRimPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255, 90) };
    private readonly SKPaint _sleeveBorderPaint = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _sleeveShadowPaint = new() { IsAntialias = false };
    private readonly SKPaint _sleevePlaceholderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _armShadowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, Color = new SKColor(0, 0, 0, 115) };
    private readonly SKPaint _armTubePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _armPivotPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x1D, 0x20, 0x28) };
    private readonly SKPaint _armPivotRimPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255, 64) };
    private readonly SKPaint _armAccentPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0xD9, 0xB3, 0x72) };
    private readonly SKPaint _cartridgePaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x26, 0x2A, 0x34) };
    private readonly SKPaint _hcRingPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPath _armPath = new();
    private readonly SKPath _clipCirclePath = new();

    public void UpdateCoverAsync(string? coverPath, Action onReady)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        string? normalized = string.IsNullOrWhiteSpace(coverPath) ? null : coverPath;
        if (string.Equals(normalized, _loadedCoverPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _loadedCoverPath = normalized;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        if (normalized is null)
        {
            SwapCoverSnapshot(null);
            onReady();
            return;
        }

        _ = Task.Run(() =>
        {
            SKImage? decoded = null;
            try
            {
                if (!File.Exists(normalized) || token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                using var raw = SKBitmap.Decode(normalized);
                if (raw is null || raw.Width <= 0 || raw.Height <= 0) return;

                using var target = new SKBitmap(CoverDecodeSize, CoverDecodeSize, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(target))
                {
                    canvas.Clear(SKColors.Transparent);
                    int side = Math.Min(raw.Width, raw.Height);
                    int sx = (raw.Width - side) / 2;
                    int sy = (raw.Height - side) / 2;
                    using var rawImg = SKImage.FromBitmap(raw);
                    using var paint = new SKPaint { IsAntialias = true };
                    canvas.DrawImage(
                        rawImg,
                        new SKRect(sx, sy, sx + side, sy + side),
                        new SKRect(0, 0, CoverDecodeSize, CoverDecodeSize),
                        HighSampling,
                        paint);
                }

                if (token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
                decoded = SKImage.FromPixelCopy(target.Info, target.GetPixels(), target.RowBytes);
                if (decoded is null) return;

                var snap = new CoverImageSnapshot(decoded);
                decoded = null;
                if (token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                {
                    snap.Release();
                    return;
                }
                SwapCoverSnapshot(snap);
                onReady();
            }
            catch
            {
                decoded?.Dispose();
            }
        }, token);
    }

    private void SwapCoverSnapshot(CoverImageSnapshot? next)
    {
        CoverImageSnapshot? old;
        lock (_lock)
        {
            old = _coverSnapshot;
            _coverSnapshot = next;
        }
        old?.Release();
    }

    public void SetPointerParallaxTarget(float targetDeg)
    {
        _targetParallaxDeg = float.IsFinite(targetDeg) ? Math.Clamp(targetDeg, -8f, 8f) : 0f;
    }

    public void ResetPointerParallax()
    {
        _targetParallaxDeg = 0f;
    }

    public bool NeedsAnimation(bool isPlaying, bool motionEnabled)
    {
        float targetArm = isPlaying ? TonearmPlayingDeg : TonearmParkedDeg;
        float targetEjection = isPlaying ? 0.62f : 0.34f;
        if (!motionEnabled)
        {
            return MathF.Abs(_tonearmAngleDeg - targetArm) >= 0.05f
                || MathF.Abs(_sleeveEjection - targetEjection) >= 0.005f
                || MathF.Abs(_parallaxDeg) >= 0.05f;
        }

        return isPlaying
            || MathF.Abs(_tonearmAngleDeg - targetArm) >= 0.05f
            || MathF.Abs(_sleeveEjection - targetEjection) >= 0.005f
            || MathF.Abs(_parallaxDeg - _targetParallaxDeg) >= 0.05f;
    }

    public bool AdvanceFrame(double dtSeconds, bool isPlaying, bool motionEnabled)
    {
        double clampedDt = double.IsFinite(dtSeconds) ? Math.Clamp(dtSeconds, 0.0, 0.1) : 0.016;
        if (!motionEnabled)
        {
            _tonearmAngleDeg = isPlaying ? TonearmPlayingDeg : TonearmParkedDeg;
            _sleeveEjection = isPlaying ? 0.62f : 0.34f;
            _parallaxDeg = 0f;
            _targetParallaxDeg = 0f;
            return false;
        }

        if (isPlaying)
        {
            // 18s per revolution -> 20 deg/s smooth showcase spin
            _discAngleDeg = (_discAngleDeg + (float)(clampedDt * 20.0)) % 360f;
        }

        _tonearmAngleDeg = VinylMaterialMath.StepTonearmAngle(_tonearmAngleDeg, isPlaying, clampedDt);
        _sleeveEjection = VinylMaterialMath.StepSleeveEjection(_sleeveEjection, isPlaying, clampedDt);
        _parallaxDeg = VinylMaterialMath.StepDampedValue(_parallaxDeg, _targetParallaxDeg, clampedDt, halfLifeSec: 0.10f);

        return NeedsAnimation(isPlaying, motionEnabled: true);
    }

    public void Render(
        SKCanvas canvas,
        int width,
        int height,
        float scale,
        VinylLayoutMode layoutMode,
        DesktopVinylStyle style,
        (byte R, byte G, byte B)? coverSeedRgb,
        bool isDarkTheme,
        bool isHighContrast,
        bool drawSleeveShadow = true)
    {
        if (width <= 0 || height <= 0 || Volatile.Read(ref _disposed) != 0) return;

        float safeScale = float.IsFinite(scale) && scale > 0.25f ? scale : 1f;
        canvas.Clear(SKColors.Transparent);

        CoverImageSnapshot? coverSnap = null;
        lock (_lock)
        {
            if (_coverSnapshot is { } cur && cur.TryAddRef())
            {
                coverSnap = cur;
            }
        }

        try
        {
            if (isHighContrast)
            {
                RenderHighContrast(canvas, width, height, safeScale, layoutMode, coverSnap?.Image, isDarkTheme);
                return;
            }

            if (layoutMode == VinylLayoutMode.HalfEjectedSleeve)
            {
                RenderHalfEjectedSleeve(canvas, width, height, safeScale, style, coverSeedRgb, coverSnap?.Image, isDarkTheme, drawSleeveShadow);
            }
            else
            {
                RenderFullTurntable(canvas, width, height, safeScale, style, coverSeedRgb, coverSnap?.Image, isDarkTheme);
            }
        }
        finally
        {
            coverSnap?.Release();
        }
    }

    private void RenderFullTurntable(
        SKCanvas canvas,
        int width,
        int height,
        float scale,
        DesktopVinylStyle style,
        (byte R, byte G, byte B)? coverSeedRgb,
        SKImage? coverImg,
        bool isDarkTheme)
    {
        float cx = width * 0.46f;
        float cy = height * 0.50f;
        float discRadius = MathF.Max(28f * scale, MathF.Min(width, height) * 0.445f);
        float labelRadius = discRadius * 0.36f;

        DrawPlatterAndOverlays(canvas, cx, cy, discRadius, labelRadius, scale, style, coverSeedRgb, coverImg, isDarkTheme);

        float pivotX = width * 0.87f;
        float pivotY = height * 0.13f;
        float elbowX = pivotX - discRadius * 0.16f;
        float elbowY = pivotY + discRadius * 0.68f;
        float tipX = cx + discRadius * 0.60f;
        float tipY = cy + discRadius * 0.14f;
        DrawTonearm(canvas, scale, pivotX, pivotY, elbowX, elbowY, tipX, tipY);
    }

    private void RenderHalfEjectedSleeve(
        SKCanvas canvas,
        int width,
        int height,
        float scale,
        DesktopVinylStyle style,
        (byte R, byte G, byte B)? coverSeedRgb,
        SKImage? coverImg,
        bool isDarkTheme,
        bool drawSleeveShadow)
    {
        float margin = 4f * scale;
        float sleeveSize = MathF.Max(100f * scale, MathF.Min(height - margin * 2f, width * 0.54f));
        float sleeveLeft = margin;
        float sleeveTop = (height - sleeveSize) * 0.5f;
        var sleeveRect = new SKRect(sleeveLeft, sleeveTop, sleeveLeft + sleeveSize, sleeveTop + sleeveSize);

        float discRadius = sleeveSize * 0.48f;
        float maxCenterX = MathF.Min(width - discRadius - 12f * scale, sleeveLeft + sleeveSize * 0.96f);
        float minCenterX = sleeveLeft + sleeveSize * 0.72f;
        float t = Math.Clamp((_sleeveEjection - 0.34f) / (0.62f - 0.34f), 0f, 1f);
        float discCx = minCenterX + (maxCenterX - minCenterX) * t;
        float discCy = sleeveTop + sleeveSize * 0.50f;
        float labelRadius = discRadius * 0.36f;

        // Disc slides out behind the crisp 0px square Zune album sleeve
        DrawPlatterAndOverlays(canvas, discCx, discCy, discRadius, labelRadius, scale, style, coverSeedRgb, coverImg, isDarkTheme);

        // Soft sleeve edge shadow cast onto the ejected disc
        if (drawSleeveShadow)
        {
            float shadowW = 14f * scale;
            using var shadowShader = SKShader.CreateLinearGradient(
                new SKPoint(sleeveRect.Right - 1f, 0),
                new SKPoint(sleeveRect.Right + shadowW, 0),
                [new SKColor(0, 0, 0, isDarkTheme ? (byte)135 : (byte)64), SKColors.Transparent],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _sleeveShadowPaint.Shader = shadowShader;
            canvas.DrawRect(sleeveRect.Right, sleeveRect.Top + 2f * scale, shadowW, sleeveRect.Height - 4f * scale, _sleeveShadowPaint);
            _sleeveShadowPaint.Shader = null;
        }

        // Crisp 0px square jacket sleeve
        if (coverImg is not null)
        {
            canvas.DrawImage(
                coverImg,
                new SKRect(0, 0, coverImg.Width, coverImg.Height),
                sleeveRect,
                HighSampling,
                _bitmapPaint);
        }
        else
        {
            _sleevePlaceholderPaint.Color = isDarkTheme ? new SKColor(0x16, 0x16, 0x16) : new SKColor(0xEC, 0xEC, 0xEC);
            canvas.DrawRect(sleeveRect, _sleevePlaceholderPaint);
        }

        // Hairline Zune 0px sleeve frame + subtle opening notch edge
        _sleeveBorderPaint.Color = isDarkTheme ? new SKColor(255, 255, 255, 42) : new SKColor(0, 0, 0, 48);
        _sleeveBorderPaint.StrokeWidth = MathF.Max(1f, 1f * scale);
        canvas.DrawRect(sleeveRect, _sleeveBorderPaint);

        // Compact overhead tonearm reaching onto the exposed right vinyl lip
        float pivotX = MathF.Min(width - 14f * scale, discCx + discRadius * 0.88f);
        float pivotY = MathF.Max(14f * scale, sleeveTop + 8f * scale);
        float elbowX = pivotX - discRadius * 0.08f;
        float elbowY = pivotY + discRadius * 0.56f;
        float tipX = discCx + discRadius * 0.58f;
        float tipY = discCy + discRadius * 0.14f;
        DrawTonearm(canvas, scale * 0.86f, pivotX, pivotY, elbowX, elbowY, tipX, tipY);
    }

    private void DrawPlatterAndOverlays(
        SKCanvas canvas,
        float cx,
        float cy,
        float discRadius,
        float labelRadius,
        float scale,
        DesktopVinylStyle style,
        (byte R, byte G, byte B)? coverSeedRgb,
        SKImage? coverImg,
        bool isDarkTheme)
    {
        int radiusPx = Math.Max(16, (int)MathF.Round(discRadius));
        EnsurePlatterBaseCache(radiusPx, style, coverSeedRgb, isDarkTheme);
        EnsureOverlayShaders(cx, cy, discRadius, style, coverSeedRgb, isDarkTheme);

        if (_cachedPlatterBase is { } platterImg)
        {
            float pad = platterImg.Width * 0.5f;
            canvas.DrawImage(platterImg, cx - pad, cy - pad, HighSampling, _bitmapPaint);
        }

        if (style == DesktopVinylStyle.CoverPearl)
        {
            if (_cachedNacreSwirlShader is not null)
            {
                _overlayPaint.Shader = _cachedNacreSwirlShader;
                canvas.Save();
                canvas.RotateDegrees(_discAngleDeg, cx, cy);
                canvas.DrawCircle(cx, cy, discRadius, _overlayPaint);
                canvas.Restore();
            }

            if (_cachedSheenShader is not null)
            {
                float fresnelDeg = 32f + _parallaxDeg + MathF.Sin(_discAngleDeg * (MathF.PI / 180f)) * 3f;
                _overlayPaint.Shader = _cachedSheenShader;
                canvas.Save();
                canvas.RotateDegrees(fresnelDeg, cx, cy);
                canvas.DrawCircle(cx, cy, discRadius, _overlayPaint);
                canvas.Restore();
            }
        }
        else if (style == DesktopVinylStyle.GrooveDiffraction)
        {
            float angleRad = _discAngleDeg * 2f * (MathF.PI / 180f);
            float wobble1 = MathF.Sin(angleRad) * 11f;
            float wobble2 = MathF.Cos(angleRad) * 14f;
            if (_cachedDiffractionPrimaryShader is not null)
            {
                _overlayPaint.Shader = _cachedDiffractionPrimaryShader;
                canvas.Save();
                canvas.RotateDegrees(32f + _parallaxDeg + wobble1, cx, cy);
                canvas.DrawCircle(cx, cy, discRadius, _overlayPaint);
                canvas.Restore();
            }
            if (_cachedDiffractionSecondaryShader is not null)
            {
                _overlayPaint.Shader = _cachedDiffractionSecondaryShader;
                canvas.Save();
                canvas.RotateDegrees(36f + _parallaxDeg * 0.8f + wobble2, cx, cy);
                canvas.DrawCircle(cx, cy, discRadius, _overlayPaint);
                canvas.Restore();
            }
            if (_cachedSheenShader is not null)
            {
                _overlayPaint.Shader = _cachedSheenShader;
                canvas.Save();
                canvas.RotateDegrees(32f + _parallaxDeg * 0.6f, cx, cy);
                canvas.DrawCircle(cx, cy, discRadius, _overlayPaint);
                canvas.Restore();
            }
        }
        else if (_cachedSheenShader is not null)
        {
            _overlayPaint.Shader = _cachedSheenShader;
            canvas.Save();
            canvas.RotateDegrees(32f + _parallaxDeg, cx, cy);
            canvas.DrawCircle(cx, cy, discRadius, _overlayPaint);
            canvas.Restore();
        }

        _overlayPaint.Shader = null;

        // Rotating circular center label artwork
        canvas.Save();
        canvas.RotateDegrees(_discAngleDeg, cx, cy);
        if (coverImg is not null)
        {
            _clipCirclePath.Reset();
            _clipCirclePath.AddCircle(cx, cy, labelRadius);
            canvas.Save();
            canvas.ClipPath(_clipCirclePath, antialias: true);
            var labelRect = new SKRect(cx - labelRadius, cy - labelRadius, cx + labelRadius, cy + labelRadius);
            canvas.DrawImage(
                coverImg,
                new SKRect(0, 0, coverImg.Width, coverImg.Height),
                labelRect,
                HighSampling,
                _bitmapPaint);
            canvas.Restore();
        }
        else
        {
            canvas.DrawCircle(cx, cy, labelRadius, _labelFallbackPaint);
        }
        canvas.Restore();

        _labelBorderPaint.StrokeWidth = MathF.Max(1.5f, 2f * scale);
        canvas.DrawCircle(cx, cy, labelRadius, _labelBorderPaint);

        float spindleRadius = MathF.Max(3.5f, 5f * scale);
        canvas.DrawCircle(cx, cy, spindleRadius, _spindlePaint);
        _spindleRimPaint.StrokeWidth = MathF.Max(1f, 1f * scale);
        canvas.DrawCircle(cx, cy, spindleRadius, _spindleRimPaint);
    }

    private void DrawTonearm(
        SKCanvas canvas,
        float scale,
        float pivotX,
        float pivotY,
        float elbowX,
        float elbowY,
        float tipX,
        float tipY)
    {
        if (_cachedArmTubeShader is null
            || MathF.Abs(_cachedArmPivotX - pivotX) > 0.5f
            || MathF.Abs(_cachedArmTipX - tipX) > 0.5f)
        {
            _cachedArmTubeShader?.Dispose();
            _cachedArmTubeShader = SKShader.CreateLinearGradient(
                new SKPoint(pivotX, pivotY),
                new SKPoint(tipX, tipY),
                [new SKColor(0xD4, 0xD8, 0xE0), new SKColor(0x8D, 0x95, 0xA3), new SKColor(0xE6, 0xE9, 0xEF)],
                null,
                SKShaderTileMode.Clamp);
            _armTubePaint.Shader = _cachedArmTubeShader;
            _cachedArmPivotX = pivotX;
            _cachedArmTipX = tipX;
        }

        canvas.Save();
        canvas.RotateDegrees(_tonearmAngleDeg, pivotX, pivotY);

        _armPath.Reset();
        _armPath.MoveTo(pivotX, pivotY);
        _armPath.LineTo(elbowX, elbowY);
        _armPath.LineTo(tipX, tipY);

        _armShadowPaint.StrokeWidth = 4.8f * scale;
        canvas.Save();
        canvas.Translate(2f * scale, 3.8f * scale);
        canvas.DrawPath(_armPath, _armShadowPaint);
        canvas.Restore();

        _armTubePaint.StrokeWidth = 3.4f * scale;
        canvas.DrawPath(_armPath, _armTubePaint);

        canvas.Save();
        canvas.RotateDegrees(-28f, tipX, tipY);
        float hw = 5.5f * scale;
        float hh = 10.5f * scale;
        canvas.DrawRoundRect(
            new SKRect(tipX - hw, tipY - hh * 0.35f, tipX + hw, tipY + hh * 0.65f),
            2f * scale,
            2f * scale,
            _cartridgePaint);
        canvas.DrawCircle(tipX, tipY + hh * 0.45f, 1.7f * scale, _armAccentPaint);
        canvas.Restore();

        float pivotRadius = 11.5f * scale;
        canvas.DrawCircle(pivotX, pivotY, pivotRadius, _armPivotPaint);
        _armPivotRimPaint.StrokeWidth = MathF.Max(1f, 1.2f * scale);
        canvas.DrawCircle(pivotX, pivotY, pivotRadius, _armPivotRimPaint);
        canvas.DrawCircle(pivotX, pivotY, 4.8f * scale, _armAccentPaint);

        canvas.Restore();
    }

    private void RenderHighContrast(
        SKCanvas canvas,
        int width,
        int height,
        float scale,
        VinylLayoutMode layoutMode,
        SKImage? coverImg,
        bool isDarkTheme)
    {
        var ink = isDarkTheme ? SKColors.White : SKColors.Black;
        _hcRingPaint.Color = ink;
        _hcRingPaint.StrokeWidth = MathF.Max(1.5f, 2f * scale);

        float cx = layoutMode == VinylLayoutMode.HalfEjectedSleeve ? width * 0.60f : width * 0.50f;
        float cy = height * 0.50f;
        float discRadius = MathF.Max(28f * scale, MathF.Min(width, height) * 0.42f);
        float labelRadius = discRadius * 0.36f;

        canvas.DrawCircle(cx, cy, discRadius, _hcRingPaint);
        _hcRingPaint.StrokeWidth = MathF.Max(1f, 1f * scale);
        canvas.DrawCircle(cx, cy, discRadius * 0.68f, _hcRingPaint);

        if (coverImg is not null)
        {
            _clipCirclePath.Reset();
            _clipCirclePath.AddCircle(cx, cy, labelRadius);
            canvas.Save();
            canvas.ClipPath(_clipCirclePath, antialias: true);
            canvas.DrawImage(
                coverImg,
                new SKRect(0, 0, coverImg.Width, coverImg.Height),
                new SKRect(cx - labelRadius, cy - labelRadius, cx + labelRadius, cy + labelRadius),
                HighSampling,
                _bitmapPaint);
            canvas.Restore();
        }
        canvas.DrawCircle(cx, cy, labelRadius, _hcRingPaint);
        canvas.DrawCircle(cx, cy, MathF.Max(3.5f, 5f * scale), _hcRingPaint);
    }

    private void EnsurePlatterBaseCache(
        int radiusPx,
        DesktopVinylStyle style,
        (byte R, byte G, byte B)? seed,
        bool isDarkTheme)
    {
        if (_cachedPlatterBase is not null
            && Math.Abs(_cachedPlatterRadiusPx - radiusPx) <= 1
            && _cachedPlatterStyle == style
            && _cachedPlatterSeed == seed
            && _cachedPlatterDark == isDarkTheme)
        {
            return;
        }

        _cachedPlatterBase?.Dispose();
        _cachedPlatterBase = null;

        int padPx = (int)MathF.Ceiling(radiusPx * 1.07f);
        int sizePx = Math.Max(32, padPx * 2);
        float cx = sizePx * 0.5f;
        float cy = sizePx * 0.5f;
        float r = radiusPx;

        using var surface = SKSurface.Create(new SKImageInfo(sizePx, sizePx, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null) return;

        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);

        using var discPaint = new SKPaint { IsAntialias = true };
        using var groovePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var rimPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        if (style == DesktopVinylStyle.CoverPearl)
        {
            var palette = VinylMaterialMath.DerivePearlPalette(seed, isDarkTheme);
            using var shadowShader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                r * 1.045f,
                [ToSkColor(palette.DropShadow), ToSkColor(palette.DropShadow), SKColors.Transparent],
                [0f, 0.90f, 1f],
                SKShaderTileMode.Clamp);
            using var shadowPaint = new SKPaint { IsAntialias = true, Shader = shadowShader };
            c.DrawCircle(cx, cy, r * 1.045f, shadowPaint);

            using var bodyShader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                r,
                Array.ConvertAll(palette.BodyStops, ToSkColor),
                [0f, 0.44f, 0.78f, 1f],
                SKShaderTileMode.Clamp);
            discPaint.Shader = bodyShader;
            c.DrawCircle(cx, cy, r, discPaint);

            groovePaint.Color = ToSkColor(palette.GrooveDark);
            groovePaint.StrokeWidth = MathF.Max(1f, r / 145f);
            using var ridgePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = ToSkColor(palette.GrooveLight),
                StrokeWidth = MathF.Max(0.85f, r / 165f),
            };
            float minInnerR = r * 0.37f;
            foreach (float frac in PearlGrooveFracs)
            {
                float gr = r * frac;
                c.DrawCircle(cx, cy, gr, groovePaint);
                c.DrawCircle(cx, cy, MathF.Max(minInnerR, gr - MathF.Max(0.9f, r / 150f)), ridgePaint);
            }

            rimPaint.Color = ToSkColor(palette.OuterRim);
            rimPaint.StrokeWidth = MathF.Max(1.3f, r / 115f);
            c.DrawCircle(cx, cy, r - rimPaint.StrokeWidth * 0.5f, rimPaint);
        }
        else if (style == DesktopVinylStyle.GrooveDiffraction)
        {
            using var bodyShader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                r,
                [
                    new SKColor(0x23, 0x26, 0x30),
                    new SKColor(0x0E, 0x10, 0x16),
                    new SKColor(0x1C, 0x1F, 0x29),
                    new SKColor(0x08, 0x09, 0x0E),
                ],
                [0f, 0.42f, 0.78f, 1f],
                SKShaderTileMode.Clamp);
            discPaint.Shader = bodyShader;
            c.DrawCircle(cx, cy, r, discPaint);

            groovePaint.Color = new SKColor(224, 232, 248, 26);
            groovePaint.StrokeWidth = MathF.Max(0.9f, r / 155f);
            foreach (float frac in DiffractionGrooveFracs)
            {
                c.DrawCircle(cx, cy, r * frac, groovePaint);
            }

            rimPaint.Color = new SKColor(210, 220, 240, 56);
            rimPaint.StrokeWidth = MathF.Max(1.2f, r / 120f);
            c.DrawCircle(cx, cy, r - rimPaint.StrokeWidth * 0.5f, rimPaint);
        }
        else
        {
            using var bodyShader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                r,
                [
                    new SKColor(0x18, 0x1A, 0x20),
                    new SKColor(0x0B, 0x0C, 0x10),
                    new SKColor(0x13, 0x15, 0x1B),
                    new SKColor(0x07, 0x08, 0x0B),
                ],
                [0f, 0.45f, 0.78f, 1f],
                SKShaderTileMode.Clamp);
            discPaint.Shader = bodyShader;
            c.DrawCircle(cx, cy, r, discPaint);

            groovePaint.Color = new SKColor(255, 255, 255, 20);
            groovePaint.StrokeWidth = MathF.Max(1f, r / 145f);
            foreach (float frac in ClassicGrooveFracs)
            {
                c.DrawCircle(cx, cy, r * frac, groovePaint);
            }

            rimPaint.Color = new SKColor(255, 255, 255, 38);
            rimPaint.StrokeWidth = MathF.Max(1.2f, r / 120f);
            c.DrawCircle(cx, cy, r - rimPaint.StrokeWidth * 0.5f, rimPaint);
        }

        _cachedPlatterBase = surface.Snapshot();
        _cachedPlatterRadiusPx = radiusPx;
        _cachedPlatterStyle = style;
        _cachedPlatterSeed = seed;
        _cachedPlatterDark = isDarkTheme;
    }

    private void EnsureOverlayShaders(
        float cx,
        float cy,
        float radius,
        DesktopVinylStyle style,
        (byte R, byte G, byte B)? seed,
        bool isDarkTheme)
    {
        if (MathF.Abs(_cachedOverlayCx - cx) <= 0.5f
            && MathF.Abs(_cachedOverlayCy - cy) <= 0.5f
            && MathF.Abs(_cachedOverlayRadius - radius) <= 0.5f
            && _cachedOverlayStyle == style
            && _cachedOverlaySeed == seed
            && _cachedOverlayDark == isDarkTheme)
        {
            return;
        }

        _cachedNacreSwirlShader?.Dispose();
        _cachedNacreSwirlShader = null;
        _cachedSheenShader?.Dispose();
        _cachedSheenShader = null;
        _cachedDiffractionPrimaryShader?.Dispose();
        _cachedDiffractionPrimaryShader = null;
        _cachedDiffractionSecondaryShader?.Dispose();
        _cachedDiffractionSecondaryShader = null;

        var center = new SKPoint(cx, cy);
        if (style == DesktopVinylStyle.CoverPearl)
        {
            var palette = VinylMaterialMath.DerivePearlPalette(seed, isDarkTheme);
            _cachedNacreSwirlShader = SKShader.CreateSweepGradient(
                center,
                [
                    ToSkColor(palette.SwirlWarm),
                    SKColors.Transparent,
                    ToSkColor(palette.SwirlCool),
                    SKColors.Transparent,
                    ToSkColor(palette.SwirlWarm),
                ],
                [0f, 0.25f, 0.52f, 0.78f, 1f]);

            _cachedSheenShader = SKShader.CreateSweepGradient(
                center,
                [
                    new SKColor(255, 255, 255, 112),
                    SKColors.Transparent,
                    SKColors.Transparent,
                    new SKColor(255, 255, 255, 86),
                    SKColors.Transparent,
                    new SKColor(255, 255, 255, 112),
                ],
                [0f, 0.14f, 0.44f, 0.56f, 0.80f, 1f]);
        }
        else if (style == DesktopVinylStyle.GrooveDiffraction)
        {
            _cachedDiffractionPrimaryShader = SKShader.CreateSweepGradient(
                center,
                Array.ConvertAll(VinylMaterialMath.DiffractionSpectrumStops(), ToSkColor),
                VinylMaterialMath.DiffractionSpectrumPositions());

            _cachedDiffractionSecondaryShader = SKShader.CreateSweepGradient(
                center,
                [
                    new SKColor(175, 220, 255, 38),
                    SKColors.Transparent,
                    SKColors.Transparent,
                    new SKColor(240, 188, 224, 36),
                    SKColors.Transparent,
                    new SKColor(175, 220, 255, 38),
                ],
                [0f, 0.08f, 0.47f, 0.53f, 0.62f, 1f]);

            _cachedSheenShader = SKShader.CreateSweepGradient(
                center,
                [
                    new SKColor(255, 255, 255, 52),
                    SKColors.Transparent,
                    SKColors.Transparent,
                    new SKColor(255, 255, 255, 44),
                    SKColors.Transparent,
                    new SKColor(255, 255, 255, 52),
                ],
                [0f, 0.10f, 0.46f, 0.55f, 0.76f, 1f]);
        }
        else
        {
            _cachedSheenShader = SKShader.CreateSweepGradient(
                center,
                [
                    new SKColor(255, 255, 255, 34),
                    SKColors.Transparent,
                    SKColors.Transparent,
                    new SKColor(255, 255, 255, 28),
                    SKColors.Transparent,
                    new SKColor(255, 255, 255, 34),
                ],
                [0f, 0.16f, 0.44f, 0.54f, 0.78f, 1f]);
        }

        _cachedOverlayCx = cx;
        _cachedOverlayCy = cy;
        _cachedOverlayRadius = radius;
        _cachedOverlayStyle = style;
        _cachedOverlaySeed = seed;
        _cachedOverlayDark = isDarkTheme;
    }

    private static SKColor ToSkColor(ArgbColor color) => new(color.R, color.G, color.B, color.A);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        SwapCoverSnapshot(null);

        _cachedPlatterBase?.Dispose();
        _cachedPlatterBase = null;
        _cachedNacreSwirlShader?.Dispose();
        _cachedNacreSwirlShader = null;
        _cachedSheenShader?.Dispose();
        _cachedSheenShader = null;
        _cachedDiffractionPrimaryShader?.Dispose();
        _cachedDiffractionPrimaryShader = null;
        _cachedDiffractionSecondaryShader?.Dispose();
        _cachedDiffractionSecondaryShader = null;
        _cachedArmTubeShader?.Dispose();
        _cachedArmTubeShader = null;

        _bitmapPaint.Dispose();
        _overlayPaint.Dispose();
        _labelFallbackPaint.Dispose();
        _labelBorderPaint.Dispose();
        _spindlePaint.Dispose();
        _spindleRimPaint.Dispose();
        _sleeveBorderPaint.Dispose();
        _sleeveShadowPaint.Dispose();
        _sleevePlaceholderPaint.Dispose();
        _armShadowPaint.Dispose();
        _armTubePaint.Dispose();
        _armPivotPaint.Dispose();
        _armPivotRimPaint.Dispose();
        _armAccentPaint.Dispose();
        _cartridgePaint.Dispose();
        _hcRingPaint.Dispose();
        _armPath.Dispose();
        _clipCirclePath.Dispose();
    }
}
