using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tonewise.Audio;
using Tonewise.Desktop.Models;
using Tonewise.Desktop.Services;
using Tonewise.Interop;
using Tonewise.SyncHost;

namespace Tonewise.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibraryStore _store = new();
    private readonly TonewiseAudioEngine _engine = new();
    private SyncHostServer? _syncHost;
    private List<TrackItem> _allTracks = [];
    private EqualizerApoProfile? _eqProfile;

    public ObservableCollection<TrackItem> FilteredTracks { get; } = [];
    public ObservableCollection<AudioOutputEndpoint> OutputEndpoints { get; } = [];

    [ObservableProperty]
    private string _statusMessage = "Load a music folder containing .tonewise/manifest.db";

    [ObservableProperty]
    private TrackItem? _currentTrack;

    [ObservableProperty]
    private bool _isPlaying;

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
    private string _filterMoodCluster = "all";

    [ObservableProperty]
    private string _signalPathSummary = "Idle — WASAPI Shared/Exclusive standby";

    [ObservableProperty]
    private string _activeEqSummary = "EQ: Flat (0 filters)";

    [ObservableProperty]
    private float[]? _currentWaveform;

    [ObservableProperty]
    private double _playbackProgress;

    [ObservableProperty]
    private string _timeDisplay = "0:00 / 0:00";

    [ObservableProperty]
    private string _lanSyncStatus = "LAN Sync Host: Off";

    public event Action? WaveformUpdated;

    public MainViewModel()
    {
        _engine.SignalPathChanged += state =>
        {
            SignalPathSummary =
                $"{state.OutputDeviceName}\n{state.ShareModeLabel}\n"
                + $"{state.SampleRateHz / 1000.0:0.#} kHz · {state.BitsPerSample}-bit · "
                + (state.DspBypassed
                    ? "DSP Bypassed (Bit-Perfect)"
                    : $"64-bit DSP ({state.BiquadCount} filters, Preamp {state.EffectivePreampDb:+0.0;-0.0;0.0} dB)");
        };
        RefreshEndpoints();
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
        StatusMessage = $"Loaded {_allTracks.Count} track(s) from {musicRoot}";
    }

    public void LoadEqualizerApoFile(string filePath)
    {
        string text = File.ReadAllText(filePath);
        _eqProfile = EqualizerApoProfile.Parse(text);
        ActiveEqSummary =
            $"EQ: {Path.GetFileName(filePath)} ({_eqProfile.Filters.Count} filters, Preamp {_eqProfile.PreampDb:+0.0;-0.0;0.0} dB)";
        PushEnginePreferences();
    }

    partial void OnFilterSceneChanged(string value) => ApplyFiveDimFilter();
    partial void OnFilterEnergyChanged(string value) => ApplyFiveDimFilter();
    partial void OnFilterVocalsChanged(string value) => ApplyFiveDimFilter();
    partial void OnFilterMoodClusterChanged(string value) => ApplyFiveDimFilter();
    partial void OnPreferWasapiExclusiveChanged(bool value) => PushEnginePreferences();
    partial void OnSelectedEndpointChanged(AudioOutputEndpoint? value) => PushEnginePreferences();

    public void ApplyFiveDimFilter()
    {
        FilteredTracks.Clear();
        if (_allTracks.Count == 0) return;

        var predicates = new List<object>();
        if (FilterScene != "all")
        {
            predicates.Add(new { predicate = new { field = "dj_scene", op = "in", values = new[] { FilterScene } } });
        }
        if (FilterEnergy != "all")
        {
            predicates.Add(new { predicate = new { field = "dj_energy", op = "eq", values = new[] { FilterEnergy } } });
        }
        if (FilterVocals != "all")
        {
            predicates.Add(new { predicate = new { field = "dj_vocals", op = "eq", values = new[] { FilterVocals } } });
        }
        if (FilterMoodCluster != "all")
        {
            predicates.Add(new { predicate = new { field = "mood_cluster", op = "in", values = new[] { FilterMoodCluster } } });
        }

        if (predicates.Count == 0)
        {
            foreach (var t in _allTracks) FilteredTracks.Add(t);
            return;
        }

        // Delegate 5D AST evaluation to shared Rust tonewise-core via P/Invoke
        var nowDaysBase = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

        string sceneJson = JsonSerializer.Serialize(new
        {
            id = "00000000-0000-4000-8000-000000000001",
            title = "Desktop Matrix Filter",
            updated_at_ms = 0,
            sort_arc = "flat",
            tempo_null_passthrough = true,
            rule = new { all = predicates },
        });
        string tracksJson = JsonSerializer.Serialize(tracksPayload);

        var matchedUids = TonewiseCoreClient.EvaluateScene(sceneJson, tracksJson).ToHashSet();
        foreach (var t in _allTracks)
        {
            if (matchedUids.Contains(t.TrackUid))
            {
                FilteredTracks.Add(t);
            }
        }
    }

    public void PlayTrack(TrackItem track)
    {
        CurrentTrack = track;
        CurrentWaveform = _store.LoadWaveform(track);
        WaveformUpdated?.Invoke();

        PushEnginePreferences();
        if (File.Exists(track.FullPath))
        {
            _engine.PlayFile(track.FullPath);
            IsPlaying = true;
            StatusMessage = $"Playing: {track.Title}";
        }
        else
        {
            StatusMessage = $"File not found: {track.FullPath}";
        }
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        _engine.TogglePlayPause();
        IsPlaying = _engine.IsPlaying;
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

    private void PushEnginePreferences()
    {
        _engine.SetPreferences(
            PreferWasapiExclusive,
            SelectedEndpoint?.Id,
            _eqProfile,
            CurrentTrack?.ReplayGainTrackDb ?? 0.0
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
