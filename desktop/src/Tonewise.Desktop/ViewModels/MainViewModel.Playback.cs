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
    public void PlayFromBrowseView(TrackItem track)
    {
        _playbackHistory.Clear();
        _djFreezeBackup = null;
        _currentFreezeToken = "";
        IsDjFreezeActive = false;

        var playable = FilteredTracks.Where(item => !string.IsNullOrWhiteSpace(item.FullPath)).ToList();
        PlaybackQueue.ReplaceAll(playable);
        _unshuffledQueueSnapshot = playable;
        if (IsShuffleEnabled && PlaybackQueue.Count > 1)
        {
            ApplyWorkAwareShuffleToQueue(track);
        }
        PlaybackQueueTitle = CurrentViewHeader;
        UpdatePlaybackQueueSummary();
        PlayTrack(track);
    }

    public void PlayQueueItem(TrackItem track)
    {
        if (track is null) return;
        bool shouldPause = !IsPlaying && RememberPauseOnSkip;
        PlayTrack(track, startPaused: shouldPause);
    }

    public void RemoveQueueItem(TrackItem track)
    {
        if (track is null) return;
        PlaybackQueue.Remove(track);
        _unshuffledQueueSnapshot.Remove(track);
        UpdatePlaybackQueueSummary();
        PreloadNextGaplessTrack();
        PersistPlaybackSessionSnapshot(force: true);
    }

    public void OnPlaybackQueueReordered()
    {
        if (!IsShuffleEnabled)
        {
            _unshuffledQueueSnapshot = [.. PlaybackQueue];
        }
        PreloadNextGaplessTrack();
        PersistPlaybackSessionSnapshot(force: true);
    }

    [RelayCommand]
    private void ClearPlaybackQueue()
    {
        CancelPendingPlaybackRecovery();
        PlaybackQueue.Clear();
        _unshuffledQueueSnapshot.Clear();
        _playbackHistory.Clear();
        _djFreezeBackup = null;
        _currentFreezeToken = "";
        IsDjFreezeActive = false;
        PlaybackQueueTitle = "播放队列为空";
        Interlocked.Increment(ref _gaplessPreloadGen);
        _engine.ClearPreloadedNext();
        UpdatePlaybackQueueSummary();
        PersistPlaybackSessionSnapshot(force: true);
    }

    [RelayCommand]
    private void LoadCurrentViewToQueue()
    {
        _djFreezeBackup = null;
        _currentFreezeToken = "";
        IsDjFreezeActive = false;
        var playable = FilteredTracks.Where(item => !string.IsNullOrWhiteSpace(item.FullPath)).ToList();
        PlaybackQueue.ReplaceAll(playable);
        _unshuffledQueueSnapshot = playable;
        if (IsShuffleEnabled && PlaybackQueue.Count > 1)
        {
            ApplyWorkAwareShuffleToQueue(CurrentTrack);
        }
        PlaybackQueueTitle = CurrentViewHeader;
        UpdatePlaybackQueueSummary();
        PreloadNextGaplessTrack();
        PersistPlaybackSessionSnapshot(force: true);
        Notify($"已将当前列表 {PlaybackQueue.Count} 首加入播放队列。", "success");
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        IsShuffleEnabled = !IsShuffleEnabled;
        Notify($"播放顺序已切换为：{ShuffleModeLabel}", "info");
    }

    [RelayCommand]
    private void CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch
        {
            "all" => "one",
            "one" => "off",
            _ => "all",
        };
        Notify($"循环模式已切换为：{RepeatModeLabel}", "info");
    }

    private void ApplyShuffleModeToActiveQueue(bool enabled)
    {
        if (PlaybackQueue.Count <= 1) return;
        if (enabled)
        {
            _unshuffledQueueSnapshot = [.. PlaybackQueue];
            ApplyWorkAwareShuffleToQueue(CurrentTrack);
        }
        else if (_unshuffledQueueSnapshot.Count > 0)
        {
            var currentSet = PlaybackQueue.ToHashSet();
            var restored = _unshuffledQueueSnapshot.Where(currentSet.Contains).ToList();
            foreach (var extra in PlaybackQueue)
            {
                if (!restored.Contains(extra)) restored.Add(extra);
            }
            ReplaceQueueItems(restored);
        }
        PreloadNextGaplessTrack();
        PersistPlaybackSessionSnapshot(force: true);
    }

    private void ApplyWorkAwareShuffleToQueue(TrackItem? anchorTrack)
    {
        if (PlaybackQueue.Count <= 1) return;
        var list = PlaybackQueue.ToList();
        int anchorIdx = anchorTrack is null
            ? -1
            : list.FindIndex(t => string.Equals(t.TrackUid, anchorTrack.TrackUid, StringComparison.Ordinal));
        var descriptors = list.Select(t => new ShuffleTrackDescriptor(
            t.TrackUid,
            t.Album,
            t.WorkName,
            t.MovementNumber is > 0 ? t.MovementNumber : null,
            t.RelPath,
            WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay),
            t.DiscNumber,
            t.TrackNumber)).ToList();
        int[] order = WorkAwareShuffleEngine.BuildWorkAwareOrder(descriptors, anchorIdx, _shuffleRandom);
        var reordered = new List<TrackItem>(order.Length);
        foreach (int idx in order) reordered.Add(list[idx]);
        ReplaceQueueItems(reordered);
    }

    private void ReplaceQueueItems(IReadOnlyList<TrackItem> items)
    {
        PlaybackQueue.ReplaceAll(items);
        UpdatePlaybackQueueSummary();
    }

    private void UpdatePlaybackModeLabels()
    {
        ShuffleModeLabel = IsShuffleEnabled ? "随机播放(整部)" : "顺序播放";
        RepeatModeLabel = RepeatMode switch
        {
            "one" => "单曲循环",
            "off" => "播完停止",
            _ => "列表循环",
        };
    }

    private void UpdatePlaybackQueueSummary()
    {
        PlaybackQueueSummary = $"播放列表 ({PlaybackQueue.Count})";
    }

    public void EnqueueTrackNext(TrackItem track) => EnqueueTracksNext([track]);

    public void EnqueueTracksNext(IReadOnlyList<TrackItem> tracks)
    {
        var playable = tracks.Where(t => !string.IsNullOrWhiteSpace(t.FullPath)).ToList();
        if (playable.Count == 0)
        {
            StatusMessage = "未匹配本地音频，无法加入下一首播放。";
            return;
        }

        if (PlaybackQueue.Count == 0)
        {
            var initial = new List<TrackItem>(playable.Count + 1);
            if (CurrentTrack is not null && !string.IsNullOrWhiteSpace(CurrentTrack.FullPath))
            {
                initial.Add(CurrentTrack);
            }
            initial.AddRange(playable);
            PlaybackQueue.AddRange(initial);
            _unshuffledQueueSnapshot = [.. PlaybackQueue];
            PlaybackQueueTitle = "自定义播放队列";
        }
        else
        {
            int curIdx = CurrentTrack is null ? -1 : FindQueueIndex(PlaybackQueue, CurrentTrack);
            int insertIdx = curIdx >= 0 ? Math.Min(curIdx + 1, PlaybackQueue.Count) : 0;
            PlaybackQueue.InsertRange(insertIdx, playable);
            if (!IsShuffleEnabled) _unshuffledQueueSnapshot = [.. PlaybackQueue];
        }

        PreloadNextGaplessTrack();
        PersistPlaybackSessionSnapshot(force: true);
        StatusMessage = playable.Count == 1
            ? $"已加入下一首播放: {playable[0].Title} (队列 {PlaybackQueue.Count} 首)"
            : $"已插入 {playable.Count} 首到下一首播放 (队列 {PlaybackQueue.Count} 首)";
    }

    public void EnqueueTracksToEnd(IReadOnlyList<TrackItem> tracks)
    {
        var playable = tracks.Where(t => !string.IsNullOrWhiteSpace(t.FullPath)).ToList();
        if (playable.Count == 0)
        {
            StatusMessage = "未匹配本地音频，无法追加到队列。";
            return;
        }

        var appended = new List<TrackItem>(playable.Count + 1);
        if (PlaybackQueue.Count == 0 && CurrentTrack is not null && !string.IsNullOrWhiteSpace(CurrentTrack.FullPath))
        {
            appended.Add(CurrentTrack);
            PlaybackQueueTitle = "自定义播放队列";
        }
        appended.AddRange(playable);
        PlaybackQueue.AddRange(appended);
        if (!IsShuffleEnabled) _unshuffledQueueSnapshot = [.. PlaybackQueue];
        PreloadNextGaplessTrack();
        PersistPlaybackSessionSnapshot(force: true);
        StatusMessage = playable.Count == 1
            ? $"已追加到队尾: {playable[0].Title} (队列 {PlaybackQueue.Count} 首)"
            : $"已追加 {playable.Count} 首到队尾 (队列 {PlaybackQueue.Count} 首)";
    }

    public void PlayTrack(TrackItem track, double initialSeekFraction = 0.0, bool startPaused = false) =>
        _ = TryPlayTrackInternal(track, initialSeekFraction, startPaused);

    private bool TryPlayTrackInternal(TrackItem track, double initialSeekFraction = 0.0, bool startPaused = false)
    {
        CancelPendingPlaybackRecovery();
        EvaluateAndCommitPlayCountIfQualified(completedNaturally: false);

        var previousTrack = CurrentTrack;
        CurrentTrack = track;
        _pendingRestorePositionSec = 0.0;
        CurrentWaveform = _store.LoadWaveform(track);
        if (InspectedTrack is null || ReferenceEquals(InspectedTrack, previousTrack) || InspectedTrack.TrackUid == previousTrack?.TrackUid)
        {
            InspectTrack(track);
        }
        else
        {
            TrackChanged?.Invoke(track);
        }
        WaveformUpdated?.Invoke();

        PushEnginePreferences();
        if (LongPathHelper.FileExists(track.FullPath))
        {
            try
            {
                _engine.PlayFile(track.FullPath, initialSeekFraction: initialSeekFraction, startPaused: startPaused);
                if (startPaused)
                {
                    IsPlaying = false;
                    _pausedSinceUtc = DateTime.UtcNow;
                }
                else
                {
                    IsPlaying = true;
                    _pausedSinceUtc = null;
                }

                ResetScrobbleSession(track);
                if (previousTrack is not null
                    && !string.Equals(previousTrack.TrackUid, track.TrackUid, StringComparison.Ordinal)
                    && (_playbackHistory.Count == 0 || !string.Equals(_playbackHistory[^1].TrackUid, previousTrack.TrackUid, StringComparison.Ordinal)))
                {
                    _playbackHistory.Add(previousTrack);
                    if (_playbackHistory.Count > 200) _playbackHistory.RemoveAt(0);
                }

                if (!startPaused)
                {
                    StatusMessage = $"正在播放：{track.Title}";
                    SendNowPlayingInBackground(track);
                }
                PreloadNextGaplessTrack();
                PersistPlaybackSessionSnapshot(force: true);
            }
            catch (Exception ex)
            {
                IsPlaying = false;
                StatusMessage = $"播放失败 ({track.Title}): {ex.Message}";
                return false;
            }

            if (CurrentWaveform is null)
            {
                string uid = track.TrackUid;
                string fullPath = track.FullPath;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var analysis = TonewiseCoreClient.AnalyzeAudioFile(fullPath);
                        if (analysis.Waveform150.Count == 0) return;
                        float[] wf = new float[analysis.Waveform150.Count];
                        for (int i = 0; i < wf.Length; i++)
                        {
                            wf[i] = analysis.Waveform150[i] / 255f;
                        }
                        void ApplyWaveform()
                        {
                            if (CurrentTrack?.TrackUid == uid)
                            {
                                CurrentWaveform = wf;
                                WaveformUpdated?.Invoke();
                            }
                        }
                        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
                            _uiContext.Post(_ => ApplyWaveform(), null);
                        else
                            ApplyWaveform();
                    }
                    catch
                    {
                        // Non-fatal on-demand waveform fallback
                    }
                });
            }
            return true;
        }
        else
        {
            _engine.Stop();
            IsPlaying = false;
            StatusMessage = string.IsNullOrWhiteSpace(track.FullPath)
                ? $"本地曲库未匹配：{track.ArtistsDisplay} — {track.Title}"
                : $"文件不存在：{track.FullPath}";
            return false;
        }
    }

    private void ReopenCurrentOutputAtCurrentPosition()
    {
        if (_engine.CurrentFilePath is null || CurrentTrack is null) return;
        bool wasPlaying = _engine.IsPlaying || IsPlaying;
        TimeSpan pos = _engine.Position;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (!LongPathHelper.FileExists(CurrentTrack.FullPath)) return;
        try
        {
            _engine.PlayFile(
                CurrentTrack.FullPath,
                initialPosition: pos > TimeSpan.Zero ? pos : null,
                startPaused: !wasPlaying);
            IsPlaying = wasPlaying;
            _pausedSinceUtc = wasPlaying ? null : DateTime.UtcNow;
            PreloadNextGaplessTrack();
        }
        catch (Exception ex)
        {
            IsPlaying = false;
            StatusMessage = $"切换输出设备失败 ({CurrentTrack.Title}): {ex.Message}";
        }
    }

    public void SwitchAlternateRecording(TrackItem alternate)
    {
        double ratio = PlaybackProgress;
        string? curWorkKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
            alternate.WorkName,
            alternate.Album,
            WorkAwareShuffleEngine.BuildAlbumScope(alternate.Album, alternate.ConductorsDisplay));

        IReadOnlyList<TrackItem> altWorkOrdered = string.IsNullOrEmpty(curWorkKey)
            ? [alternate]
            : _allTracks
                .Where(t => !string.IsNullOrWhiteSpace(t.FullPath)
                            && string.Equals(
                                WorkAwareShuffleEngine.BuildWorkBlockKey(
                                    t.WorkName,
                                    t.Album,
                                    WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay)),
                                curWorkKey,
                                StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.DiscNumber > 0 ? t.DiscNumber : int.MaxValue)
                .ThenBy(t => t.MovementNumber is > 0 ? t.MovementNumber.Value : int.MaxValue)
                .ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue)
                .ThenBy(t => t.RelPath, Comparer<string>.Create(WorkAwareShuffleEngine.NaturalPathCompare))
                .ToList();

        if (PlaybackQueue.Count > 0 && CurrentTrack is not null)
        {
            var queueList = PlaybackQueue.ToList();
            int curIdx = queueList.FindIndex(t => string.Equals(t.TrackUid, CurrentTrack.TrackUid, StringComparison.Ordinal));
            if (curIdx >= 0)
            {
                var (updatedQueue, _) = ClassicalAbSwitchHelper.ReplaceWorkBlockInQueue(
                    queueList,
                    curIdx,
                    alternate,
                    altWorkOrdered,
                    t => t.TrackUid,
                    t => t.WorkName,
                    t => WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay));
                ReplaceQueueItems(updatedQueue);
            }
        }
        else if (altWorkOrdered.Count > 1)
        {
            ReplaceQueueItems(altWorkOrdered);
        }

        PlayTrack(alternate, initialSeekFraction: ratio);
        StatusMessage = $"已切换录音版本（乐章 {ratio:P0} 处）：{alternate.AlternateVersionLabel}";
    }

    private void UpdateClassicalAndBiographyPanel(TrackItem track)
    {
        InspectedComposerChip = string.IsNullOrWhiteSpace(track.ComposersDisplay) ? "" : track.ComposersDisplay.Split(',')[0].Trim();
        InspectedConductorChip = string.IsNullOrWhiteSpace(track.ConductorsDisplay) ? "" : track.ConductorsDisplay.Split(',')[0].Trim();
        InspectedPublisherChip = string.IsNullOrWhiteSpace(track.Publisher) ? "" : track.Publisher.Trim();
        HasInspectedComposer = !string.IsNullOrWhiteSpace(InspectedComposerChip);
        HasInspectedConductor = !string.IsNullOrWhiteSpace(InspectedConductorChip);
        HasInspectedPublisher = !string.IsNullOrWhiteSpace(InspectedPublisherChip);

        AlternateRecordings.Clear();
        var alts = ClassicalAbSwitchHelper.FindAlternateRecordings(
            track,
            _allTracks,
            t => t.TrackUid,
            t => t.WorkName,
            t => t.MovementName,
            t => t.MovementNumber,
            t => WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay)
        );
        foreach (var alt in alts) AlternateRecordings.Add(alt);
        HasAlternateRecordings = AlternateRecordings.Count > 0;

        var enc = _store.GetEncyclopediaService();
        if (enc is null || !File.Exists(enc.DbPath))
        {
            CurrentBiographySummary = track.IsClassical
                ? $"{track.ClassicalFourLevelSummary}\n(Run biography sync or open populated encyclopedia.db for composer notes)"
                : $"{track.ArtistsDisplay} — {track.Album}";
            return;
        }

        string lookupName = track.IsClassical && !string.IsNullOrWhiteSpace(track.ComposersDisplay)
            ? track.ComposersDisplay.Split(',')[0].Trim()
            : track.ArtistsDisplay.Split(',')[0].Trim();
        string lookupType = track.IsClassical && !string.IsNullOrWhiteSpace(track.ComposersDisplay)
            ? EncyclopediaEntityType.Composer
            : EncyclopediaEntityType.Artist;

        var bio = enc.GetBiography(lookupType, lookupName)
                  ?? enc.GetBiography(EncyclopediaEntityType.Artist, lookupName);

        if (bio is not null)
        {
            string body = !string.IsNullOrWhiteSpace(bio.SummaryZh) ? bio.SummaryZh : (bio.SummaryEn ?? "");
            CurrentBiographySummary = $"{bio.DisplayName} ({bio.BirthDeathYears ?? bio.Era ?? bio.EntityType})\n{body}";
        }
        else
        {
            CurrentBiographySummary = $"本地百科暂无「{lookupName}」的条目。";
        }
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        bool wasRecovering = _isAutoRecoveringPlayback;
        CancelPendingPlaybackRecovery(releasePowerKeepAlive: wasRecovering);
        if (wasRecovering)
        {
            IsPlaying = false;
            _lastProgressSampleUtc = null;
            _pausedSinceUtc = DateTime.UtcNow;
            PersistPlaybackSessionSnapshot(force: true);
            StatusMessage = "已取消自动重连并暂停播放。";
            return;
        }

        if (_engine.CurrentFilePath is null && CurrentTrack is not null)
        {
            double durSec = CurrentTrack.DurationMs / 1000.0;
            double frac = durSec > 0 && _pendingRestorePositionSec > 0
                ? Math.Clamp(_pendingRestorePositionSec / durSec, 0.0, 0.99)
                : 0.0;
            PlayTrack(CurrentTrack, initialSeekFraction: frac, startPaused: false);
            return;
        }

        if (_engine.IsPlaying)
        {
            AccumulateListeningTime();
            EvaluateAndCommitPlayCountIfQualified(completedNaturally: false);
        }
        _engine.TogglePlayPause();
        IsPlaying = _engine.IsPlaying;
        if (IsPlaying)
        {
            _lastProgressSampleUtc = DateTime.UtcNow;
            _pausedSinceUtc = null;
        }
        else
        {
            _lastProgressSampleUtc = null;
            _pausedSinceUtc = DateTime.UtcNow;
            PersistPlaybackSessionSnapshot(force: true);
        }
    }

    private void HandlePlaybackEnded()
    {
        EvaluateAndCommitPlayCountIfQualified(completedNaturally: true);
        if (RepeatMode == "one" && CurrentTrack is not null)
        {
            PlayTrack(CurrentTrack, startPaused: PauseOnSingleRepeat);
            return;
        }
        NextTrack(autoAdvance: true);
    }

    private void HandleGaplessTrackAdvanced(string nextPath)
    {
        EvaluateAndCommitPlayCountIfQualified(completedNaturally: true);

        IList<TrackItem> source = PlaybackQueue.Count > 0 ? PlaybackQueue : FilteredTracks;
        int curIdx = CurrentTrack is null ? -1 : FindQueueIndex(source, CurrentTrack);
        TrackItem? nextTrack = null;
        if (curIdx >= 0)
        {
            for (int offset = 1; offset <= source.Count; offset++)
            {
                var cand = source[(curIdx + offset) % source.Count];
                if (string.Equals(cand.FullPath, nextPath, StringComparison.OrdinalIgnoreCase))
                {
                    nextTrack = cand;
                    break;
                }
            }
        }
        nextTrack ??= source.FirstOrDefault(t => string.Equals(t.FullPath, nextPath, StringComparison.OrdinalIgnoreCase))
                      ?? _allTracks.FirstOrDefault(t => string.Equals(t.FullPath, nextPath, StringComparison.OrdinalIgnoreCase));
        if (nextTrack is null) return;

        var previousTrack = CurrentTrack;
        string? prevWorkKey = BuildTrackWorkKey(previousTrack);
        string? nextWorkKey = BuildTrackWorkKey(nextTrack);
        if (_sleepTimer.ShouldStopAtTrackTransition(prevWorkKey, nextWorkKey))
        {
            CancelPendingPlaybackRecovery();
            _engine.Pause();
            _engine.SetSleepFadeLinear(1.0);
            _pausedSinceUtc = DateTime.UtcNow;
            IsPlaying = false;
            SleepTimerBadge = "";
            IsSleepTimerActive = false;
            PersistPlaybackSessionSnapshot(force: true);
            StatusMessage = "睡眠定时（本曲/本作品结束）已暂停播放。";
            SleepTimerExpired?.Invoke();
            return;
        }

        CurrentTrack = nextTrack;
        _pendingRestorePositionSec = 0.0;
        ResetScrobbleSession(nextTrack);
        if (previousTrack is not null && !string.Equals(previousTrack.TrackUid, nextTrack.TrackUid, StringComparison.Ordinal))
        {
            _playbackHistory.Add(previousTrack);
            if (_playbackHistory.Count > 200) _playbackHistory.RemoveAt(0);
        }

        CurrentWaveform = _store.LoadWaveform(nextTrack);
        if (InspectedTrack is null || ReferenceEquals(InspectedTrack, previousTrack) || InspectedTrack.TrackUid == previousTrack?.TrackUid)
        {
            InspectTrack(nextTrack);
        }
        else
        {
            TrackChanged?.Invoke(nextTrack);
        }
        WaveformUpdated?.Invoke();
        IsPlaying = _engine.IsPlaying;
        StatusMessage = $"正在播放：{nextTrack.Title}";
        SendNowPlayingInBackground(nextTrack);
        PersistPlaybackSessionSnapshot(force: true);
        PreloadNextGaplessTrack();
    }

    [RelayCommand]
    public void NextTrack() => NextTrack(autoAdvance: false);

    private void NextTrack(bool autoAdvance)
    {
        if (!autoAdvance)
        {
            CancelPendingPlaybackRecovery();
        }
        IList<TrackItem> source = PlaybackQueue.Count > 0 ? PlaybackQueue : FilteredTracks;
        if (source.Count == 0) return;

        bool shouldPause = !autoAdvance && !IsPlaying && RememberPauseOnSkip;
        int maxAttempts = autoAdvance ? Math.Min(5, source.Count) : 1;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            TrackItem? candidate = ResolveNextCandidate(source, autoAdvance, allowReshuffle: attempt == 0);
            if (candidate is null)
            {
                EvaluateAndCommitPlayCountIfQualified(completedNaturally: autoAdvance);
                IsPlaying = false;
                Interlocked.Increment(ref _gaplessPreloadGen);
                _engine.ClearPreloadedNext();
                _engine.ReleasePowerKeepAlive();
                if (autoAdvance && _sleepTimer.Mode != SleepTimerMode.Off)
                {
                    _sleepTimer.ShouldStopAtTrackTransition(BuildTrackWorkKey(CurrentTrack), null);
                    _engine.SetSleepFadeLinear(1.0);
                    SleepTimerBadge = "";
                    IsSleepTimerActive = false;
                    SleepTimerExpired?.Invoke();
                }
                PersistPlaybackSessionSnapshot(force: true);
                StatusMessage = "当前列表已顺序播放完毕。";
                return;
            }
            if (autoAdvance && _sleepTimer.ShouldStopAtTrackTransition(BuildTrackWorkKey(CurrentTrack), BuildTrackWorkKey(candidate)))
            {
                EvaluateAndCommitPlayCountIfQualified(completedNaturally: true);
                IsPlaying = false;
                _pausedSinceUtc = DateTime.UtcNow;
                _engine.SetSleepFadeLinear(1.0);
                SleepTimerBadge = "";
                IsSleepTimerActive = false;
                PersistPlaybackSessionSnapshot(force: true);
                StatusMessage = "睡眠定时（本曲/本作品结束）已暂停播放。";
                SleepTimerExpired?.Invoke();
                return;
            }
            if (TryPlayTrackInternal(candidate, startPaused: shouldPause) || !autoAdvance)
            {
                return;
            }
        }
    }

    private TrackItem? ResolveNextCandidate(IList<TrackItem> source, bool autoAdvance, bool allowReshuffle)
    {
        if (source.Count == 0) return null;
        int startIdx = CurrentTrack is null ? -1 : FindQueueIndex(source, CurrentTrack);

        for (int offset = 1; offset <= source.Count; offset++)
        {
            int rawIdx = startIdx + offset;
            if (rawIdx >= source.Count)
            {
                if (autoAdvance && RepeatMode == "off")
                {
                    return null;
                }
                if (IsShuffleEnabled && PlaybackQueue.Count > 1)
                {
                    if (!allowReshuffle) return null;
                    var list = PlaybackQueue.ToList();
                    var descriptors = list.Select(t => new ShuffleTrackDescriptor(
                        t.TrackUid,
                        t.Album,
                        t.WorkName,
                        t.MovementNumber is > 0 ? t.MovementNumber : null,
                        t.RelPath,
                        WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay),
                        t.DiscNumber,
                        t.TrackNumber)).ToList();
                    int[] order = WorkAwareShuffleEngine.BuildWorkAwareOrder(descriptors, -1, _shuffleRandom);
                    var reshuffled = order.Select(i => list[i]).ToList();
                    WorkAwareShuffleEngine.AvoidLeadingRepeatPreservingWorkBlock(
                        reshuffled,
                        CurrentTrack?.TrackUid,
                        t => t.TrackUid,
                        t => t.Album,
                        t => t.WorkName,
                        t => WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay),
                        BuildTrackWorkKey(CurrentTrack));
                    ReplaceQueueItems(reshuffled);
                    return reshuffled.FirstOrDefault(t => !string.IsNullOrEmpty(t.FullPath));
                }
            }
            int idx = ((rawIdx % source.Count) + source.Count) % source.Count;
            var cand = source[idx];
            if (!string.IsNullOrEmpty(cand.FullPath))
            {
                return cand;
            }
        }
        return null;
    }

    private void PreloadNextGaplessTrack()
    {
        int gen = Interlocked.Increment(ref _gaplessPreloadGen);
        if (RepeatMode == "one" || CurrentTrack is null || _engine.CurrentFilePath is null)
        {
            _engine.ClearPreloadedNext();
            return;
        }
        IList<TrackItem> source = PlaybackQueue.Count > 0 ? PlaybackQueue : FilteredTracks;
        var next = ResolveNextCandidate(source, autoAdvance: true, allowReshuffle: false);
        if (next is null || string.IsNullOrWhiteSpace(next.FullPath))
        {
            _engine.ClearPreloadedNext();
            return;
        }
        if (_sleepTimer.WillStopAtTransition(BuildTrackWorkKey(CurrentTrack), BuildTrackWorkKey(next)))
        {
            _engine.ClearPreloadedNext();
            return;
        }

        string nextPath = next.FullPath;
        double nextRg = ResolveReplayGainForTrack(next);
        _ = Task.Run(() =>
        {
            if (Volatile.Read(ref _gaplessPreloadGen) != gen) return;
            _engine.PreloadNextFile(nextPath, nextRg);
            if (Volatile.Read(ref _gaplessPreloadGen) != gen)
            {
                _engine.ClearPreloadedNext();
            }
        });
    }

    private static int FindQueueIndex(IList<TrackItem> source, TrackItem track)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (string.Equals(source[i].TrackUid, track.TrackUid, StringComparison.Ordinal))
                return i;
        }
        for (int i = 0; i < source.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(track.RelPath)
                && string.Equals(source[i].RelPath, track.RelPath, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    [RelayCommand]
    public void PreviousTrack()
    {
        CancelPendingPlaybackRecovery();
        double currentPosSec = _engine.CurrentFilePath is not null
            ? _engine.Position.TotalSeconds
            : _pendingRestorePositionSec;
        if (RewindOnPrevious && CurrentTrack is not null && currentPosSec > 3.0)
        {
            if (_engine.CurrentFilePath is not null)
            {
                SeekFraction(0.0);
            }
            else
            {
                _pendingRestorePositionSec = 0.0;
                PlaybackPositionSeconds = 0.0;
                PlaybackProgress = 0.0;
            }
            return;
        }

        bool shouldPause = !IsPlaying && RememberPauseOnSkip;
        IList<TrackItem> source = PlaybackQueue.Count > 0 ? PlaybackQueue : FilteredTracks;
        if (source.Count == 0)
        {
            if (_playbackHistory.Count > 0)
            {
                var prev = _playbackHistory[^1];
                _playbackHistory.RemoveAt(_playbackHistory.Count - 1);
                if (!string.IsNullOrEmpty(prev.FullPath)) PlayTrack(prev, startPaused: shouldPause);
            }
            return;
        }

        int startIdx = CurrentTrack is null ? 0 : Math.Max(0, FindQueueIndex(source, CurrentTrack));
        for (int offset = 1; offset <= source.Count; offset++)
        {
            int idx = ((startIdx - offset) % source.Count + source.Count) % source.Count;
            var candidate = source[idx];
            if (!string.IsNullOrEmpty(candidate.FullPath))
            {
                PlayTrack(candidate, startPaused: shouldPause);
                return;
            }
        }
        StatusMessage = "当前队列无可播放的本地曲目。";
    }

    [RelayCommand]
    public void ActivateCodingQuickScene()
    {
        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = "all";
            FilterScene = "coding";
            ExcludeVocal = true;
            FilterVocals = "all";
            FilterEnergy = "all";
            FilterTempoBucket = "all";
            FilterMoodCluster = "all";
            SelectedEnergyArc = "flat";
        });
        if (FilteredTracks.Count > 0)
        {
            PlayFromBrowseView(FilteredTracks[0]);
        }
    }

    public void SeekFraction(double fraction)
    {
        CancelPendingPlaybackRecovery();
        if (_engine.CurrentFilePath is null && CurrentTrack is not null)
        {
            double durSec = CurrentTrack.DurationMs / 1000.0;
            _pendingRestorePositionSec = Math.Clamp(fraction, 0.0, 1.0) * durSec;
            PlaybackPositionSeconds = _pendingRestorePositionSec;
            PlaybackProgress = Math.Clamp(fraction, 0.0, 1.0);
            PersistPlaybackSessionSnapshot(force: true);
            return;
        }
        _engine.Seek(fraction);
        RefreshProgress();
    }

    public void SeekToSeconds(double seconds)
    {
        CancelPendingPlaybackRecovery();
        if (_engine.CurrentFilePath is null && CurrentTrack is not null)
        {
            double totalSec = CurrentTrack.DurationMs / 1000.0;
            if (totalSec > 0)
            {
                _pendingRestorePositionSec = Math.Clamp(seconds, 0.0, totalSec);
                PlaybackPositionSeconds = _pendingRestorePositionSec;
                PlaybackProgress = Math.Clamp(_pendingRestorePositionSec / totalSec, 0.0, 1.0);
                PersistPlaybackSessionSnapshot(force: true);
            }
            return;
        }
        double dur = _engine.Duration.TotalSeconds;
        if (dur <= 0) return;
        _engine.Seek(Math.Clamp(seconds / dur, 0.0, 0.995));
        RefreshProgress();
        WaveformUpdated?.Invoke();
    }

    private static string? BuildTrackWorkKey(TrackItem? track) =>
        track is null
            ? null
            : WorkAwareShuffleEngine.BuildWorkBlockKey(
                track.WorkName,
                track.Album,
                WorkAwareShuffleEngine.BuildAlbumScope(track.Album, track.ConductorsDisplay));

    [RelayCommand]
    public void SetSleepTimer(string? option)
    {
        string normalized = (option ?? "off").Trim().ToLowerInvariant();
        if (normalized == "off")
        {
            _sleepTimer.Cancel();
            _engine.SetSleepFadeLinear(1.0);
            SleepTimerBadge = "";
            IsSleepTimerActive = false;
            StatusMessage = "已关闭睡眠定时。";
        }
        else if (normalized == "track")
        {
            _sleepTimer.SetEndOfTrack();
            _engine.SetSleepFadeLinear(1.0);
            SleepTimerBadge = "🌙 本曲";
            IsSleepTimerActive = true;
            StatusMessage = "已开启睡眠定时：本曲结束后暂停。";
        }
        else if (normalized == "work")
        {
            _sleepTimer.SetEndOfWork(BuildTrackWorkKey(CurrentTrack));
            _engine.SetSleepFadeLinear(1.0);
            SleepTimerBadge = "🌙 本作品";
            IsSleepTimerActive = true;
            StatusMessage = "已开启睡眠定时：当前古典作品全部乐章结束后暂停。";
        }
        else if (int.TryParse(normalized, out int minutes) && minutes > 0)
        {
            _sleepTimer.SetCountdown(minutes);
            var tick = _sleepTimer.Tick();
            _engine.SetSleepFadeLinear(tick.FadeGainLinear);
            SleepTimerBadge = tick.BadgeText;
            IsSleepTimerActive = tick.IsActive;
            StatusMessage = $"已开启睡眠定时：{minutes} 分钟后淡出暂停。";
        }
        PreloadNextGaplessTrack();
    }

    private void TickSleepTimer()
    {
        if (!IsSleepTimerActive && _sleepTimer.Mode == SleepTimerMode.Off) return;
        var tick = _sleepTimer.Tick();
        _engine.SetSleepFadeLinear(tick.FadeGainLinear);
        SleepTimerBadge = tick.BadgeText;
        IsSleepTimerActive = tick.IsActive;

        if (tick.Expired)
        {
            CancelPendingPlaybackRecovery(releasePowerKeepAlive: true);
            if (_engine.IsPlaying || IsPlaying)
            {
                _engine.Pause();
                IsPlaying = false;
                _pausedSinceUtc = DateTime.UtcNow;
                PersistPlaybackSessionSnapshot(force: true);
            }
            _engine.SetSleepFadeLinear(1.0);
            StatusMessage = "睡眠定时已到，已暂停播放。";
            SleepTimerExpired?.Invoke();
        }
    }

    public void RefreshProgress()
    {
        TickSleepTimer();
        if (_engine.CurrentFilePath is null && CurrentTrack is not null)
        {
            double totalSec = Math.Max(0.0, CurrentTrack.DurationMs / 1000.0);
            PlaybackDurationSeconds = totalSec;
            PlaybackPositionSeconds = Math.Clamp(_pendingRestorePositionSec, 0.0, totalSec);
            PlaybackProgress = totalSec > 0 ? Math.Clamp(PlaybackPositionSeconds / totalSec, 0.0, 1.0) : 0.0;
            var pTs = TimeSpan.FromSeconds(PlaybackPositionSeconds);
            var dTs = TimeSpan.FromSeconds(totalSec);
            TimeDisplay = totalSec > 0
                ? $"{(int)pTs.TotalMinutes}:{pTs.Seconds:D2} / {(int)dTs.TotalMinutes}:{dTs.Seconds:D2}"
                : "";
            string rgRest = CurrentTrack.ReplayGainBadge;
            string qRest = PlaybackQueue.Count > 0 ? $" · 队列 {PlaybackQueue.Count} 首" : "";
            CurrentTrackSpecsSubline = $"{TimeDisplay}{(rgRest == "RG2 --" ? "" : $" · {rgRest}")}{qRest}";
            if (!_isAutoRecoveringPlayback)
            {
                IsPlaying = false;
            }
            return;
        }

        var dur = _engine.Duration;
        var pos = _engine.Position;
        PlaybackDurationSeconds = Math.Max(0.0, dur.TotalSeconds);
        PlaybackPositionSeconds = Math.Clamp(pos.TotalSeconds, 0.0, PlaybackDurationSeconds);
        PlaybackProgress = dur.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0) : 0.0;
        TimeDisplay = dur.TotalSeconds > 0
            ? $"{(int)pos.TotalMinutes}:{pos.Seconds:D2} / {(int)dur.TotalMinutes}:{dur.Seconds:D2}"
            : "";
        if (CurrentTrack is null && dur.TotalSeconds <= 0)
        {
            CurrentTrackSpecsSubline = PlaybackQueue.Count > 0 ? $"队列 {PlaybackQueue.Count} 首" : "";
        }
        else
        {
            string rg = CurrentTrack?.ReplayGainBadge ?? "";
            string queueInfo = PlaybackQueue.Count > 0 ? $" · 队列 {PlaybackQueue.Count} 首" : "";
            CurrentTrackSpecsSubline = $"{TimeDisplay}{(string.IsNullOrEmpty(rg) || rg == "RG2 --" ? "" : $" · {rg}")}{queueInfo}";
        }

        if (!_isAutoRecoveringPlayback)
        {
            IsPlaying = _engine.IsPlaying;
        }
        AccumulateListeningTime();
        if (IsPlaying && _scrobbleTrack is not null && !_scrobbledCurrentTrack)
        {
            int curDurMs = _scrobbleTrack.DurationMs > 0 ? _scrobbleTrack.DurationMs : (int)(PlaybackDurationSeconds * 1000.0);
            if (PlaybackScrobbleEvaluator.ShouldCommitPlay(curDurMs, _accumulatedPlayMs, completedNaturally: false))
            {
                EvaluateAndCommitPlayCountIfQualified(completedNaturally: false);
            }
        }

        if (IsPlaying && CurrentTrack is not null && (DateTime.UtcNow - _lastSessionSavedUtc).TotalSeconds >= 5.0)
        {
            PersistPlaybackSessionSnapshot(force: false);
        }

        if (!IsPlaying
            && CurrentTrack is not null
            && _engine.CurrentFilePath is not null
            && _engine.CurrentSignalPath.IsExclusive
            && ExclusiveIdleReleaseMinutes > 0
            && _pausedSinceUtc.HasValue
            && (DateTime.UtcNow - _pausedSinceUtc.Value).TotalMinutes >= ExclusiveIdleReleaseMinutes)
        {
            _pendingRestorePositionSec = PlaybackPositionSeconds;
            _engine.ClearPreloadedNext();
            _engine.Stop();
            _pausedSinceUtc = null;
            PersistPlaybackSessionSnapshot(force: true);
            StatusMessage = $"WASAPI 独占已空闲释放（暂停超 {ExclusiveIdleReleaseMinutes} 分钟），点击播放即可从当前位置续播。";
        }

        if (_engine.ConsumePendingNaturalEnd())
        {
            HandlePlaybackEnded();
        }
    }

    private void ResetScrobbleSession(TrackItem track)
    {
        _scrobbleTrack = track;
        _scrobbleStartedUtcSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _accumulatedPlayMs = 0.0;
        _lastProgressSampleUtc = _engine.IsPlaying ? DateTime.UtcNow : null;
        _scrobbledCurrentTrack = false;
    }

    private void AccumulateListeningTime()
    {
        if (_scrobbleTrack is null) return;
        var now = DateTime.UtcNow;
        if (_engine.IsPlaying)
        {
            if (_lastProgressSampleUtc.HasValue)
            {
                double delta = Math.Clamp((now - _lastProgressSampleUtc.Value).TotalMilliseconds, 0.0, 15_000.0);
                _accumulatedPlayMs += delta;
            }
            _lastProgressSampleUtc = now;
        }
        else
        {
            _lastProgressSampleUtc = null;
        }
    }

    private void EvaluateAndCommitPlayCountIfQualified(bool completedNaturally, bool synchronousLocalPersist = false)
    {
        var track = _scrobbleTrack;
        if (track is null || _scrobbledCurrentTrack) return;
        AccumulateListeningTime();

        int durMs = track.DurationMs > 0 ? track.DurationMs : (int)(PlaybackDurationSeconds * 1000.0);
        if (!PlaybackScrobbleEvaluator.ShouldCommitPlay(durMs, _accumulatedPlayMs, completedNaturally))
            return;

        _scrobbledCurrentTrack = true;
        bool scrobbleEligible = PlaybackScrobbleEvaluator.IsEligibleForLastFmScrobble(durMs, _accumulatedPlayMs, completedNaturally);
        long playedAtSec = _scrobbleStartedUtcSec > 0 ? _scrobbleStartedUtcSec : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        IncrementTrackPlayCountInMemory(track.TrackUid, playedAtSec * 1000L);

        string? musicRoot = _store.MusicRoot;
        string? manifestDb = _store.ManifestDbPath;
        if (string.IsNullOrWhiteSpace(musicRoot)) return;
        string tonewiseDir = Path.Combine(musicRoot, ".tonewise");

        if (synchronousLocalPersist)
        {
            try
            {
                LastFmLedgerService.RecordLocalPlaybackEvent(
                    manifestDbPath: manifestDb ?? "",
                    tonewiseDir: tonewiseDir,
                    trackUid: track.TrackUid,
                    artist: track.ArtistsDisplay,
                    title: track.Title,
                    album: track.Album,
                    playedAtUnixSec: playedAtSec,
                    scrobbleEligible: scrobbleEligible);
            }
            catch
            {
                // Non-fatal local shutdown flush
            }
            return;
        }

        _ = Task.Run(async () =>
        {
            await _lastFmPushGate.WaitAsync().ConfigureAwait(false);
            try
            {
                LastFmLedgerService.RecordLocalPlaybackEvent(
                    manifestDbPath: manifestDb ?? "",
                    tonewiseDir: tonewiseDir,
                    trackUid: track.TrackUid,
                    artist: track.ArtistsDisplay,
                    title: track.Title,
                    album: track.Album,
                    playedAtUnixSec: playedAtSec,
                    scrobbleEligible: scrobbleEligible);

                var cfg = LastFmAccountConfig.Load(tonewiseDir);
                if (cfg is { HasWriteSession: true } && scrobbleEligible && !string.IsNullOrWhiteSpace(manifestDb))
                {
                    var client = new LastFmWebApiClient();
                    int pushed = await client.FlushPendingScrobblesAsync(manifestDb, tonewiseDir, cfg).ConfigureAwait(false);
                    if (pushed > 0 && _uiContext is not null)
                    {
                        _uiContext.Post(_ => LastFmLedgerStatus = $"Last.fm 实时同步（@{cfg.Username}）：已自动上报 {pushed} 条播放记录（最近：{track.Title}）", null);
                    }
                }
            }
            catch
            {
                // Keep queued in history.desktop.jsonl for 14-day retry
            }
            finally
            {
                _lastFmPushGate.Release();
            }
        });
    }

    private void SendNowPlayingInBackground(TrackItem track)
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot)) return;
        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        var cfg = LastFmAccountConfig.Load(tonewiseDir);
        if (cfg is not { HasWriteSession: true }) return;

        int durSec = track.DurationMs > 0 ? Math.Max(30, track.DurationMs / 1000) : 0;
        _ = Task.Run(async () =>
        {
            try
            {
                var client = new LastFmWebApiClient();
                await client.UpdateNowPlayingAsync(
                    cfg.ApiKey,
                    cfg.ApiSecret,
                    cfg.SessionKey,
                    track.ArtistsDisplay,
                    track.Title,
                    track.Album,
                    durSec).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal NowPlaying update
            }
        });
    }

    private void IncrementTrackPlayCountInMemory(string trackUid, long playedTsMs)
    {
        TrackItem? updated = null;
        for (int i = 0; i < _allTracks.Count; i++)
        {
            if (string.Equals(_allTracks[i].TrackUid, trackUid, StringComparison.Ordinal))
            {
                updated = _allTracks[i] with
                {
                    EffectivePlayCount = _allTracks[i].EffectivePlayCount + 1,
                    LastPlayedTsMs = Math.Max(_allTracks[i].LastPlayedTsMs, playedTsMs),
                };
                _allTracks[i] = updated;
                break;
            }
        }
        if (updated is null) return;

        for (int i = 0; i < PlaybackQueue.Count; i++)
        {
            if (string.Equals(PlaybackQueue[i].TrackUid, trackUid, StringComparison.Ordinal))
                PlaybackQueue[i] = updated;
        }
        for (int i = 0; i < FilteredTracks.Count; i++)
        {
            if (string.Equals(FilteredTracks[i].TrackUid, trackUid, StringComparison.Ordinal))
                FilteredTracks[i] = updated;
        }
        if (CurrentTrack is not null && string.Equals(CurrentTrack.TrackUid, trackUid, StringComparison.Ordinal))
            CurrentTrack = updated;
        if (InspectedTrack is not null && string.Equals(InspectedTrack.TrackUid, trackUid, StringComparison.Ordinal))
            InspectedTrack = updated;
    }

    [RelayCommand]
    public async Task ToggleCurrentTrackLovedAsync()
    {
        if (CurrentTrack is null) return;
        var target = CurrentTrack;
        bool nextLoved = !target.IsLoved;
        await ApplyLexiconTagsToTracksAsync([target], new Dictionary<string, string?>
        {
            ["rating"] = nextLoved ? "5" : "0",
        });
        Notify(nextLoved ? $"已喜爱「{target.Title}」（将同步至 Last.fm）" : $"已取消喜爱「{target.Title}」", "success");
    }

    public async void ToggleLovedForTracks(IReadOnlyList<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;
        bool nextLoved = !tracks[0].IsLoved;
        await ApplyLexiconTagsToTracksAsync(tracks, new Dictionary<string, string?>
        {
            ["rating"] = nextLoved ? "5" : "0",
        });
        Notify(nextLoved ? $"已标记 {tracks.Count} 首为喜爱" : $"已取消 {tracks.Count} 首喜爱标记", "success");
    }

    public void NavigateToCurrentTrackArtist()
    {
        if (CurrentTrack is null || string.IsNullOrWhiteSpace(CurrentTrack.ArtistsDisplay)) return;
        string primary = PlaybackScrobbleEvaluator.ExtractPrimaryArtist(CurrentTrack.ArtistsDisplay);
        FilterByCreditEntity(primary);
    }

    public void NavigateToCurrentTrackAlbum()
    {
        if (CurrentTrack is null || string.IsNullOrWhiteSpace(CurrentTrack.Album)) return;
        FilterByCreditEntity(CurrentTrack.Album);
    }

    private void RefreshCurrentFreezeCapsule()
    {
        var track = CurrentTrack;
        if (track is null)
        {
            HasFreezeCapsule = false;
            CurrentFreezeCapsuleLabel = "";
            return;
        }

        if (track.DjScene.Count > 0)
        {
            string sc = track.DjScene[0].Trim();
            string label = sc.ToLowerInvariant() switch
            {
                "focus" => "专注",
                "coding" => "编程",
                "relax" => "放松",
                "energy" => "活力",
                "pop" => "流行",
                _ => sc,
            };
            _currentFreezeToken = $"scene:{sc}";
            CurrentFreezeCapsuleLabel = IsDjFreezeActive ? $"❄ 已冻结 · {ActiveDjFreezeLabel}" : $"❄ 同场景：{label}";
            HasFreezeCapsule = true;
            return;
        }

        if (track.Moods.Count > 0)
        {
            string mood = track.Moods[0].Trim();
            _currentFreezeToken = $"mood:{mood}";
            CurrentFreezeCapsuleLabel = IsDjFreezeActive ? $"❄ 已冻结 · {ActiveDjFreezeLabel}" : $"❄ 同情绪：{mood}";
            HasFreezeCapsule = true;
            return;
        }

        if (!string.IsNullOrWhiteSpace(track.DjVocals) && !string.Equals(track.DjVocals, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            string vocals = track.DjVocals.Trim();
            string label = vocals.Equals("instrumental", StringComparison.OrdinalIgnoreCase) ? "纯器乐" : "含人声";
            _currentFreezeToken = $"vocals:{vocals}";
            CurrentFreezeCapsuleLabel = IsDjFreezeActive ? $"❄ 已冻结 · {ActiveDjFreezeLabel}" : $"❄ {label}";
            HasFreezeCapsule = true;
            return;
        }

        HasFreezeCapsule = IsDjFreezeActive;
        CurrentFreezeCapsuleLabel = IsDjFreezeActive ? $"❄ 已冻结 · {ActiveDjFreezeLabel}" : "";
    }

    [ObservableProperty]
    private string _activeDjFreezeLabel = "";

    [RelayCommand]
    public void ToggleDjFreeze()
    {
        if (IsDjFreezeActive && _djFreezeBackup is not null)
        {
            var backup = _djFreezeBackup.Value;
            _djFreezeBackup = null;
            IsDjFreezeActive = false;
            ActiveDjFreezeLabel = "";
            PlaybackQueueTitle = backup.QueueTitle;
            ReplaceQueueItems(backup.Queue);
            PreloadNextGaplessTrack();
            PersistPlaybackSessionSnapshot(force: true);
            RefreshCurrentFreezeCapsule();
            Notify("已退出氛围冻结，恢复原播放队列（当前播放不中断）。", "info");
            return;
        }

        if (CurrentTrack is null || string.IsNullOrWhiteSpace(_currentFreezeToken)) return;

        var parts = _currentFreezeToken.Split(':', 2);
        string kind = parts[0];
        string val = parts.Length > 1 ? parts[1] : "";

        var tailMovements = ResolveRemainingWorkMovementsAfter(CurrentTrack);
        var excludedUids = new HashSet<string>(StringComparer.Ordinal) { CurrentTrack.TrackUid };
        foreach (var tail in tailMovements) excludedUids.Add(tail.TrackUid);

        var rawMatches = _allTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FullPath)
                        && !excludedUids.Contains(t.TrackUid)
                        && (kind switch
                        {
                            "scene" => t.DjScene.Any(s => string.Equals(s.Trim(), val, StringComparison.OrdinalIgnoreCase)),
                            "mood" => t.Moods.Any(m => string.Equals(m.Trim(), val, StringComparison.OrdinalIgnoreCase)),
                            "vocals" => string.Equals(t.DjVocals?.Trim(), val, StringComparison.OrdinalIgnoreCase),
                            _ => false,
                        }))
            .ToList();

        if (rawMatches.Count == 0 && tailMovements.Count == 0)
        {
            Notify("未找到更多匹配该氛围标签的本地曲目。", "warning");
            return;
        }

        var matches = BuildWorkPreservingFreezeSample(rawMatches, maxTracks: 80);

        _djFreezeBackup = ([.. PlaybackQueue], PlaybackQueueTitle, _currentFreezeToken);
        var frozenQueue = new List<TrackItem>(1 + tailMovements.Count + matches.Count) { CurrentTrack };
        frozenQueue.AddRange(tailMovements);
        frozenQueue.AddRange(matches);

        IsDjFreezeActive = true;
        ActiveDjFreezeLabel = val;
        PlaybackQueueTitle = $"❄ 氛围冻结 · {val}";
        ReplaceQueueItems(frozenQueue);
        PreloadNextGaplessTrack();
        PersistPlaybackSessionSnapshot(force: true);
        RefreshCurrentFreezeCapsule();
        Notify($"已锁定氛围队列（共 {frozenQueue.Count} 首，当前播放不中断），再点即可恢复原队列。", "success");
    }

    private List<TrackItem> ResolveRemainingWorkMovementsAfter(TrackItem current)
    {
        string? curWorkKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
            current.WorkName,
            current.Album,
            WorkAwareShuffleEngine.BuildAlbumScope(current.Album, current.ConductorsDisplay));
        if (string.IsNullOrEmpty(curWorkKey)) return [];

        int qIdx = -1;
        for (int i = 0; i < PlaybackQueue.Count; i++)
        {
            if (string.Equals(PlaybackQueue[i].TrackUid, current.TrackUid, StringComparison.Ordinal))
            {
                qIdx = i;
                break;
            }
        }
        if (qIdx >= 0)
        {
            var queuedTail = new List<TrackItem>();
            for (int i = qIdx + 1; i < PlaybackQueue.Count; i++)
            {
                var cand = PlaybackQueue[i];
                string? k = WorkAwareShuffleEngine.BuildWorkBlockKey(
                    cand.WorkName,
                    cand.Album,
                    WorkAwareShuffleEngine.BuildAlbumScope(cand.Album, cand.ConductorsDisplay));
                if (!string.Equals(k, curWorkKey, StringComparison.OrdinalIgnoreCase)) break;
                if (!string.IsNullOrWhiteSpace(cand.FullPath)) queuedTail.Add(cand);
            }
            if (queuedTail.Count > 0) return queuedTail;
        }

        var orderedWork = _allTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FullPath)
                        && string.Equals(
                            WorkAwareShuffleEngine.BuildWorkBlockKey(
                                t.WorkName,
                                t.Album,
                                WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay)),
                            curWorkKey,
                            StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.DiscNumber > 0 ? t.DiscNumber : int.MaxValue)
            .ThenBy(t => t.MovementNumber is > 0 ? t.MovementNumber.Value : int.MaxValue)
            .ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue)
            .ThenBy(t => t.RelPath, Comparer<string>.Create(WorkAwareShuffleEngine.NaturalPathCompare))
            .ToList();
        int curInWork = orderedWork.FindIndex(t => string.Equals(t.TrackUid, current.TrackUid, StringComparison.Ordinal));
        return curInWork >= 0 && curInWork + 1 < orderedWork.Count
            ? orderedWork.GetRange(curInWork + 1, orderedWork.Count - curInWork - 1)
            : [];
    }

    private List<TrackItem> BuildWorkPreservingFreezeSample(List<TrackItem> rawMatches, int maxTracks)
    {
        if (rawMatches.Count <= 1) return rawMatches;
        var descriptors = rawMatches.Select(t => new ShuffleTrackDescriptor(
            t.TrackUid,
            t.Album,
            t.WorkName,
            t.MovementNumber is > 0 ? t.MovementNumber : null,
            t.RelPath,
            WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay),
            t.DiscNumber,
            t.TrackNumber)).ToList();
        int[] order = WorkAwareShuffleEngine.BuildWorkAwareOrder(descriptors, -1, _shuffleRandom);
        var picked = new List<TrackItem>(Math.Min(maxTracks, rawMatches.Count));

        int pos = 0;
        while (pos < order.Length && picked.Count < maxTracks)
        {
            var head = rawMatches[order[pos]];
            string? headWorkKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
                head.WorkName,
                head.Album,
                WorkAwareShuffleEngine.BuildAlbumScope(head.Album, head.ConductorsDisplay));
            int end = pos + 1;
            if (!string.IsNullOrEmpty(headWorkKey))
            {
                while (end < order.Length)
                {
                    var next = rawMatches[order[end]];
                    string? nextKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
                        next.WorkName,
                        next.Album,
                        WorkAwareShuffleEngine.BuildAlbumScope(next.Album, next.ConductorsDisplay));
                    if (!string.Equals(nextKey, headWorkKey, StringComparison.OrdinalIgnoreCase)) break;
                    end++;
                }
            }
            int blockSize = end - pos;
            if (picked.Count > 0 && picked.Count + blockSize > maxTracks) break;
            for (int i = pos; i < end; i++) picked.Add(rawMatches[order[i]]);
            pos = end;
        }
        return picked;
    }

    private void PersistPlaybackSessionSnapshot(bool force, bool synchronous = false)
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot)) return;
        var now = DateTime.UtcNow;
        if (!force && (now - _lastSessionSavedUtc).TotalSeconds < 4.5) return;
        _lastSessionSavedUtc = now;

        var uids = PlaybackQueue.Select(t => t.TrackUid).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (uids.Count == 0 && CurrentTrack is not null)
        {
            uids = [CurrentTrack.TrackUid];
        }
        if (uids.Count == 0) return;

        double posSec = _engine.CurrentFilePath is not null
            ? Math.Max(0.0, _engine.Position.TotalSeconds)
            : _pendingRestorePositionSec;

        var snap = new PlaybackSessionSnapshot(
            QueueTrackUids: uids,
            CurrentTrackUid: CurrentTrack?.TrackUid,
            PositionSeconds: posSec,
            QueueTitle: PlaybackQueueTitle,
            IsShuffled: IsShuffleEnabled,
            RepeatMode: RepeatMode,
            ActiveSlotIndex: ActiveSessionSlotIndex,
            SavedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        string root = _store.MusicRoot;
        if (synchronous)
        {
            try { PlaybackSessionStore.Save(root, snap); } catch { }
            return;
        }
        _ = Task.Run(() =>
        {
            try { PlaybackSessionStore.Save(root, snap); } catch { }
        });
    }

    private void RestoreSavedPlaybackSession(string musicRoot)
    {
        var snap = PlaybackSessionStore.Load(musicRoot);
        if (snap is null || _allTracks.Count == 0) return;

        var trackByUid = _allTracks.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
        var restoredQueue = new List<TrackItem>(snap.QueueTrackUids.Count);
        foreach (string uid in snap.QueueTrackUids)
        {
            if (trackByUid.TryGetValue(uid, out var item) && !string.IsNullOrWhiteSpace(item.FullPath))
            {
                restoredQueue.Add(item);
            }
        }
        if (restoredQueue.Count == 0) return;

        ReplaceQueueItems(restoredQueue);
        _unshuffledQueueSnapshot = [.. restoredQueue];
        PlaybackQueueTitle = string.IsNullOrWhiteSpace(snap.QueueTitle) ? "恢复的上次队列" : snap.QueueTitle;
        ActiveSessionSlotIndex = snap.ActiveSlotIndex;

        var curTrack = (!string.IsNullOrWhiteSpace(snap.CurrentTrackUid) && trackByUid.TryGetValue(snap.CurrentTrackUid, out var exact))
            ? exact
            : restoredQueue[0];

        CurrentTrack = curTrack;
        _pendingRestorePositionSec = Math.Max(0.0, snap.PositionSeconds);
        double totalSec = Math.Max(0.0, curTrack.DurationMs / 1000.0);
        PlaybackDurationSeconds = totalSec;
        PlaybackPositionSeconds = totalSec > 0 ? Math.Clamp(_pendingRestorePositionSec, 0.0, totalSec) : _pendingRestorePositionSec;
        PlaybackProgress = totalSec > 0 ? Math.Clamp(PlaybackPositionSeconds / totalSec, 0.0, 1.0) : 0.0;
        var pTs = TimeSpan.FromSeconds(PlaybackPositionSeconds);
        var dTs = TimeSpan.FromSeconds(totalSec);
        TimeDisplay = totalSec > 0
            ? $"{(int)pTs.TotalMinutes}:{pTs.Seconds:D2} / {(int)dTs.TotalMinutes}:{dTs.Seconds:D2}"
            : "";
        CurrentTrackSpecsSubline = $"{TimeDisplay} · 已恢复队列 ({restoredQueue.Count} 首 · 点击播放续播)";
        CurrentWaveform = _store.LoadWaveform(curTrack);
        InspectTrack(curTrack);
        WaveformUpdated?.Invoke();
        ReloadQueueSlotChips();
    }

    public void SaveCurrentToSessionSlot(int slotIndex)
    {
        if (string.IsNullOrWhiteSpace(_store.SessionsJsonPath)) return;
        IList<TrackItem> source = PlaybackQueue.Count > 0 ? PlaybackQueue : FilteredTracks;
        var uids = source.Select(t => t.TrackUid).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        int curIdx = CurrentTrack is null ? 0 : Math.Max(0, FindQueueIndex(source, CurrentTrack));
        ulong posMs = _engine.CurrentFilePath is not null
            ? (ulong)Math.Max(0, _engine.Position.TotalMilliseconds)
            : (ulong)Math.Max(0, _pendingRestorePositionSec * 1000.0);
        ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string existingName = SessionSlots.FirstOrDefault(s => s.SlotIndex == slotIndex)?.Name ?? $"槽位 {slotIndex}";
        string autoName = string.IsNullOrWhiteSpace(PlaybackQueueTitle) || PlaybackQueueTitle.StartsWith("队列未激活", StringComparison.Ordinal)
            ? existingName
            : PlaybackQueueTitle;
        var dto = new SessionSlotDto(slotIndex, autoName, FilterScene, nowMs, uids, curIdx, posMs);

        var updated = SessionSlotStore.SaveSlot(_store.SessionsJsonPath, dto);
        SessionSlots.Clear();
        foreach (var s in updated) SessionSlots.Add(s);
        ActiveSessionSlotIndex = slotIndex;
        ReloadQueueSlotChips();
        PersistPlaybackSessionSnapshot(force: true);
        StatusMessage = $"已保存 {uids.Count} 首到场景槽 #{slotIndex}（{autoName}）。";
    }

    public void QuickSwitchOrSaveSessionSlot(int slotIndex)
    {
        if (ActiveSessionSlotIndex > 0 && ActiveSessionSlotIndex != slotIndex && PlaybackQueue.Count > 0)
        {
            SaveCurrentToSessionSlot(ActiveSessionSlotIndex);
        }

        if (ActiveSessionSlotIndex == slotIndex)
        {
            SaveCurrentToSessionSlot(slotIndex);
            return;
        }

        var slot = SessionSlots.FirstOrDefault(s => s.SlotIndex == slotIndex);
        if (slot is null || slot.QueueTrackUids.Count == 0)
        {
            SaveCurrentToSessionSlot(slotIndex);
            return;
        }
        RestoreSessionSlot(slotIndex);
    }

    public void RestoreSessionSlot(int slotIndex)
    {
        var slot = SessionSlots.FirstOrDefault(s => s.SlotIndex == slotIndex);
        if (slot is null) return;
        if (!string.IsNullOrWhiteSpace(slot.SceneId) && slot.SceneId != "all")
        {
            FilterScene = slot.SceneId;
        }

        var byUid = _allTracks.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
        var resolvedQueue = slot.QueueTrackUids
            .Select(uid => byUid.TryGetValue(uid, out var tr) ? tr : null)
            .Where(tr => tr is not null && !string.IsNullOrWhiteSpace(tr.FullPath))
            .Cast<TrackItem>()
            .ToList();

        if (resolvedQueue.Count == 0)
        {
            Notify($"场景槽 #{slotIndex} 暂无可恢复的本地曲目。", "warning");
            return;
        }

        ActiveSessionSlotIndex = slotIndex;
        ReplaceQueueItems(resolvedQueue);
        _unshuffledQueueSnapshot = [.. resolvedQueue];
        PlaybackQueueTitle = string.IsNullOrWhiteSpace(slot.Name) ? $"场景槽 #{slotIndex}" : slot.Name;

        int targetIdx = Math.Clamp(slot.CurrentIndex, 0, resolvedQueue.Count - 1);
        var targetTrack = resolvedQueue[targetIdx];
        double frac = targetTrack.DurationMs > 0 ? slot.PositionMs / (double)targetTrack.DurationMs : 0.0;
        PlayTrack(targetTrack, frac);
        ReloadQueueSlotChips();
        StatusMessage = $"已切换到场景槽 #{slotIndex}（{PlaybackQueueTitle} · {resolvedQueue.Count} 首）。";
    }

    private void ReloadSessionSlots()
    {
        string path = _store.SessionsJsonPath ?? Path.Combine(Path.GetTempPath(), "tonewise_sessions_default.json");
        SessionSlots.Clear();
        foreach (var s in SessionSlotStore.LoadOrInitializeDefault(path))
        {
            SessionSlots.Add(s);
        }
        ReloadQueueSlotChips();
    }

    private void ReloadQueueSlotChips()
    {
        QueueSlotChips.Clear();
        for (int idx = 1; idx <= 5; idx++)
        {
            var slot = SessionSlots.FirstOrDefault(s => s.SlotIndex == idx);
            int count = slot?.QueueTrackUids.Count ?? 0;
            bool populated = count > 0;
            bool active = ActiveSessionSlotIndex == idx;
            string label = populated ? $"#{idx} ({count})" : $"+ #{idx}";
            string tip = populated
                ? $"#{idx} {slot!.Name}（{count} 首）— 点击切换（同号点击覆写保存）"
                : $"空槽位 #{idx} — 点击保存当前队列";
            QueueSlotChips.Add(new QueueSlotChipItem(idx, label, tip, active, populated));
        }
    }

    private void CancelPendingPlaybackRecovery(bool releasePowerKeepAlive = false)
    {
        _isAutoRecoveringPlayback = false;
        _pendingEndpointReturnInterruption = null;
        _pendingEndpointReturnDeadlineUtc = DateTime.MinValue;
        var cts = Interlocked.Exchange(ref _playbackRecoveryCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }
        if (releasePowerKeepAlive)
        {
            _engine.ReleasePowerKeepAlive();
        }
    }

    private bool PruneAndCheckRecoveryRateLimit()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(30);
        while (_recentRecoveryTimestampsUtc.Count > 0 && _recentRecoveryTimestampsUtc.Peek() < cutoff)
        {
            _recentRecoveryTimestampsUtc.Dequeue();
        }
        return _recentRecoveryTimestampsUtc.Count < 4;
    }

    private static string FormatPlaybackTimestamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private void HandlePlaybackInterrupted(AudioPlaybackInterruption interruption)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        double posSec = Math.Max(0.0, interruption.Position.TotalSeconds);
        _pendingRestorePositionSec = posSec;
        PersistPlaybackSessionSnapshot(force: false);

        var track = CurrentTrack;
        bool trackMatches = track is not null
            && !string.IsNullOrWhiteSpace(interruption.FilePath)
            && string.Equals(track.FullPath, interruption.FilePath, StringComparison.OrdinalIgnoreCase)
            && LongPathHelper.FileExists(track.FullPath);

        if (!trackMatches)
        {
            CancelPendingPlaybackRecovery(releasePowerKeepAlive: true);
            IsPlaying = false;
            _lastProgressSampleUtc = null;
            _pausedSinceUtc = DateTime.UtcNow;
            RefreshEndpoints(handleLostSelection: false);
            StatusMessage = $"音频输出中断：{interruption.Reason}";
            return;
        }

        if (!PruneAndCheckRecoveryRateLimit())
        {
            CancelPendingPlaybackRecovery(releasePowerKeepAlive: true);
            IsPlaying = false;
            _lastProgressSampleUtc = null;
            _pausedSinceUtc = DateTime.UtcNow;
            PersistPlaybackSessionSnapshot(force: true);
            StatusMessage = $"音频输出连续中断，已保留进度 {FormatPlaybackTimestamp(posSec)} 暂停（点击播放续播）：{interruption.Reason}";
            return;
        }

        CancelPendingPlaybackRecovery(releasePowerKeepAlive: false);
        var cts = new CancellationTokenSource();
        _playbackRecoveryCts = cts;
        _isAutoRecoveringPlayback = true;
        _pendingEndpointReturnInterruption = null;
        _pendingEndpointReturnDeadlineUtc = DateTime.MinValue;
        StatusMessage = $"检测到系统音频切换或中断，正在重连输出设备（进度 {FormatPlaybackTimestamp(posSec)}）...";
        _ = RunBoundedPlaybackRecoveryAsync(track!, interruption, cts, cts.Token);
    }

    private async Task RunBoundedPlaybackRecoveryAsync(
        TrackItem track,
        AudioPlaybackInterruption interruption,
        CancellationTokenSource ownedCts,
        CancellationToken ct)
    {
        int[] backoffMs = [350, 1000, 2200];
        Exception? lastException = null;
        try
        {
            for (int attempt = 0; attempt < backoffMs.Length; attempt++)
            {
                await Task.Delay(backoffMs[attempt], ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
                if (CurrentTrack is null || !string.Equals(CurrentTrack.TrackUid, track.TrackUid, StringComparison.Ordinal)) return;
                if (!LongPathHelper.FileExists(track.FullPath)) break;

                RefreshEndpoints(handleLostSelection: false);
                if (OutputEndpoints.Count == 0)
                {
                    continue;
                }

                PushEnginePreferences();
                bool allowExclusive = !(attempt == 2 && interruption.WasExclusive && PreferWasapiExclusive);
                try
                {
                    _engine.PlayFile(
                        track.FullPath,
                        initialPosition: interruption.Position > TimeSpan.Zero ? interruption.Position : null,
                        startPaused: false,
                        allowExclusive: allowExclusive);

                    if (ReferenceEquals(_playbackRecoveryCts, ownedCts))
                    {
                        _playbackRecoveryCts = null;
                    }
                    _isAutoRecoveringPlayback = false;
                    _pendingEndpointReturnInterruption = null;
                    _pendingEndpointReturnDeadlineUtc = DateTime.MinValue;
                    _recentRecoveryTimestampsUtc.Enqueue(DateTime.UtcNow);
                    IsPlaying = true;
                    _lastProgressSampleUtc = DateTime.UtcNow;
                    _pausedSinceUtc = null;
                    PreloadNextGaplessTrack();
                    PersistPlaybackSessionSnapshot(force: false);
                    StatusMessage = allowExclusive
                        ? $"已自动重连音频输出并从 {FormatPlaybackTimestamp(interruption.Position.TotalSeconds)} 续播：{track.Title}"
                        : $"系统锁屏/息屏暂不支持独占输出，已临时以 WASAPI 共享从 {FormatPlaybackTimestamp(interruption.Position.TotalSeconds)} 续播：{track.Title}";
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
            if (ReferenceEquals(_playbackRecoveryCts, ownedCts))
            {
                _playbackRecoveryCts = null;
            }
            _isAutoRecoveringPlayback = false;
            IsPlaying = false;
            _lastProgressSampleUtc = null;
            _pausedSinceUtc = DateTime.UtcNow;
            _pendingRestorePositionSec = Math.Max(0.0, interruption.Position.TotalSeconds);
            _pendingEndpointReturnInterruption = interruption;
            _pendingEndpointReturnDeadlineUtc = DateTime.UtcNow.AddSeconds(60);
            _engine.ReleasePowerKeepAlive();
            PersistPlaybackSessionSnapshot(force: true);
            string detail = lastException?.Message ?? interruption.Reason;
            StatusMessage = $"音频输出中断（已保留进度 {FormatPlaybackTimestamp(_pendingRestorePositionSec)}，设备恢复或点击播放可续播）：{detail}";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_playbackRecoveryCts, ownedCts))
            {
                _isAutoRecoveringPlayback = false;
                _playbackRecoveryCts = null;
            }
        }
        finally
        {
            ownedCts.Dispose();
        }
    }

    private void TryResumeAfterEndpointReturn()
    {
        var pending = _pendingEndpointReturnInterruption;
        _pendingEndpointReturnInterruption = null;
        _pendingEndpointReturnDeadlineUtc = DateTime.MinValue;
        if (pending is null || Volatile.Read(ref _disposed) != 0 || _isAutoRecoveringPlayback) return;

        var track = CurrentTrack;
        if (track is null
            || string.IsNullOrWhiteSpace(pending.FilePath)
            || !string.Equals(track.FullPath, pending.FilePath, StringComparison.OrdinalIgnoreCase)
            || !LongPathHelper.FileExists(track.FullPath))
        {
            return;
        }

        if (!PruneAndCheckRecoveryRateLimit()) return;

        try
        {
            PushEnginePreferences();
            _engine.PlayFile(
                track.FullPath,
                initialPosition: pending.Position > TimeSpan.Zero ? pending.Position : null,
                startPaused: false,
                allowExclusive: true);
            _recentRecoveryTimestampsUtc.Enqueue(DateTime.UtcNow);
            IsPlaying = true;
            _lastProgressSampleUtc = DateTime.UtcNow;
            _pausedSinceUtc = null;
            PreloadNextGaplessTrack();
            PersistPlaybackSessionSnapshot(force: false);
            StatusMessage = $"输出设备已恢复，已从 {FormatPlaybackTimestamp(pending.Position.TotalSeconds)} 自动续播：{track.Title}";
        }
        catch
        {
            IsPlaying = false;
            _lastProgressSampleUtc = null;
            _pausedSinceUtc = DateTime.UtcNow;
            _engine.ReleasePowerKeepAlive();
        }
    }

}
