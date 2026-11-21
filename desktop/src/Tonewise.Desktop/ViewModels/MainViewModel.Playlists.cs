using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tonewise.Audio;
using Tonewise.Desktop.Models;
using Tonewise.Desktop.Services;
using Tonewise.Interop;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Export;
using Tonewise.SyncHost.Ledger;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public IReadOnlyList<LocalLibraryTrackRef> GetLibraryRefs() =>
        _allTracks.Select(t => new LocalLibraryTrackRef(
            t.TrackUid,
            t.RelPath,
            t.FullPath,
            t.Title,
            t.ArtistsDisplay,
            t.Album,
            t.DurationMs,
            t.SampleRateHz,
            t.ComposersDisplay,
            t.ConductorsDisplay,
            t.WorkName,
            t.MovementName
        )).ToList();

    public IReadOnlyList<TrackItem> SearchLibraryTracks(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _allTracks.Take(limit).ToList();
        }
        string q = query.Trim();
        return _allTracks
            .Where(t =>
                t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.ArtistsDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Album.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    public PlaylistEntry ToPlaylistEntry(TrackItem track) =>
        string.IsNullOrWhiteSpace(track.RelPath)
            ? new PlaylistEntry(null, track.Title, track.ArtistsDisplay, track.Album, track.DurationMs, false, null)
            : new PlaylistEntry(track.RelPath, track.Title, track.ArtistsDisplay, track.Album, track.DurationMs, true, track.TrackUid);

    public PlaylistDocument LoadPlaylistDocument(PlaylistSummary summary) =>
        PlaylistStore.LoadPlaylist(summary.FilePath, GetLibraryRefs(), _store.MusicRoot);

    public void RefreshPlaylists()
    {
        string? prevPath = SelectedPlaylist?.FilePath;
        Playlists.Clear();
        foreach (var p in PlaylistStore.ListPlaylists(_store.MusicRoot, GetLibraryRefs()))
        {
            Playlists.Add(p);
        }
        if (!string.IsNullOrWhiteSpace(prevPath))
        {
            SelectedPlaylist = Playlists.FirstOrDefault(p =>
                string.Equals(p.FilePath, prevPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void SelectPlaylist(PlaylistSummary? playlist)
    {
        if (playlist is null)
        {
            NavigateToPrimary("library", "tracks");
            return;
        }

        SelectedPlaylist = playlist;
        if (PrimaryNavId != "playlists")
        {
            PrimaryNavId = "playlists";
            SecondarySubTab = "workspace";
            UpdateNavigationState();
        }
        LoadPlaylistTracksIntoView(playlist);
    }

    private void LoadPlaylistTracksIntoView(PlaylistSummary playlist)
    {
        var doc = PlaylistStore.LoadPlaylist(playlist.FilePath, GetLibraryRefs(), _store.MusicRoot);
        var byUid = _allTracks.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);

        var loaded = new List<TrackItem>(doc.Entries.Count);
        for (int i = 0; i < doc.Entries.Count; i++)
        {
            var entry = doc.Entries[i];
            if (entry.IsLocalMatched && entry.MatchedTrackUid is not null && byUid.TryGetValue(entry.MatchedTrackUid, out var localTrack))
            {
                loaded.Add(localTrack);
            }
            else
            {
                loaded.Add(new TrackItem(
                    TrackUid: $"unmatched:{i}",
                    AudioFrameBlake3Hex: "",
                    RelPath: "",
                    FullPath: "",
                    Title: entry.Title,
                    Album: entry.Album ?? "",
                    ArtistsDisplay: entry.Artist,
                    ComposersDisplay: "",
                    ConductorsDisplay: "",
                    WorkDisplay: "",
                    Genre: "",
                    Year: null,
                    SampleRateHz: 0,
                    DurationMs: entry.DurationMs,
                    ReplayGainTrackDb: null,
                    DjScene: [],
                    DjVocals: null,
                    DjEnergy: null,
                    TempoBpm: null,
                    TempoBucket: null,
                    Moods: [],
                    Rating: 0,
                    LastPlayedTsMs: 0
                ));
            }
        }
        FilteredTracks.ReplaceAll(loaded);

        SelectedPlaylist = playlist;
        IsViewingPlaylist = true;
        RefreshBreadcrumbs(doc.Name);
        int localCount = doc.Entries.Count(e => e.IsLocalMatched);
        CurrentViewHeader = $"歌单: {doc.Name} ({doc.Entries.Count} 首 · {localCount} 本地)";
        ActiveFilterPillsSummary = $"歌单视图 · {doc.Name} · 本地匹配 {localCount} / {doc.Entries.Count}";
        StatusMessage = $"正在查看歌单 '{doc.Name}'：{doc.Entries.Count} 首（本地匹配 {localCount} 首，当前后台播放队列不受影响）。";
    }

    [RelayCommand]
    public void SaveCurrentPlaylistOrder()
    {
        if (!IsViewingPlaylist || SelectedPlaylist is null)
        {
            StatusMessage = "请先在歌单工作区选择一个歌单再保存拖拽排序。";
            return;
        }
        var updatedEntries = FilteredTracks.Select(ToPlaylistEntry).ToList();
        SaveEditedPlaylist(SelectedPlaylist, SelectedPlaylist.Name, updatedEntries);
        StatusMessage = $"已保存歌单 '{SelectedPlaylist.Name}' 的拖拽排序 ({updatedEntries.Count} 首)。";
    }

    [RelayCommand]
    public void ShowAllLibraryTracks() => SelectPlaylist(null);

    public void CreatePlaylist(string name, bool seedWithCurrentFilteredTracks)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Playlist name is required.", nameof(name));

        var entries = seedWithCurrentFilteredTracks
            ? FilteredTracks.Select(ToPlaylistEntry).ToList()
            : [];

        string savedPath = PlaylistStore.SavePlaylist(_store.MusicRoot, name.Trim(), entries);
        RefreshPlaylists();
        var created = Playlists.FirstOrDefault(p =>
            string.Equals(Path.GetFullPath(p.FilePath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase));
        if (created is not null)
        {
            SelectPlaylist(created);
        }
        StatusMessage = $"已创建歌单「{name.Trim()}」，共 {entries.Count} 首。";
    }

    public void SaveEditedPlaylist(PlaylistSummary target, string newName, IReadOnlyList<PlaylistEntry> updatedEntries)
    {
        string savedPath = PlaylistStore.SavePlaylist(_store.MusicRoot, newName, updatedEntries, target.FilePath);
        RefreshPlaylists();
        var updated = Playlists.FirstOrDefault(p =>
            string.Equals(Path.GetFullPath(p.FilePath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase));
        if (updated is not null)
        {
            SelectPlaylist(updated);
        }
        StatusMessage = $"已保存歌单「{newName}」（{updatedEntries.Count} 首）。";
    }

    public void DeleteSelectedPlaylist() => DeletePlaylist(SelectedPlaylist);

    public void DeletePlaylist(PlaylistSummary? target = null)
    {
        var actual = target ?? SelectedPlaylist;
        if (actual is null) return;

        int deletedIndex = -1;
        for (int i = 0; i < Playlists.Count; i++)
        {
            if (string.Equals(Playlists[i].FilePath, actual.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                deletedIndex = i;
                break;
            }
        }

        bool deletedWasSelected = SelectedPlaylist is not null
            && string.Equals(SelectedPlaylist.FilePath, actual.FilePath, StringComparison.OrdinalIgnoreCase);

        string deletedName = actual.Name;
        PlaylistStore.DeletePlaylist(actual.FilePath);
        RefreshPlaylists();

        if (deletedWasSelected || SelectedPlaylist is null)
        {
            if (Playlists.Count > 0)
            {
                int nextIdx = Math.Clamp(deletedIndex >= 0 ? deletedIndex : 0, 0, Playlists.Count - 1);
                var next = Playlists[nextIdx];
                SelectedPlaylist = next;
                LoadPlaylistTracksIntoView(next);
            }
            else
            {
                SelectedPlaylist = null;
                IsViewingPlaylist = false;
                FilteredTracks.ReplaceAll([]);
                CurrentViewHeader = "歌单工作区 (暂无 .m3u8 歌单，可新建或在线导入)";
                ActiveFilterPillsSummary = "Playlists · 支持 Spotify / Apple Music / Billboard / Kworb / TXT 导入";
                RefreshBreadcrumbs(null);
            }
        }

        StatusMessage = $"已删除歌单「{deletedName}」。";
    }

    public void AddTrackToCurrentOrNamedPlaylist(TrackItem track, string? playlistName = null)
    {
        var target = SelectedPlaylist ?? Playlists.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(playlistName) && string.Equals(p.Name, playlistName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            string name = string.IsNullOrWhiteSpace(playlistName) ? "Favorites" : playlistName.Trim();
            string savedPath = PlaylistStore.SavePlaylist(_store.MusicRoot, name, [ToPlaylistEntry(track)]);
            RefreshPlaylists();
            StatusMessage = $"已创建歌单「{name}」并加入「{track.Title}」。";
            var created = Playlists.FirstOrDefault(p => string.Equals(p.FilePath, savedPath, StringComparison.OrdinalIgnoreCase));
            if (created is not null) SelectPlaylist(created);
            return;
        }

        var doc = LoadPlaylistDocument(target);
        var updatedEntries = doc.Entries.ToList();
        updatedEntries.Add(ToPlaylistEntry(track));
        SaveEditedPlaylist(target, doc.Name, updatedEntries);
    }

    public async Task ImportPlaylistFromUrlAsync(
        string url,
        string? customName = null,
        int maxTracks = 200,
        CancellationToken ct = default)
    {
        StatusMessage = $"正在导入歌单：{url}";
        var parsed = await PlaylistImporter.FetchAndParseUrlAsync(url, maxTracks, ct: ct);
        string finalName = string.IsNullOrWhiteSpace(customName) ? parsed.Title : customName.Trim();
        var entries = PlaylistTrackMatcher.ResolveSeeds(parsed.Tracks, GetLibraryRefs(), _store.MusicRoot);
        string savedPath = PlaylistStore.SavePlaylist(_store.MusicRoot, finalName, entries);
        RefreshPlaylists();
        var imported = Playlists.FirstOrDefault(p =>
            string.Equals(Path.GetFullPath(p.FilePath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase));
        if (imported is not null)
        {
            SelectPlaylist(imported);
        }
        StatusMessage = $"已导入歌单「{finalName}」（{parsed.SourceKind}）：共 {entries.Count} 首，本地匹配 {entries.Count(e => e.IsLocalMatched)} 首。";
    }

    public void ImportPlaylistFromText(string textContent, string playlistName, int maxTracks = 5000)
    {
        var parsed = PlaylistImporter.ParseTextPlaylist(textContent, playlistName, maxTracks);
        string finalName = string.IsNullOrWhiteSpace(playlistName) ? parsed.Title : playlistName.Trim();
        var entries = PlaylistTrackMatcher.ResolveSeeds(parsed.Tracks, GetLibraryRefs(), _store.MusicRoot);
        string savedPath = PlaylistStore.SavePlaylist(_store.MusicRoot, finalName, entries);
        RefreshPlaylists();
        var imported = Playlists.FirstOrDefault(p =>
            string.Equals(Path.GetFullPath(p.FilePath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase));
        if (imported is not null)
        {
            SelectPlaylist(imported);
        }
        StatusMessage = $"已导入歌单「{finalName}」（{parsed.SourceKind}）：共 {entries.Count} 首，本地匹配 {entries.Count(e => e.IsLocalMatched)} 首。";
    }

    public PlaylistBatchRematchSummary RematchPlaylistsAndPersist(IReadOnlyList<PlaylistSummary>? targets = null)
    {
        if (_allTracks.Count == 0)
        {
            StatusMessage = "当前曲库为空，请先打开曲库后再重匹歌单。";
            return new PlaylistBatchRematchSummary(0, 0, 0, 0, 0, 0);
        }

        var index = PlaylistTrackMatcher.BuildIndex(GetLibraryRefs());
        PlaylistBatchRematchSummary summary;

        if (targets is { Count: > 0 })
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0;
            int updated = 0;
            int totalTracks = 0;
            int totalMatched = 0;
            int newlyMatched = 0;
            int pathRepaired = 0;

            foreach (var target in targets)
            {
                if (string.IsNullOrWhiteSpace(target.FilePath) || !File.Exists(target.FilePath)) continue;
                string fullPath = Path.GetFullPath(target.FilePath);
                if (!seenPaths.Add(fullPath)) continue;

                try
                {
                    var res = PlaylistStore.RematchPlaylist(target.FilePath, index, _store.MusicRoot, persistIfChanged: true);
                    scanned++;
                    if (res.WasSaved) updated++;
                    totalTracks += res.TotalCount;
                    totalMatched += res.MatchedCount;
                    newlyMatched += res.NewlyMatchedCount;
                    pathRepaired += res.PathRepairedCount;
                }
                catch
                {
                    // Skip unreadable/corrupt playlist file
                }
            }

            summary = new PlaylistBatchRematchSummary(
                PlaylistsScanned: scanned,
                PlaylistsUpdated: updated,
                TotalTracks: totalTracks,
                TotalMatched: totalMatched,
                NewlyMatchedCount: newlyMatched,
                PathRepairedCount: pathRepaired);
        }
        else
        {
            summary = PlaylistStore.RematchAllPlaylists(_store.MusicRoot, index, persistIfChanged: true);
        }

        RefreshPlaylists();
        if (IsViewingPlaylist && SelectedPlaylist is not null && PrimaryNavId == "playlists")
        {
            LoadPlaylistTracksIntoView(SelectedPlaylist);
        }

        if (summary.PlaylistsScanned == 0)
        {
            StatusMessage = "当前曲库下暂无可重匹的 .m3u8 歌单。";
        }
        else if (summary.PlaylistsUpdated > 0)
        {
            StatusMessage =
                $"重匹完成：检查 {summary.PlaylistsScanned} 个歌单，写回更新 {summary.PlaylistsUpdated} 个（新匹配 +{summary.NewlyMatchedCount} 首，修复路径 {summary.PathRepairedCount} 首，当前共匹配 {summary.TotalMatched} / {summary.TotalTracks} 首）。";
            Notify(StatusMessage, "success");
        }
        else
        {
            StatusMessage =
                $"重匹完成：检查 {summary.PlaylistsScanned} 个歌单，均已是最新匹配状态（共匹配 {summary.TotalMatched} / {summary.TotalTracks} 首）。";
            Notify(StatusMessage, "info");
        }

        return summary;
    }

}
