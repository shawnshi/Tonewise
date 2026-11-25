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
                    _eqProfile = _engine.ActiveProfile;
                    _usingExternalEqFile = true;
                    _activeEqFilePath = autoEq;
                    ActiveEqSummary = $"Auto EQ: {Path.GetFileName(autoEq)}";
                }
            }
        }

        SaveUserSettings();
        var oldHost = Interlocked.Exchange(ref _syncHost, null);
        bool wasHostRunning = oldHost is { IsRunning: true };
        StopPairingCodeRefreshTimer();
        _syncStore = new SyncOutboxAndPairingStore(musicRoot);
        ApplySyncPolicyToStore();
        if (oldHost is not null)
        {
            _ = RestartOrDisposeSyncHostAsync(oldHost, musicRoot, _syncStore, restart: wasHostRunning);
        }
        else if (_uiContext is not null && !_lanSyncManuallyStopped)
        {
            _ = EnsureLanSyncHostRunningAsync(silent: true);
        }
        if (_uiContext is not null)
        {
            EnsureUsbCoordinatorStarted();
        }
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
        UpdateFilterChipLabels();
        ApplyFiveDimFilter();
    }

    public IReadOnlyList<string> SelectedGenres => _selectedGenres;

    public IReadOnlyList<GenreFacetItem> GetAvailableGenreFacets() =>
        GenreFacetHelper.BuildFacets(_allTracks.Select(t => t.Genre));

    public bool IsGenreSelected(string? genre) =>
        !string.IsNullOrWhiteSpace(genre)
        && _selectedGenres.Any(g => string.Equals(g, genre.Trim(), StringComparison.OrdinalIgnoreCase));

    public void ToggleGenreFilter(string genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return;
        string clean = genre.Trim();
        int idx = _selectedGenres.FindIndex(g => string.Equals(g, clean, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _selectedGenres.RemoveAt(idx);
        }
        else
        {
            _selectedGenres.Add(clean);
        }
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }

    public void SelectSingleGenreFilter(string genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return;
        string clean = genre.Trim();
        _selectedGenres.Clear();
        _selectedGenres.Add(clean);
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }

    public void ClearGenreFilters()
    {
        if (_selectedGenres.Count == 0) return;
        _selectedGenres.Clear();
        UpdateFilterChipLabels();
        TriggerFilterRefresh();
    }

    public string CurrentVocalsOptionTag => ExcludeVocal ? "exclude_vocal" : FilterVocals;

    public void SetVocalsFilterOption(string? optionTag)
    {
        RunBatchFilterUpdate(() =>
        {
            switch (optionTag?.Trim().ToLowerInvariant())
            {
                case "exclude_vocal":
                    ExcludeVocal = true;
                    FilterVocals = "all";
                    break;
                case "instrumental":
                    ExcludeVocal = false;
                    FilterVocals = "instrumental";
                    break;
                case "vocal":
                    ExcludeVocal = false;
                    FilterVocals = "vocal";
                    break;
                case "unknown":
                    ExcludeVocal = false;
                    FilterVocals = "unknown";
                    break;
                default:
                    ExcludeVocal = false;
                    FilterVocals = "all";
                    break;
            }
        });
    }

    private void UpdateFilterChipLabels()
    {
        GenreFilterChipLabel = GenreFacetHelper.FormatGenreChipLabel(_selectedGenres);
        IsGenreFilterActive = _selectedGenres.Count > 0;

        SceneFilterChipLabel = FilterScene switch
        {
            "focus" => "场景: 专注",
            "coding" => "场景: 编程",
            "relax" => "场景: 放松",
            "energy" => "场景: 活力",
            "pop" => "场景: 流行",
            _ => "场景",
        };
        IsSceneFilterActive = !string.Equals(FilterScene, "all", StringComparison.OrdinalIgnoreCase);

        EnergyFilterChipLabel = FilterEnergy switch
        {
            "low" => "能量: 低",
            "medium" => "能量: 中",
            "high" => "能量: 高",
            _ => "能量",
        };
        IsEnergyFilterActive = !string.Equals(FilterEnergy, "all", StringComparison.OrdinalIgnoreCase);

        VocalsFilterChipLabel = ExcludeVocal
            ? "人声: 排除人声"
            : FilterVocals switch
            {
                "instrumental" => "人声: 纯器乐",
                "vocal" => "人声: 含人声",
                "unknown" => "人声: 未知",
                _ => "人声",
            };
        IsVocalsFilterActive = ExcludeVocal || !string.Equals(FilterVocals, "all", StringComparison.OrdinalIgnoreCase);

        TempoFilterChipLabel = string.Equals(FilterTempoBucket, "all", StringComparison.OrdinalIgnoreCase)
            ? "节奏"
            : $"节奏: {FilterTempoBucket}";
        IsTempoFilterActive = !string.Equals(FilterTempoBucket, "all", StringComparison.OrdinalIgnoreCase);

        MoodFilterChipLabel = FilterMoodCluster switch
        {
            "calm_relaxed" => "情绪: 平静舒缓",
            "warm_romantic" => "情绪: 温暖浪漫",
            "focus_deep" => "情绪: 沉思专注",
            "bright_energetic" => "情绪: 明快振奋",
            "melancholic_dark" => "情绪: 忧郁深沉",
            _ => "情绪",
        };
        IsMoodFilterActive = !string.Equals(FilterMoodCluster, "all", StringComparison.OrdinalIgnoreCase);

        ArcFilterChipLabel = SelectedEnergyArc switch
        {
            "energy_asc" => "曲线: 能量渐强",
            "energy_desc_sleep" => "曲线: 助眠渐弱",
            "rediscovery_90d" => "曲线: 90天未听",
            _ => "曲线",
        };
        IsArcFilterActive = !string.Equals(SelectedEnergyArc, "flat", StringComparison.OrdinalIgnoreCase);

        TempoPassthroughChipLabel = ScenesTempoNullPassthrough ? "空Tempo: 透传" : "空Tempo: 严格";

        HasAnyActiveStripFilter =
            IsGenreFilterActive
            || IsSceneFilterActive
            || IsEnergyFilterActive
            || IsVocalsFilterActive
            || IsTempoFilterActive
            || IsMoodFilterActive
            || IsArcFilterActive
            || !ScenesTempoNullPassthrough;
    }

    [RelayCommand]
    public void ResetAllFilters()
    {
        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = "all";
            _selectedGenres.Clear();
            FilterScene = "all";
            FilterEnergy = "all";
            FilterVocals = "all";
            ExcludeVocal = false;
            FilterTempoBucket = "all";
            FilterMoodCluster = "all";
            SelectedEnergyArc = "flat";
            ScenesTempoNullPassthrough = true;
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
        if (normalized == "sync" && _uiContext is not null && !_lanSyncManuallyStopped && _syncHost is not { IsRunning: true } && !string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            _ = EnsureLanSyncHostRunningAsync(silent: true);
        }
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
        string name = PrimaryNavId == "scenes" && SecondarySubTab == "autodj"
            ? $"AutoDJ-{DateTime.Now:MMdd-HHmm}"
            : $"Scene-{(FilterScene == "all" ? "mix" : FilterScene)}-{DateTime.Now:MMdd-HHmm}";
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
                LastPlayedTsMs: t.LastPlayedTsMs,
                WorkKey: WorkAwareShuffleEngine.BuildWorkBlockKey(
                    t.WorkName,
                    t.Album,
                    WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay)),
                MovementNumber: t.MovementNumber is > 0 ? t.MovementNumber : null,
                DiscNumber: t.DiscNumber,
                TrackNumber: t.TrackNumber,
                RelPath: t.RelPath))
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

        IsShuffleEnabled = false;
        FilteredTracks.ReplaceAll(orderedTracks);
        CurrentViewHeader = $"AutoDJ · {res.Profile.PeriodLabel} ({orderedTracks.Count} 首)";
        ActiveFilterPillsSummary = $"常听 {res.FrequentCount} 首 (70%) · 探索 {res.DiscoveryCount} 首 (30%) · 邻曲最大差 {res.MaxAdjacentBpmDelta:0.#} BPM";
        AutoDjSummaryText = $"{res.SummaryText} · 仅载入临时播放列表（未自动保存为歌单）";
        UpdateScenesStudioAndArcTelemetry();

        PlayFromBrowseView(orderedTracks[0]);
        InspectTrack(orderedTracks[0]);
        Notify($"{res.SummaryText}（已载入播放列表，如需保存请点击「将当前结果保存为歌单」）", "success");
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
            CurrentViewHeader = "";
            ActiveFilterPillsSummary = "";
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
                ActiveFilterPillsSummary = "Playlists · 支持 Spotify / Apple Music / Billboard / Kworb / TXT 导入";
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
        var map = new Dictionary<string, (string Key, string Title, string Subtitle, List<TrackItem> Tracks)>(StringComparer.OrdinalIgnoreCase);

        void AddToBucket(string key, string subtitle, TrackItem track, string? titleOverride = null)
        {
            string cleanKey = string.IsNullOrWhiteSpace(key) ? "(未命名)" : key.Trim();
            string displayTitle = string.IsNullOrWhiteSpace(titleOverride) ? cleanKey : titleOverride.Trim();
            if (!string.IsNullOrWhiteSpace(q) &&
                !displayTitle.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !track.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!map.TryGetValue(cleanKey, out var bucket))
            {
                bucket = (cleanKey, displayTitle, subtitle, []);
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
                    foreach (string composer in SplitComposers(track.ComposersDisplay))
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
                                 .Concat(SplitComposers(track.ComposersDisplay))
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
                    string composerKey = SplitComposers(track.ComposersDisplay).FirstOrDefault()
                        ?? SplitPerformers(track.ArtistsDisplay).FirstOrDefault()
                        ?? "classical";
                    AddToBucket($"{composerKey}::{work}", string.IsNullOrWhiteSpace(sub) ? "古典作品" : sub, track, titleOverride: work);
                    break;
                }
                case ("classical", "composers"):
                {
                    foreach (string composer in SplitComposers(track.ComposersDisplay))
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
        IEnumerable<(string Key, string Title, string Subtitle, List<TrackItem> Tracks)> ordered = sortAlphabetical
            ? map.Values
                .OrderBy(v => v.Title.StartsWith('(') ? 1 : 0)
                .ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(1500)
            : map.Values
                .OrderByDescending(v => v.Tracks.Count)
                .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
                .Take(400);

        bool isClassicalWorks = (PrimaryNavId, SecondarySubTab) == ("classical", "works");
        var groups = new List<EntityGroupItem>();
        foreach (var entry in ordered)
        {
            if (isClassicalWorks && entry.Tracks.Count > 1)
            {
                entry.Tracks.Sort((a, b) =>
                {
                    string scopeA = WorkAwareShuffleEngine.BuildAlbumScope(a.Album, a.ConductorsDisplay);
                    string scopeB = WorkAwareShuffleEngine.BuildAlbumScope(b.Album, b.ConductorsDisplay);
                    int cmp = StringComparer.OrdinalIgnoreCase.Compare(scopeA, scopeB);
                    if (cmp != 0) return cmp;
                    int da = a.DiscNumber > 0 ? a.DiscNumber : int.MaxValue;
                    int db = b.DiscNumber > 0 ? b.DiscNumber : int.MaxValue;
                    cmp = da.CompareTo(db);
                    if (cmp != 0) return cmp;
                    int ma = a.MovementNumber is > 0 ? a.MovementNumber.Value : int.MaxValue;
                    int mb = b.MovementNumber is > 0 ? b.MovementNumber.Value : int.MaxValue;
                    cmp = ma.CompareTo(mb);
                    if (cmp != 0) return cmp;
                    int ta = a.TrackNumber > 0 ? a.TrackNumber : int.MaxValue;
                    int tb = b.TrackNumber > 0 ? b.TrackNumber : int.MaxValue;
                    cmp = ta.CompareTo(tb);
                    if (cmp != 0) return cmp;
                    return WorkAwareShuffleEngine.NaturalPathCompare(a.RelPath, b.RelPath);
                });
            }
            else if (sortAlphabetical && entry.Tracks.Count > 1)
            {
                entry.Tracks.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title));
            }
            string badge = isClassicalWorks
                ? $"{entry.Tracks.Count} 轨 · {Math.Max(1, entry.Tracks.Select(t => WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay)).Distinct(StringComparer.OrdinalIgnoreCase).Count())} 版"
                : $"{entry.Tracks.Count} 首";
            groups.Add(new EntityGroupItem(entry.Key, entry.Title, entry.Subtitle, badge, entry.Tracks));
        }
        CurrentEntityGroups.ReplaceAll(groups);

        var restored = prevKey is not null
            ? CurrentEntityGroups.FirstOrDefault(g => string.Equals(g.Key, prevKey, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectEntityGroup(restored ?? CurrentEntityGroups.FirstOrDefault());
    }

    public static IReadOnlyList<string> SplitPerformers(string? raw) =>
        ArtistEntitySplitter.SplitPerformers(raw);

    public static IReadOnlyList<string> SplitComposers(string? raw) =>
        ArtistEntitySplitter.SplitComposers(raw);

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
            _selectedGenres.Clear();
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
            _selectedGenres.Clear();
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
            _selectedGenres.Clear();
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

    [RelayCommand]
    public void ActivateClockQuickScene(string sceneId)
    {
        switch (sceneId?.Trim().ToLowerInvariant())
        {
            case "focus":
                RunBatchFilterUpdate(() =>
                {
                    LibraryNavMode = "all";
                    _selectedGenres.Clear();
                    FilterScene = "focus";
                    ExcludeVocal = true;
                    FilterVocals = "all";
                    FilterEnergy = "low";
                    FilterTempoBucket = "all";
                    FilterMoodCluster = "all";
                    SelectedEnergyArc = "flat";
                });
                break;
            case "coding":
                RunBatchFilterUpdate(() =>
                {
                    LibraryNavMode = "all";
                    _selectedGenres.Clear();
                    FilterScene = "coding";
                    ExcludeVocal = true;
                    FilterVocals = "all";
                    FilterEnergy = "all";
                    FilterTempoBucket = "all";
                    FilterMoodCluster = "all";
                    SelectedEnergyArc = "flat";
                });
                break;
            case "relax":
                RunBatchFilterUpdate(() =>
                {
                    LibraryNavMode = "all";
                    _selectedGenres.Clear();
                    FilterScene = "relax";
                    ExcludeVocal = false;
                    FilterVocals = "vocal";
                    FilterEnergy = "all";
                    FilterTempoBucket = "all";
                    FilterMoodCluster = "calm_relaxed";
                    SelectedEnergyArc = "flat";
                });
                break;
            case "sleep":
                RunBatchFilterUpdate(() =>
                {
                    LibraryNavMode = "all";
                    _selectedGenres.Clear();
                    FilterScene = "all";
                    ExcludeVocal = false;
                    FilterVocals = "all";
                    FilterEnergy = "all";
                    FilterTempoBucket = "all";
                    FilterMoodCluster = "all";
                    SelectedEnergyArc = "energy_desc_sleep";
                });
                break;
            default:
                return;
        }
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

        UpdateFilterChipLabels();
        var query = new FiveDimFilterQuery(
            Scene: FilterScene,
            Energy: FilterEnergy,
            Vocals: FilterVocals,
            ExcludeVocal: ExcludeVocal,
            TempoBucket: FilterTempoBucket,
            MoodCluster: FilterMoodCluster,
            SortArc: SelectedEnergyArc,
            TempoNullPassthrough: ScenesTempoNullPassthrough,
            Genres: _selectedGenres.Count > 0 ? _selectedGenres.ToArray() : null
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
                    : 120u,
                work_key = WorkAwareShuffleEngine.BuildWorkBlockKey(
                    t.WorkName,
                    t.Album,
                    WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay)),
                movement_order = t.MovementNumber is > 0 ? t.MovementNumber : (int?)null,
                composers = SplitComposers(t.ComposersDisplay),
                conductors = SplitPerformers(t.ConductorsDisplay),
                work_name = string.IsNullOrWhiteSpace(t.WorkName) ? null : t.WorkName.Trim(),
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
            if (string.Equals(query.SortArc, "work_shuffle", StringComparison.OrdinalIgnoreCase) && ordered.Count > 1)
            {
                var descriptors = ordered.Select(t => new ShuffleTrackDescriptor(
                    t.TrackUid,
                    t.Album,
                    t.WorkName,
                    t.MovementNumber is > 0 ? t.MovementNumber : null,
                    t.RelPath,
                    WorkAwareShuffleEngine.BuildAlbumScope(t.Album, t.ConductorsDisplay),
                    t.DiscNumber,
                    t.TrackNumber)).ToList();
                int[] shuffledIdx = WorkAwareShuffleEngine.BuildWorkAwareOrder(descriptors, -1, _shuffleRandom);
                ordered = shuffledIdx.Select(i => ordered[i]).ToList();
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

        bool preserveSceneArc = FiveDimQueryBuilder.RequiresFilterOrSort(query)
                                && !string.Equals(query.SortArc, "flat", StringComparison.OrdinalIgnoreCase);
        if (!preserveSceneArc)
        {
            working = ApplyColumnSort(working);
        }
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
        if (_selectedGenres.Count > 0) pills.Add($"流派: {string.Join(", ", _selectedGenres)}");
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
        if (_selectedGenres.Count > 0) clauses.Add($"genre IN [{string.Join(", ", _selectedGenres.Select(g => $"'{g}'"))}]");
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

}
