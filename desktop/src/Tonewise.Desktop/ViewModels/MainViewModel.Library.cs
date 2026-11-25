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
        _libraryHydrationCts?.Cancel();
        _libraryHydrationCts?.Dispose();
        var hydrationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _libraryHydrationCts = hydrationCts;

        StatusMessage = $"正在索引曲库：{musicRoot}";
        BeginBusy($"正在索引曲库 {musicRoot} ...");
        try
        {
            await _store.ScanAndBuildManifestIfMissingAsync(musicRoot, forceRebuild: false, fastMode: true, ct);
            var stage1 = await Task.Run(() =>
            {
                var loaded = _store.LoadFromMusicRoot(musicRoot).ToList();
                var byUid = loaded.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
                var seeds = loaded.Select(ToQuickplaySeed).ToList();
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var (hist, newest, redisc) = QuickplayAggregator.BuildAll(seeds, nowMs, 8);

                List<QuickplayTileItem> ConvertCards(IReadOnlyList<QuickplayCardSeed> src)
                {
                    var items = new List<QuickplayTileItem>(src.Count);
                    foreach (var s in src)
                    {
                        var tracks = s.TrackUids
                            .Select(u => byUid.TryGetValue(u, out var tr) ? tr : null)
                            .Where(tr => tr is not null)
                            .Cast<TrackItem>()
                            .ToList();
                        if (tracks.Count == 0) continue;
                        items.Add(new QuickplayTileItem(s.TileId, s.Kind, s.TargetKey, s.Title, s.Subtitle, s.BadgeText, s.CoverArtPath, tracks));
                    }
                    return items;
                }

                List<QuickplayPinRecord> rawPins = [];
                string? stateDbPath = string.IsNullOrWhiteSpace(_store.ManifestDbPath)
                    ? null
                    : DesktopStateStore.ResolveStateDbPath(_store.ManifestDbPath);
                if (!string.IsNullOrWhiteSpace(stateDbPath) && File.Exists(stateDbPath))
                {
                    rawPins = DesktopStateStore.ListQuickplayPins(stateDbPath).ToList();
                }

                var prebuiltPins = new List<QuickplayTileItem>(rawPins.Count);
                bool hasPlaylistPins = false;
                foreach (var pin in rawPins)
                {
                    if (string.Equals(pin.Kind, "playlist", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPlaylistPins = true;
                        continue;
                    }
                    var matched = loaded
                        .Where(t => QuickplayPinMatcher.MatchesTrack(
                            pin, t.TrackUid, t.Album, t.WorkName, t.Genre,
                            t.ArtistsDisplay, t.ComposersDisplay, t.ConductorsDisplay,
                            t.AlbumDirectory, t.RelPath))
                        .ToList();
                    string? cover = !string.IsNullOrWhiteSpace(pin.CoverPath) && File.Exists(pin.CoverPath)
                        ? pin.CoverPath
                        : matched.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverArtPath))?.CoverArtPath;
                    prebuiltPins.Add(new QuickplayTileItem(
                        pin.PinId, pin.Kind, pin.TargetKey, pin.Title, pin.Subtitle,
                        $"{matched.Count} 首", cover, matched, IsPinned: true));
                }

                return (
                    Tracks: loaded,
                    History: ConvertCards(hist),
                    Newest: ConvertCards(newest),
                    Rediscovery: ConvertCards(redisc),
                    Pins: prebuiltPins,
                    HasPlaylistPins: hasPlaylistPins);
            }, ct);

            ct.ThrowIfCancellationRequested();
            ApplyStage1LoadedLibrary(
                musicRoot,
                stage1.Tracks,
                stage1.History,
                stage1.Newest,
                stage1.Rediscovery,
                stage1.Pins);

            _ = HydrateSecondaryLibraryStateAsync(musicRoot, stage1.Tracks, stage1.HasPlaylistPins, hydrationCts.Token);
        }
        finally
        {
            EndBusy();
        }
    }

    private static QuickplayTrackSeed ToQuickplaySeed(TrackItem t) => new(
        t.TrackUid, t.Title, t.Album, t.ArtistsDisplay, t.WorkName, t.ConductorsDisplay,
        t.Rating, t.EffectivePlayCount, t.LastPlayedTsMs, t.MtimeMs, t.Year, t.CoverArtPath);

    private void ApplyStage1LoadedLibrary(
        string musicRoot,
        List<TrackItem> loaded,
        IReadOnlyList<QuickplayTileItem> history,
        IReadOnlyList<QuickplayTileItem> newlyAdded,
        IReadOnlyList<QuickplayTileItem> rediscovery,
        IReadOnlyList<QuickplayTileItem> pins)
    {
        _allTracks = loaded;
        MusicRootDisplayPath = musicRoot;
        IsViewingPlaylist = false;
        SelectedPlaylist = null;
        UpdateClassicalAdaptiveState();

        ReplaceCollection(QuickplayHistory, history);
        ReplaceCollection(QuickplayNewAlbums, newlyAdded);
        ReplaceCollection(QuickplayRediscovery, rediscovery);
        HasQuickplayHistory = QuickplayHistory.Count > 0;
        ReplaceCollection(QuickplayPins, pins);
        HasQuickplayPins = QuickplayPins.Count > 0;
        HasNoQuickplayPins = !HasQuickplayPins;

        ReloadSessionSlots();
        RestoreSavedPlaybackSession(musicRoot);

        if (PrimaryNavId is "quickplay" or "now_playing")
        {
            UpdateNavigationState();
            CurrentViewHeader = "";
            ActiveFilterPillsSummary = "";
        }
        else
        {
            NavigateToPrimary(PrimaryNavId, SecondarySubTab);
        }

        LibraryLoaded?.Invoke();
        StatusMessage = $"已加载 {_allTracks.Count:N0} 首曲目（{musicRoot}）";
    }

    private async Task HydrateSecondaryLibraryStateAsync(
        string musicRoot,
        List<TrackItem> loaded,
        bool hasPlaylistPins,
        CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;

            var secondary = await Task.Run(() =>
            {
                var refs = loaded.Select(t => new LocalLibraryTrackRef(
                    t.TrackUid, t.RelPath, t.FullPath, t.Title, t.ArtistsDisplay, t.Album,
                    t.DurationMs, t.SampleRateHz, t.ComposersDisplay, t.ConductorsDisplay,
                    t.WorkName, t.MovementName, t.AlbumArtist, t.Genre)).ToList();

                var playlists = PlaylistStore.ListPlaylists(musicRoot, refs).ToList();

                int approvedCount = 0;
                int pendingCount = 0;
                IReadOnlyList<TagDiffProposal> pendingList = [];
                var diffStore = _store.GetStagingDiffStore();
                if (diffStore is not null)
                {
                    (approvedCount, pendingCount) = diffStore.GetPendingProposalCounts();
                    pendingList = diffStore.GetPendingProposals(limit: 300);
                }

                string? autoEqPath = null;
                string? endpointName = SelectedEndpoint?.FriendlyName;
                if (!string.IsNullOrWhiteSpace(endpointName)
                    && !_deviceDspProfiles.ContainsKey(GetCurrentDspProfileKey())
                    && !_usingExternalEqFile)
                {
                    autoEqPath = _engine.TryAutoLoadDeviceDspProfile(musicRoot, endpointName);
                }

                return (playlists, approvedCount, pendingCount, pendingList, autoEqPath);
            }, ct);

            if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
            if (!string.Equals(_store.MusicRoot, musicRoot, StringComparison.OrdinalIgnoreCase)) return;

            string? prevPath = SelectedPlaylist?.FilePath;
            ReplaceCollection(Playlists, secondary.playlists);
            if (!string.IsNullOrWhiteSpace(prevPath))
            {
                SelectedPlaylist = Playlists.FirstOrDefault(p =>
                    string.Equals(p.FilePath, prevPath, StringComparison.OrdinalIgnoreCase));
            }
            if (PrimaryNavId == "collection" && SecondarySubTab == "playlists")
            {
                var target = SelectedPlaylist ?? Playlists.FirstOrDefault();
                if (target is not null)
                {
                    LoadPlaylistTracksIntoView(target);
                }
            }
            if (hasPlaylistPins)
            {
                RefreshQuickplayCollections();
            }

            ApplyStagingSnapshot(secondary.approvedCount, secondary.pendingCount, secondary.pendingList);

            if (FilteredTracks.Count == 0 && _allTracks.Count > 0 && PrimaryNavId is "quickplay" or "now_playing")
            {
                ApplyFiveDimFilter();
                CurrentViewHeader = "";
                ActiveFilterPillsSummary = "";
            }

            if (secondary.autoEqPath is not null)
            {
                _eqProfile = _engine.ActiveProfile;
                _usingExternalEqFile = true;
                _activeEqFilePath = secondary.autoEqPath;
                ActiveEqSummary = $"Auto EQ: {Path.GetFileName(secondary.autoEqPath)}";
            }

            await Task.Delay(350, ct);
            if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;

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
            RestartLibraryFileWatcher(musicRoot);
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
        catch (OperationCanceledException)
        {
            // Cancelled due to folder switch or shutdown
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("MainViewModel.HydrateSecondaryLibraryStateAsync", ex);
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
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
        RefreshQuickplayCollections();
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
            else if (IsViewingPlaylist && SelectedPlaylist is not null && PrimaryNavId == "collection" && SecondarySubTab == "playlists")
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
        NavigateToPrimary("collection", "tracks");
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
                if (PrimaryNavId != "collection" || SecondarySubTab is not "scenes" and not "tracks")
                {
                    PrimaryNavId = "collection";
                    SecondarySubTab = "scenes";
                    ScenesMode = "studio";
                    UpdateNavigationState();
                }
                else if (SecondarySubTab == "scenes" && ScenesMode != "studio")
                {
                    ScenesMode = "studio";
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
                if (PrimaryNavId != "collection" || SecondarySubTab is not "scenes" and not "tracks")
                {
                    PrimaryNavId = "collection";
                    SecondarySubTab = "scenes";
                    ScenesMode = "studio";
                    UpdateNavigationState();
                }
                else if (SecondarySubTab == "scenes" && ScenesMode != "studio")
                {
                    ScenesMode = "studio";
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
                if (PrimaryNavId != "collection" || SecondarySubTab is not "scenes" and not "tracks")
                {
                    PrimaryNavId = "collection";
                    SecondarySubTab = "scenes";
                    ScenesMode = "studio";
                    UpdateNavigationState();
                }
                else if (SecondarySubTab == "scenes" && ScenesMode != "studio")
                {
                    ScenesMode = "studio";
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
        string rawNav = (navId ?? "collection").Trim().ToLowerInvariant();
        string rawSub = (subTab ?? "").Trim().ToLowerInvariant();

        string normalized;
        string targetSubTab = rawSub;
        switch (rawNav)
        {
            case "quickplay":
            case "collection":
            case "studio":
            case "now_playing":
            case "settings":
                normalized = rawNav;
                break;
            case "library":
                normalized = "collection";
                if (string.IsNullOrEmpty(targetSubTab)) targetSubTab = "tracks";
                break;
            case "artists":
                normalized = "collection";
                if (rawSub == "all_performers") ShowAllPerformers = true;
                else if (rawSub == "album_artists") ShowAllPerformers = false;
                targetSubTab = rawSub is "composers" or "conductors" ? rawSub : "artists";
                break;
            case "classical":
                normalized = "collection";
                if (rawSub == "ensembles") ShowClassicalEnsembles = true;
                else if (rawSub == "works") ShowClassicalEnsembles = false;
                targetSubTab = rawSub is "composers" or "conductors" ? rawSub : "classical";
                break;
            case "scenes":
                normalized = "collection";
                targetSubTab = "scenes";
                if (rawSub is "presets" or "autodj" or "studio" or "arc") ScenesMode = rawSub;
                break;
            case "playlists":
                normalized = "collection";
                targetSubTab = "playlists";
                break;
            case "inbox":
                normalized = "studio";
                targetSubTab = "inbox";
                break;
            case "sync":
                normalized = "studio";
                targetSubTab = rawSub is "lastfm" or "usb" ? rawSub : "sync";
                break;
            default:
                normalized = "collection";
                break;
        }

        if (string.Equals(targetSubTab, "folder", StringComparison.Ordinal))
        {
            targetSubTab = "folders";
        }

        if (!IsValidSubTab(normalized, targetSubTab))
        {
            targetSubTab = GetDefaultSubTab(normalized);
        }

        PrimaryNavId = normalized;
        SecondarySubTab = targetSubTab;
        UpdateNavigationState();
        ApplyActiveNavigationData();
        if (normalized == "studio" && targetSubTab is "sync" or "lastfm" or "usb"
            && _uiContext is not null && !_lanSyncManuallyStopped && _syncHost is not { IsRunning: true }
            && !string.IsNullOrWhiteSpace(_store.MusicRoot))
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
        string name = PrimaryNavId == "collection" && SecondarySubTab == "scenes" && ScenesMode == "autodj"
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

        if (PrimaryNavId != "collection" || SecondarySubTab != "scenes" || ScenesMode != "autodj")
        {
            PrimaryNavId = "collection";
            SecondarySubTab = "scenes";
            ScenesMode = "autodj";
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

    private static string GetDefaultSubTab(string navId) => ZuneNavigationRoutes.GetDefaultSubTab(navId);

    private static bool IsValidSubTab(string navId, string subTab) => ZuneNavigationRoutes.IsValidSubTab(navId, subTab);

    partial void OnScenesModeChanged(string value)
    {
        if (PrimaryNavId == "collection" && SecondarySubTab == "scenes")
        {
            UpdateNavigationState();
            if (value == "arc" && SelectedEnergyArc == "flat")
            {
                SelectedEnergyArc = "energy_asc";
            }
        }
    }

    partial void OnShowAllPerformersChanged(bool value)
    {
        if (PrimaryNavId == "collection" && SecondarySubTab == "artists")
        {
            RebuildEntityGroups();
        }
    }

    partial void OnShowClassicalEnsemblesChanged(bool value)
    {
        if (PrimaryNavId == "collection" && SecondarySubTab == "classical")
        {
            RebuildEntityGroups();
        }
    }

    public void SetScenesMode(string mode)
    {
        ScenesMode = mode is "autodj" or "studio" or "arc" ? mode : "presets";
    }

    public void SetNowPlayingPanelTab(string tab)
    {
        string norm = (tab ?? "upnext").Trim().ToLowerInvariant();
        IsNowPlayingUpNextTab = norm == "upnext";
        IsNowPlayingBookletTab = norm == "booklet";
        IsNowPlayingAlternatesTab = norm == "alternates";
    }

    private void UpdateNavigationState()
    {
        IsNavQuickplay = PrimaryNavId == "quickplay";
        IsNavCollection = PrimaryNavId == "collection";
        IsNavStudio = PrimaryNavId == "studio";
        IsNavNowPlaying = PrimaryNavId == "now_playing";
        IsNavSettings = PrimaryNavId == "settings";

        IsQuickplayViewVisible = IsNavQuickplay;
        IsNowPlayingViewVisible = IsNavNowPlaying;
        IsCollectionOrStudioMainVisible = !IsNavQuickplay && !IsNavNowPlaying;

        IsNavLibrary = IsNavCollection && SecondarySubTab is "tracks" or "albums" or "genres" or "folders";
        IsNavArtists = IsNavCollection && SecondarySubTab == "artists";
        IsNavClassical = IsNavCollection && SecondarySubTab is "classical" or "composers" or "conductors";
        IsNavClassicalWorks = IsNavCollection && SecondarySubTab == "classical";
        IsNavClassicalComposers = IsNavCollection && SecondarySubTab == "composers";
        IsNavClassicalConductors = IsNavCollection && SecondarySubTab == "conductors";
        IsNavScenes = IsNavCollection && SecondarySubTab == "scenes";
        IsNavPlaylists = IsNavCollection && SecondarySubTab == "playlists";
        IsNavInbox = IsNavStudio && SecondarySubTab == "inbox";
        IsNavSync = IsNavStudio && SecondarySubTab is "sync" or "lastfm" or "usb";

        bool entityMode = IsNavCollection && SecondarySubTab is "albums" or "artists" or "classical" or "composers" or "conductors" or "genres" or "folders";
        IsEntityBrowserVisible = entityMode;
        IsAlbumsGridMode = IsNavCollection && SecondarySubTab == "albums";
        IsScenesDeckVisible = IsNavCollection && SecondarySubTab == "scenes";
        IsScenesPresetSubTab = IsScenesDeckVisible && ScenesMode == "presets";
        IsScenesAutoDjSubTab = IsScenesDeckVisible && ScenesMode == "autodj";
        IsScenesStudioSubTab = IsScenesDeckVisible && ScenesMode == "studio";
        IsScenesArcSubTab = IsScenesDeckVisible && ScenesMode == "arc";
        IsPlaylistsDeckVisible = IsNavCollection && SecondarySubTab == "playlists";
        IsInboxAuditDeckVisible = IsNavStudio && SecondarySubTab == "inbox";
        IsSyncScrobbleViewVisible = IsNavStudio && SecondarySubTab is "sync" or "lastfm" or "usb";
        IsSettingsViewVisible = IsNavSettings;
        IsTrackTableVisible = IsNavCollection || (IsNavStudio && SecondarySubTab == "inbox");
        IsFiveDimFilterStripVisible = (IsNavCollection && SecondarySubTab == "tracks")
                                      || (IsNavCollection && SecondarySubTab == "scenes" && ScenesMode == "presets");
        UpdateFilterDrawerState();
        UpdateArcStateFlags();

        IsSyncLanSubTab = IsNavStudio && SecondarySubTab == "sync";
        IsSyncLastFmSubTab = IsNavStudio && SecondarySubTab == "lastfm";
        IsSyncUsbSubTab = IsNavStudio && SecondarySubTab == "usb";

        IsSettingsAudioSubTab = IsNavSettings && SecondarySubTab == "audio";
        IsSettingsStorageSubTab = IsNavSettings && SecondarySubTab == "storage";
        IsSettingsApisUiSubTab = IsNavSettings && SecondarySubTab == "apis_ui";

        PrimarySectionTitle = PrimaryNavId switch
        {
            "quickplay" => "quickplay",
            "collection" => "collection",
            "studio" => "studio",
            "now_playing" => "now playing",
            "settings" => "settings",
            _ => "Tonewise",
        };

        (string Id, string Label)[] defs = PrimaryNavId switch
        {
            "collection" => ZuneNavigationRoutes.CollectionSubTabs,
            "studio" =>
            [
                ("sync", "sync"),
                ("lastfm", "last.fm"),
                ("usb", "usb export"),
                ("inbox", "inbox"),
            ],
            "settings" =>
            [
                ("audio", "audio & dsp"),
                ("storage", "storage"),
                ("apis_ui", "appearance"),
            ],
            _ => [],
        };

        CurrentSubTabs.Clear();
        foreach (var (id, label) in defs)
        {
            CurrentSubTabs.Add(new SubTabOption(id, label, id == SecondarySubTab));
        }

        PrimaryViewSummary = defs.Length > 0 ? string.Join(" · ", defs.Select(d => d.Label)) : PrimarySectionTitle;
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
        if (PrimaryNavId is "quickplay" or "now_playing")
        {
            RefreshQuickplayCollections();
            CurrentViewHeader = "";
            ActiveFilterPillsSummary = "";
            return;
        }

        if (PrimaryNavId == "settings" || (PrimaryNavId == "studio" && SecondarySubTab != "inbox"))
        {
            CurrentViewHeader = "";
            ActiveFilterPillsSummary = "";
            return;
        }

        if (PrimaryNavId == "collection" && SecondarySubTab == "playlists")
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

        string nextMode = PrimaryNavId == "studio" && SecondarySubTab == "inbox"
            ? "audit_missing_scene"
            : "all";

        RunBatchFilterUpdate(() =>
        {
            LibraryNavMode = nextMode;
            if (PrimaryNavId == "collection" && SecondarySubTab == "scenes" && ScenesMode == "arc" && SelectedEnergyArc == "flat")
            {
                SelectedEnergyArc = "energy_asc";
            }
        });

        if (PrimaryNavId == "studio" && SecondarySubTab == "inbox")
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
                case ("collection", "albums"):
                {
                    string album = string.IsNullOrWhiteSpace(track.Album) ? "(未分配专辑)" : track.Album;
                    string sub = string.IsNullOrWhiteSpace(track.ArtistsDisplay) ? track.YearDisplay : $"{track.ArtistsDisplay} · {track.YearDisplay}";
                    AddToBucket(album, sub, track);
                    break;
                }
                case ("collection", "genres"):
                {
                    string genre = string.IsNullOrWhiteSpace(track.Genre) ? "(未标注流派)" : track.Genre;
                    AddToBucket(genre, "流派切片", track);
                    break;
                }
                case ("collection", "folders"):
                {
                    string folder = string.IsNullOrWhiteSpace(track.AlbumDirectory)
                        ? Path.GetDirectoryName(track.RelPath)?.Replace('\\', '/') ?? "(根目录)"
                        : track.AlbumDirectory.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(folder)) folder = "(根目录)";
                    AddToBucket(folder, "物理存储目录", track);
                    break;
                }
                case ("collection", "artists"):
                {
                    if (ShowAllPerformers)
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
                    }
                    else
                    {
                        foreach (string artist in SplitPerformers(track.ArtistsDisplay))
                        {
                            AddToBucket(artist, "专辑艺人", track);
                        }
                    }
                    break;
                }
                case ("collection", "classical"):
                {
                    if (!track.IsClassical && string.IsNullOrWhiteSpace(track.WorkName)) break;
                    if (ShowClassicalEnsembles)
                    {
                        foreach (string ensemble in SplitPerformers(track.ArtistsDisplay))
                        {
                            AddToBucket(ensemble, "乐团 / 独奏", track);
                        }
                    }
                    else
                    {
                        string work = !string.IsNullOrWhiteSpace(track.WorkName)
                            ? track.WorkName
                            : (!string.IsNullOrWhiteSpace(track.WorkDisplay) ? track.WorkDisplay : track.Album);
                        if (string.IsNullOrWhiteSpace(work)) break;
                        string sub = string.IsNullOrWhiteSpace(track.ComposersDisplay) ? track.ArtistsDisplay : track.ComposersDisplay;
                        string composerKey = SplitComposers(track.ComposersDisplay).FirstOrDefault()
                            ?? SplitPerformers(track.ArtistsDisplay).FirstOrDefault()
                            ?? "classical";
                        AddToBucket($"{composerKey}::{work}", string.IsNullOrWhiteSpace(sub) ? "古典作品" : sub, track, titleOverride: work);
                    }
                    break;
                }
                case ("collection", "composers"):
                {
                    foreach (string composer in SplitComposers(track.ComposersDisplay))
                    {
                        AddToBucket(composer, "作曲家", track);
                    }
                    break;
                }
                case ("collection", "conductors"):
                {
                    foreach (string conductor in SplitPerformers(track.ConductorsDisplay))
                    {
                        AddToBucket(conductor, "指挥家", track);
                    }
                    break;
                }
            }
        }

        string? prevKey = SelectedEntityGroup?.Key;
        bool sortAlphabetical = SecondarySubTab == "artists";
        IEnumerable<(string Key, string Title, string Subtitle, List<TrackItem> Tracks)> ordered = sortAlphabetical
            ? map.Values
                .OrderBy(v => v.Title.StartsWith('(') ? 1 : 0)
                .ThenBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(1500)
            : map.Values
                .OrderByDescending(v => v.Tracks.Count)
                .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
                .Take(400);

        bool isClassicalWorks = (PrimaryNavId, SecondarySubTab) == ("collection", "classical") && !ShowClassicalEnsembles;
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
            string? cover = entry.Tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverArtPath))?.CoverArtPath;
            groups.Add(new EntityGroupItem(entry.Key, entry.Title, entry.Subtitle, badge, entry.Tracks, cover));
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
        if (PrimaryNavId != "collection" || SecondarySubTab is not "scenes" and not "tracks")
        {
            PrimaryNavId = "collection";
            SecondarySubTab = "scenes";
            ScenesMode = "presets";
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
        if (PrimaryNavId != "collection" || SecondarySubTab is not "scenes" and not "tracks")
        {
            PrimaryNavId = "collection";
            SecondarySubTab = "scenes";
            ScenesMode = "presets";
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
        if (PrimaryNavId != "collection" || SecondarySubTab is not "scenes" and not "tracks")
        {
            PrimaryNavId = "collection";
            SecondarySubTab = "scenes";
            ScenesMode = "presets";
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
        HasMissingTempoBanner = missingTempo > 0 && PrimaryNavId == "studio" && SecondarySubTab == "inbox";
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
        IsFilterDrawerOpen = (PrimaryNavId == "collection" && SecondarySubTab == "scenes" && ScenesMode == "presets")
                             || (IsFiveDimFilterStripVisible && IsFilterStripExpanded);

    public IReadOnlyList<string> GetUniqueCoverArtPaths(int maxCount = 64) =>
        _allTracks
            .Select(t => t.CoverArtPath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .Cast<string>()
            .ToList();

    public void RefreshQuickplayCollections()
    {
        var byUid = _allTracks.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
        var seeds = _allTracks.Select(ToQuickplaySeed).ToList();

        void FillTileList(ObservableCollection<QuickplayTileItem> dest, IReadOnlyList<QuickplayCardSeed> src)
        {
            dest.Clear();
            foreach (var s in src)
            {
                var tracks = s.TrackUids
                    .Select(u => byUid.TryGetValue(u, out var tr) ? tr : null)
                    .Where(tr => tr is not null)
                    .Cast<TrackItem>()
                    .ToList();
                if (tracks.Count == 0) continue;
                dest.Add(new QuickplayTileItem(s.TileId, s.Kind, s.TargetKey, s.Title, s.Subtitle, s.BadgeText, s.CoverArtPath, tracks));
            }
        }

        var (hist, newest, redisc) = QuickplayAggregator.BuildAll(seeds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 8);
        FillTileList(QuickplayHistory, hist);
        FillTileList(QuickplayNewAlbums, newest);
        FillTileList(QuickplayRediscovery, redisc);
        HasQuickplayHistory = QuickplayHistory.Count > 0;

        QuickplayPins.Clear();
        string? stateDbPath = string.IsNullOrWhiteSpace(_store.ManifestDbPath)
            ? null
            : DesktopStateStore.ResolveStateDbPath(_store.ManifestDbPath);
        if (!string.IsNullOrWhiteSpace(stateDbPath) && File.Exists(stateDbPath))
        {
            var pins = DesktopStateStore.ListQuickplayPins(stateDbPath);
            foreach (var pin in pins)
            {
                IReadOnlyList<TrackItem> tracks = ResolveTracksForPin(pin);
                string? cover = !string.IsNullOrWhiteSpace(pin.CoverPath) && File.Exists(pin.CoverPath)
                    ? pin.CoverPath
                    : tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverArtPath))?.CoverArtPath;
                string badge = pin.Kind == "playlist" ? "歌单" : $"{tracks.Count} 首";
                QuickplayPins.Add(new QuickplayTileItem(
                    pin.PinId,
                    pin.Kind,
                    pin.TargetKey,
                    pin.Title,
                    pin.Subtitle,
                    badge,
                    cover,
                    tracks,
                    IsPinned: true));
            }
        }
        HasQuickplayPins = QuickplayPins.Count > 0;
        HasNoQuickplayPins = !HasQuickplayPins;
    }

    private IReadOnlyList<TrackItem> ResolveTracksForPin(QuickplayPinRecord pin)
    {
        if (string.Equals(pin.Kind, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            var pl = Playlists.FirstOrDefault(p =>
                string.Equals(p.Name, pin.TargetKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.FilePath, pin.TargetKey, StringComparison.OrdinalIgnoreCase));
            if (pl is not null)
            {
                var doc = LoadPlaylistDocument(pl);
                var byUid = _allTracks.ToDictionary(t => t.TrackUid, StringComparer.Ordinal);
                return doc.Entries
                    .Where(e => e.IsLocalMatched && e.MatchedTrackUid is not null && byUid.ContainsKey(e.MatchedTrackUid))
                    .Select(e => byUid[e.MatchedTrackUid!])
                    .ToList();
            }
            return [];
        }

        return _allTracks
            .Where(t => QuickplayPinMatcher.MatchesTrack(
                pin,
                t.TrackUid,
                t.Album,
                t.WorkName,
                t.Genre,
                t.ArtistsDisplay,
                t.ComposersDisplay,
                t.ConductorsDisplay,
                t.AlbumDirectory,
                t.RelPath))
            .ToList();
    }

    public void PlayQuickplayTile(QuickplayTileItem? tile)
    {
        if (tile is null) return;
        var tracks = tile.Tracks;
        if (tracks.Count == 0 && tile.IsPinned && !string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            var pins = DesktopStateStore.ListQuickplayPins(DesktopStateStore.ResolveStateDbPath(_store.ManifestDbPath));
            var match = pins.FirstOrDefault(p => p.PinId == tile.TileId);
            if (match is not null) tracks = ResolveTracksForPin(match);
        }

        var playable = tracks.Where(t => !string.IsNullOrWhiteSpace(t.FullPath)).ToList();
        if (playable.Count == 0)
        {
            Notify($"「{tile.Title}」暂无可播放本地曲目。", "warning");
            return;
        }

        _playbackHistory.Clear();
        _djFreezeBackup = null;
        _currentFreezeToken = "";
        IsDjFreezeActive = false;
        PlaybackQueue.ReplaceAll(playable);
        _unshuffledQueueSnapshot = playable;
        if (IsShuffleEnabled && PlaybackQueue.Count > 1)
        {
            ApplyWorkAwareShuffleToQueue(playable[0]);
        }
        PlaybackQueueTitle = tile.Title;
        UpdatePlaybackQueueSummary();
        PlayTrack(PlaybackQueue[0]);
    }

    public void PinEntityGroupToQuickplay(EntityGroupItem? group)
    {
        if (group is null)
        {
            Notify("请先选择一个分组或专辑卡片。", "warning");
            return;
        }
        if (string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            Notify("请先打开本地曲库。", "warning");
            return;
        }

        string kind = SecondarySubTab switch
        {
            "albums" => "album",
            "folders" => "folder",
            "classical" when !ShowClassicalEnsembles => "work",
            "genres" => "genre",
            _ => "entity",
        };
        string targetKey = kind switch
        {
            "work" => group.Title,
            "album" when string.Equals(group.Key, "(未分配专辑)", StringComparison.Ordinal) => QuickplayPinMatcher.EmptyAlbumSentinel,
            _ => group.Key,
        };
        string pinId = $"{kind}::{targetKey.Trim().ToLowerInvariant()}";
        string? cover = group.CoverArtPath ?? group.Tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverArtPath))?.CoverArtPath;

        string stateDbPath = DesktopStateStore.ResolveStateDbPath(_store.ManifestDbPath);
        DesktopStateStore.UpsertQuickplayPin(stateDbPath, new QuickplayPinRecord(
            PinId: pinId,
            Kind: kind,
            TargetKey: targetKey,
            Title: group.Title,
            Subtitle: group.Subtitle,
            CoverPath: cover,
            SortOrder: 0,
            PinnedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        RefreshQuickplayCollections();
        Notify($"已将「{group.Title}」固定到 quickplay。", "success");
    }

    public void PinPlaylistToQuickplay(PlaylistSummary? playlist)
    {
        if (playlist is null || string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            Notify("请先选择歌单。", "warning");
            return;
        }
        string pinId = $"playlist::{playlist.Name.Trim().ToLowerInvariant()}";
        string stateDbPath = DesktopStateStore.ResolveStateDbPath(_store.ManifestDbPath);
        DesktopStateStore.UpsertQuickplayPin(stateDbPath, new QuickplayPinRecord(
            PinId: pinId,
            Kind: "playlist",
            TargetKey: playlist.Name,
            Title: playlist.Name,
            Subtitle: playlist.CountBadge,
            CoverPath: null,
            SortOrder: 0,
            PinnedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        RefreshQuickplayCollections();
        Notify($"已将歌单「{playlist.Name}」固定到 quickplay。", "success");
    }

    [RelayCommand]
    public void PinCurrentTrackAlbumToQuickplay()
    {
        var track = CurrentTrack;
        if (track is null || string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            Notify("当前无正在播放的曲目。", "warning");
            return;
        }
        bool useWork = track.IsClassical && !string.IsNullOrWhiteSpace(track.WorkName);
        bool hasAlbum = !string.IsNullOrWhiteSpace(track.Album);
        string kind = useWork ? "work" : (hasAlbum ? "album" : "track");
        string title = useWork ? track.WorkName : (hasAlbum ? track.Album : track.Title);
        string targetKey = kind == "track" ? track.TrackUid : title;
        string sub = string.IsNullOrWhiteSpace(track.ArtistsDisplay) ? track.ComposersDisplay : track.ArtistsDisplay;
        string pinId = $"{kind}::{targetKey.Trim().ToLowerInvariant()}";
        string stateDbPath = DesktopStateStore.ResolveStateDbPath(_store.ManifestDbPath);
        DesktopStateStore.UpsertQuickplayPin(stateDbPath, new QuickplayPinRecord(
            PinId: pinId,
            Kind: kind,
            TargetKey: targetKey,
            Title: title,
            Subtitle: sub,
            CoverPath: track.CoverArtPath,
            SortOrder: 0,
            PinnedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        RefreshQuickplayCollections();
        Notify($"已将「{title}」固定到 quickplay。", "success");
    }

    public void UnpinQuickplayItem(string? pinId)
    {
        if (string.IsNullOrWhiteSpace(pinId) || string.IsNullOrWhiteSpace(_store.ManifestDbPath)) return;
        string stateDbPath = DesktopStateStore.ResolveStateDbPath(_store.ManifestDbPath);
        if (DesktopStateStore.RemoveQuickplayPin(stateDbPath, pinId))
        {
            RefreshQuickplayCollections();
            Notify("已从 quickplay 取消固定。", "info");
        }
    }

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
