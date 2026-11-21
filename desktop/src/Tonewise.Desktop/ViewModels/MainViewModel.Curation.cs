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
    [RelayCommand]
    private async Task RunFillMissingScanAsync()
    {
        if (_allTracks.Count == 0)
        {
            StatusMessage = "请先打开曲库，再扫描规则缺口。";
            return;
        }
        BeginBusy("正在扫描曲库元数据缺口 (Fill-Missing)...");
        try
        {
            var snapshot = _allTracks.ToList();
            int added = await Task.Run(() => _store.ScanMissingMetadataAndEnqueue(snapshot));
            RefreshStagingProposals();
            Notify($"扫描完成：新增 {added} 条待审提案。", "success");
        }
        catch (Exception ex)
        {
            Notify($"扫描规则缺口失败: {ex.Message}", "error");
        }
        finally
        {
            EndBusy();
        }
    }

    private readonly List<string> _lastCommitBatchIds = [];

    public (int ApprovedCount, int PendingCount) GetStagingCounts() =>
        _store.GetStagingDiffStore()?.GetPendingProposalCounts() ?? (0, 0);

    public int GetHighConfidencePendingCount() =>
        _store.GetStagingDiffStore()?.CountPendingAtOrAbove(0.80) ?? 0;

    [RelayCommand]
    private async Task CommitStagingBatchAsync()
    {
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null)
        {
            StatusMessage = "请先加载含 .tonewise/manifest.db 的音乐目录。";
            return;
        }
        if (IsBusy)
        {
            Notify("已有后台任务在执行，请等待完成或取消后再提交。", "warning");
            return;
        }

        var (approvedCount, pendingCount) = await Task.Run(() => diffStore.GetPendingProposalCounts());
        if (approvedCount == 0 && pendingCount == 0)
        {
            Notify("待审队列为空，没有可应用的变更。", "warning");
            return;
        }

        // Approved present: drain ALL explicitly approved items batch-by-batch (500/batch).
        // None approved: apply a single bounded batch of up to 200 high-confidence (>=80%) items.
        bool approvedMode = approvedCount > 0;
        double threshold = approvedMode ? 1.01 : 0.80;
        int maxProps = approvedMode ? 500 : 200;
        const int maxFiles = 25;
        int totalBatches = approvedMode ? (int)Math.Ceiling(approvedCount / (double)maxProps) : 1;

        var token = BeginBusy(
            approvedMode ? $"正在应用 {approvedCount} 条已批准变更..." : "未批准单条，正在应用最多 200 条高置信度变更...",
            indeterminate: false,
            cancellable: approvedMode && totalBatches > 1);

        int committedTotal = 0;
        int filesTotal = 0;
        int batchIndex = 0;
        var batchIds = new List<string>();
        bool cancelled = false;
        try
        {
            while (batchIndex < totalBatches)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var candidateUids = await Task.Run(() => diffStore.GetCommitCandidateTrackUids(threshold, maxProps));
                if (candidateUids.Count == 0) break;

                ReportBusyProgress(100.0 * batchIndex / totalBatches, $"正在应用第 {batchIndex + 1}/{totalBatches} 批变更...");

                var suspendPaths = CurrentTrack is not null
                    && !string.IsNullOrWhiteSpace(CurrentTrack.FullPath)
                    && candidateUids.Contains(CurrentTrack.TrackUid, StringComparer.Ordinal)
                        ? new[] { CurrentTrack.FullPath }
                        : Array.Empty<string>();

                CurationCommitResult result;
                using (_engine.SuspendIfPlayingFile(suspendPaths))
                {
                    result = await Task.Run(() => diffStore.CommitApprovedBatch(
                        customBatchId: null,
                        autoApproveThreshold: threshold,
                        maxProposals: maxProps,
                        maxPhysicalFiles: maxFiles));
                }

                if (result.CommittedCount == 0) break;
                committedTotal += result.CommittedCount;
                filesTotal += result.PhysicalFilesUpdated;
                batchIds.Add(result.BatchId);
                LastCommittedBatchId = result.BatchId;

                if (!string.IsNullOrWhiteSpace(_store.MusicRoot))
                {
                    var uidsToRefresh = result.CommittedTrackUids is { Count: > 0 } ? result.CommittedTrackUids : candidateUids;
                    string root = _store.MusicRoot;
                    var refreshedMap = await Task.Run(() => _store.LoadTracksByUids(root, uidsToRefresh));
                    RefreshTracksFromPartialMap(refreshedMap);
                }
                batchIndex++;
            }
            ReportBusyProgress(100.0);
        }
        catch (Exception ex)
        {
            RefreshStagingProposals();
            Notify($"应用变更失败（已完成 {committedTotal} 条）: {ex.Message}", "error");
            EndBusy();
            return;
        }

        if (batchIds.Count > 0)
        {
            _lastCommitBatchIds.Clear();
            _lastCommitBatchIds.AddRange(batchIds);
        }
        RefreshStagingProposals();
        EndBusy();

        if (committedTotal == 0)
        {
            Notify("没有可应用的变更：请先批准至少一条提案。", "warning");
        }
        else if (cancelled)
        {
            Notify($"已取消：完成 {batchIds.Count}/{totalBatches} 批，应用 {committedTotal} 条变更（写回 {filesTotal} 个文件）。可「撤回上一批次」整体回滚。", "warning");
        }
        else
        {
            Notify($"已应用 {committedTotal} 条变更，共 {batchIds.Count} 批（写回 {filesTotal} 个文件）。", "success");
        }
    }

    [RelayCommand]
    private async Task ApproveAllHighConfidenceAsync()
    {
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null) return;
        int approved = await Task.Run(() => diffStore.ApproveAllHighConfidence(0.80));
        RefreshStagingProposals();
        if (approved > 0)
            Notify($"已批准 {approved} 条高置信度 (≥80%) 提案，点击「应用已批准的变更」开始分批写入。", "success");
        else
            Notify("当前没有置信度 ≥80% 的待审提案。", "warning");
    }

    [RelayCommand]
    private async Task ClearPendingStagingAsync()
    {
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null) return;
        int removed = await Task.Run(() => diffStore.ClearPendingProposals());
        RefreshStagingProposals();
        Notify($"已清除 {removed} 条待审/已驳回提案（已批准的提案保留）。", "success");
    }

    [RelayCommand]
    private void RollbackLastBatch()
    {
        var diffStore = _store.GetStagingDiffStore();
        var ids = _lastCommitBatchIds.Count > 0
            ? _lastCommitBatchIds.ToList()
            : (string.IsNullOrWhiteSpace(LastCommittedBatchId) ? [] : new List<string> { LastCommittedBatchId });
        if (diffStore is null || ids.Count == 0)
        {
            Notify("没有可撤回的变更批次。", "warning");
            return;
        }

        int restored = 0;
        for (int i = ids.Count - 1; i >= 0; i--)
        {
            restored += diffStore.RollbackBatch(ids[i]);
        }
        _lastCommitBatchIds.Clear();
        LastCommittedBatchId = null;
        if (!string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            _allTracks = _store.LoadFromMusicRoot(_store.MusicRoot).ToList();
            UpdateClassicalAdaptiveState();
            RefreshPlaylists();
            TriggerFilterRefresh();
        }
        RefreshStagingProposals();
        Notify($"已撤回 {ids.Count} 个批次共 {restored} 项标签变更。", "success");
    }

    private void RefreshTracksFromPartialMap(IReadOnlyDictionary<string, TrackItem> refreshedMap)
    {
        if (refreshedMap.Count == 0) return;

        var prevPlaylist = SelectedPlaylist;
        bool wasPlaylist = IsViewingPlaylist;
        string? inspectedUid = InspectedTrack?.TrackUid;

        for (int i = 0; i < _allTracks.Count; i++)
        {
            if (refreshedMap.TryGetValue(_allTracks[i].TrackUid, out var updatedItem))
            {
                _allTracks[i] = updatedItem;
            }
        }
        if (CurrentTrack is not null && refreshedMap.TryGetValue(CurrentTrack.TrackUid, out var refreshedCurrent))
        {
            CurrentTrack = refreshedCurrent;
        }
        UpdateClassicalAdaptiveState();
        if (wasPlaylist && prevPlaylist is not null && PrimaryNavId == "playlists")
        {
            LoadPlaylistTracksIntoView(prevPlaylist);
        }
        else if (IsEntityBrowserVisible)
        {
            RebuildEntityGroups();
        }
        else
        {
            ApplyFiveDimFilter();
        }
        if (inspectedUid is not null)
        {
            var refreshed = _allTracks.FirstOrDefault(t => t.TrackUid == inspectedUid);
            if (refreshed is not null) InspectTrack(refreshed);
        }
    }

    public void ApproveProposal(string proposalId, bool approved)
    {
        _store.GetStagingDiffStore()?.SetProposalApproval(proposalId, approved);
        RefreshStagingProposals();
    }

    [RelayCommand]
    private async Task RebuildLibraryManifestAsync()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            StatusMessage = "请先打开曲库。";
            return;
        }
        string root = _store.MusicRoot;
        StatusMessage = $"正在扫描曲库：{root}";
        BeginBusy($"正在重建本地指纹库 {root} ...");
        try
        {
            using var _watchGuard = _libraryWatcher?.SuspendWatching();
            await _store.ScanAndBuildManifestIfMissingAsync(root, forceRebuild: true, fastMode: true);
            _allTracks = await Task.Run(() => _store.LoadFromMusicRoot(root).ToList());
            UpdateClassicalAdaptiveState();
            RefreshPlaylists();
            TriggerFilterRefresh();
            RefreshStagingProposals();
            StatusMessage = $"已重建指纹库并索引 {_allTracks.Count:N0} 首曲目。";
            Notify($"曲库索引重建完成：共 {_allTracks.Count:N0} 首曲目。", "success");
        }
        catch (Exception ex)
        {
            StatusMessage = $"重建指纹库失败：{ex.Message}";
            Notify(StatusMessage, "error");
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task OrganizeLibraryFoldersAsync()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot) || string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            StatusMessage = "请先打开曲库。";
            Notify("请先打开曲库，再执行文件夹整理。", "warning");
            return;
        }

        string root = _store.MusicRoot;
        string dbPath = _store.ManifestDbPath;
        BeginBusy("正在按 <Album Artist>\\<Album>\\<Disc-Track#> <Title> 整理曲库文件夹...");
        try
        {
            using var _watchGuard = _libraryWatcher?.SuspendWatching();
            var res = await Task.Run(() =>
            {
                using var _ = _engine.SuspendIfPlayingFile(_allTracks.Select(t => t.FullPath));
                return LibraryFolderOrganizerService.OrganizeLibraryFolders(root, dbPath);
            });

            _allTracks = await Task.Run(() => _store.LoadFromMusicRoot(root).ToList());
            var libRefs = GetLibraryRefs();
            var rematchSummary = await Task.Run(() => PlaylistStore.RematchAllPlaylists(root, libRefs));
            UpdateClassicalAdaptiveState();
            RefreshPlaylists();
            TriggerFilterRefresh();
            RefreshStagingProposals();

            string rematchNote = rematchSummary.PlaylistsUpdated > 0
                ? $"，同步修复 {rematchSummary.PlaylistsUpdated} 个歌单（新匹配 +{rematchSummary.NewlyMatchedCount}，路径修复 {rematchSummary.PathRepairedCount}）"
                : "";
            StorageOrganizeStatus =
                $"整理完成：检查 {res.TotalChecked:N0} 首，迁移规范 {res.MovedCount:N0} 首（收敛超长路径 {res.LongPathFixedCount:N0} 首），清理失效索引 {res.StaleRemovedCount:N0} 条，已规范跳过 {res.SkippedCount:N0} 首{rematchNote}。";
            StatusMessage = StorageOrganizeStatus;
            Notify(StorageOrganizeStatus, res.MovedCount > 0 || res.StaleRemovedCount > 0 || rematchSummary.PlaylistsUpdated > 0 ? "success" : "info");
        }
        catch (Exception ex)
        {
            StorageOrganizeStatus = $"文件夹整理失败：{ex.Message}";
            StatusMessage = StorageOrganizeStatus;
            Notify(StorageOrganizeStatus, "error");
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task EnrichLibraryCoversAndBiosAsync()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot) || _allTracks.Count == 0)
        {
            StatusMessage = "请先打开曲库，再抓取封面与传记。";
            return;
        }

        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        string? lfmApiKey = LastFmAccountConfig.Load(tonewiseDir)?.ApiKey;
        var coverService = new CoverArtWaterfallService();
        var encService = _store.GetEncyclopediaService()
                         ?? new EncyclopediaService(Path.Combine(tonewiseDir, "encyclopedia.db"));

        StatusMessage = "正在抓取缺失的专辑封面与人物传记…";
        int coversSaved = 0;
        int biosCached = 0;
        var enrichToken = BeginBusy("正在抓取缺失封面与人物传记...", indeterminate: false, cancellable: true);
        var enrichSnapshot = _allTracks.ToList();

        await Task.Run(async () =>
        {
            var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int idx = 0;

            foreach (var t in enrichSnapshot)
            {
                if (enrichToken.IsCancellationRequested) break;
                idx++;
                if (idx % 25 == 0)
                {
                    ReportBusyProgress(100.0 * idx / Math.Max(1, enrichSnapshot.Count),
                        $"正在抓取封面与传记 ({idx}/{enrichSnapshot.Count}) · 封面 +{coversSaved} · 传记 +{biosCached}");
                }
                if (!string.IsNullOrWhiteSpace(t.AlbumDirectory) && seenAlbums.Add(t.AlbumDirectory))
                {
                    string coverPath = Path.Combine(t.AlbumDirectory, "cover.jpg");
                    if (!File.Exists(coverPath))
                    {
                        var fetched = await coverService.FetchBestCoverAsync(t.ArtistsDisplay, t.Album, null, lfmApiKey);
                        if (fetched.Success && fetched.ImageBytes is not null)
                        {
                            var outcome = coverService.SaveFolderCoverIfMissing(t.AlbumDirectory, fetched.ImageBytes);
                            if (outcome.Saved) coversSaved++;
                        }
                        await Task.Delay(250);
                    }
                }

                string entityType = t.IsClassical && !string.IsNullOrWhiteSpace(t.ComposersDisplay)
                    ? EncyclopediaEntityType.Composer
                    : EncyclopediaEntityType.Artist;
                string entityName = (t.IsClassical && !string.IsNullOrWhiteSpace(t.ComposersDisplay)
                    ? t.ComposersDisplay
                    : t.ArtistsDisplay).Split(',')[0].Trim();

                if (!string.IsNullOrWhiteSpace(entityName) && seenEntities.Add($"{entityType}:{entityName}"))
                {
                    var bio = await encService.FetchAndCacheBiographyAsync(entityType, entityName, null, lfmApiKey);
                    if (bio is not null) biosCached++;
                    await Task.Delay(250);
                }
            }
        });

        _allTracks = _store.LoadFromMusicRoot(_store.MusicRoot).ToList();
        ApplyFiveDimFilter();
        if (CurrentTrack is not null)
        {
            var refreshed = _allTracks.FirstOrDefault(x => x.TrackUid == CurrentTrack.TrackUid);
            if (refreshed is not null) UpdateClassicalAndBiographyPanel(refreshed);
        }
        bool enrichCancelled = enrichToken.IsCancellationRequested;
        EndBusy();
        StatusMessage = enrichCancelled
            ? $"已取消抓取: {coversSaved} 张封面已保存, {biosCached} 条传记已缓存。"
            : $"抓取完成：新增 {coversSaved} 张封面，缓存 {biosCached} 条传记。";
    }

    [RelayCommand]
    private async Task FetchMissingBasicInfoForCurrentListAsync()
    {
        if (FilteredTracks.Count == 0)
        {
            Notify("当前列表为空，无可补全曲目。", "warning");
            return;
        }
        var targets = FilteredTracks.Take(40).ToList();
        await FetchMissingBasicInfoForTracksAsync(targets);
    }

    [RelayCommand]
    private async Task FetchMissing5DTagsForCurrentListAsync()
    {
        if (FilteredTracks.Count == 0)
        {
            Notify("当前列表为空，无可分析曲目。", "warning");
            return;
        }
        var targets = FilteredTracks.Take(40).ToList();
        await FetchMissing5DTagsForTracksAsync(targets);
    }

    public async Task FetchMissingBasicInfoForTracksAsync(IReadOnlyList<TrackItem> targets)
    {
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null || string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            Notify("请先加载含 .tonewise/manifest.db 的音乐目录。", "warning");
            return;
        }
        if (targets.Count == 0)
        {
            Notify("请先选择至少一首曲目。", "warning");
            return;
        }

        var token = BeginBusy($"正在获取 {targets.Count} 首曲目缺失的基础信息...", indeterminate: targets.Count <= 1, cancellable: targets.Count > 1);
        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        string? lfmApiKey = LastFmAccountConfig.Load(tonewiseDir)?.ApiKey;
        var lookupService = new BasicMetadataLookupService();
        int updatedTracks = 0;
        int coversSaved = 0;
        var updatedUids = new List<string>();

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var t = targets[i];
                if (targets.Count > 1)
                {
                    ReportBusyProgress(100.0 * i / targets.Count, $"正在补全基础信息 ({i + 1}/{targets.Count})：{t.Title}");
                }

                var q = new BasicMetadataQuery(
                    TrackUid: t.TrackUid,
                    FullPath: t.FullPath,
                    RelPath: t.RelPath,
                    Title: t.Title,
                    Artists: t.ArtistsDisplay,
                    Album: t.Album,
                    Composers: t.ComposersDisplay,
                    Conductors: t.ConductorsDisplay,
                    WorkName: t.WorkName,
                    MovementName: t.MovementName,
                    Genre: t.Genre,
                    Year: t.Year,
                    Publisher: t.Publisher,
                    DurationMs: t.DurationMs,
                    AlbumDirectory: t.AlbumDirectory,
                    CoverArtPath: t.CoverArtPath);

                var (updates, savedCover) = await lookupService.ResolveMissingMetadataAsync(q, lfmApiKey, token);
                if (savedCover) coversSaved++;

                if (updates.Count > 0)
                {
                    using (_engine.SuspendIfPlayingFile([t.FullPath]))
                    {
                        var res = await Task.Run(() => diffStore.ApplyDirectCuratedEdits([t.TrackUid], updates), token);
                        if (res.CommittedCount > 0)
                        {
                            updatedTracks++;
                            updatedUids.Add(t.TrackUid);
                        }
                    }
                }
                else if (savedCover)
                {
                    updatedUids.Add(t.TrackUid);
                }
            }

            if (updatedUids.Count > 0)
            {
                string root = _store.MusicRoot;
                var refreshedMap = await Task.Run(() => _store.LoadTracksByUids(root, updatedUids));
                RefreshTracksFromPartialMap(refreshedMap);
            }
        }
        catch (OperationCanceledException)
        {
            Notify($"已取消补全（已更新 {updatedTracks} 首，保存封面 {coversSaved} 张）。", "warning");
            EndBusy();
            return;
        }
        catch (Exception ex)
        {
            Notify($"获取基础信息失败: {ex.Message}", "error");
            EndBusy();
            return;
        }

        EndBusy();
        if (updatedTracks > 0 || coversSaved > 0)
        {
            Notify($"已补全 {updatedTracks} 首曲目的缺失基础信息（新增封面 {coversSaved} 张）。", "success");
        }
        else
        {
            Notify("未检测到可补全的新增基础信息（当前字段已完整或暂未匹配到云端/目录条目）。", "info");
        }
    }

    public async Task FetchMissing5DTagsForTracksAsync(IReadOnlyList<TrackItem> targets)
    {
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null || string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            Notify("请先加载含 .tonewise/manifest.db 的音乐目录。", "warning");
            return;
        }
        if (targets.Count == 0)
        {
            Notify("请先选择至少一首曲目。", "warning");
            return;
        }

        var token = BeginBusy($"正在分析 {targets.Count} 首曲目缺失的五维标签...", indeterminate: targets.Count <= 1, cancellable: targets.Count > 1);
        int updatedTracks = 0;
        var updatedUids = new List<string>();

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var t = targets[i];
                if (targets.Count > 1)
                {
                    ReportBusyProgress(100.0 * i / targets.Count, $"正在分析五维标签 ({i + 1}/{targets.Count})：{t.Title}");
                }

                Acoustic5DResult inferred;
                bool isClassical = TrackItem.IsClassicalGenre(t.Genre) || t.IsClassical;
                if (File.Exists(t.FullPath))
                {
                    inferred = await Task.Run(() =>
                    {
                        try
                        {
                            return Acoustic5DInferenceService.AnalyzeFileAndInfer5D(t.TrackUid, t.FullPath, isClassical);
                        }
                        catch
                        {
                            return Infer5DFromTrackMetadata(t, isClassical);
                        }
                    }, token);
                }
                else
                {
                    inferred = Infer5DFromTrackMetadata(t, isClassical);
                }

                var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (!t.TempoBpm.HasValue && inferred.TempoBpm is > 0)
                {
                    updates["tempo_bpm"] = inferred.TempoBpm.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                }
                if (string.IsNullOrWhiteSpace(t.TempoBucket))
                {
                    string? bucket = inferred.TempoBucket ?? Acoustic5DInferenceService.MapBpmToTempoBucket(t.TempoBpm);
                    if (!string.IsNullOrWhiteSpace(bucket)) updates["tempo_bucket"] = bucket;
                }
                if (string.IsNullOrWhiteSpace(t.DjVocals) || string.Equals(t.DjVocals, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(inferred.DjVocals)) updates["dj_vocals"] = inferred.DjVocals;
                    else if (isClassical) updates["dj_vocals"] = "instrumental";
                }
                if (string.IsNullOrWhiteSpace(t.DjEnergy) && !string.IsNullOrWhiteSpace(inferred.DjEnergy))
                {
                    updates["dj_energy"] = inferred.DjEnergy;
                }
                if (t.Moods.Count == 0 && inferred.Moods.Count > 0)
                {
                    updates["mood_json"] = JsonSerializer.Serialize(inferred.Moods);
                }
                if (t.DjScene.Count == 0)
                {
                    var scenes = inferred.DjScenes.Count > 0
                        ? inferred.DjScenes
                        : Acoustic5DInferenceService.DeriveDjScenesDeterministic(
                            updates.GetValueOrDefault("dj_vocals") ?? t.DjVocals,
                            updates.GetValueOrDefault("dj_energy") ?? t.DjEnergy,
                            updates.GetValueOrDefault("tempo_bucket") ?? t.TempoBucket,
                            t.Moods.Count > 0 ? t.Moods : inferred.Moods);
                    if (scenes.Count > 0) updates["dj_scene_json"] = JsonSerializer.Serialize(scenes);
                }

                if (updates.Count > 0)
                {
                    using (_engine.SuspendIfPlayingFile([t.FullPath]))
                    {
                        var res = await Task.Run(() => diffStore.ApplyDirectCuratedEdits([t.TrackUid], updates), token);
                        if (res.CommittedCount > 0)
                        {
                            updatedTracks++;
                            updatedUids.Add(t.TrackUid);
                        }
                    }
                }
            }

            if (updatedUids.Count > 0)
            {
                string root = _store.MusicRoot;
                var refreshedMap = await Task.Run(() => _store.LoadTracksByUids(root, updatedUids));
                RefreshTracksFromPartialMap(refreshedMap);
            }
        }
        catch (OperationCanceledException)
        {
            Notify($"已取消分析（已更新 {updatedTracks} 首五维标签）。", "warning");
            EndBusy();
            return;
        }
        catch (Exception ex)
        {
            Notify($"获取五维标签失败: {ex.Message}", "error");
            EndBusy();
            return;
        }

        EndBusy();
        if (updatedTracks > 0)
        {
            Notify($"已分析并写入 {updatedTracks} 首曲目的缺失五维标签。", "success");
        }
        else
        {
            Notify("选中曲目的五维标签已完整，无需新增。", "info");
        }
    }

    private static Acoustic5DResult Infer5DFromTrackMetadata(TrackItem t, bool isClassical)
    {
        double? instProb = isClassical ? 0.90 : null;
        double? bpm = t.TempoBpm ?? (isClassical ? 74.0 : 108.0);
        double arousal = string.Equals(t.DjEnergy, "high", StringComparison.OrdinalIgnoreCase) ? 0.78
            : string.Equals(t.DjEnergy, "low", StringComparison.OrdinalIgnoreCase) ? 0.28 : 0.45;
        var vec = new AcousticFeatureVector(
            TrackUid: t.TrackUid,
            InstrumentalProbability: instProb,
            Bpm: bpm,
            BpmConfidence: 0.80,
            Arousal: arousal,
            Valence: 0.52,
            IntegratedLufs: t.ReplayGainTrackDb.HasValue ? -18.0 - t.ReplayGainTrackDb.Value : -15.0,
            TopGenre: string.IsNullOrWhiteSpace(t.Genre) && isClassical ? "Classical" : t.Genre,
            MoodHeads: isClassical ? [("Contemplative", 0.80), ("Calm", 0.74)] : null);
        return Acoustic5DInferenceService.InferTrack5D(vec);
    }

    public string ExportFilteredToUsb(string usbRootDir)
    {
        var exportTracks = FilteredTracks
            .Select(t => new UsbExportTrack(t.TrackUid, t.FullPath, t.Title, t.ArtistsDisplay, t.Album, t.DurationMs, t.CoverArtPath))
            .ToList();
        string name = FilterScene == "all" ? "Tonewise-Export" : $"Tonewise-{FilterScene}";
        var summary = UsbCarExporter.ExportPlaylistToUsb(name, exportTracks, usbRootDir);
        StatusMessage = $"已导出到 USB：{summary.ExportedTracks} 首曲目、{summary.ExportedCovers} 张封面 → {summary.PlaylistM3u8Path}";
        return summary.PlaylistM3u8Path;
    }

    private void RefreshStagingProposals()
    {
        StagingProposals.Clear();
        StagingProposalItems.Clear();
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null)
        {
            _totalPendingStagingCount = 0;
            HasStagingProposals = false;
            StagingSummaryText = "待审队列：未加载曲库";
            UpdateInboxAuditBadge();
            return;
        }

        var (approvedCount, pendingCount) = diffStore.GetPendingProposalCounts();
        _totalPendingStagingCount = approvedCount + pendingCount;

        var trackMap = _allTracks.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
        var viewItems = new List<StagingProposalViewItem>();
        foreach (var p in diffStore.GetPendingProposals(limit: 300))
        {
            StagingProposals.Add(p);
            string trackLabel = trackMap.TryGetValue(p.TrackUid, out var tr)
                ? (string.IsNullOrWhiteSpace(tr.ArtistsDisplay) ? tr.Title : $"{tr.Title} · {tr.ArtistsDisplay}")
                : p.TrackUid;
            string origLabel = FormatProposalValueDisplay(p.OriginalValue);
            string suggestedLabel = FormatProposalValueDisplay(p.SuggestedValue);
            string statusTag = p.Status == ProposalStatus.Approved ? $"[已批准] {p.SourceTag}" : p.SourceTag;
            viewItems.Add(new StagingProposalViewItem(
                ProposalId: p.ProposalId,
                TrackUid: p.TrackUid,
                TrackDisplay: trackLabel,
                FieldName: FormatProposalFieldLabel(p.FieldName),
                ValueTransition: $"{origLabel} → {suggestedLabel}",
                SourceBadge: $"{statusTag} ({p.Confidence:P0})",
                Status: p.Status
            ));
        }
        StagingProposalItems.ReplaceAll(viewItems);
        HasStagingProposals = _totalPendingStagingCount > 0;
        StagingSummaryText = _totalPendingStagingCount > viewItems.Count
            ? $"待审队列：已批准 {approvedCount:N0} 条 / 共 {_totalPendingStagingCount:N0} 条（列表仅显示优先的前 {viewItems.Count} 条）"
            : $"待审队列：已批准 {approvedCount:N0} 条 / 待审 {pendingCount:N0} 条";
        UpdateInboxAuditBadge();
    }

    private static string FormatProposalFieldLabel(string? fieldName) => (fieldName ?? "").Trim().ToLowerInvariant() switch
    {
        "mood_json" or "mood" => "情绪",
        "dj_scene_json" or "dj_scene" => "场景",
        "dj_energy" => "能量",
        "dj_vocals" => "人声",
        "tempo_bpm" => "BPM",
        "tempo_bucket" => "节奏",
        "genre" => "流派",
        "album" => "专辑",
        "year" => "年份",
        "publisher" => "厂牌",
        "artists_json" => "艺人",
        "composers_json" => "作曲",
        "conductors_json" => "指挥",
        "work_name" => "作品",
        "movement_name" => "乐章",
        _ => fieldName ?? "",
    };

    private static string FormatProposalValueDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "未设置";
        string trimmed = raw.Trim();
        if (trimmed == "[]") return "未设置";
        if (trimmed.StartsWith('['))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<List<string>>(trimmed);
                if (arr is null || arr.Count == 0) return "未设置";
                return string.Join(" · ", arr.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            catch
            {
                // Fall back to trimmed raw text if not valid JSON array
            }
        }
        return trimmed;
    }

    public void RejectProposal(string proposalId)
    {
        _store.GetStagingDiffStore()?.SetProposalApproval(proposalId, approved: false);
        RefreshStagingProposals();
    }

    public void ApplyLexiconTagsToTracks(IReadOnlyList<TrackItem> tracks, IReadOnlyDictionary<string, string?> updates)
    {
        if (tracks.Count == 0 || updates.Count == 0) return;
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null)
        {
            StatusMessage = "请先加载含 .tonewise/manifest.db 的音乐目录。";
            return;
        }

        var uids = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FullPath) && !t.TrackUid.StartsWith("unmatched:", StringComparison.Ordinal))
            .Select(t => t.TrackUid)
            .ToList();
        if (uids.Count == 0)
        {
            StatusMessage = "所选条目未匹配本地曲库，无法写入标签。";
            return;
        }

        var prevLovedByUid = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.TrackUid))
            .GroupBy(t => t.TrackUid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().IsLoved, StringComparer.Ordinal);

        CurationCommitResult result;
        using (_engine.SuspendIfPlayingFile(tracks.Select(t => t.FullPath)))
        {
            result = diffStore.ApplyDirectCuratedEdits(uids, updates);
        }
        if (result.CommittedCount > 0)
        {
            LastCommittedBatchId = result.BatchId;
            if (!string.IsNullOrWhiteSpace(_store.MusicRoot))
            {
                string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
                if (updates.TryGetValue("rating", out string? rawRating))
                {
                    bool newLoved = int.TryParse(rawRating?.Trim(), out int rv) && rv >= 5;
                    bool queuedAnyLoveChange = false;
                    foreach (var tr in tracks)
                    {
                        if (string.IsNullOrWhiteSpace(tr.TrackUid) || !uids.Contains(tr.TrackUid, StringComparer.Ordinal)) continue;
                        bool oldLoved = prevLovedByUid.TryGetValue(tr.TrackUid, out bool prev) && prev;
                        if (oldLoved != newLoved)
                        {
                            LastFmLedgerService.EnqueueLoveChange(
                                tonewiseDir,
                                tr.TrackUid,
                                tr.ArtistsDisplay,
                                tr.Title,
                                newLoved);
                            queuedAnyLoveChange = true;
                        }
                    }
                    if (queuedAnyLoveChange)
                    {
                        FlushPendingLoveOpsInBackground(_store.MusicRoot);
                    }
                }

                var prevPlaylist = SelectedPlaylist;
                bool wasPlaylist = IsViewingPlaylist;
                string? inspectedUid = InspectedTrack?.TrackUid;
                var refreshedMap = _store.LoadTracksByUids(_store.MusicRoot, uids);
                for (int i = 0; i < _allTracks.Count; i++)
                {
                    if (refreshedMap.TryGetValue(_allTracks[i].TrackUid, out var updatedItem))
                    {
                        _allTracks[i] = updatedItem;
                    }
                }
                for (int i = 0; i < PlaybackQueue.Count; i++)
                {
                    if (refreshedMap.TryGetValue(PlaybackQueue[i].TrackUid, out var updatedQ))
                    {
                        PlaybackQueue[i] = updatedQ;
                    }
                }
                if (CurrentTrack is not null && refreshedMap.TryGetValue(CurrentTrack.TrackUid, out var refreshedCurrent))
                {
                    CurrentTrack = refreshedCurrent;
                }
                UpdateClassicalAdaptiveState();
                if (wasPlaylist && prevPlaylist is not null && PrimaryNavId == "playlists")
                {
                    LoadPlaylistTracksIntoView(prevPlaylist);
                }
                else if (IsEntityBrowserVisible)
                {
                    RebuildEntityGroups();
                }
                else
                {
                    ApplyFiveDimFilter();
                }
                if (inspectedUid is not null)
                {
                    var refreshed = _allTracks.FirstOrDefault(t => t.TrackUid == inspectedUid);
                    if (refreshed is not null) InspectTrack(refreshed);
                }
            }
        }
        RefreshStagingProposals();
        StatusMessage = $"元数据编辑已写入 {uids.Count} 首曲目 ({result.CommittedCount} 项字段 · 物理文件更新 {result.PhysicalFilesUpdated} 个 · 批次 {result.BatchId})";
    }

}
