using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.SyncHost;

public sealed partial class SyncOutboxAndPairingStore
{
    public OutboxSummary GetOutboxSummary(string? deviceId = null)
    {
        var resolved = ResolveOutbox(deviceId);
        return new OutboxSummary(resolved.Items.Count, resolved.Tracks.Count, resolved.TotalBytes, resolved.DuplicateTrackCount);
    }

    public ResolvedOutboxPayload ResolveOutbox(string? deviceId = null, string? hostName = null)
    {
        List<SyncOutboxItem> relevantItems;
        lock (_gate)
        {
            relevantItems = FilterOutboxForDevice(LoadOutboxUnlocked(), deviceId);
        }

        string effectiveHost = string.IsNullOrWhiteSpace(hostName) ? Environment.MachineName : hostName;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (relevantItems.Count == 0)
        {
            return new ResolvedOutboxPayload(effectiveHost, nowMs, relevantItems, [], [], 0, 0);
        }

        var rows = LoadManifestRows();
        var rowsByUid = new Dictionary<string, ManifestTrackRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            rowsByUid[r.TrackUid] = r;
            if (!string.IsNullOrWhiteSpace(r.AudioFrameBlake3Hex))
                rowsByUid.TryAdd(r.AudioFrameBlake3Hex, r);
        }

        var libRefs = rows.Select(r => r.ToLibraryTrackRef(BuildFullPath(r.RelPath))).ToList();

        bool hasPlaylistItems = relevantItems.Any(i => string.Equals(i.Kind, "playlist", StringComparison.OrdinalIgnoreCase));
        var libIndex = hasPlaylistItems ? PlaylistTrackMatcher.BuildIndex(libRefs) : null;
        var playlistsInDir = libIndex is not null
            ? PlaylistStore.ListPlaylists(_musicRoot, libIndex)
            : [];

        var outboxIdsByUid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var canonicalUidByTargetPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var canonicalUidByPhysicalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resolvedTrackOrder = new List<ManifestTrackRow>();
        var resolvedPlaylists = new List<ResolvedSyncPlaylist>();
        var coverCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int duplicateTrackCount = 0;

        void AttachTrack(ManifestTrackRow row, string outboxId)
        {
            if (!TryGetValidPhysicalFileInfo(row.RelPath, out string resolvedFullPath, out long realSize, out long realMtime))
                return;

            string targetRel = BuildTargetRelPath(row);
            string canonicalUid = row.TrackUid;
            bool isDuplicate = false;

            if (outboxIdsByUid.ContainsKey(row.TrackUid))
            {
                isDuplicate = true;
            }
            else if (canonicalUidByPhysicalPath.TryGetValue(resolvedFullPath, out var existingByPhysical))
            {
                canonicalUid = existingByPhysical;
                isDuplicate = true;
            }
            else if (canonicalUidByTargetPath.TryGetValue(targetRel, out var existingByTarget))
            {
                canonicalUid = existingByTarget;
                isDuplicate = true;
            }

            if (!isDuplicate)
            {
                var normalizedRow = row with { FileSize = realSize > 0 ? realSize : row.FileSize, MtimeMs = realMtime > 0 ? realMtime : row.MtimeMs };
                outboxIdsByUid[canonicalUid] = [outboxId];
                canonicalUidByPhysicalPath[resolvedFullPath] = canonicalUid;
                canonicalUidByTargetPath[targetRel] = canonicalUid;
                resolvedTrackOrder.Add(normalizedRow);
            }
            else
            {
                duplicateTrackCount++;
                var owners = outboxIdsByUid[canonicalUid];
                if (!owners.Contains(outboxId, StringComparer.OrdinalIgnoreCase))
                {
                    owners.Add(outboxId);
                }
            }
        }

