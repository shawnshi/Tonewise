using SkiaSharp;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.Desktop.Rendering;

public sealed class ZuneMosaicRenderer : IDisposable
{
    private const int TileDecodeSize = 220;
    private const int MaxCovers = 64;
    private static readonly SKSamplingOptions HighSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly SKSamplingOptions MotionSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly struct CellDrawItem
    {
        public readonly int Col;
        public readonly int Row;
        public readonly int Span;
        public readonly int CurrentCoverIndex;
        public readonly int NextCoverIndex;
        public readonly float EasedTransition;
        public readonly float BreathPhase;
        public readonly int AlphaBias;
        public readonly bool IsTransitioning;

        public CellDrawItem(ZuneMosaicCell cell)
        {
            Col = cell.Col;
            Row = cell.Row;
            Span = cell.Span;
            CurrentCoverIndex = cell.CurrentCoverIndex;
            NextCoverIndex = cell.NextCoverIndex;
            EasedTransition = cell.EasedTransition;
            BreathPhase = cell.BreathPhase;
            AlphaBias = cell.AlphaBias;
            IsTransitioning = cell.IsTransitioning;
        }
    }

    private sealed class MosaicTileSnapshot(List<SKImage> tiles)
    {
        private int _refCount = 1;
        public IReadOnlyList<SKImage> Tiles { get; } = tiles;

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
                foreach (var img in Tiles) img.Dispose();
            }
        }
    }

    private readonly object _lock = new();
    private readonly ZuneMosaicWallState _wallState = new();
    private readonly List<CellDrawItem> _drawBuffer = [];
    private readonly SKPaint _tilePaint = new() { IsAntialias = false };
    private readonly SKPaint _cellBorderPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = false };
    private readonly SKPaint _scrimPaint = new();
    private readonly SKPaint _accentGlowPaint = new();

    private MosaicTileSnapshot? _currentSnapshot;
    private string _loadedSignature = "";
    private int _disposed;
    private CancellationTokenSource? _loadCts;

    private SKShader? _cachedLinearScrimShader;
    private SKShader? _cachedAccentGlowShader;
    private int _cachedScrimWidth = -1;
    private int _cachedScrimHeight = -1;
    private bool _cachedScrimDark;
    private (byte R, byte G, byte B) _cachedAccentRgb;

    public bool HasCovers
    {
        get
        {
            lock (_lock)
            {
                return _currentSnapshot is { Tiles.Count: > 0 };
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

                var newSnapshot = new MosaicTileSnapshot(decoded);
                MosaicTileSnapshot? oldSnapshot;
                lock (_lock)
                {
                    if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                    {
                        newSnapshot.Release();
                        return;
                    }
                    oldSnapshot = _currentSnapshot;
                    _currentSnapshot = newSnapshot;
                }
                oldSnapshot?.Release();

                onReady();
            }
            catch
            {
                foreach (var img in decoded) img.Dispose();
            }
        }, ct);
    }

    public bool AdvanceFrame(double deltaSeconds)
    {
        lock (_lock)
        {
            if (_currentSnapshot is not { Tiles.Count: > 0 } snap)
            {
                return false;
            }
            return _wallState.Advance(deltaSeconds, snap.Tiles.Count);
        }
    }

    public void AdvanceSwapTick()
    {
        lock (_lock)
        {
            if (_currentSnapshot is { Tiles.Count: > 1 } snap)
            {
                _wallState.TriggerManualSwap(snap.Tiles.Count);
            }
        }
    }

    public void Render(SKCanvas canvas, int width, int height, bool isDarkTheme, (byte R, byte G, byte B) accentRgb)
    {
        if (width <= 0 || height <= 0 || Volatile.Read(ref _disposed) != 0) return;

        canvas.Clear(isDarkTheme ? SKColors.Black : SKColors.White);

        MosaicTileSnapshot? snapshot = null;
        int baseTile = 0;
        int patternWidth = 0;
        float driftX = 0f;
        float driftY = 0f;
        float elapsedSec = 0f;

        lock (_lock)
        {
            if (_currentSnapshot is { Tiles.Count: > 0 } cur && cur.TryAddRef())
            {
                snapshot = cur;
                _wallState.EnsureLayout(width, height, cur.Tiles.Count);
                baseTile = _wallState.BaseTileSize;
                patternWidth = _wallState.PatternWidthPx;
                driftX = _wallState.DriftXPx;
                driftY = _wallState.DriftYPx;
                elapsedSec = (float)_wallState.ElapsedSec;
                _drawBuffer.Clear();
                var cells = _wallState.Cells;
                for (int i = 0; i < cells.Count; i++)
                {
                    _drawBuffer.Add(new CellDrawItem(cells[i]));
                }
            }
        }

        if (snapshot is null || baseTile <= 0 || patternWidth <= 0)
        {
            snapshot?.Release();
            DrawMinimalGeometricGrid(canvas, width, height, isDarkTheme, accentRgb);
            return;
        }

        try
        {
            var tiles = snapshot.Tiles;
            int tileCount = tiles.Count;
            byte baseAlpha = isDarkTheme ? (byte)124 : (byte)94;
            _cellBorderPaint.Color = isDarkTheme ? new SKColor(255, 255, 255, 18) : new SKColor(0, 0, 0, 16);

            float yOrigin = driftY - baseTile * 0.25f;
            for (int pass = 0; pass < 2; pass++)
            {
                float passOffsetX = pass == 0 ? -driftX : (-driftX + patternWidth);
                if (passOffsetX >= width || passOffsetX + patternWidth <= 0f) continue;

                for (int i = 0; i < _drawBuffer.Count; i++)
                {
                    var cell = _drawBuffer[i];
                    float size = cell.Span * baseTile;
                    float left = passOffsetX + cell.Col * baseTile;
                    float right = left + size;
                    if (right <= 0f || left >= width) continue;

                    float top = yOrigin + cell.Row * baseTile;
                    float bottom = top + size;
                    if (bottom <= 0f || top >= height) continue;

                    var dst = new SKRect(left + 1f, top + 1f, right - 1f, bottom - 1f);
                    float breath = MathF.Sin(elapsedSec * 0.55f + cell.BreathPhase) * 10f;
                    byte cellAlpha = (byte)Math.Clamp(baseAlpha + cell.AlphaBias + breath, 56f, 168f);

                    if (!cell.IsTransitioning)
                    {
                        _tilePaint.Color = new SKColor(255, 255, 255, cellAlpha);
                        canvas.DrawImage(tiles[cell.CurrentCoverIndex % tileCount], dst, MotionSampling, _tilePaint);
                    }
                    else
                    {
                        float t = cell.EasedTransition;
                        byte outAlpha = (byte)Math.Clamp(cellAlpha * (1f - t), 0f, 255f);
                        byte inAlpha = (byte)Math.Clamp(cellAlpha * t, 0f, 255f);

                        if (outAlpha > 0)
                        {
                            _tilePaint.Color = new SKColor(255, 255, 255, outAlpha);
                            canvas.DrawImage(tiles[cell.CurrentCoverIndex % tileCount], dst, MotionSampling, _tilePaint);
                        }

                        if (inAlpha > 0)
                        {
                            float scale = 1.03f - 0.03f * t;
                            float cx = dst.MidX;
                            float cy = dst.MidY;
                            float halfW = (dst.Width * 0.5f) * scale;
                            float halfH = (dst.Height * 0.5f) * scale;
                            var zoomedDst = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);

                            canvas.Save();
                            canvas.ClipRect(dst);
                            _tilePaint.Color = new SKColor(255, 255, 255, inAlpha);
                            canvas.DrawImage(tiles[cell.NextCoverIndex % tileCount], zoomedDst, MotionSampling, _tilePaint);
                            canvas.Restore();
                        }
                    }

                    canvas.DrawRect(dst, _cellBorderPaint);
                }
            }

            DrawCachedScrim(canvas, width, height, isDarkTheme, accentRgb);
        }
        finally
        {
            snapshot.Release();
        }
    }

    private void DrawCachedScrim(SKCanvas canvas, int width, int height, bool isDarkTheme, (byte R, byte G, byte B) accentRgb)
    {
        if (_cachedLinearScrimShader is null
            || _cachedAccentGlowShader is null
            || _cachedScrimWidth != width
            || _cachedScrimHeight != height
            || _cachedScrimDark != isDarkTheme
            || _cachedAccentRgb != accentRgb)
        {
            _cachedLinearScrimShader?.Dispose();
            _cachedAccentGlowShader?.Dispose();

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

            _cachedLinearScrimShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, height * 0.5f),
                stops,
                [0f, 0.36f, 0.72f, 1f],
                SKShaderTileMode.Clamp);

            _cachedAccentGlowShader = SKShader.CreateRadialGradient(
                new SKPoint(width * 0.18f, height * 0.42f),
                Math.Max(width, height) * 0.48f,
                [
                    new SKColor(accentRgb.R, accentRgb.G, accentRgb.B, isDarkTheme ? (byte)24 : (byte)16),
                    new SKColor(accentRgb.R, accentRgb.G, accentRgb.B, 0),
                ],
                [0f, 1f],
                SKShaderTileMode.Clamp);

            _scrimPaint.Shader = _cachedLinearScrimShader;
            _accentGlowPaint.Shader = _cachedAccentGlowShader;
            _cachedScrimWidth = width;
            _cachedScrimHeight = height;
            _cachedScrimDark = isDarkTheme;
            _cachedAccentRgb = accentRgb;
        }

        canvas.DrawRect(0, 0, width, height, _scrimPaint);
        canvas.DrawRect(0, 0, width, height, _accentGlowPaint);
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
        MosaicTileSnapshot? oldSnapshot;
        lock (_lock)
        {
            oldSnapshot = _currentSnapshot;
            _currentSnapshot = null;
        }
        oldSnapshot?.Release();
        _cachedLinearScrimShader?.Dispose();
        _cachedLinearScrimShader = null;
        _cachedAccentGlowShader?.Dispose();
        _cachedAccentGlowShader = null;
        _tilePaint.Dispose();
        _cellBorderPaint.Dispose();
        _scrimPaint.Dispose();
        _accentGlowPaint.Dispose();
    }
}
