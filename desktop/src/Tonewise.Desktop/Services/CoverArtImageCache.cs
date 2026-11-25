using Microsoft.UI.Xaml.Media.Imaging;

namespace Tonewise.Desktop.Services;

/// <summary>
/// Bounded bitmap cache for Quickplay tiles, track rows, and entity cover grids.
/// Exposed as static methods for compiled {x:Bind} inside DataTemplates as well as code-behind recycling.
/// </summary>
public static class CoverArtImageCache
{
    private const int MaxCacheEntries = 600;
    private const int MaxMissingEntries = 2000;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (BitmapImage Image, LinkedListNode<string> Node)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> LruOrder = new();
    private static readonly HashSet<string> MissingPaths = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapImage? GetQuickplayCover(string? coverArtPath) =>
        GetBitmap(coverArtPath, decodeWidth: 220);

    public static BitmapImage? GetRowCover(string? coverArtPath) =>
        GetBitmap(coverArtPath, decodeWidth: 80);

    public static void Clear()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            LruOrder.Clear();
            MissingPaths.Clear();
        }
    }

    private static void MarkMissing(string coverArtPath, string cacheKey)
    {
        lock (CacheLock)
        {
            if (Cache.Remove(cacheKey, out var entry))
            {
                LruOrder.Remove(entry.Node);
            }
            if (MissingPaths.Count >= MaxMissingEntries)
            {
                MissingPaths.Clear();
            }
            MissingPaths.Add(coverArtPath);
        }
    }

    public static BitmapImage? GetBitmap(string? coverArtPath, int decodeWidth = 80)
    {
        if (string.IsNullOrWhiteSpace(coverArtPath)) return null;

        string cacheKey = $"{decodeWidth}|{coverArtPath}";
        lock (CacheLock)
        {
            if (MissingPaths.Contains(coverArtPath)) return null;
            if (Cache.TryGetValue(cacheKey, out var existing))
            {
                LruOrder.Remove(existing.Node);
                LruOrder.AddFirst(existing.Node);
                return existing.Image;
            }
        }

        try
        {
            string fullPath = Path.GetFullPath(coverArtPath);
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                fullPath = fullPath[4..];
            }
            var uri = new Uri(fullPath);
            var bmp = new BitmapImage
            {
                DecodePixelWidth = decodeWidth,
                DecodePixelType = DecodePixelType.Logical,
                UriSource = uri,
            };
            bmp.ImageFailed += (_, _) => MarkMissing(coverArtPath, cacheKey);

            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out var existing))
                {
                    LruOrder.Remove(existing.Node);
                    LruOrder.AddFirst(existing.Node);
                    return existing.Image;
                }
                while (Cache.Count >= MaxCacheEntries && LruOrder.Last is { } oldest)
                {
                    LruOrder.RemoveLast();
                    Cache.Remove(oldest.Value);
                }
                var node = LruOrder.AddFirst(cacheKey);
                Cache[cacheKey] = (bmp, node);
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
