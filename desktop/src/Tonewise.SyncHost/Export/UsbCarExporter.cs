using System.Text;

namespace Tonewise.SyncHost.Export;

public sealed record UsbExportTrack(
    string TrackUid,
    string SourceAudioPath,
    string Title,
    string Artist,
    string Album,
    int DurationMs,
    string? CoverArtSourcePath = null
);

public sealed record UsbExportSummary(
    int ExportedTracks,
    int ExportedCovers,
    string PlaylistM3u8Path,
    IReadOnlyList<string> MissingSourceFiles
);

/// <summary>
/// Exports a curated scene or playlist to a car USB drive with relative POSIX paths
/// (`../Tracks/...`) and BOM-free UTF-8 `.m3u8` compatible with embedded automotive head units.
/// </summary>
public static class UsbCarExporter
{
    public static UsbExportSummary ExportPlaylistToUsb(
        string playlistName,
        IReadOnlyList<UsbExportTrack> tracks,
        string usbRootDir)
    {
        if (string.IsNullOrWhiteSpace(playlistName))
            throw new ArgumentException("Playlist name is required", nameof(playlistName));
        if (string.IsNullOrWhiteSpace(usbRootDir))
            throw new ArgumentException("USB root directory is required", nameof(usbRootDir));

        string playlistsDir = Path.Combine(usbRootDir, "Playlists");
        string tracksDir = Path.Combine(usbRootDir, "Tracks");
        Directory.CreateDirectory(playlistsDir);
        Directory.CreateDirectory(tracksDir);

        var sb = new StringBuilder("#EXTM3U\n");
        var copiedAlbumsCover = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        int exportedTracks = 0;
        int exportedCovers = 0;

        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.SourceAudioPath) || !File.Exists(track.SourceAudioPath))
            {
                missing.Add(track.SourceAudioPath);
                continue;
            }

            string albumFolderName = SanitizeSegment(string.IsNullOrWhiteSpace(track.Album) ? "Singles" : track.Album);
            string destAlbumDir = Path.Combine(tracksDir, albumFolderName);
            Directory.CreateDirectory(destAlbumDir);

            string fileName = Path.GetFileName(track.SourceAudioPath);
            string destAudioPath = Path.Combine(destAlbumDir, fileName);
            File.Copy(track.SourceAudioPath, destAudioPath, overwrite: true);
            exportedTracks++;

            if (copiedAlbumsCover.Add(albumFolderName))
            {
                string? coverSrc = track.CoverArtSourcePath;
                if (string.IsNullOrWhiteSpace(coverSrc))
                {
                    string candidate = Path.Combine(Path.GetDirectoryName(track.SourceAudioPath) ?? "", "cover.jpg");
                    if (File.Exists(candidate)) coverSrc = candidate;
                }
                if (!string.IsNullOrWhiteSpace(coverSrc) && File.Exists(coverSrc))
                {
                    File.Copy(coverSrc, Path.Combine(destAlbumDir, "cover.jpg"), overwrite: true);
                    exportedCovers++;
                }
            }

            int durationSec = Math.Max(0, track.DurationMs / 1000);
            sb.Append($"#EXTINF:{durationSec},{track.Artist} - {track.Title}\n");
            sb.Append($"../Tracks/{albumFolderName}/{fileName}\n");
        }

        string safePlaylist = SanitizeSegment(playlistName);
        string m3uPath = Path.Combine(playlistsDir, safePlaylist + ".m3u8");
        File.WriteAllText(m3uPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new UsbExportSummary(exportedTracks, exportedCovers, m3uPath, missing);
    }

    private static string SanitizeSegment(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(raw.Trim().Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }
}
