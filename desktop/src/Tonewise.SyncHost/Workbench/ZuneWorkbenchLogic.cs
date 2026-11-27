namespace Tonewise.SyncHost.Workbench;

public sealed record QuickplayPinRecord(
    string PinId,
    string Kind,
    string TargetKey,
    string Title,
    string Subtitle,
    string? CoverPath,
    int SortOrder,
    long PinnedAtMs
);

public sealed record QuickplayTrackSeed(
    string TrackUid,
    string Title,
    string Album,
    string ArtistsDisplay,
    string WorkName,
    string ConductorsDisplay,
    int Rating,
    int EffectivePlayCount,
    long LastPlayedTsMs,
    long MtimeMs,
    int? Year,
    string? CoverArtPath,
    int DiscNumber = 0,
    int? MovementNumber = null,
    int TrackNumber = 0,
    string RelPath = ""
);

public sealed record QuickplayCardSeed(
    string TileId,
    string Kind,
    string TargetKey,
    string Title,
    string Subtitle,
    string BadgeText,
    string? CoverArtPath,
    IReadOnlyList<string> TrackUids
);

public static class ZuneColorMath
{
    public static readonly (byte R, byte G, byte B) DefaultDarkAccentRgb = (0xE5, 0x00, 0x6B);
    public static readonly (byte R, byte G, byte B) DefaultLightAccentRgb = (0xC2, 0x00, 0x52);

    private static readonly (byte R, byte G, byte B) BlackRgb = (0x00, 0x00, 0x00);
    private static readonly (byte R, byte G, byte B) WhiteRgb = (0xFF, 0xFF, 0xFF);

    public static (byte R, byte G, byte B) ContrastingOnColor(byte r, byte g, byte b) =>
        ComputeContrastRatio(WhiteRgb, (r, g, b)) >= ComputeContrastRatio(BlackRgb, (r, g, b))
            ? WhiteRgb
            : BlackRgb;

