using SkiaSharp;

namespace Tonewise.Desktop.Rendering;

public sealed class ZuneMosaicRenderer : IDisposable
{
    private const int TileDecodeSize = 220;
    private const int MaxCovers = 64;
    private static readonly SKSamplingOptions HighSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly object _lock = new();
    private readonly List<SKImage> _tiles = [];
    private string _loadedSignature = "";
    private int _swapOffset;
    private int _disposed;
    private CancellationTokenSource? _loadCts;

    public bool HasCovers
    {
        get
        {
            lock (_lock)
            {
                return _tiles.Count > 0;
            }
        }
    }

    public void UpdateCoversAsync(IReadOnlyList<string> coverPaths, Action onReady)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var filtered = coverPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCovers)
            .ToList();

        string sig = string.Join("|", filtered);
        if (string.Equals(sig, _loadedSignature, StringComparison.Ordinal))
        {
            return;
        }
        _loadedSignature = sig;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        _ = Task.Run(() =>
        {
            var decoded = new List<SKImage>(filtered.Count);
            try
            {
                foreach (string path in filtered)
                {
                    if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) break;
                    if (!File.Exists(path)) continue;

                    try
                    {
                        using var raw = SKBitmap.Decode(path);
                        if (raw is null || raw.Width <= 0 || raw.Height <= 0) continue;

                        using var thumb = new SKBitmap(TileDecodeSize, TileDecodeSize, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using (var canvas = new SKCanvas(thumb))
                        {
                            canvas.Clear(SKColors.Transparent);
                            int side = Math.Min(raw.Width, raw.Height);
                            int sx = (raw.Width - side) / 2;
                            int sy = (raw.Height - side) / 2;
                            var srcRect = new SKRect(sx, sy, sx + side, sy + side);
                            var dstRect = new SKRect(0, 0, TileDecodeSize, TileDecodeSize);
                            using var rawImg = SKImage.FromBitmap(raw);
                            using var paint = new SKPaint { IsAntialias = true };
                            canvas.DrawImage(rawImg, srcRect, dstRect, HighSampling, paint);
                        }

                        var tileImg = SKImage.FromPixelCopy(thumb.Info, thumb.GetPixels(), thumb.RowBytes);
                        if (tileImg is not null)
                        {
                            decoded.Add(tileImg);
                        }
                    }
                    catch
                    {
                        // Skip unreadable image file
                    }
                }

                if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                {
                    foreach (var img in decoded) img.Dispose();
                    return;
                }

                lock (_lock)
                {
                    foreach (var old in _tiles) old.Dispose();
                    _tiles.Clear();
                    _tiles.AddRange(decoded);
                }

                onReady();
            }
            catch
            {
                foreach (var img in decoded) img.Dispose();
            }
        }, ct);
    }

    public void AdvanceSwapTick()
    {
        lock (_lock)
        {
            if (_tiles.Count > 1)
            {
                _swapOffset = (_swapOffset + 1) % _tiles.Count;
            }
        }
    }

    public void Render(SKCanvas canvas, int width, int height, bool isDarkTheme, (byte R, byte G, byte B) accentRgb)
    {
        if (width <= 0 || height <= 0) return;

        canvas.Clear(isDarkTheme ? SKColors.Black : SKColors.White);

        List<SKImage> snapshot;
        int offset;
        lock (_lock)
        {
            snapshot = [.. _tiles];
            offset = _swapOffset;
        }

        if (snapshot.Count == 0)
        {
            DrawMinimalGeometricGrid(canvas, width, height, isDarkTheme, accentRgb);
            return;
        }

        int tileSize = Math.Clamp(Math.Min(width, height) / 5, 108, 176);
        int cols = (int)Math.Ceiling(width / (double)tileSize);
        int rows = (int)Math.Ceiling(height / (double)tileSize);

        byte baseAlpha = isDarkTheme ? (byte)128 : (byte)96;
        using var tilePaint = new SKPaint
        {
            IsAntialias = false,
        };
        using var cellBorderPaint = new SKPaint
        {
            Color = isDarkTheme ? new SKColor(255, 255, 255, 18) : new SKColor(0, 0, 0, 16),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = false,
        };

        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var img = snapshot[(idx + offset) % snapshot.Count];
                int jitter = ((r * 7 + c * 13) % 29) - 14;
                byte cellAlpha = (byte)Math.Clamp(baseAlpha + jitter, 60, 160);
                tilePaint.Color = new SKColor(255, 255, 255, cellAlpha);

                var dst = new SKRect(c * tileSize, r * tileSize, (c + 1) * tileSize, (r + 1) * tileSize);
                dst.Inflate(-1f, -1f);
                canvas.DrawImage(img, dst, HighSampling, tilePaint);
                canvas.DrawRect(dst, cellBorderPaint);
                idx++;
            }
        }

        DrawScrim(canvas, width, height, isDarkTheme, accentRgb);
    }

    private static void DrawScrim(SKCanvas canvas, int width, int height, bool isDarkTheme, (byte R, byte G, byte B) accentRgb)
    {
        // Left-weighted editorial vignette: >= 7:1 contrast on left hero metadata while keeping center/right mosaic vivid
        SKColor[] stops = isDarkTheme
            ? [
                new SKColor(0, 0, 0, 214),
                new SKColor(0, 0, 0, 128),
                new SKColor(0, 0, 0, 82),
                new SKColor(0, 0, 0, 132),
              ]
            : [
                new SKColor(255, 255, 255, 220),
                new SKColor(255, 255, 255, 134),
                new SKColor(255, 255, 255, 88),
                new SKColor(255, 255, 255, 136),
              ];

        using var scrimPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, height * 0.5f),
                stops,
                [0f, 0.36f, 0.72f, 1f],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, width, height, scrimPaint);

        using var accentGlowPaint = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(width * 0.18f, height * 0.42f),
                Math.Max(width, height) * 0.48f,
                [
                    new SKColor(accentRgb.R, accentRgb.G, accentRgb.B, isDarkTheme ? (byte)24 : (byte)16),
                    new SKColor(accentRgb.R, accentRgb.G, accentRgb.B, 0),
                ],
                [0f, 1f],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, width, height, accentGlowPaint);
    }

    private static void DrawMinimalGeometricGrid(SKCanvas canvas, int width, int height, bool isDarkTheme, (byte R, byte G, byte B) accentRgb)
    {
        int step = 120;
        var lineColor = isDarkTheme ? new SKColor(255, 255, 255, 16) : new SKColor(0, 0, 0, 16);
        using var linePaint = new SKPaint
        {
            Color = lineColor,
            StrokeWidth = 1f,
            IsAntialias = false,
        };

        for (int x = 0; x < width; x += step)
        {
            canvas.DrawLine(x, 0, x, height, linePaint);
        }
        for (int y = 0; y < height; y += step)
        {
            canvas.DrawLine(0, y, width, y, linePaint);
        }

        using var accentBlock = new SKPaint
        {
            Color = new SKColor(accentRgb.R, accentRgb.G, accentRgb.B, isDarkTheme ? (byte)22 : (byte)16),
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawRect(step * 2, step, step, step, accentBlock);
        canvas.DrawRect(width - step * 2, height - step * 2, step, step, accentBlock);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        lock (_lock)
        {
            foreach (var img in _tiles) img.Dispose();
            _tiles.Clear();
        }
    }
}
