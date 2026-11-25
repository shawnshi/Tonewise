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
    public async Task RefreshEndpointsAsync(bool handleLostSelection = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var endpoints = await Task.Run(AudioDeviceRouter.EnumerateRenderEndpoints).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0) return;

        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    ApplyEndpointsOnUiThread(endpoints, handleLostSelection);
                }
            }, null);
        }
        else
        {
            ApplyEndpointsOnUiThread(endpoints, handleLostSelection);
        }
    }

    public void RefreshEndpoints(bool handleLostSelection = false)
    {
        var endpoints = AudioDeviceRouter.EnumerateRenderEndpoints();
        ApplyEndpointsOnUiThread(endpoints, handleLostSelection);
    }

    private void ApplyEndpointsOnUiThread(IReadOnlyList<AudioOutputEndpoint> endpoints, bool handleLostSelection)
    {
        string? previousId = !string.IsNullOrWhiteSpace(SelectedEndpoint?.Id)
            ? SelectedEndpoint.Id
            : _pendingSavedEndpointId;
        EndpointReconcileResult reconciled;
        bool prevSup = _suppressDspSync;
        bool prevSaveSup = _suppressSettingsSave;
        _suppressDspSync = true;
        _suppressSettingsSave = true;
        try
        {
            reconciled = AudioEndpointReconciler.ReconcileInPlace(OutputEndpoints, endpoints, previousId);
            if (!string.Equals(SelectedEndpoint?.Id, reconciled.TargetSelection?.Id, StringComparison.Ordinal))
            {
                SelectedEndpoint = reconciled.TargetSelection;
            }
            if (!string.IsNullOrWhiteSpace(_pendingSavedEndpointId) && SelectedEndpoint is not null)
            {
                _pendingSavedEndpointId = null;
                LoadDspConfigForCurrentEndpoint(_store.MusicRoot);
            }
        }
        finally
        {
            _suppressDspSync = prevSup;
            _suppressSettingsSave = prevSaveSup;
        }

        if (handleLostSelection && reconciled.LostPreviousSelection && SelectedEndpoint is not null)
        {
            LoadDspConfigForCurrentEndpoint(_store.MusicRoot);
            PushEnginePreferences();
            if (_engine.CurrentFilePath is not null)
            {
                ReopenCurrentOutputAtCurrentPosition();
            }
            else if (_pendingEndpointReturnInterruption is not null
                && DateTime.UtcNow <= _pendingEndpointReturnDeadlineUtc
                && !_isAutoRecoveringPlayback)
            {
                TryResumeAfterEndpointReturn();
            }
        }
        else if (handleLostSelection
            && _pendingEndpointReturnInterruption is not null
            && DateTime.UtcNow <= _pendingEndpointReturnDeadlineUtc
            && OutputEndpoints.Count > 0
            && !_isAutoRecoveringPlayback)
        {
            TryResumeAfterEndpointReturn();
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

    [RelayCommand]
    public void ClearFirImpulse()
    {
        _engine.SetFirImpulseFile(null);
        _firImpulseFilePath = null;
        FirImpulseFileName = "FIR: 未加载";
        SaveUserSettings();
    }

    public void LoadFirImpulseFile(string wavPath)
    {
        _engine.SetFirImpulseFile(wavPath, FirDryWetMix);
        _firImpulseFilePath = wavPath;
        FirImpulseFileName = $"FIR: {Path.GetFileName(wavPath)}";
        SaveUserSettings();
    }

    private string GetCurrentDspProfileKey()
    {
        string raw = SelectedEndpoint?.FriendlyName ?? SelectedEndpoint?.Id ?? "Default";
        return raw.Trim();
    }

    private void OnBuiltInDspChanged(bool markCustomEq, bool autoEnableExtended = false)
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
            else if (autoEnableExtended && !IsDspEnabled)
            {
                IsDspEnabled = true;
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
            _activeBandTemplates = null;
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
        _activeBandTemplates = [.. bands];
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
        var template = _activeBandTemplates is { Count: > 0 }
            ? _activeBandTemplates
            : DesktopDspChainConfig.CreatePreset(preset).EffectiveBands;
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
                _eqProfile = _engine.ActiveProfile;
                _usingExternalEqFile = true;
                _activeEqFilePath = autoEq;
                ActiveEqSummary = $"Auto EQ: {Path.GetFileName(autoEq)}";
            }
        }
    }
    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    private void SyncVolumePercentFromDb(double db)
    {
        if (!_syncingVolume)
        {
            _syncingVolume = true;
            try
            {
                VolumePercent = db <= -79.5
                    ? 0.0
                    : Math.Round(Math.Clamp(100.0 * Math.Pow(10.0, db / 40.0), 0.0, 100.0), 0);
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

    private bool ShouldUseAlbumGainForTrack(TrackItem track)
    {
        if (string.IsNullOrWhiteSpace(track.Album)) return false;
        IList<TrackItem> q = PlaybackQueue.Count > 0 ? PlaybackQueue : FilteredTracks;
        int idx = -1;
        for (int i = 0; i < q.Count; i++)
        {
            if (string.Equals(q[i].TrackUid, track.TrackUid, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }
        string? currentWorkKey = WorkAwareShuffleEngine.BuildWorkBlockKey(
            track.WorkName,
            track.Album,
            WorkAwareShuffleEngine.BuildAlbumScope(track.Album, track.ConductorsDisplay));
        return ReplayGainResolver.ShouldUseAlbumReplayGain(
            IsShuffleEnabled,
            q.Count,
            idx,
            i => q[i].Album,
            i => WorkAwareShuffleEngine.BuildWorkBlockKey(
                q[i].WorkName,
                q[i].Album,
                WorkAwareShuffleEngine.BuildAlbumScope(q[i].Album, q[i].ConductorsDisplay)),
            track.Album,
            currentWorkKey);
    }

    private double ResolveReplayGainForTrack(TrackItem? track)
    {
        if (track is null) return 0.0;
        return ReplayGainResolver.ResolveGainDb(
            ReplayGainMode,
            track.ReplayGainTrackDb,
            track.ReplayGainAlbumDb,
            ShouldUseAlbumGainForTrack(track),
            ReplayGainPreAmpWithTagDb,
            ReplayGainPreAmpWithoutTagDb);
    }

    private void PushEnginePreferences()
    {
        var extDsp = BuildCurrentExtendedDspConfig();
        var layerAProfile = _usingExternalEqFile
            ? _eqProfile
            : (extDsp.Enabled ? extDsp.ToEqualizerApoProfile() : null);

        var (composedProfile, effectiveCrossfeed) = ComposeActiveDspProfile(
            extDsp,
            layerAProfile,
            _activeAutoEqResolution);

        _engine.SetExtendedDsp(extDsp with
        {
            Enabled = extDsp.Enabled || _usingExternalEqFile,
            CrossfeedEnabled = effectiveCrossfeed,
        });
        _appliedEngineEndpointId = SelectedEndpoint?.Id;
        _engine.SetPreferences(
            PreferWasapiExclusive,
            SelectedEndpoint?.Id,
            composedProfile,
            ResolveReplayGainForTrack(CurrentTrack)
        );
        UpdateAutoEqBadge();
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

    private static string BuildAutoEqMetaFingerprint(TrackItem track) =>
        $"{track.Genre}|{track.DjVocals}|{track.WorkName}|{track.Album}|{track.ConductorsDisplay}";

    private static TrackAutoEqInputDto BuildTrackAutoEqInput(TrackItem track)
    {
        var genres = string.IsNullOrWhiteSpace(track.Genre)
            ? []
            : track.Genre
                .Split([';', '/', ',', '、', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        return new TrackAutoEqInputDto(
            Genres: genres,
            DjVocals: track.DjVocals,
            WorkName: track.WorkName,
            WorkKey: BuildTrackWorkKey(track));
    }

    private void ResolveAndUpdateCurrentAutoEq(
        TrackItem? track,
        bool keepWorkLockFromPrevious,
        string? previousWorkKeyOverride = null)
    {
        if (track is null)
        {
            _activeAutoEqResolution = null;
            _lastAutoEqWorkKey = null;
            _activeAutoEqTrackUid = null;
            _activeAutoEqTrackMetaKey = null;
            UpdateAutoEqBadge();
            return;
        }

        string? prevWorkKey = keepWorkLockFromPrevious
            ? (previousWorkKeyOverride ?? _lastAutoEqWorkKey)
            : null;
        var prevRes = keepWorkLockFromPrevious ? _activeAutoEqResolution : null;

        _activeAutoEqResolution = TonewiseCoreClient.ResolveAutoEq(
            _autoEqPolicy,
            BuildTrackAutoEqInput(track),
            prevWorkKey,
            prevRes);
        _lastAutoEqWorkKey = BuildTrackWorkKey(track);
        _activeAutoEqTrackUid = track.TrackUid;
        _activeAutoEqTrackMetaKey = BuildAutoEqMetaFingerprint(track);
        UpdateAutoEqBadge();
    }

    private GaplessDspTransition BuildGaplessDspTransition(TrackItem nextTrack, double nextReplayGainDb)
    {
        var extDsp = BuildCurrentExtendedDspConfig();
        var layerAProfile = _usingExternalEqFile
            ? _eqProfile
            : (extDsp.Enabled ? extDsp.ToEqualizerApoProfile() : null);

        var nextResolved = TonewiseCoreClient.ResolveAutoEq(
            _autoEqPolicy,
            BuildTrackAutoEqInput(nextTrack),
            _lastAutoEqWorkKey,
            _activeAutoEqResolution);

        var (composedProfile, effectiveCrossfeed) = ComposeActiveDspProfile(
            extDsp,
            layerAProfile,
            nextResolved);

        return new GaplessDspTransition(
            ReplayGainDb: nextReplayGainDb,
            ComposedProfile: composedProfile,
            HasProfileOverride: true,
            CrossfeedEnabled: effectiveCrossfeed);
    }

    private (EqualizerApoProfile? Profile, bool EffectiveCrossfeed) ComposeActiveDspProfile(
        DesktopDspChainConfig layerAConfig,
        EqualizerApoProfile? externalLayerA,
        ResolvedAutoEqDto? layerB)
    {
        bool masterActive = layerAConfig.Enabled || _usingExternalEqFile;
        if (!masterActive)
        {
            return (null, false);
        }

        bool layerBActive = _autoEqPolicy.Enabled && layerB is { Applied: true };
        if (!layerBActive || layerB is null)
        {
            return (externalLayerA ?? layerAConfig.ToEqualizerApoProfile(), layerAConfig.CrossfeedEnabled);
        }

        var deviceFilters = _usingExternalEqFile && externalLayerA is not null
            ? externalLayerA.Filters
                .Select(f => new AutoEqBiquadDto(f.Kind.ToString(), (float)f.FrequencyHz, (float)f.GainDb, (float)f.Q))
                .ToList()
            : layerAConfig.EffectiveBands
                .Where(b => b.Enabled)
                .Select(b => new AutoEqBiquadDto(b.Kind.ToString(), (float)b.FrequencyHz, (float)b.GainDb, (float)b.Q))
                .ToList();
        float devicePreamp = (float)(_usingExternalEqFile && externalLayerA is not null
            ? externalLayerA.PreampDb
            : layerAConfig.PreampDb);

        var composed = TonewiseCoreClient.ComposeDsp(
            new DspProfileDto(devicePreamp, deviceFilters),
            new DspProfileDto(layerB.PreampDb, layerB.Filters));

        var specs = composed.Filters.Select(MapDtoToBiquadSpec).ToList();
        var mergedProfile = new EqualizerApoProfile(
            PreampDb: composed.PreampDb,
            Filters: specs,
            BoundDeviceName: externalLayerA?.BoundDeviceName,
            FirImpulsePath: externalLayerA?.FirImpulsePath,
            EnableIso226: externalLayerA?.EnableIso226 ?? false,
            ReferenceSplDb: externalLayerA?.ReferenceSplDb ?? 80.0);

        bool effectiveCrossfeed = layerAConfig.CrossfeedEnabled || layerB.EnableCrossfeed;
        return (mergedProfile, effectiveCrossfeed);
    }

    private static BiquadSpec MapDtoToBiquadSpec(AutoEqBiquadDto f)
    {
        var kind = f.Kind.Trim().ToLowerInvariant() switch
        {
            "lowshelf" or "low_shelf" or "lsc" or "ls" => BiquadKind.LowShelf,
            "highshelf" or "high_shelf" or "hsc" or "hs" => BiquadKind.HighShelf,
            "lowpass" or "low_pass" or "lp" or "lpq" => BiquadKind.LowPass,
            "highpass" or "high_pass" or "hp" or "hpq" => BiquadKind.HighPass,
            _ => BiquadKind.Peaking,
        };
        return new BiquadSpec(kind, f.FreqHz, f.GainDb, f.Q, BiquadChannelScope.All);
    }

    private void UpdateAutoEqBadge()
    {
        if (!_autoEqPolicy.Enabled)
        {
            ActiveAutoEqBadge = "自动内容 EQ：已关闭";
            return;
        }
        if (!IsDspEnabled && !_usingExternalEqFile)
        {
            ActiveAutoEqBadge = "自动内容 EQ：随内置 DSP 旁路";
            return;
        }
        var res = _activeAutoEqResolution;
        if (res is null || !res.Applied)
        {
            ActiveAutoEqBadge = "自动内容 EQ：就绪（等待曲目）";
            return;
        }
        string modeDetail = !res.HasNonZeroEq && res.EnableCrossfeed
            ? "Flat + Crossfeed"
            : !res.HasNonZeroEq
                ? "Flat 0 dB"
                : res.EnableCrossfeed
                    ? "EQ + Crossfeed"
                    : "已叠加 EQ";
        string lockNote = res.LockedToPreviousWork ? " · 作品锁定" : "";
        ActiveAutoEqBadge = $"自动内容层：{res.DisplayLabel}（{modeDetail}{lockNote}）";
    }

    public void RefreshAutoEqAndPush()
    {
        ResolveAndUpdateCurrentAutoEq(CurrentTrack, keepWorkLockFromPrevious: false);
        PushEnginePreferences();
        PreloadNextGaplessTrack();
    }

    private void ApplyAutoEqPolicyToViewModel(AutoEqPolicyDto policy, bool fromSync = false)
    {
        _autoEqPolicy = policy with { CustomRules = policy.CustomRules ?? [] };
        bool prevSync = _suppressAutoEqSync;
        _suppressAutoEqSync = true;
        try
        {
            IsAutoEqEnabled = _autoEqPolicy.Enabled;
            IsAutoEqClassicalCrossfeedEnabled = _autoEqPolicy.ClassicalAutoCrossfeed;
            IsAutoEqLockClassicalWorkEnabled = _autoEqPolicy.LockWithinClassicalWork;
        }
        finally
        {
            _suppressAutoEqSync = prevSync;
        }
        LoadSelectedAutoEqRuleSlots();
        ResolveAndUpdateCurrentAutoEq(CurrentTrack, keepWorkLockFromPrevious: false);
        if (fromSync)
        {
            PushEnginePreferences();
            PreloadNextGaplessTrack();
        }
        else
        {
            UpdateAutoEqBadge();
        }
    }

    private void OnAutoEqToggleChanged(bool autoEnableDsp)
    {
        if (_suppressAutoEqSync) return;
        _autoEqPolicy = _autoEqPolicy with
        {
            Enabled = IsAutoEqEnabled,
            ClassicalAutoCrossfeed = IsAutoEqClassicalCrossfeedEnabled,
            LockWithinClassicalWork = IsAutoEqLockClassicalWorkEnabled,
            UpdatedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CustomRules = _autoEqPolicy.CustomRules ?? [],
        };
        if (autoEnableDsp && !IsDspEnabled)
        {
            _suppressDspSync = true;
            try
            {
                IsDspEnabled = true;
                var cfg = BuildCurrentExtendedDspConfig();
                _deviceDspProfiles[GetCurrentDspProfileKey()] = cfg;
            }
            finally
            {
                _suppressDspSync = false;
            }
        }
        PersistAutoEqPolicyToWorkspaceAndSettings();
        RefreshAutoEqAndPush();
    }

    private void PersistAutoEqPolicyToWorkspaceAndSettings()
    {
        SaveUserSettings();
        if (!string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            try
            {
                var host = new SyncHostServer(_store.MusicRoot, store: _syncStore);
                host.WriteAutoEqPolicy(_autoEqPolicy);
            }
            catch
            {
                // Non-fatal if library root is temporarily read-only
            }
        }
    }

    private void SyncAutoEqPolicyWithLibraryRoot()
    {
        if (string.IsNullOrWhiteSpace(_store.MusicRoot)) return;
        try
        {
            var host = new SyncHostServer(_store.MusicRoot, store: _syncStore);
            var (winner, changed) = host.MergeAutoEqPolicyWithChangeFlag(_autoEqPolicy);
            if (!changed && winner.UpdatedAtMs > _autoEqPolicy.UpdatedAtMs)
            {
                ApplyAutoEqPolicyToViewModel(winner, fromSync: true);
                SaveUserSettings();
            }
        }
        catch
        {
            // Non-fatal fallback
        }
    }

    private string CurrentRuleSlotKey =>
        $"{(string.IsNullOrWhiteSpace(SelectedAutoEqGenre) ? "classical" : SelectedAutoEqGenre.Trim().ToLowerInvariant())}:{(string.IsNullOrWhiteSpace(SelectedAutoEqVocal) ? "instrumental" : SelectedAutoEqVocal.Trim().ToLowerInvariant())}";

    private void LoadSelectedAutoEqRuleSlots()
    {
        string key = CurrentRuleSlotKey;
        AutoEqRuleOverrideDto? custom = null;
        bool isCustom = _autoEqPolicy.CustomRules is { } rulesMap &&
            rulesMap.TryGetValue(key, out custom) &&
            custom is not null;
        IReadOnlyList<AutoEqBiquadDto> bands;
        bool? crossOverride;
        if (isCustom && custom is not null)
        {
            bands = custom.Filters;
            crossOverride = custom.CrossfeedOverride;
            SelectedAutoEqRuleStatusText = "已自定义（Layer B 跨端同步）";
        }
        else
        {
            var cat = TonewiseCoreClient.GetDefaultAutoEqCatalog()
                .FirstOrDefault(e => string.Equals(e.SlotKey, key, StringComparison.OrdinalIgnoreCase));
            bands = cat?.DefaultFilters ?? [];
            crossOverride = null;
            SelectedAutoEqRuleStatusText = cat is not null
                ? $"默认规则 ({cat.Label})"
                : "默认规则";
        }

        double Gain(int idx) => idx < bands.Count ? Math.Round(bands[idx].GainDb, 1) : 0.0;
        bool prevRuleSup = _suppressAutoEqRuleSync;
        _suppressAutoEqRuleSync = true;
        try
        {
            AutoEqRuleBand0Db = Gain(0);
            AutoEqRuleBand1Db = Gain(1);
            AutoEqRuleBand2Db = Gain(2);
            AutoEqRuleBand3Db = Gain(3);
            AutoEqRuleBand4Db = Gain(4);
            AutoEqRuleBand5Db = Gain(5);
            AutoEqRuleBand6Db = Gain(6);
            AutoEqRuleBand7Db = Gain(7);
            AutoEqRuleBand8Db = Gain(8);
            AutoEqRuleBand9Db = Gain(9);
            AutoEqRuleCrossfeedMode = crossOverride switch
            {
                true => "on",
                false => "off",
                _ => "auto",
            };
        }
        finally
        {
            _suppressAutoEqRuleSync = prevRuleSup;
        }
    }

    private void OnAutoEqRuleSliderEdited()
    {
        if (_suppressAutoEqRuleSync) return;
        SaveSelectedAutoEqRule();
    }

    [RelayCommand]
    private void SaveSelectedAutoEqRule()
    {
        string key = CurrentRuleSlotKey;
        var cat = TonewiseCoreClient.GetDefaultAutoEqCatalog()
            .FirstOrDefault(e => string.Equals(e.SlotKey, key, StringComparison.OrdinalIgnoreCase));
        var template = cat?.DefaultFilters ?? [];
        double[] gains =
        [
            AutoEqRuleBand0Db, AutoEqRuleBand1Db, AutoEqRuleBand2Db, AutoEqRuleBand3Db, AutoEqRuleBand4Db,
            AutoEqRuleBand5Db, AutoEqRuleBand6Db, AutoEqRuleBand7Db, AutoEqRuleBand8Db, AutoEqRuleBand9Db,
        ];
        var filters = new List<AutoEqBiquadDto>(10);
        for (int i = 0; i < 10; i++)
        {
            var t = i < template.Count
                ? template[i]
                : new AutoEqBiquadDto("Peaking", (float)DesktopDspChainConfig.StandardTenBandFrequencies[i], 0f, 1.41f);
            filters.Add(t with { GainDb = (float)Math.Clamp(gains[i], -12.0, 12.0) });
        }
        bool? crossOverride = AutoEqRuleCrossfeedMode switch
        {
            "on" => true,
            "off" => false,
            _ => null,
        };
        var rules = new Dictionary<string, AutoEqRuleOverrideDto>(_autoEqPolicy.CustomRules ?? [], StringComparer.OrdinalIgnoreCase)
        {
            [key] = new AutoEqRuleOverrideDto(0f, filters, crossOverride),
        };
        _autoEqPolicy = _autoEqPolicy with
        {
            UpdatedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CustomRules = rules,
        };
        SelectedAutoEqRuleStatusText = "已自定义（Layer B 跨端同步）";
        PersistAutoEqPolicyToWorkspaceAndSettings();
        RefreshAutoEqAndPush();
    }

    [RelayCommand]
    private void ResetSelectedAutoEqRule()
    {
        string key = CurrentRuleSlotKey;
        var rules = new Dictionary<string, AutoEqRuleOverrideDto>(_autoEqPolicy.CustomRules ?? [], StringComparer.OrdinalIgnoreCase);
        if (rules.Remove(key))
        {
            _autoEqPolicy = _autoEqPolicy with
            {
                UpdatedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CustomRules = rules,
            };
            PersistAutoEqPolicyToWorkspaceAndSettings();
            RefreshAutoEqAndPush();
        }
        LoadSelectedAutoEqRuleSlots();
        Notify($"已恢复「{key}」为默认规则", "info");
    }

    public void ImportApoToSelectedAutoEqRule(string filePath)
    {
        string text = File.ReadAllText(filePath);
        var parsed = EqualizerApoProfile.Parse(text);
        string key = CurrentRuleSlotKey;
        var filters = parsed.Filters
            .Select(f => new AutoEqBiquadDto(f.Kind.ToString(), (float)f.FrequencyHz, (float)f.GainDb, (float)f.Q))
            .ToList();
        bool? crossOverride = AutoEqRuleCrossfeedMode switch
        {
            "on" => true,
            "off" => false,
            _ => null,
        };
        var rules = new Dictionary<string, AutoEqRuleOverrideDto>(_autoEqPolicy.CustomRules ?? [], StringComparer.OrdinalIgnoreCase)
        {
            [key] = new AutoEqRuleOverrideDto((float)parsed.PreampDb, filters, crossOverride),
        };
        _autoEqPolicy = _autoEqPolicy with
        {
            UpdatedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CustomRules = rules,
        };
        LoadSelectedAutoEqRuleSlots();
        PersistAutoEqPolicyToWorkspaceAndSettings();
        RefreshAutoEqAndPush();
        Notify($"已导入 EqualizerAPO 至规则「{key}」（{filters.Count} 段）", "success");
    }

}