    public static double ComputeRelativeLuminance(byte r, byte g, byte b)
    {
        static double Linearize(byte c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        double rl = Linearize(r);
        double gl = Linearize(g);
        double bl = Linearize(b);
        return 0.2126 * rl + 0.7152 * gl + 0.0722 * bl;
    }

    public static double ComputeContrastRatio((byte R, byte G, byte B) fg, (byte R, byte G, byte B) bg)
    {
        double l1 = ComputeRelativeLuminance(fg.R, fg.G, fg.B);
        double l2 = ComputeRelativeLuminance(bg.R, bg.G, bg.B);
        double lighter = Math.Max(l1, l2);
        double darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static (byte R, byte G, byte B) ClampAccentForTheme(byte r, byte g, byte b, bool isDarkTheme)
    {
        var (h, s, l) = RgbToHsl(r, g, b);
        if (s < 0.18)
        {
            return isDarkTheme ? DefaultDarkAccentRgb : DefaultLightAccentRgb;
        }

        var bg = isDarkTheme ? BlackRgb : WhiteRgb;
        const double targetContrast = 4.55;

        double clampedS = Math.Clamp(s, 0.45, 0.96);
        double curL = isDarkTheme ? Math.Clamp(l, 0.48, 0.85) : Math.Clamp(l, 0.18, 0.48);
        var candidate = HslToRgb(h, clampedS, curL);
        if (ComputeContrastRatio(candidate, bg) >= targetContrast)
        {
            return candidate;
        }

        // Binary search lightness toward higher contrast while preserving hue
        double lo = isDarkTheme ? curL : 0.04;
        double hi = isDarkTheme ? 0.96 : curL;
        var best = isDarkTheme ? DefaultDarkAccentRgb : DefaultLightAccentRgb;
        for (int i = 0; i < 20; i++)
        {
            double mid = (lo + hi) * 0.5;
            var test = HslToRgb(h, clampedS, mid);
            double ratio = ComputeContrastRatio(test, bg);
            if (ratio >= targetContrast)
            {
                best = test;
                if (isDarkTheme)
                    hi = mid; // find minimum lightness that passes
                else
                    lo = mid; // find maximum lightness that passes
            }
            else
            {
                if (isDarkTheme)
                    lo = mid;
                else
                    hi = mid;
            }
        }

        return ComputeContrastRatio(best, bg) >= 4.5
            ? best
            : (isDarkTheme ? DefaultDarkAccentRgb : DefaultLightAccentRgb);
    }

    public static (byte R, byte G, byte B)? SelectDominantVibrantRgb(ReadOnlySpan<(byte R, byte G, byte B)> pixels)
    {
        if (pixels.IsEmpty) return null;

        // Quantize hue into 24 buckets (15 deg each) and accumulate weighted RGB
        Span<double> bucketWeight = stackalloc double[24];
        Span<double> bucketR = stackalloc double[24];
        Span<double> bucketG = stackalloc double[24];
        Span<double> bucketB = stackalloc double[24];

        for (int i = 0; i < pixels.Length; i++)
        {
            var (r, g, b) = pixels[i];
            var (h, s, l) = RgbToHsl(r, g, b);
            if (s < 0.22 || l < 0.12 || l > 0.88) continue;

            double midLumWeight = 1.0 - Math.Abs(l - 0.50) * 1.4;
            if (midLumWeight <= 0.05) continue;

            double weight = s * s * midLumWeight;
            int bucket = Math.Clamp((int)(h * 24.0) % 24, 0, 23);
            bucketWeight[bucket] += weight;
            bucketR[bucket] += r * weight;
            bucketG[bucket] += g * weight;
            bucketB[bucket] += b * weight;
        }

        int bestBucket = -1;
        double bestScore = 0.0;
        for (int bIdx = 0; bIdx < 24; bIdx++)
        {
            if (bucketWeight[bIdx] > bestScore)
            {
                bestScore = bucketWeight[bIdx];
                bestBucket = bIdx;
            }
        }

        if (bestBucket < 0 || bestScore <= 0.0) return null;
        return (
            (byte)Math.Clamp(Math.Round(bucketR[bestBucket] / bestScore), 0, 255),
            (byte)Math.Clamp(Math.Round(bucketG[bestBucket] / bestScore), 0, 255),
            (byte)Math.Clamp(Math.Round(bucketB[bestBucket] / bestScore), 0, 255));
    }

    private static (double H, double S, double L) RgbToHsl(byte rByte, byte gByte, byte bByte)
    {
        double r = rByte / 255.0;
        double g = gByte / 255.0;
        double b = bByte / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) * 0.5;
        if (Math.Abs(max - min) < 1e-6) return (0.0, 0.0, l);

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6.0 : 0.0);
        else if (max == g) h = (b - r) / d + 2.0;
        else h = (r - g) / d + 4.0;
        h /= 6.0;
        return (h, s, l);
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        if (s <= 1e-6)
        {
            byte gray = (byte)Math.Clamp(Math.Round(l * 255.0), 0, 255);
            return (gray, gray, gray);
        }

        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;
        byte r = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255.0), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h) * 255.0), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255.0), 0, 255);
        return (r, g, b);
    }
}

public static class QuickplayAggregator
{
    private const long NinetyDaysMs = 90L * 86_400_000L;

    public static (
        IReadOnlyList<QuickplayCardSeed> History,
        IReadOnlyList<QuickplayCardSeed> NewlyAdded,
        IReadOnlyList<QuickplayCardSeed> Rediscovery) BuildAll(
        IEnumerable<QuickplayTrackSeed> tracks,
        long nowMs,
        int maxCount = 8)
    {
        var buckets = GroupAlbumsOrWorks(tracks);
        int take = Math.Max(0, maxCount);

        var history = buckets
            .Where(g => g.MaxLastPlayedTsMs > 0)
            .OrderByDescending(g => g.MaxLastPlayedTsMs)
            .ThenByDescending(g => g.TotalPlayCount)
            .Take(take)
            .Select(g => new QuickplayCardSeed(
                TileId: $"history::{g.GroupKey}",
                Kind: g.IsWork ? "work" : "album",
                TargetKey: g.GroupKey,
                Title: g.Title,
                Subtitle: g.Subtitle,
                BadgeText: $"{g.TrackUids.Count} 首",
                CoverArtPath: g.CoverArtPath,
                TrackUids: g.TrackUids))
            .ToList();

        var newlyAdded = buckets
            .OrderByDescending(g => g.MaxMtimeMs)
            .ThenByDescending(g => g.MaxYear ?? 0)
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(take)
            .Select(g => new QuickplayCardSeed(
                TileId: $"new::{g.GroupKey}",
                Kind: g.IsWork ? "work" : "album",
                TargetKey: g.GroupKey,
                Title: g.Title,
                Subtitle: g.Subtitle,
                BadgeText: g.MaxYear is > 0 ? $"{g.MaxYear}" : $"{g.TrackUids.Count} 首",
                CoverArtPath: g.CoverArtPath,
                TrackUids: g.TrackUids))
            .ToList();

        var rediscovery = buckets
            .Where(g => g.MaxLastPlayedTsMs <= 0 || (nowMs - g.MaxLastPlayedTsMs) >= NinetyDaysMs)
            .OrderByDescending(g => g.MaxRating >= 4 ? 1 : 0)
            .ThenByDescending(g => g.MaxRating)
            .ThenByDescending(g => g.TotalPlayCount)
            .ThenBy(g => g.MaxLastPlayedTsMs)
            .Take(take)
            .Select(g => new QuickplayCardSeed(
                TileId: $"rediscover::{g.GroupKey}",
                Kind: g.IsWork ? "work" : "album",
                TargetKey: g.GroupKey,
                Title: g.Title,
                Subtitle: g.Subtitle,
                BadgeText: g.MaxRating >= 4 ? $"{g.MaxRating}★ · 唤醒" : "沉睡馆藏",
                CoverArtPath: g.CoverArtPath,
                TrackUids: g.TrackUids))
            .ToList();

        return (history, newlyAdded, rediscovery);
    }

