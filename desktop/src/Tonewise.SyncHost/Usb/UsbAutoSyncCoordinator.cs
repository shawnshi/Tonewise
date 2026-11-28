namespace Tonewise.SyncHost.Usb;

public sealed record UsbConnectedPhone(
    string Serial,
    string State,
    string Model,
    string? PackageName
);

public sealed record UsbCoordinatorState(
    bool AdbAvailable,
    string? AdbPath,
    string AdbStatusText,
    IReadOnlyList<UsbConnectedPhone> ConnectedDevices,
    string? ActiveSerial,
    bool IsSyncing,
    double ProgressPercent,
    string ProgressDetail,
    string? PendingLargePruneDeviceId,
    int PendingLargePruneCount,
    int LocalAudioCount,
    double PendingLargePruneRatio
);

public sealed class UsbAutoSyncCoordinator : IAsyncDisposable
{
    private readonly IUsbAdbRunner _adb;
    private readonly Func<CancellationToken, Task<SyncHostServer?>> _ensureSyncHostAsync;
    private readonly Func<SyncHostServer?> _getActiveSyncHost;
    private readonly TimeSpan _autoTriggerDebounce;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _triggerLock = new(1, 1);
    private readonly SemaphoreSlim _fallbackLock = new(1, 1);

    private readonly HashSet<string> _autoTriggeredSerials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _pendingDebounceCts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Model, string? PackageName)> _deviceMetaCache = new(StringComparer.Ordinal);

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private CancellationTokenSource? _fallbackCts;
    private SyncHostServer? _subscribedHost;
    private UsbCoordinatorState _state;
    private bool _disposed;

    public bool AutoSyncEnabled { get; set; } = true;
    public bool MirrorPruneEnabled { get; set; } = false;
    public string PruneScope { get; set; } = "managed_only";
    public bool AllowAdbForeignDeleteFallback { get; set; } = false;

    public event Action<UsbCoordinatorState>? StateChanged;

    public UsbCoordinatorState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public UsbAutoSyncCoordinator(
        IUsbAdbRunner adb,
        Func<CancellationToken, Task<SyncHostServer?>> ensureSyncHostAsync,
        Func<SyncHostServer?> getActiveSyncHost,
        TimeSpan? autoTriggerDebounce = null)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _ensureSyncHostAsync = ensureSyncHostAsync ?? throw new ArgumentNullException(nameof(ensureSyncHostAsync));
        _getActiveSyncHost = getActiveSyncHost ?? throw new ArgumentNullException(nameof(getActiveSyncHost));
        _autoTriggerDebounce = autoTriggerDebounce ?? TimeSpan.FromMilliseconds(1500);

        string? initialAdb = _adb.ResolveAdbPath();
        _state = new UsbCoordinatorState(
            AdbAvailable: initialAdb is not null,
            AdbPath: initialAdb,
            AdbStatusText: initialAdb is not null ? "USB 监听待命" : "未检测到 adb.exe（请安装 Android Platform-Tools 或放入 tools\\adb.exe）",
            ConnectedDevices: [],
            ActiveSerial: null,
            IsSyncing: false,
            ProgressPercent: 0.0,
            ProgressDetail: "",
            PendingLargePruneDeviceId: null,
            PendingLargePruneCount: 0,
            LocalAudioCount: 0,
            PendingLargePruneRatio: 0.0);
    }

    public void RefreshAdbAvailability()
    {
        string? resolved = _adb.ResolveAdbPath();
        UpdateState(s => s with
        {
            AdbAvailable = resolved is not null,
            AdbPath = resolved,
        });
    }

    public void AttachHostEvents(SyncHostServer? host)
    {
        if (host is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_subscribedHost, host)) return;
            if (_subscribedHost is not null)
            {
                _subscribedHost.SyncSessionProgressChanged -= OnHostSyncSessionProgressChanged;
                _subscribedHost.UsbDirectPushHandler = null;
            }
            _subscribedHost = host;
            host.SyncSessionProgressChanged += OnHostSyncSessionProgressChanged;
            host.UsbDirectPushHandler = HandleUsbDirectPushAsync;
        }
    }

    public void StartMonitoring()
    {
        lock (_gate)
        {
            if (_disposed || _monitorCts is not null) return;
            _monitorCts = new CancellationTokenSource();
            var ct = _monitorCts.Token;
            AttachHostEvents(_getActiveSyncHost());
            _monitorTask = Task.Run(() => _adb.TrackDevicesAsync(OnDevicesChanged, OnAdbStatusLog, ct), CancellationToken.None);
        }
    }

    public async Task StopMonitoringAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        List<CancellationTokenSource> debounces;
        lock (_gate)
        {
            cts = _monitorCts;
            _monitorCts = null;
            task = _monitorTask;
            _monitorTask = null;
            debounces = _pendingDebounceCts.Values.ToList();
            _pendingDebounceCts.Clear();
        }

        foreach (var d in debounces)
        {
            try { d.Cancel(); d.Dispose(); } catch { }
        }
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch
                {
                    // Non-blocking shutdown
                }
            }
            cts.Dispose();
        }
    }

    private void OnAdbStatusLog(string text)
    {
        string? adbPath = _adb.ResolveAdbPath();
        UpdateState(s => s with
        {
            AdbAvailable = adbPath is not null,
            AdbPath = adbPath,
            AdbStatusText = text,
        });
    }

    private void OnDevicesChanged(IReadOnlyList<AdbDeviceInfo> rawDevices)
    {
        var readySerials = rawDevices
            .Where(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Serial)
            .ToHashSet(StringComparer.Ordinal);

        var toSchedule = new List<string>();
        var toCancel = new List<CancellationTokenSource>();
        var connected = new List<UsbConnectedPhone>(rawDevices.Count);
        var needMetaLookup = new List<string>();

        lock (_gate)
        {
            _autoTriggeredSerials.RemoveWhere(s => !readySerials.Contains(s));

            foreach (string pendingSerial in _pendingDebounceCts.Keys.ToList())
            {
                if (!readySerials.Contains(pendingSerial))
                {
                    toCancel.Add(_pendingDebounceCts[pendingSerial]);
                    _pendingDebounceCts.Remove(pendingSerial);
                }
            }

            foreach (var raw in rawDevices)
            {
                _deviceMetaCache.TryGetValue(raw.Serial, out var meta);
                connected.Add(new UsbConnectedPhone(
                    Serial: raw.Serial,
                    State: raw.State,
                    Model: string.IsNullOrWhiteSpace(meta.Model) ? raw.Serial : meta.Model,
                    PackageName: meta.PackageName));

                if (string.Equals(raw.State, "device", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_deviceMetaCache.ContainsKey(raw.Serial))
                    {
                        needMetaLookup.Add(raw.Serial);
                    }
                    if (AutoSyncEnabled
                        && !_autoTriggeredSerials.Contains(raw.Serial)
                        && !_pendingDebounceCts.ContainsKey(raw.Serial))
                    {
                        var debounceCts = new CancellationTokenSource();
                        _pendingDebounceCts[raw.Serial] = debounceCts;
                        toSchedule.Add(raw.Serial);
                    }
                }
            }

            string? adbPath = _adb.ResolveAdbPath();
            string statusText = FormatConnectedStatus(connected, adbPath is not null);
            string? activeSerial = _state.ActiveSerial is not null && readySerials.Contains(_state.ActiveSerial)
                ? _state.ActiveSerial
                : connected.FirstOrDefault(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase))?.Serial;

            _state = _state with
            {
                AdbAvailable = adbPath is not null,
                AdbPath = adbPath,
                AdbStatusText = statusText,
                ConnectedDevices = connected,
                ActiveSerial = activeSerial,
            };
        }

        foreach (var c in toCancel)
        {
            try { c.Cancel(); c.Dispose(); } catch { }
        }

        NotifyStateChanged();

        foreach (string serial in needMetaLookup)
        {
            _ = PopulateDeviceMetadataAsync(serial);
        }

        foreach (string serial in toSchedule)
        {
            CancellationToken token;
            lock (_gate)
            {
                if (!_pendingDebounceCts.TryGetValue(serial, out var cts))
                    continue;
                token = cts.Token;
            }
            _ = DebounceAndAutoTriggerAsync(serial, token);
        }
    }

    private async Task PopulateDeviceMetadataAsync(string serial)
    {
        try
        {
            string? model = await _adb.GetDeviceModelAsync(serial, CancellationToken.None).ConfigureAwait(false);
            string? pkg = await _adb.DetectTonewisePackageAsync(serial, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _deviceMetaCache[serial] = (model ?? serial, pkg);
                var updated = _state.ConnectedDevices
                    .Select(d => string.Equals(d.Serial, serial, StringComparison.Ordinal)
                        ? d with { Model = model ?? serial, PackageName = pkg }
                        : d)
                    .ToList();
                _state = _state with
                {
                    ConnectedDevices = updated,
                    AdbStatusText = FormatConnectedStatus(updated, _state.AdbAvailable),
                };
            }
            NotifyStateChanged();
        }
        catch
        {
            // Non-fatal metadata enrichment
        }
    }

    private async Task DebounceAndAutoTriggerAsync(string serial, CancellationToken ct)
    {
        try
        {
            if (_autoTriggerDebounce > TimeSpan.Zero)
            {
                await Task.Delay(_autoTriggerDebounce, ct).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (_pendingDebounceCts.TryGetValue(serial, out var cts))
                {
                    _pendingDebounceCts.Remove(serial);
                    cts.Dispose();
                }

                if (_disposed || !AutoSyncEnabled)
                    return;

                bool stillReady = _state.ConnectedDevices.Any(d =>
                    string.Equals(d.Serial, serial, StringComparison.Ordinal)
                    && string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase));
                if (!stillReady || !_autoTriggeredSerials.Add(serial))
                    return;
            }

            await TriggerSyncAsync(serial, isAuto: true, pruneConfirmed: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Debounce cancelled because device detached or coordinator stopped
        }
    }

    public async Task<bool> TriggerSyncAsync(
        string? serial = null,
        bool isAuto = false,
        bool pruneConfirmed = false,
        CancellationToken ct = default)
    {
        await _triggerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? targetSerial;
            string? cachedPkg = null;
            string? pendingLargeDeviceId;
            lock (_gate)
            {
                var readyList = _state.ConnectedDevices
                    .Where(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var chosen = !string.IsNullOrWhiteSpace(serial)
                    ? readyList.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal))
                    : readyList.FirstOrDefault();
                targetSerial = chosen?.Serial;
                cachedPkg = chosen?.PackageName;
                pendingLargeDeviceId = _state.PendingLargePruneDeviceId;
            }

            if (string.IsNullOrWhiteSpace(targetSerial))
            {
                UpdateState(s => s with
                {
                    IsSyncing = false,
                    ProgressDetail = "未检测到已授权 USB 调试的手机",
                });
                return false;
            }

            var host = await _ensureSyncHostAsync(ct).ConfigureAwait(false);
            if (host is null || !host.IsRunning || host.ListeningPort <= 0)
            {
                UpdateState(s => s with
                {
                    IsSyncing = false,
                    ProgressDetail = "SyncHost 服务未启动，请先打开本地曲库",
                });
                return false;
            }

            AttachHostEvents(host);

            var existingPolicy = host.Store.GetSyncPolicy();
            host.Store.SetSyncPolicy(existingPolicy with
            {
                MirrorPruneEnabled = MirrorPruneEnabled,
                PruneScope = PruneScope,
                TargetRoot = "Music",
                AllowAdbForeignDeleteFallback = AllowAdbForeignDeleteFallback,
            });

            if (MirrorPruneEnabled)
            {
                host.Store.PromoteCopyItemsToSubscribe();
            }

            host.ClearCancelActiveSync();
            if (pruneConfirmed && !string.IsNullOrWhiteSpace(pendingLargeDeviceId))
            {
                host.ConfirmLargePruneForDevice(pendingLargeDeviceId);
            }

            string? pkg = cachedPkg ?? await _adb.DetectTonewisePackageAsync(targetSerial, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pkg))
            {
                UpdateState(s => s with
                {
                    ActiveSerial = targetSerial,
                    IsSyncing = false,
                    ProgressDetail = "手机未安装 Tonewise 应用",
                });
                return false;
            }

            lock (_gate)
            {
                if (_deviceMetaCache.TryGetValue(targetSerial, out var prevMeta))
                    _deviceMetaCache[targetSerial] = (prevMeta.Model, pkg);
                else
                    _deviceMetaCache[targetSerial] = (targetSerial, pkg);
            }

            bool tunnelOk = await _adb.SetupReverseTunnelAsync(targetSerial, 45820, host.ListeningPort, ct).ConfigureAwait(false);
            if (!tunnelOk)
            {
                UpdateState(s => s with
                {
                    ActiveSerial = targetSerial,
                    IsSyncing = false,
                    ProgressDetail = "ADB reverse 端口映射失败，请检查 USB 连接",
                });
                return false;
            }

            string nonce = host.IssueUsbSyncNonce(targetSerial, allowAutoPair: true, pruneConfirmed: pruneConfirmed);
            UpdateState(s => s with
            {
                ActiveSerial = targetSerial,
                IsSyncing = true,
                ProgressPercent = 0.0,
                ProgressDetail = isAuto ? "已检测到 USB 连接，正在自动触发双向同步..." : "正在唤醒手机执行 USB 双向同步...",
                PendingLargePruneDeviceId = pruneConfirmed ? null : s.PendingLargePruneDeviceId,
                PendingLargePruneCount = pruneConfirmed ? 0 : s.PendingLargePruneCount,
                LocalAudioCount = pruneConfirmed ? 0 : s.LocalAudioCount,
                PendingLargePruneRatio = pruneConfirmed ? 0.0 : s.PendingLargePruneRatio,
            });

            var (ok, detail) = await _adb.TriggerPhoneUsbSyncAsync(
                targetSerial,
                pkg,
                45820,
                nonce,
                host.Store.HostId,
                allowAutoPair: true,
                ct).ConfigureAwait(false);

            if (!ok)
            {
                UpdateState(s => s with
                {
                    IsSyncing = false,
                    ProgressDetail = $"唤醒手机同步失败：{detail}",
                });
                return false;
            }

            return true;
        }
        finally
        {
            _triggerLock.Release();
        }
    }

    public void RequestCancelActiveSync()
    {
        _getActiveSyncHost()?.RequestCancelActiveSync();
        lock (_gate)
        {
            try { _fallbackCts?.Cancel(); } catch { }
        }
        UpdateState(s => s with
        {
            ProgressDetail = "已请求取消同步（等待手机当前分片结束）...",
        });
    }

    public void DismissLargePruneConfirmation()
    {
        var host = _getActiveSyncHost();
        string? devId = CurrentState.PendingLargePruneDeviceId;
        if (!string.IsNullOrWhiteSpace(devId))
        {
            host?.ClearLargePruneConfirmation(devId);
        }
        UpdateState(s => s with
        {
            PendingLargePruneDeviceId = null,
            PendingLargePruneCount = 0,
            LocalAudioCount = 0,
            PendingLargePruneRatio = 0.0,
        });
    }

    private void OnHostSyncSessionProgressChanged(SyncSessionProgressReport report)
    {
        if (!string.IsNullOrWhiteSpace(report.Transport)
            && !string.Equals(report.Transport, "usb", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (report.PendingLargePruneCount > 0 && !report.IsRunning)
        {
            UpdateState(s => s with
            {
                IsSyncing = false,
                ProgressPercent = 0.0,
                ProgressDetail = $"大比例删歌确认：手机 Music 目录拟删除 {report.PendingLargePruneCount} / {report.LocalAudioCount} 首（{report.PendingLargePruneRatio:P0}）",
                PendingLargePruneDeviceId = report.DeviceId,
                PendingLargePruneCount = report.PendingLargePruneCount,
                LocalAudioCount = report.LocalAudioCount,
                PendingLargePruneRatio = report.PendingLargePruneRatio,
            });
            return;
        }

        if (report.IsRunning)
        {
            double pct = report.TotalBytes > 0
                ? Math.Clamp(report.DownloadedBytes * 100.0 / report.TotalBytes, 0.0, 100.0)
                : (report.TotalTracks > 0
                    ? Math.Clamp(report.CompletedTracks * 100.0 / report.TotalTracks, 0.0, 100.0)
                    : 0.0);
            string detail = string.IsNullOrWhiteSpace(report.CurrentTrackTitle)
                ? (report.Phase ?? "正在同步...")
                : $"{report.Phase} · {report.CurrentTrackTitle}";
            UpdateState(s => s with
            {
                IsSyncing = true,
                ProgressPercent = pct,
                ProgressDetail = detail,
            });
            return;
        }

        string summary = !string.IsNullOrWhiteSpace(report.ErrorMessage)
            ? report.ErrorMessage!
            : $"同步完成：新增 {report.AddedCount} · 更新 {report.UpdatedCount} · 跳过 {report.SkippedCount} · 删除 {report.DeletedCount}";

        UpdateState(s => s with
        {
            IsSyncing = false,
            ProgressPercent = string.IsNullOrWhiteSpace(report.ErrorMessage) && report.Completed ? 100.0 : 0.0,
            ProgressDetail = summary,
            PendingLargePruneDeviceId = null,
            PendingLargePruneCount = 0,
            LocalAudioCount = 0,
            PendingLargePruneRatio = 0.0,
        });

        if (report.BlockedForeignPaths is { Count: > 0 }
            && AllowAdbForeignDeleteFallback
            && string.Equals(report.Transport, "usb", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunAdbForeignFallbackAsync(report);
        }
    }

    private async Task<UsbDirectPushSummary> HandleUsbDirectPushAsync(
        UsbPushTracksApiRequest request,
        Func<UsbDirectPushProgress, Task> onProgress,
        CancellationToken ct)
    {
        SyncHostServer? host = _getActiveSyncHost();
        string? serial;
        lock (_gate)
        {
            var snap = _state;
            var ready = snap.ConnectedDevices
                .Where(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var chosen = snap.ActiveSerial is not null
                ? ready.FirstOrDefault(d => string.Equals(d.Serial, snap.ActiveSerial, StringComparison.Ordinal))
                : null;
            chosen ??= ready.FirstOrDefault();
            serial = chosen?.Serial;
        }

        if (host is null || string.IsNullOrWhiteSpace(serial))
        {
            return new UsbDirectPushSummary(
                UsedDirectPush: false,
                PushedTracks: [],
                FailedTrackUids: request.Tracks.Select(t => t.TrackUid).ToArray(),
                CoversPushed: 0,
                CoversSkipped: 0,
                TotalBytesTransferred: 0,
                ElapsedSeconds: 0,
                AverageMbps: 0,
                Error: host is null ? "sync_host_unavailable" : "no_ready_usb_device");
        }

        var trackSpecs = new List<UsbDirectPushTrackSpec>(request.Tracks.Count);
        var unresolvedUids = new List<string>();
        foreach (UsbPushTrackRequestItem item in request.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (!host.Store.TryResolveTrackPhysicalPath(item.TrackUid, out string fullPath, out string storeRelPath))
            {
                unresolvedUids.Add(item.TrackUid);
                continue;
            }

            string? chosenRel = UsbAdbBridge.TryNormalizeSafeAudioRelPath(item.RelPath, out string clientRel)
                ? clientRel
                : (UsbAdbBridge.TryNormalizeSafeAudioRelPath(storeRelPath, out string fallbackRel) ? fallbackRel : null);
            if (chosenRel is null)
            {
                unresolvedUids.Add(item.TrackUid);
                continue;
            }

            trackSpecs.Add(new UsbDirectPushTrackSpec(
                item.TrackUid,
                chosenRel,
                fullPath,
                item.FileSize,
                item.MtimeMs));
        }

        var coverSpecs = new List<UsbDirectPushCoverSpec>(request.Covers?.Count ?? 0);
        if (request.Covers is { Count: > 0 })
        {
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UsbPushCoverRequestItem coverItem in request.Covers)
            {
                ct.ThrowIfCancellationRequested();
                string albumDir = coverItem.AlbumDir?.Trim().Replace('\\', '/').Trim('/') ?? string.Empty;
                if (!string.IsNullOrEmpty(albumDir) &&
                    !UsbAdbBridge.TryNormalizeSafeSubDirRelPath(albumDir, out albumDir))
                {
                    continue;
                }
                if (!seenDirs.Add(albumDir))
                    continue;

                if (host.Store.TryResolveTrackCoverPath(coverItem.SampleTrackUid, out string coverPath))
                {
                    coverSpecs.Add(new UsbDirectPushCoverSpec(albumDir, coverPath));
                }
            }
        }

        UsbDirectPushSummary summary = await _adb.PushFilesDirectAsync(
            serial,
            trackSpecs,
            coverSpecs,
            string.IsNullOrWhiteSpace(request.RemoteMusicRoot)
                ? UsbAdbSyncClient.DefaultRemoteMusicRoot
                : request.RemoteMusicRoot,
            async p =>
            {
                double pct = p.TotalBytes > 0
                    ? Math.Clamp(p.BytesTransferred * 100.0 / p.TotalBytes, 0.0, 100.0)
                    : (p.TotalTracks > 0
                        ? Math.Clamp(p.CompletedTracks * 100.0 / p.TotalTracks, 0.0, 100.0)
                        : 0.0);
                UpdateState(s => s with
                {
                    IsSyncing = true,
                    ProgressPercent = pct,
                    ProgressDetail = $"USB 直传 · {p.CompletedTracks}/{p.TotalTracks} ({p.SpeedMbps:F1} MB/s)",
                });
                await onProgress(p).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        if (unresolvedUids.Count == 0)
            return summary;

        string[] mergedFailed = summary.FailedTrackUids
            .Concat(unresolvedUids)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return summary with { FailedTrackUids = mergedFailed };
    }

    private async Task RunAdbForeignFallbackAsync(SyncSessionProgressReport report)
    {
        await _fallbackLock.WaitAsync().ConfigureAwait(false);
        CancellationTokenSource? localCts = null;
        try
        {
            CancellationToken fallbackCt;
            lock (_gate)
            {
                try { _fallbackCts?.Dispose(); } catch { }
                localCts = new CancellationTokenSource();
                _fallbackCts = localCts;
                fallbackCt = localCts.Token;
            }

            string? serial;
            string? pkg = null;
            lock (_gate)
            {
                var ready = _state.ConnectedDevices
                    .Where(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var chosen = _state.ActiveSerial is not null
                    ? ready.FirstOrDefault(d => string.Equals(d.Serial, _state.ActiveSerial, StringComparison.Ordinal))
                    : ready.FirstOrDefault();
                serial = chosen?.Serial;
                pkg = chosen?.PackageName;
            }

            var host = _getActiveSyncHost();
            if (string.IsNullOrWhiteSpace(serial) || host is null || report.BlockedForeignPaths is null)
                return;

            var outbox = host.Store.ResolveOutbox(report.DeviceId);
            if (outbox.SyncPolicy is not { MirrorPruneEnabled: true, OutboxAuthoritative: true }
                || outbox.Tracks.Count == 0)
            {
                return;
            }

            var desiredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in outbox.Tracks)
            {
                if (UsbAdbBridge.TryNormalizeSafeAudioRelPath(t.TargetRelPath, out string normTarget))
                {
                    desiredSet.Add(normTarget);
                }
            }

            var safePaths = UsbAdbBridge.FilterSafeForeignMusicRelPaths(report.BlockedForeignPaths, desiredSet);
            if (safePaths.Count == 0)
                return;

            UpdateState(s => s with
            {
                ProgressDetail = $"{s.ProgressDetail} · 正在通过 USB 补删未授权旧曲（{safePaths.Count} 首）...",
            });

            int adbDeleted = await _adb.DeleteForeignMusicFilesAsync(serial, safePaths, fallbackCt).ConfigureAwait(false);
            if (adbDeleted > 0)
            {
                pkg ??= await _adb.DetectTonewisePackageAsync(serial, fallbackCt).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(pkg))
                {
                    string rescanNonce = host.IssueUsbSyncNonce(serial, allowAutoPair: false);
                    await _adb.TriggerPhoneLibraryRescanAsync(serial, pkg, rescanNonce, fallbackCt).ConfigureAwait(false);
                }

                UpdateState(s => s with
                {
                    ProgressDetail = $"{s.ProgressDetail}（USB 补删未授权旧曲 {adbDeleted} 首）",
                });
            }
        }
        catch (OperationCanceledException)
        {
            UpdateState(s => s with
            {
                ProgressDetail = $"{s.ProgressDetail}（USB 补删已取消）",
            });
        }
        catch
        {
            // Non-fatal fallback deletion guard
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_fallbackCts, localCts))
                {
                    _fallbackCts = null;
                }
            }
            try { localCts?.Dispose(); } catch { }
            _fallbackLock.Release();
        }
    }

    private static string FormatConnectedStatus(IReadOnlyList<UsbConnectedPhone> devices, bool adbAvailable)
    {
        if (!adbAvailable)
            return "未检测到 adb.exe（请安装 Android Platform-Tools 或放入 tools\\adb.exe）";
        if (devices.Count == 0)
            return "未检测到 USB 连接手机（开启手机「USB 调试」后插线即可自动同步）";

        var ready = devices.FirstOrDefault(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase));
        if (ready is not null)
        {
            string pkgHint = string.IsNullOrWhiteSpace(ready.PackageName) ? "" : $" · {ready.PackageName}";
            return $"已就绪：{ready.Model} ({ready.Serial}{pkgHint})";
        }

        var unauth = devices.FirstOrDefault(d => string.Equals(d.State, "unauthorized", StringComparison.OrdinalIgnoreCase));
        if (unauth is not null)
        {
            return $"等待手机授权（{unauth.Serial}）：请在手机屏幕勾选并允许「USB 调试」";
        }

        var first = devices[0];
        return $"USB 设备状态：{first.Serial} ({first.State})";
    }

    private void UpdateState(Func<UsbCoordinatorState, UsbCoordinatorState> updater)
    {
        lock (_gate)
        {
            _state = updater(_state);
        }
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        UsbCoordinatorState snapshot;
        lock (_gate)
        {
            snapshot = _state;
        }
        StateChanged?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _fallbackCts?.Cancel(); } catch { }
            try { _fallbackCts?.Dispose(); } catch { }
            _fallbackCts = null;
            if (_subscribedHost is not null)
            {
                _subscribedHost.SyncSessionProgressChanged -= OnHostSyncSessionProgressChanged;
                _subscribedHost.UsbDirectPushHandler = null;
                _subscribedHost = null;
            }
        }
        await StopMonitoringAsync().ConfigureAwait(false);
        _triggerLock.Dispose();
        _fallbackLock.Dispose();
    }
}