        foreach (var item in relevantItems)
        {
            switch (item.Kind.ToLowerInvariant())
            {
                case "playlist":
                {
                    var plSummary = playlistsInDir.FirstOrDefault(p =>
                        string.Equals(p.Name, item.TargetKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileNameWithoutExtension(p.FilePath), item.TargetKey, StringComparison.OrdinalIgnoreCase));
                    if (plSummary is null) break;

                    var doc = PlaylistStore.LoadPlaylist(plSummary.FilePath, libIndex!, _musicRoot);
                    var plTrackUids = new List<string>();
                    var plRelPaths = new List<string>();
                    foreach (var entry in doc.Entries)
                    {
                        if (!entry.IsLocalMatched || string.IsNullOrWhiteSpace(entry.MatchedTrackUid))
                            continue;
                        if (!rowsByUid.TryGetValue(entry.MatchedTrackUid, out var row))
                            continue;
                        if (!TryGetValidPhysicalFileInfo(row.RelPath, out _, out _, out _))
                            continue;

                        AttachTrack(row, item.Id);
                        plTrackUids.Add(row.TrackUid);
                        plRelPaths.Add(BuildTargetRelPath(row));
                    }
                    resolvedPlaylists.Add(new ResolvedSyncPlaylist(item.Id, plSummary.Name, plRelPaths, plTrackUids));
                    break;
                }
                case "genre":
                {
                    bool matchUnlabeled = string.Equals(item.TargetKey, "(未标注流派)", StringComparison.OrdinalIgnoreCase);
                    foreach (var row in rows)
                    {
                        bool match = matchUnlabeled
                            ? string.IsNullOrWhiteSpace(row.Genre)
                            : string.Equals(row.Genre.Trim(), item.TargetKey.Trim(), StringComparison.OrdinalIgnoreCase);
                        if (match) AttachTrack(row, item.Id);
                    }
                    break;
                }
                case "artist":
                {
                    string targetArtist = item.TargetKey.Trim();
                    foreach (var row in rows)
                    {
                        if (row.MatchesArtist(targetArtist))
                        {
                            AttachTrack(row, item.Id);
                        }
                    }
                    break;
                }
                case "tracks":
                {
                    foreach (string uid in item.TrackUids)
                    {
                        if (rowsByUid.TryGetValue(uid, out var row))
                        {
                            AttachTrack(row, item.Id);
                        }
                    }
                    break;
                }
            }
        }

        long totalBytes = 0;
        var resolvedTracks = new List<ResolvedSyncTrack>(resolvedTrackOrder.Count);
        foreach (var row in resolvedTrackOrder)
        {
            string fullPath = BuildFullPath(row.RelPath);
            string dir = Path.GetDirectoryName(fullPath) ?? "";
            bool hasCover = false;
            if (!string.IsNullOrEmpty(dir))
            {
                if (!coverCache.TryGetValue(dir, out hasCover))
                {
                    hasCover = FindCoverFileInDir(dir) is not null;
                    coverCache[dir] = hasCover;
                }
            }

            string targetRel = BuildTargetRelPath(row);
            totalBytes += row.FileSize;
            resolvedTracks.Add(new ResolvedSyncTrack(
                TrackUid: row.TrackUid,
                RelPath: row.RelPath,
                TargetRelPath: targetRel,
                Title: row.Title,
                Artist: row.ArtistDisplay,
                Album: row.Album,
                AlbumArtist: row.AlbumArtist,
                Genre: row.Genre,
                DurationMs: row.DurationMs,
                FileSize: row.FileSize,
                MtimeMs: row.MtimeMs,
                HasCover: hasCover,
                SourceOutboxIds: outboxIdsByUid[row.TrackUid],
                HeaderBlake3: row.HeaderBlake3Hex,
                AudioFrameBlake3: row.AudioFrameBlake3Hex
            ));
        }

