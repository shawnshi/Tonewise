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
    private ManifestFileStamp ReadManifestFileStamp()
    {
        string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
        var dbInfo = new FileInfo(dbPath);
        if (!dbInfo.Exists)
        {
            return new ManifestFileStamp(false, 0, 0, 0, 0);
        }

        var walInfo = new FileInfo(dbPath + "-wal");
        return new ManifestFileStamp(
            Exists: true,
            DbTicks: dbInfo.LastWriteTimeUtc.Ticks,
            DbLength: dbInfo.Length,
            WalTicks: walInfo.Exists ? walInfo.LastWriteTimeUtc.Ticks : 0,
            WalLength: walInfo.Exists ? walInfo.Length : 0);
    }

    private static Dictionary<string, ManifestTrackRow> BuildRowsByUid(IReadOnlyList<ManifestTrackRow> rows)
    {
        var dict = new Dictionary<string, ManifestTrackRow>(rows.Count * 2, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            dict[r.TrackUid] = r;
            if (!string.IsNullOrWhiteSpace(r.AudioFrameBlake3Hex))
            {
                dict.TryAdd(r.AudioFrameBlake3Hex, r);
            }
        }
        return dict;
    }

    private (IReadOnlyList<ManifestTrackRow> Rows, Dictionary<string, ManifestTrackRow> RowsByUid, PlaylistTrackMatcher.LibraryIndex? LibraryIndex) GetCachedManifestRowsAndIndex(bool requirePlaylistIndex)
    {
        ManifestFileStamp stamp = ReadManifestFileStamp();
        List<ManifestTrackRow>? rows;
        Dictionary<string, ManifestTrackRow>? rowsByUid;
        PlaylistTrackMatcher.LibraryIndex? libIndex;

        lock (_resolveCacheLock)
        {
            if (_cachedManifestRows is not null && _cachedManifestStamp.Equals(stamp))
            {
                rows = _cachedManifestRows;
                rowsByUid = _cachedRowsByUid;
                libIndex = _cachedLibraryIndex;
            }
            else
            {
                rows = null;
                rowsByUid = null;
                libIndex = null;
            }
        }

        if (rows is null || rowsByUid is null)
        {
            ManifestFileStamp beforeStamp = ReadManifestFileStamp();
            var loadedRows = LoadManifestRows();
            var builtByUid = BuildRowsByUid(loadedRows);
            ManifestFileStamp afterStamp = ReadManifestFileStamp();
            if (afterStamp.Equals(beforeStamp))
            {
                lock (_resolveCacheLock)
                {
                    if (_cachedManifestRows is null || _cachedRowsByUid is null || !_cachedManifestStamp.Equals(afterStamp))
                    {
                        _cachedManifestStamp = afterStamp;
                        _cachedManifestRows = loadedRows;
                        _cachedRowsByUid = builtByUid;
                        _cachedLibraryIndex = null;
                        _cachedPlaylists.Clear();
                        _cachedResolvedOutboxes.Clear();
                    }
                    rows = _cachedManifestRows;
                    rowsByUid = _cachedRowsByUid;
                    libIndex = _cachedLibraryIndex;
                    stamp = _cachedManifestStamp;
                }
            }
            else
            {
                rows = loadedRows;
                rowsByUid = builtByUid;
                libIndex = null;
                stamp = afterStamp;
            }
        }

        if (requirePlaylistIndex && libIndex is null)
        {
            var libRefs = new List<LocalLibraryTrackRef>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                libRefs.Add(r.ToLibraryTrackRef(BuildFullPath(r.RelPath)));
            }
            var builtIndex = PlaylistTrackMatcher.BuildIndex(libRefs);
            lock (_resolveCacheLock)
            {
                if (_cachedManifestStamp.Equals(stamp) && ReferenceEquals(_cachedManifestRows, rows))
                {
                    _cachedLibraryIndex ??= builtIndex;
                    libIndex = _cachedLibraryIndex;
                }
                else
                {
                    libIndex = builtIndex;
                }
            }
        }

        return (rows, rowsByUid, libIndex);
    }

    private PlaylistDocument LoadPlaylistCached(string filePath, PlaylistTrackMatcher.LibraryIndex libIndex)
    {
        var fi = new FileInfo(filePath);
        var fileStamp = fi.Exists
            ? new PlaylistFileStamp(fi.LastWriteTimeUtc.Ticks, fi.Length)
            : new PlaylistFileStamp(0, -1);

        lock (_resolveCacheLock)
        {
            if (ReferenceEquals(_cachedLibraryIndex, libIndex)
                && _cachedPlaylists.TryGetValue(filePath, out var cached)
                && cached.Stamp.Equals(fileStamp))
            {
                return cached.Doc;
            }
        }

        var doc = PlaylistStore.LoadPlaylist(filePath, libIndex, _musicRoot);
        var afterFi = new FileInfo(filePath);
        var afterStamp = afterFi.Exists
            ? new PlaylistFileStamp(afterFi.LastWriteTimeUtc.Ticks, afterFi.Length)
            : new PlaylistFileStamp(0, -1);

        if (fileStamp.Length >= 0 && afterStamp.Equals(fileStamp))
        {
            lock (_resolveCacheLock)
            {
                if (ReferenceEquals(_cachedLibraryIndex, libIndex))
                {
                    _cachedPlaylists[filePath] = (fileStamp, doc);
                }
            }
        }

        return doc;
    }

    private static List<(string FilePath, string Name, string BaseName)> ListPlaylistFileHeaders(string musicRoot)
    {
        string dir = PlaylistStore.GetPlaylistsDirectory(musicRoot);
        if (!Directory.Exists(dir)) return [];

        var files = Directory.EnumerateFiles(dir, "*.m3u8")
            .Concat(Directory.EnumerateFiles(dir, "*.m3u"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var headers = new List<(string FilePath, string Name, string BaseName)>(files.Count);
        foreach (string file in files)
        {
            string baseName = Path.GetFileNameWithoutExtension(file);
            string name = baseName.Trim();
            try
            {
                using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? rawLine;
                while ((rawLine = reader.ReadLine()) is not null)
                {
                    string line = rawLine.Trim().TrimStart('\uFEFF');
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase))
                    {
                        string declared = line["#PLAYLIST:".Length..].Trim();
                        if (!string.IsNullOrWhiteSpace(declared))
                        {
                            name = declared;
                        }
                        break;
                    }
                    if (!line.StartsWith('#'))
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Fall back to base filename if header cannot be inspected
            }
            headers.Add((file, name, baseName));
        }
        return headers;
    }

    private void PreloadPlaylistsParallel(IEnumerable<string> filePaths, PlaylistTrackMatcher.LibraryIndex libIndex)
    {
        var unique = filePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unique.Count == 0) return;

        var stamps = new List<(string Path, PlaylistFileStamp Stamp)>(unique.Count);
        foreach (string path in unique)
        {
            var fi = new FileInfo(path);
            var st = fi.Exists
                ? new PlaylistFileStamp(fi.LastWriteTimeUtc.Ticks, fi.Length)
                : new PlaylistFileStamp(0, -1);
            stamps.Add((path, st));
        }

        var uncached = new List<string>(unique.Count);
        lock (_resolveCacheLock)
        {
            bool indexMatches = ReferenceEquals(_cachedLibraryIndex, libIndex);
            foreach (var (path, st) in stamps)
            {
                if (!indexMatches
                    || !_cachedPlaylists.TryGetValue(path, out var cached)
                    || !cached.Stamp.Equals(st))
                {
                    uncached.Add(path);
                }
            }
        }

        if (uncached.Count == 0) return;
        if (uncached.Count == 1)
        {
            try { LoadPlaylistCached(uncached[0], libIndex); } catch { }
            return;
        }

        Parallel.ForEach(uncached, path =>
        {
            try { LoadPlaylistCached(path, libIndex); } catch { }
        });
    }

    public void WarmResolveCache()
    {
        try
        {
            ResolveOutbox();
        }
        catch
        {
            // Non-fatal background cache warmup guard
        }
    }

    public OutboxSummary GetOutboxSummary(string? deviceId = null)
    {
        var resolved = ResolveOutbox(deviceId);
        return new OutboxSummary(
            resolved.Items.Count,
            resolved.Tracks.Count,
            resolved.TotalBytes,
            resolved.DuplicateTrackCount,
            resolved.SyncPolicy?.MissingSourceCount ?? 0);
    }

    public ResolvedOutboxPayload ResolveOutbox(
        string? deviceId = null,
        string? hostName = null,
        bool pruneConfirmedForSession = false)
    {
        List<SyncOutboxItem> relevantItems;
        long outboxVer;
        long policyVer;
        long outboxFileTicks;
        long outboxFileLength;
        SyncPolicyConfig policyConfig;
        lock (_gate)
        {
            outboxVer = Interlocked.Read(ref _outboxMutationVersion);
            policyVer = Interlocked.Read(ref _syncPolicyVersion);
            var obFi = new FileInfo(_outboxPath);
            outboxFileTicks = obFi.Exists ? obFi.LastWriteTimeUtc.Ticks : 0L;
            outboxFileLength = obFi.Exists ? obFi.Length : -1L;
            relevantItems = FilterOutboxForDevice(LoadOutboxUnlocked(), deviceId);
        }
        policyConfig = GetSyncPolicy();

        string effectiveHost = string.IsNullOrWhiteSpace(hostName) ? Environment.MachineName : hostName;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (relevantItems.Count == 0)
        {
            var emptyPolicy = new ResolvedSyncPolicy(
                MirrorPruneEnabled: policyConfig.MirrorPruneEnabled,
                PruneScope: policyConfig.PruneScope,
                TargetRoot: policyConfig.TargetRoot,
                OutboxAuthoritative: false,
                MissingSourceCount: 0,
                ExpectedPlaylistNames: [],
                LargePruneConfirmThresholdCount: policyConfig.LargePruneConfirmThresholdCount,
                LargePruneConfirmThresholdRatio: policyConfig.LargePruneConfirmThresholdRatio,
                PruneConfirmedForSession: pruneConfirmedForSession);
            return new ResolvedOutboxPayload(effectiveHost, nowMs, relevantItems, [], [], 0, 0, emptyPolicy);
        }

        bool hasPlaylistItems = relevantItems.Any(i => string.Equals(i.Kind, "playlist", StringComparison.OrdinalIgnoreCase));
        ManifestFileStamp manifestStamp = ReadManifestFileStamp();
        List<(string FilePath, string Name, string BaseName)> playlistHeaders = hasPlaylistItems
            ? ListPlaylistFileHeaders(_musicRoot)
            : [];

        var resolvedItemPlaylists = new Dictionary<string, (string FilePath, string Name)>(StringComparer.OrdinalIgnoreCase);
        string playlistStampsFingerprint = "";
        if (hasPlaylistItems)
        {
            var sbFp = new StringBuilder();
            foreach (var item in relevantItems)
            {
                if (!string.Equals(item.Kind, "playlist", StringComparison.OrdinalIgnoreCase)) continue;
                var plHeader = playlistHeaders.FirstOrDefault(p =>
                    string.Equals(p.Name, item.TargetKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.BaseName, item.TargetKey, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(plHeader.FilePath)) continue;

                resolvedItemPlaylists[item.Id] = (plHeader.FilePath, plHeader.Name);
                var pfi = new FileInfo(plHeader.FilePath);
                sbFp.Append(plHeader.FilePath).Append('|')
                    .Append(pfi.Exists ? pfi.LastWriteTimeUtc.Ticks : 0L).Append('|')
                    .Append(pfi.Exists ? pfi.Length : -1L).Append('\n');
            }
            playlistStampsFingerprint = sbFp.ToString();
        }

        string cacheKey = $"{deviceId ?? ""}\u001f{effectiveHost}\u001f{(pruneConfirmedForSession ? '1' : '0')}";
        lock (_resolveCacheLock)
        {
            if (_cachedResolvedOutboxes.TryGetValue(cacheKey, out var cachedOutbox)
                && cachedOutbox.ManifestStamp.Equals(manifestStamp)
                && cachedOutbox.OutboxFileTicks == outboxFileTicks
                && cachedOutbox.OutboxFileLength == outboxFileLength
                && cachedOutbox.OutboxMutationVersion == outboxVer
                && cachedOutbox.SyncPolicyVersion == policyVer
                && string.Equals(cachedOutbox.PlaylistStampsFingerprint, playlistStampsFingerprint, StringComparison.Ordinal)
                && (DateTime.UtcNow - cachedOutbox.ComputedAtUtc) < ResolvedOutboxCacheTtl)
            {
                return cachedOutbox.Payload;
            }
        }

        var (rows, rowsByUid, libIndex) = GetCachedManifestRowsAndIndex(hasPlaylistItems);
        if (hasPlaylistItems && resolvedItemPlaylists.Count > 0)
        {
            PreloadPlaylistsParallel(resolvedItemPlaylists.Values.Select(v => v.FilePath), libIndex!);
        }

        var physicalInfoCache = new Dictionary<string, (bool Valid, string FullPath, long Size, long Mtime)>(StringComparer.OrdinalIgnoreCase);
        bool TryGetCachedPhysicalFileInfo(string relPath, out string resolvedFullPath, out long realSize, out long realMtime)
        {
            if (!physicalInfoCache.TryGetValue(relPath, out var info))
            {
                bool ok = TryGetValidPhysicalFileInfo(relPath, out string fp, out long sz, out long mt);
                info = (ok, fp, sz, mt);
                physicalInfoCache[relPath] = info;
            }
            resolvedFullPath = info.FullPath;
            realSize = info.Size;
            realMtime = info.Mtime;
            return info.Valid;
        }

        var outboxIdsByUid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var canonicalUidByTargetPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var canonicalUidByPhysicalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resolvedTrackOrder = new List<ManifestTrackRow>();
        var resolvedPlaylists = new List<ResolvedSyncPlaylist>();
        var coverCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int duplicateTrackCount = 0;
        int missingSourceCount = 0;

        bool AttachTrack(ManifestTrackRow row, string outboxId)
        {
            if (!TryGetCachedPhysicalFileInfo(row.RelPath, out string resolvedFullPath, out long realSize, out long realMtime))
                return false;

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
            return true;
        }

        foreach (var item in relevantItems)
        {
            switch (item.Kind.ToLowerInvariant())
            {
                case "playlist":
                {
                    if (!resolvedItemPlaylists.TryGetValue(item.Id, out var plMeta))
                    {
                        missingSourceCount++;
                        break;
                    }

                    var doc = LoadPlaylistCached(plMeta.FilePath, libIndex!);
                    var plTrackUids = new List<string>();
                    var plRelPaths = new List<string>();
                    foreach (var entry in doc.Entries)
                    {
                        if (!entry.IsLocalMatched || string.IsNullOrWhiteSpace(entry.MatchedTrackUid))
                            continue;
                        if (!rowsByUid.TryGetValue(entry.MatchedTrackUid, out var row))
                        {
                            missingSourceCount++;
                            continue;
                        }
                        if (!AttachTrack(row, item.Id))
                        {
                            missingSourceCount++;
                            continue;
                        }

                        plTrackUids.Add(row.TrackUid);
                        plRelPaths.Add(BuildTargetRelPath(row));
                    }
                    resolvedPlaylists.Add(new ResolvedSyncPlaylist(item.Id, plMeta.Name, plRelPaths, plTrackUids));
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
                        if (match && !AttachTrack(row, item.Id))
                        {
                            missingSourceCount++;
                        }
                    }
                    break;
                }
                case "artist":
                {
                    string targetArtist = item.TargetKey.Trim();
                    foreach (var row in rows)
                    {
                        if (row.MatchesArtist(targetArtist) && !AttachTrack(row, item.Id))
                        {
                            missingSourceCount++;
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
                            if (!AttachTrack(row, item.Id))
                            {
                                missingSourceCount++;
                            }
                        }
                        else
                        {
                            missingSourceCount++;
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

        bool manifestExists = File.Exists(Path.Combine(_tonewiseDir, "manifest.db"));
        bool outboxAuthoritative = manifestExists && relevantItems.Count > 0 && resolvedTracks.Count > 0 && missingSourceCount == 0;
        var expectedPlaylistNames = resolvedPlaylists
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolvedPolicy = new ResolvedSyncPolicy(
            MirrorPruneEnabled: policyConfig.MirrorPruneEnabled,
            PruneScope: policyConfig.PruneScope,
            TargetRoot: policyConfig.TargetRoot,
            OutboxAuthoritative: outboxAuthoritative,
            MissingSourceCount: missingSourceCount,
            ExpectedPlaylistNames: expectedPlaylistNames,
            LargePruneConfirmThresholdCount: policyConfig.LargePruneConfirmThresholdCount,
            LargePruneConfirmThresholdRatio: policyConfig.LargePruneConfirmThresholdRatio,
            PruneConfirmedForSession: pruneConfirmedForSession);

        var payload = new ResolvedOutboxPayload(effectiveHost, nowMs, relevantItems, resolvedTracks, resolvedPlaylists, totalBytes, duplicateTrackCount, resolvedPolicy);
        if (Interlocked.Read(ref _outboxMutationVersion) == outboxVer
            && Interlocked.Read(ref _syncPolicyVersion) == policyVer
            && ReadManifestFileStamp().Equals(manifestStamp))
        {
            lock (_resolveCacheLock)
            {
                _cachedResolvedOutboxes[cacheKey] = new CachedResolvedOutbox(
                    manifestStamp,
                    outboxFileTicks,
                    outboxFileLength,
                    outboxVer,
                    policyVer,
                    playlistStampsFingerprint,
                    DateTime.UtcNow,
                    payload);
            }
        }
        return payload;
    }

    public IReadOnlyList<CatalogEntry> GetCatalog(string kind)
    {
        string normalizedKind = kind.Trim().ToLowerInvariant();
        var (rows, byUid, libIndex) = GetCachedManifestRowsAndIndex(requirePlaylistIndex: normalizedKind == "playlists");
        switch (normalizedKind)
        {
            case "playlists":
            {
                string dir = PlaylistStore.GetPlaylistsDirectory(_musicRoot);
                if (!Directory.Exists(dir)) return [];

                var files = Directory.EnumerateFiles(dir, "*.m3u8")
                    .Concat(Directory.EnumerateFiles(dir, "*.m3u"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                PreloadPlaylistsParallel(files, libIndex!);

                var result = new List<CatalogEntry>(files.Count);
                foreach (string file in files)
                {
                    try
                    {
                        var doc = LoadPlaylistCached(file, libIndex!);
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

        string cleanUid = trackUid.Trim();
        var (_, rowsByUid, _) = GetCachedManifestRowsAndIndex(requirePlaylistIndex: false);
        if (!rowsByUid.TryGetValue(cleanUid, out ManifestTrackRow? row) ||
            string.IsNullOrWhiteSpace(row.RelPath))
        {
            return false;
        }

        if (!TryGetValidPhysicalFileInfo(row.RelPath, out string audioFull, out _, out _))
            return false;

        relPath = row.RelPath;
        fullPath = audioFull;
        return true;
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

    public sealed record LanInterfaceCandidate(
        string Name,
        string Description,
        NetworkInterfaceType InterfaceType,
        bool IsUp,
        bool HasIpv4Gateway,
        IPAddress Address);

    private static readonly string[] VirtualAdapterTokens =
    [
        "vethernet", "vmware", "virtualbox", "wsl", "tailscale", "zerotier",
        "hyper-v", "meta tunnel", "wintun", "wireguard", "clash", "mihomo",
        "sing-box", "openvpn", "docker", "bluetooth", "nexgen", "tunnel",
        "tap-", "utun", "npcap", "vpn"
    ];

    public static IReadOnlyList<string> GetLocalLanUrls(int port)
    {
        if (port <= 0) return [];
        return ResolveAllLanIpv4Addresses()
            .Select(ip => $"http://{ip}:{port}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IPAddress? ResolvePreferredLanIpv4() =>
        ResolveAllLanIpv4Addresses().FirstOrDefault();

    public static IReadOnlyList<IPAddress> ResolveAllLanIpv4Addresses()
    {
        var raw = new List<LanInterfaceCandidate>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                bool isUp = ni.OperationalStatus == OperationalStatus.Up;
                var ipProps = ni.GetIPProperties();
                bool hasGateway = ipProps.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                foreach (var uni in ipProps.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    raw.Add(new LanInterfaceCandidate(
                        ni.Name ?? "",
                        ni.Description ?? "",
                        ni.NetworkInterfaceType,
                        isUp,
                        hasGateway,
                        uni.Address));
                }
            }
        }
        catch
        {
            // Ignore interface enumeration failures
        }

        return FilterAndRankLanIpv4Candidates(raw);
    }

    public static IReadOnlyList<IPAddress> FilterAndRankLanIpv4Candidates(IEnumerable<LanInterfaceCandidate> candidates)
    {
        var scored = new List<(int Score, int NicKindRank, IPAddress Address)>();
        foreach (var c in candidates)
        {
            if (!c.IsUp || c.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            // Exclude Loopback, Tunnel, and IF_TYPE_PROP_VIRTUAL (53 - WinTun / Clash / Meta Tunnel)
            if (c.InterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel || (int)c.InterfaceType == 53)
                continue;

            string desc = (c.Name + " " + c.Description).ToLowerInvariant();
            bool isVirtual = false;
            foreach (string token in VirtualAdapterTokens)
            {
                if (desc.Contains(token, StringComparison.Ordinal))
                {
                    isVirtual = true;
                    break;
                }
            }
            if (isVirtual) continue;

            byte[] b = c.Address.GetAddressBytes();
            if (b.Length != 4) continue;
            // Exclude loopback (127/8), link-local (169.254/16), unspecified (0/8), benchmark/fake-IP TUN (198.18/15), multicast+ (>=224)
            if (b[0] == 127 || b[0] == 0 || b[0] >= 224) continue;
            if (b[0] == 169 && b[1] == 254) continue;
            if (b[0] == 198 && b[1] is 18 or 19) continue;

            int netRank = (b[0], b[1]) switch
            {
                (192, 168) => 1,
                (10, _) => 2,
                (172, >= 16 and <= 31) => 3,
                _ => 0,
            };
            if (netRank == 0) continue; // Only advertise RFC1918 private LAN IPv4 addresses

            int gatewayPenalty = c.HasIpv4Gateway ? 0 : 10;
            int ifacePenalty = c.InterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 0 : 20;
            int nicKindRank = c.InterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet => 1,
                _ => 2,
            };
            scored.Add((gatewayPenalty + ifacePenalty + netRank, nicKindRank, c.Address));
        }

        var result = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in scored.OrderBy(s => s.Score).ThenBy(s => s.NicKindRank))
        {
            if (seen.Add(item.Address.ToString()))
            {
                result.Add(item.Address);
            }
        }
        return result;
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

            var fi = new FileInfo(LongPathHelper.ToExtendedIfLong(resolved));
            if (!fi.Exists)
                return false;

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
                MovementName,
                AlbumArtist,
                Genre
            );

        public IReadOnlyList<string> AllArtists
        {
            get
            {
                if (_allArtists is not null) return _allArtists;
                var list = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddSplit(string? raw, bool composerMode = false)
                {
                    if (string.IsNullOrWhiteSpace(raw)) return;
                    var split = composerMode
                        ? ArtistEntitySplitter.SplitComposers(raw)
                        : ArtistEntitySplitter.SplitPerformers(raw);
                    foreach (string s in split)
                    {
                        if (seen.Add(s)) list.Add(s);
                    }
                }

                AddSplit(AlbumArtist);
                foreach (string item in ParseJsonArray(ArtistsJson)) AddSplit(item);
                foreach (string item in ParseJsonArray(ComposersJson)) AddSplit(item, composerMode: true);
                foreach (string item in ParseJsonArray(ConductorsJson)) AddSplit(item);
                _allArtists = list;
                return _allArtists;
            }
        }

        public bool MatchesArtist(string target)
        {
            string normTarget = ArtistEntitySplitter.NormalizePerformerName(target);
            if (string.IsNullOrEmpty(normTarget)) return false;
            return AllArtists.Any(a => string.Equals(a, normTarget, StringComparison.OrdinalIgnoreCase));
        }

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
