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
    private async Task AutoSyncLastFmOnLibraryLoadAsync()
    {
        try
        {
            await SyncLastFmLedgerInternalAsync(silentIfOffline: true);
        }
        catch
        {
            // Non-fatal background startup sync guard
        }
    }

    public LastFmAccountConfig? GetSavedLastFmConfig()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot)) return null;
        return LastFmAccountConfig.Load(Path.Combine(_store.MusicRoot, ".tonewise"));
    }

    public void RefreshLastFmAuthStatus()
    {
        var cfg = GetSavedLastFmConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.ApiKey) || string.IsNullOrWhiteSpace(cfg.Username))
        {
            HasLastFmWriteSession = false;
            LastFmAuthModeBadge = "未配置（本地记录保留 14 天待补传）";
            return;
        }
        if (cfg.HasWriteSession)
        {
            HasLastFmWriteSession = true;
            LastFmAuthModeBadge = $"双向读写已授权（@{cfg.Username} · 实时 NowPlaying / Scrobble / Loved 自动推送）";
            return;
        }
        HasLastFmWriteSession = false;
        LastFmAuthModeBadge = $"仅只读拉取（@{cfg.Username} · 缺少 SessionKey，无法上报播放/红心，请使用浏览器授权）";
    }

    public async Task SaveLastFmSessionAndSyncAsync(
        string apiKey,
        string apiSecret,
        string username,
        string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot) || string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            LastFmLedgerStatus = "请先打开曲库。";
            Notify(LastFmLedgerStatus, "warning");
            return;
        }

        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        var existing = LastFmAccountConfig.Load(tonewiseDir);
        var config = new LastFmAccountConfig(
            apiKey.Trim(),
            apiSecret.Trim(),
            username.Trim(),
            sessionKey.Trim(),
            existing?.LastPullUnixSec ?? 0);
        LastFmAccountConfig.Save(tonewiseDir, config);
        RefreshLastFmAuthStatus();
        await SyncLastFmLedgerInternalAsync(silentIfOffline: false);
    }

    public async Task ConfigureAndSyncLastFmAsync(
        string apiKey,
        string apiSecret,
        string username,
        string passwordOrSessionKey,
        bool isRawSessionKey = false)
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot) || string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            LastFmLedgerStatus = "请先打开曲库。";
            Notify(LastFmLedgerStatus, "warning");
            return;
        }

        string cleanKey = apiKey.Trim();
        string cleanSecret = apiSecret.Trim();
        string cleanUser = username.Trim();
        string cleanPass = passwordOrSessionKey.Trim();
        if (string.IsNullOrWhiteSpace(cleanKey) || string.IsNullOrWhiteSpace(cleanUser))
        {
            Notify("请填写 Last.fm API Key 与用户名。", "warning");
            return;
        }

        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        var existing = LastFmAccountConfig.Load(tonewiseDir);
        var client = new LastFmWebApiClient();
        string sessionKey;
        if (isRawSessionKey && !string.IsNullOrWhiteSpace(cleanPass))
        {
            sessionKey = cleanPass;
        }
        else if (!string.IsNullOrWhiteSpace(cleanPass) && !string.IsNullOrWhiteSpace(cleanSecret))
        {
            sessionKey = await client.AuthenticateMobileSessionAsync(cleanKey, cleanSecret, cleanUser, cleanPass);
        }
        else
        {
            sessionKey = existing?.SessionKey ?? "";
        }

        var config = new LastFmAccountConfig(cleanKey, cleanSecret, cleanUser, sessionKey, existing?.LastPullUnixSec ?? 0);
        LastFmAccountConfig.Save(tonewiseDir, config);
        RefreshLastFmAuthStatus();

        await SyncLastFmLedgerInternalAsync(silentIfOffline: false);
    }

    [RelayCommand]
    private Task SyncLastFmLedgerAsync() => SyncLastFmLedgerInternalAsync(silentIfOffline: false);

    [RelayCommand]
    private async Task BackfillLocalLovedToLastFmAsync()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot) || string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            Notify("请先打开曲库。", "warning");
            return;
        }

        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        var cfg = LastFmAccountConfig.Load(tonewiseDir);
        if (cfg is null || !cfg.HasWriteSession)
        {
            Notify("当前未获得 Last.fm 写入授权（缺少 SessionKey），请先在「配置 Last.fm 账号」中使用浏览器授权。", "warning");
            return;
        }

        string dbPath = _store.ManifestDbPath;
        int enqueued = await Task.Run(() => LastFmLedgerService.EnqueueAllLocalFiveStarAsLoved(dbPath, tonewiseDir));
        if (enqueued == 0)
        {
            Notify("本地曲库暂无 5 星喜爱曲目需要回推。", "info");
            return;
        }

        BeginBusy($"正在将 {enqueued} 首本地 5 星曲目推送至 Last.fm Loved tracks...");
        try
        {
            var client = new LastFmWebApiClient();
            var (loved, unloved) = await client.FlushPendingLoveOpsAsync(tonewiseDir, cfg);
            LastFmLedgerStatus = $"Last.fm 喜爱回推完成（@{cfg.Username}）：已推送 Loved {loved} 首（取消 {unloved} 首）。";
            Notify(LastFmLedgerStatus, "success");
        }
        catch (Exception ex)
        {
            LastFmLedgerStatus = $"Last.fm 喜爱回推中断（剩余待传已保留在队列）：{ex.Message}";
            Notify(LastFmLedgerStatus, "warning");
        }
        finally
        {
            EndBusy();
        }
    }

    private void FlushPendingLoveOpsInBackground(string musicRoot)
    {
        string tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        var cfg = LastFmAccountConfig.Load(tonewiseDir);
        if (cfg is not { HasWriteSession: true }) return;

        _ = Task.Run(async () =>
        {
            await _lastFmPushGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = new LastFmWebApiClient();
                var (loved, unloved) = await client.FlushPendingLoveOpsAsync(tonewiseDir, cfg).ConfigureAwait(false);
                if ((loved > 0 || unloved > 0) && _uiContext is not null)
                {
                    _uiContext.Post(_ => LastFmLedgerStatus = $"Last.fm 实时同步（@{cfg.Username}）：已自动推送 Loved +{loved} / Unloved -{unloved}", null);
                }
            }
            catch
            {
                // Retain pending ops in lastfm_loved_outbox.json for next retry
            }
            finally
            {
                _lastFmPushGate.Release();
            }
        });
    }

    private async Task SyncLastFmLedgerInternalAsync(bool silentIfOffline)
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot) || string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            LastFmLedgerStatus = "请先打开曲库。";
            return;
        }

        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        RefreshLastFmAuthStatus();
        var cfg = LastFmAccountConfig.Load(tonewiseDir);
        if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.ApiKey) && !string.IsNullOrWhiteSpace(cfg.Username))
        {
            try
            {
                LastFmLedgerStatus = $"正在与 Last.fm 同步（@{cfg.Username}）…";
                var client = new LastFmWebApiClient();
                var summary = await client.RunFullBidirectionalSyncAsync(_store.ManifestDbPath, tonewiseDir, cfg);
                string lovePushNote = (summary.PushedLoved > 0 || summary.PushedUnloved > 0)
                    ? $"、推送喜爱 +{summary.PushedLoved}/-{summary.PushedUnloved}"
                    : "";
                string readOnlyHint = cfg.HasWriteSession ? "" : "（当前为只读模式：需浏览器授权才可自动推送）";
                LastFmLedgerStatus =
                    $"Last.fm 已同步（@{cfg.Username}）：拉取播放次数 {summary.TopPlayCountsUpdated} 首、近期记录 {summary.PulledCloudPlays} 次，更新喜爱 {summary.LovedTracksUpdated} 首，上报 {summary.PushedScrobbles} 条{lovePushNote}，归档 {summary.ExpiredArchivedCount} 条{readOnlyHint}。";
                string musicRoot = _store.MusicRoot;
                _allTracks = await Task.Run(() => _store.LoadFromMusicRoot(musicRoot).ToList());
                if (CurrentTrack is not null)
                {
                    var curRefreshed = _allTracks.FirstOrDefault(t => string.Equals(t.TrackUid, CurrentTrack.TrackUid, StringComparison.Ordinal));
                    if (curRefreshed is not null) CurrentTrack = curRefreshed;
                }
                TriggerFilterRefresh();
                if (!silentIfOffline)
                {
                    Notify(LastFmLedgerStatus, "success");
                }
                return;
            }
            catch (Exception ex)
            {
                LastFmLedgerStatus = $"Last.fm 云端同步失败，已回退本地记录：{ex.Message}";
                if (!silentIfOffline)
                {
                    Notify(LastFmLedgerStatus, "warning");
                }
            }
        }

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var prep = LastFmLedgerService.PrepareScrobbleBatches(_store.ManifestDbPath, tonewiseDir, nowSec);
        int queued = prep.Batches.Sum(b => b.Items.Count);
        LastFmLedgerStatus = $"Last.fm 本地记录：待上报 {queued} 条，去重 {prep.DeduplicatedCount} 条，超过 14 天归档 {prep.ExpiredCount} 条。";

        if (!silentIfOffline)
        {
            string musicRoot = _store.MusicRoot;
            _allTracks = await Task.Run(() => _store.LoadFromMusicRoot(musicRoot).ToList());
            TriggerFilterRefresh();
        }
    }

    private SyncOutboxAndPairingStore? EnsureSyncStore()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot)) return null;
        return _syncStore ??= new SyncOutboxAndPairingStore(_store.MusicRoot);
    }

    [RelayCommand]
    private async Task ToggleLanSyncHostAsync()
    {
        var existing = Interlocked.Exchange(ref _syncHost, null);
        if (existing is not null)
        {
            StopPairingCodeRefreshTimer();
            existing.SyncStateChanged -= OnSyncHostStateChanged;
            await existing.DisposeAsync();
            LanSyncStatus = "局域网同步服务：未开启";
            LanHostUrlsText = "服务启动后将显示本机局域网地址（端口 45820）";
            LanPairingCode = "------";
            return;
        }
        await EnsureLanSyncHostRunningAsync();
    }

    public async Task<bool> EnsureLanSyncHostRunningAsync()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            Notify("请先打开曲库，再开启局域网同步服务。", "warning");
            return false;
        }
        if (_syncHost is { IsRunning: true })
        {
            StartPairingCodeRefreshTimer();
            RefreshLanSyncUiState();
            return true;
        }

        try
        {
            var store = EnsureSyncStore()!;
            var host = new SyncHostServer(_store.MusicRoot, store: store);
            host.SyncStateChanged += OnSyncHostStateChanged;
            int port = await host.StartAsync(45820, enableMdns: true);
            _syncHost = host;
            var urls = SyncOutboxAndPairingStore.GetLocalLanUrls(port);
            LanHostUrlsText = urls.Count > 0 ? string.Join("   |   ", urls) : $"http://127.0.0.1:{port}";
            LanPairingCode = store.CurrentPairingCode;
            LanSyncStatus = $"局域网同步服务：已开启，端口 {port}（mDNS _tonewise._tcp）";
            StartPairingCodeRefreshTimer();
            RefreshLanSyncUiState();
            return true;
        }
        catch (Exception ex)
        {
            LanSyncStatus = $"局域网同步服务启动失败：{ex.Message}";
            Notify(LanSyncStatus, "error");
            return false;
        }
    }

    private void StartPairingCodeRefreshTimer()
    {
        var timer = new System.Threading.Timer(_ =>
        {
            if (_syncHost is not { IsRunning: true }) return;
            var store = _syncStore;
            if (store is null) return;
            string currentCode = store.CurrentPairingCode;
            if (string.Equals(LanPairingCode, currentCode, StringComparison.Ordinal)) return;
            if (_uiContext is not null)
                _uiContext.Post(_ =>
                {
                    if (_syncHost is { IsRunning: true })
                        LanPairingCode = currentCode;
                }, null);
            else
                LanPairingCode = currentCode;
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        Interlocked.Exchange(ref _pairingCodeRefreshTimer, timer)?.Dispose();
    }

    private void StopPairingCodeRefreshTimer()
    {
        Interlocked.Exchange(ref _pairingCodeRefreshTimer, null)?.Dispose();
    }

    private async Task RestartOrDisposeSyncHostAsync(
        SyncHostServer oldHost,
        string newMusicRoot,
        SyncOutboxAndPairingStore newStore,
        bool restart)
    {
        try
        {
            oldHost.SyncStateChanged -= OnSyncHostStateChanged;
            await oldHost.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown errors when switching music roots
        }

        if (!restart || !string.Equals(_store.MusicRoot, newMusicRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        void PostUi(Action action)
        {
            if (_uiContext is not null) _uiContext.Post(_ => action(), null);
            else action();
        }

        try
        {
            var host = new SyncHostServer(newMusicRoot, store: newStore);
            host.SyncStateChanged += OnSyncHostStateChanged;
            int port = await host.StartAsync(45820, enableMdns: true).ConfigureAwait(false);
            if (!string.Equals(_store.MusicRoot, newMusicRoot, StringComparison.OrdinalIgnoreCase))
            {
                host.SyncStateChanged -= OnSyncHostStateChanged;
                await host.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _syncHost = host;
            var urls = SyncOutboxAndPairingStore.GetLocalLanUrls(port);
            PostUi(() =>
            {
                LanHostUrlsText = urls.Count > 0 ? string.Join("   |   ", urls) : $"http://127.0.0.1:{port}";
                LanPairingCode = newStore.CurrentPairingCode;
                LanSyncStatus = $"局域网同步服务：已开启，端口 {port}（mDNS _tonewise._tcp）";
                StartPairingCodeRefreshTimer();
                RefreshLanSyncUiState();
            });
        }
        catch (Exception ex)
        {
            PostUi(() =>
            {
                LanSyncStatus = $"局域网同步服务重启失败：{ex.Message}";
                LanPairingCode = "------";
            });
        }
    }

    private void OnSyncHostStateChanged()
    {
        async void ApplyAsync()
        {
            RefreshLanSyncUiState();
            ReloadSessionSlots();
            string? musicRoot = _store.MusicRoot;
            if (!string.IsNullOrWhiteSpace(musicRoot) && LongPathHelper.DirectoryExists(musicRoot))
            {
                try
                {
                    var refreshed = await Task.Run(() => _store.LoadFromMusicRoot(musicRoot).ToList());
                    if (string.Equals(_store.MusicRoot, musicRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        _allTracks = refreshed;
                        if (CurrentTrack is not null)
                        {
                            var updatedCur = _allTracks.FirstOrDefault(t => string.Equals(t.TrackUid, CurrentTrack.TrackUid, StringComparison.Ordinal));
                            if (updatedCur is not null) CurrentTrack = updatedCur;
                        }
                        if (InspectedTrack is not null)
                        {
                            var updatedSel = _allTracks.FirstOrDefault(t => string.Equals(t.TrackUid, InspectedTrack.TrackUid, StringComparison.Ordinal));
                            if (updatedSel is not null) InspectTrack(updatedSel);
                        }
                        UpdateClassicalAdaptiveState();
                        TriggerFilterRefresh();
                        RefreshPlaylists();
                        RefreshLastFmAuthStatus();
                    }
                }
                catch
                {
                    RefreshPlaylists();
                }
            }
            else
            {
                RefreshPlaylists();
            }
        }

        if (_uiContext is not null)
            _uiContext.Post(_ => ApplyAsync(), null);
        else
            ApplyAsync();
    }

    public void RefreshLanSyncUiState()
    {
        var store = EnsureSyncStore();
        if (store is null)
        {
            PairedDevices.Clear();
            SyncOutboxItems.Clear();
            HasPairedDevices = false;
            HasLanOutboxItems = false;
            LanOutboxSummaryText = "请先打开曲库";
            return;
        }

        if (_syncHost is { IsRunning: true })
        {
            LanPairingCode = store.CurrentPairingCode;
            var urls = SyncOutboxAndPairingStore.GetLocalLanUrls(_syncHost.ListeningPort);
            LanHostUrlsText = urls.Count > 0 ? string.Join("   |   ", urls) : $"http://127.0.0.1:{_syncHost.ListeningPort}";
        }

        PairedDevices.Clear();
        var devices = store.GetPairedDevices();
        foreach (var d in devices)
        {
            string syncText = d.LastSyncMs > 0
                ? $"最近同步：{DateTimeOffset.FromUnixTimeMilliseconds(d.LastSyncMs).LocalDateTime:MM-dd HH:mm}"
                : $"已配对（{DateTimeOffset.FromUnixTimeMilliseconds(d.PairedAtMs).LocalDateTime:MM-dd HH:mm} · 尚未同步）";
            PairedDevices.Add(new PairedDeviceViewItem(d.DeviceId, d.DeviceName, syncText));
        }
        HasPairedDevices = PairedDevices.Count > 0;

        SyncOutboxItems.Clear();
        var items = store.GetOutboxItems();
        foreach (var item in items)
        {
            string kindLabel = item.Kind.ToLowerInvariant() switch
            {
                "playlist" => "歌单",
                "genre" => "流派",
                "artist" => "艺人",
                "tracks" => "曲目",
                _ => item.Kind,
            };
            string modeBadge = string.Equals(item.Mode, "subscribe", StringComparison.OrdinalIgnoreCase) ? "订阅" : "单次";
            string detail = item.TrackUids.Count > 0
                ? $"{item.TrackUids.Count} 首 · 添加于 {DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAtMs).LocalDateTime:MM-dd HH:mm}"
                : $"添加于 {DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAtMs).LocalDateTime:MM-dd HH:mm}";
            SyncOutboxItems.Add(new SyncOutboxViewItem(item.Id, kindLabel, item.DisplayName, modeBadge, detail));
        }
        HasLanOutboxItems = SyncOutboxItems.Count > 0;

        if (items.Count == 0)
        {
            LanOutboxSummaryText = "发送队列为空（可在曲目表、歌单或流派/艺人卡片右键「发送到手机」）";
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var summary = store.GetOutboxSummary();
                string mbText = $"{summary.TotalBytes / (1024.0 * 1024.0):F1} MB";
                string dupText = summary.DuplicateTrackCount > 0 ? $"（已去重 {summary.DuplicateTrackCount} 首）" : "";
                string text = $"待同步：{summary.ItemCount} 项 · 解析后共 {summary.TrackCount:N0} 首曲目{dupText} · {mbText}";
                if (_uiContext is not null)
                    _uiContext.Post(_ => LanOutboxSummaryText = text, null);
                else
                    LanOutboxSummaryText = text;
            }
            catch
            {
                // Ignore background outbox summary errors
            }
        });
    }

    [RelayCommand]
    private void RegenerateLanPairingCode()
    {
        var store = EnsureSyncStore();
        if (store is null)
        {
            Notify("请先打开曲库。", "warning");
            return;
        }
        LanPairingCode = store.RegeneratePairingCode();
        Notify($"已刷新 6 位配对码：{LanPairingCode}", "info");
    }

    public void UnpairLanDevice(string deviceId)
    {
        var store = EnsureSyncStore();
        if (store is not null && store.UnpairDevice(deviceId))
        {
            RefreshLanSyncUiState();
            Notify("已解除该手机设备的配对授权。", "info");
        }
    }

    public void RemoveLanOutboxItem(string id)
    {
        var store = EnsureSyncStore();
        if (store is not null && store.RemoveOutboxItem(id))
        {
            RefreshLanSyncUiState();
        }
    }

    [RelayCommand]
    private void ClearLanOutbox()
    {
        var store = EnsureSyncStore();
        if (store is null) return;
        store.ClearOutbox();
        RefreshLanSyncUiState();
        Notify("已清空手机同步发送队列。", "info");
    }

    public async Task SendTracksToPhoneAsync(IReadOnlyList<TrackItem> tracks)
    {
        var uids = tracks.Select(t => t.TrackUid).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (uids.Count == 0)
        {
            Notify("请先选择要发送到手机的曲目。", "warning");
            return;
        }
        var store = EnsureSyncStore();
        if (store is null)
        {
            Notify("请先打开曲库。", "warning");
            return;
        }
        string label = tracks.Count == 1 ? $"{tracks[0].ArtistsDisplay} - {tracks[0].Title}".Trim(' ', '-') : $"自选 {uids.Count} 首曲目";
        store.EnqueueTracks(uids, label, mode: "copy");
        await EnsureLanSyncHostRunningAsync();
        var summary = await Task.Run(() => store.GetOutboxSummary());
        RefreshLanSyncUiState();
        Notify($"已将「{label}」加入手机同步队列（单次 · 共 {summary.TrackCount} 首 · {summary.TotalBytes / (1024.0 * 1024.0):F1} MB）", "success");
    }

    public Task SendPlaylistToPhoneAsync(PlaylistSummary? playlist) =>
        SendPlaylistsToPhoneAsync(playlist is null ? [] : [playlist]);

    public async Task SendPlaylistsToPhoneAsync(IReadOnlyList<PlaylistSummary> playlists)
    {
        var targets = (playlists ?? [])
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0 && SelectedPlaylist is not null)
        {
            targets.Add(SelectedPlaylist);
        }

        if (targets.Count == 0)
        {
            Notify("请先选择要发送的歌单。", "warning");
            return;
        }

        var store = EnsureSyncStore();
        if (store is null)
        {
            Notify("请先打开曲库。", "warning");
            return;
        }

        store.EnqueuePlaylists(targets.Select(p => p.Name), mode: "subscribe");
        await EnsureLanSyncHostRunningAsync();
        var summary = await Task.Run(() => store.GetOutboxSummary());
        RefreshLanSyncUiState();

        string dedupNote = summary.DuplicateTrackCount > 0 ? $" · 已合并重复 {summary.DuplicateTrackCount} 首" : "";
        string label = targets.Count == 1
            ? $"歌单「{targets[0].Name}」"
            : $"{targets.Count} 个歌单（{string.Join("、", targets.Take(3).Select(p => p.Name))}{(targets.Count > 3 ? " 等" : "")}）";
        Notify($"已将{label}加入手机同步队列（订阅 · 共 {summary.TrackCount} 首{dedupNote} · {summary.TotalBytes / (1024.0 * 1024.0):F1} MB）", "success");
    }

    public async Task SendEntityGroupToPhoneAsync(EntityGroupItem? group)
    {
        if (group is null)
        {
            Notify("请先选择一个分组卡片。", "warning");
            return;
        }
        var store = EnsureSyncStore();
        if (store is null)
        {
            Notify("请先打开曲库。", "warning");
            return;
        }

        string modeBadge;
        if ((PrimaryNavId, SecondarySubTab) == ("library", "genres"))
        {
            store.EnqueueGenre(group.Key, mode: "subscribe");
            modeBadge = "订阅流派";
        }
        else if (PrimaryNavId == "artists" || (PrimaryNavId, SecondarySubTab) is ("classical", "composers") or ("classical", "conductors") or ("classical", "ensembles"))
        {
            store.EnqueueArtist(group.Key, mode: "subscribe");
            modeBadge = "订阅艺人";
        }
        else
        {
            var uids = group.Tracks.Select(t => t.TrackUid).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
            store.EnqueueTracks(uids, $"{group.Subtitle}：{group.Title}", mode: "copy");
            modeBadge = "单次专辑/分组";
        }

        await EnsureLanSyncHostRunningAsync();
        var summary = await Task.Run(() => store.GetOutboxSummary());
        RefreshLanSyncUiState();
        Notify($"已将「{group.Title}」加入手机同步队列（{modeBadge} · 队列共 {summary.TrackCount} 首 · {summary.TotalBytes / (1024.0 * 1024.0):F1} MB）", "success");
    }

}
