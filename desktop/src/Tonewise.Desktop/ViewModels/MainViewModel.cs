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
using Tonewise.SyncHost.Usb;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibraryStore _store = new();
    private readonly TonewiseAudioEngine _engine = new();
    private readonly SleepTimerEngine _sleepTimer = new();
    private readonly AudioDeviceChangeWatcher _deviceChangeWatcher = new();
    private readonly SynchronizationContext? _uiContext;
    private SyncHostServer? _syncHost;
    private SyncOutboxAndPairingStore? _syncStore;
    private UsbAutoSyncCoordinator? _usbCoordinator;
    private string? _customAdbPath;
    private System.Threading.Timer? _pairingCodeRefreshTimer;
    private System.Threading.Timer? _endpointDebounceTimer;
    private int _disposed;
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
    public BulkObservableCollection<TrackItem> PlaybackQueue { get; } = [];
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

    [ObservableProperty]
    private string _breadcrumbTrail = "";

    [ObservableProperty]
    private string _lanSyncTransferText = "Wi-Fi 局域网传输空闲（与 USB 同步进度独立显示）";

    [ObservableProperty]
    private bool _isLanSyncTransferring;
    public ObservableCollection<PairedDeviceViewItem> PairedDevices { get; } = [];
    public ObservableCollection<SyncOutboxViewItem> SyncOutboxItems { get; } = [];
    public ObservableCollection<QuickplayTileItem> QuickplayPins { get; } = [];
    public ObservableCollection<QuickplayTileItem> QuickplayHistory { get; } = [];
    public ObservableCollection<QuickplayTileItem> QuickplayNewAlbums { get; } = [];
    public ObservableCollection<QuickplayTileItem> QuickplayRediscovery { get; } = [];

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
    private bool _isAutoEqEnabled;

    [ObservableProperty]
    private bool _isAutoEqClassicalCrossfeedEnabled = true;

    [ObservableProperty]
    private bool _isAutoEqLockClassicalWorkEnabled = true;

    [ObservableProperty]
    private string _activeAutoEqBadge = "自动内容 EQ：已关闭";

    [ObservableProperty]
    private string _selectedAutoEqGenre = "classical";

    [ObservableProperty]
    private string _selectedAutoEqVocal = "instrumental";

    [ObservableProperty]
    private string _autoEqRuleCrossfeedMode = "auto";

    [ObservableProperty]
    private string _selectedAutoEqRuleStatusText = "默认规则";

    [ObservableProperty]
    private double _autoEqRuleBand0Db;

    [ObservableProperty]
    private double _autoEqRuleBand1Db;

    [ObservableProperty]
    private double _autoEqRuleBand2Db;

    [ObservableProperty]
    private double _autoEqRuleBand3Db;

    [ObservableProperty]
    private double _autoEqRuleBand4Db;

    [ObservableProperty]
    private double _autoEqRuleBand5Db;

    [ObservableProperty]
    private double _autoEqRuleBand6Db;

    [ObservableProperty]
    private double _autoEqRuleBand7Db;

    [ObservableProperty]
    private double _autoEqRuleBand8Db;

    [ObservableProperty]
    private double _autoEqRuleBand9Db;

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
    private string _sleepTimerBadge = "";

    [ObservableProperty]
    private bool _isSleepTimerActive;

    public event Action? SleepTimerExpired;

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
    private bool _lockVolumeOnBitPerfect = true;

    [ObservableProperty]
    private bool _rememberVolumePerEndpoint = true;

    [ObservableProperty]
    private bool _isVolumeLockedByBitPerfect;

    [ObservableProperty]
    private bool _isSoftwareVolumeControlEnabled = true;

    [ObservableProperty]
    private bool _isEndpointVolumeSafetyPromptVisible;

    [ObservableProperty]
    private string _endpointVolumeSafetyPromptText = "";

    private readonly Dictionary<string, double> _endpointVolumesDb = new(StringComparer.OrdinalIgnoreCase);
    private double? _pendingRestoreEndpointVolumeDb;
    private string? _pendingRestoreEndpointKey;
    private int? _mainWindowX;
    private int? _mainWindowY;
    private int? _mainWindowWidth;
    private int? _mainWindowHeight;
    private bool _mainWindowMaximized;

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

    private readonly List<string> _selectedGenres = [];

    [ObservableProperty]
    private string _genreFilterChipLabel = "流派";

    [ObservableProperty]
    private bool _isGenreFilterActive;

    [ObservableProperty]
    private string _sceneFilterChipLabel = "场景";

    [ObservableProperty]
    private bool _isSceneFilterActive;

    [ObservableProperty]
    private string _energyFilterChipLabel = "能量";

    [ObservableProperty]
    private bool _isEnergyFilterActive;

    [ObservableProperty]
    private string _vocalsFilterChipLabel = "人声";

    [ObservableProperty]
    private bool _isVocalsFilterActive;

    [ObservableProperty]
    private string _tempoFilterChipLabel = "节奏";

    [ObservableProperty]
    private bool _isTempoFilterActive;

    [ObservableProperty]
    private string _moodFilterChipLabel = "情绪";

    [ObservableProperty]
    private bool _isMoodFilterActive;

    [ObservableProperty]
    private string _arcFilterChipLabel = "曲线";

    [ObservableProperty]
    private bool _isArcFilterActive;

    [ObservableProperty]
    private string _tempoPassthroughChipLabel = "空Tempo: 透传";

    [ObservableProperty]
    private bool _hasAnyActiveStripFilter;

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
    private bool _isUsbAutoSyncEnabled = true;

    [ObservableProperty]
    private bool _isMirrorPruneEnabled = false;

    [ObservableProperty]
    private string _mirrorPruneScope = "managed_only";

    [ObservableProperty]
    private bool _allowAdbForeignDeleteFallback = false;

    [ObservableProperty]
    private string _usbAdbStatusText = "正在检测 ADB 环境...";

    [ObservableProperty]
    private string _usbConnectionSummaryText = "未检测到 USB 连接手机";

    [ObservableProperty]
    private string _usbSyncProgressText = "";

    [ObservableProperty]
    private double _usbSyncProgressPercent;

    [ObservableProperty]
    private bool _isUsbSyncRunning;

    [ObservableProperty]
    private bool _hasConnectedUsbDevice;

    [ObservableProperty]
    private bool _hasPendingLargePruneConfirmation;

    [ObservableProperty]
    private string _pendingLargePruneMessage = "";

    [ObservableProperty]
    private int _outboxMissingSourceCount;

    public bool HasOutboxMissingSources => OutboxMissingSourceCount > 0;

    public bool IsMirrorPruneScopeAllMusic
    {
        get => !string.Equals(MirrorPruneScope, "managed_only", StringComparison.OrdinalIgnoreCase);
        set
        {
            string next = value ? "music_folder_all" : "managed_only";
            if (!string.Equals(MirrorPruneScope, next, StringComparison.OrdinalIgnoreCase))
            {
                MirrorPruneScope = next;
            }
        }
    }

    public string MirrorPruneScopeDescription => IsMirrorPruneScopeAllMusic
        ? "范围：整个手机 Music 目录（未在同步列表的曲目都会被清理）"
        : "范围：仅 Tonewise 托管同步记录（保留手机原有外部曲目）";

    partial void OnOutboxMissingSourceCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasOutboxMissingSources));
    }

    [ObservableProperty]
    private bool _hasSelectedEntityGroup;

    [ObservableProperty]
    private bool _isPlaylistMultiSelectMode;

    [ObservableProperty]
    private int _selectedPlaylistCount;

    [ObservableProperty]
    private bool _isPlaylistsExpanded;

    [ObservableProperty]
    private string _playlistsCountBadge = "共 0 个歌单";

    [RelayCommand]
    private void TogglePlaylistsExpanded() => IsPlaylistsExpanded = !IsPlaylistsExpanded;

    [ObservableProperty]
    private string _auditMissingGenreLabel = "缺流派 (0)";

    [ObservableProperty]
    private string _auditMissingComposerLabel = "缺作曲 (0)";

    [ObservableProperty]
    private string _auditMissingConductorLabel = "缺指挥 (0)";

    [ObservableProperty]
    private string _auditMissingAlbumLabel = "缺专辑 (0)";

    [ObservableProperty]
    private string _auditMissingPublisherLabel = "缺厂牌 (0)";

    [ObservableProperty]
    private string _auditMissingSceneLabel = "缺场景 (0)";

    [ObservableProperty]
    private string _auditMissingTempoLabel = "缺节奏 (0)";

    [ObservableProperty]
    private string _auditMissingCoverLabel = "缺封面 (0)";

    [ObservableProperty]
    private string _auditMissingVocalsLabel = "缺人声标签 (0)";

    [ObservableProperty]
    private string _auditRediscovery90dLabel = "90天未听 (0)";

    [ObservableProperty]
    private string _inboxAuditResultSummaryText = "当前体检视图：全量曲目表 — 共 0 首";

    [ObservableProperty]
    private bool _showNowPlayingComposerAction;

    [ObservableProperty]
    private bool _showNowPlayingConductorAction;

    [ObservableProperty]
    private bool _hasNowPlayingClassicalCreditActions;

    [ObservableProperty]
    private string _nowPlayingComposerTooltip = "";

    [ObservableProperty]
    private string _nowPlayingConductorTooltip = "";

    [ObservableProperty]
    private bool _nowPlayingIsClassical;

    [ObservableProperty]
    private string _nowPlayingLine1 = "选择任意曲目开启沉浸画廊";

    [ObservableProperty]
    private string _nowPlayingLine2 = "TONEWISE GALLERY";

    [ObservableProperty]
    private string _nowPlayingLine3 = "";

    [ObservableProperty]
    private double _nowPlayingLine1FontSize = 34.0;

    [ObservableProperty]
    private int _nowPlayingLine1MaxLines = 1;

    [ObservableProperty]
    private double _nowPlayingLine2FontSize = 18.0;

    [ObservableProperty]
    private bool _nowPlayingLine3Visible;

    public string SendPlaylistToPhoneButtonLabel =>
        SelectedPlaylistCount > 1 ? $"发送到手机 ({SelectedPlaylistCount})" : "发送到手机";

    partial void OnSelectedPlaylistCountChanged(int value)
    {
        OnPropertyChanged(nameof(SendPlaylistToPhoneButtonLabel));
    }

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

    public string CurrentRecordingVersionLabel => CurrentTrack?.AlternateVersionLabel ?? "";

    [ObservableProperty]
    private string _currentBiographySummary = "选择曲目后显示本地百科传记。";

    [ObservableProperty]
    private string? _currentBookletPath;

    [ObservableProperty]
    private bool _hasBookletPdf;

    [ObservableProperty]
    private EntityGroupItem? _selectedEntityGroup;

    [ObservableProperty]
    private string _primaryNavId = "collection";

    [ObservableProperty]
    private string _secondarySubTab = "tracks";

    [ObservableProperty]
    private string _primarySectionTitle = "collection";

    [ObservableProperty]
    private string _primaryViewSummary = "albums · tracks · artists · classical";

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
    private bool _enableDynamicCoverAccent;

    /// <summary>"gallery" | "vinyl".</summary>
    [ObservableProperty]
    private string _nowPlayingStageMode = DesktopVinylStyleCodec.StageGallery;

    /// <summary>"classic" | "pearl" | "diffraction".</summary>
    [ObservableProperty]
    private string _vinylFinishStyle = "classic";

    [ObservableProperty]
    private bool _vinylUseAmbientBackdrop = true;

    public bool IsNowPlayingVinylMode =>
        string.Equals(NowPlayingStageMode, DesktopVinylStyleCodec.StageVinyl, StringComparison.Ordinal);

    public bool IsNowPlayingGalleryMode => !IsNowPlayingVinylMode;

    public bool IsNowPlayingVinylHalfEjected => IsNowPlayingVinylMode && IsNowPlayingQueueOpen;

    public bool IsNowPlayingVinylFullTurntable => IsNowPlayingVinylMode && !IsNowPlayingQueueOpen;

    public string NowPlayingStageModeLabel => IsNowPlayingVinylMode ? "唱片舞台" : "封套画廊";

    public string NowPlayingStageToggleLabel => IsNowPlayingVinylMode ? "切回封套" : "唱片模式";

    public DesktopVinylStyle ResolvedVinylStyle => DesktopVinylStyleCodec.ParseStyle(VinylFinishStyle);

    public string VinylFinishDisplayLabel => DesktopVinylStyleCodec.DisplayName(ResolvedVinylStyle);

    public string VinylFinishButtonLabel => $"材质：{VinylFinishDisplayLabel}";

    public bool IsVinylClassicSelected => ResolvedVinylStyle == DesktopVinylStyle.ClassicBlack;

    public bool IsVinylPearlSelected => ResolvedVinylStyle == DesktopVinylStyle.CoverPearl;

    public bool IsVinylDiffractionSelected => ResolvedVinylStyle == DesktopVinylStyle.GrooveDiffraction;

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
    private bool _isNavQuickplay;

    [ObservableProperty]
    private bool _isNavCollection = true;

    [ObservableProperty]
    private bool _isNavStudio;

    [ObservableProperty]
    private bool _isNavNowPlaying;

    [ObservableProperty]
    private bool _isQuickplayViewVisible;

    [ObservableProperty]
    private bool _isNowPlayingViewVisible;

    [ObservableProperty]
    private bool _isCollectionOrStudioMainVisible = true;

    [ObservableProperty]
    private bool _isAlbumsGridMode;

    [ObservableProperty]
    private bool _isAlbumsWallExpanded;

    [ObservableProperty]
    private bool _canNavigateBack;

    [ObservableProperty]
    private bool _hasQuickplayPins;

    [ObservableProperty]
    private bool _hasNoQuickplayPins = true;

    [ObservableProperty]
    private bool _hasQuickplayHistory;

    [ObservableProperty]
    private string _scenesMode = "presets";

    [ObservableProperty]
    private bool _showAllPerformers;

    [ObservableProperty]
    private bool _showClassicalEnsembles;

    [ObservableProperty]
    private bool _isClassicalComposerViewVisible;

    public bool IsClassicalComposersMode => !ShowClassicalEnsembles;

    [ObservableProperty]
    private bool _isNowPlayingQueueOpen;

    [ObservableProperty]
    private bool _isNowPlayingUpNextTab = true;

    [ObservableProperty]
    private bool _isNowPlayingBookletTab;

    [ObservableProperty]
    private bool _isNowPlayingAlternatesTab;

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

    [ObservableProperty]
    private bool _isArcWorkShuffle;

    private readonly List<TrackItem> _playbackHistory = [];
    private List<TrackItem> _unshuffledQueueSnapshot = [];
    private (List<TrackItem> Queue, string QueueTitle, string FreezeToken)? _djFreezeBackup;
    private string _currentFreezeToken = "";
    private readonly Random _shuffleRandom = new();
    private readonly HashSet<string> _autoSyncedLastFmRoots = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DesktopDspChainConfig> _deviceDspProfiles = new(StringComparer.OrdinalIgnoreCase);
    private List<DesktopPeqBand>? _activeBandTemplates;
    private bool _suppressDspSync;
    private bool _usingExternalEqFile;
    private AutoEqPolicyDto _autoEqPolicy = new();
    private ResolvedAutoEqDto? _activeAutoEqResolution;
    private string? _lastAutoEqWorkKey;
    private string? _activeAutoEqTrackUid;
    private string? _activeAutoEqTrackMetaKey;
    private bool _suppressAutoEqSync;
    private bool _suppressAutoEqRuleSync;
    private int _gaplessPreloadGen;
    private int? _deskClockX;
    private int? _deskClockY;
    private int? _deskClockWidth;
    private int? _deskClockHeight;
    private bool _deskClockAlwaysOnTop = true;

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
    private CancellationTokenSource? _playbackRecoveryCts;
    private AudioPlaybackInterruption? _pendingEndpointReturnInterruption;
    private DateTime _pendingEndpointReturnDeadlineUtc = DateTime.MinValue;
    private bool _isAutoRecoveringPlayback;
    private string? _appliedEngineEndpointId;
    private string? _pendingSavedEndpointId;
    private CancellationTokenSource? _libraryHydrationCts;
    private readonly Queue<DateTime> _recentRecoveryTimestampsUtc = new();

    public event Action? WaveformUpdated;
    public event Action<TrackItem?>? TrackChanged;
    public event Action? SubTabsChanged;
    public event Action? LibraryLoaded;

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
                RefreshBitPerfectVolumeLockState(engineExclusiveHint: state.IsExclusive);
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
        _engine.PlaybackInterrupted += interruption =>
        {
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
                _uiContext.Post(_ => HandlePlaybackInterrupted(interruption), null);
            else
                HandlePlaybackInterrupted(interruption);
        };
        _deviceChangeWatcher.EndpointsChanged += ScheduleEndpointRefresh;
        PlaybackQueue.CollectionChanged += (_, _) => UpdatePlaybackQueueSummary();
        UpdatePlaybackModeLabels();
        UpdatePlaybackQueueSummary();

        if (_uiContext is not null)
        {
            _ = RefreshEndpointsAsync();
        }
        else
        {
            RefreshEndpoints();
        }
        ReloadSessionSlots();
        UpdateSearchSuggestions("");
        UpdateNavigationState();
    }

    [RelayCommand]
    public void ToggleNowPlayingQueue() => IsNowPlayingQueueOpen = !IsNowPlayingQueueOpen;

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
            LockVolumeOnBitPerfect = saved.LockVolumeOnBitPerfect;
            RememberVolumePerEndpoint = saved.RememberVolumePerEndpoint;
            _endpointVolumesDb.Clear();
            foreach (var kv in DesktopSettingsStore.DeserializeEndpointVolumes(saved.EndpointVolumesJson))
            {
                _endpointVolumesDb[kv.Key] = kv.Value;
            }
            _mainWindowX = saved.MainWindowX;
            _mainWindowY = saved.MainWindowY;
            _mainWindowWidth = saved.MainWindowWidth;
            _mainWindowHeight = saved.MainWindowHeight;
            _mainWindowMaximized = saved.MainWindowMaximized;
            IsIso226Enabled = saved.IsIso226Enabled;
            VolumeAttenuationDb = saved.VolumeAttenuationDb;
            IsTpdfDitherEnabled = saved.IsTpdfDitherEnabled;
            FirDryWetMix = saved.FirDryWetMix;
            AlwaysExpandClassicalSubItems = saved.AlwaysExpandClassicalSubItems;
            IsSidebarExpanded = saved.IsSidebarExpanded;
            IsInspectorOpen = saved.IsInspectorOpen;
            AppTheme = saved.AppTheme is "light" or "system" ? saved.AppTheme : "dark";
            EnableDynamicCoverAccent = saved.EnableDynamicCoverAccent;
            NowPlayingStageMode = DesktopVinylStyleCodec.NormalizeStageMode(saved.NowPlayingStageMode);
            VinylFinishStyle = DesktopVinylStyleCodec.ToWireKey(DesktopVinylStyleCodec.ParseStyle(saved.VinylFinishStyle));
            VinylUseAmbientBackdrop = saved.VinylUseAmbientBackdrop;
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
            ApplyAutoEqPolicyToViewModel(SyncHostServer.DeserializeAutoEqPolicy(saved.AutoEqPolicyJson));
            _deskClockX = saved.DeskClockX;
            _deskClockY = saved.DeskClockY;
            _deskClockWidth = saved.DeskClockWidth;
            _deskClockHeight = saved.DeskClockHeight;
            _deskClockAlwaysOnTop = saved.DeskClockAlwaysOnTop;
            IsUsbAutoSyncEnabled = saved.EnableUsbAutoSync;
            IsMirrorPruneEnabled = saved.EnableMirrorPrune;
            MirrorPruneScope = string.Equals(saved.MirrorPruneScope, "managed_only", StringComparison.OrdinalIgnoreCase)
                ? "managed_only"
                : "music_folder_all";
            AllowAdbForeignDeleteFallback = saved.AllowAdbForeignDeleteFallback;
            _customAdbPath = saved.CustomAdbPath;

            if (!string.IsNullOrWhiteSpace(saved.SelectedEndpointId))
            {
                var ep = OutputEndpoints.FirstOrDefault(e =>
                    string.Equals(e.Id, saved.SelectedEndpointId, StringComparison.OrdinalIgnoreCase));
                if (ep is not null)
                {
                    SelectedEndpoint = ep;
                    _pendingSavedEndpointId = null;
                }
                else
                {
                    _pendingSavedEndpointId = saved.SelectedEndpointId;
                }
            }

            IsDacHardwareLocked = saved.IsDacHardwareLocked;

            LoadDspConfigForCurrentEndpoint(fallbackAutoFileRoot: null);

            if (RememberVolumePerEndpoint)
            {
                string epKey = string.IsNullOrWhiteSpace(SelectedEndpoint?.Id) ? "__default__" : SelectedEndpoint.Id!;
                if (_endpointVolumesDb.TryGetValue(epKey, out double savedEpDb))
                {
                    VolumeAttenuationDb = savedEpDb;
                }
            }
            RefreshBitPerfectVolumeLockState();

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
            if (_uiContext is not null)
            {
                _ = LoadSavedLibraryOnStartupAsync(saved.LastMusicRoot);
            }
            else
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
            }
        }
    }

    private async Task LoadSavedLibraryOnStartupAsync(string musicRoot)
    {
        try
        {
            await LoadMusicFolderAsync(musicRoot);
        }
        catch (Exception ex)
        {
            StatusMessage = $"自动加载上次曲库失败: {ex.Message}";
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
            IsShuffleEnabled: _sessionShuffleLock.IsLocked ? (_sessionShuffleLock.SavedGlobalShuffle ?? false) : IsShuffleEnabled,
            RepeatMode: RepeatMode,
            EnableAutoWatchLibrary: EnableAutoWatchLibrary,
            ReplayGainMode: ReplayGainMode,
            ReplayGainPreAmpWithTagDb: ReplayGainPreAmpWithTagDb,
            ReplayGainPreAmpWithoutTagDb: ReplayGainPreAmpWithoutTagDb,
            RewindOnPrevious: RewindOnPrevious,
            RememberPauseOnSkip: RememberPauseOnSkip,
            PauseOnSingleRepeat: PauseOnSingleRepeat,
            ExclusiveIdleReleaseMinutes: ExclusiveIdleReleaseMinutes,
            DeviceDspProfilesJson: DesktopDspChainConfig.SerializeDeviceProfileMap(_deviceDspProfiles),
            DeskClockX: _deskClockX,
            DeskClockY: _deskClockY,
            DeskClockWidth: _deskClockWidth,
            DeskClockHeight: _deskClockHeight,
            DeskClockAlwaysOnTop: _deskClockAlwaysOnTop,
            EnableUsbAutoSync: IsUsbAutoSyncEnabled,
            EnableMirrorPrune: IsMirrorPruneEnabled,
            MirrorPruneScope: MirrorPruneScope,
            AllowAdbForeignDeleteFallback: AllowAdbForeignDeleteFallback,
            CustomAdbPath: _customAdbPath,
            EnableDynamicCoverAccent: EnableDynamicCoverAccent,
            AutoEqPolicyJson: SyncHostServer.SerializeAutoEqPolicy(_autoEqPolicy),
            LockVolumeOnBitPerfect: LockVolumeOnBitPerfect,
            RememberVolumePerEndpoint: RememberVolumePerEndpoint,
            EndpointVolumesJson: DesktopSettingsStore.SerializeEndpointVolumes(_endpointVolumesDb),
            MainWindowX: _mainWindowX,
            MainWindowY: _mainWindowY,
            MainWindowWidth: _mainWindowWidth,
            MainWindowHeight: _mainWindowHeight,
            MainWindowMaximized: _mainWindowMaximized,
            NowPlayingStageMode: NowPlayingStageMode,
            VinylFinishStyle: VinylFinishStyle,
            VinylUseAmbientBackdrop: VinylUseAmbientBackdrop);

        try
        {
            DesktopSettingsStore.Save(snapshot, _settingsFilePath);
        }
        catch
        {
            // Non-fatal if settings directory is temporarily read-only
        }
    }

    public (int? X, int? Y, int? Width, int? Height, bool AlwaysOnTop) GetDeskClockWindowPlacement() =>
        (_deskClockX, _deskClockY, _deskClockWidth, _deskClockHeight, _deskClockAlwaysOnTop);

    public (int? X, int? Y, int? Width, int? Height, bool Maximized) GetMainWindowPlacement() =>
        (_mainWindowX, _mainWindowY, _mainWindowWidth, _mainWindowHeight, _mainWindowMaximized);

    public void SaveMainWindowPlacement(int x, int y, int width, int height, bool maximized)
    {
        _mainWindowX = x;
        _mainWindowY = y;
        _mainWindowWidth = width;
        _mainWindowHeight = height;
        _mainWindowMaximized = maximized;
        SaveUserSettings();
    }

    public void SaveDeskClockWindowPlacement(int x, int y, int width, int height, bool alwaysOnTop)
    {
        _deskClockX = x;
        _deskClockY = y;
        _deskClockWidth = width;
        _deskClockHeight = height;
        _deskClockAlwaysOnTop = alwaysOnTop;
        SaveUserSettings();
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
    partial void OnFilterSceneChanged(string value)
    {
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }
    partial void OnFilterEnergyChanged(string value)
    {
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }
    partial void OnFilterVocalsChanged(string value)
    {
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }
    partial void OnExcludeVocalChanged(bool value)
    {
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }
    partial void OnFilterTempoBucketChanged(string value)
    {
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }
    partial void OnFilterMoodClusterChanged(string value)
    {
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }
    partial void OnScenesTempoNullPassthroughChanged(bool value)
    {
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }
    partial void OnSelectedEnergyArcChanged(string value)
    {
        UpdateArcStateFlags();
        UpdateFilterChipLabels();
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
        if (!_suppressSettingsSave && !_suppressDspSync)
        {
            RefreshBitPerfectVolumeLockState();
            PushEnginePreferences();
            ReopenCurrentOutputAtCurrentPosition();
        }
        SaveUserSettings();
    }
    partial void OnLockVolumeOnBitPerfectChanged(bool value)
    {
        RefreshBitPerfectVolumeLockState();
        if (!_suppressSettingsSave && !_suppressDspSync)
        {
            PushEnginePreferences();
        }
        SaveUserSettings();
    }
    partial void OnRememberVolumePerEndpointChanged(bool value)
    {
        if (value && !IsVolumeLockedByBitPerfect)
        {
            string curKey = string.IsNullOrWhiteSpace(SelectedEndpoint?.Id) ? "__default__" : SelectedEndpoint.Id!;
            _endpointVolumesDb[curKey] = Math.Round(Math.Clamp(VolumeAttenuationDb, -80.0, 0.0), 1);
        }
        SaveUserSettings();
    }
    partial void OnSelectedEndpointChanged(AudioOutputEndpoint? value)
    {
        if (value is null && OutputEndpoints.Count > 0)
        {
            return;
        }
        if (!_suppressDspSync)
        {
            string prevKey = string.IsNullOrWhiteSpace(_appliedEngineEndpointId) ? "__default__" : _appliedEngineEndpointId!;
            double prevDb = VolumeAttenuationDb;
            bool endpointChanged = !string.Equals(_appliedEngineEndpointId, value?.Id, StringComparison.Ordinal);
            if (endpointChanged && RememberVolumePerEndpoint && !IsVolumeLockedByBitPerfect)
            {
                _endpointVolumesDb[prevKey] = Math.Round(Math.Clamp(prevDb, -80.0, 0.0), 1);
            }

            LoadDspConfigForCurrentEndpoint(_store.MusicRoot);
            if (endpointChanged)
            {
                ApplyEndpointVolumeWithSafetyClamp(value, prevDb);
            }
            RefreshBitPerfectVolumeLockState();
            PushEnginePreferences();
            if (endpointChanged)
            {
                ReopenCurrentOutputAtCurrentPosition();
            }
        }
        SaveUserSettings();
    }

    private void ApplyEndpointVolumeWithSafetyClamp(AudioOutputEndpoint? targetEndpoint, double prevDb)
    {
        IsEndpointVolumeSafetyPromptVisible = false;
        _pendingRestoreEndpointVolumeDb = null;
        _pendingRestoreEndpointKey = null;

        if (!RememberVolumePerEndpoint || IsVolumeLockedByBitPerfect)
        {
            return;
        }

        string newKey = string.IsNullOrWhiteSpace(targetEndpoint?.Id) ? "__default__" : targetEndpoint.Id!;
        string friendly = string.IsNullOrWhiteSpace(targetEndpoint?.FriendlyName) ? "默认输出设备" : targetEndpoint.FriendlyName;
        bool prevSuppress = _suppressSettingsSave;
        _suppressSettingsSave = true;
        try
        {
            if (_endpointVolumesDb.TryGetValue(newKey, out double savedDb))
            {
                if (savedDb > -12.0 && prevDb < -12.0)
                {
                    VolumeAttenuationDb = -12.0;
                    _pendingRestoreEndpointVolumeDb = savedDb;
                    _pendingRestoreEndpointKey = newKey;
                    EndpointVolumeSafetyPromptText = $"已切换至「{friendly}」：记忆音量 {savedDb:0.0} dB 接近满幅，已先安全限幅至 -12.0 dB。";
                    IsEndpointVolumeSafetyPromptVisible = true;
                }
                else
                {
                    VolumeAttenuationDb = savedDb;
                }
            }
            else if (prevDb > -12.0)
            {
                VolumeAttenuationDb = -12.0;
                _pendingRestoreEndpointVolumeDb = prevDb;
                _pendingRestoreEndpointKey = newKey;
                EndpointVolumeSafetyPromptText = $"首次切换至「{friendly}」：已安全衰减至 -12.0 dB 以防满幅爆音。";
                IsEndpointVolumeSafetyPromptVisible = true;
            }
        }
        finally
        {
            _suppressSettingsSave = prevSuppress;
        }
    }

    public void RefreshBitPerfectVolumeLockState(bool? engineExclusiveHint = null)
    {
        bool exclusiveTarget = PreferWasapiExclusive || (engineExclusiveHint ?? _engine.CurrentSignalPath.IsExclusive);
        bool locked = LockVolumeOnBitPerfect && exclusiveTarget;
        IsVolumeLockedByBitPerfect = locked;
        IsSoftwareVolumeControlEnabled = !locked;
        if (locked)
        {
            bool prevSup = _suppressSettingsSave;
            _suppressSettingsSave = true;
            try
            {
                if (IsMuted) IsMuted = false;
                if (Math.Abs(VolumeAttenuationDb) > 0.01) VolumeAttenuationDb = 0.0;
            }
            finally
            {
                _suppressSettingsSave = prevSup;
            }
        }
        UpdateVolumeToolTip();
    }

    [RelayCommand]
    private void UnlockBitPerfectVolume()
    {
        LockVolumeOnBitPerfect = false;
        Notify("已解锁 Bit-Perfect 0 dB 硬件直通音量锁，软件衰减将启用 24-bit TPDF 抖动。", "info");
    }

    [RelayCommand]
    private void ConfirmRestoreEndpointVolume()
    {
        if (_pendingRestoreEndpointVolumeDb is double targetDb)
        {
            string curKey = string.IsNullOrWhiteSpace(SelectedEndpoint?.Id) ? "__default__" : SelectedEndpoint.Id!;
            if (_pendingRestoreEndpointKey is null || string.Equals(curKey, _pendingRestoreEndpointKey, StringComparison.OrdinalIgnoreCase))
            {
                VolumeAttenuationDb = targetDb;
            }
        }
        IsEndpointVolumeSafetyPromptVisible = false;
        _pendingRestoreEndpointVolumeDb = null;
        _pendingRestoreEndpointKey = null;
    }

    [RelayCommand]
    private void DismissEndpointVolumeSafetyPrompt()
    {
        IsEndpointVolumeSafetyPromptVisible = false;
        _pendingRestoreEndpointVolumeDb = null;
        _pendingRestoreEndpointKey = null;
    }

    private void ScheduleEndpointRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _endpointDebounceTimer ??= new System.Threading.Timer(_ =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (_uiContext is not null)
                    _uiContext.Post(_ =>
                    {
                        if (Volatile.Read(ref _disposed) == 0) RefreshEndpoints(handleLostSelection: true);
                    }, null);
                else
                    RefreshEndpoints(handleLostSelection: true);
            }, null, Timeout.Infinite, Timeout.Infinite);
            _endpointDebounceTimer.Change(300, Timeout.Infinite);
        }
        catch
        {
            // Ignore during teardown
        }
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
    partial void OnIsCrossfeedEnabledChanged(bool value) => OnBuiltInDspChanged(markCustomEq: false, autoEnableExtended: value);
    partial void OnCrossfeedCutoffHzChanged(double value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnCrossfeedFeedDbChanged(double value) => OnBuiltInDspChanged(markCustomEq: false);
    partial void OnDspBalanceDbChanged(double value) => OnBuiltInDspChanged(markCustomEq: false, autoEnableExtended: Math.Abs(value) > 0.05);
    partial void OnIsMonoFoldEnabledChanged(bool value) => OnBuiltInDspChanged(markCustomEq: false, autoEnableExtended: value);
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
    partial void OnIsAutoEqEnabledChanged(bool value) => OnAutoEqToggleChanged(autoEnableDsp: value);
    partial void OnIsAutoEqClassicalCrossfeedEnabledChanged(bool value) => OnAutoEqToggleChanged(autoEnableDsp: false);
    partial void OnIsAutoEqLockClassicalWorkEnabledChanged(bool value) => OnAutoEqToggleChanged(autoEnableDsp: false);
    partial void OnSelectedAutoEqGenreChanged(string value) => LoadSelectedAutoEqRuleSlots();
    partial void OnSelectedAutoEqVocalChanged(string value) => LoadSelectedAutoEqRuleSlots();
    partial void OnAutoEqRuleCrossfeedModeChanged(string value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand0DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand1DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand2DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand3DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand4DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand5DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand6DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand7DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand8DbChanged(double value) => OnAutoEqRuleSliderEdited();
    partial void OnAutoEqRuleBand9DbChanged(double value) => OnAutoEqRuleSliderEdited();

    partial void OnCurrentTrackChanged(TrackItem? value)
    {
        HasCurrentTrack = value is not null;
        CurrentTrackLoved = value?.IsLoved ?? false;
        RefreshCurrentFreezeCapsule();
        RefreshNowPlayingClassicalActions();
        UpdateClassicalPlayingHighlight(value?.TrackUid);
        if (value is not null
            && string.Equals(_activeAutoEqTrackUid, value.TrackUid, StringComparison.Ordinal)
            && !string.Equals(_activeAutoEqTrackMetaKey, BuildAutoEqMetaFingerprint(value), StringComparison.Ordinal))
        {
            RefreshAutoEqAndPush();
        }
    }

    partial void OnIsIso226EnabledChanged(bool value)
    {
        PushAcousticPipeline();
        SaveUserSettings();
    }
    partial void OnVolumeAttenuationDbChanged(double value)
    {
        if (IsVolumeLockedByBitPerfect && Math.Abs(value) > 0.01)
        {
            bool prevSup = _suppressSettingsSave;
            _suppressSettingsSave = true;
            try
            {
                VolumeAttenuationDb = 0.0;
            }
            finally
            {
                _suppressSettingsSave = prevSup;
            }
            UpdateVolumeToolTip();
            return;
        }
        if (!_suppressSettingsSave && RememberVolumePerEndpoint && !IsVolumeLockedByBitPerfect)
        {
            string curKey = string.IsNullOrWhiteSpace(SelectedEndpoint?.Id) ? "__default__" : SelectedEndpoint.Id!;
            _endpointVolumesDb[curKey] = Math.Round(Math.Clamp(value, -80.0, 0.0), 1);
        }
        PushAcousticPipeline();
        SyncVolumePercentFromDb(value);
        SaveUserSettings();
    }

    partial void OnVolumePercentChanged(double value)
    {
        if (_syncingVolume) return;
        if (IsVolumeLockedByBitPerfect)
        {
            SyncVolumePercentFromDb(0.0);
            return;
        }
        double pct = Math.Clamp(value, 0.0, 100.0);
        double db = pct <= 0.0 ? -80.0 : Math.Clamp(40.0 * Math.Log10(pct / 100.0), -80.0, 0.0);
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
        if (IsVolumeLockedByBitPerfect && value)
        {
            IsMuted = false;
            return;
        }
        _engine.IsMuted = value;
        UpdateVolumeToolTip();
    }

    public void PreloadInitialAppTheme(string? savedTheme)
    {
        string normalized = savedTheme is "light" or "dark" or "system" ? savedTheme : "dark";
        _suppressSettingsSave = true;
        try
        {
            AppTheme = normalized;
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    partial void OnAppThemeChanged(string value) => SaveUserSettings();
    partial void OnEnableDynamicCoverAccentChanged(bool value) => SaveUserSettings();

    partial void OnNowPlayingStageModeChanged(string value)
    {
        string normalized = DesktopVinylStyleCodec.NormalizeStageMode(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            _nowPlayingStageMode = normalized;
        }
        OnPropertyChanged(nameof(IsNowPlayingVinylMode));
        OnPropertyChanged(nameof(IsNowPlayingGalleryMode));
        OnPropertyChanged(nameof(IsNowPlayingVinylHalfEjected));
        OnPropertyChanged(nameof(IsNowPlayingVinylFullTurntable));
        OnPropertyChanged(nameof(NowPlayingStageModeLabel));
        OnPropertyChanged(nameof(NowPlayingStageToggleLabel));
        SaveUserSettings();
    }

    partial void OnVinylFinishStyleChanged(string value)
    {
        string normalized = DesktopVinylStyleCodec.ToWireKey(DesktopVinylStyleCodec.ParseStyle(value));
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            _vinylFinishStyle = normalized;
        }
        OnPropertyChanged(nameof(ResolvedVinylStyle));
        OnPropertyChanged(nameof(VinylFinishDisplayLabel));
        OnPropertyChanged(nameof(VinylFinishButtonLabel));
        OnPropertyChanged(nameof(IsVinylClassicSelected));
        OnPropertyChanged(nameof(IsVinylPearlSelected));
        OnPropertyChanged(nameof(IsVinylDiffractionSelected));
        SaveUserSettings();
    }

    partial void OnVinylUseAmbientBackdropChanged(bool value) => SaveUserSettings();

    partial void OnIsNowPlayingQueueOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNowPlayingVinylHalfEjected));
        OnPropertyChanged(nameof(IsNowPlayingVinylFullTurntable));
    }

    [RelayCommand]
    public void ToggleNowPlayingStageMode()
    {
        NowPlayingStageMode = IsNowPlayingVinylMode
            ? DesktopVinylStyleCodec.StageGallery
            : DesktopVinylStyleCodec.StageVinyl;
    }

    [RelayCommand]
    public void CycleVinylFinishStyle()
    {
        var next = DesktopVinylStyleCodec.Next(ResolvedVinylStyle);
        VinylFinishStyle = DesktopVinylStyleCodec.ToWireKey(next);
        if (!IsNowPlayingVinylMode)
        {
            NowPlayingStageMode = DesktopVinylStyleCodec.StageVinyl;
        }
        Notify($"黑胶材质：{VinylFinishDisplayLabel}", "info");
    }

    [RelayCommand]
    public void SetVinylFinishStyle(string? style)
    {
        var parsed = DesktopVinylStyleCodec.ParseStyle(style);
        VinylFinishStyle = DesktopVinylStyleCodec.ToWireKey(parsed);
        if (!IsNowPlayingVinylMode)
        {
            NowPlayingStageMode = DesktopVinylStyleCodec.StageVinyl;
        }
        Notify($"黑胶材质：{VinylFinishDisplayLabel}", "info");
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
        OnPropertyChanged(nameof(FirDryWetPercentDisplay));
        PushAcousticPipeline();
        SaveUserSettings();
    }

    public string FirDryWetPercentDisplay => $"{Math.Round(FirDryWetMix * 100.0):0}%";
    public bool HasFirImpulse => !string.IsNullOrWhiteSpace(_firImpulseFilePath);

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelPendingPlaybackRecovery();
        var quickplayCts = Interlocked.Exchange(ref _quickplayBgCts, null);
        if (quickplayCts is not null)
        {
            try { quickplayCts.Cancel(); } catch { }
            quickplayCts.Dispose();
        }
        var hydrationCts = Interlocked.Exchange(ref _libraryHydrationCts, null);
        if (hydrationCts is not null)
        {
            try { hydrationCts.Cancel(); } catch { }
            hydrationCts.Dispose();
        }
        Interlocked.Exchange(ref _endpointDebounceTimer, null)?.Dispose();
        _deviceChangeWatcher.EndpointsChanged -= ScheduleEndpointRefresh;
        _deviceChangeWatcher.Dispose();
        AccumulateListeningTime();
        EvaluateAndCommitPlayCountIfQualified(completedNaturally: false, synchronousLocalPersist: true);
        PersistPlaybackSessionSnapshot(force: true, synchronous: true);
        Interlocked.Exchange(ref _libraryWatcher, null)?.Dispose();
        StopPairingCodeRefreshTimer();
        var usb = Interlocked.Exchange(ref _usbCoordinator, null);
        if (usb is not null)
        {
            usb.StateChanged -= OnUsbCoordinatorStateChanged;
            _ = Task.Run(async () =>
            {
                try
                {
                    await usb.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort non-blocking USB monitor shutdown on window close
                }
            });
        }
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
