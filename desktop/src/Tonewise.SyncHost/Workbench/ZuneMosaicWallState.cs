namespace Tonewise.SyncHost.Workbench;

public sealed class ZuneMosaicCell
{
    public int Col { get; }
    public int Row { get; }
    public int Span { get; }
    public int CurrentCoverIndex { get; set; }
    public int NextCoverIndex { get; set; }
    public float TransitionProgress { get; set; }
    public float BreathPhase { get; }
    public int AlphaBias { get; }

    public ZuneMosaicCell(
        int col,
        int row,
        int span,
        int initialCoverIndex,
        float breathPhase,
        int alphaBias)
    {
        Col = col;
        Row = row;
        Span = span;
        CurrentCoverIndex = initialCoverIndex;
        NextCoverIndex = initialCoverIndex;
        TransitionProgress = 1f;
        BreathPhase = breathPhase;
        AlphaBias = alphaBias;
    }

    public bool IsTransitioning => TransitionProgress < 1f;

    public float EasedTransition =>
        TransitionProgress >= 1f
            ? 1f
            : TransitionProgress * TransitionProgress * (3f - 2f * TransitionProgress);
}

public sealed class ZuneMosaicWallState
{
    public const float DriftSpeedPxPerSec = 14f;
    public const double SwapIntervalSec = 2.0;
    public const float TransitionDurationSec = 0.85f;

    private readonly List<ZuneMosaicCell> _cells = [];
    private int _layoutTileSize;
    private int _layoutCols;
    private int _layoutRows;
    private int _layoutCoverCount;
    private double _swapAccumulatorSec;
    private int _rngState = 0x5A17C0DE;

    public IReadOnlyList<ZuneMosaicCell> Cells => _cells;
    public int BaseTileSize { get; private set; }
    public int PatternCols { get; private set; }
    public int PatternRows { get; private set; }
    public int PatternWidthPx => PatternCols * BaseTileSize;
    public float DriftXPx { get; private set; }
    public float DriftYPx { get; private set; }
    public double ElapsedSec { get; private set; }

    public void EnsureLayout(int viewportWidth, int viewportHeight, int coverCount)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || coverCount <= 0)
        {
            _cells.Clear();
            _layoutCols = 0;
            _layoutRows = 0;
            _layoutCoverCount = 0;
            BaseTileSize = 0;
            PatternCols = 0;
            PatternRows = 0;
            return;
        }

        int tileSize = Math.Clamp(Math.Min(viewportWidth, viewportHeight) / 6, 92, 148);
        int cols = Math.Max((int)Math.Ceiling(viewportWidth / (double)tileSize) + 4, 12);
        int rows = (int)Math.Ceiling(viewportHeight / (double)tileSize) + 2;

        if (_layoutTileSize == tileSize
            && _layoutCols == cols
            && _layoutRows == rows
            && _layoutCoverCount == coverCount
            && _cells.Count > 0)
        {
            return;
        }

        _layoutTileSize = tileSize;
        _layoutCols = cols;
        _layoutRows = rows;
        _layoutCoverCount = coverCount;
        BaseTileSize = tileSize;
        PatternCols = cols;
        PatternRows = rows;
        if (PatternWidthPx > 0)
        {
            DriftXPx %= PatternWidthPx;
        }
        else
        {
            DriftXPx = 0f;
        }

        _cells.Clear();
        bool[,] occupied = new bool[rows, cols];
        int cellIdx = 0;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (occupied[r, c]) continue;

                bool canPromote =
                    c + 1 < cols
                    && r + 1 < rows
                    && c % 3 != 2
                    && !occupied[r, c + 1]
                    && !occupied[r + 1, c]
                    && !occupied[r + 1, c + 1]
                    && (((r * 31 + c * 17) % 11 == 0) || ((r * 13 + c * 29) % 13 == 0));

                int span = canPromote ? 2 : 1;
                for (int dr = 0; dr < span; dr++)
                {
                    for (int dc = 0; dc < span; dc++)
                    {
                        occupied[r + dr, c + dc] = true;
                    }
                }

                int coverIdx = cellIdx % coverCount;
                float phase = ((r * 19 + c * 23) % 628) / 100f;
                int alphaBias = span == 2 ? 14 : (((r * 7 + c * 13) % 25) - 12);
                _cells.Add(new ZuneMosaicCell(c, r, span, coverIdx, phase, alphaBias));
                cellIdx++;
            }
        }
    }

    public bool Advance(double deltaSeconds, int coverCount)
    {
        double dt = Math.Clamp(deltaSeconds, 0.0, 0.1);
        if (dt <= 0.0 || _cells.Count == 0 || coverCount <= 0 || PatternWidthPx <= 0)
        {
            return false;
        }

        ElapsedSec += dt;
        DriftXPx = (DriftXPx + (float)(DriftSpeedPxPerSec * dt)) % PatternWidthPx;
        DriftYPx = MathF.Sin((float)(ElapsedSec * 0.14)) * (BaseTileSize * 0.18f);

        float step = (float)(dt / TransitionDurationSec);
        for (int i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (!cell.IsTransitioning) continue;
            float next = cell.TransitionProgress + step;
            if (next >= 1f)
            {
                cell.CurrentCoverIndex = cell.NextCoverIndex;
                cell.TransitionProgress = 1f;
            }
            else
            {
                cell.TransitionProgress = next;
            }
        }

        if (coverCount > 1)
        {
            _swapAccumulatorSec += dt;
            if (_swapAccumulatorSec >= SwapIntervalSec)
            {
                _swapAccumulatorSec %= SwapIntervalSec;
                TriggerStaggeredSwaps(count: 2, coverCount);
            }
        }

        return true;
    }

    public void TriggerManualSwap(int coverCount)
    {
        if (coverCount <= 1 || _cells.Count == 0) return;
        TriggerStaggeredSwaps(count: 2, coverCount);
    }

    private void TriggerStaggeredSwaps(int count, int coverCount)
    {
        if (_cells.Count == 0 || coverCount <= 1) return;

        int picked = 0;
        int n = _cells.Count;
        int start = NextDeterministicInt(n);
        int stride = n > 7 ? 7 : 1;

        for (int attempt = 0; attempt < n && picked < count; attempt++)
        {
            int idx = (start + attempt * stride) % n;
            var cell = _cells[idx];
            if (cell.IsTransitioning) continue;

            int delta = 1 + NextDeterministicInt(coverCount - 1);
            int nextCover = (cell.CurrentCoverIndex + delta) % coverCount;
            cell.NextCoverIndex = nextCover;
            cell.TransitionProgress = 0f;
            picked++;
        }
    }

    private int NextDeterministicInt(int maxExclusive)
    {
        if (maxExclusive <= 1) return 0;
        uint x = unchecked((uint)_rngState);
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        if (x == 0) x = 0x6D2B79F5u;
        _rngState = unchecked((int)x);
        return (int)(x % (uint)maxExclusive);
    }
}