    public static IReadOnlyList<QuickplayCardSeed> BuildHistoryAlbums(
        IEnumerable<QuickplayTrackSeed> tracks,
        int maxCount = 8) =>
        BuildAll(tracks, 0, maxCount).History;

    public static IReadOnlyList<QuickplayCardSeed> BuildNewlyAddedAlbums(
        IEnumerable<QuickplayTrackSeed> tracks,
        int maxCount = 8) =>
        BuildAll(tracks, 0, maxCount).NewlyAdded;

    public static IReadOnlyList<QuickplayCardSeed> BuildRediscoveryAlbums(
        IEnumerable<QuickplayTrackSeed> tracks,
        long nowMs,
        int maxCount = 8) =>
        BuildAll(tracks, nowMs, maxCount).Rediscovery;

    private sealed class AlbumBucket
    {
        public required string GroupKey { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public bool IsWork { get; init; }
        public string? CoverArtPath { get; set; }
        public long MaxLastPlayedTsMs { get; set; }
        public long MaxMtimeMs { get; set; }
        public int? MaxYear { get; set; }
        public int MaxRating { get; set; }
        public int TotalPlayCount { get; set; }
        public List<QuickplayTrackSeed> Seeds { get; } = [];
        public List<string> TrackUids { get; } = [];
    }

    private static List<AlbumBucket> GroupAlbumsOrWorks(IEnumerable<QuickplayTrackSeed> tracks)
    {
        var map = new Dictionary<string, AlbumBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            if (string.IsNullOrWhiteSpace(t.TrackUid)) continue;

            string? workKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
                t.WorkName,
                t.Album,
                WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay));

            bool isWork = !string.IsNullOrEmpty(workKey);
            string groupKey = isWork
                ? $"work::{workKey}"
                : $"album::{(string.IsNullOrWhiteSpace(t.Album) ? t.Title : t.Album.Trim().ToLowerInvariant())}";

            if (!map.TryGetValue(groupKey, out var bucket))
            {
                string title = isWork
                    ? t.WorkName.Trim()
                    : (string.IsNullOrWhiteSpace(t.Album) ? t.Title : t.Album.Trim());
                string subtitle = string.IsNullOrWhiteSpace(t.ArtistsDisplay)
                    ? (string.IsNullOrWhiteSpace(t.ConductorsDisplay) ? "Local Library" : t.ConductorsDisplay)
                    : t.ArtistsDisplay;

                bucket = new AlbumBucket
                {
                    GroupKey = groupKey,
                    Title = title,
                    Subtitle = subtitle,
                    IsWork = isWork,
                };
                map[groupKey] = bucket;
            }

