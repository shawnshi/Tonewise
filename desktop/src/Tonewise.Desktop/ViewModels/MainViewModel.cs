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

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibraryStore _store = new();
    private readonly TonewiseAudioEngine _engine = new();
    private readonly SynchronizationContext? _uiContext;
    private SyncHostServer? _syncHost;
    private SyncOutboxAndPairingStore? _syncStore;
    private LibraryFileWatcherService? _libraryWatcher;
    private List<TrackItem> _allTracks = [];
    private EqualizerApoProfile? _eqProfile;
    private bool _suppressFilterRefresh;
    private string? _settingsFilePath;
    private bool _settingsPersistenceEnabled;
    private bool _suppressSettingsSave;
    private string? _activeEqFilePath;
    private string? _firImpulseFilePath;
    private int _totalPendingStagingCount;

    public BulkObservableCollection<TrackItem> FilteredTracks { get; } = [];
    public ObservableCollection<TrackItem> PlaybackQueue { get; } = [];
    public ObservableCollection<AudioOutputEndpoint> OutputEndpoints { get; } = [];
    public ObservableCollection<TagDiffProposal> StagingProposals { get; } = [];
    public BulkObservableCollection<StagingProposalViewItem> StagingProposalItems { get; } = [];
    public ObservableCollection<TrackItem> AlternateRecordings { get; } = [];
    public ObservableCollection<SessionSlotDto> SessionSlots { get; } = [];
    public ObservableCollection<QueueSlotChipItem> QueueSlotChips { get; } = [];
    public ObservableCollection<PlaylistSummary> Playlists { get; } = [];
    public ObservableCollection<SubTabOption> CurrentSubTabs { get; } = [];
    public BulkObservableCollection<EntityGroupItem> CurrentEntityGroups { get; } = [];
    public ObservableCollection<string> SearchSuggestions { get; } = [];
    public ObservableCollection<string> BreadcrumbItems { get; } = [];
    public ObservableCollection<PairedDeviceViewItem> PairedDevices { get; } = [];
    public ObservableCollection<SyncOutboxViewItem> SyncOutboxItems { get; } = [];

    [ObservableProperty]
    private string _lastFmAuthModeBadge = "未配置 Last.fm";

    [ObservableProperty]
    private bool _hasLastFmWriteSession;

    [ObservableProperty]
    private bool _currentTrackLoved;

    [ObservableProperty]
    private bool _hasCurrentTrack;

    [ObservableProperty]
    private string _replayGainMode = "dynamic";

    [ObservableProperty]
    private double _replayGainPreAmpWithTagDb;

    [ObservableProperty]
    private double _replayGainPreAmpWithoutTagDb;

    [ObservableProperty]
    private string _dspPresetId = "Flat";

    [ObservableProperty]
    private bool _isDspEnabled;

    [ObservableProperty]
    private bool _isGainMatchedBypass;

    [ObservableProperty]
    private bool _isCrossfeedEnabled;

    [ObservableProperty]
    private double _crossfeedCutoffHz = 700.0;

    [ObservableProperty]
    private double _crossfeedFeedDb = 4.5;

    [ObservableProperty]
    private double _dspBalanceDb;

    [ObservableProperty]
    private bool _isMonoFoldEnabled;

    [ObservableProperty]
    private bool _isLimiterEnabled = true;

    [ObservableProperty]
    private double _dspPreampDb;

    [ObservableProperty]
    private double _eqBand0Db;

    [ObservableProperty]
    private double _eqBand1Db;

    [ObservableProperty]
    private double _eqBand2Db;

    [ObservableProperty]
    private double _eqBand3Db;

    [ObservableProperty]
    private double _eqBand4Db;

    [ObservableProperty]
    private double _eqBand5Db;

    [ObservableProperty]
    private double _eqBand6Db;

    [ObservableProperty]
    private double _eqBand7Db;

    [ObservableProperty]
    private double _eqBand8Db;

    [ObservableProperty]
    private double _eqBand9Db;

    [ObservableProperty]
    private bool _rewindOnPrevious = true;

    [ObservableProperty]
    private bool _rememberPauseOnSkip;

    [ObservableProperty]
    private bool _pauseOnSingleRepeat;

    [ObservableProperty]
    private int _exclusiveIdleReleaseMinutes = 15;

    [ObservableProperty]
    private bool _isDjFreezeActive;

    [ObservableProperty]
    private string _currentFreezeCapsuleLabel = "";

    [ObservableProperty]
    private bool _hasFreezeCapsule;

    [ObservableProperty]
    private int _activeSessionSlotIndex;

    [ObservableProperty]
    private PlaylistSummary? _selectedPlaylist;

    [ObservableProperty]
    private bool _isViewingPlaylist;

    [ObservableProperty]
    private string _currentViewHeader = "全量曲目表 (0)";

    [ObservableProperty]
    private string _playbackQueueTitle = "队列未激活";

    [ObservableProperty]
    private TrackItem? _inspectedTrack;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _libraryNavMode = "all";

    [ObservableProperty]
    private string _activeFilterPillsSummary = "全量曲库 · 无附加过滤";

    [ObservableProperty]
    private string _signalPurityColorHex = "#10B981";

    [ObservableProperty]
    private string _signalPurityLabel = "64-Bit DSP";

    [ObservableProperty]
    private bool _isDacHardwareLocked;

    [ObservableProperty]
    private bool _isDacHardwareUnlocked = true;

    [ObservableProperty]
    private string _currentTrackSpecsSubline = "";

    [ObservableProperty]
    private string _inspectedComposerChip = "";

    [ObservableProperty]
    private string _inspectedConductorChip = "";

    [ObservableProperty]
    private string _inspectedPublisherChip = "";

    [ObservableProperty]
    private bool _hasInspectedComposer;

    [ObservableProperty]
    private bool _hasInspectedConductor;

    [ObservableProperty]
    private bool _hasInspectedPublisher;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private TrackItem? _currentTrack;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _playPauseLabel = "Play";

    [ObservableProperty]
    private bool _preferWasapiExclusive = true;

    [ObservableProperty]
    private AudioOutputEndpoint? _selectedEndpoint;

    [ObservableProperty]
    private string _filterScene = "all";

    [ObservableProperty]
    private string _filterEnergy = "all";

    [ObservableProperty]
    private string _filterVocals = "all";

    [ObservableProperty]
    private bool _excludeVocal;

    [ObservableProperty]
    private string _filterTempoBucket = "all";

    [ObservableProperty]
    private string _filterMoodCluster = "all";

    [ObservableProperty]
    private string _selectedEnergyArc = "flat";

    [ObservableProperty]
    private int _missingTempoCount;

    [ObservableProperty]
    private bool _hasMissingTempoBanner;

    [ObservableProperty]
    private string _missingMetadataBannerText = "";

    [ObservableProperty]
    private string _signalPathBadge = "Idle — Standby";

    [ObservableProperty]
    private string _signalPathSummary = "Idle — WASAPI Shared/Exclusive standby";

    [ObservableProperty]
    private string _activeEqSummary = "EQ: Flat (0 filters)";

    [ObservableProperty]
    private bool _isIso226Enabled;

    [ObservableProperty]
    private double _volumeAttenuationDb;

    [ObservableProperty]
    private bool _isTpdfDitherEnabled = true;

    [ObservableProperty]
    private string _firImpulseFileName = "FIR: Bypassed";

    [ObservableProperty]
    private double _firDryWetMix = 1.0;

    [ObservableProperty]
    private bool _hasSystemApoConflict;

    [ObservableProperty]
    private string _systemApoWarningText = "";

    [ObservableProperty]
    private float[]? _currentWaveform;

    [ObservableProperty]
    private double _playbackProgress;

    [ObservableProperty]
    private string _timeDisplay = "0:00 / 0:00";

    [ObservableProperty]
    private string _lanSyncStatus = "局域网同步服务：未开启";

    [ObservableProperty]
    private string _lanHostUrlsText = "服务启动后将显示本机局域网地址（端口 45820）";

    [ObservableProperty]
    private string _lanPairingCode = "------";

    [ObservableProperty]
    private string _lanOutboxSummaryText = "发送队列为空（在曲目、歌单、流派或艺人卡片上右键「发送到手机」）";

    [ObservableProperty]
    private bool _hasLanOutboxItems;

    [ObservableProperty]
    private bool _hasPairedDevices;

    [ObservableProperty]
    private bool _hasSelectedEntityGroup;

    [ObservableProperty]
    private string _lastFmLedgerStatus = "Last.fm 记录：空闲";

    [ObservableProperty]
    private string _stagingSummaryText = "待审队列：空";

    [ObservableProperty]
    private bool _hasStagingProposals;

    [ObservableProperty]
    private string? _lastCommittedBatchId;

    [ObservableProperty]
    private bool _hasAlternateRecordings;

    [ObservableProperty]
    private string _currentBiographySummary = "选择曲目后显示本地百科传记。";

    [ObservableProperty]
    private string? _currentBookletPath;

    [ObservableProperty]
    private bool _hasBookletPdf;

    [ObservableProperty]
    private EntityGroupItem? _selectedEntityGroup;

    [ObservableProperty]
    private string _primaryNavId = "library";

    [ObservableProperty]
    private string _secondarySubTab = "tracks";

    [ObservableProperty]
    private string _primarySectionTitle = "曲库";

    [ObservableProperty]
    private string _primaryViewSummary = "曲目 · 专辑 · 流派 · 文件夹";

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [ObservableProperty]
    private double _sidebarWidth = 244.0;

    [ObservableProperty]
    private bool _isInspectorOpen = true;

    /// <summary>"dark" (default design field) | "light" | "system".</summary>
    [ObservableProperty]
    private string _appTheme = "dark";

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private double _volumePercent = 100.0;

    [ObservableProperty]
    private string _volumeToolTip = "音量 100% (0.0 dB)";

    [ObservableProperty]
    private string _notificationMessage = "";

    /// <summary>"info" | "success" | "warning" | "error".</summary>
    [ObservableProperty]
    private string _notificationLevel = "info";

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    private double _busyProgressValue;

    [ObservableProperty]
    private bool _isBusyIndeterminate = true;

    [ObservableProperty]
    private bool _canCancelBusy;

    private CancellationTokenSource? _busyCts;
    private bool _syncingVolume;
    private string? _explicitNotificationLevel;

    public double PlaybackPositionSeconds { get; private set; }
    public double PlaybackDurationSeconds { get; private set; }

    [ObservableProperty]
    private double _classicalTrackRatio;

    [ObservableProperty]
    private string _classicalRatioText = "0.0% (0 / 0 首)";

    [ObservableProperty]
    private bool _alwaysExpandClassicalSubItems;

    [ObservableProperty]
    private bool _showClassicalSidebarSubItems;

    [ObservableProperty]
    private int _inboxAuditBadgeCount;

    [ObservableProperty]
    private string _inboxAuditBadgeText = "0";

    [ObservableProperty]
    private int _inboxAuditBadgeDisplayValue;

    [ObservableProperty]
    private bool _hasInboxAuditBadge;

    [ObservableProperty]
    private string _searchBoxText = "";

    [ObservableProperty]
    private string _musicRootDisplayPath = "尚未打开本地曲库目录";

    [ObservableProperty]
    private bool _isTrackTableVisible = true;

    [ObservableProperty]
    private bool _isEntityBrowserVisible;

    [ObservableProperty]
    private bool _isScenesDeckVisible;

    [ObservableProperty]
    private bool _isPlaylistsDeckVisible;

    [ObservableProperty]
    private bool _isInboxAuditDeckVisible;

    [ObservableProperty]
    private bool _isSyncScrobbleViewVisible;

    [ObservableProperty]
    private bool _isSettingsViewVisible;

    [ObservableProperty]
    private bool _isFiveDimFilterStripVisible = true;

    [ObservableProperty]
    private bool _isNavLibrary = true;

    [ObservableProperty]
    private bool _isNavArtists;

    [ObservableProperty]
    private bool _isNavClassical;

    [ObservableProperty]
    private bool _isNavClassicalWorks;

    [ObservableProperty]
    private bool _isNavClassicalComposers;

    [ObservableProperty]
    private bool _isNavClassicalConductors;

    [ObservableProperty]
    private bool _isNavScenes;

    [ObservableProperty]
    private bool _isNavPlaylists;

    [ObservableProperty]
    private bool _isNavInbox;

    [ObservableProperty]
    private bool _isNavSync;

    [ObservableProperty]
    private bool _isNavSettings;

    [ObservableProperty]
    private bool _isSyncLanSubTab = true;

    [ObservableProperty]
    private bool _isSyncLastFmSubTab;

    [ObservableProperty]
    private bool _isSyncUsbSubTab;

    [ObservableProperty]
    private bool _isSettingsAudioSubTab = true;

    [ObservableProperty]
    private bool _isSettingsStorageSubTab;

    [ObservableProperty]
    private string _storageOrganizeStatus = "按 <Album Artist>\\<Album>\\<Disc-Track#> <Title> 规范自动整理散落曲目、补齐碟轨号并收敛超长路径。";

    [ObservableProperty]
    private bool _enableAutoWatchLibrary = true;

    [ObservableProperty]
    private string _fileWatcherStatusText = "实时监测已开启：自动感知曲库内新增、移动、重命名或删除的音频文件与歌单。";

    partial void OnEnableAutoWatchLibraryChanged(bool value)
    {
        RestartLibraryFileWatcher(_store.MusicRoot);
        SaveUserSettings();
    }

    [ObservableProperty]
    private bool _isSettingsApisUiSubTab;

    [ObservableProperty]
    private bool _isScenesPresetSubTab = true;

    [ObservableProperty]
    private bool _isScenesAutoDjSubTab;

    [ObservableProperty]
    private string _autoDjSummaryText = "基于当前时段自动生成 50 首平滑混音歌单（70% 经常播放 + 30% 未播放/少播放，严控邻曲节奏与情绪过渡）";

    [ObservableProperty]
    private bool _isScenesStudioSubTab;

    [ObservableProperty]
    private bool _isScenesArcSubTab;

    [ObservableProperty]
    private bool _hasInspectedTrack;

    [ObservableProperty]
    private bool _hasNoInspectedTrack = true;

    [ObservableProperty]
    private string _inspectorHeaderTitle = "";

    [ObservableProperty]
    private string _inspectorHeaderSubtitle = "";

    [ObservableProperty]
    private string _inspectorHeaderSubline = "";

    [ObservableProperty]
    private bool _isMultiSelectMode;

    [ObservableProperty]
    private string _trackSortColumn = "";

    [ObservableProperty]
    private bool _trackSortAscending = true;

    [ObservableProperty]
    private string _headerTitleText = "曲目 / 专辑";

    [ObservableProperty]
    private string _headerArtistText = "艺人";

    [ObservableProperty]
    private string _headerComposerText = "作曲 / 作品 · 指挥";

    [ObservableProperty]
    private string _headerYearText = "年份";

    [ObservableProperty]
    private string _headerSceneText = "场景 · 情绪";

    [ObservableProperty]
    private string _headerEnergyText = "能量 · 节奏";

    [ObservableProperty]
    private string _headerPlayCountText = "播放";

    [ObservableProperty]
    private string _headerDurationText = "时长";

    [ObservableProperty]
    private bool _isFilterStripExpanded;

    [ObservableProperty]
    private bool _isFilterDrawerOpen;

    partial void OnIsFilterStripExpandedChanged(bool value) => UpdateFilterDrawerState();

    [ObservableProperty]
    private bool _scenesTempoNullPassthrough = true;

    [ObservableProperty]
    private string _scenesRuleDslPreview = "SCENE = ALL · Tempo IS NULL 通配放行";

    [ObservableProperty]
    private string _scenesEnergyDistributionText = "低能量 0 · 中能量 0 · 高能量 0";

    [ObservableProperty]
    private bool _isShuffleEnabled;

    /// <summary>"all" | "one" | "off".</summary>
    [ObservableProperty]
    private string _repeatMode = "all";

    [ObservableProperty]
    private string _shuffleModeLabel = "顺序播放";

    [ObservableProperty]
    private string _repeatModeLabel = "列表循环";

    [ObservableProperty]
    private string _playbackQueueSummary = "播放列表 (0)";

    [ObservableProperty]
    private bool _isArcFlat;

    [ObservableProperty]
    private bool _isArcAsc;

    [ObservableProperty]
    private bool _isArcSleep;

    [ObservableProperty]
    private bool _isArcRediscovery;

    private readonly List<TrackItem> _playbackHistory = [];
    private List<TrackItem> _unshuffledQueueSnapshot = [];
    private (List<TrackItem> Queue, string QueueTitle, string FreezeToken)? _djFreezeBackup;
    private string _currentFreezeToken = "";
    private readonly Random _shuffleRandom = new();
    private readonly HashSet<string> _autoSyncedLastFmRoots = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DesktopDspChainConfig> _deviceDspProfiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressDspSync;
    private bool _usingExternalEqFile;

    // Live Last.fm Scrobble & Exclusive Idle state
    private TrackItem? _scrobbleTrack;
    private long _scrobbleStartedUtcSec;
    private double _accumulatedPlayMs;
    private DateTime? _lastProgressSampleUtc;
    private bool _scrobbledCurrentTrack;
    private readonly SemaphoreSlim _lastFmPushGate = new(1, 1);
    private double _pendingRestorePositionSec;
    private DateTime _lastSessionSavedUtc = DateTime.MinValue;
    private DateTime? _pausedSinceUtc;

    public event Action? WaveformUpdated;
    public event Action<TrackItem?>? TrackChanged;
    public event Action? SubTabsChanged;

    public MainViewModel()
    {
        _uiContext = SynchronizationContext.Current;
        _engine.SignalPathChanged += state =>
        {
            void ApplyState()
            {
                SignalPathBadge = state.SignalPathBadge;
                HasSystemApoConflict = state.HasSystemApoConflict;
                SystemApoWarningText = state.SystemApoWarning ?? "";
                if (state.IsExclusive && state.DspBypassed && !state.HasSystemApoConflict)
                {
                    SignalPurityColorHex = "#A855F7";
                    SignalPurityLabel = "Bit-Perfect 无损直通";
                }
                else if (state.GainMatchedBypass)
                {
                    SignalPurityColorHex = "#38BDF8";
                    SignalPurityLabel = "等响度 A/B 盲测旁路";
                }
                else if (state.HasSystemApoConflict || !state.IsExclusive)
                {
                    SignalPurityColorHex = "#F59E0B";
                    SignalPurityLabel = state.HasSystemApoConflict ? "存在 APO 干扰风险" : "共享混音路径";
                }
                else
                {
                    SignalPurityColorHex = "#10B981";
                    SignalPurityLabel = "64-Bit 浮点 DSP";
                }
                SignalPathSummary =
                    $"{state.OutputDeviceName}\n{state.ShareModeLabel}\n"
                    + $"{state.SampleRateHz / 1000.0:0.#} kHz · {state.BitsPerSample}-bit · "
                    + (state.DspBypassed
                        ? "DSP Bypassed (Bit-Perfect)"
                        : (state.GainMatchedBypass
                            ? $"Gain-Matched A/B Bypass (RG/Preamp {state.EffectivePreampDb:+0.0;-0.0;0.0} dB)"
                            : $"64-bit DSP ({state.BiquadCount} PEQ, FIR {state.FirTapCount} taps, Crossfeed={(state.CrossfeedActive ? "On" : "Off")}, ISO226={(state.Iso226Active ? "On" : "Off")}, Limiter={(state.LimiterActive ? "On" : "Off")}, TPDF={(state.TpdfDitherActive ? "24b" : "Off")}, Preamp {state.EffectivePreampDb:+0.0;-0.0;0.0} dB)"));
            }
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
                _uiContext.Post(_ => ApplyState(), null);
            else
                ApplyState();
        };
        _engine.PlaybackEnded += () =>
        {
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
                _uiContext.Post(_ => HandlePlaybackEnded(), null);
            else
                HandlePlaybackEnded();
        };
        _engine.GaplessTrackAdvanced += nextPath =>
        {
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
                _uiContext.Post(_ => HandleGaplessTrackAdvanced(nextPath), null);
            else
                HandleGaplessTrackAdvanced(nextPath);
        };
        PlaybackQueue.CollectionChanged += (_, _) => UpdatePlaybackQueueSummary();
        UpdatePlaybackModeLabels();
        UpdatePlaybackQueueSummary();

        RefreshEndpoints();
        ReloadSessionSlots();
        RefreshPlaylists();
        UpdateSearchSuggestions("");
        UpdateNavigationState();
    }

    public void RestoreSavedSettingsAndLibrary(string? settingsPath = null)
    {
        _settingsFilePath = string.IsNullOrWhiteSpace(settingsPath)
            ? DesktopSettingsStore.DefaultSettingsPath
            : settingsPath;
        _settingsPersistenceEnabled = true;

        var saved = DesktopSettingsStore.Load(_settingsFilePath);
        _suppressSettingsSave = true;
        _suppressDspSync = true;
        try
        {
            PreferWasapiExclusive = saved.PreferWasapiExclusive;
            IsIso226Enabled = saved.IsIso226Enabled;
            VolumeAttenuationDb = saved.VolumeAttenuationDb;
            IsTpdfDitherEnabled = saved.IsTpdfDitherEnabled;
            FirDryWetMix = saved.FirDryWetMix;
            AlwaysExpandClassicalSubItems = saved.AlwaysExpandClassicalSubItems;
            IsSidebarExpanded = saved.IsSidebarExpanded;
            IsInspectorOpen = saved.IsInspectorOpen;
            AppTheme = saved.AppTheme is "light" or "system" ? saved.AppTheme : "dark";
            IsShuffleEnabled = saved.IsShuffleEnabled;
            RepeatMode = saved.RepeatMode is "one" or "off" ? saved.RepeatMode : "all";
            EnableAutoWatchLibrary = saved.EnableAutoWatchLibrary;
            ReplayGainMode = saved.ReplayGainMode is "off" or "track" or "album" or "dynamic" ? saved.ReplayGainMode : "dynamic";
            ReplayGainPreAmpWithTagDb = Math.Clamp(saved.ReplayGainPreAmpWithTagDb, -15.0, 15.0);
            ReplayGainPreAmpWithoutTagDb = Math.Clamp(saved.ReplayGainPreAmpWithoutTagDb, -15.0, 15.0);
            RewindOnPrevious = saved.RewindOnPrevious;
            RememberPauseOnSkip = saved.RememberPauseOnSkip;
            PauseOnSingleRepeat = saved.PauseOnSingleRepeat;
            ExclusiveIdleReleaseMinutes = saved.ExclusiveIdleReleaseMinutes is 0 or 5 or 15 or 30 ? saved.ExclusiveIdleReleaseMinutes : 15;
            _deviceDspProfiles = DesktopDspChainConfig.DeserializeDeviceProfileMap(saved.DeviceDspProfilesJson);

            if (!string.IsNullOrWhiteSpace(saved.SelectedEndpointId))
            {
                var ep = OutputEndpoints.FirstOrDefault(e =>
                    string.Equals(e.Id, saved.SelectedEndpointId, StringComparison.OrdinalIgnoreCase));
                if (ep is not null)
                {
                    SelectedEndpoint = ep;
                }
            }

            IsDacHardwareLocked = saved.IsDacHardwareLocked;

            LoadDspConfigForCurrentEndpoint(fallbackAutoFileRoot: null);

            if (!string.IsNullOrWhiteSpace(saved.ActiveEqFilePath) && File.Exists(saved.ActiveEqFilePath))
            {
                try
                {
                    LoadEqualizerApoFile(saved.ActiveEqFilePath);
                }
                catch
                {
                    // Non-fatal fallback if saved EQ profile is invalid or removed
                }
            }

            if (!string.IsNullOrWhiteSpace(saved.FirImpulseFilePath) && File.Exists(saved.FirImpulseFilePath))
            {
                try
                {
                    LoadFirImpulseFile(saved.FirImpulseFilePath);
                }
                catch
                {
                    // Non-fatal fallback if saved FIR impulse is missing or invalid
                }
            }
        }
        finally
        {
            _suppressDspSync = false;
            _suppressSettingsSave = false;
        }

        if (!string.IsNullOrWhiteSpace(saved.LastMusicRoot) && Directory.Exists(saved.LastMusicRoot))
        {
            string manifestPath = Path.Combine(saved.LastMusicRoot, ".tonewise", "manifest.db");
            if (File.Exists(manifestPath))
            {
                try
                {
                    LoadMusicFolder(saved.LastMusicRoot);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"自动加载上次曲库失败: {ex.Message}";
                }
            }
            else if (_uiContext is not null)
            {
                _ = LoadMusicFolderAsync(saved.LastMusicRoot);
            }
        }
    }

    private void SaveUserSettings()
    {
        if (_suppressSettingsSave || !_settingsPersistenceEnabled) return;

        var snapshot = new DesktopUserSettings(
            LastMusicRoot: string.IsNullOrWhiteSpace(_store.MusicRoot) ? null : _store.MusicRoot,
            PreferWasapiExclusive: PreferWasapiExclusive,
            IsDacHardwareLocked: IsDacHardwareLocked,
            SelectedEndpointId: SelectedEndpoint?.Id,
            IsIso226Enabled: IsIso226Enabled,
            VolumeAttenuationDb: VolumeAttenuationDb,
            IsTpdfDitherEnabled: IsTpdfDitherEnabled,
            FirDryWetMix: FirDryWetMix,
            ActiveEqFilePath: _usingExternalEqFile ? _activeEqFilePath : null,
            FirImpulseFilePath: _firImpulseFilePath,
            AlwaysExpandClassicalSubItems: AlwaysExpandClassicalSubItems,
            IsSidebarExpanded: IsSidebarExpanded,
            IsInspectorOpen: IsInspectorOpen,
            AppTheme: AppTheme,
            IsShuffleEnabled: IsShuffleEnabled,
            RepeatMode: RepeatMode,
            EnableAutoWatchLibrary: EnableAutoWatchLibrary,
            ReplayGainMode: ReplayGainMode,
            ReplayGainPreAmpWithTagDb: ReplayGainPreAmpWithTagDb,
            ReplayGainPreAmpWithoutTagDb: ReplayGainPreAmpWithoutTagDb,
            RewindOnPrevious: RewindOnPrevious,
            RememberPauseOnSkip: RememberPauseOnSkip,
            PauseOnSingleRepeat: PauseOnSingleRepeat,
            ExclusiveIdleReleaseMinutes: ExclusiveIdleReleaseMinutes,
            DeviceDspProfilesJson: DesktopDspChainConfig.SerializeDeviceProfileMap(_deviceDspProfiles));

        try
        {
            DesktopSettingsStore.Save(snapshot, _settingsFilePath);
        }
        catch
        {
            // Non-fatal if settings directory is temporarily read-only
        }
    }

    public void RefreshEndpoints()
    {
        OutputEndpoints.Clear();
        foreach (var ep in AudioDeviceRouter.EnumerateRenderEndpoints())
        {
            OutputEndpoints.Add(ep);
            if (ep.IsDefault && SelectedEndpoint is null)
            {
                SelectedEndpoint = ep;
            }
        }
    }

    public void LoadMusicFolder(string musicRoot)
    {
        _allTracks = _store.LoadFromMusicRoot(musicRoot).ToList();
        ApplyLoadedLibraryState(musicRoot);
    }

    public async Task LoadMusicFolderAsync(string musicRoot, CancellationToken ct = default)
    {
        StatusMessage = $"正在索引曲库：{musicRoot}";
        BeginBusy($"正在索引曲库 {musicRoot} ...");
        try
        {
            await _store.ScanAndBuildManifestIfMissingAsync(musicRoot, forceRebuild: false, fastMode: true, ct);
            var loaded = await Task.Run(() => _store.LoadFromMusicRoot(musicRoot).ToList(), ct);
            ct.ThrowIfCancellationRequested();
            _allTracks = loaded;
            ApplyLoadedLibraryState(musicRoot);
        }
        finally
        {
            EndBusy();
        }
    }

    private void ApplyLoadedLibraryState(string musicRoot)
    {
        MusicRootDisplayPath = musicRoot;
        IsViewingPlaylist = false;
        SelectedPlaylist = null;
        UpdateClassicalAdaptiveState();
        ApplyFiveDimFilter();
        RefreshStagingProposals();
        ReloadSessionSlots();
        RefreshPlaylists();
        NavigateToPrimary(PrimaryNavId, SecondarySubTab);

        if (!string.IsNullOrWhiteSpace(SelectedEndpoint?.FriendlyName))
        {
            string key = GetCurrentDspProfileKey();
            if (!_deviceDspProfiles.ContainsKey(key) && !_usingExternalEqFile)
            {
                string? autoEq = _engine.TryAutoLoadDeviceDspProfile(musicRoot, SelectedEndpoint.FriendlyName);
                if (autoEq is not null)
                {
                    _usingExternalEqFile = true;
                    _activeEqFilePath = autoEq;
                    ActiveEqSummary = $"Auto EQ: {Path.GetFileName(autoEq)}";
                }
            }
        }

        SaveUserSettings();
        _syncStore = new SyncOutboxAndPairingStore(musicRoot);
        RefreshLanSyncUiState();
        StatusMessage = $"已加载 {_allTracks.Count:N0} 首曲目（{musicRoot}）";
        RestartLibraryFileWatcher(musicRoot);
        RestoreSavedPlaybackSession(musicRoot);
        RefreshLastFmAuthStatus();

        if (_uiContext is not null && _autoSyncedLastFmRoots.Add(musicRoot))
        {
            string tonewiseDir = Path.Combine(musicRoot, ".tonewise");
            var lfmCfg = LastFmAccountConfig.Load(tonewiseDir);
            if (lfmCfg is not null
                && !string.IsNullOrWhiteSpace(lfmCfg.ApiKey)
                && !string.IsNullOrWhiteSpace(lfmCfg.Username))
            {
                _ = AutoSyncLastFmOnLibraryLoadAsync();
            }
        }
    }

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

    public void LoadEqualizerApoFile(string filePath)
    {
        string text = File.ReadAllText(filePath);
        _eqProfile = EqualizerApoProfile.Parse(text);
        _activeEqFilePath = filePath;
        _usingExternalEqFile = true;
        if (_eqProfile.EnableIso226) IsIso226Enabled = true;
        if (!string.IsNullOrWhiteSpace(_eqProfile.FirImpulsePath))
        {
            string resolved = Path.IsPathRooted(_eqProfile.FirImpulsePath)
                ? _eqProfile.FirImpulsePath
                : Path.Combine(Path.GetDirectoryName(filePath) ?? "", _eqProfile.FirImpulsePath);
            if (File.Exists(resolved))
            {
                LoadFirImpulseFile(resolved);
            }
        }

        ActiveEqSummary =
            $"EQ: {Path.GetFileName(filePath)} ({_eqProfile.Filters.Count} L/R bands, Preamp {_eqProfile.PreampDb:+0.0;-0.0;0.0} dB)";
        PushEnginePreferences();
        SaveUserSettings();
    }

    public void LoadFirImpulseFile(string wavPath)
    {
        _engine.SetFirImpulseFile(wavPath, FirDryWetMix);
        _firImpulseFilePath = wavPath;
        FirImpulseFileName = $"FIR: {Path.GetFileName(wavPath)}";
        SaveUserSettings();
    }

    private void TriggerFilterRefresh()
    {
        if (!_suppressFilterRefresh)
        {
            if (IsEntityBrowserVisible)
            {
                RebuildEntityGroups();
            }
            else if (IsViewingPlaylist && SelectedPlaylist is not null && PrimaryNavId == "playlists")
            {
                LoadPlaylistTracksIntoView(SelectedPlaylist);
            }
            else
            {
                ApplyFiveDimFilter();
            }
        }
    }

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        SidebarWidth = value ? 244.0 : 58.0;
        UpdateClassicalAdaptiveState();
        SaveUserSettings();
    }

    partial void OnIsInspectorOpenChanged(bool value)
    {
        SaveUserSettings();
    }

    partial void OnAlwaysExpandClassicalSubItemsChanged(bool value)
    {
        UpdateClassicalAdaptiveState();
        SaveUserSettings();
    }

    partial void OnLibraryNavModeChanged(string value) => TriggerFilterRefresh();
    partial void OnFilterSceneChanged(string value) => TriggerFilterRefresh();
    partial void OnFilterEnergyChanged(string value) => TriggerFilterRefresh();
    partial void OnFilterVocalsChanged(string value) => TriggerFilterRefresh();
    partial void OnExcludeVocalChanged(bool value) => TriggerFilterRefresh();
    partial void OnFilterTempoBucketChanged(string value) => TriggerFilterRefresh();
    partial void OnFilterMoodClusterChanged(string value) => TriggerFilterRefresh();
    partial void OnScenesTempoNullPassthroughChanged(bool value) => TriggerFilterRefresh();
    partial void OnSelectedEnergyArcChanged(string value)
    {
        UpdateArcStateFlags();
        TriggerFilterRefresh();
    }
    partial void OnIsShuffleEnabledChanged(bool value)
    {
        UpdatePlaybackModeLabels();
        if (!_suppressSettingsSave && PlaybackQueue.Count > 1)
        {
            ApplyShuffleModeToActiveQueue(value);
        }
        SaveUserSettings();
    }
    partial void OnRepeatModeChanged(string value)
    {
        UpdatePlaybackModeLabels();
        if (!_suppressSettingsSave)
        {
            PreloadNextGaplessTrack();
            PersistPlaybackSessionSnapshot(force: true);
        }
        SaveUserSettings();
    }
    partial void OnSearchQueryChanged(string value) => TriggerFilterRefresh();
    partial void OnIsDacHardwareLockedChanged(bool value)
    {
        IsDacHardwareUnlocked = !value;
        SaveUserSettings();
    }
    partial void OnPreferWasapiExclusiveChanged(bool value)
    {
        PushEnginePreferences();
        SaveUserSettings();
    }
    partial void OnSelectedEndpointChanged(AudioOutputEndpoint? value)
    {
        if (!_suppressDspSync)
        {
            LoadDspConfigForCurrentEndpoint(_store.MusicRoot);
        }
        PushEnginePreferences();
        SaveUserSettings();
    }
    partial void OnReplayGainModeChanged(string value)
    {
        if (_suppressSettingsSave) return;
        PushEnginePreferences();
        PreloadNextGaplessTrack();
        SaveUserSettings();
    }
    partial void OnReplayGainPreAmpWithTagDbChanged(double value)
    {
        if (_suppressSettingsSave) return;
        PushEnginePreferences();
        SaveUserSettings();
    }
    partial void OnReplayGainPreAmpWithoutTagDbChanged(double value)
    {
        if (_suppressSettingsSave) return;
        PushEnginePreferences();
        SaveUserSettings();
    }
    partial void OnRewindOnPreviousChanged(bool value) => SaveUserSettings();
    partial void OnRememberPauseOnSkipChanged(bool value) => SaveUserSettings();
    partial void OnPauseOnSingleRepeatChanged(bool value) => SaveUserSettings();
    public string ExclusiveIdleReleaseTag
    {
        get => ExclusiveIdleReleaseMinutes.ToString();
        set
        {
            if (int.TryParse(value, out int minutes) && minutes != ExclusiveIdleReleaseMinutes)
            {
                ExclusiveIdleReleaseMinutes = minutes;
            }
        }
    }

    partial void OnExclusiveIdleReleaseMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(ExclusiveIdleReleaseTag));
        SaveUserSettings();
    }

    partial void OnIsDspEnabledChanged(bool value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnIsGainMatchedBypassChanged(bool value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnIsCrossfeedEnabledChanged(bool value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnCrossfeedCutoffHzChanged(double value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnCrossfeedFeedDbChanged(double value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnDspBalanceDbChanged(double value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnIsMonoFoldEnabledChanged(bool value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnIsLimiterEnabledChanged(bool value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnDspPreampDbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand0DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand1DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand2DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand3DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand4DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand5DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand6DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand7DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand8DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);
    partial void OnEqBand9DbChanged(double value) => OnBuiltInDspChanged(markCustomEq: true);

    partial void OnCurrentTrackChanged(TrackItem? value)
    {
        HasCurrentTrack = value is not null;
        CurrentTrackLoved = value?.IsLoved ?? false;
        RefreshCurrentFreezeCapsule();
    }

    private string GetCurrentDspProfileKey()
    {
        string raw = SelectedEndpoint?.FriendlyName ?? SelectedEndpoint?.Id ?? "Default";
        return raw.Trim();
    }

    private void OnBuiltInDspChanged(bool markCustomEq)
    {
        if (_suppressDspSync) return;
        _suppressDspSync = true;
        try
        {
            if (markCustomEq)
            {
                _usingExternalEqFile = false;
                _activeEqFilePath = null;
                DspPresetId = "Custom";
                if (!IsDspEnabled) IsDspEnabled = true;
            }
            var cfg = BuildCurrentExtendedDspConfig();
            if (!_usingExternalEqFile)
            {
                _eqProfile = IsDspEnabled ? cfg.ToEqualizerApoProfile() : null;
                ActiveEqSummary = IsDspEnabled
                    ? $"内置 DSP ({DspPresetId}, {_eqProfile?.Filters.Count ?? 0} 段, Preamp {DspPreampDb:+0.0;-0.0;0.0} dB)"
                    : "EQ: Flat (已旁路)";
            }
            _deviceDspProfiles[GetCurrentDspProfileKey()] = cfg;
        }
        finally
        {
            _suppressDspSync = false;
        }
        PushEnginePreferences();
        SaveUserSettings();
    }

    [RelayCommand]
    public void SelectDspPreset(string presetName)
    {
        if (!Enum.TryParse<DesktopDspPresetId>(presetName, ignoreCase: true, out var preset))
        {
            preset = DesktopDspPresetId.Flat;
        }

        var cfg = DesktopDspChainConfig.CreatePreset(preset);
        _suppressDspSync = true;
        try
        {
            _usingExternalEqFile = false;
            _activeEqFilePath = null;
            ApplyDspChainConfigToViewModel(cfg);
            _eqProfile = cfg.Enabled ? cfg.ToEqualizerApoProfile() : null;
            ActiveEqSummary = cfg.Enabled
                ? $"内置 DSP ({cfg.PresetId}, {_eqProfile?.Filters.Count ?? 0} 段, Preamp {cfg.PreampDb:+0.0;-0.0;0.0} dB)"
                : "EQ: Flat (已旁路)";
            _deviceDspProfiles[GetCurrentDspProfileKey()] = cfg;
        }
        finally
        {
            _suppressDspSync = false;
        }
        PushEnginePreferences();
        SaveUserSettings();
        Notify($"已切换 DSP 预设：{cfg.PresetId}", "info");
    }

    [RelayCommand]
    private void ResetTenBandEq()
    {
        _suppressDspSync = true;
        try
        {
            _usingExternalEqFile = false;
            _activeEqFilePath = null;
            EqBand0Db = 0; EqBand1Db = 0; EqBand2Db = 0; EqBand3Db = 0; EqBand4Db = 0;
            EqBand5Db = 0; EqBand6Db = 0; EqBand7Db = 0; EqBand8Db = 0; EqBand9Db = 0;
            DspPreampDb = 0.0;
            DspPresetId = "Flat";
            var cfg = BuildCurrentExtendedDspConfig();
            _eqProfile = IsDspEnabled ? cfg.ToEqualizerApoProfile() : null;
            ActiveEqSummary = "EQ: Flat (已归零)";
            _deviceDspProfiles[GetCurrentDspProfileKey()] = cfg;
        }
        finally
        {
            _suppressDspSync = false;
        }
        PushEnginePreferences();
        SaveUserSettings();
    }

    private void ApplyDspChainConfigToViewModel(DesktopDspChainConfig cfg)
    {
        DspPresetId = cfg.PresetId.ToString();
        IsDspEnabled = cfg.Enabled;
        IsGainMatchedBypass = cfg.GainMatchedBypass;
        DspPreampDb = cfg.PreampDb;
        IsCrossfeedEnabled = cfg.CrossfeedEnabled;
        CrossfeedCutoffHz = cfg.CrossfeedCutoffHz;
        CrossfeedFeedDb = cfg.CrossfeedFeedDb;
        DspBalanceDb = cfg.BalanceDb;
        IsMonoFoldEnabled = cfg.MonoFold;
        IsLimiterEnabled = cfg.LimiterEnabled;

        var bands = cfg.EffectiveBands;
        double BandGain(int idx) => idx < bands.Count ? bands[idx].GainDb : 0.0;
        EqBand0Db = BandGain(0);
        EqBand1Db = BandGain(1);
        EqBand2Db = BandGain(2);
        EqBand3Db = BandGain(3);
        EqBand4Db = BandGain(4);
        EqBand5Db = BandGain(5);
        EqBand6Db = BandGain(6);
        EqBand7Db = BandGain(7);
        EqBand8Db = BandGain(8);
        EqBand9Db = BandGain(9);
    }

    private DesktopDspChainConfig BuildCurrentExtendedDspConfig()
    {
        Enum.TryParse<DesktopDspPresetId>(DspPresetId, ignoreCase: true, out var preset);
        var template = DesktopDspChainConfig.CreatePreset(preset).EffectiveBands;
        double[] gains = [EqBand0Db, EqBand1Db, EqBand2Db, EqBand3Db, EqBand4Db, EqBand5Db, EqBand6Db, EqBand7Db, EqBand8Db, EqBand9Db];
        var bands = new List<DesktopPeqBand>(10);
        for (int i = 0; i < 10; i++)
        {
            var t = i < template.Count
                ? template[i]
                : new DesktopPeqBand(BiquadKind.Peaking, DesktopDspChainConfig.StandardTenBandFrequencies[i], 0.0);
            bands.Add(t with { GainDb = Math.Clamp(gains[i], -12.0, 12.0) });
        }
        return new DesktopDspChainConfig(
            PresetId: preset,
            Enabled: IsDspEnabled,
            GainMatchedBypass: IsGainMatchedBypass,
            PreampDb: Math.Clamp(DspPreampDb, -15.0, 6.0),
            Bands: bands,
            CrossfeedEnabled: IsCrossfeedEnabled,
            CrossfeedCutoffHz: Math.Clamp(CrossfeedCutoffHz, 450.0, 950.0),
            CrossfeedFeedDb: Math.Clamp(CrossfeedFeedDb, 1.5, 9.5),
            CrossfeedDelayMs: 0.28,
            BalanceDb: Math.Clamp(DspBalanceDb, -6.0, 6.0),
            MonoFold: IsMonoFoldEnabled,
            LimiterEnabled: IsLimiterEnabled,
            LimiterCeilingDb: -0.2);
    }

    private void LoadDspConfigForCurrentEndpoint(string? fallbackAutoFileRoot)
    {
        string key = GetCurrentDspProfileKey();
        if (_deviceDspProfiles.TryGetValue(key, out var cfg))
        {
            bool prevSup = _suppressDspSync;
            _suppressDspSync = true;
            try
            {
                ApplyDspChainConfigToViewModel(cfg);
                if (!_usingExternalEqFile)
                {
                    _eqProfile = cfg.Enabled ? cfg.ToEqualizerApoProfile() : null;
                    ActiveEqSummary = cfg.Enabled
                        ? $"内置 DSP ({cfg.PresetId}, {_eqProfile?.Filters.Count ?? 0} 段, {key})"
                        : $"EQ: Flat ({key})";
                }
            }
            finally
            {
                _suppressDspSync = prevSup;
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(fallbackAutoFileRoot)
            && !string.IsNullOrWhiteSpace(SelectedEndpoint?.FriendlyName))
        {
            string? autoEq = _engine.TryAutoLoadDeviceDspProfile(fallbackAutoFileRoot, SelectedEndpoint.FriendlyName);
            if (autoEq is not null)
            {
                _usingExternalEqFile = true;
                _activeEqFilePath = autoEq;
                ActiveEqSummary = $"Auto EQ: {Path.GetFileName(autoEq)}";
            }
        }
    }
    partial void OnIsIso226EnabledChanged(bool value)
    {
        PushAcousticPipeline();
        SaveUserSettings();
    }
    partial void OnVolumeAttenuationDbChanged(double value)
    {
        PushAcousticPipeline();
        SyncVolumePercentFromDb(value);
        SaveUserSettings();
    }

    partial void OnVolumePercentChanged(double value)
    {
        if (_syncingVolume) return;
        double pct = Math.Clamp(value, 0.0, 100.0);
        double db = pct <= 1.0 ? -60.0 : Math.Clamp(20.0 * Math.Log10(pct / 100.0), -60.0, 0.0);
        _syncingVolume = true;
        try
        {
            VolumeAttenuationDb = Math.Round(db, 1);
        }
        finally
        {
            _syncingVolume = false;
        }
        UpdateVolumeToolTip();
    }

    partial void OnIsMutedChanged(bool value)
    {
        _engine.IsMuted = value;
        UpdateVolumeToolTip();
    }

    partial void OnAppThemeChanged(string value) => SaveUserSettings();

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    private void SyncVolumePercentFromDb(double db)
    {
        if (!_syncingVolume)
        {
            _syncingVolume = true;
            try
            {
                VolumePercent = Math.Round(Math.Clamp(100.0 * Math.Pow(10.0, db / 20.0), 0.0, 100.0), 0);
            }
            finally
            {
                _syncingVolume = false;
            }
        }
        UpdateVolumeToolTip();
    }

    private void UpdateVolumeToolTip()
    {
        VolumeToolTip = IsMuted
            ? "已静音 (点击取消静音)"
            : $"音量 {VolumePercent:0}% ({VolumeAttenuationDb:0.0} dB 数字衰减)";
    }

    partial void OnStatusMessageChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            IsNotificationOpen = false;
            return;
        }
        NotificationLevel = _explicitNotificationLevel ?? InferNotificationLevel(value);
        _explicitNotificationLevel = null;
        NotificationMessage = value;
        IsNotificationOpen = true;
    }

    /// <summary>Publishes a user-facing result with an explicit severity ("info" | "success" | "warning" | "error").</summary>
    public void Notify(string message, string level)
    {
        _explicitNotificationLevel = level;
        if (StatusMessage == message)
        {
            NotificationLevel = level;
            NotificationMessage = message;
            IsNotificationOpen = true;
            _explicitNotificationLevel = null;
            return;
        }
        StatusMessage = message;
    }

    public static string InferNotificationLevel(string message)
    {
        string m = message.ToLowerInvariant();
        if (m.Contains("失败") || m.Contains("error") || m.Contains("failed") || m.Contains("异常") || m.Contains("无效"))
            return "error";
        if (m.Contains("请先") || m.Contains("无法") || m.Contains("未匹配") || m.Contains("not found") || m.Contains("open a music folder") || m.Contains("已放弃") || m.Contains("不存在") || m.Contains("暂无") || m.Contains("为空"))
            return "warning";
        if (m.Contains("已加载") || m.Contains("已导入") || m.Contains("已创建") || m.Contains("已删除") || m.Contains("已导出") || m.Contains("已重建") || m.Contains("已应用") || m.Contains("已撤回") || m.Contains("已提交") || m.Contains("已写入") || m.Contains("已批准") || m.Contains("已清空") || m.Contains("loaded") || m.Contains("complete") || m.Contains("rebuilt") || m.Contains("synced") || m.Contains("已保存") || m.Contains("已将") || m.Contains("已同步") || m.Contains("完成") || m.Contains("已切换") || m.Contains("rolled back"))
            return "success";
        return "info";
    }

    private CancellationToken BeginBusy(string text, bool indeterminate = true, bool cancellable = false)
    {
        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        BusyText = text;
        IsBusyIndeterminate = indeterminate;
        BusyProgressValue = 0;
        CanCancelBusy = cancellable;
        IsBusy = true;
        return _busyCts.Token;
    }

    private void ReportBusyProgress(double percent, string? text = null)
    {
        void Apply()
        {
            IsBusyIndeterminate = false;
            BusyProgressValue = Math.Clamp(percent, 0.0, 100.0);
            if (text is not null) BusyText = text;
        }
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
            _uiContext.Post(_ => Apply(), null);
        else
            Apply();
    }

    private void EndBusy()
    {
        IsBusy = false;
        CanCancelBusy = false;
        BusyText = "";
        BusyProgressValue = 0;
        _busyCts?.Dispose();
        _busyCts = null;
    }

    [RelayCommand]
    private void CancelBusy()
    {
        try
        {
            _busyCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed
        }
        BusyText = "正在取消，当前批次完成后停止...";
    }

    public IReadOnlyList<string> GetKnownSceneTags() =>
        _allTracks.SelectMany(t => t.DjScene)
            .Concat(["focus", "coding", "relax", "energy", "pop", "sleep", "workout", "dinner"])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<string> GetKnownMoodTags() =>
        _allTracks.SelectMany(t => t.Moods)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(400)
            .ToList();
    partial void OnIsTpdfDitherEnabledChanged(bool value)
    {
        PushAcousticPipeline();
        SaveUserSettings();
    }
    partial void OnFirDryWetMixChanged(double value)
    {
        PushAcousticPipeline();
        SaveUserSettings();
    }

    partial void OnIsPlayingChanged(bool value)
    {
        PlayPauseLabel = value ? "Pause" : "Play";
        if (value)
        {
            _pausedSinceUtc = null;
        }
        else if (CurrentTrack is not null)
        {
            _pausedSinceUtc ??= DateTime.UtcNow;
        }
    }

    private void RunBatchFilterUpdate(Action update)
    {
        _suppressFilterRefresh = true;
        try
        {
            update();
        }
        finally
        {
            _suppressFilterRefresh = false;
        }
        ApplyFiveDimFilter();
    }

    [RelayCommand]
    public void ResetAllFilters()
    {
        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = "all";
            FilterScene = "all";
            FilterEnergy = "all";
            FilterVocals = "all";
            ExcludeVocal = false;
            FilterTempoBucket = "all";
            FilterMoodCluster = "all";
            SelectedEnergyArc = "flat";
            SearchQuery = "";
            SearchBoxText = "";
        });
    }

    public void SetLibraryNavMode(string mode)
    {
        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = string.IsNullOrWhiteSpace(mode) ? "all" : mode;
        });
    }

    public void FilterByCreditEntity(string entityValue)
    {
        if (string.IsNullOrWhiteSpace(entityValue)) return;
        _suppressFilterRefresh = true;
        try
        {
            LibraryNavMode = "all";
            SearchQuery = entityValue.Trim();
            SearchBoxText = SearchQuery;
        }
        finally
        {
            _suppressFilterRefresh = false;
        }
        NavigateToPrimary("library", "tracks");
    }

    public void UpdateSearchSuggestions(string? rawInput)
    {
        string input = (rawInput ?? "").Trim();
        SearchSuggestions.Clear();

        if (string.IsNullOrEmpty(input))
        {
            foreach (var (token, desc) in SearchQueryDslParser.Presets.Take(6))
            {
                SearchSuggestions.Add($"{token} · {desc}");
            }
            return;
        }

        foreach (var (token, desc) in SearchQueryDslParser.Presets)
        {
            if (token.Contains(input, StringComparison.OrdinalIgnoreCase) ||
                desc.Contains(input, StringComparison.OrdinalIgnoreCase))
            {
                SearchSuggestions.Add($"{token} · {desc}");
                if (SearchSuggestions.Count >= 10) return;
            }
        }

        var seen = new HashSet<string>(SearchSuggestions, StringComparer.OrdinalIgnoreCase);
        void TryAddSuggestion(string label)
        {
            if (SearchSuggestions.Count < 10 && seen.Add(label))
            {
                SearchSuggestions.Add(label);
            }
        }

        foreach (var t in _allTracks)
        {
            if (SearchSuggestions.Count >= 10) break;
            if (!string.IsNullOrWhiteSpace(t.WorkName) && t.WorkName.Contains(input, StringComparison.OrdinalIgnoreCase))
                TryAddSuggestion($"Work: {t.WorkName}");
            if (!string.IsNullOrWhiteSpace(t.ComposersDisplay) && t.ComposersDisplay.Contains(input, StringComparison.OrdinalIgnoreCase))
                TryAddSuggestion($"Composer: {t.ComposersDisplay.Split(',')[0].Trim()}");
            if (!string.IsNullOrWhiteSpace(t.ArtistsDisplay) && t.ArtistsDisplay.Contains(input, StringComparison.OrdinalIgnoreCase))
                TryAddSuggestion($"Artist: {t.ArtistsDisplay}");
            if (!string.IsNullOrWhiteSpace(t.Album) && t.Album.Contains(input, StringComparison.OrdinalIgnoreCase))
                TryAddSuggestion($"Album: {t.Album}");
            if (t.Title.Contains(input, StringComparison.OrdinalIgnoreCase))
                TryAddSuggestion($"Track: {t.Title}");
        }
    }

    public void ApplySearchSuggestionOrQuery(string? rawQuery)
    {
        string raw = rawQuery ?? "";
        SearchBoxText = raw;
        var parsed = SearchQueryDslParser.Parse(raw);

        switch (parsed.Kind)
        {
            case "scene":
                if (PrimaryNavId is not "scenes" and not "library" || (PrimaryNavId == "library" && SecondarySubTab != "tracks"))
                {
                    PrimaryNavId = "scenes";
                    SecondarySubTab = "studio";
                    UpdateNavigationState();
                }
                RunBatchFilterUpdate(() =>
                {
                    LibraryNavMode = "all";
                    FilterScene = parsed.Value;
                    SearchQuery = "";
                });
                break;

            case "vocals":
                if (PrimaryNavId is not "scenes" and not "library" || (PrimaryNavId == "library" && SecondarySubTab != "tracks"))
                {
                    PrimaryNavId = "scenes";
                    SecondarySubTab = "studio";
                    UpdateNavigationState();
                }
                RunBatchFilterUpdate(() =>
                {
                    LibraryNavMode = "all";
                    FilterVocals = parsed.Value;
                    ExcludeVocal = parsed.Value == "instrumental";
                    SearchQuery = "";
                });
                break;

            case "energy":
                if (PrimaryNavId is not "scenes" and not "library" || (PrimaryNavId == "library" && SecondarySubTab != "tracks"))
                {
                    PrimaryNavId = "scenes";
                    SecondarySubTab = "studio";
                    UpdateNavigationState();
                }
                RunBatchFilterUpdate(() =>
                {
                    LibraryNavMode = "all";
                    FilterEnergy = parsed.Value;
                    SearchQuery = "";
                });
                break;

            case "nav":
                SearchQuery = "";
                NavigateToPrimary(parsed.Value, parsed.SubTab);
                break;

            default:
                SearchQuery = parsed.Value;
                break;
        }
    }

    public void NavigateBreadcrumbAt(int index)
    {
        if (index <= 0)
        {
            NavigateToPrimary(PrimaryNavId);
            return;
        }
        NavigateToPrimary(PrimaryNavId, SecondarySubTab);
    }

    [RelayCommand]
    public void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    [RelayCommand]
    public void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    public void NavigateToPrimary(string navId, string? subTab = null)
    {
        string normalized = (navId ?? "library").Trim().ToLowerInvariant() switch
        {
            "library" or "artists" or "classical" or "scenes" or "playlists" or "inbox" or "sync" or "settings" => (navId ?? "library").Trim().ToLowerInvariant(),
            _ => "library",
        };

        string targetSubTab = (subTab ?? "").Trim().ToLowerInvariant();
        if (!IsValidSubTab(normalized, targetSubTab))
        {
            targetSubTab = GetDefaultSubTab(normalized);
        }

        PrimaryNavId = normalized;
        SecondarySubTab = targetSubTab;
        UpdateNavigationState();
        ApplyActiveNavigationData();
    }

    public void SelectSubTab(string subTabId)
    {
        NavigateToPrimary(PrimaryNavId, subTabId);
    }

    public void SelectEntityGroup(EntityGroupItem? group)
    {
        SelectedEntityGroup = group;
        HasSelectedEntityGroup = group is not null;
        if (group is not null)
        {
            FilteredTracks.ReplaceAll(group.Tracks);
            CurrentViewHeader = $"{group.Title} ({group.Tracks.Count} 首)";
            ActiveFilterPillsSummary = $"{PrimarySectionTitle} · {group.Subtitle}";
        }
        else
        {
            FilteredTracks.ReplaceAll([]);
            CurrentViewHeader = $"{PrimarySectionTitle} (0)";
            ActiveFilterPillsSummary = $"{PrimarySectionTitle} · 暂无匹配实体";
        }
        RefreshBreadcrumbs(group?.Title);
    }

    [RelayCommand]
    public void SnapshotSceneToPlaylist()
    {
        if (FilteredTracks.Count == 0)
        {
            StatusMessage = "当前场景筛选结果为空，无法固化为静态歌单。";
            return;
        }
        string sceneTag = FilterScene == "all" ? "mix" : FilterScene;
        string name = $"Scene-{sceneTag}-{DateTime.Now:MMdd-HHmm}";
        CreatePlaylist(name, seedWithCurrentFilteredTracks: true);
        StatusMessage = $"已将当前场景 ({FilteredTracks.Count} 首) 快照固化为静态歌单: {name}";
    }

    [RelayCommand]
    public void GenerateAutoDjPlaylist()
    {
        var playable = _allTracks.Where(t => !string.IsNullOrWhiteSpace(t.FullPath)).ToList();
        if (playable.Count == 0)
        {
            Notify("请先打开包含本地曲目的音乐目录，再使用 AutoDJ。", "warning");
            return;
        }

        var candidates = playable
            .Select(t => new AutoDjTrackCandidate(
                TrackUid: t.TrackUid,
                Title: t.Title,
                Artist: t.ArtistsDisplay,
                EffectivePlayCount: t.EffectivePlayCount,
                Rating: t.Rating,
                TempoBpm: t.TempoBpm,
                TempoBucket: t.TempoBucket,
                DjEnergy: t.DjEnergy,
                DjVocals: t.DjVocals,
                DjScene: t.DjScene,
                Moods: t.Moods,
                LastPlayedTsMs: t.LastPlayedTsMs))
            .ToList();

        var res = AutoDjPlaylistGenerator.Generate(candidates, DateTime.Now, targetCount: 50);
        var byUid = playable.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
        var orderedTracks = new List<TrackItem>(res.OrderedTrackUids.Count);
        foreach (string uid in res.OrderedTrackUids)
        {
            if (byUid.TryGetValue(uid, out var item))
            {
                orderedTracks.Add(item);
            }
        }
        if (orderedTracks.Count == 0)
        {
            Notify("AutoDJ 未能匹配到可播放曲目。", "warning");
            return;
        }

        if (PrimaryNavId != "scenes" || SecondarySubTab != "autodj")
        {
            PrimaryNavId = "scenes";
            SecondarySubTab = "autodj";
            UpdateNavigationState();
        }

        IsViewingPlaylist = false;
        SelectedPlaylist = null;
        string playlistName = $"AutoDJ-{res.Profile.PeriodLabel}-{DateTime.Now:MMdd-HHmm}";
        if (!string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            var entries = orderedTracks.Select(ToPlaylistEntry).ToList();
            PlaylistStore.SavePlaylist(_store.MusicRoot, playlistName, entries);
            RefreshPlaylists();
        }

        IsShuffleEnabled = false;
        FilteredTracks.ReplaceAll(orderedTracks);
        CurrentViewHeader = $"AutoDJ · {res.Profile.PeriodLabel} ({orderedTracks.Count} 首)";
        ActiveFilterPillsSummary = $"常听 {res.FrequentCount} 首 (70%) · 探索 {res.DiscoveryCount} 首 (30%) · 邻曲最大差 {res.MaxAdjacentBpmDelta:0.#} BPM";
        AutoDjSummaryText = $"{res.SummaryText} · 歌单「{playlistName}」已保存";
        UpdateScenesStudioAndArcTelemetry();

        PlayFromBrowseView(orderedTracks[0]);
        InspectTrack(orderedTracks[0]);
        Notify($"{res.SummaryText}（已保存至歌单「{playlistName}」）", "success");
    }

    private static string GetDefaultSubTab(string navId) => navId switch
    {
        "library" => "tracks",
        "artists" => "album_artists",
        "classical" => "works",
        "scenes" => "presets",
        "playlists" => "workspace",
        "inbox" => "new_imports",
        "sync" => "lan",
        "settings" => "audio",
        _ => "tracks",
    };

    private static bool IsValidSubTab(string navId, string subTab) => (navId, subTab) switch
    {
        ("library", "tracks" or "albums" or "genres" or "folders") => true,
        ("artists", "album_artists" or "composers" or "conductors" or "all_performers") => true,
        ("classical", "works" or "composers" or "conductors" or "ensembles") => true,
        ("scenes", "presets" or "autodj" or "studio" or "arc") => true,
        ("playlists", "workspace") => true,
        ("inbox", "new_imports" or "backfill" or "artwork_bio") => true,
        ("sync", "lan" or "lastfm" or "usb") => true,
        ("settings", "audio" or "storage" or "apis_ui") => true,
        _ => false,
    };

    private void UpdateNavigationState()
    {
        IsNavLibrary = PrimaryNavId == "library";
        IsNavArtists = PrimaryNavId == "artists";
        IsNavClassical = PrimaryNavId == "classical";
        IsNavClassicalWorks = PrimaryNavId == "classical" && SecondarySubTab == "works";
        IsNavClassicalComposers = PrimaryNavId == "classical" && SecondarySubTab == "composers";
        IsNavClassicalConductors = PrimaryNavId == "classical" && SecondarySubTab == "conductors";
        IsNavScenes = PrimaryNavId == "scenes";
        IsNavPlaylists = PrimaryNavId == "playlists";
        IsNavInbox = PrimaryNavId == "inbox";
        IsNavSync = PrimaryNavId == "sync";
        IsNavSettings = PrimaryNavId == "settings";

        bool entityMode = (PrimaryNavId == "library" && SecondarySubTab is "albums" or "genres" or "folders")
                          || PrimaryNavId is "artists" or "classical";
        IsEntityBrowserVisible = entityMode;
        IsScenesDeckVisible = PrimaryNavId == "scenes";
        IsScenesPresetSubTab = PrimaryNavId == "scenes" && SecondarySubTab == "presets";
        IsScenesAutoDjSubTab = PrimaryNavId == "scenes" && SecondarySubTab == "autodj";
        IsScenesStudioSubTab = PrimaryNavId == "scenes" && SecondarySubTab == "studio";
        IsScenesArcSubTab = PrimaryNavId == "scenes" && SecondarySubTab == "arc";
        IsPlaylistsDeckVisible = PrimaryNavId == "playlists";
        IsInboxAuditDeckVisible = PrimaryNavId == "inbox";
        IsSyncScrobbleViewVisible = PrimaryNavId == "sync";
        IsSettingsViewVisible = PrimaryNavId == "settings";
        IsTrackTableVisible = PrimaryNavId is not "sync" and not "settings";
        IsFiveDimFilterStripVisible = (PrimaryNavId == "library" && SecondarySubTab == "tracks")
                                      || (PrimaryNavId == "scenes" && SecondarySubTab == "presets");
        UpdateFilterDrawerState();
        UpdateArcStateFlags();

        IsSyncLanSubTab = PrimaryNavId == "sync" && SecondarySubTab == "lan";
        IsSyncLastFmSubTab = PrimaryNavId == "sync" && SecondarySubTab == "lastfm";
        IsSyncUsbSubTab = PrimaryNavId == "sync" && SecondarySubTab == "usb";

        IsSettingsAudioSubTab = PrimaryNavId == "settings" && SecondarySubTab == "audio";
        IsSettingsStorageSubTab = PrimaryNavId == "settings" && SecondarySubTab == "storage";
        IsSettingsApisUiSubTab = PrimaryNavId == "settings" && SecondarySubTab == "apis_ui";

        PrimarySectionTitle = PrimaryNavId switch
        {
            "library" => "曲库",
            "artists" => "艺人",
            "classical" => "古典",
            "scenes" => "场景",
            "playlists" => "歌单",
            "inbox" => "入库审校",
            "sync" => "同步与记录",
            "settings" => "设置",
            _ => "Tonewise",
        };

        (string Id, string Label)[] defs = PrimaryNavId switch
        {
            "library" => [("tracks", "曲目"), ("albums", "专辑"), ("genres", "流派"), ("folders", "文件夹")],
            "artists" => [("album_artists", "专辑艺人"), ("composers", "作曲"), ("conductors", "指挥"), ("all_performers", "全部演职员")],
            "classical" => [("works", "作品"), ("composers", "作曲家"), ("conductors", "指挥家"), ("ensembles", "乐团")],
            "scenes" => [("presets", "预设场景"), ("autodj", "AutoDJ"), ("studio", "规则编排"), ("arc", "能量曲线")],
            "playlists" => [("workspace", "全部歌单")],
            "inbox" => [("new_imports", "待审变更"), ("backfill", "五维与 BPM 回填"), ("artwork_bio", "封面与传记")],
            "sync" => [("lan", "局域网设备"), ("lastfm", "Last.fm 记录"), ("usb", "USB 导出")],
            "settings" => [("audio", "音频与 DSP"), ("storage", "曲库存储"), ("apis_ui", "外观与导航")],
            _ => [("tracks", "曲目")],
        };

        CurrentSubTabs.Clear();
        foreach (var (id, label) in defs)
        {
            CurrentSubTabs.Add(new SubTabOption(id, label, id == SecondarySubTab));
        }

        PrimaryViewSummary = string.Join(" · ", defs.Select(d => d.Label.Split('·')[0].Trim()));
        RefreshBreadcrumbs();
        SubTabsChanged?.Invoke();
    }

    private void RefreshBreadcrumbs(string? activeEntity = null)
    {
        string subLabel = CurrentSubTabs.FirstOrDefault(sub => sub.IsActive)?.Label ?? SecondarySubTab;

        BreadcrumbItems.Clear();
        BreadcrumbItems.Add(PrimarySectionTitle);
        if (CurrentSubTabs.Count > 1)
        {
            BreadcrumbItems.Add(subLabel);
        }
        if (!string.IsNullOrWhiteSpace(activeEntity))
        {
            BreadcrumbItems.Add(activeEntity);
        }
    }

    private void ApplyActiveNavigationData()
    {
        if (PrimaryNavId is "sync" or "settings")
        {
            CurrentViewHeader = PrimarySectionTitle;
            ActiveFilterPillsSummary = PrimaryViewSummary;
            return;
        }

        if (PrimaryNavId == "playlists")
        {
            var target = SelectedPlaylist ?? Playlists.FirstOrDefault();
            if (target is not null)
            {
                LoadPlaylistTracksIntoView(target);
            }
            else
            {
                IsViewingPlaylist = false;
                FilteredTracks.ReplaceAll([]);
                CurrentViewHeader = "歌单工作区 (暂无 .m3u8 歌单，可新建或在线导入)";
                ActiveFilterPillsSummary = "Playlists · 支持 Apple Music / Billboard / Kworb / TXT 导入";
            }
            return;
        }

        if (IsEntityBrowserVisible)
        {
            IsViewingPlaylist = false;
            SelectedPlaylist = null;
            RebuildEntityGroups();
            return;
        }

        string nextMode = PrimaryNavId switch
        {
            "inbox" => SecondarySubTab switch
            {
                "new_imports" => "audit_missing_scene",
                "backfill" => "audit_missing_tempo",
                "artwork_bio" => "audit_missing_cover",
                _ => "audit_missing_scene",
            },
            _ => "all",
        };

        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = nextMode;
            if (PrimaryNavId == "scenes" && SecondarySubTab == "arc" && SelectedEnergyArc == "flat")
            {
                SelectedEnergyArc = "energy_asc";
            }
        });

        if (PrimaryNavId == "inbox")
        {
            RefreshStagingProposals();
        }
    }

    private void RebuildEntityGroups()
    {
        if (_allTracks.Count == 0)
        {
            CurrentEntityGroups.ReplaceAll([]);
            SelectEntityGroup(null);
            return;
        }

        string q = SearchQuery.Trim();
        var map = new Dictionary<string, (string Title, string Subtitle, List<TrackItem> Tracks)>(StringComparer.OrdinalIgnoreCase);

        void AddToBucket(string key, string subtitle, TrackItem track)
        {
            string cleanKey = string.IsNullOrWhiteSpace(key) ? "(未命名)" : key.Trim();
            if (!string.IsNullOrWhiteSpace(q) &&
                !cleanKey.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !track.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!map.TryGetValue(cleanKey, out var bucket))
            {
                bucket = (cleanKey, subtitle, []);
                map[cleanKey] = bucket;
            }
            bucket.Tracks.Add(track);
        }

        foreach (var track in _allTracks)
        {
            switch ((PrimaryNavId, SecondarySubTab))
            {
                case ("library", "albums"):
                {
                    string album = string.IsNullOrWhiteSpace(track.Album) ? "(未分配专辑)" : track.Album;
                    string sub = string.IsNullOrWhiteSpace(track.ArtistsDisplay) ? track.YearDisplay : $"{track.ArtistsDisplay} · {track.YearDisplay}";
                    AddToBucket(album, sub, track);
                    break;
                }
                case ("library", "genres"):
                {
                    string genre = string.IsNullOrWhiteSpace(track.Genre) ? "(未标注流派)" : track.Genre;
                    AddToBucket(genre, "流派切片", track);
                    break;
                }
                case ("library", "folders"):
                {
                    string folder = string.IsNullOrWhiteSpace(track.AlbumDirectory)
                        ? Path.GetDirectoryName(track.RelPath)?.Replace('\\', '/') ?? "(根目录)"
                        : track.AlbumDirectory.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(folder)) folder = "(根目录)";
                    AddToBucket(folder, "物理存储目录", track);
                    break;
                }
                case ("artists", "album_artists"):
                {
                    foreach (string artist in SplitPerformers(track.ArtistsDisplay))
                    {
                        AddToBucket(artist, "专辑艺人", track);
                    }
                    break;
                }
                case ("artists", "composers"):
                {
                    foreach (string composer in SplitPerformers(track.ComposersDisplay))
                    {
                        AddToBucket(composer, "作曲", track);
                    }
                    break;
                }
                case ("artists", "conductors"):
                {
                    foreach (string conductor in SplitPerformers(track.ConductorsDisplay))
                    {
                        AddToBucket(conductor, "指挥", track);
                    }
                    break;
                }
                case ("artists", "all_performers"):
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in SplitPerformers(track.ArtistsDisplay)
                                 .Concat(SplitPerformers(track.ComposersDisplay))
                                 .Concat(SplitPerformers(track.ConductorsDisplay)))
                    {
                        if (seen.Add(name))
                        {
                            AddToBucket(name, "演职员", track);
                        }
                    }
                    break;
                }
                case ("classical", "works"):
                {
                    if (!track.IsClassical && string.IsNullOrWhiteSpace(track.WorkName)) break;
                    string work = !string.IsNullOrWhiteSpace(track.WorkName)
                        ? track.WorkName
                        : (!string.IsNullOrWhiteSpace(track.WorkDisplay) ? track.WorkDisplay : track.Album);
                    if (string.IsNullOrWhiteSpace(work)) break;
                    string sub = string.IsNullOrWhiteSpace(track.ComposersDisplay) ? track.ArtistsDisplay : track.ComposersDisplay;
                    AddToBucket(work, string.IsNullOrWhiteSpace(sub) ? "古典作品" : sub, track);
                    break;
                }
                case ("classical", "composers"):
                {
                    foreach (string composer in SplitPerformers(track.ComposersDisplay))
                    {
                        AddToBucket(composer, "作曲家", track);
                    }
                    break;
                }
                case ("classical", "conductors"):
                {
                    foreach (string conductor in SplitPerformers(track.ConductorsDisplay))
                    {
                        AddToBucket(conductor, "指挥家", track);
                    }
                    break;
                }
                case ("classical", "ensembles"):
                {
                    if (!track.IsClassical) break;
                    foreach (string ensemble in SplitPerformers(track.ArtistsDisplay))
                    {
                        AddToBucket(ensemble, "乐团 / 独奏", track);
                    }
                    break;
                }
            }
        }

        string? prevKey = SelectedEntityGroup?.Key;
        bool sortAlphabetical = PrimaryNavId == "artists";
        IEnumerable<(string Title, string Subtitle, List<TrackItem> Tracks)> ordered = sortAlphabetical
            ? map.Values
                .OrderBy(v => v.Title.StartsWith('(') ? 1 : 0)
                .ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(1500)
            : map.Values
                .OrderByDescending(v => v.Tracks.Count)
                .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
                .Take(400);

        var groups = new List<EntityGroupItem>();
        foreach (var entry in ordered)
        {
            if (sortAlphabetical && entry.Tracks.Count > 1)
            {
                entry.Tracks.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title));
            }
            string badge = (PrimaryNavId, SecondarySubTab) == ("classical", "works")
                ? $"{entry.Tracks.Count} 轨 · {Math.Max(1, entry.Tracks.Select(t => t.Album).Distinct(StringComparer.OrdinalIgnoreCase).Count())} 版"
                : $"{entry.Tracks.Count} 首";
            groups.Add(new EntityGroupItem(entry.Title, entry.Title, entry.Subtitle, badge, entry.Tracks));
        }
        CurrentEntityGroups.ReplaceAll(groups);

        var restored = prevKey is not null
            ? CurrentEntityGroups.FirstOrDefault(g => string.Equals(g.Key, prevKey, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectEntityGroup(restored ?? CurrentEntityGroups.FirstOrDefault());
    }

    public static IReadOnlyList<string> SplitPerformers(string? raw) =>
        ArtistEntitySplitter.SplitPerformers(raw);

    private void UpdateClassicalAdaptiveState()
    {
        int total = _allTracks.Count;
        int classicalCount = total == 0 ? 0 : _allTracks.Count(t => t.IsClassical);
        double ratio = total > 0 ? (double)classicalCount / total : 0.0;
        ClassicalTrackRatio = ratio;
        ClassicalRatioText = $"{ratio:P1} ({classicalCount} / {total} 首 · ρ≥20% 自动展开)";
        ShowClassicalSidebarSubItems = AdaptiveClassicalHierarchyPolicy.ShouldShowSidebarSubItems(
            classicalCount,
            total,
            AlwaysExpandClassicalSubItems,
            IsSidebarExpanded
        );
    }

    private void UpdateInboxAuditBadge()
    {
        int stagingCount = Math.Max(_totalPendingStagingCount, StagingProposals.Count);
        int totalBadge = stagingCount + MissingTempoCount;
        InboxAuditBadgeCount = totalBadge;
        InboxAuditBadgeDisplayValue = Math.Min(999, totalBadge);
        HasInboxAuditBadge = totalBadge > 0;
        InboxAuditBadgeText = totalBadge > 999 ? "999+" : totalBadge.ToString();
    }

    [RelayCommand]
    public void ActivateDtdDeepFocusScene()
    {
        if (PrimaryNavId is not "scenes" and not "library" || SecondarySubTab is not "presets" and not "studio" and not "tracks")
        {
            PrimaryNavId = "scenes";
            SecondarySubTab = "presets";
            UpdateNavigationState();
        }
        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = "all";
            FilterScene = "focus";
            ExcludeVocal = true;
            FilterVocals = "all";
            FilterEnergy = "low";
            FilterTempoBucket = "all";
            FilterMoodCluster = "all";
            SelectedEnergyArc = "flat";
        });
        if (FilteredTracks.Count > 0) PlayFromBrowseView(FilteredTracks[0]);
    }

    [RelayCommand]
    public void ActivateDtdAfternoonRelaxScene()
    {
        if (PrimaryNavId is not "scenes" and not "library" || SecondarySubTab is not "presets" and not "studio" and not "tracks")
        {
            PrimaryNavId = "scenes";
            SecondarySubTab = "presets";
            UpdateNavigationState();
        }
        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = "all";
            FilterScene = "relax";
            ExcludeVocal = false;
            FilterVocals = "vocal";
            FilterEnergy = "all";
            FilterTempoBucket = "all";
            FilterMoodCluster = "calm_relaxed";
            SelectedEnergyArc = "flat";
        });
        if (FilteredTracks.Count > 0) PlayFromBrowseView(FilteredTracks[0]);
    }

    [RelayCommand]
    public void ActivateDtdNightSleepScene()
    {
        if (PrimaryNavId is not "scenes" and not "library" || SecondarySubTab is not "presets" and not "studio" and not "tracks")
        {
            PrimaryNavId = "scenes";
            SecondarySubTab = "presets";
            UpdateNavigationState();
        }
        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = "all";
            FilterScene = "all";
            ExcludeVocal = false;
            FilterVocals = "all";
            FilterEnergy = "all";
            FilterTempoBucket = "all";
            FilterMoodCluster = "all";
            SelectedEnergyArc = "energy_desc_sleep";
        });
        if (FilteredTracks.Count > 0) PlayFromBrowseView(FilteredTracks[0]);
    }

    public void ApplyFiveDimFilter()
    {
        IsViewingPlaylist = false;
        SelectedPlaylist = null;
        if (_allTracks.Count == 0)
        {
            FilteredTracks.ReplaceAll([]);
            MissingTempoCount = 0;
            HasMissingTempoBanner = false;
            MissingMetadataBannerText = "";
            CurrentViewHeader = "全量曲目表 (0)";
            ActiveFilterPillsSummary = "尚未加载本地曲库";
            return;
        }

        int missingTempo = _allTracks.Count(t => t.TempoBucket is null && t.TempoBpm is null);
        MissingTempoCount = missingTempo;
        HasMissingTempoBanner = missingTempo > 0 && PrimaryNavId == "inbox";
        MissingMetadataBannerText = missingTempo > 0
            ? $"待回填 Tempo: {missingTempo} 首 · 已启用空值通配防漏筛"
            : "全库 Tempo 元数据已就绪";
        UpdateInboxAuditBadge();

        var query = new FiveDimFilterQuery(
            Scene: FilterScene,
            Energy: FilterEnergy,
            Vocals: FilterVocals,
            ExcludeVocal: ExcludeVocal,
            TempoBucket: FilterTempoBucket,
            MoodCluster: FilterMoodCluster,
            SortArc: SelectedEnergyArc,
            TempoNullPassthrough: ScenesTempoNullPassthrough
        );

        IEnumerable<TrackItem> working;
        if (!FiveDimQueryBuilder.RequiresFilterOrSort(query))
        {
            working = _allTracks;
        }
        else
        {
            long nowDaysBase = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var tracksPayload = _allTracks.Select(t => new
            {
                track_uid = t.TrackUid,
                title = t.Title,
                dj_scene = t.DjScene,
                dj_vocals = t.DjVocals,
                dj_energy = t.DjEnergy,
                tempo_bucket = t.TempoBucket,
                moods = t.Moods,
                genre = t.Genre,
                rating = t.Rating,
                unplayed_days = t.LastPlayedTsMs > 0
                    ? (uint)Math.Max(0, (nowDaysBase - t.LastPlayedTsMs) / 86_400_000L)
                    : 0u,
            });

            string sceneJson = FiveDimQueryBuilder.BuildSceneCardJson(query);
            string tracksJson = JsonSerializer.Serialize(tracksPayload);

            var orderedUids = TonewiseCoreClient.EvaluateScene(sceneJson, tracksJson);
            var byUid = _allTracks.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
            var ordered = new List<TrackItem>(orderedUids.Count);
            foreach (string uid in orderedUids)
            {
                if (byUid.TryGetValue(uid, out var track))
                {
                    ordered.Add(track);
                }
            }
            working = ordered;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        working = LibraryNavMode switch
        {
            "classical" => working.Where(t => t.IsClassical),
            "rediscovery_90d" => working.Where(t => t.LastPlayedTsMs <= 0 || (nowMs - t.LastPlayedTsMs) >= 90L * 86_400_000L),
            "audit_missing_tempo" => working.Where(t => t.TempoBucket is null && t.TempoBpm is null),
            "audit_missing_scene" => working.Where(t => t.DjScene.Count == 0),
            "audit_missing_vocals" => working.Where(t => string.IsNullOrWhiteSpace(t.DjVocals) || string.Equals(t.DjVocals, "unknown", StringComparison.OrdinalIgnoreCase)),
            "audit_missing_cover" => working.Where(t => string.IsNullOrWhiteSpace(t.CoverArtPath)),
            "audit_missing_genre" => working.Where(t => string.IsNullOrWhiteSpace(t.Genre)),
            "audit_missing_composer" => working.Where(t => string.IsNullOrWhiteSpace(t.ComposersDisplay)),
            "audit_missing_conductor" => working.Where(t => string.IsNullOrWhiteSpace(t.ConductorsDisplay)),
            "audit_missing_album" => working.Where(t => string.IsNullOrWhiteSpace(t.Album)),
            "audit_missing_publisher" => working.Where(t => string.IsNullOrWhiteSpace(t.Publisher)),
            _ => working,
        };

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            string q = SearchQuery.Trim();
            working = working.Where(t =>
                t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.ArtistsDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Album.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.ComposersDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.ConductorsDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.WorkName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Genre.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.DjScene.Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase))
                || t.Moods.Any(m => m.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        working = ApplyColumnSort(working);
        FilteredTracks.ReplaceAll(working);

        string navLabel = LibraryNavMode switch
        {
            "classical" => "古典作品集",
            "rediscovery_90d" => "90天未听重温",
            "audit_missing_tempo" => "体检 · 缺失 Tempo",
            "audit_missing_scene" => "体检 · 缺失场景",
            "audit_missing_vocals" => "体检 · 缺失人声标签",
            "audit_missing_cover" => "体检 · 缺失封面",
            "audit_missing_genre" => "体检 · 缺失流派",
            "audit_missing_composer" => "体检 · 缺失作曲",
            "audit_missing_conductor" => "体检 · 缺失指挥",
            "audit_missing_album" => "体检 · 缺失专辑",
            "audit_missing_publisher" => "体检 · 缺失厂牌",
            _ => "全量曲目表",
        };
        CurrentViewHeader = $"{navLabel} ({FilteredTracks.Count} / {_allTracks.Count})";

        var pills = new List<string>();
        if (FilterScene != "all") pills.Add($"SCENE: {FilterScene}");
        if (ExcludeVocal) pills.Add("NOT vocal");
        else if (FilterVocals != "all") pills.Add($"VOCALS: {FilterVocals}");
        if (FilterEnergy != "all") pills.Add($"ENERGY: {FilterEnergy}");
        if (FilterTempoBucket != "all") pills.Add($"Tempo: {FilterTempoBucket}");
        if (FilterMoodCluster != "all") pills.Add($"Mood: {FilterMoodCluster}");
        if (!ScenesTempoNullPassthrough) pills.Add("严格校验Tempo非空");
        if (SelectedEnergyArc != "flat") pills.Add($"Arc: {SelectedEnergyArc}");
        if (!string.IsNullOrWhiteSpace(SearchQuery)) pills.Add($"检索: \"{SearchQuery.Trim()}\"");
        ActiveFilterPillsSummary = pills.Count > 0 ? string.Join(" · ", pills) : "";

        UpdateScenesStudioAndArcTelemetry();
        UpdateFilterDrawerState();
    }

    private void UpdateFilterDrawerState() =>
        IsFilterDrawerOpen = (PrimaryNavId == "scenes" && SecondarySubTab == "presets")
                             || (IsFiveDimFilterStripVisible && IsFilterStripExpanded);

    [RelayCommand]
    private void ToggleFilterStrip()
    {
        IsFilterStripExpanded = !IsFilterStripExpanded;
        UpdateFilterDrawerState();
    }

    public void SetHasInlineTarget(bool hasTarget)
    {
        HasInspectedTrack = hasTarget || InspectedTrack is not null;
        HasNoInspectedTrack = !HasInspectedTrack;
    }

    public void ToggleTrackSort(string columnId)
    {
        if (string.IsNullOrWhiteSpace(columnId)) return;
        if (string.Equals(TrackSortColumn, columnId, StringComparison.OrdinalIgnoreCase))
        {
            TrackSortAscending = !TrackSortAscending;
        }
        else
        {
            TrackSortColumn = columnId;
            TrackSortAscending = !string.Equals(columnId, "playcount", StringComparison.OrdinalIgnoreCase);
        }
        UpdateSortHeaderLabels();
        FilteredTracks.ReplaceAll(ApplyColumnSort(FilteredTracks.ToList()));
    }

    private void UpdateSortHeaderLabels()
    {
        string Arrow(string col, string baseLabel) =>
            string.Equals(TrackSortColumn, col, StringComparison.OrdinalIgnoreCase)
                ? (TrackSortAscending ? $"{baseLabel} ↑" : $"{baseLabel} ↓")
                : baseLabel;

        HeaderTitleText = Arrow("title", "曲目 / 专辑");
        HeaderArtistText = Arrow("artist", "艺人");
        HeaderComposerText = Arrow("composer", "作曲 / 作品 · 指挥");
        HeaderYearText = Arrow("year", "年份");
        HeaderSceneText = Arrow("scene", "场景 · 情绪");
        HeaderEnergyText = Arrow("energy", "能量 · 节奏");
        HeaderPlayCountText = Arrow("playcount", "播放");
        HeaderDurationText = Arrow("duration", "时长");
    }

    private IEnumerable<TrackItem> ApplyColumnSort(IEnumerable<TrackItem> items)
    {
        if (string.IsNullOrWhiteSpace(TrackSortColumn)) return items;
        var list = items.ToList();
        if (list.Count <= 1) return list;

        int Sign(bool asc) => asc ? 1 : -1;
        int dir = Sign(TrackSortAscending);

        int CompareStringsEmptyLast(string? a, string? b)
        {
            bool emptyA = string.IsNullOrWhiteSpace(a);
            bool emptyB = string.IsNullOrWhiteSpace(b);
            if (emptyA && emptyB) return 0;
            if (emptyA) return 1; // keep empty at bottom regardless of sort direction
            if (emptyB) return -1;
            return dir * StringComparer.CurrentCultureIgnoreCase.Compare(a!.Trim(), b!.Trim());
        }

        int CompareZeroLast(int a, int b)
        {
            bool zeroA = a <= 0;
            bool zeroB = b <= 0;
            if (zeroA && zeroB) return 0;
            if (zeroA) return 1;
            if (zeroB) return -1;
            return dir * a.CompareTo(b);
        }

        int CompareEnergyTempo(TrackItem x, TrackItem y)
        {
            static double Score(TrackItem t) =>
                t.TempoBpm is > 0
                    ? t.TempoBpm.Value
                    : (t.DjEnergy ?? "").ToLowerInvariant() switch
                    {
                        "high" => 130.0,
                        "medium" => 95.0,
                        "low" => 65.0,
                        _ => -1.0,
                    };
            double sx = Score(x), sy = Score(y);
            if (sx < 0 && sy < 0) return 0;
            if (sx < 0) return 1;
            if (sy < 0) return -1;
            return dir * sx.CompareTo(sy);
        }

        list.Sort((x, y) =>
        {
            int cmp = TrackSortColumn.ToLowerInvariant() switch
            {
                "title" => CompareStringsEmptyLast(x.Title, y.Title),
                "artist" => CompareStringsEmptyLast(x.ArtistsDisplay, y.ArtistsDisplay),
                "composer" => CompareStringsEmptyLast(
                    string.IsNullOrWhiteSpace(x.ComposersDisplay) ? x.WorkAndConductorSubline : x.ComposersDisplay,
                    string.IsNullOrWhiteSpace(y.ComposersDisplay) ? y.WorkAndConductorSubline : y.ComposersDisplay),
                "year" => CompareZeroLast(x.Year ?? 0, y.Year ?? 0),
                "scene" => CompareStringsEmptyLast(
                    string.IsNullOrWhiteSpace(x.SceneBadge) ? x.MoodBadge : x.SceneBadge,
                    string.IsNullOrWhiteSpace(y.SceneBadge) ? y.MoodBadge : y.SceneBadge),
                "energy" => CompareEnergyTempo(x, y),
                "playcount" => CompareZeroLast(
                    x.EffectivePlayCount > 0 ? x.EffectivePlayCount * 10 + x.Rating : (x.Rating >= 5 ? 5 : 0),
                    y.EffectivePlayCount > 0 ? y.EffectivePlayCount * 10 + y.Rating : (y.Rating >= 5 ? 5 : 0)),
                "duration" => CompareZeroLast(x.DurationMs, y.DurationMs),
                _ => 0,
            };
            return cmp != 0 ? cmp : StringComparer.CurrentCultureIgnoreCase.Compare(x.Title, y.Title);
        });
        return list;
    }

    private void UpdateScenesStudioAndArcTelemetry()
    {
        var clauses = new List<string>();
        if (FilterScene != "all") clauses.Add($"scene = '{FilterScene}'");
        if (ExcludeVocal) clauses.Add("vocals != 'vocal'");
        else if (FilterVocals != "all") clauses.Add($"vocals = '{FilterVocals}'");
        if (FilterEnergy != "all") clauses.Add($"energy = '{FilterEnergy}'");
        if (FilterTempoBucket != "all") clauses.Add($"tempo = '{FilterTempoBucket}'");
        if (FilterMoodCluster != "all") clauses.Add($"mood ∈ {FilterMoodCluster}");
        string expr = clauses.Count > 0 ? string.Join(" AND ", clauses) : "ALL TRACKS (全库匹配)";
        ScenesRuleDslPreview = $"规则表达式: {expr}"
            + (ScenesTempoNullPassthrough ? " · Tempo IS NULL 放行" : " · 严格过滤空 Tempo")
            + $" · 命中 {FilteredTracks.Count} 首";

        int low = 0, med = 0, high = 0, unknown = 0;
        foreach (var t in FilteredTracks)
        {
            if (string.Equals(t.DjEnergy, "low", StringComparison.OrdinalIgnoreCase)) low++;
            else if (string.Equals(t.DjEnergy, "medium", StringComparison.OrdinalIgnoreCase)) med++;
            else if (string.Equals(t.DjEnergy, "high", StringComparison.OrdinalIgnoreCase)) high++;
            else unknown++;
        }
        ScenesEnergyDistributionText = $"当前队列能量构成: 低能量 {low} 首 · 中能量 {med} 首 · 高能量 {high} 首 · 待标注 {unknown} 首";
    }

    private void UpdateArcStateFlags()
    {
        IsArcFlat = string.Equals(SelectedEnergyArc, "flat", StringComparison.OrdinalIgnoreCase);
        IsArcAsc = string.Equals(SelectedEnergyArc, "energy_asc", StringComparison.OrdinalIgnoreCase);
        IsArcSleep = string.Equals(SelectedEnergyArc, "energy_desc_sleep", StringComparison.OrdinalIgnoreCase);
        IsArcRediscovery = string.Equals(SelectedEnergyArc, "rediscovery_90d", StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnergyArc(string arcId)
    {
        SelectedEnergyArc = arcId switch
        {
            "energy_asc" or "energy_desc_sleep" or "rediscovery_90d" => arcId,
            _ => "flat",
        };
    }

    [RelayCommand]
    private void PlayFilteredFromStart()
    {
        if (FilteredTracks.Count == 0)
        {
            Notify("当前结果集为空，无法播放。", "warning");
            return;
        }
        PlayFromBrowseView(FilteredTracks[0]);
    }

    public void InspectTrack(TrackItem? track)
    {
        InspectedTrack = track;
        HasInspectedTrack = track is not null;
        HasNoInspectedTrack = track is null;
        if (track is null)
        {
            InspectorHeaderTitle = "";
            InspectorHeaderSubtitle = "";
            InspectorHeaderSubline = "";
            CurrentBookletPath = null;
            HasBookletPdf = false;
            AlternateRecordings.Clear();
            HasAlternateRecordings = false;
            HasInspectedComposer = false;
            HasInspectedConductor = false;
            HasInspectedPublisher = false;
            CurrentBiographySummary = "选择任意曲目以查看本地古典乐章层级、演职员穿透与百科注释。";
            TrackChanged?.Invoke(null);
            return;
        }

        InspectorHeaderTitle = track.Title;
        InspectorHeaderSubtitle = track.ArtistsDisplay;
        InspectorHeaderSubline = track.ClassicalFourLevelSummary;
        CurrentBookletPath = track.BookletPdfPath;
        HasBookletPdf = !string.IsNullOrWhiteSpace(track.BookletPdfPath) && File.Exists(track.BookletPdfPath);
        UpdateClassicalAndBiographyPanel(track);
        TrackChanged?.Invoke(track);
    }

    public void PlayFromBrowseView(TrackItem track)
    {
        PlaybackQueue.Clear();
        _playbackHistory.Clear();
        _djFreezeBackup = null;
        _currentFreezeToken = "";
        IsDjFreezeActive = false;

        foreach (var item in FilteredTracks)
        {
            if (!string.IsNullOrWhiteSpace(item.FullPath))
            {
                PlaybackQueue.Add(item);
            }
        }
        _unshuffledQueueSnapshot = [.. PlaybackQueue];
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
        PlaybackQueue.Clear();
        _unshuffledQueueSnapshot.Clear();
        _playbackHistory.Clear();
        _djFreezeBackup = null;
        _currentFreezeToken = "";
        IsDjFreezeActive = false;
        PlaybackQueueTitle = "播放队列为空";
        _engine.ClearPreloadedNext();
        UpdatePlaybackQueueSummary();
        PersistPlaybackSessionSnapshot(force: true);
    }

    [RelayCommand]
    private void LoadCurrentViewToQueue()
    {
        PlaybackQueue.Clear();
        _djFreezeBackup = null;
        _currentFreezeToken = "";
        IsDjFreezeActive = false;
        foreach (var item in FilteredTracks)
        {
            if (!string.IsNullOrWhiteSpace(item.FullPath))
            {
                PlaybackQueue.Add(item);
            }
        }
        _unshuffledQueueSnapshot = [.. PlaybackQueue];
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
            t.MovementNumber,
            t.RelPath)).ToList();
        int[] order = WorkAwareShuffleEngine.BuildWorkAwareOrder(descriptors, anchorIdx, _shuffleRandom);
        var reordered = new List<TrackItem>(order.Length);
        foreach (int idx in order) reordered.Add(list[idx]);
        ReplaceQueueItems(reordered);
    }

    private void ReplaceQueueItems(IReadOnlyList<TrackItem> items)
    {
        PlaybackQueue.Clear();
        foreach (var item in items)
        {
            PlaybackQueue.Add(item);
        }
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
            if (CurrentTrack is not null && !string.IsNullOrWhiteSpace(CurrentTrack.FullPath))
            {
                PlaybackQueue.Add(CurrentTrack);
            }
            foreach (var t in playable) PlaybackQueue.Add(t);
            _unshuffledQueueSnapshot = [.. PlaybackQueue];
            PlaybackQueueTitle = "自定义播放队列";
        }
        else
        {
            int curIdx = CurrentTrack is null ? -1 : FindQueueIndex(PlaybackQueue, CurrentTrack);
            int insertIdx = curIdx >= 0 ? Math.Min(curIdx + 1, PlaybackQueue.Count) : 0;
            for (int i = 0; i < playable.Count; i++)
            {
                PlaybackQueue.Insert(insertIdx + i, playable[i]);
            }
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

        if (PlaybackQueue.Count == 0 && CurrentTrack is not null && !string.IsNullOrWhiteSpace(CurrentTrack.FullPath))
        {
            PlaybackQueue.Add(CurrentTrack);
            PlaybackQueueTitle = "自定义播放队列";
        }
        foreach (var t in playable)
        {
            PlaybackQueue.Add(t);
        }
        if (!IsShuffleEnabled) _unshuffledQueueSnapshot = [.. PlaybackQueue];
        PreloadNextGaplessTrack();
        PersistPlaybackSessionSnapshot(force: true);
        StatusMessage = playable.Count == 1
            ? $"已追加到队尾: {playable[0].Title} (队列 {PlaybackQueue.Count} 首)"
            : $"已追加 {playable.Count} 首到队尾 (队列 {PlaybackQueue.Count} 首)";
    }

    public void PlayTrack(TrackItem track, double initialSeekFraction = 0.0, bool startPaused = false)
    {
        EvaluateAndCommitPlayCountIfQualified(completedNaturally: false);

        var previousTrack = CurrentTrack;
        CurrentTrack = track;
        _pendingRestorePositionSec = 0.0;
        CurrentWaveform = _store.LoadWaveform(track);
        if (InspectedTrack is null || ReferenceEquals(InspectedTrack, previousTrack) || InspectedTrack.TrackUid == previousTrack?.TrackUid)
        {
            InspectTrack(track);
        }
        WaveformUpdated?.Invoke();

        PushEnginePreferences();
        if (LongPathHelper.FileExists(track.FullPath))
        {
            try
            {
                _engine.PlayFile(track.FullPath);
                if (initialSeekFraction > 0.0)
                {
                    _engine.Seek(Math.Clamp(initialSeekFraction, 0.0, 0.99));
                }
                if (startPaused)
                {
                    _engine.Pause();
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
                return;
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
        }
        else
        {
            IsPlaying = false;
            StatusMessage = string.IsNullOrWhiteSpace(track.FullPath)
                ? $"本地曲库未匹配：{track.ArtistsDisplay} — {track.Title}"
                : $"文件不存在：{track.FullPath}";
        }
    }

    public void SwitchAlternateRecording(TrackItem alternate)
    {
        double ratio = PlaybackProgress;
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
            t => t.MovementName
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
        IList<TrackItem> source = PlaybackQueue.Count > 0 ? PlaybackQueue : FilteredTracks;
        if (source.Count == 0) return;

        bool shouldPause = !autoAdvance && !IsPlaying && RememberPauseOnSkip;
        TrackItem? candidate = ResolveNextCandidate(source, autoAdvance, allowReshuffle: true);
        if (candidate is null)
        {
            EvaluateAndCommitPlayCountIfQualified(completedNaturally: autoAdvance);
            IsPlaying = false;
            _engine.ClearPreloadedNext();
            PersistPlaybackSessionSnapshot(force: true);
            StatusMessage = "当前列表已顺序播放完毕。";
            return;
        }
        PlayTrack(candidate, startPaused: shouldPause);
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
                        t.TrackUid, t.Album, t.WorkName, t.MovementNumber, t.RelPath)).ToList();
                    int[] order = WorkAwareShuffleEngine.BuildWorkAwareOrder(descriptors, -1, _shuffleRandom);
                    var reshuffled = order.Select(i => list[i]).ToList();
                    if (CurrentTrack is not null && reshuffled.Count > 1
                        && string.Equals(reshuffled[0].TrackUid, CurrentTrack.TrackUid, StringComparison.Ordinal))
                    {
                        (reshuffled[0], reshuffled[^1]) = (reshuffled[^1], reshuffled[0]);
                    }
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

        string nextPath = next.FullPath;
        double nextRg = ResolveReplayGainForTrack(next);
        _ = Task.Run(() => _engine.PreloadNextFile(nextPath, nextRg));
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

    public void RefreshProgress()
    {
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
            IsPlaying = false;
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

        IsPlaying = _engine.IsPlaying;
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

    private void EvaluateAndCommitPlayCountIfQualified(bool completedNaturally)
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
    public void ToggleCurrentTrackLoved()
    {
        if (CurrentTrack is null) return;
        bool nextLoved = !CurrentTrack.IsLoved;
        ApplyLexiconTagsToTracks([CurrentTrack], new Dictionary<string, string?>
        {
            ["rating"] = nextLoved ? "5" : "0",
        });
        Notify(nextLoved ? $"已喜爱「{CurrentTrack.Title}」（将同步至 Last.fm）" : $"已取消喜爱「{CurrentTrack.Title}」", "success");
    }

    public void ToggleLovedForTracks(IReadOnlyList<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;
        bool nextLoved = !tracks[0].IsLoved;
        ApplyLexiconTagsToTracks(tracks, new Dictionary<string, string?>
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

        HasFreezeCapsule = false;
        CurrentFreezeCapsuleLabel = "";
    }

    [ObservableProperty]
    private string _activeDjFreezeLabel = "";

    [RelayCommand]
    public void ToggleDjFreeze()
    {
        if (CurrentTrack is null || string.IsNullOrWhiteSpace(_currentFreezeToken)) return;

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

        var parts = _currentFreezeToken.Split(':', 2);
        string kind = parts[0];
        string val = parts.Length > 1 ? parts[1] : "";
        var matches = _allTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FullPath)
                        && !string.Equals(t.TrackUid, CurrentTrack.TrackUid, StringComparison.Ordinal)
                        && (kind switch
                        {
                            "scene" => t.DjScene.Any(s => string.Equals(s.Trim(), val, StringComparison.OrdinalIgnoreCase)),
                            "mood" => t.Moods.Any(m => string.Equals(m.Trim(), val, StringComparison.OrdinalIgnoreCase)),
                            "vocals" => string.Equals(t.DjVocals?.Trim(), val, StringComparison.OrdinalIgnoreCase),
                            _ => false,
                        }))
            .OrderBy(_ => _shuffleRandom.Next())
            .Take(80)
            .ToList();

        if (matches.Count == 0)
        {
            Notify("未找到更多匹配该氛围标签的本地曲目。", "warning");
            return;
        }

        _djFreezeBackup = ([.. PlaybackQueue], PlaybackQueueTitle, _currentFreezeToken);
        var frozenQueue = new List<TrackItem>(matches.Count + 1) { CurrentTrack };
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

    private void PersistPlaybackSessionSnapshot(bool force)
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
            ApplyFiveDimFilter();
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
            ApplyFiveDimFilter();
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
            ApplyFiveDimFilter();
            RefreshStagingProposals();
            RefreshPlaylists();

            StorageOrganizeStatus =
                $"整理完成：检查 {res.TotalChecked:N0} 首，迁移规范 {res.MovedCount:N0} 首（收敛超长路径 {res.LongPathFixedCount:N0} 首），清理失效索引 {res.StaleRemovedCount:N0} 条，已规范跳过 {res.SkippedCount:N0} 首。";
            StatusMessage = StorageOrganizeStatus;
            Notify(StorageOrganizeStatus, res.MovedCount > 0 || res.StaleRemovedCount > 0 ? "success" : "info");
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

    private void RestartLibraryFileWatcher(string? musicRoot)
    {
        Interlocked.Exchange(ref _libraryWatcher, null)?.Dispose();

        if (!EnableAutoWatchLibrary)
        {
            FileWatcherStatusText = "实时监测已关闭（可随时点击「重建曲库索引」手动同步）。";
            return;
        }
        if (string.IsNullOrWhiteSpace(musicRoot) || !LongPathHelper.DirectoryExists(musicRoot))
        {
            FileWatcherStatusText = "等待打开曲库后启动实时监测...";
            return;
        }

        try
        {
            _libraryWatcher = new LibraryFileWatcherService(musicRoot, changed => OnLibraryFilesChangedAsync(musicRoot, changed));
            FileWatcherStatusText = $"实时监测运行中（{musicRoot}）：自动同步新增、移动、重命名或删除的曲目与歌单。";
        }
        catch (Exception ex)
        {
            FileWatcherStatusText = $"实时监测启动失败：{ex.Message}";
        }
    }

    private async Task OnLibraryFilesChangedAsync(string expectedRoot, IReadOnlyList<string> changedPaths)
    {
        if (!EnableAutoWatchLibrary || IsBusy) return;
        if (!string.Equals(_store.MusicRoot, expectedRoot, StringComparison.OrdinalIgnoreCase)) return;

        var loaded = await Task.Run(() =>
        {
            _store.ScanAndBuildManifestIfMissing(expectedRoot, forceRebuild: true, fastMode: true);
            return _store.LoadFromMusicRoot(expectedRoot).ToList();
        }).ConfigureAwait(false);

        void ApplyWatcherUiUpdate()
        {
            if (!EnableAutoWatchLibrary || !string.Equals(_store.MusicRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
                return;

            _allTracks = loaded;
            UpdateClassicalAdaptiveState();
            TriggerFilterRefresh();
            RefreshStagingProposals();
            RefreshPlaylists();

            FileWatcherStatusText =
                $"实时监测运行中 · 最近自动同步 {DateTime.Now:HH:mm:ss}（捕获 {changedPaths.Count} 处变更，当前共 {_allTracks.Count:N0} 首）";
            StatusMessage = $"曲库文件自动监测：已同步 {changedPaths.Count} 处变更，当前共 {_allTracks.Count:N0} 首曲目。";
        }

        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
            _uiContext.Post(_ => ApplyWatcherUiUpdate(), null);
        else
            ApplyWatcherUiUpdate();
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

    private bool IsCurrentQueueSingleAlbum()
    {
        if (CurrentTrack is null || string.IsNullOrWhiteSpace(CurrentTrack.Album)) return false;
        IList<TrackItem> q = PlaybackQueue.Count > 0 ? PlaybackQueue : FilteredTracks;
        if (q.Count <= 1) return !IsShuffleEnabled;
        string album = CurrentTrack.Album.Trim();
        int sameAlbum = 0;
        int sampleCap = Math.Min(q.Count, 12);
        for (int i = 0; i < sampleCap; i++)
        {
            if (string.Equals(q[i].Album?.Trim(), album, StringComparison.OrdinalIgnoreCase))
                sameAlbum++;
        }
        return !IsShuffleEnabled && sameAlbum >= sampleCap;
    }

    private double ResolveReplayGainForTrack(TrackItem? track)
    {
        if (track is null) return 0.0;
        return ReplayGainResolver.ResolveGainDb(
            ReplayGainMode,
            track.ReplayGainTrackDb,
            track.ReplayGainAlbumDb,
            IsCurrentQueueSingleAlbum(),
            ReplayGainPreAmpWithTagDb,
            ReplayGainPreAmpWithoutTagDb);
    }

    private void PushEnginePreferences()
    {
        var extDsp = BuildCurrentExtendedDspConfig();
        _engine.SetExtendedDsp(extDsp);
        var activeProfile = _usingExternalEqFile
            ? _eqProfile
            : (extDsp.Enabled ? extDsp.ToEqualizerApoProfile() : null);
        _engine.SetPreferences(
            PreferWasapiExclusive,
            SelectedEndpoint?.Id,
            activeProfile,
            ResolveReplayGainForTrack(CurrentTrack)
        );
        PushAcousticPipeline();
    }

    private void PushAcousticPipeline()
    {
        _engine.SetAcousticPipeline(
            iso226Enabled: IsIso226Enabled,
            referenceSplDb: _eqProfile?.ReferenceSplDb ?? 80.0,
            volumeAttenuationDb: VolumeAttenuationDb,
            tpdfDitherEnabled: IsTpdfDitherEnabled,
            firDryWetMix: FirDryWetMix
        );
    }

    private SyncOutboxAndPairingStore? EnsureSyncStore()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot)) return null;
        return _syncStore ??= new SyncOutboxAndPairingStore(_store.MusicRoot);
    }

    [RelayCommand]
    private async Task ToggleLanSyncHostAsync()
    {
        if (_syncHost is not null)
        {
            _syncHost.SyncStateChanged -= OnSyncHostStateChanged;
            await _syncHost.DisposeAsync();
            _syncHost = null;
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

    private void OnSyncHostStateChanged()
    {
        if (_uiContext is not null)
            _uiContext.Post(_ => RefreshLanSyncUiState(), null);
        else
            RefreshLanSyncUiState();
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
                string text = $"待同步：{summary.ItemCount} 项 · 解析后共 {summary.TrackCount:N0} 首曲目 · {mbText}";
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

    public async Task SendPlaylistToPhoneAsync(PlaylistSummary? playlist)
    {
        if (playlist is null)
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
        store.EnqueuePlaylist(playlist.Name, mode: "subscribe");
        await EnsureLanSyncHostRunningAsync();
        var summary = await Task.Run(() => store.GetOutboxSummary());
        RefreshLanSyncUiState();
        Notify($"已将歌单「{playlist.Name}」加入手机同步队列（订阅 · 共 {summary.TrackCount} 首 · {summary.TotalBytes / (1024.0 * 1024.0):F1} MB）", "success");
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

    public IReadOnlyList<LocalLibraryTrackRef> GetLibraryRefs() =>
        _allTracks.Select(t => new LocalLibraryTrackRef(
            t.TrackUid,
            t.RelPath,
            t.FullPath,
            t.Title,
            t.ArtistsDisplay,
            t.Album,
            t.DurationMs,
            t.SampleRateHz
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

    public void DeleteSelectedPlaylist()
    {
        if (SelectedPlaylist is null) return;
        string deletedName = SelectedPlaylist.Name;
        PlaylistStore.DeletePlaylist(SelectedPlaylist.FilePath);
        RefreshPlaylists();
        SelectPlaylist(null);
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

    public void Dispose()
    {
        AccumulateListeningTime();
        EvaluateAndCommitPlayCountIfQualified(completedNaturally: false);
        PersistPlaybackSessionSnapshot(force: true);
        Interlocked.Exchange(ref _libraryWatcher, null)?.Dispose();
        var host = Interlocked.Exchange(ref _syncHost, null);
        if (host is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort non-blocking shutdown on window close
                }
            });
        }
        _engine.Dispose();
    }
}
