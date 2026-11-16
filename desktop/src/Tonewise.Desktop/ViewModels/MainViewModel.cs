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
using Tonewise.SyncHost.Workbench;

namespace Tonewise.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibraryStore _store = new();
    private readonly TonewiseAudioEngine _engine = new();
    private readonly SynchronizationContext? _uiContext;
    private SyncHostServer? _syncHost;
    private List<TrackItem> _allTracks = [];
    private EqualizerApoProfile? _eqProfile;

    public ObservableCollection<TrackItem> FilteredTracks { get; } = [];
    public ObservableCollection<AudioOutputEndpoint> OutputEndpoints { get; } = [];
    public ObservableCollection<TagDiffProposal> StagingProposals { get; } = [];
    public ObservableCollection<TrackItem> AlternateRecordings { get; } = [];
    public ObservableCollection<SessionSlotDto> SessionSlots { get; } = [];

    [ObservableProperty]
    private string _statusMessage = "Load a music folder containing .tonewise/manifest.db";

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
    private string _lanSyncStatus = "LAN Sync Host: Off";

    [ObservableProperty]
    private string _lastFmLedgerStatus = "Last.fm High-Water-Mark Ledger: Idle";

    [ObservableProperty]
    private string _stagingSummaryText = "Staging Diff Queue: Empty";

    [ObservableProperty]
    private bool _hasStagingProposals;

    [ObservableProperty]
    private string? _lastCommittedBatchId;

    [ObservableProperty]
    private bool _hasAlternateRecordings;

    [ObservableProperty]
    private string _currentBiographySummary = "Select a track to view local encyclopedia notes (encyclopedia.db).";

    [ObservableProperty]
    private string? _currentBookletPath;

    [ObservableProperty]
    private bool _hasBookletPdf;

    public event Action? WaveformUpdated;
    public event Action<TrackItem?>? TrackChanged;

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
                SignalPathSummary =
                    $"{state.OutputDeviceName}\n{state.ShareModeLabel}\n"
                    + $"{state.SampleRateHz / 1000.0:0.#} kHz · {state.BitsPerSample}-bit · "
                    + (state.DspBypassed
                        ? "DSP Bypassed (Bit-Perfect)"
                        : $"64-bit DSP ({state.BiquadCount} PEQ, FIR {state.FirTapCount} taps, ISO226={(state.Iso226Active ? "On" : "Off")}, TPDF={(state.TpdfDitherActive ? "24b" : "Off")}, Preamp {state.EffectivePreampDb:+0.0;-0.0;0.0} dB)");
            }
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
                _uiContext.Post(_ => ApplyState(), null);
            else
                ApplyState();
        };
        _engine.PlaybackEnded += () =>
        {
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
                _uiContext.Post(_ => NextTrack(), null);
            else
                NextTrack();
        };

        RefreshEndpoints();
        ReloadSessionSlots();
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
        ApplyFiveDimFilter();
        RefreshStagingProposals();
        ReloadSessionSlots();

        if (!string.IsNullOrWhiteSpace(SelectedEndpoint?.FriendlyName))
        {
            string? autoEq = _engine.TryAutoLoadDeviceDspProfile(musicRoot, SelectedEndpoint.FriendlyName);
            if (autoEq is not null)
            {
                ActiveEqSummary = $"Auto EQ: {Path.GetFileName(autoEq)}";
            }
        }

        StatusMessage = $"Loaded {_allTracks.Count} track(s) from {musicRoot}";
    }

    public void LoadEqualizerApoFile(string filePath)
    {
        string text = File.ReadAllText(filePath);
        _eqProfile = EqualizerApoProfile.Parse(text);
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
    }

    public void LoadFirImpulseFile(string wavPath)
    {
        _engine.SetFirImpulseFile(wavPath, FirDryWetMix);
        FirImpulseFileName = $"FIR: {Path.GetFileName(wavPath)}";
    }

    partial void OnFilterSceneChanged(string value) => ApplyFiveDimFilter();
    partial void OnFilterEnergyChanged(string value) => ApplyFiveDimFilter();
    partial void OnFilterVocalsChanged(string value) => ApplyFiveDimFilter();
    partial void OnExcludeVocalChanged(bool value) => ApplyFiveDimFilter();
    partial void OnFilterTempoBucketChanged(string value) => ApplyFiveDimFilter();
    partial void OnFilterMoodClusterChanged(string value) => ApplyFiveDimFilter();
    partial void OnSelectedEnergyArcChanged(string value) => ApplyFiveDimFilter();
    partial void OnPreferWasapiExclusiveChanged(bool value) => PushEnginePreferences();
    partial void OnSelectedEndpointChanged(AudioOutputEndpoint? value) => PushEnginePreferences();
    partial void OnIsIso226EnabledChanged(bool value) => PushAcousticPipeline();
    partial void OnVolumeAttenuationDbChanged(double value) => PushAcousticPipeline();
    partial void OnIsTpdfDitherEnabledChanged(bool value) => PushAcousticPipeline();
    partial void OnFirDryWetMixChanged(double value) => PushAcousticPipeline();

    partial void OnIsPlayingChanged(bool value)
    {
        PlayPauseLabel = value ? "Pause" : "Play";
    }

    public void ApplyFiveDimFilter()
    {
        FilteredTracks.Clear();
        if (_allTracks.Count == 0)
        {
            MissingTempoCount = 0;
            HasMissingTempoBanner = false;
            MissingMetadataBannerText = "";
            return;
        }

        int missingTempo = _allTracks.Count(t => t.TempoBucket is null && t.TempoBpm is null);
        MissingTempoCount = missingTempo;
        HasMissingTempoBanner = missingTempo > 0;
        MissingMetadataBannerText = missingTempo > 0
            ? $"{missingTempo} track(s) missing Tempo/5D tags — Tempo IS NULL passthrough active. Click 'Scan Missing' to populate Staging Diff Queue."
            : "All tracks carry complete Tempo metadata.";

        var query = new FiveDimFilterQuery(
            Scene: FilterScene,
            Energy: FilterEnergy,
            Vocals: FilterVocals,
            ExcludeVocal: ExcludeVocal,
            TempoBucket: FilterTempoBucket,
            MoodCluster: FilterMoodCluster,
            SortArc: SelectedEnergyArc,
            TempoNullPassthrough: true
        );

        if (!FiveDimQueryBuilder.RequiresFilterOrSort(query))
        {
            foreach (var t in _allTracks) FilteredTracks.Add(t);
            return;
        }

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
        foreach (string uid in orderedUids)
        {
            if (byUid.TryGetValue(uid, out var track))
            {
                FilteredTracks.Add(track);
            }
        }
    }

    public void PlayTrack(TrackItem track, double initialSeekFraction = 0.0)
    {
        CurrentTrack = track;
        CurrentWaveform = _store.LoadWaveform(track);
        CurrentBookletPath = track.BookletPdfPath;
        HasBookletPdf = !string.IsNullOrWhiteSpace(track.BookletPdfPath) && File.Exists(track.BookletPdfPath);
        UpdateClassicalAndBiographyPanel(track);
        WaveformUpdated?.Invoke();
        TrackChanged?.Invoke(track);

        PushEnginePreferences();
        if (File.Exists(track.FullPath))
        {
            _engine.PlayFile(track.FullPath);
            if (initialSeekFraction > 0.0)
            {
                _engine.Seek(Math.Clamp(initialSeekFraction, 0.0, 0.99));
            }
            IsPlaying = true;
            StatusMessage = $"Playing: {track.Title}";
        }
        else
        {
            IsPlaying = false;
            StatusMessage = $"File not found: {track.FullPath}";
        }
    }

    public void SwitchAlternateRecording(TrackItem alternate)
    {
        double ratio = PlaybackProgress;
        PlayTrack(alternate, initialSeekFraction: ratio);
        StatusMessage = $"A/B Switched ({ratio:P0} in movement): {alternate.AlternateVersionLabel}";
    }

    private void UpdateClassicalAndBiographyPanel(TrackItem track)
    {
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
            CurrentBiographySummary = $"No local encyclopedia entry for '{lookupName}'.";
        }
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        _engine.TogglePlayPause();
        IsPlaying = _engine.IsPlaying;
    }

    [RelayCommand]
    public void NextTrack()
    {
        if (FilteredTracks.Count == 0) return;
        int idx = CurrentTrack is null ? -1 : FilteredTracks.IndexOf(CurrentTrack);
        int nextIdx = (idx + 1) % FilteredTracks.Count;
        PlayTrack(FilteredTracks[nextIdx]);
    }

    [RelayCommand]
    public void PreviousTrack()
    {
        if (FilteredTracks.Count == 0) return;
        int idx = CurrentTrack is null ? 0 : FilteredTracks.IndexOf(CurrentTrack);
        int prevIdx = (idx - 1 + FilteredTracks.Count) % FilteredTracks.Count;
        PlayTrack(FilteredTracks[prevIdx]);
    }

    [RelayCommand]
    public void ActivateCodingQuickScene()
    {
        FilterScene = "coding";
        ExcludeVocal = true;
        SelectedEnergyArc = "flat";
        if (FilteredTracks.Count > 0)
        {
            PlayTrack(FilteredTracks[0]);
        }
    }

    public void SeekFraction(double fraction)
    {
        _engine.Seek(fraction);
        RefreshProgress();
    }

    public void RefreshProgress()
    {
        var dur = _engine.Duration;
        var pos = _engine.Position;
        PlaybackProgress = dur.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0) : 0.0;
        TimeDisplay = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2} / {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
        IsPlaying = _engine.IsPlaying;
    }

    [RelayCommand]
    private void RunFillMissingScan()
    {
        int added = _store.ScanMissingMetadataAndEnqueue(_allTracks);
        RefreshStagingProposals();
        StatusMessage = $"Fill-Missing scan queued {added} proposal(s) into Staging Diff Queue.";
    }

    [RelayCommand]
    private void CommitStagingBatch()
    {
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null) return;

        var result = diffStore.CommitApprovedBatch();
        if (result.CommittedCount > 0)
        {
            LastCommittedBatchId = result.BatchId;
            if (!string.IsNullOrWhiteSpace(_store.MusicRoot))
            {
                _allTracks = _store.LoadFromMusicRoot(_store.MusicRoot).ToList();
                ApplyFiveDimFilter();
            }
        }
        RefreshStagingProposals();
        StatusMessage = $"Committed {result.CommittedCount} proposal(s) under batch {result.BatchId}.";
    }

    [RelayCommand]
    private void RollbackLastBatch()
    {
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null || string.IsNullOrWhiteSpace(LastCommittedBatchId))
        {
            StatusMessage = "No committed curation batch available to rollback.";
            return;
        }

        string bid = LastCommittedBatchId;
        int restored = diffStore.RollbackBatch(bid);
        LastCommittedBatchId = null;
        if (!string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            _allTracks = _store.LoadFromMusicRoot(_store.MusicRoot).ToList();
            ApplyFiveDimFilter();
        }
        RefreshStagingProposals();
        StatusMessage = $"Rolled back {restored} tag changes from batch {bid}.";
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
            StatusMessage = "Open a music folder first.";
            return;
        }
        string root = _store.MusicRoot;
        StatusMessage = $"Scanning audio library in {root}...";
        await Task.Run(() => _store.ScanAndBuildManifestIfMissing(root, forceRebuild: true));
        _allTracks = _store.LoadFromMusicRoot(root).ToList();
        ApplyFiveDimFilter();
        RefreshStagingProposals();
        StatusMessage = $"Rebuilt manifest & indexed {_allTracks.Count} track(s).";
    }

    [RelayCommand]
    private async Task EnrichLibraryCoversAndBiosAsync()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot) || _allTracks.Count == 0)
        {
            StatusMessage = "Load a music library before running cover/biography enrichment.";
            return;
        }

        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        string? lfmApiKey = LastFmAccountConfig.Load(tonewiseDir)?.ApiKey;
        var coverService = new CoverArtWaterfallService();
        var encService = _store.GetEncyclopediaService()
                         ?? new EncyclopediaService(Path.Combine(tonewiseDir, "encyclopedia.db"));

        StatusMessage = "Fetching missing album covers & composer/artist encyclopedia bios...";
        int coversSaved = 0;
        int biosCached = 0;

        await Task.Run(async () =>
        {
            var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in _allTracks)
            {
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
        StatusMessage = $"Enrichment complete: {coversSaved} new cover.jpg saved, {biosCached} encyclopedia bios cached.";
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
            LastFmLedgerStatus = "Open a music folder first.";
            return;
        }

        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        var client = new LastFmWebApiClient();
        string sessionKey = isRawSessionKey
            ? passwordOrSessionKey.Trim()
            : await client.AuthenticateMobileSessionAsync(apiKey.Trim(), apiSecret.Trim(), username.Trim(), passwordOrSessionKey);

        var config = new LastFmAccountConfig(apiKey.Trim(), apiSecret.Trim(), username.Trim(), sessionKey);
        LastFmAccountConfig.Save(tonewiseDir, config);

        var summary = await client.RunFullBidirectionalSyncAsync(_store.ManifestDbPath, tonewiseDir, config);
        LastFmLedgerStatus =
            $"Last.fm Synced (@{username}): +{summary.PulledCloudPlays} pulled, {summary.LovedTracksUpdated} loved, {summary.PushedScrobbles} scrobbled, {summary.ExpiredArchivedCount} archived (>14d).";

        _allTracks = _store.LoadFromMusicRoot(_store.MusicRoot).ToList();
        ApplyFiveDimFilter();
    }

    [RelayCommand]
    private async Task SyncLastFmLedgerAsync()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot) || string.IsNullOrWhiteSpace(_store.ManifestDbPath))
        {
            LastFmLedgerStatus = "Open a music folder first.";
            return;
        }

        string tonewiseDir = Path.Combine(_store.MusicRoot, ".tonewise");
        var cfg = LastFmAccountConfig.Load(tonewiseDir);
        if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.SessionKey))
        {
            try
            {
                LastFmLedgerStatus = $"Syncing with Last.fm (@{cfg.Username})...";
                var client = new LastFmWebApiClient();
                var summary = await client.RunFullBidirectionalSyncAsync(_store.ManifestDbPath, tonewiseDir, cfg);
                LastFmLedgerStatus =
                    $"Last.fm Synced (@{cfg.Username}): +{summary.PulledCloudPlays} pulled, {summary.LovedTracksUpdated} loved, {summary.PushedScrobbles} scrobbled, {summary.ExpiredArchivedCount} archived.";
                _allTracks = _store.LoadFromMusicRoot(_store.MusicRoot).ToList();
                ApplyFiveDimFilter();
                return;
            }
            catch (Exception ex)
            {
                LastFmLedgerStatus = $"Last.fm cloud sync error (fell back to local ledger): {ex.Message}";
            }
        }

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var prep = LastFmLedgerService.PrepareScrobbleBatches(_store.ManifestDbPath, tonewiseDir, nowSec);
        int queued = prep.Batches.Sum(b => b.Items.Count);
        LastFmLedgerStatus = $"Last.fm Local Ledger: {queued} pending scrobble(s), {prep.DeduplicatedCount} deduped, {prep.ExpiredCount} expired (>14d archived).";

        _allTracks = _store.LoadFromMusicRoot(_store.MusicRoot).ToList();
        ApplyFiveDimFilter();
    }

    public string ExportFilteredToUsb(string usbRootDir)
    {
        var exportTracks = FilteredTracks
            .Select(t => new UsbExportTrack(t.TrackUid, t.FullPath, t.Title, t.ArtistsDisplay, t.Album, t.DurationMs, t.CoverArtPath))
            .ToList();
        string name = FilterScene == "all" ? "Tonewise-Export" : $"Tonewise-{FilterScene}";
        var summary = UsbCarExporter.ExportPlaylistToUsb(name, exportTracks, usbRootDir);
        StatusMessage = $"USB Exported {summary.ExportedTracks} tracks & {summary.ExportedCovers} covers -> {summary.PlaylistM3u8Path}";
        return summary.PlaylistM3u8Path;
    }

    public void SaveCurrentToSessionSlot(int slotIndex)
    {
        if (string.IsNullOrWhiteSpace(_store.SessionsJsonPath)) return;
        var uids = FilteredTracks.Select(t => t.TrackUid).ToList();
        int curIdx = CurrentTrack is null ? 0 : Math.Max(0, FilteredTracks.IndexOf(CurrentTrack));
        ulong posMs = (ulong)Math.Max(0, _engine.Position.TotalMilliseconds);
        ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string name = SessionSlots.FirstOrDefault(s => s.SlotIndex == slotIndex)?.Name ?? $"Slot {slotIndex}";
        var dto = new SessionSlotDto(slotIndex, name, FilterScene, nowMs, uids, curIdx, posMs);

        var updated = SessionSlotStore.SaveSlot(_store.SessionsJsonPath, dto);
        SessionSlots.Clear();
        foreach (var s in updated) SessionSlots.Add(s);
        StatusMessage = $"Saved {uids.Count} tracks to Session Slot {slotIndex} ({name}).";
    }

    public void RestoreSessionSlot(int slotIndex)
    {
        var slot = SessionSlots.FirstOrDefault(s => s.SlotIndex == slotIndex);
        if (slot is null) return;
        if (!string.IsNullOrWhiteSpace(slot.SceneId)) FilterScene = slot.SceneId;
        if (slot.QueueTrackUids.Count > 0 && slot.CurrentIndex < slot.QueueTrackUids.Count)
        {
            string targetUid = slot.QueueTrackUids[slot.CurrentIndex];
            var match = _allTracks.FirstOrDefault(t => t.TrackUid == targetUid);
            if (match is not null)
            {
                double frac = match.DurationMs > 0 ? slot.PositionMs / (double)match.DurationMs : 0.0;
                PlayTrack(match, frac);
                return;
            }
        }
        StatusMessage = $"Switched to Session Slot {slotIndex} ({slot.Name}).";
    }

    private void ReloadSessionSlots()
    {
        string path = _store.SessionsJsonPath ?? Path.Combine(Path.GetTempPath(), "tonewise_sessions_default.json");
        SessionSlots.Clear();
        foreach (var s in SessionSlotStore.LoadOrInitializeDefault(path))
        {
            SessionSlots.Add(s);
        }
    }

    private void RefreshStagingProposals()
    {
        StagingProposals.Clear();
        var diffStore = _store.GetStagingDiffStore();
        if (diffStore is null)
        {
            HasStagingProposals = false;
            StagingSummaryText = "Staging Diff Queue: Unavailable";
            return;
        }
        foreach (var p in diffStore.GetPendingProposals())
        {
            StagingProposals.Add(p);
        }
        HasStagingProposals = StagingProposals.Count > 0;
        StagingSummaryText = $"Staging Diff Queue: {StagingProposals.Count} pending proposal(s)";
    }

    private void PushEnginePreferences()
    {
        _engine.SetPreferences(
            PreferWasapiExclusive,
            SelectedEndpoint?.Id,
            _eqProfile,
            CurrentTrack?.ReplayGainTrackDb ?? 0.0
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

    [RelayCommand]
    private async Task ToggleLanSyncHostAsync()
    {
        if (_syncHost is not null)
        {
            await _syncHost.DisposeAsync();
            _syncHost = null;
            LanSyncStatus = "LAN Sync Host: Off";
            return;
        }
        if (string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            LanSyncStatus = "Open a music folder first";
            return;
        }
        var host = new SyncHostServer(_store.MusicRoot);
        int port = await host.StartAsync(45820, enableMdns: true);
        _syncHost = host;
        LanSyncStatus = $"LAN Sync Host: Active on port {port} (mDNS _tonewise._tcp)";
    }

    public void Dispose()
    {
        _syncHost?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _engine.Dispose();
    }
}