            bucket.Seeds.Add(t);
            if (string.IsNullOrWhiteSpace(bucket.CoverArtPath) && !string.IsNullOrWhiteSpace(t.CoverArtPath))
                bucket.CoverArtPath = t.CoverArtPath;
            if (t.LastPlayedTsMs > bucket.MaxLastPlayedTsMs)
                bucket.MaxLastPlayedTsMs = t.LastPlayedTsMs;
            if (t.MtimeMs > bucket.MaxMtimeMs)
                bucket.MaxMtimeMs = t.MtimeMs;
            if (t.Year is > 0 && (!bucket.MaxYear.HasValue || t.Year.Value > bucket.MaxYear.Value))
                bucket.MaxYear = t.Year;
            if (t.Rating > bucket.MaxRating)
                bucket.MaxRating = t.Rating;
            bucket.TotalPlayCount += Math.Max(0, t.EffectivePlayCount);
        }

        foreach (var bucket in map.Values)
        {
            if (bucket.Seeds.Count > 1)
            {
                bucket.Seeds.Sort((a, b) =>
                {
                    int da = a.DiscNumber > 0 ? a.DiscNumber : int.MaxValue;
                    int db = b.DiscNumber > 0 ? b.DiscNumber : int.MaxValue;
                    int cmp = da.CompareTo(db);
                    if (cmp != 0) return cmp;
                    if (bucket.IsWork)
                    {
                        int ma = a.MovementNumber is > 0 ? a.MovementNumber.Value : int.MaxValue;
                        int mb = b.MovementNumber is > 0 ? b.MovementNumber.Value : int.MaxValue;
                        cmp = ma.CompareTo(mb);
                        if (cmp != 0) return cmp;
                    }
                    int ta = a.TrackNumber > 0 ? a.TrackNumber : int.MaxValue;
                    int tb = b.TrackNumber > 0 ? b.TrackNumber : int.MaxValue;
                    cmp = ta.CompareTo(tb);
                    if (cmp != 0) return cmp;
                    return WorkAwareShuffleEngine.NaturalPathCompare(a.RelPath, b.RelPath);
                });
            }
            foreach (var seed in bucket.Seeds)
            {
                bucket.TrackUids.Add(seed.TrackUid);
            }
        }
        return [.. map.Values];
    }
}

public static class ZuneNavigationRoutes
{
    public static readonly (string Id, string Label)[] CollectionSubTabs =
    [
        ("playlists", "playlists"),
        ("scenes", "scenes"),
        ("genres", "genres"),
        ("albums", "albums"),
        ("tracks", "tracks"),
        ("artists", "artists"),
        ("classical", "classical"),
        ("composers", "composers"),
        ("conductors", "conductors"),
        ("folders", "folder"),
    ];

    public static string GetDefaultSubTab(string navId) => navId switch
    {
        "quickplay" => "home",
        "collection" => "tracks",
        "studio" => "sync",
        "now_playing" => "gallery",
        "settings" => "audio",
        _ => "tracks",
    };

    public static bool IsValidSubTab(string navId, string subTab) => (navId, subTab) switch
    {
        ("quickplay", "home") => true,
        ("collection", "playlists" or "scenes" or "genres" or "albums" or "tracks" or "artists" or "classical" or "composers" or "conductors" or "folders" or "folder") => true,
        ("studio", "sync" or "lastfm" or "usb" or "inbox") => true,
        ("now_playing", "gallery") => true,
        ("settings", "audio" or "storage" or "apis_ui") => true,
        _ => false,
    };
}

public static class QuickplayPinMatcher
{
    public const string EmptyAlbumSentinel = "__empty_album__";