        return new ResolvedOutboxPayload(effectiveHost, nowMs, relevantItems, resolvedTracks, resolvedPlaylists, totalBytes, duplicateTrackCount);
    }

    public IReadOnlyList<CatalogEntry> GetCatalog(string kind)
    {
        var rows = LoadManifestRows();
        switch (kind.Trim().ToLowerInvariant())
        {
            case "playlists":
            {
                string dir = PlaylistStore.GetPlaylistsDirectory(_musicRoot);
                if (!Directory.Exists(dir)) return [];

                var libRefs = rows.Select(r => r.ToLibraryTrackRef(BuildFullPath(r.RelPath))).ToList();
                var libIndex = PlaylistTrackMatcher.BuildIndex(libRefs);
                var byUid = rows.ToDictionary(r => r.TrackUid, StringComparer.OrdinalIgnoreCase);

                var files = Directory.EnumerateFiles(dir, "*.m3u8")
                    .Concat(Directory.EnumerateFiles(dir, "*.m3u"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var result = new List<CatalogEntry>(files.Count);
                foreach (string file in files)
                {
                    try
                    {
                        var doc = PlaylistStore.LoadPlaylist(file, libIndex, _musicRoot);
                        int matched = 0;
                        long bytes = 0;
                        foreach (var entry in doc.Entries)
                        {
                            if (!entry.IsLocalMatched || entry.MatchedTrackUid is null) continue;
                            matched++;
                            if (byUid.TryGetValue(entry.MatchedTrackUid, out var row))
                            {
                                bytes += row.FileSize;
                            }
                        }
                        result.Add(new CatalogEntry("playlist", doc.Name, doc.Name, matched, bytes));
                    }
                    catch
                    {
                        // Skip unreadable/corrupt playlist file
                    }
                }
                return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            case "genres":
            {
                return rows
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.Genre) ? "(未标注流派)" : r.Genre.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new CatalogEntry("genre", g.Key, g.Key, g.Count(), g.Sum(r => r.FileSize)))
                    .OrderByDescending(c => c.TrackCount)
                    .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            case "artists":
            {
                var counts = new Dictionary<string, (string Name, int Tracks, long Bytes)>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows)
                {
                    foreach (string name in r.AllArtists)
                    {
                        if (!counts.TryGetValue(name, out var prev))
                            counts[name] = (name, 1, r.FileSize);
                        else
                            counts[name] = (prev.Name, prev.Tracks + 1, prev.Bytes + r.FileSize);
                    }
                }
                return counts.Values
                    .OrderByDescending(v => v.Tracks)
                    .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Take(500)
                    .Select(v => new CatalogEntry("artist", v.Name, v.Name, v.Tracks, v.Bytes))
                    .ToList();
            }
            default:
                return [];
        }
    }

    public bool TryResolveTrackPhysicalPath(string trackUid, out string fullPath, out string relPath)
    {
        fullPath = "";
        relPath = "";
        if (string.IsNullOrWhiteSpace(trackUid)) return false;

        string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
        if (!File.Exists(dbPath)) return false;

        string cleanUid = trackUid.Trim();
        string? dbRelPath = null;
        using (var conn = TonewiseSqlite.OpenReadOnly(dbPath))
        {
            using var cmd = conn.CreateCommand();
            if (TryHexToBytes32(cleanUid, out byte[]? b3) && b3 is not null)
            {
                cmd.CommandText = """
                    SELECT rel_path
                    FROM tracks
                    WHERE track_uid = $uid OR audio_frame_blake3 = $b3
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$uid", cleanUid);
                cmd.Parameters.AddWithValue("$b3", b3);
            }
            else
            {
                cmd.CommandText = """
                    SELECT rel_path
                    FROM tracks
                    WHERE track_uid = $uid
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$uid", cleanUid);
            }
            object? res = cmd.ExecuteScalar();
            dbRelPath = res as string;
        }

        if (string.IsNullOrWhiteSpace(dbRelPath)) return false;
        if (!TryGetValidPhysicalFileInfo(dbRelPath, out string audioFull, out _, out _))
            return false;

        relPath = dbRelPath;
        fullPath = audioFull;
        return true;
    }

    private static bool TryHexToBytes32(string hex, out byte[]? bytes)
    {
        bytes = null;
        if (hex.Length != 64) return false;
        foreach (char c in hex)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        bytes = Convert.FromHexString(hex);
        return bytes.Length == 32;
    }

    public bool TryResolveTrackCoverPath(string trackUid, out string coverPath)
    {
        coverPath = "";
        if (!TryResolveTrackPhysicalPath(trackUid, out string audioFull, out _))
            return false;

        string? dir = Path.GetDirectoryName(audioFull);
        if (string.IsNullOrWhiteSpace(dir)) return false;

        string? candidate = FindCoverFileInDir(dir);
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        string rootFull = GetNormalizedRootPrefix();
        string fullCandidate = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(candidate));
        if (!fullCandidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return false;

        coverPath = fullCandidate;
        return true;
    }

    public static IReadOnlyList<string> GetLocalLanUrls(int port)
    {
        if (port <= 0) return [];
        return GetScoredLanIpv4Candidates()
            .Select(c => $"http://{c.Address}:{port}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IPAddress? ResolvePreferredLanIpv4() =>
        GetScoredLanIpv4Candidates().FirstOrDefault().Address;

    private static List<(int Score, IPAddress Address)> GetScoredLanIpv4Candidates()
    {
        var scored = new List<(int Score, IPAddress Address)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                int ifacePenalty = (desc.Contains("vethernet") || desc.Contains("vmware") || desc.Contains("virtualbox")
                                    || desc.Contains("wsl") || desc.Contains("tailscale") || desc.Contains("zerotier")
                                    || desc.Contains("hyper-v"))
                    ? 100
                    : (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 20);

                var ipProps = ni.GetIPProperties();
                foreach (var uni in ipProps.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = uni.Address.GetAddressBytes();
                    if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;

                    int netRank = (b[0], b[1]) switch
                    {
                        (192, 168) => 1,
                        (10, _) => 2,
                        (172, >= 16 and <= 31) => 3,
                        _ => 10
                    };
                    scored.Add((ifacePenalty + netRank, uni.Address));
                }
            }
        }
        catch
        {
            // Ignore interface enumeration failures
        }

        return scored
            .OrderBy(s => s.Score)
            .ToList();
    }

    private bool TryGetValidPhysicalFileInfo(string relPath, out string fullPath, out long fileSize, out long mtimeMs)
    {
        fullPath = "";
        fileSize = 0;
        mtimeMs = 0;
        if (string.IsNullOrWhiteSpace(relPath)) return false;

        try
        {
            string rootFull = GetNormalizedRootPrefix();
            string combined = BuildFullPath(relPath);
            string resolved = LongPathHelper.StripExtendedPrefix(Path.GetFullPath(combined));
            if (!resolved.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!LongPathHelper.FileExists(resolved))
                return false;

            var fi = new FileInfo(LongPathHelper.ToExtendedIfLong(resolved));
            fileSize = fi.Length;
            mtimeMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            fullPath = resolved;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetNormalizedRootPrefix() =>
        LongPathHelper.StripExtendedPrefix(Path.GetFullPath(_musicRoot))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    private string BuildFullPath(string relPath) =>
        Path.Combine(_musicRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

    private static string? FindCoverFileInDir(string dir)
    {
        if (!LongPathHelper.DirectoryExists(dir)) return null;
        foreach (string name in CoverCandidateNames)
        {
            string candidate = Path.Combine(dir, name);
            if (LongPathHelper.FileExists(candidate)) return candidate;
        }
        return null;
    }

    private static string BuildTargetRelPath(ManifestTrackRow row)
    {
        var rawSegments = row.RelPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cleanSegments = new List<string>(rawSegments.Length);
        foreach (string seg in rawSegments)
        {
            string s = SanitizePathSegment(seg);
            if (!string.IsNullOrEmpty(s)) cleanSegments.Add(s);
        }
        if (cleanSegments.Count > 0)
        {
            return string.Join('/', cleanSegments);
        }

        string artistSeg = SanitizePathSegment(string.IsNullOrWhiteSpace(row.AlbumArtist) ? row.ArtistDisplay : row.AlbumArtist);
        if (string.IsNullOrEmpty(artistSeg)) artistSeg = "Unknown Artist";
        string albumSeg = SanitizePathSegment(row.Album);
        if (string.IsNullOrEmpty(albumSeg)) albumSeg = "Unknown Album";
        string ext = Path.GetExtension(row.RelPath);
        if (string.IsNullOrEmpty(ext)) ext = ".flac";
        string titleSeg = SanitizePathSegment(row.Title);
        if (string.IsNullOrEmpty(titleSeg)) titleSeg = row.TrackUid;
        return $"{artistSeg}/{albumSeg}/{titleSeg}{ext}";
    }

    private static string SanitizePathSegment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim();
        if (s is "." or "..") return "";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                sb.Append('_');
            else
                sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private List<ManifestTrackRow> LoadManifestRows()
    {
        var result = new List<ManifestTrackRow>();
        string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
        if (!File.Exists(dbPath)) return result;

        using var conn = TonewiseSqlite.OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT track_uid, lower(hex(audio_frame_blake3)), rel_path,
                   title, album, album_artist, artists_json, composers_json, conductors_json,
                   genre, duration_ms, sample_rate_hz, file_size, mtime_ms,
                   lower(hex(header_blake3)), work_name, movement_name
            FROM tracks
            ORDER BY album, rel_path;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string uid = reader.GetString(0);
            string blake3 = reader.IsDBNull(1) ? "" : reader.GetString(1);
            string relPath = reader.IsDBNull(2) ? "" : reader.GetString(2);
            string title = reader.IsDBNull(3) ? "" : reader.GetString(3);
            string album = reader.IsDBNull(4) ? "" : reader.GetString(4);
            string albumArtist = reader.IsDBNull(5) ? "" : reader.GetString(5);
            string artistsJson = reader.IsDBNull(6) ? "[]" : reader.GetString(6);
            string composersJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);
            string conductorsJson = reader.IsDBNull(8) ? "[]" : reader.GetString(8);
            string genre = reader.IsDBNull(9) ? "" : reader.GetString(9);
            int durationMs = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
            int sampleRate = reader.IsDBNull(11) ? 44100 : reader.GetInt32(11);
            long fileSize = reader.IsDBNull(12) ? 0L : reader.GetInt64(12);
            long mtimeMs = reader.IsDBNull(13) ? 0L : reader.GetInt64(13);
            string headerBlake3 = reader.IsDBNull(14) ? "" : reader.GetString(14);
            string workName = reader.IsDBNull(15) ? "" : reader.GetString(15);
            string movementName = reader.IsDBNull(16) ? "" : reader.GetString(16);

            result.Add(new ManifestTrackRow(
                uid, blake3, relPath, title, album, albumArtist,
                artistsJson, composersJson, conductorsJson,
                genre, durationMs, sampleRate, fileSize, mtimeMs, headerBlake3,
                workName, movementName));
        }
        return result;
    }

    private sealed record ManifestTrackRow(
        string TrackUid,
        string AudioFrameBlake3Hex,
        string RelPath,
        string Title,
        string Album,
        string AlbumArtist,
        string ArtistsJson,
        string ComposersJson,
        string ConductorsJson,
        string Genre,
        int DurationMs,
        int SampleRateHz,
        long FileSize,
        long MtimeMs,
        string HeaderBlake3Hex = "",
        string WorkName = "",
        string MovementName = ""
    )
    {
        private List<string>? _allArtists;

        public string ArtistDisplay =>
            ParseJsonArray(ArtistsJson) is { Count: > 0 } arr
                ? string.Join(", ", arr)
                : AlbumArtist;

        public string ComposersDisplay => string.Join(", ", ParseJsonArray(ComposersJson));

        public string ConductorsDisplay => string.Join(", ", ParseJsonArray(ConductorsJson));

        public LocalLibraryTrackRef ToLibraryTrackRef(string fullPath) =>
            new(
                TrackUid,
                RelPath,
                fullPath,
                Title,
                ArtistDisplay,
                Album,
                DurationMs,
                SampleRateHz,
                ComposersDisplay,
                ConductorsDisplay,
                WorkName,
                MovementName
            );

        public IReadOnlyList<string> AllArtists
        {
            get
            {
                if (_allArtists is not null) return _allArtists;
                var list = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddSplit(string? raw)
                {
                    if (string.IsNullOrWhiteSpace(raw)) return;
                    foreach (string s in ArtistEntitySplitter.SplitPerformers(raw))
                    {
                        if (seen.Add(s)) list.Add(s);
                    }
                }

                AddSplit(AlbumArtist);
                foreach (string item in ParseJsonArray(ArtistsJson)) AddSplit(item);
                foreach (string item in ParseJsonArray(ComposersJson)) AddSplit(item);
                foreach (string item in ParseJsonArray(ConductorsJson)) AddSplit(item);
                _allArtists = list;
                return _allArtists;
            }
        }

        public bool MatchesArtist(string target) =>
            AllArtists.Any(a => string.Equals(a, target, StringComparison.OrdinalIgnoreCase));

        private static List<string> ParseJsonArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }
}
