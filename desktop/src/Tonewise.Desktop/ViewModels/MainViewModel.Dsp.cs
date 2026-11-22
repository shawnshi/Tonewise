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
    public void RefreshEndpoints(bool handleLostSelection = false)
    {
        string? previousId = SelectedEndpoint?.Id;
        var endpoints = AudioDeviceRouter.EnumerateRenderEndpoints();
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
        _engine.SetExtendedDsp(extDsp);
        var activeProfile = _usingExternalEqFile
            ? _eqProfile
            : (extDsp.Enabled ? extDsp.ToEqualizerApoProfile() : null);
        _appliedEngineEndpointId = SelectedEndpoint?.Id;
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

}
