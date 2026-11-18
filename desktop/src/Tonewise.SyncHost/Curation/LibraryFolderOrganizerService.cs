using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Tonewise.SyncHost.Curation;

public static class LongPathHelper
{
    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;

        string full = path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path.TrimStart('\\');
        }
        if (Path.IsPathRooted(path))
        {
            return @"\\?\" + full.Replace('/', '\\');
        }
        return path;
    }

    public static string ToExtendedIfLong(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length >= 240 ? ToExtendedPath(path) : path;

    public static string StripExtendedPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + path[@"\\?\UNC\".Length..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[@"\\?\".Length..];
        return path;
    }

    public static bool FileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (File.Exists(path)) return true;
            string ext = ToExtendedPath(path);
            return !string.Equals(ext, path, StringComparison.Ordinal) && File.Exists(ext);
        }
        catch
        {
            return false;
        }
    }

    public static bool DirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (Directory.Exists(path)) return true;
            string ext = ToExtendedPath(path);
            return !string.Equals(ext, path, StringComparison.Ordinal) && Directory.Exists(ext);
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureDirectory(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath)) return;
        Directory.CreateDirectory(ToExtendedIfLong(dirPath));
    }

    public static IEnumerable<string> SafeEnumerateFiles(
        string rootDir,
        string searchPattern = "*.*",
        bool recursive = true,
        bool skipHiddenDirectories = true)
    {
        if (!DirectoryExists(rootDir)) yield break;

        if (!recursive)
        {
            IEnumerable<string> topFiles;
            try
            {
                topFiles = Directory.EnumerateFiles(
                    ToExtendedIfLong(rootDir),
                    searchPattern,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.System,
                    });
            }
            catch
            {
                yield break;
            }

            foreach (string f in topFiles)
            {
                yield return StripExtendedPrefix(f);
            }
            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(StripExtendedPrefix(rootDir));

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            string extCurrent = ToExtendedPath(current);

            string[] files = [];
            try
            {
                files = Directory.GetFiles(extCurrent, searchPattern);
            }
            catch
            {
                // Skip inaccessible/corrupt/unsearchable directory cleanly without aborting library scan
            }
            foreach (string f in files)
            {
                yield return StripExtendedPrefix(f);
            }

            string[] subdirs = [];
            try
            {
                subdirs = Directory.GetDirectories(extCurrent);
            }
            catch
            {
                // Ignore inaccessible subdirectories
            }
            foreach (string sub in subdirs)
            {
                string cleanSub = StripExtendedPrefix(sub);
                if (skipHiddenDirectories)
                {
                    string name = Path.GetFileName(cleanSub.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (name.StartsWith('.')) continue;
                }
                stack.Push(cleanSub);
            }
        }
    }
}

public sealed record FolderOrganizeSummary(
    int TotalChecked,
    int MovedCount,
    int LongPathFixedCount,
    int StaleRemovedCount,
    int SkippedCount,
    IReadOnlyList<string> SampleMoves
);

/// <summary>
/// Organizes non-compliant library files into `&lt;Album Artist&gt;\&lt;Album&gt;\&lt;Disc-Track#&gt; &lt;Title&gt;`,
/// safely shortens overlong multi-artist paths within Windows limits (&lt;= 235 chars), prunes stale DB entries,
/// and updates `manifest.db` (`rel_path`, `album_artist`) transactionally.
/// </summary>
public static partial class LibraryFolderOrganizerService
{
    private const int MaxTargetFullPathLength = 235;
    private const int MaxSegmentArtistLength = 68;
    private const int MaxSegmentAlbumLength = 72;
    private const int MaxFileNameLength = 96;

    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    public static FolderOrganizeSummary OrganizeLibraryFolders(string musicRoot, string manifestDbPath)
    {
        if (string.IsNullOrWhiteSpace(musicRoot) || !LongPathHelper.DirectoryExists(musicRoot))
            return new FolderOrganizeSummary(0, 0, 0, 0, 0, []);
        if (string.IsNullOrWhiteSpace(manifestDbPath) || !File.Exists(manifestDbPath))
            return new FolderOrganizeSummary(0, 0, 0, 0, 0, []);

        string normalizedRoot = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(musicRoot)).TrimEnd('\\', '/');

        using var conn = new SqliteConnection($"Data Source={manifestDbPath}");
        conn.Open();

