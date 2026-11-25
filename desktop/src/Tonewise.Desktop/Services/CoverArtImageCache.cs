using Microsoft.UI.Xaml.Media.Imaging;

namespace Tonewise.Desktop.Services;

/// <summary>
/// Bounded bitmap cache for Quickplay tiles, track rows, and entity cover grids.
/// Exposed as static methods for compiled {x:Bind} inside DataTemplates as well as code-behind recycling.
/// </summary>
public static class CoverArtImageCache
{
    private const int MaxCacheEntries = 600;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapImage? GetQuickplayCover(string? coverArtPath) =>
        GetBitmap(coverArtPath, decodeWidth: 220);

    public static BitmapImage? GetRowCover(string? coverArtPath) =>
        GetBitmap(coverArtPath, decodeWidth: 80);

    public static void Clear()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    public static BitmapImage? GetBitmap(string? coverArtPath, int decodeWidth = 80)
    {
        if (string.IsNullOrWhiteSpace(coverArtPath) || !File.Exists(coverArtPath)) return null;

        string cacheKey = $"{decodeWidth}|{coverArtPath}";
        lock (CacheLock)
        {
            if (Cache.TryGetValue(cacheKey, out var existing))
            {
                return existing;
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

            lock (CacheLock)
            {
                if (Cache.Count >= MaxCacheEntries)
                {
                    Cache.Clear();
                }
                Cache[cacheKey] = bmp;
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
