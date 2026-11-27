using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Tonewise.Desktop.Models;
using Tonewise.Desktop.Motion;
using Tonewise.Desktop.Rendering;
using Tonewise.Desktop.Services;
using Tonewise.Desktop.ViewModels;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;
using Tonewise.SyncHost.Playlists;
using Tonewise.SyncHost.Workbench;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;

namespace Tonewise.Desktop;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _searchDebounceTimer;
    private string _pendingSearchText = "";
    private Win32GlobalHotkeyService? _globalHotkeys;
    private DeskClockWidgetWindow? _oledWidget;
    private MediaPlayer? _smtcPlayer;
    private SystemMediaTransportControls? _smtc;

    private PdfDocument? _bookletDoc;
    private string? _loadedBookletPath;
    private int _bookletPageIndex;
    private CancellationTokenSource? _folderLoadCts;

    private readonly DispatcherTimer _notificationTimer;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    private async Task<ContentDialogResult> ShowDialogSafeAsync(ContentDialog dialog)
    {
        dialog.XamlRoot ??= RootWorkbench.XamlRoot;
        if (dialog.XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        await _dialogGate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ViewModel.Notify($"对话框显示失败: {ex.Message}", "warning");
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogGate.Release();
        }
    }
    private bool _isSeekDragging;
    private bool _updatingSeekSlider;
    private double _lastWaveformProgress = -1.0;

    private readonly ZuneMosaicRenderer _mosaicRenderer = new();
    private readonly DispatcherTimer _mosaicAnimTimer;
    private long _lastMosaicTickTimestamp;
    private readonly DispatcherTimer _nowPlayingIdleTimer;
    private DateTime _lastNowPlayingActivityUtc = DateTime.UtcNow;
    private bool _isNowPlayingChromeFaded;
    private bool _startupInitialized;
    private bool _isWindowVisibleAndNotMinimized = true;
    private bool _mirrorPruneConfirmArmed;
    private bool _suppressMirrorPruneConfirm;
    private Action? _pendingUndoAction;
    private bool _settingUndoNotification;
    private CancellationTokenSource? _mosaicPrewarmCts;
    private CancellationTokenSource? _dynamicAccentCts;
    private (byte R, byte G, byte B) _currentAccentRgb = ZuneColorMath.DefaultDarkAccentRgb;

    private static readonly SKColor InactiveBarColor = new(0x7A, 0x7A, 0x7A, 0x60);
    private readonly SKPaint _activeBarPaint = new() { Color = new SKColor(0xE5, 0x00, 0x6B), IsAntialias = false };
    private static readonly SKPaint InactiveBarPaint = new() { Color = InactiveBarColor, IsAntialias = false };

    public Visibility VisibleWhenBoth(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VisibleWhenFirstAndNotSecond(bool a, bool b) => a && !b ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NegateToVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public double PivotInkOpacity(bool isActive) => isActive ? 1.0 : 0.62;

    public double PlaylistsDeckMaxHeight(bool expanded) => expanded ? 420 : 152;

    public string PlaylistsExpandToggleLabel(bool expanded) => expanded ? "收起歌单" : "展开全部";

    public string PlaylistsExpandToggleGlyph(bool expanded) => expanded ? "\uE70E" : "\uE70D";

    private async Task RunSafeUiActionAsync(Func<Task> action, string operationName)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(operationName, ex);
            ViewModel.Notify($"{operationName}失败：{ex.Message}", "error");
        }
    }

    public InfoBarSeverity SeverityFromLevel(string level) => level switch
    {
        "success" => InfoBarSeverity.Success,
        "warning" => InfoBarSeverity.Warning,
        "error" => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public string PlayPauseGlyph(bool isPlaying) => isPlaying ? "\uE769" : "\uE768";

    public string PlayPauseAutomationName(bool isPlaying) => isPlaying ? "暂停" : "播放";

    public string VolumeGlyph(bool isMuted, double percent) =>
        isMuted || percent <= 0 ? "\uE74F" : percent < 34 ? "\uE993" : percent < 67 ? "\uE994" : "\uE995";

    public string RepeatGlyph(string mode) => mode switch
    {
        "one" => "\uE8ED",
        _ => "\uE8EE",
    };

    public double ShuffleOpacity(bool isEnabled) => isEnabled ? 1.0 : 0.45;

    public double RepeatOpacity(string mode) =>
        string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase) ? 0.45 : 1.0;

    public Visibility IsRepeatIndicatorVisible(string mode) =>
        string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NonEmptyVisibility(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public string FormatDb(double db) => $"{db:+0.0;-0.0;0.0}";

    public string HeartGlyph(bool isLoved) => isLoved ? "\uEB52" : "\uEB51";
    public string HeartAutomationName(bool isLoved) => isLoved ? "取消喜爱当前曲目" : "喜爱当前曲目";

    public SolidColorBrush HeartForeground(bool isLoved)
    {
        if (isLoved) return new SolidColorBrush(Color.FromArgb(0xFF, _currentAccentRgb.R, _currentAccentRgb.G, _currentAccentRgb.B));
        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        return new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0x52, 0x52, 0x52) : Color.FromArgb(0xFF, 0xA3, 0xA3, 0xA3));
    }

    public SolidColorBrush FreezePillBackground(bool active)
    {
        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        if (active) return new SolidColorBrush(Color.FromArgb(0x28, _currentAccentRgb.R, _currentAccentRgb.G, _currentAccentRgb.B));
        return new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF4) : Color.FromArgb(0xFF, 0x0D, 0x0D, 0x0D));
    }

    public SolidColorBrush FreezePillBorder(bool active)
    {
        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        if (active) return new SolidColorBrush(Color.FromArgb(0xFF, _currentAccentRgb.R, _currentAccentRgb.G, _currentAccentRgb.B));
        return new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC) : Color.FromArgb(0xFF, 0x26, 0x26, 0x26));
    }

    public MainWindow()
    {
        ViewModel.PreloadInitialAppTheme(App.InitialAppTheme);
        InitializeComponent();

        TrySetObsidianBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarDragRegion);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlapped)
        {
            overlapped.PreferredMinimumWidth = 1024;
            overlapped.PreferredMinimumHeight = 700;
        }
        try
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch
        {
            // TitleBar customization fallback on older Windows hosts
        }

        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tonewise.ico");
        if (File.Exists(icoPath))
        {
            AppWindow.SetIcon(icoPath);
        }

        UpdateSignalPurityDotBrush();
        SyncSubSelectorBarItems();
        SyncInlineInspectorTargets([]);
        ApplyAppTheme();
        ApplyInspectorState();

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ViewModel.ApplySearchSuggestionOrQuery(_pendingSearchText);
        };

        ViewModel.SubTabsChanged += SyncSubSelectorBarItems;
        ViewModel.NavigationSettled += OnViewModelNavigationSettled;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SignalPurityColorHex))
            {
                UpdateSignalPurityDotBrush();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsPlaying))
            {
                SyncSmtcPlaybackState();
                if (!ViewModel.IsPlaying) RestoreNowPlayingChrome();
                UpdateMainWindowTimers();
            }
            else if (args.PropertyName == nameof(MainViewModel.CurrentTrack))
            {
                UpdateCoverArtImage(TransportCoverImage, ViewModel.CurrentTrack?.CoverArtPath);
                UpdateCoverArtImage(NowPlayingCoverImage, ViewModel.CurrentTrack?.CoverArtPath, decodeWidth: 360);
                _ = ApplyDynamicAccentFromCoverAsync(ViewModel.CurrentTrack?.CoverArtPath);
            }
            else if (args.PropertyName == nameof(MainViewModel.EnableDynamicCoverAccent))
            {
                _ = ApplyDynamicAccentFromCoverAsync(ViewModel.CurrentTrack?.CoverArtPath);
            }
            else if (args.PropertyName == nameof(MainViewModel.InspectedTrack))
            {
                UpdateCoverArtImage(InspectedCoverImage, ViewModel.InspectedTrack?.CoverArtPath);
                var selected =
                    ViewModel.IsNavClassicalWorks && ViewModel.IsClassicalComposerDetailOpen
                        ? ViewModel.GetSelectedClassicalTracks()
                        : TrackListView.SelectedItems.OfType<TrackItem>().ToList();
                if (selected.Count <= 1)
                {
                    SyncInlineInspectorTargets(ViewModel.InspectedTrack is null ? [] : [ViewModel.InspectedTrack]);
                }
            }
            else if (args.PropertyName == nameof(MainViewModel.LibraryNavMode))
            {
                SyncActiveInboxAuditButtons();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsMultiSelectMode))
            {
                ViewModel.SyncClassicalMultiSelectMode();
                if (TrackListView is not null)
                {
                    TrackListView.SelectionMode = ViewModel.IsMultiSelectMode
                        ? ListViewSelectionMode.Multiple
                        : ListViewSelectionMode.Extended;
                }
            }
            else if (args.PropertyName == nameof(MainViewModel.SelectedPlaylist) && PlaylistsListView is not null)
            {
                SyncPlaylistsListViewSelection();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsInspectorOpen))
            {
                ApplyInspectorState();
            }
            else if (args.PropertyName == nameof(MainViewModel.AppTheme))
            {
                ApplyAppTheme();
            }
            else if (args.PropertyName is nameof(MainViewModel.IsNotificationOpen) or nameof(MainViewModel.NotificationMessage))
            {
                if (!_settingUndoNotification)
                {
                    _pendingUndoAction = null;
                    if (GlobalNotificationUndoButton is not null)
                    {
                        GlobalNotificationUndoButton.Visibility = Visibility.Collapsed;
                    }
                }
                ScheduleNotificationAutoClose();
            }
        };
        ViewModel.WaveformUpdated += () =>
        {
            _lastWaveformProgress = -1.0;
            WaveformCanvas.Invalidate();
        };
        ViewModel.TracksUpdatingInPlace += BeginPreserveTrackListViewportAndSelection;
        ViewModel.TracksUpdatedInPlace += EndPreserveTrackListViewportAndSelection;
        TrackListView?.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTrackListPointerPressed), handledEventsToo: true);
        RootWorkbench.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnNowPlayingPointerActivity), handledEventsToo: true);
        RootWorkbench.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnNowPlayingPointerActivity), handledEventsToo: true);
        ViewModel.TrackChanged += async track =>
        {
            try
            {
                UpdateCoverArtImage(TransportCoverImage, ViewModel.CurrentTrack?.CoverArtPath);
                UpdateCoverArtImage(NowPlayingCoverImage, ViewModel.CurrentTrack?.CoverArtPath, decodeWidth: 360);
                UpdateCoverArtImage(InspectedCoverImage, ViewModel.InspectedTrack?.CoverArtPath);
                RefreshQueuePlayingHighlights();
                await ApplyDynamicAccentFromCoverAsync(ViewModel.CurrentTrack?.CoverArtPath);
                await UpdateSmtcMetadataAsync(ViewModel.CurrentTrack);
                await LoadBookletForTrackAsync(track);
                UpdateMainWindowTimers();
                if (ViewModel.IsNowPlayingViewVisible)
                {
                    NowPlayingMosaicCanvas?.Invalidate();
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("MainWindow.TrackChanged", ex);
            }
        };

        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            if (ViewModel.NotificationLevel is "success" or "info")
            {
                ViewModel.IsNotificationOpen = false;
            }
        };

        _mosaicAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(36) };
        _mosaicAnimTimer.Tick += (_, _) =>
        {
            if (ViewModel.IsNowPlayingViewVisible
                && ViewModel.IsPlaying
                && ZuneMotionHelper.IsMotionEnabled()
                && !_accessibilitySettings.HighContrast
                && _mosaicRenderer.HasCovers)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long prev = _lastMosaicTickTimestamp;
                _lastMosaicTickTimestamp = now;
                double dt = prev > 0
                    ? (double)(now - prev) / System.Diagnostics.Stopwatch.Frequency
                    : 0.036;
                if (_mosaicRenderer.AdvanceFrame(dt))
                {
                    NowPlayingMosaicCanvas?.Invalidate();
                }
            }
        };

        _nowPlayingIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nowPlayingIdleTimer.Tick += (_, _) => UpdateNowPlayingChromeAutoFade();

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _isSeekDragging = true), handledEventsToo: true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _isSeekDragging = false), handledEventsToo: true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => _isSeekDragging = false), handledEventsToo: true);

        ViewModel.LibraryLoaded += () =>
        {
            UpdateCoverArtImage(TransportCoverImage, ViewModel.CurrentTrack?.CoverArtPath);
            UpdateCoverArtImage(NowPlayingCoverImage, ViewModel.CurrentTrack?.CoverArtPath, decodeWidth: 360);
            UpdateCoverArtImage(InspectedCoverImage, ViewModel.InspectedTrack?.CoverArtPath);
            SyncInlineInspectorTargets(ViewModel.InspectedTrack is null ? [] : [ViewModel.InspectedTrack]);
            _ = ApplyDynamicAccentFromCoverAsync(ViewModel.CurrentTrack?.CoverArtPath);
            if (ViewModel.IsNowPlayingViewVisible)
            {
                RefreshNowPlayingMosaic();
            }
            else
            {
                _ = PrewarmNowPlayingMosaicAsync();
            }
        };

        Activated += (_, _) =>
        {
            if (_startupInitialized) return;
            _startupInitialized = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                InitializeSmtc();
                InitializeGlobalHotkeys();
                ViewModel.RestoreSavedSettingsAndLibrary();
                _mirrorPruneConfirmArmed = true;
                ApplyAppTheme();
                ApplyInspectorState();
            });
        };

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _progressTimer.Tick += (_, _) =>
        {
            ViewModel.RefreshProgress();
            SyncSeekSliderFromEngine();
            if (ViewModel.IsPlaying && ViewModel.CurrentWaveform is not null)
            {
                double progress = ViewModel.PlaybackProgress;
                if (Math.Abs(progress - _lastWaveformProgress) >= 0.001)
                {
                    _lastWaveformProgress = progress;
                    WaveformCanvas.Invalidate();
                }
            }
        };
        AppWindow.Changed += OnMainAppWindowChanged;
        UpdateMainWindowTimers();

        Closed += (_, _) =>
        {
            AppWindow.Changed -= OnMainAppWindowChanged;
            _folderLoadCts?.Cancel();
            _folderLoadCts?.Dispose();
            _folderLoadCts = null;
            _mosaicPrewarmCts?.Cancel();
            _mosaicPrewarmCts?.Dispose();
            _mosaicPrewarmCts = null;
            _dynamicAccentCts?.Cancel();
            _dynamicAccentCts?.Dispose();
            _dynamicAccentCts = null;
            _mosaicAnimTimer.Stop();
            _nowPlayingIdleTimer.Stop();
            _mosaicRenderer.Dispose();
            _progressTimer.Stop();
            _searchDebounceTimer.Stop();
            _notificationTimer.Stop();
            _globalHotkeys?.Dispose();
            _oledWidget?.Close();
            _smtcPlayer?.Dispose();
            ViewModel.Dispose();
        };
    }

    private void OnMainAppWindowChanged(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        bool minimized = (sender.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.State
            == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized;
        bool active = sender.IsVisible && !minimized;
        if (active == _isWindowVisibleAndNotMinimized) return;
        _isWindowVisibleAndNotMinimized = active;
        UpdateMainWindowTimers();
        if (active)
        {
            ViewModel.RefreshProgress();
            SyncSeekSliderFromEngine();
            WaveformCanvas?.Invalidate();
        }
    }

    private void UpdateMainWindowTimers()
    {
        bool activePlaying = _isWindowVisibleAndNotMinimized && ViewModel.IsPlaying;
        if (activePlaying)
        {
            if (!_progressTimer.IsEnabled) _progressTimer.Start();
        }
        else if (_progressTimer.IsEnabled)
        {
            _progressTimer.Stop();
        }

        bool activeNowPlaying = activePlaying && ViewModel.IsNowPlayingViewVisible;
        bool animateMosaic = activeNowPlaying && ZuneMotionHelper.IsMotionEnabled() && !_accessibilitySettings.HighContrast;
        if (animateMosaic)
        {
            if (!_mosaicAnimTimer.IsEnabled)
            {
                _lastMosaicTickTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                _mosaicAnimTimer.Start();
            }
        }
        else if (_mosaicAnimTimer.IsEnabled)
        {
            _mosaicAnimTimer.Stop();
        }

        if (activeNowPlaying)
        {
            if (!_nowPlayingIdleTimer.IsEnabled) _nowPlayingIdleTimer.Start();
        }
        else
        {
            if (_nowPlayingIdleTimer.IsEnabled) _nowPlayingIdleTimer.Stop();
        }
    }

    private void TrySetObsidianBackdrop()
    {
        // Zune "Content, Not Chrome": pure solid #000000 / #FFFFFF canvas without Mica/Acrylic blur
        SystemBackdrop = null;
    }

    private void InitializeGlobalHotkeys()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            _globalHotkeys = new Win32GlobalHotkeyService(hwnd);
            const uint mod = Win32GlobalHotkeyService.ModControl | Win32GlobalHotkeyService.ModAlt;
            // Ctrl+Alt+P -> Toggle Play/Pause; Ctrl+Alt+C -> Coding Scene; Ctrl+Alt+M -> Toggle OLED Widget
            _globalHotkeys.TryRegister(1, mod, 0x50, () => DispatcherQueue.TryEnqueue(() => ViewModel.TogglePlayPauseCommand.Execute(null)));
            _globalHotkeys.TryRegister(2, mod, 0x43, () => DispatcherQueue.TryEnqueue(() => ViewModel.ActivateCodingQuickSceneCommand.Execute(null)));
            _globalHotkeys.TryRegister(3, mod, 0x4D, () => DispatcherQueue.TryEnqueue(ToggleOledWidget));
        }
        catch
        {
            // Hotkey registration unavailable in restricted sessions
        }
    }


    // ---------------- Theme & Dynamic Cover Accent ----------------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private bool ResolveEffectiveIsDarkTheme() => ViewModel.AppTheme switch
    {
        "light" => false,
        "dark" => true,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    private void ApplyAppTheme()
    {
        bool isDark = ResolveEffectiveIsDarkTheme();
        RootWorkbench.RequestedTheme = ViewModel.AppTheme switch
        {
            "light" => ElementTheme.Light,
            "system" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                int useDark = isDark ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            var accentRgb = isDark ? ZuneColorMath.DefaultDarkAccentRgb : ZuneColorMath.DefaultLightAccentRgb;
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonForegroundColor = isDark
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            AppWindow.TitleBar.ButtonHoverBackgroundColor = isDark
                ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
            AppWindow.TitleBar.ButtonHoverForegroundColor = Color.FromArgb(0xFF, accentRgb.R, accentRgb.G, accentRgb.B);
        }
        catch
        {
            // Older hosts without DWM/TitleBar customization
        }
        AttachFlyoutThemeSync(LexiconTagMatrixFlyout);
        AttachFlyoutThemeSync(AcousticRackFlyout);
        AttachFlyoutThemeSync(PlaybackQueueFlyout);
        _ = ApplyDynamicAccentFromCoverAsync(ViewModel.CurrentTrack?.CoverArtPath);
        SyncSubSelectorBarItems();
        RefreshDynamicAccentControls();
        NowPlayingMosaicCanvas?.Invalidate();
    }

    private async Task ApplyDynamicAccentFromCoverAsync(string? coverArtPath)
    {
        var prev = _dynamicAccentCts;
        var cts = new CancellationTokenSource();
        _dynamicAccentCts = cts;
        CancellationToken token = cts.Token;
        if (prev is not null)
        {
            try { prev.Cancel(); } catch (ObjectDisposedException) { }
            prev.Dispose();
        }

        bool isDark = ResolveEffectiveIsDarkTheme();
        (byte R, byte G, byte B) targetRgb = isDark
            ? ZuneColorMath.DefaultDarkAccentRgb
            : ZuneColorMath.DefaultLightAccentRgb;

        if (ViewModel.EnableDynamicCoverAccent && !string.IsNullOrWhiteSpace(coverArtPath))
        {
            try
            {
                var sampled = await Task.Run(() => ExtractClampedCoverAccent(coverArtPath, isDark), token);
                if (token.IsCancellationRequested || !ReferenceEquals(_dynamicAccentCts, cts))
                {
                    return;
                }
                if (sampled.HasValue)
                {
                    targetRgb = sampled.Value;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                // Keep default Zune magenta fallback
            }
        }

        if (token.IsCancellationRequested || !ReferenceEquals(_dynamicAccentCts, cts)) return;
        ApplyResolvedAccentRgb(targetRgb, isDark);
    }

    private static (byte R, byte G, byte B)? ExtractClampedCoverAccent(string coverArtPath, bool isDark)
    {
        if (!File.Exists(coverArtPath)) return null;
        using var raw = SKBitmap.Decode(coverArtPath);
        if (raw is null || raw.Width <= 0 || raw.Height <= 0) return null;

        const int sampleDim = 24;
        using var small = new SKBitmap(sampleDim, sampleDim, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(small))
        {
            canvas.DrawBitmap(raw, new SKRect(0, 0, raw.Width, raw.Height), new SKRect(0, 0, sampleDim, sampleDim));
        }

        var pixels = new (byte R, byte G, byte B)[sampleDim * sampleDim];
        int k = 0;
        for (int y = 0; y < sampleDim; y++)
        {
            for (int x = 0; x < sampleDim; x++)
            {
                var px = small.GetPixel(x, y);
                pixels[k++] = (px.Red, px.Green, px.Blue);
            }
        }

        var dominant = ZuneColorMath.SelectDominantVibrantRgb(pixels);
        return dominant is { } rgb
            ? ZuneColorMath.ClampAccentForTheme(rgb.R, rgb.G, rgb.B, isDark)
            : null;
    }

    private void ApplyResolvedAccentRgb((byte R, byte G, byte B) rgb, bool isDark)
    {
        _currentAccentRgb = rgb;
        var winColor = Color.FromArgb(0xFF, rgb.R, rgb.G, rgb.B);
        var onAccentRgb = ZuneColorMath.ContrastingOnColor(rgb.R, rgb.G, rgb.B);
        var onAccentColor = Color.FromArgb(0xFF, onAccentRgb.R, onAccentRgb.G, onAccentRgb.B);
        _activeBarPaint.Color = new SKColor(rgb.R, rgb.G, rgb.B);

        var subtleDarkColor = Color.FromArgb(0x26, rgb.R, rgb.G, rgb.B);
        var subtleLightColor = Color.FromArgb(0x1C, rgb.R, rgb.G, rgb.B);

        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue("Default", out object? darkObj) && darkObj is ResourceDictionary darkDict)
            {
                if (darkDict.TryGetValue("ZuneAccentBrush", out object? darkBrush) && darkBrush is SolidColorBrush b)
                    b.Color = winColor;
                if (darkDict.TryGetValue("ZuneInkOnAccentBrush", out object? darkOnBrush) && darkOnBrush is SolidColorBrush ob)
                    ob.Color = onAccentColor;
                if (darkDict.TryGetValue("ZuneAccentSubtleBrush", out object? darkSub) && darkSub is SolidColorBrush sb)
                    sb.Color = subtleDarkColor;
            }
            if (dict.ThemeDictionaries.TryGetValue("Light", out object? lightObj) && lightObj is ResourceDictionary lightDict)
            {
                if (lightDict.TryGetValue("ZuneAccentBrush", out object? lightBrush) && lightBrush is SolidColorBrush b)
                    b.Color = winColor;
                if (lightDict.TryGetValue("ZuneInkOnAccentBrush", out object? lightOnBrush) && lightOnBrush is SolidColorBrush ob)
                    ob.Color = onAccentColor;
                if (lightDict.TryGetValue("ZuneAccentSubtleBrush", out object? lightSub) && lightSub is SolidColorBrush sb)
                    sb.Color = subtleLightColor;
            }
        }
        if (Application.Current.Resources.TryGetValue("ZuneAccentBrush", out object? appRes) && appRes is SolidColorBrush appAccent)
        {
            appAccent.Color = winColor;
        }
        if (Application.Current.Resources.TryGetValue("ZuneInkOnAccentBrush", out object? appOnRes) && appOnRes is SolidColorBrush appOnAccent)
        {
            appOnAccent.Color = onAccentColor;
        }

        try
        {
            AppWindow.TitleBar.ButtonForegroundColor = isDark
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            AppWindow.TitleBar.ButtonHoverBackgroundColor = isDark
                ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
            AppWindow.TitleBar.ButtonHoverForegroundColor = winColor;
        }
        catch
        {
            // Older hosts without TitleBar customization
        }

        WaveformCanvas?.Invalidate();
        NowPlayingMosaicCanvas?.Invalidate();
        SyncSubSelectorBarItems();
        RefreshDynamicAccentControls();
    }

    private void RefreshDynamicAccentControls()
    {
        if (TransportHeartIcon is not null)
        {
            TransportHeartIcon.Foreground = HeartForeground(ViewModel.CurrentTrackLoved);
        }
        if (DjFreezeButton is not null)
        {
            DjFreezeButton.Background = FreezePillBackground(ViewModel.IsDjFreezeActive);
            DjFreezeButton.BorderBrush = FreezePillBorder(ViewModel.IsDjFreezeActive);
        }
        SyncActiveInboxAuditButtons();
    }

    private string _lastSettledStageKey = "";

    private void OnViewModelNavigationSettled(string primaryNavId, string subTab)
    {
        if (primaryNavId == "collection" && subTab == "playlists")
        {
            SyncPlaylistsListViewSelection();
        }

        if (primaryNavId == "now_playing")
        {
            RefreshNowPlayingMosaic();
        }
        RestoreNowPlayingChrome();
        UpdateMainWindowTimers();

        if (ViewModel.IsInboxAuditDeckVisible)
        {
            SyncActiveInboxAuditButtons();
        }

        string stageKey = primaryNavId switch
        {
            "quickplay" => "quickplay",
            "now_playing" => "now_playing",
            _ => $"{primaryNavId}:{subTab}",
        };
        if (!string.Equals(stageKey, _lastSettledStageKey, StringComparison.Ordinal))
        {
            bool samePrimaryStage = _lastSettledStageKey.Contains(':') && stageKey.Contains(':');
            _lastSettledStageKey = stageKey;
            UIElement? activeStage = primaryNavId switch
            {
                "quickplay" => QuickplayScrollViewer,
                "now_playing" => NowPlayingStage,
                _ => CollectionAndStudioStage,
            };
            if (activeStage is not null)
            {
                float offset = samePrimaryStage ? 8f : 12f;
                int duration = samePrimaryStage ? 90 : 110;
                if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (RootWorkbench?.IsLoaded != true) return;
                    ZuneMotionHelper.PlayStaggeredEntrance(
                        null,
                        activeStage,
                        horizontalOffset: offset,
                        durationMs: duration);
                }))
                {
                    ZuneMotionHelper.PlayStaggeredEntrance(
                        null,
                        activeStage,
                        horizontalOffset: offset,
                        durationMs: duration);
                }
            }
        }
    }

    private void SyncActiveInboxAuditButtons()
    {
        if (InboxAuditChipsPanel is null || !ViewModel.IsInboxAuditDeckVisible) return;
        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        var activeBg = new SolidColorBrush(Color.FromArgb(isLight ? (byte)0x1F : (byte)0x2E, _currentAccentRgb.R, _currentAccentRgb.G, _currentAccentRgb.B));
        var activeBorder = new SolidColorBrush(Color.FromArgb(0xFF, _currentAccentRgb.R, _currentAccentRgb.G, _currentAccentRgb.B));
        var inactiveBg = new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5) : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
        var inactiveBorder = new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC) : Color.FromArgb(0xFF, 0x26, 0x26, 0x26));
        var activeFg = new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        var inactiveFg = new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0x22, 0x22, 0x22) : Color.FromArgb(0xFF, 0xD8, 0xD8, 0xD8));

        foreach (var btn in InboxAuditChipsPanel.Children.OfType<Button>())
        {
            bool active = btn.Tag is string tag
                && string.Equals(tag, ViewModel.LibraryNavMode, StringComparison.OrdinalIgnoreCase);
            btn.Background = active ? activeBg : inactiveBg;
            btn.BorderBrush = active ? activeBorder : inactiveBorder;
            btn.Foreground = active ? activeFg : inactiveFg;
            btn.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    // ---------------- Global notification ----------------

    private readonly HashSet<Flyout> _themeSyncedFlyouts = [];

    /// <summary>
    /// Flyouts render in a popup layer that does not inherit RootWorkbench.RequestedTheme,
    /// so push the active theme onto the presenter every time the flyout opens.
    /// </summary>
    private void AttachFlyoutThemeSync(Flyout? flyout)
    {
        if (flyout is null || !_themeSyncedFlyouts.Add(flyout)) return;
        flyout.Opened += (_, _) =>
        {
            if (flyout.Content is FrameworkElement content)
            {
                content.RequestedTheme = RootWorkbench.ActualTheme;
                if (VisualTreeHelper.GetParent(content) is FrameworkElement parent)
                {
                    DependencyObject? node = parent;
                    while (node is not null and not FlyoutPresenter)
                    {
                        node = VisualTreeHelper.GetParent(node);
                    }
                    if (node is FlyoutPresenter presenter)
                    {
                        presenter.RequestedTheme = RootWorkbench.ActualTheme;
                    }
                }
            }
        };
    }

    private void ScheduleNotificationAutoClose()
    {
        if (_notificationTimer is null) return;
        _notificationTimer.Stop();
        if (ViewModel.IsNotificationOpen && ViewModel.NotificationLevel is "success" or "info")
        {
            _notificationTimer.Start();
        }
    }

    // ---------------- Seek slider (keyboard accessible playhead) ----------------

    private void SyncSeekSliderFromEngine()
    {
        if (_isSeekDragging || SeekSlider is null) return;
        double dur = ViewModel.PlaybackDurationSeconds;
        double pos = ViewModel.PlaybackPositionSeconds;
        _updatingSeekSlider = true;
        try
        {
            SeekSlider.IsEnabled = dur > 0;
            SeekSlider.Maximum = Math.Max(1.0, dur);
            SeekSlider.Value = Math.Clamp(pos, 0.0, SeekSlider.Maximum);
        }
        finally
        {
            _updatingSeekSlider = false;
        }
    }

    private void OnSeekSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeekSlider) return;
        if (ViewModel.PlaybackDurationSeconds <= 0) return;
        if (Math.Abs(e.NewValue - ViewModel.PlaybackPositionSeconds) < 0.75) return;
        ViewModel.SeekToSeconds(e.NewValue);
    }

    // ---------------- Two-Stage Classical Collection Hierarchy ----------------

    private void OnClassicalEraFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            ViewModel.SetClassicalEraFilter(fe.Tag as string ?? "");
        }
    }

    private void OnOpenClassicalJumpMatrixClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenClassicalJumpMatrix();

    private void OnOpenClassicalJumpMatrixTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ViewModel.OpenClassicalJumpMatrix();
    }

    private void OnCloseClassicalJumpMatrixClick(object sender, RoutedEventArgs e) =>
        ViewModel.CloseClassicalJumpMatrix();

    private void OnClassicalComposerItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ClassicalComposerViewItem composer)
        {
            ViewModel.OpenClassicalComposer(composer);
        }
    }

    private void OnCloseClassicalComposerDetailClick(object sender, RoutedEventArgs e) =>
        ViewModel.CloseClassicalComposerDetail();

    private void OnClassicalFormFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string form })
        {
            ViewModel.SetClassicalFormFilter(form);
        }
    }

    private static bool IsClassicalModifierDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private static bool IsDescendantOfButtonOrCheckBox(DependencyObject? source, DependencyObject? stopAt)
    {
        var cur = source;
        while (cur is not null && !ReferenceEquals(cur, stopAt))
        {
            if (cur is ButtonBase)
            {
                return true;
            }
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private static T? ResolveClassicalBoundItem<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.Tag as T
        ?? (sender as FrameworkElement)?.DataContext as T;

    private void SyncClassicalInspectorSelection(IReadOnlyList<TrackItem> selected)
    {
        if (selected.Count > 0)
        {
            UpdateCoverArtImage(InspectedCoverImage, selected[0].CoverArtPath);
            if (selected.Count > 1 && InspectorSplitView is not null && !InspectorSplitView.IsPaneOpen)
            {
                SetInspectorOpen(true);
            }
        }
        SyncInlineInspectorTargets(selected);
    }

    private void OnClassicalWorkHeaderClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalWorkViewItem>(sender) is { } work)
        {
            bool ctrl = IsClassicalModifierDown(VirtualKey.Control);
            bool shift = IsClassicalModifierDown(VirtualKey.Shift);
            if (ctrl || shift || ViewModel.IsMultiSelectMode)
            {
                var selected = ViewModel.SelectClassicalWorkTracks(work, additiveToggle: ctrl || ViewModel.IsMultiSelectMode, rangeSelect: shift);
                SyncClassicalInspectorSelection(selected);
            }
            else
            {
                ViewModel.ToggleClassicalWorkExpanded(work);
                var selected = ViewModel.SelectClassicalWorkTracks(work, additiveToggle: false, rangeSelect: false);
                SyncClassicalInspectorSelection(selected);
            }
        }
    }

    private void OnClassicalWorkCheckboxClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalWorkViewItem>(sender) is { } work)
        {
            bool shift = IsClassicalModifierDown(VirtualKey.Shift);
            var selected = ViewModel.SelectClassicalWorkTracks(work, additiveToggle: true, rangeSelect: shift);
            if (sender is CheckBox cb)
            {
                cb.IsChecked = work.IsSelected;
            }
            SyncClassicalInspectorSelection(selected);
        }
    }

    private void OnEditClassicalWorkClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalWorkViewItem>(sender) is { } work)
        {
            var workTracks = work.SelectedRecordingTracks;
            var currentSelected = ViewModel.GetSelectedClassicalTracks();
            IReadOnlyList<TrackItem> targets;
            if (currentSelected.Count > 1
                && workTracks.Any(wt => currentSelected.Any(sel => sel.TrackUid == wt.TrackUid)))
            {
                targets = currentSelected;
            }
            else
            {
                targets = ViewModel.SelectClassicalWorkTracks(work, additiveToggle: false, rangeSelect: false);
            }
            FocusInlineInspectorEditor(targets);
        }
    }

    private void OnClassicalWorkRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalWorkViewItem>(sender) is { } work)
        {
            var workTracks = work.SelectedRecordingTracks;
            var currentSelected = ViewModel.GetSelectedClassicalTracks();
            bool alreadySelected = workTracks.Any(wt => currentSelected.Any(sel => sel.TrackUid == wt.TrackUid));
            if (!alreadySelected)
            {
                var selected = ViewModel.SelectClassicalWorkTracks(work, additiveToggle: false, rangeSelect: false);
                SyncClassicalInspectorSelection(selected);
            }
        }
    }

    private void OnPlayClassicalWorkClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalWorkViewItem>(sender) is { } work)
        {
            ViewModel.PlayClassicalWork(work);
        }
    }

    private void OnSelectClassicalRecordingClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalRecordingViewItem>(sender) is { } rec)
        {
            ViewModel.SelectClassicalRecording(rec);
        }
    }

    private void OnClassicalMovementTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsDescendantOfButtonOrCheckBox(e.OriginalSource as DependencyObject, sender as DependencyObject))
        {
            return;
        }
        if (ResolveClassicalBoundItem<ClassicalMovementViewItem>(sender) is { } mov)
        {
            bool ctrl = IsClassicalModifierDown(VirtualKey.Control);
            bool shift = IsClassicalModifierDown(VirtualKey.Shift);
            var selected = ViewModel.SelectClassicalMovementTrack(mov, additiveToggle: ctrl || ViewModel.IsMultiSelectMode, rangeSelect: shift);
            SyncClassicalInspectorSelection(selected);
        }
    }

    private void OnClassicalMovementCheckboxClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalMovementViewItem>(sender) is { } mov)
        {
            bool shift = IsClassicalModifierDown(VirtualKey.Shift);
            var selected = ViewModel.SelectClassicalMovementTrack(mov, additiveToggle: true, rangeSelect: shift);
            if (sender is CheckBox cb)
            {
                cb.IsChecked = mov.IsSelected;
            }
            SyncClassicalInspectorSelection(selected);
        }
    }

    private void OnClassicalMovementDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsDescendantOfButtonOrCheckBox(e.OriginalSource as DependencyObject, sender as DependencyObject))
        {
            return;
        }
        if (ResolveClassicalBoundItem<ClassicalMovementViewItem>(sender) is { } mov)
        {
            ViewModel.PlayClassicalMovement(mov);
        }
    }

    private void OnClassicalMovementRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalMovementViewItem>(sender) is { } mov)
        {
            var currentSelected = ViewModel.GetSelectedClassicalTracks();
            if (!currentSelected.Any(t => t.TrackUid == mov.TrackUid))
            {
                var selected = ViewModel.SelectClassicalMovementTrack(mov, additiveToggle: false, rangeSelect: false);
                SyncClassicalInspectorSelection(selected);
            }
        }
    }

    private void OnPlayClassicalMovementClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalMovementViewItem>(sender) is { } mov)
        {
            ViewModel.PlayClassicalMovement(mov);
        }
    }

    private void OnClassicalJumpEraTileClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalJumpTileItem>(sender) is { } tile)
        {
            ViewModel.SetClassicalEraFilter(tile.Key);
            ViewModel.CloseClassicalJumpMatrix();
        }
    }

    private void OnClassicalJumpInitialTileClick(object sender, RoutedEventArgs e)
    {
        if (ResolveClassicalBoundItem<ClassicalJumpTileItem>(sender) is { } tile)
        {
            var target = ViewModel.ResolveJumpTargetForInitial(tile.Key);
            if (target is not null)
            {
                ClassicalComposerListView?.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            }
        }
    }

}