        bool hasAlbumArtistCol = HasColumn(conn, "tracks", "album_artist");
        var rows = new List<(string Uid, string RelPath, string Title, string Album, string? AlbumArtist, IReadOnlyList<string> Artists)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT track_uid, rel_path, title, album, {(hasAlbumArtistCol ? "album_artist" : "NULL")}, artists_json
                FROM tracks
                ORDER BY rel_path;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    ParseStringArray(reader.IsDBNull(5) ? "[]" : reader.GetString(5))
                ));
            }
        }

        int totalChecked = rows.Count;
        int movedCount = 0;
        int longPathFixedCount = 0;
        int skippedCount = 0;
        var staleUids = new List<string>();
        var dbUpdates = new List<(string Uid, string NewRelPath, string NewAlbumArtist)>();
        var samples = new List<string>();
        var sourceAlbumDirsTouched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var albumSeqCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            string currentRelPosix = row.RelPath.Replace('\\', '/').TrimStart('/');
            string sourceFull = Path.Combine(normalizedRoot, currentRelPosix.Replace('/', Path.DirectorySeparatorChar));

            if (!LongPathHelper.FileExists(sourceFull))
            {
                staleUids.Add(row.Uid);
                continue;
            }

            string ext = Path.GetExtension(sourceFull);
            var embedded = ReadEmbeddedHeaderTags(sourceFull);

            string rawArtist = ResolveAlbumArtist(row.AlbumArtist, row.Artists, currentRelPosix, embedded);
            string rawAlbum = ResolveAlbumName(row.Album, currentRelPosix, embedded);

            string artistSeg = CondenseArtistSegment(rawArtist, MaxSegmentArtistLength);
            string albumSeg = SanitizeSegment(rawAlbum, MaxSegmentAlbumLength, "Unknown Album");

            string currentFileName = Path.GetFileName(sourceFull);
            string currentStem = Path.GetFileNameWithoutExtension(currentFileName);

            bool wasAlreadyOverlong = sourceFull.Length > MaxTargetFullPathLength
                || currentRelPosix.Split('/').Any(s => s.Length > 85);

            string? discTrack = ResolveDiscTrackPrefix(currentStem, embedded);
            if (string.IsNullOrWhiteSpace(discTrack))
            {
                string key = $"{artistSeg}|{albumSeg}";
                albumSeqCounters[key] = albumSeqCounters.GetValueOrDefault(key) + 1;
                discTrack = $"1-{albumSeqCounters[key]:D2}";
            }

            string cleanTitle = ResolveCleanTitle(row.Title, currentStem, embedded);
            if (IsPathCompliant(sourceFull, currentRelPosix, artistSeg, albumSeg, MaxTargetFullPathLength))
            {
                skippedCount++;
                continue;
            }

            string targetDir = Path.Combine(normalizedRoot, artistSeg, albumSeg);
            int maxTitleLen = Math.Min(
                MaxFileNameLength - discTrack.Length - 1 - ext.Length,
                MaxTargetFullPathLength - targetDir.Length - 1 - discTrack.Length - 1 - ext.Length);
            maxTitleLen = Math.Max(12, maxTitleLen);

            string titleSeg = SanitizeSegment(cleanTitle, maxTitleLen, "Unknown Track");
            string targetFileName = $"{discTrack} {titleSeg}{ext}";
            string targetFull = Path.Combine(targetDir, targetFileName);
            string targetRelPosix = $"{artistSeg}/{albumSeg}/{targetFileName}";

            if (string.Equals(
                LongPathHelper.StripExtendedPrefix(sourceFull),
                LongPathHelper.StripExtendedPrefix(targetFull),
                StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                continue;
            }

            try
            {
                LongPathHelper.EnsureDirectory(targetDir);
                string finalTargetFull = EnsureUniqueTargetPath(targetFull, sourceFull);
                if (!string.Equals(finalTargetFull, targetFull, StringComparison.OrdinalIgnoreCase))
                {
                    targetFull = finalTargetFull;
                    targetRelPosix = Path.GetRelativePath(normalizedRoot, targetFull).Replace('\\', '/');
                }

                File.Move(LongPathHelper.ToExtendedPath(sourceFull), LongPathHelper.ToExtendedPath(targetFull));
                movedCount++;
                if (wasAlreadyOverlong)
                {
                    longPathFixedCount++;
                }
                if (samples.Count < 8)
                {
                    samples.Add($"{currentRelPosix} -> {targetRelPosix}");
                }

                dbUpdates.Add((row.Uid, targetRelPosix, string.IsNullOrWhiteSpace(row.AlbumArtist) ? artistSeg : row.AlbumArtist!));

                string? oldDir = Path.GetDirectoryName(sourceFull);
                if (!string.IsNullOrWhiteSpace(oldDir)
                    && !string.Equals(oldDir.TrimEnd('\\', '/'), normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    sourceAlbumDirsTouched[oldDir] = targetDir;
                }
            }
            catch
            {
                skippedCount++;
            }
        }

        // Preserve companion cover/booklet assets and prune empty source folders
        foreach (var (oldDir, newDir) in sourceAlbumDirsTouched)
        {
            CopyCompanionAssetsIfMissing(oldDir, newDir);
            TryCleanupEmptyDirectories(oldDir, normalizedRoot);
        }

        if (staleUids.Count > 0 || dbUpdates.Count > 0)
        {
            using var tx = conn.BeginTransaction();
            foreach (var upd in dbUpdates)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = hasAlbumArtistCol
                    ? "UPDATE tracks SET rel_path = $rel, album_artist = COALESCE(NULLIF(album_artist, ''), $aa) WHERE track_uid = $uid;"
                    : "UPDATE tracks SET rel_path = $rel WHERE track_uid = $uid;";
                cmd.Parameters.AddWithValue("$rel", upd.NewRelPath);
                if (hasAlbumArtistCol) cmd.Parameters.AddWithValue("$aa", upd.NewAlbumArtist);
                cmd.Parameters.AddWithValue("$uid", upd.Uid);
                cmd.ExecuteNonQuery();
            }

            foreach (string staleUid in staleUids)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM tracks WHERE track_uid = $uid;";
                del.Parameters.AddWithValue("$uid", staleUid);
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }

        return new FolderOrganizeSummary(totalChecked, movedCount, longPathFixedCount, staleUids.Count, skippedCount, samples);
    }

    private static bool IsPathCompliant(
        string fullPath,
        string relPosix,
        string expectedArtistSeg,
        string expectedAlbumSeg,
        int maxFullPath)
    {
        if (fullPath.Length > maxFullPath) return false;
        string[] segs = relPosix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length != 3) return false;
        if (segs[0].Length > 80 || segs[1].Length > 80 || segs[2].Length > MaxFileNameLength) return false;

        string fileStem = Path.GetFileNameWithoutExtension(segs[2]);
        if (!ValidDiscTrackPrefixRegex().IsMatch(fileStem)) return false;

        string actualArtistSeg = SanitizeSegment(segs[0], MaxSegmentArtistLength, "Unknown Artist");
        string actualAlbumSeg = SanitizeSegment(segs[1], MaxSegmentAlbumLength, "Unknown Album");

        bool artistMatches = actualArtistSeg.Equals(expectedArtistSeg, StringComparison.OrdinalIgnoreCase)
            || segs[0].Equals("Various Artists", StringComparison.OrdinalIgnoreCase);
        bool albumMatches = actualAlbumSeg.Equals(expectedAlbumSeg, StringComparison.OrdinalIgnoreCase);
        return artistMatches && albumMatches;
    }

    private static string ResolveAlbumArtist(
        string? dbAlbumArtist,
        IReadOnlyList<string> artists,
        string currentRelPosix,
        EmbeddedHeaderTags embedded)
    {
        if (!string.IsNullOrWhiteSpace(dbAlbumArtist)) return dbAlbumArtist.Trim();
        if (!string.IsNullOrWhiteSpace(embedded.AlbumArtist)) return embedded.AlbumArtist.Trim();

        string[] segs = currentRelPosix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length >= 3 && segs[0].Equals("Various Artists", StringComparison.OrdinalIgnoreCase))
        {
            return "Various Artists";
        }
        if (artists.Count > 0 && !string.IsNullOrWhiteSpace(artists[0]))
        {
            return artists[0].Trim();
        }
        if (!string.IsNullOrWhiteSpace(embedded.Artist)) return embedded.Artist.Trim();
        if (segs.Length >= 3 && !segs[0].Contains("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return segs[0].Trim();
        }
        return "Unknown Artist";
    }

    private static string ResolveAlbumName(string? dbAlbum, string currentRelPosix, EmbeddedHeaderTags embedded)
    {
        if (!string.IsNullOrWhiteSpace(dbAlbum)) return dbAlbum.Trim();
        if (!string.IsNullOrWhiteSpace(embedded.Album)) return embedded.Album.Trim();
        string[] segs = currentRelPosix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length >= 3 && !segs[1].Contains("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return segs[1].Trim();
        }
        return "Unknown Album";
    }

    private static string? ResolveDiscTrackPrefix(string currentStem, EmbeddedHeaderTags embedded)
    {
        var m = ValidDiscTrackPrefixRegex().Match(currentStem);
        if (m.Success
            && int.TryParse(m.Groups["disc"].Value, out int d)
            && int.TryParse(m.Groups["track"].Value, out int t)
            && d is >= 1 and <= 99
            && t is >= 1 and <= 999)
        {
            return t >= 100 ? $"{d}-{t:D3}" : $"{d}-{t:D2}";
        }

        if (embedded.TrackNumber is >= 1 and <= 999)
        {
            int disc = embedded.DiscNumber is >= 1 and <= 99 ? embedded.DiscNumber.Value : 1;
            return embedded.TrackNumber.Value >= 100
                ? $"{disc}-{embedded.TrackNumber.Value:D3}"
                : $"{disc}-{embedded.TrackNumber.Value:D2}";
        }

        var trackOnly = LeadingTrackOnlyRegex().Match(currentStem);
        if (trackOnly.Success && int.TryParse(trackOnly.Groups["track"].Value, out int trk) && trk is >= 1 and <= 999)
        {
            return trk >= 100 ? $"1-{trk:D3}" : $"1-{trk:D2}";
        }

        return null;
    }

    private static string ResolveCleanTitle(string dbTitle, string currentStem, EmbeddedHeaderTags embedded)
    {
        string raw = !string.IsNullOrWhiteSpace(dbTitle)
            ? dbTitle.Trim()
            : (!string.IsNullOrWhiteSpace(embedded.Title) ? embedded.Title.Trim() : currentStem);

        raw = BrokenDiscTrackRegex().Replace(raw, "").Trim();
        raw = ValidDiscTrackPrefixRegex().Replace(raw, "").Trim();
        raw = LeadingHashPrefixRegex().Replace(raw, "").Trim();
        raw = LeadingTrackOnlyRegex().Replace(raw, "").Trim();

        if (string.IsNullOrWhiteSpace(dbTitle))
        {
            int dashIdx = raw.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 1 && dashIdx < raw.Length - 3)
            {
                raw = raw[(dashIdx + 3)..].Trim();
            }
        }
        return string.IsNullOrWhiteSpace(raw) ? "Unknown Track" : raw;
    }

    private static string CondenseArtistSegment(string raw, int maxLen)
    {
        string cleaned = raw.Replace(':', ' ');
        if (cleaned.Length > maxLen)
        {
            string[] parts = MultiArtistSplitRegex().Split(cleaned)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            if (parts.Length > 0)
            {
                string primary = parts[0];
                if (parts.Length > 1 && primary.Length + parts[1].Length + 2 <= maxLen)
                {
                    cleaned = $"{primary}, {parts[1]}";
                }
                else
                {
                    cleaned = primary;
                }
            }
        }
        return SanitizeSegment(cleaned, maxLen, "Unknown Artist");
    }

    private static string SanitizeSegment(string raw, int maxLen, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        string s = raw.Replace(":", "  ").Replace("/", " ").Replace("\\", " ");
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (InvalidFileChars.Contains(ch) || char.IsControl(ch))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }
        string compact = MultiSpaceRegex().Replace(sb.ToString(), " ").Trim().TrimEnd('.', ' ');
        if (compact.Length > maxLen)
        {
            compact = compact[..maxLen].TrimEnd('.', ' ', '-', ',', ';');
        }
        return string.IsNullOrWhiteSpace(compact) ? fallback : compact;
    }

    private static string EnsureUniqueTargetPath(string targetFull, string sourceFull)
    {
        string extTarget = LongPathHelper.ToExtendedPath(targetFull);
        if (!File.Exists(extTarget)) return targetFull;
        if (string.Equals(LongPathHelper.ToExtendedPath(sourceFull), extTarget, StringComparison.OrdinalIgnoreCase))
            return targetFull;

        string dir = Path.GetDirectoryName(targetFull)!;
        string stem = Path.GetFileNameWithoutExtension(targetFull);
        string ext = Path.GetExtension(targetFull);
        for (int n = 2; n < 1000; n++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(LongPathHelper.ToExtendedPath(candidate)))
                return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static void CopyCompanionAssetsIfMissing(string oldDir, string newDir)
    {
        try
        {
            if (string.Equals(oldDir.TrimEnd('\\', '/'), newDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return;
            LongPathHelper.EnsureDirectory(newDir);
            foreach (string asset in LongPathHelper.SafeEnumerateFiles(oldDir, "*.*", recursive: false))
            {
                string name = Path.GetFileName(asset);
                bool isAsset = name.Equals("cover.jpg", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cover.png", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("folder.jpg", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                if (!isAsset) continue;

                string destAsset = Path.Combine(newDir, name);
                if (!LongPathHelper.FileExists(destAsset))
                {
                    File.Copy(LongPathHelper.ToExtendedPath(asset), LongPathHelper.ToExtendedPath(destAsset), overwrite: false);
                }
            }
        }
        catch
        {
            // Non-fatal companion asset preservation
        }
    }

    private static void TryCleanupEmptyDirectories(string dir, string musicRoot)
    {
        try
        {
            string cur = dir.TrimEnd('\\', '/');
            string rootClean = musicRoot.TrimEnd('\\', '/');
            while (!string.IsNullOrWhiteSpace(cur)
                   && cur.Length > rootClean.Length
                   && cur.StartsWith(rootClean, StringComparison.OrdinalIgnoreCase))
            {
                string extCur = LongPathHelper.ToExtendedPath(cur);
                if (!Directory.Exists(extCur)) break;
                if (Directory.EnumerateFileSystemEntries(extCur).Any()) break;
                Directory.Delete(extCur, recursive: false);
                cur = Path.GetDirectoryName(cur)?.TrimEnd('\\', '/') ?? "";
            }
        }
        catch
        {
            // Ignore locked or non-empty folders
        }
    }

    private static bool HasColumn(SqliteConnection conn, string table, string col)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", col);
        return cmd.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<string> ParseStringArray(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    public readonly record struct EmbeddedHeaderTags(
        string? AlbumArtist,
        string? Artist,
        string? Album,
        string? Title,
        int? DiscNumber,
        int? TrackNumber
    );

    public static EmbeddedHeaderTags ReadEmbeddedHeaderTags(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            using var fs = new FileStream(LongPathHelper.ToExtendedPath(filePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (ext == ".flac") return ReadFlacComments(fs);
            if (ext == ".mp3") return ReadId3v2Comments(fs);
        }
        catch
        {
        }
        return default;
    }

    private static EmbeddedHeaderTags ReadFlacComments(Stream fs)
    {
        Span<byte> sig = stackalloc byte[4];
        if (fs.Read(sig) != 4 || sig[0] != 'f' || sig[1] != 'L' || sig[2] != 'a' || sig[3] != 'C')
            return default;

        Span<byte> hdr = stackalloc byte[4];
        while (fs.Read(hdr) == 4)
        {
            bool isLast = (hdr[0] & 0x80) != 0;
            int blockType = hdr[0] & 0x7F;
            int len = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            if (len < 0 || len > 4 * 1024 * 1024) break;

            if (blockType == 4)
            {
                byte[] buf = new byte[len];
                if (fs.Read(buf, 0, len) != len || len < 8) return default;
                int pos = 0;
                uint vendorLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
                pos += 4 + (int)Math.Min(vendorLen, (uint)(len - 4));
                if (pos + 4 > len) return default;
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
                pos += 4;

                string? aa = null, ar = null, al = null, ti = null;
                int? dn = null, tn = null;
                for (uint i = 0; i < count && pos + 4 <= len; i++)
                {
                    uint clen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
                    pos += 4;
                    if (clen > (uint)(len - pos)) break;
                    string entry = Encoding.UTF8.GetString(buf, pos, (int)clen);
                    pos += (int)clen;
                    int eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = entry[..eq].Trim().ToUpperInvariant();
                    string v = entry[(eq + 1)..].Trim();
                    switch (k)
                    {
                        case "ALBUMARTIST" or "ALBUM ARTIST": aa ??= v; break;
                        case "ARTIST": ar ??= v; break;
                        case "ALBUM": al ??= v; break;
                        case "TITLE": ti ??= v; break;
                        case "DISCNUMBER" or "DISC": dn ??= ParseLeadingPositiveInt(v); break;
                        case "TRACKNUMBER" or "TRACK": tn ??= ParseLeadingPositiveInt(v); break;
                    }
                }
                return new EmbeddedHeaderTags(aa, ar, al, ti, dn, tn);
            }

            fs.Seek(len, SeekOrigin.Current);
            if (isLast) break;
        }
        return default;
    }

    private static EmbeddedHeaderTags ReadId3v2Comments(Stream fs)
    {
        Span<byte> hdr = stackalloc byte[10];
        if (fs.Read(hdr) != 10 || hdr[0] != 'I' || hdr[1] != 'D' || hdr[2] != '3')
            return default;

        byte version = hdr[3];
        if (version is < 3 or > 4) return default;
        int tagSize = ((hdr[6] & 0x7F) << 21) | ((hdr[7] & 0x7F) << 14) | ((hdr[8] & 0x7F) << 7) | (hdr[9] & 0x7F);
        int readCap = Math.Min(tagSize, 512 * 1024);
        byte[] buf = new byte[readCap];
        int actual = fs.Read(buf, 0, readCap);

        string? aa = null, ar = null, al = null, ti = null;
        int? dn = null, tn = null;
        int pos = 0;
        while (pos + 10 <= actual)
        {
            if (buf[pos] == 0) break;
            string frameId = Encoding.ASCII.GetString(buf, pos, 4);
            int frameSize = version == 4
                ? ((buf[pos + 4] & 0x7F) << 21) | ((buf[pos + 5] & 0x7F) << 14) | ((buf[pos + 6] & 0x7F) << 7) | (buf[pos + 7] & 0x7F)
                : BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(pos + 4, 4));
            pos += 10;
            if (frameSize <= 1 || pos + frameSize > actual) break;

            if (frameId is "TPE2" or "TPE1" or "TALB" or "TIT2" or "TPOS" or "TRCK")
            {
                string val = DecodeId3TextFrame(buf.AsSpan(pos, frameSize));
                switch (frameId)
                {
                    case "TPE2": aa ??= val; break;
                    case "TPE1": ar ??= val; break;
                    case "TALB": al ??= val; break;
                    case "TIT2": ti ??= val; break;
                    case "TPOS": dn ??= ParseLeadingPositiveInt(val); break;
                    case "TRCK": tn ??= ParseLeadingPositiveInt(val); break;
                }
            }
            pos += frameSize;
        }
        return new EmbeddedHeaderTags(aa, ar, al, ti, dn, tn);
    }

    private static string DecodeId3TextFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= 1) return "";
        byte enc = payload[0];
        var raw = payload[1..];
        Encoding e = enc switch
        {
            1 => Encoding.Unicode,
            2 => Encoding.BigEndianUnicode,
            3 => Encoding.UTF8,
            _ => Encoding.Latin1,
        };
        return e.GetString(raw).Trim('\0', ' ');
    }

    private static int? ParseLeadingPositiveInt(string raw)
    {
        var m = LeadingPositiveIntRegex().Match(raw);
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) && v > 0 ? v : null;
    }

    [GeneratedRegex(@"^(?<disc>\d{1,2})-(?<track>\d{2,3})\s+")]
    private static partial Regex ValidDiscTrackPrefixRegex();

    [GeneratedRegex(@"^(?<track>\d{1,3})(?:[\.\-\s_]+)(?!-)")]
    private static partial Regex LeadingTrackOnlyRegex();

    [GeneratedRegex(@"^\d+--\d+\s+")]
    private static partial Regex BrokenDiscTrackRegex();

    [GeneratedRegex(@"^#\s+")]
    private static partial Regex LeadingHashPrefixRegex();

    [GeneratedRegex(@"\s*(?:;\s*|,\s+|\s+&\s+|\s+Feat\.\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MultiArtistSplitRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^0*(\d+)")]
    private static partial Regex LeadingPositiveIntRegex();
}