    public static string NormalizeFolderKey(string? albumDirectory, string? relPath)
    {
        if (!string.IsNullOrWhiteSpace(albumDirectory))
        {
            return albumDirectory.Replace('\\', '/').Trim();
        }
        if (!string.IsNullOrWhiteSpace(relPath))
        {
            string? dir = Path.GetDirectoryName(relPath)?.Replace('\\', '/').Trim();
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        return "(根目录)";
    }

    public static bool MatchesTrack(
        QuickplayPinRecord pin,
        string trackUid,
        string album,
        string workName,
        string genre,
        string artistsDisplay,
        string composersDisplay,
        string conductorsDisplay,
        string albumDirectory,
        string relPath)
    {
        if (string.Equals(pin.Kind, "track", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(trackUid, pin.TargetKey, StringComparison.Ordinal);
        }

        if (string.Equals(pin.Kind, "folder", StringComparison.OrdinalIgnoreCase))
        {
            string expectedFolder = pin.TargetKey.Replace('\\', '/').Trim();
            return string.Equals(NormalizeFolderKey(albumDirectory, relPath), expectedFolder, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(pin.Kind, "album", StringComparison.OrdinalIgnoreCase))
        {
            if (pin.TargetKey is EmptyAlbumSentinel or "(未分配专辑)")
            {
                return string.IsNullOrWhiteSpace(album);
            }
            return string.Equals(album, pin.TargetKey, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(pin.Kind, "work", StringComparison.OrdinalIgnoreCase))
        {
            string? trackWorkKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
                workName,
                album,
                WorkAwareShuffleEngine.BuildAlbumScope(album, conductorsDisplay));
            if (pin.TargetKey.Contains("::", StringComparison.Ordinal))
            {
                return !string.IsNullOrEmpty(trackWorkKey)
                    && string.Equals(trackWorkKey, pin.TargetKey, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(workName, pin.TargetKey, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(pin.Kind, "genre", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(genre, pin.TargetKey, StringComparison.OrdinalIgnoreCase);
        }

        return artistsDisplay.Contains(pin.TargetKey, StringComparison.OrdinalIgnoreCase)
            || composersDisplay.Contains(pin.TargetKey, StringComparison.OrdinalIgnoreCase)
            || conductorsDisplay.Contains(pin.TargetKey, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Pre-indexed O(1) lookup structure over library tracks for Quickplay pin resolution so UI/background
/// refreshes avoid O(P * N) full-library linear scans.
/// </summary>
public sealed class QuickplayLookupIndex<TTrack>
{
    private readonly List<TTrack> _all;
    private readonly Func<TTrack, string> _getArtists;
    private readonly Func<TTrack, string> _getComposers;
    private readonly Func<TTrack, string> _getConductors;
    private readonly Dictionary<string, TTrack> _byUid;
    private readonly Dictionary<string, List<TTrack>> _byAlbum;
    private readonly Dictionary<string, List<TTrack>> _byFolder;
    private readonly Dictionary<string, List<TTrack>> _byWorkBlock;
    private readonly Dictionary<string, List<TTrack>> _byWorkName;
    private readonly Dictionary<string, List<TTrack>> _byGenre;

    public QuickplayLookupIndex(
        IReadOnlyList<TTrack> tracks,
        Func<TTrack, string> getTrackUid,
        Func<TTrack, string> getAlbum,
        Func<TTrack, string> getWorkName,
        Func<TTrack, string> getGenre,
        Func<TTrack, string> getArtists,
        Func<TTrack, string> getComposers,
        Func<TTrack, string> getConductors,
        Func<TTrack, string> getAlbumDirectory,
        Func<TTrack, string> getRelPath)
    {
        _all = tracks as List<TTrack> ?? [.. tracks];
        _getArtists = getArtists;
        _getComposers = getComposers;
        _getConductors = getConductors;

        int cap = Math.Max(16, _all.Count);
        _byUid = new Dictionary<string, TTrack>(cap, StringComparer.Ordinal);
        _byAlbum = new Dictionary<string, List<TTrack>>(StringComparer.OrdinalIgnoreCase);
        _byFolder = new Dictionary<string, List<TTrack>>(StringComparer.OrdinalIgnoreCase);
        _byWorkBlock = new Dictionary<string, List<TTrack>>(StringComparer.OrdinalIgnoreCase);
        _byWorkName = new Dictionary<string, List<TTrack>>(StringComparer.OrdinalIgnoreCase);
        _byGenre = new Dictionary<string, List<TTrack>>(StringComparer.OrdinalIgnoreCase);

        static void AddToBucket(Dictionary<string, List<TTrack>> dict, string key, TTrack item)
        {
            if (!dict.TryGetValue(key, out var list))
            {
                list = [];
                dict[key] = list;
            }
            list.Add(item);
        }

        foreach (var tr in _all)
        {
            string uid = getTrackUid(tr);
            if (!string.IsNullOrEmpty(uid))
            {
                _byUid[uid] = tr;
            }

            string album = getAlbum(tr);
            string albumKey = string.IsNullOrWhiteSpace(album) ? QuickplayPinMatcher.EmptyAlbumSentinel : album;
            AddToBucket(_byAlbum, albumKey, tr);

            string folderKey = QuickplayPinMatcher.NormalizeFolderKey(getAlbumDirectory(tr), getRelPath(tr));
            AddToBucket(_byFolder, folderKey, tr);

            string workName = getWorkName(tr);
            if (!string.IsNullOrWhiteSpace(workName))
            {
                AddToBucket(_byWorkName, workName, tr);
            }

            string conductors = getConductors(tr);
            string? workBlock = WorkAwareShuffleEngine.BuildWorkBlockKey(
                workName,
                album,
                WorkAwareShuffleEngine.BuildAlbumScope(album, conductors));
            if (!string.IsNullOrEmpty(workBlock))
            {
                AddToBucket(_byWorkBlock, workBlock, tr);
            }

            string genre = getGenre(tr);
            if (!string.IsNullOrWhiteSpace(genre))
            {
                AddToBucket(_byGenre, genre, tr);
            }
        }
    }

    public bool TryGetByUid(string uid, out TTrack track) => _byUid.TryGetValue(uid, out track!);

    public IEnumerable<TTrack> MatchTracks(QuickplayPinRecord pin)
    {
        if (string.Equals(pin.Kind, "track", StringComparison.OrdinalIgnoreCase))
        {
            return _byUid.TryGetValue(pin.TargetKey, out var tr) ? [tr] : [];
        }

        if (string.Equals(pin.Kind, "folder", StringComparison.OrdinalIgnoreCase))
        {
            string expectedFolder = pin.TargetKey.Replace('\\', '/').Trim();
            return _byFolder.TryGetValue(expectedFolder, out var list) ? list : [];
        }

        if (string.Equals(pin.Kind, "album", StringComparison.OrdinalIgnoreCase))
        {
            string key = pin.TargetKey is QuickplayPinMatcher.EmptyAlbumSentinel or "(未分配专辑)"
                ? QuickplayPinMatcher.EmptyAlbumSentinel
                : pin.TargetKey;
            return _byAlbum.TryGetValue(key, out var list) ? list : [];
        }

        if (string.Equals(pin.Kind, "work", StringComparison.OrdinalIgnoreCase))
        {
            if (pin.TargetKey.Contains("::", StringComparison.Ordinal))
            {
                return _byWorkBlock.TryGetValue(pin.TargetKey, out var scoped) ? scoped : [];
            }
            return _byWorkName.TryGetValue(pin.TargetKey, out var byWork) ? byWork : [];
        }

        if (string.Equals(pin.Kind, "genre", StringComparison.OrdinalIgnoreCase))
        {
            return _byGenre.TryGetValue(pin.TargetKey, out var list) ? list : [];
        }

        return _all.Where(tr =>
            _getArtists(tr).Contains(pin.TargetKey, StringComparison.OrdinalIgnoreCase)
            || _getComposers(tr).Contains(pin.TargetKey, StringComparison.OrdinalIgnoreCase)
            || _getConductors(tr).Contains(pin.TargetKey, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record InboxAuditTrackSeed(
    string Genre,
    string ComposersDisplay,
    string ConductorsDisplay,
    string Album,
    string Publisher,
    int DjSceneCount,
    string? TempoBucket,
    double? TempoBpm,
    string? CoverArtPath,
    string? DjVocals,
    long LastPlayedTsMs
);

public sealed record InboxAuditCounts(
    int MissingGenre,
    int MissingComposer,
    int MissingConductor,
    int MissingAlbum,
    int MissingPublisher,
    int MissingScene,
    int MissingTempo,
    int MissingCover,
    int MissingVocals,
    int Rediscovery90d
);

public static class InboxAuditEvaluator
{
    public const long NinetyDaysMs = 90L * 86_400_000L;

    public static bool IsAuditOrRediscoveryMode(string? navMode) =>
        !string.IsNullOrWhiteSpace(navMode)
        && (navMode.StartsWith("audit_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(navMode, "rediscovery_90d", StringComparison.OrdinalIgnoreCase));

    public static bool MatchesMode(
        string? navMode,
        string? genre,
        string? composersDisplay,
        string? conductorsDisplay,
        string? album,
        string? publisher,
        int djSceneCount,
        string? tempoBucket,
        double? tempoBpm,
        string? coverArtPath,
        string? djVocals,
        long lastPlayedTsMs,
        long nowMs) => (navMode ?? "").Trim().ToLowerInvariant() switch
    {
        "rediscovery_90d" => lastPlayedTsMs <= 0 || (nowMs - lastPlayedTsMs) >= NinetyDaysMs,
        "audit_missing_tempo" => tempoBucket is null && tempoBpm is null,
        "audit_missing_scene" => djSceneCount == 0,
        "audit_missing_vocals" => string.IsNullOrWhiteSpace(djVocals) || string.Equals(djVocals, "unknown", StringComparison.OrdinalIgnoreCase),
        "audit_missing_cover" => string.IsNullOrWhiteSpace(coverArtPath),
        "audit_missing_genre" => string.IsNullOrWhiteSpace(genre),
        "audit_missing_composer" => string.IsNullOrWhiteSpace(composersDisplay),
        "audit_missing_conductor" => string.IsNullOrWhiteSpace(conductorsDisplay),
        "audit_missing_album" => string.IsNullOrWhiteSpace(album),
        "audit_missing_publisher" => string.IsNullOrWhiteSpace(publisher),
        _ => true,
    };

    public static bool MatchesMode(InboxAuditTrackSeed track, string? navMode, long nowMs) =>
        MatchesMode(
            navMode,
            track.Genre,
            track.ComposersDisplay,
            track.ConductorsDisplay,
            track.Album,
            track.Publisher,
            track.DjSceneCount,
            track.TempoBucket,
            track.TempoBpm,
            track.CoverArtPath,
            track.DjVocals,
            track.LastPlayedTsMs,
            nowMs);

    public static InboxAuditCounts ComputeCounts(IEnumerable<InboxAuditTrackSeed> tracks, long nowMs)
    {
        int missingGenre = 0;
        int missingComposer = 0;
        int missingConductor = 0;
        int missingAlbum = 0;
        int missingPublisher = 0;
        int missingScene = 0;
        int missingTempo = 0;
        int missingCover = 0;
        int missingVocals = 0;
        int rediscovery90d = 0;

        foreach (var t in tracks)
        {
            if (string.IsNullOrWhiteSpace(t.Genre)) missingGenre++;
            if (string.IsNullOrWhiteSpace(t.ComposersDisplay)) missingComposer++;
            if (string.IsNullOrWhiteSpace(t.ConductorsDisplay)) missingConductor++;
            if (string.IsNullOrWhiteSpace(t.Album)) missingAlbum++;
            if (string.IsNullOrWhiteSpace(t.Publisher)) missingPublisher++;
            if (t.DjSceneCount == 0) missingScene++;
            if (t.TempoBucket is null && t.TempoBpm is null) missingTempo++;
            if (string.IsNullOrWhiteSpace(t.CoverArtPath)) missingCover++;
            if (string.IsNullOrWhiteSpace(t.DjVocals) || string.Equals(t.DjVocals, "unknown", StringComparison.OrdinalIgnoreCase)) missingVocals++;
            if (t.LastPlayedTsMs <= 0 || (nowMs - t.LastPlayedTsMs) >= NinetyDaysMs) rediscovery90d++;
        }

        return new InboxAuditCounts(
            MissingGenre: missingGenre,
            MissingComposer: missingComposer,
            MissingConductor: missingConductor,
            MissingAlbum: missingAlbum,
            MissingPublisher: missingPublisher,
            MissingScene: missingScene,
            MissingTempo: missingTempo,
            MissingCover: missingCover,
            MissingVocals: missingVocals,
            Rediscovery90d: rediscovery90d);
    }

    public static string FormatResultSummary(string? navMode, string navLabel, int matchedCount, int totalCount)
    {
        if (IsAuditOrRediscoveryMode(navMode))
        {
            return matchedCount == 0
                ? $"当前体检筛选：{navLabel} — 0 首缺失（曲库共 {totalCount:N0} 首）"
                : $"当前体检筛选：{navLabel} — 命中 {matchedCount:N0} 首（曲库共 {totalCount:N0} 首）";
        }
        return $"当前视图：{navLabel} — 共 {matchedCount:N0} / {totalCount:N0} 首（点击上方体检项筛选元数据缺口）";
    }
}

public static class NowPlayingFormatter
{
    public static (string Line1, string Line2, string Line3) FormatNonClassicalNowPlaying(
        string? title,
        string? artist,
        string? album,
        int? year)
    {
        string? yearPart = year is > 0
            ? year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        string line3 = string.Join(" · ", new[] { album, yearPart }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim()));
        return (title?.Trim() ?? "", artist?.Trim() ?? "", line3);
    }
}
