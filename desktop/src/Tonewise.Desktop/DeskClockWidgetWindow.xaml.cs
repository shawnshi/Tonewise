using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Tonewise.Desktop.Models;
using Tonewise.Desktop.Motion;
using Tonewise.Desktop.Rendering;
using Tonewise.Desktop.ViewModels;
using Tonewise.SyncHost.Workbench;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Tonewise.Desktop;

public sealed partial class DeskClockWidgetWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly DispatcherTimer _waveTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _burnInTimer;
    private int _burnInTick;
    private bool _isCompactOverlay;
    private bool _isFullscreenWallClock;
    private bool _alwaysOnTop;
    private bool _keepAwakeActive;
    private bool _sleepExpiredAllowSleep;
    private bool _isWindowVisibleAndNotMinimized = true;
    private DateTime? _pausedSinceUtc;
    private DateTime _lastInteractionUtc = DateTime.UtcNow;
    private long _lastRenderedSecond = -1;
    private string? _loadedCoverPath;
    private readonly VinylTurntableRenderer _oledVinylRenderer = new();
    private bool _isOledVinylRenderingSubscribed;
    private long _lastOledVinylTickTimestamp;
    private (byte R, byte G, byte B)? _oledCoverSeedRgb;
    private CancellationTokenSource? _oledSeedCts;
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibilitySettings = new();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS = 0x80000000u;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002u;

    private static readonly SKColor ZuneMagenta = new(0xE5, 0x00, 0x6B);
    private static readonly SKColor DimTrack = new(0x7A, 0x7A, 0x7A, 0x70);
    private static readonly SKPaint ZuneMagentaPaint = new() { Color = ZuneMagenta, IsAntialias = false };
    private static readonly SKPaint DimTrackPaint = new() { Color = DimTrack, IsAntialias = false };
    private static readonly SolidColorBrush ActiveAccentBrush = new(Color.FromArgb(0xFF, 0xE5, 0x00, 0x6B));
    private static readonly SolidColorBrush MutedInkBrush = new(Color.FromArgb(0xFF, 0xA3, 0xA3, 0xA3));
    private static readonly SolidColorBrush LovedHeartBrush = new(Color.FromArgb(0xFF, 0xE5, 0x00, 0x6B));

    public DeskClockWidgetWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WidgetTitleDragBar);

        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tonewise.ico");
        if (File.Exists(icoPath)) AppWindow.SetIcon(icoPath);

        var placement = ViewModel.GetDeskClockWindowPlacement();
        _alwaysOnTop = placement.AlwaysOnTop;
        ApplyOverlappedCompanionChrome(restorePlacement: true);
        UpdateKeepAwakeState();
        SyncTransportControlAppearance();
        SyncTrackPresentation(ViewModel.CurrentTrack);

        ViewModel.TrackChanged += OnViewModelTrackChanged;
        ViewModel.WaveformUpdated += OnViewModelWaveformUpdated;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.SleepTimerExpired += OnSleepTimerExpired;
        SizeChanged += OnWindowSizeChanged;

        UpdateClockText(force: true);
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClockText(force: false);
            UpdateKeepAwakeState();
            UpdateFullscreenChromeAutoHide();
        };

        _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _waveTimer.Tick += (_, _) =>
        {
            if (ViewModel.IsPlaying)
            {
                WidgetWaveformCanvas.Invalidate();
            }
        };

        _burnInTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _burnInTimer.Tick += (_, _) => ApplyOledBurnInShift(++_burnInTick);

        AppWindow.Changed += OnAppWindowChanged;
        UpdateTimerActivity();

        Closed += (_, _) =>
        {
            SaveWindowPlacementIfNormal();
            AppWindow.Changed -= OnAppWindowChanged;
            _clockTimer.Stop();
            _waveTimer.Stop();
            _burnInTimer.Stop();
            if (_isOledVinylRenderingSubscribed)
            {
                CompositionTarget.Rendering -= OnOledVinylCompositionRendering;
                _isOledVinylRenderingSubscribed = false;
            }
            _oledSeedCts?.Cancel();
            _oledSeedCts?.Dispose();
            _oledSeedCts = null;
            _oledVinylRenderer.Dispose();
            SizeChanged -= OnWindowSizeChanged;
            ViewModel.TrackChanged -= OnViewModelTrackChanged;
            ViewModel.WaveformUpdated -= OnViewModelWaveformUpdated;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.SleepTimerExpired -= OnSleepTimerExpired;
            if (_keepAwakeActive)
            {
                try { _ = SetThreadExecutionState(ES_CONTINUOUS); } catch { }
                _keepAwakeActive = false;
            }
            Bindings.StopTracking();
        };
    }

    private void OnViewModelTrackChanged(TrackItem? track)
    {
        SyncTrackPresentation(track);
        WidgetWaveformCanvas.Invalidate();
    }

    private void OnViewModelWaveformUpdated() => WidgetWaveformCanvas.Invalidate();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsPlaying)
            or nameof(MainViewModel.IsShuffleEnabled)
            or nameof(MainViewModel.RepeatMode)
            or nameof(MainViewModel.CurrentTrackLoved)
            or nameof(MainViewModel.NowPlayingStageMode)
            or nameof(MainViewModel.VinylFinishStyle))
        {
            SyncTransportControlAppearance();
            SyncOledVinylStageVisibility();
            if (e.PropertyName is nameof(MainViewModel.IsPlaying)
                or nameof(MainViewModel.NowPlayingStageMode)
                or nameof(MainViewModel.VinylFinishStyle))
            {
                UpdateTimerActivity();
                OledVinylCanvas.Invalidate();
            }
        }
        else if (e.PropertyName is nameof(MainViewModel.CurrentTrack)
            or nameof(MainViewModel.ActiveFilterPillsSummary))
        {
            SyncTrackPresentation(ViewModel.CurrentTrack);
        }
    }

    private void SyncTransportControlAppearance()
    {
        PlayPauseButton.Content = ViewModel.IsPlaying ? "⏸" : "▶";
        ShuffleButton.Foreground = ViewModel.IsShuffleEnabled ? ActiveAccentBrush : MutedInkBrush;
        RepeatButton.Content = ViewModel.RepeatMode == "one" ? "↺" : "↻";
        RepeatButton.Foreground = ViewModel.RepeatMode != "off" ? ActiveAccentBrush : MutedInkBrush;
        LoveButton.Content = ViewModel.CurrentTrackLoved ? "♥" : "♡";
        LoveButton.Foreground = ViewModel.CurrentTrackLoved ? LovedHeartBrush : MutedInkBrush;
        PinToggleButton.Foreground = _alwaysOnTop ? ActiveAccentBrush : MutedInkBrush;
        PinToggleButton.Content = _alwaysOnTop ? "置顶 ✓" : "置顶";
        VinylToggleButton.Foreground = ViewModel.IsNowPlayingVinylMode ? ActiveAccentBrush : MutedInkBrush;
        VinylToggleButton.Content = ViewModel.IsNowPlayingVinylMode ? "黑胶 ✓" : "黑胶";
    }

    private void SyncTrackPresentation(TrackItem? track)
    {
        if (track is null)
        {
            EyebrowTextBlock.Text = string.IsNullOrWhiteSpace(ViewModel.ActiveFilterPillsSummary)
                ? "TONEWISE CHRONOMETER"
                : ViewModel.ActiveFilterPillsSummary;
            TitleTextBlock.Text = "Tonewise OLED 挂钟";
            SubtitleTextBlock.Text = "F11 全屏壁挂 · 拖动顶部栏跨屏移动";
            UpdateCoverArt(null);
            return;
        }

        if (track.IsClassical)
        {
            EyebrowTextBlock.Text = !string.IsNullOrWhiteSpace(track.ComposersDisplay)
                ? track.ComposersDisplay.ToUpperInvariant()
                : track.Album;

            string mvt = !string.IsNullOrWhiteSpace(track.MovementName) ? track.MovementName : track.Title;
            if (track.MovementNumber is > 0 && !mvt.StartsWith($"{track.MovementNumber}.", StringComparison.Ordinal))
            {
                mvt = $"{track.MovementNumber}. {mvt}";
            }
            TitleTextBlock.Text = !string.IsNullOrWhiteSpace(track.WorkName) &&
                !string.Equals(track.WorkName, mvt, StringComparison.OrdinalIgnoreCase)
                ? $"{track.WorkName} · {mvt}"
                : mvt;

            bool hasConductor = !string.IsNullOrWhiteSpace(track.ConductorsDisplay);
            bool hasArtists = !string.IsNullOrWhiteSpace(track.ArtistsDisplay);
            bool alreadyIncluded = hasConductor && hasArtists &&
                track.ArtistsDisplay.Contains(track.ConductorsDisplay!, StringComparison.OrdinalIgnoreCase);
            SubtitleTextBlock.Text = hasConductor && !alreadyIncluded && hasArtists
                ? $"{track.ConductorsDisplay} · {track.ArtistsDisplay}"
                : (hasArtists ? track.ArtistsDisplay : track.ConductorsDisplay ?? track.Album);
        }
        else
        {
            EyebrowTextBlock.Text = !string.IsNullOrWhiteSpace(ViewModel.ActiveFilterPillsSummary)
                ? ViewModel.ActiveFilterPillsSummary
                : track.Album;
            TitleTextBlock.Text = track.Title;
            SubtitleTextBlock.Text = track.ArtistsDisplay;
        }

        UpdateCoverArt(track.CoverArtPath);
    }

    private void UpdateCoverArt(string? coverPath)
    {
        if (string.Equals(_loadedCoverPath, coverPath, StringComparison.Ordinal)) return;
        _loadedCoverPath = coverPath;

        _oledVinylRenderer.UpdateCoverAsync(coverPath, () =>
            DispatcherQueue?.TryEnqueue(() => OledVinylCanvas?.Invalidate()));
        _ = UpdateOledCoverSeedAsync(coverPath);

        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
        {
            CoverArtImage.Source = null;
            CoverArtBorder.Visibility = Visibility.Collapsed;
            SyncOledVinylStageVisibility();
            return;
        }

        try
        {
            CoverArtImage.Source = new BitmapImage(new Uri(coverPath));
            CoverArtBorder.Visibility = Visibility.Visible;
        }
        catch
        {
            CoverArtImage.Source = null;
            CoverArtBorder.Visibility = Visibility.Collapsed;
        }
        SyncOledVinylStageVisibility();
    }

    private async Task UpdateOledCoverSeedAsync(string? coverPath)
    {
        _oledSeedCts?.Cancel();
        _oledSeedCts?.Dispose();
        var cts = new CancellationTokenSource();
        _oledSeedCts = cts;
        var token = cts.Token;

        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
        {
            _oledCoverSeedRgb = null;
            OledVinylCanvas?.Invalidate();
            return;
        }

        try
        {
            var seed = await Task.Run(() =>
                MainWindow.ExtractCoverSeedAndClampedAccent(coverPath, isDark: true).RawSeed, token);

            if (!token.IsCancellationRequested && ReferenceEquals(_oledSeedCts, cts))
            {
                _oledCoverSeedRgb = seed;
                OledVinylCanvas?.Invalidate();
            }
        }
        catch
        {
            // Keep neutral fallback
        }
    }

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        double w = args.Size.Width;
        double h = args.Size.Height;
        if (w <= 0 || h <= 0) return;

        double clockPt = _isFullscreenWallClock
            ? Math.Clamp(Math.Min(h * 0.22, w * 0.12), 72.0, 200.0)
            : Math.Clamp(Math.Min(h * 0.19, w * 0.085), 28.0, 72.0);
        ClockTextBlock.FontSize = clockPt;
        SecondsTextBlock.FontSize = Math.Max(11.0, clockPt * 0.34);
        DateTextBlock.FontSize = _isFullscreenWallClock ? 14.0 : 10.0;

        double coverSize = _isFullscreenWallClock
            ? Math.Clamp(h * 0.16, 72.0, 140.0)
            : Math.Clamp(h * 0.26, 48.0, 84.0);
        CoverArtBorder.Width = coverSize;
        CoverArtBorder.Height = coverSize;

        double vinylSize = Math.Clamp(Math.Min(h * 0.44, w * 0.28), 200.0, 380.0);
        OledVinylCanvas.Width = vinylSize;
        OledVinylCanvas.Height = vinylSize;
        SyncOledVinylStageVisibility();

        TitleTextBlock.FontSize = _isFullscreenWallClock ? 22.0 : 14.0;
        SubtitleTextBlock.FontSize = _isFullscreenWallClock ? 15.0 : 11.0;
        EyebrowTextBlock.FontSize = _isFullscreenWallClock ? 13.0 : 10.0;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        bool minimized = sender.Presenter is OverlappedPresenter ov && ov.State == OverlappedPresenterState.Minimized;
        bool visible = sender.IsVisible && !minimized;
        if (visible != _isWindowVisibleAndNotMinimized)
        {
            _isWindowVisibleAndNotMinimized = visible;
            UpdateTimerActivity();
        }
    }

    private void UpdateTimerActivity()
    {
        if (!_isWindowVisibleAndNotMinimized)
        {
            _clockTimer.Stop();
            _waveTimer.Stop();
            _burnInTimer.Stop();
            if (_isOledVinylRenderingSubscribed)
            {
                CompositionTarget.Rendering -= OnOledVinylCompositionRendering;
                _isOledVinylRenderingSubscribed = false;
            }
            if (_keepAwakeActive)
            {
                try { _ = SetThreadExecutionState(ES_CONTINUOUS); } catch { }
                _keepAwakeActive = false;
            }
            return;
        }

        if (!_clockTimer.IsEnabled)
        {
            UpdateClockText(force: true);
            _clockTimer.Start();
        }
        if (!_burnInTimer.IsEnabled)
        {
            _burnInTimer.Start();
        }
        if (ViewModel.IsPlaying)
        {
            if (!_waveTimer.IsEnabled)
            {
                _waveTimer.Start();
            }
        }
        else if (_waveTimer.IsEnabled)
        {
            _waveTimer.Stop();
        }
        SyncOledVinylRenderLoop();
        UpdateKeepAwakeState();
    }

    private bool IsOledVinylActive =>
        _isFullscreenWallClock && ViewModel.IsNowPlayingVinylMode && !_accessibilitySettings.HighContrast;

    private void SyncOledVinylStageVisibility()
    {
        if (OledVinylCanvas is null || CoverArtBorder is null) return;
        bool showVinyl = IsOledVinylActive;
        OledVinylCanvas.Visibility = showVinyl ? Visibility.Visible : Visibility.Collapsed;
        if (showVinyl)
        {
            CoverArtBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            bool hasCover = !string.IsNullOrWhiteSpace(_loadedCoverPath) && CoverArtImage.Source is not null;
            CoverArtBorder.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SyncOledVinylRenderLoop()
    {
        bool motionEnabled = ZuneMotionHelper.IsMotionEnabled();
        bool shouldRun = _isWindowVisibleAndNotMinimized
            && IsOledVinylActive
            && _oledVinylRenderer.NeedsAnimation(ViewModel.IsPlaying, motionEnabled);

        if (shouldRun)
        {
            if (!_isOledVinylRenderingSubscribed)
            {
                _lastOledVinylTickTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnOledVinylCompositionRendering;
                _isOledVinylRenderingSubscribed = true;
            }
        }
        else if (_isOledVinylRenderingSubscribed)
        {
            CompositionTarget.Rendering -= OnOledVinylCompositionRendering;
            _isOledVinylRenderingSubscribed = false;
        }
    }

    private void OnOledVinylCompositionRendering(object? sender, object e)
    {
        bool motionEnabled = ZuneMotionHelper.IsMotionEnabled();
        if (!_isWindowVisibleAndNotMinimized || !IsOledVinylActive)
        {
            SyncOledVinylRenderLoop();
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long prev = _lastOledVinylTickTimestamp;
        _lastOledVinylTickTimestamp = now;
        double dt = prev > 0 ? (double)(now - prev) / System.Diagnostics.Stopwatch.Frequency : 0.016;
        bool keepGoing = _oledVinylRenderer.AdvanceFrame(dt, ViewModel.IsPlaying, motionEnabled);
        OledVinylCanvas?.Invalidate();
        if (!keepGoing)
        {
            SyncOledVinylRenderLoop();
        }
    }

    private void UpdateKeepAwakeState()
    {
        if (!_isWindowVisibleAndNotMinimized)
        {
            if (_keepAwakeActive)
            {
                try { _ = SetThreadExecutionState(ES_CONTINUOUS); } catch { }
                _keepAwakeActive = false;
            }
            return;
        }
        if (ViewModel.IsPlaying)
        {
            _sleepExpiredAllowSleep = false;
        }
        if (ViewModel.IsPlaying || (_isFullscreenWallClock && !_sleepExpiredAllowSleep))
        {
            _pausedSinceUtc = null;
            if (!_keepAwakeActive)
            {
                try
                {
                    _ = SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED);
                    _keepAwakeActive = true;
                }
                catch { }
            }
        }
        else
        {
            _pausedSinceUtc ??= DateTime.UtcNow;
            if (_keepAwakeActive && (DateTime.UtcNow - _pausedSinceUtc.Value).TotalMinutes >= 10.0)
            {
                try { _ = SetThreadExecutionState(ES_CONTINUOUS); } catch { }
                _keepAwakeActive = false;
            }
        }
    }

    private void ApplyOverlappedCompanionChrome(bool restorePlacement = false)
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        double scale = 1.0;
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi >= 96u) scale = dpi / 96.0;
        }
        catch { }

        var saved = restorePlacement ? ViewModel.GetDeskClockWindowPlacement() : default;
        if (restorePlacement && saved.Width is > 320 && saved.Height is > 160)
        {
            int targetW = saved.Width.Value;
            int targetH = saved.Height.Value;
            int targetX = saved.X ?? 120;
            int targetY = saved.Y ?? 120;
            try
            {
                var displayArea = DisplayArea.GetFromRect(
                    new RectInt32(targetX, targetY, targetW, targetH),
                    DisplayAreaFallback.Primary);
                var workArea = displayArea.WorkArea;
                targetW = Math.Clamp(targetW, 320, Math.Max(320, workArea.Width));
                targetH = Math.Clamp(targetH, 160, Math.Max(160, workArea.Height));
                targetX = Math.Clamp(targetX, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - targetW));
                targetY = Math.Clamp(targetY, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - targetH));
            }
            catch { }

            AppWindow.ResizeClient(new SizeInt32(targetW, targetH));
            if (saved.X.HasValue && saved.Y.HasValue)
            {
                try { AppWindow.Move(new PointInt32(targetX, targetY)); } catch { }
            }
        }
        else
        {
            AppWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(560 * scale),
                (int)Math.Round(246 * scale)));
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsAlwaysOnTop = _alwaysOnTop;
            presenter.IsResizable = true;
        }
    }

    private void SaveWindowPlacementIfNormal()
    {
        if (_isCompactOverlay || _isFullscreenWallClock) return;
        try
        {
            var pos = AppWindow.Position;
            var size = AppWindow.ClientSize;
            if (size.Width > 200 && size.Height > 100)
            {
                ViewModel.SaveDeskClockWindowPlacement(pos.X, pos.Y, size.Width, size.Height, _alwaysOnTop);
            }
        }
        catch { }
    }

    public static (int Dx, int Dy) ComputeOledBurnInOffset(int tickCount) =>
        OledBurnInGuard.ComputeOrbitOffset(tickCount, amplitudeDip: 12);

    public void ApplyOledBurnInShift(int tickCount)
    {
        var (dx, dy) = ComputeOledBurnInOffset(tickCount);
        OledCanvasHost.Margin = new Thickness(14 + dx, 14 + dy, 14 - dx, 14 - dy);
    }

    private void UpdateClockText(bool force)
    {
        var now = DateTime.Now;
        long secondTick = now.Ticks / TimeSpan.TicksPerSecond;
        if (!force && secondTick == _lastRenderedSecond) return;
        _lastRenderedSecond = secondTick;

        bool is24Hour = !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("t", StringComparison.Ordinal);
        ClockTextBlock.Text = now.ToString(is24Hour ? "HH:mm" : "h:mm", CultureInfo.CurrentCulture);
        SecondsTextBlock.Text = now.ToString(is24Hour ? "ss" : "ss tt", CultureInfo.CurrentCulture);
        DateTextBlock.Text = now.ToString("ddd · MMM d", CultureInfo.CurrentCulture).ToUpperInvariant();
    }

    private void OnRootPointerActivity(object sender, PointerRoutedEventArgs e)
    {
        _sleepExpiredAllowSleep = false;
        RestoreFullscreenChrome();
    }

    private void OnSleepTimerExpired()
    {
        _sleepExpiredAllowSleep = true;
        if (_keepAwakeActive)
        {
            try { _ = SetThreadExecutionState(ES_CONTINUOUS); } catch { }
            _keepAwakeActive = false;
        }
        UpdateFullscreenChromeAutoHide();
    }

    private void RestoreFullscreenChrome()
    {
        _lastInteractionUtc = DateTime.UtcNow;
        if (TopChromeBar.Visibility != Visibility.Visible)
        {
            TopChromeBar.Visibility = Visibility.Visible;
            BottomTransportBar.Visibility = Visibility.Visible;
        }
    }

    private void UpdateFullscreenChromeAutoHide()
    {
        if (_isFullscreenWallClock && ViewModel.IsPlaying)
        {
            if ((DateTime.UtcNow - _lastInteractionUtc).TotalSeconds >= 10.0)
            {
                TopChromeBar.Visibility = Visibility.Collapsed;
                BottomTransportBar.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            RestoreFullscreenChrome();
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F11)
        {
            ToggleFullscreenMode();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && _isFullscreenWallClock)
        {
            ToggleFullscreenMode();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.V)
        {
            bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
            if (shift)
            {
                ViewModel.CycleVinylFinishStyleCommand.Execute(null);
            }
            else
            {
                ViewModel.ToggleNowPlayingStageModeCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void OnToggleVinylModeClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleNowPlayingStageModeCommand.Execute(null);
    }

    private void OnOledVinylTapped(object sender, TappedRoutedEventArgs e)
    {
        RestoreFullscreenChrome();
        ViewModel.TogglePlayPauseCommand.Execute(null);
        SyncOledVinylRenderLoop();
        e.Handled = true;
    }

    private void OnPaintOledVinyl(object? sender, SKPaintSurfaceEventArgs e)
    {
        float scale = 1f;
        try
        {
            if (OledRootGrid?.XamlRoot is { RasterizationScale: > 0 } xr)
            {
                scale = (float)xr.RasterizationScale;
            }
        }
        catch { }
        _oledVinylRenderer.Render(
            e.Surface.Canvas,
            e.Info.Width,
            e.Info.Height,
            scale,
            VinylTurntableRenderer.VinylLayoutMode.FullTurntable,
            ViewModel.ResolvedVinylStyle,
            _oledCoverSeedRgb,
            isDarkTheme: true,
            isHighContrast: _accessibilitySettings.HighContrast);
    }

    private void OnToggleAlwaysOnTopClick(object sender, RoutedEventArgs e)
    {
        _alwaysOnTop = !_alwaysOnTop;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _alwaysOnTop;
        }
        SyncTransportControlAppearance();
        SaveWindowPlacementIfNormal();
    }

    private void OnToggleFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreenMode();

    private void ToggleFullscreenMode()
    {
        if (!_isFullscreenWallClock)
        {
            SaveWindowPlacementIfNormal();
            _isCompactOverlay = false;
            PipToggleButton.Content = "PiP 画中画";
            _isFullscreenWallClock = true;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            FullscreenToggleButton.Content = "退出全屏";
            RestoreFullscreenChrome();
        }
        else
        {
            _isFullscreenWallClock = false;
            ApplyOverlappedCompanionChrome(restorePlacement: true);
            FullscreenToggleButton.Content = "全屏挂钟";
            RestoreFullscreenChrome();
        }
        SyncOledVinylStageVisibility();
        SyncOledVinylRenderLoop();
        UpdateKeepAwakeState();
    }

    private void OnTogglePipModeClick(object sender, RoutedEventArgs e)
    {
        _isCompactOverlay = !_isCompactOverlay;
        _isFullscreenWallClock = false;
        FullscreenToggleButton.Content = "全屏挂钟";
        if (_isCompactOverlay)
        {
            SaveWindowPlacementIfNormal();
            AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
            PipToggleButton.Content = "还原窗口";
        }
        else
        {
            ApplyOverlappedCompanionChrome(restorePlacement: true);
            PipToggleButton.Content = "PiP 画中画";
        }
        RestoreFullscreenChrome();
    }

    private void OnWaveformPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RestoreFullscreenChrome();
        double width = WidgetWaveformCanvas.ActualWidth;
        if (width <= 0) return;
        double x = e.GetCurrentPoint(WidgetWaveformCanvas).Position.X;
        ViewModel.SeekFraction(Math.Clamp(x / width, 0.0, 1.0));
        WidgetWaveformCanvas.Invalidate();
        e.Handled = true;
    }

    private void OnCloseWidgetClick(object sender, RoutedEventArgs e) => Close();

    private void OnPaintWaveform(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        int w = e.Info.Width;
        int h = e.Info.Height;
        if (w <= 0 || h <= 0) return;

        float[]? envelope = ViewModel.CurrentWaveform;
        int count = envelope?.Length ?? 150;
        float step = w / (float)count;
        float barW = Math.Clamp(step * 0.65f, 1.5f, 5f);
        float centerY = h / 2f;
        float progressX = (float)(ViewModel.PlaybackProgress * w);
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        SKPaint? hcActivePaint = highContrast ? new SKPaint { Color = SKColors.White, IsAntialias = false } : null;
        SKPaint? hcDimPaint = highContrast ? new SKPaint { Color = new SKColor(0xA0, 0xA0, 0xA0), IsAntialias = false } : null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                float amp = envelope is not null ? Math.Clamp(envelope[i], 0.06f, 1f) : 0.16f;
                float halfH = Math.Max(2f, amp * (h * 0.44f));
                float cx = (i + 0.5f) * step;
                var rect = new SKRect(cx - barW / 2f, centerY - halfH, cx + barW / 2f, centerY + halfH);
                canvas.DrawRect(rect, cx <= progressX ? (hcActivePaint ?? ZuneMagentaPaint) : (hcDimPaint ?? DimTrackPaint));
            }
        }
        finally
        {
            hcActivePaint?.Dispose();
            hcDimPaint?.Dispose();
        }
    }
}
