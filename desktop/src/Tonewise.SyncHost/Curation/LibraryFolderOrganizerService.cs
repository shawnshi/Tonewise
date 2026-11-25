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

    public static FolderOrganizeSummary OrganizeLibraryFolders(
        string musicRoot,
        string manifestDbPath,
        bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(musicRoot) || !LongPathHelper.DirectoryExists(musicRoot))
            return new FolderOrganizeSummary(0, 0, 0, 0, 0, []);
        if (string.IsNullOrWhiteSpace(manifestDbPath) || !File.Exists(manifestDbPath))
            return new FolderOrganizeSummary(0, 0, 0, 0, 0, []);

        string normalizedRoot = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(musicRoot)).TrimEnd('\\', '/');

        using var conn = dryRun
            ? TonewiseSqlite.OpenReadOnly(manifestDbPath)
            : TonewiseSqlite.OpenReadWrite(manifestDbPath);

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

        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        var movedPairs = new List<(string SourceFull, string TargetFull)>();

        foreach (var row in rows)
        {
            string currentRelPosix = row.RelPath.Replace('\\', '/').TrimStart('/');
            string sourceFull = Path.GetFullPath(Path.Combine(normalizedRoot, currentRelPosix.Replace('/', Path.DirectorySeparatorChar)));
            if (!sourceFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                continue;
            }

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
            string targetFull = Path.GetFullPath(Path.Combine(targetDir, targetFileName));
            if (!targetFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                continue;
            }
            string targetRelPosix = $"{artistSeg}/{albumSeg}/{targetFileName}";

            if (string.Equals(
                LongPathHelper.StripExtendedPrefix(sourceFull),
                LongPathHelper.StripExtendedPrefix(targetFull),
                StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                continue;
            }

            if (dryRun)
            {
                movedCount++;
                if (wasAlreadyOverlong)
                {
                    longPathFixedCount++;
                }
                if (samples.Count < 12)
                {
                    samples.Add($"{currentRelPosix} -> {targetRelPosix}");
                }
                continue;
            }

            try
            {
                LongPathHelper.EnsureDirectory(targetDir);
                string finalTargetFull = EnsureUniqueTargetPath(targetFull, sourceFull);
                if (!string.Equals(finalTargetFull, targetFull, StringComparison.OrdinalIgnoreCase))
                {
                    targetFull = Path.GetFullPath(finalTargetFull);
                    if (!targetFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedCount++;
                        continue;
                    }
                    targetRelPosix = Path.GetRelativePath(normalizedRoot, targetFull).Replace('\\', '/');
                }

                File.Move(LongPathHelper.ToExtendedPath(sourceFull), LongPathHelper.ToExtendedPath(targetFull));
                movedPairs.Add((sourceFull, targetFull));
                movedCount++;
                if (wasAlreadyOverlong)
                {
                    longPathFixedCount++;
                }
                if (samples.Count < 12)
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

        if (dryRun)
        {
            return new FolderOrganizeSummary(totalChecked, movedCount, longPathFixedCount, staleUids.Count, skippedCount, samples);
        }

        string journalDir = Path.GetDirectoryName(Path.GetFullPath(manifestDbPath)) ?? Path.Combine(normalizedRoot, ".tonewise");
        string journalPath = Path.Combine(journalDir, "organize_journal.json");
        if (movedPairs.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(journalDir);
                string journalJson = JsonSerializer.Serialize(
                    movedPairs.Select(p => new { source = p.SourceFull, target = p.TargetFull }),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(journalPath, journalJson + "\n", new UTF8Encoding(false));
            }
            catch
            {
                // Best-effort forensic journal write before DB commit
            }
        }

        if (staleUids.Count > 0 || dbUpdates.Count > 0)
        {
            try
            {
                using var tx = TonewiseSqlite.BeginImmediateTransaction(conn);
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
            catch
            {
                for (int i = movedPairs.Count - 1; i >= 0; i--)
                {
                    var (src, dst) = movedPairs[i];
                    try
                    {
                        string extSrc = LongPathHelper.ToExtendedPath(src);
                        string extDst = LongPathHelper.ToExtendedPath(dst);
                        if (LongPathHelper.FileExists(dst) && !LongPathHelper.FileExists(src))
                        {
                            string? parent = Path.GetDirectoryName(src);
                            if (!string.IsNullOrWhiteSpace(parent)) LongPathHelper.EnsureDirectory(parent);
                            File.Move(extDst, extSrc);
                        }
                    }
                    catch
                    {
                        // Best-effort rollback of moved file
                    }
                }
                throw;
            }
        }

        if (File.Exists(journalPath))
        {
            try { File.Delete(journalPath); } catch { }
        }

        // Preserve companion cover/booklet assets and prune empty source folders after DB commit succeeds
        foreach (var (oldDir, newDir) in sourceAlbumDirsTouched)
        {
            CopyCompanionAssetsIfMissing(oldDir, newDir);
            TryCleanupEmptyDirectories(oldDir, normalizedRoot);
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
        int? TrackNumber,
        string? Composer = null,
        string? Conductor = null,
        string? WorkName = null,
        string? MovementName = null,
        int? MovementNumber = null,
        string? Genre = null,
        double? ReplayGainTrackDb = null,
        double? ReplayGainAlbumDb = null,
        int? SampleRateHz = null,
        int? BitDepth = null,
        long? DurationMs = null
    );

    public static EmbeddedHeaderTags ReadEmbeddedHeaderTags(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            using var fs = new FileStream(LongPathHelper.ToExtendedPath(filePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (ext == ".flac") return ReadFlacComments(fs);
            if (ext == ".mp3") return ReadId3v2Comments(fs);
            if (ext == ".wav") return ReadWavHeaderInfo(fs);
        }
        catch
        {
        }
        return default;
    }

    private static EmbeddedHeaderTags ReadWavHeaderInfo(Stream fs)
    {
        Span<byte> riff = stackalloc byte[12];
        if (fs.Read(riff) != 12 || riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F'
            || riff[8] != 'W' || riff[9] != 'A' || riff[10] != 'V' || riff[11] != 'E')
            return default;

        int? sr = null, bd = null;
        int byteRate = 0;
        long dataSize = 0;
        Span<byte> hdr = stackalloc byte[8];
        while (fs.Read(hdr) == 8)
        {
            uint chunkLen = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
            if (hdr[0] == 'f' && hdr[1] == 'm' && hdr[2] == 't' && hdr[3] == ' ' && chunkLen >= 16)
            {
                byte[] fmt = new byte[Math.Min(chunkLen, 64)];
                if (fs.Read(fmt, 0, fmt.Length) == fmt.Length)
                {
                    int parsedSr = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                    byteRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(8, 4));
                    int parsedBd = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));
                    if (parsedSr > 0) sr = parsedSr;
                    if (parsedBd > 0) bd = parsedBd;
                    if (chunkLen > fmt.Length) fs.Seek(chunkLen - fmt.Length, SeekOrigin.Current);
                }
                else
                {
                    break;
                }
            }
            else if (hdr[0] == 'd' && hdr[1] == 'a' && hdr[2] == 't' && hdr[3] == 'a')
            {
                dataSize = chunkLen;
                fs.Seek(chunkLen, SeekOrigin.Current);
            }
            else
            {
                fs.Seek(chunkLen, SeekOrigin.Current);
            }

            if ((chunkLen & 1) != 0 && fs.Position < fs.Length)
            {
                fs.Seek(1, SeekOrigin.Current);
            }
        }

        long? durMs = byteRate > 0 && dataSize > 0 ? (long)(dataSize * 1000.0 / byteRate) : null;
        return new EmbeddedHeaderTags(
            null, null, null, null, null, null,
            SampleRateHz: sr,
            BitDepth: bd,
            DurationMs: durMs);
    }

    private static EmbeddedHeaderTags ReadFlacComments(Stream fs)
    {
        Span<byte> sig = stackalloc byte[4];
        if (fs.Read(sig) != 4 || sig[0] != 'f' || sig[1] != 'L' || sig[2] != 'a' || sig[3] != 'C')
            return default;

        int? flacSampleRate = null, flacBitDepth = null;
        long? flacDurationMs = null;
        Span<byte> hdr = stackalloc byte[4];
        Span<byte> si = stackalloc byte[34];
        while (fs.Read(hdr) == 4)
        {
            bool isLast = (hdr[0] & 0x80) != 0;
            int blockType = hdr[0] & 0x7F;
            int len = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            if (len < 0 || len > 4 * 1024 * 1024) break;

            if (blockType == 0 && len >= 34)
            {
                if (fs.Read(si) != 34) break;
                ulong packed = BinaryPrimitives.ReadUInt64BigEndian(si.Slice(10, 8));
                int sr = (int)((packed >> 44) & 0xFFFFFUL);
                int bd = (int)(((packed >> 36) & 0x1FUL) + 1);
                long totalSamples = (long)(packed & 0xF_FFFF_FFFFUL);
                if (sr > 0)
                {
                    flacSampleRate = sr;
                    flacBitDepth = bd;
                    if (totalSamples > 0)
                    {
                        flacDurationMs = totalSamples * 1000L / sr;
                    }
                }
                if (len > 34) fs.Seek(len - 34, SeekOrigin.Current);
                if (isLast) break;
                continue;
            }

            if (blockType == 4)
            {
                byte[] buf = new byte[len];
                if (fs.Read(buf, 0, len) != len || len < 8)
                {
                    return new EmbeddedHeaderTags(
                        null, null, null, null, null, null,
                        SampleRateHz: flacSampleRate,
                        BitDepth: flacBitDepth,
                        DurationMs: flacDurationMs);
                }
                int pos = 0;
                uint vendorLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
                pos += 4 + (int)Math.Min(vendorLen, (uint)(len - 4));
                if (pos + 4 > len)
                {
                    return new EmbeddedHeaderTags(
                        null, null, null, null, null, null,
                        SampleRateHz: flacSampleRate,
                        BitDepth: flacBitDepth,
                        DurationMs: flacDurationMs);
                }
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
                pos += 4;

                string? aa = null, ar = null, al = null, ti = null, work = null, contentGroup = null, mov = null, genre = null;
                int? dn = null, tn = null, movNo = null;
                double? rgTrack = null, rgAlbum = null;
                var composers = new List<string>();
                var conductors = new List<string>();
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
                    if (string.IsNullOrEmpty(v)) continue;
                    switch (k)
                    {
                        case "ALBUMARTIST" or "ALBUM ARTIST": aa ??= v; break;
                        case "ARTIST": ar ??= v; break;
                        case "ALBUM": al ??= v; break;
                        case "TITLE": ti ??= v; break;
                        case "DISCNUMBER" or "DISC": dn ??= ParseLeadingPositiveInt(v); break;
                        case "TRACKNUMBER" or "TRACK": tn ??= ParseLeadingPositiveInt(v); break;
                        case "COMPOSER": composers.Add(v); break;
                        case "CONDUCTOR": conductors.Add(v); break;
                        case "WORK": work ??= v; break;
                        case "CONTENTGROUP" or "GROUPING": contentGroup ??= v; break;
                        case "MOVEMENT" or "MOVEMENTNAME": mov ??= v; break;
                        case "MOVEMENTNUMBER": movNo ??= ParseLeadingPositiveInt(v); break;
                        case "GENRE": genre ??= v; break;
                        case "REPLAYGAIN_TRACK_GAIN": rgTrack ??= ParseGainDb(v); break;
                        case "REPLAYGAIN_ALBUM_GAIN": rgAlbum ??= ParseGainDb(v); break;
                    }
                }
                return new EmbeddedHeaderTags(
                    aa,
                    ar,
                    al,
                    ti,
                    dn,
                    tn,
                    Composer: composers.Count > 0 ? string.Join("; ", composers) : null,
                    Conductor: conductors.Count > 0 ? string.Join("; ", conductors) : null,
                    WorkName: work ?? contentGroup,
                    MovementName: mov,
                    MovementNumber: movNo,
                    Genre: genre,
                    ReplayGainTrackDb: rgTrack,
                    ReplayGainAlbumDb: rgAlbum,
                    SampleRateHz: flacSampleRate,
                    BitDepth: flacBitDepth,
                    DurationMs: flacDurationMs);
            }

            fs.Seek(len, SeekOrigin.Current);
            if (isLast) break;
        }
        return new EmbeddedHeaderTags(
            null, null, null, null, null, null,
            SampleRateHz: flacSampleRate,
            BitDepth: flacBitDepth,
            DurationMs: flacDurationMs);
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

        string? aa = null, ar = null, al = null, ti = null, work = null, tit1 = null, mov = null, genre = null;
        int? dn = null, tn = null, movNo = null;
        double? rgTrack = null, rgAlbum = null;
        var composers = new List<string>();
        var conductors = new List<string>();
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

            var payload = buf.AsSpan(pos, frameSize);
            if (frameId is "TPE2" or "TPE1" or "TALB" or "TIT2" or "TPOS" or "TRCK"
                or "TCOM" or "TPE3" or "TIT1" or "MVNM" or "MVIN" or "TCON")
            {
                string val = DecodeId3TextFrame(payload);
                if (!string.IsNullOrEmpty(val))
                {
                    switch (frameId)
                    {
                        case "TPE2": aa ??= val; break;
                        case "TPE1": ar ??= val; break;
                        case "TALB": al ??= val; break;
                        case "TIT2": ti ??= val; break;
                        case "TPOS": dn ??= ParseLeadingPositiveInt(val); break;
                        case "TRCK": tn ??= ParseLeadingPositiveInt(val); break;
                        case "TCOM": composers.Add(val); break;
                        case "TPE3": conductors.Add(val); break;
                        case "TIT1": tit1 ??= val; break;
                        case "MVNM": mov ??= val; break;
                        case "MVIN": movNo ??= ParseLeadingPositiveInt(val); break;
                        case "TCON": genre ??= NormalizeId3Genre(val); break;
                    }
                }
            }
            else if (frameId == "TXXX" && TryDecodeId3TxxxFrame(payload, out string desc, out string txxxVal) && !string.IsNullOrEmpty(txxxVal))
            {
                switch (desc.ToUpperInvariant())
                {
                    case "WORK": work ??= txxxVal; break;
                    case "GROUPING" or "CONTENTGROUP": tit1 ??= txxxVal; break;
                    case "MOVEMENT" or "MOVEMENTNAME": mov ??= txxxVal; break;
                    case "MOVEMENTNUMBER": movNo ??= ParseLeadingPositiveInt(txxxVal); break;
                    case "COMPOSER" when composers.Count == 0: composers.Add(txxxVal); break;
                    case "CONDUCTOR" when conductors.Count == 0: conductors.Add(txxxVal); break;
                    case "REPLAYGAIN_TRACK_GAIN": rgTrack ??= ParseGainDb(txxxVal); break;
                    case "REPLAYGAIN_ALBUM_GAIN": rgAlbum ??= ParseGainDb(txxxVal); break;
                }
            }
            pos += frameSize;
        }
        return new EmbeddedHeaderTags(
            aa,
            ar,
            al,
            ti,
            dn,
            tn,
            Composer: composers.Count > 0 ? string.Join("; ", composers) : null,
            Conductor: conductors.Count > 0 ? string.Join("; ", conductors) : null,
            WorkName: work ?? tit1,
            MovementName: mov,
            MovementNumber: movNo,
            Genre: genre,
            ReplayGainTrackDb: rgTrack,
            ReplayGainAlbumDb: rgAlbum);
    }

    private static string DecodeId3TextFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= 1) return "";
        byte enc = payload[0];
        var raw = payload[1..];
        if (enc is 1 or 2) return DecodeUtf16Segment(raw, enc == 2);
        Encoding e = enc == 3 ? Encoding.UTF8 : Encoding.Latin1;
        return e.GetString(raw).Trim('\0', ' ', '\uFEFF');
    }

    private static bool TryDecodeId3TxxxFrame(ReadOnlySpan<byte> payload, out string description, out string value)
    {
        description = "";
        value = "";
        if (payload.Length <= 2) return false;
        byte enc = payload[0];
        var raw = payload[1..];
        if (enc is 1 or 2)
        {
            int split = -1;
            for (int i = 0; i + 1 < raw.Length; i += 2)
            {
                if (raw[i] == 0 && raw[i + 1] == 0)
                {
                    split = i;
                    break;
                }
            }
            if (split < 0 || split + 2 > raw.Length) return false;
            description = DecodeUtf16Segment(raw[..split], enc == 2);
            value = DecodeUtf16Segment(raw[(split + 2)..], enc == 2);
            return true;
        }

        int nul = raw.IndexOf((byte)0);
        if (nul < 0 || nul + 1 > raw.Length) return false;
        Encoding e = enc == 3 ? Encoding.UTF8 : Encoding.Latin1;
        description = e.GetString(raw[..nul]).Trim('\0', ' ');
        value = e.GetString(raw[(nul + 1)..]).Trim('\0', ' ');
        return true;
    }

    private static string DecodeUtf16Segment(ReadOnlySpan<byte> slice, bool defaultBigEndian)
    {
        if (slice.Length < 2) return "";
        bool bigEndian = defaultBigEndian;
        if (slice[0] == 0xFF && slice[1] == 0xFE)
        {
            bigEndian = false;
            slice = slice[2..];
        }
        else if (slice[0] == 0xFE && slice[1] == 0xFF)
        {
            bigEndian = true;
            slice = slice[2..];
        }
        Encoding e = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        return e.GetString(slice).Trim('\0', ' ', '\uFEFF');
    }

    private static double? ParseGainDb(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string cleaned = raw.Trim();
        int dbIdx = cleaned.IndexOf("db", StringComparison.OrdinalIgnoreCase);
        if (dbIdx >= 0) cleaned = cleaned[..dbIdx].Trim();
        return double.TryParse(
            cleaned,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double val) && double.IsFinite(val)
            ? val
            : null;
    }

    private static string? NormalizeId3Genre(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (trimmed.StartsWith('('))
        {
            int close = trimmed.IndexOf(')');
            if (close > 1 && int.TryParse(trimmed[1..close], out int id3Num))
            {
                string after = trimmed[(close + 1)..].Trim();
                if (!string.IsNullOrEmpty(after)) return after;
                if (id3Num == 32) return "Classical";
            }
        }
        return trimmed;
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

/// <summary>
/// Watches a music library root using <see cref="FileSystemWatcher"/>, ignores internal metadata
/// and temporary files, coalesces burst edits with a debounce timer, and notifies callers safely.
/// </summary>
public sealed class LibraryFileWatcherService : IDisposable
{
    private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".wav", ".aiff", ".aif", ".m4a", ".alac", ".aac", ".ogg", ".opus", ".dsf", ".dff", ".m3u8"
    };

    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".part", ".crdownload", ".bak"
    };

    private readonly string _musicRoot;
    private readonly Func<IReadOnlyList<string>, Task> _onChangesDetected;
    private readonly int _debounceMs;
    private FileSystemWatcher? _watcher;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _suppressedPathsUntilUtcTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounceTimer;

    private int _suspendCount;
    private long _suppressUntilUtcTicks;
    private bool _isProcessing;
    private bool _rescheduleRequested;
    private bool _fullRescanPending;
    private bool _watcherRebuildPending;
    private bool _disposed;

    public LibraryFileWatcherService(
        string musicRoot,
        Func<IReadOnlyList<string>, Task> onChangesDetected,
        int debounceMs = 1800)
    {
        _musicRoot = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(musicRoot));
        _onChangesDetected = onChangesDetected ?? throw new ArgumentNullException(nameof(onChangesDetected));
        _debounceMs = Math.Clamp(debounceMs, 50, 30_000);
        _debounceTimer = new Timer(_ => _ = FlushPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = CreateWatcher();
    }

    private FileSystemWatcher CreateWatcher()
    {
        var watcher = new FileSystemWatcher(_musicRoot)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += OnWatcherEvent;
        watcher.Deleted += OnWatcherEvent;
        watcher.Changed += OnWatcherEvent;
        watcher.Renamed += OnWatcherRenamed;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null) return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnWatcherEvent;
            watcher.Deleted -= OnWatcherEvent;
            watcher.Changed -= OnWatcherEvent;
            watcher.Renamed -= OnWatcherRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
        catch { }
    }

    private void TryRebuildWatcherIfNeeded()
    {
        FileSystemWatcher? oldWatcher = null;
        lock (_gate)
        {
            if (_disposed || !_watcherRebuildPending) return;
            if (!Directory.Exists(_musicRoot)) return;
            oldWatcher = _watcher;
            try
            {
                _watcher = CreateWatcher();
                _watcherRebuildPending = false;
            }
            catch
            {
                // Leave _watcherRebuildPending = true so subsequent ticks retry
                return;
            }
        }
        DisposeWatcher(oldWatcher);
    }

    public IDisposable SuspendWatching()
    {
        Interlocked.Increment(ref _suspendCount);
        lock (_gate)
        {
            _pendingPaths.Clear();
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        return new SuspendScope(this);
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        if (!ShouldProcessPath(e.FullPath, isChangeOnly: e.ChangeType == WatcherChangeTypes.Changed))
            return;
        EnqueuePath(e.FullPath);
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        bool oldOk = ShouldProcessPath(e.OldFullPath, isChangeOnly: false);
        bool newOk = ShouldProcessPath(e.FullPath, isChangeOnly: false);
        if (!oldOk && !newOk) return;
        if (oldOk) EnqueuePath(e.OldFullPath);
        if (newOk) EnqueuePath(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _fullRescanPending = true;
            _watcherRebuildPending = true;
            _pendingPaths.Add(_musicRoot);
            if (Volatile.Read(ref _suspendCount) == 0)
            {
                _debounceTimer.Change(_debounceMs, Timeout.Infinite);
            }
        }
    }

    public static bool ShouldProcessPath(string? fullPath, bool isChangeOnly)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        string normalized = fullPath.Replace('\\', '/');
        foreach (string seg in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg.StartsWith('.')) return false;
        }

        string ext = Path.GetExtension(fullPath);
        if (TempExtensions.Contains(ext)) return false;

        if (!string.IsNullOrEmpty(ext))
        {
            return WatchedExtensions.Contains(ext);
        }

        // For extensionless entries (directories being created, moved, or deleted), skip noisy mtime-only Changed bubbles.
        return !isChangeOnly;
    }

    public void SuppressPathsTemporarily(IEnumerable<string> fullPaths, int durationMs = 4500)
    {
        long untilTicks = DateTime.UtcNow.AddMilliseconds(Math.Clamp(durationMs, 100, 30_000)).Ticks;
        foreach (string raw in fullPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string norm = NormalizeWatchPath(raw);
            _suppressedPathsUntilUtcTicks[norm] = untilTicks;
        }
    }

    public bool IsPathSuppressed(string? path, long? nowUtcTicksOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string norm = NormalizeWatchPath(path);
        if (_suppressedPathsUntilUtcTicks.TryGetValue(norm, out long untilTicks))
        {
            long nowTicks = nowUtcTicksOverride ?? DateTime.UtcNow.Ticks;
            if (nowTicks < untilTicks) return true;
            _suppressedPathsUntilUtcTicks.TryRemove(norm, out _);
        }
        return false;
    }

    private static string NormalizeWatchPath(string fullPath)
    {
        string stripped = LongPathHelper.StripExtendedPrefix(fullPath);
        try
        {
            return Path.GetFullPath(stripped);
        }
        catch
        {
            return stripped;
        }
    }

    private void EnqueuePath(string path)
    {
        if (_disposed || Volatile.Read(ref _suspendCount) > 0) return;
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressUntilUtcTicks)) return;
        if (IsPathSuppressed(path)) return;

        lock (_gate)
        {
            if (_disposed) return;
            _pendingPaths.Add(path);
            _debounceTimer.Change(_debounceMs, Timeout.Infinite);
        }
    }

    private async Task FlushPendingAsync()
    {
        List<string> snapshot;
        int? suppressDelayMs = null;
        lock (_gate)
        {
            if (_disposed || Volatile.Read(ref _suspendCount) > 0)
            {
                _pendingPaths.Clear();
                return;
            }
            long suppressUntil = Interlocked.Read(ref _suppressUntilUtcTicks);
            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks < suppressUntil)
            {
                if (_pendingPaths.Count > 0 || _fullRescanPending)
                {
                    if (_fullRescanPending)
                        _pendingPaths.Add(_musicRoot);
                    suppressDelayMs = (int)Math.Clamp((suppressUntil - nowTicks) / TimeSpan.TicksPerMillisecond + 50, 50, 2000);
                }
                goto RescheduleDuringSuppress;
            }
            if (_isProcessing)
            {
                _rescheduleRequested = true;
                return;
            }
            if (_fullRescanPending)
            {
                _pendingPaths.Add(_musicRoot);
            }
            if (_pendingPaths.Count == 0) return;

            snapshot = _pendingPaths.ToList();
            _pendingPaths.Clear();
            _fullRescanPending = false;
            _isProcessing = true;
        }

        goto RunCallback;

    RescheduleDuringSuppress:
        if (suppressDelayMs.HasValue)
        {
            try { _debounceTimer.Change(suppressDelayMs.Value, Timeout.Infinite); } catch { }
        }
        return;

    RunCallback:
        TryRebuildWatcherIfNeeded();
        try
        {
            await WaitForFilesReadableAsync(snapshot).ConfigureAwait(false);
            if (!_disposed && Volatile.Read(ref _suspendCount) == 0)
            {
                await _onChangesDetected(snapshot).ConfigureAwait(false);
            }
        }
        catch
        {
            // Non-fatal background watcher callback guard
        }
        finally
        {
            bool triggerAgain = false;
            lock (_gate)
            {
                _isProcessing = false;
                if (!_disposed && (_rescheduleRequested || _pendingPaths.Count > 0))
                {
                    _rescheduleRequested = false;
                    triggerAgain = true;
                }
            }
            if (triggerAgain)
            {
                try { _debounceTimer.Change(_debounceMs, Timeout.Infinite); } catch { }
            }
        }
    }

    private static async Task WaitForFilesReadableAsync(IReadOnlyList<string> paths)
    {
        int[] backoffMs = [100, 150, 200, 250, 300, 400];
        int checks = 0;
        foreach (string rawPath in paths)
        {
            if (checks >= 16) break;
            string ext = Path.GetExtension(rawPath);
            if (string.IsNullOrEmpty(ext) || !LongPathHelper.FileExists(rawPath)) continue;

            checks++;
            string ioPath = LongPathHelper.ToExtendedIfLong(rawPath);
            for (int attempt = 0; attempt < backoffMs.Length; attempt++)
            {
                try
                {
                    using var fs = new FileStream(ioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    break;
                }
                catch
                {
                    await Task.Delay(backoffMs[attempt]).ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class SuspendScope(LibraryFileWatcherService owner) : IDisposable
    {
        private int _ended;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Interlocked.Exchange(ref owner._suppressUntilUtcTicks, DateTime.UtcNow.AddMilliseconds(450).Ticks);
                if (Interlocked.Decrement(ref owner._suspendCount) == 0)
                {
                    bool scheduleRescan = false;
                    lock (owner._gate)
                    {
                        if (!owner._disposed && owner._fullRescanPending)
                        {
                            owner._pendingPaths.Add(owner._musicRoot);
                            scheduleRescan = true;
                        }
                    }
                    if (scheduleRescan)
                    {
                        try { owner._debounceTimer.Change(500, Timeout.Infinite); } catch { }
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcherToDispose;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingPaths.Clear();
            watcherToDispose = _watcher;
            _watcher = null;
        }

        DisposeWatcher(watcherToDispose);
        try { _debounceTimer.Dispose(); } catch { }
    }
}
