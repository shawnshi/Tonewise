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
using Tonewise.Desktop.Services;
using Tonewise.Desktop.ViewModels;
using Tonewise.SyncHost.Curation;
using Tonewise.SyncHost.Ledger;
using Tonewise.SyncHost.Playlists;
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
    private bool _suppressSubTabSync;

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
    private bool _syncingNavSelection;
    private double _lastWaveformProgress = -1.0;

    private static readonly SKColor ActiveBarColor = new(0xD9, 0xB3, 0x72);
    private static readonly SKColor InactiveBarColor = new(0x64, 0x74, 0x8B, 0x60);
    private static readonly SKPaint ActiveBarPaint = new() { Color = ActiveBarColor, IsAntialias = true };
    private static readonly SKPaint InactiveBarPaint = new() { Color = InactiveBarColor, IsAntialias = true };

    public Visibility VisibleWhenBoth(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

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

    public SolidColorBrush HeartForeground(bool isLoved)
    {
        if (isLoved) return new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x6B, 0x52));
        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        return new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0x64, 0x74, 0x8B) : Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8));
    }

    public SolidColorBrush FreezePillBackground(bool active)
    {
        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        if (active) return new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0xE0, 0xF2, 0xFE) : Color.FromArgb(0xFF, 0x1F, 0x35, 0x45));
        return new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0xEF, 0xF4, 0xFA) : Color.FromArgb(0xFF, 0x14, 0x1A, 0x26));
    }

    public SolidColorBrush FreezePillBorder(bool active)
    {
        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        if (active) return new SolidColorBrush(Color.FromArgb(0xFF, 0x38, 0xBD, 0xF8));
        return new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1) : Color.FromArgb(0xFF, 0x29, 0x34, 0x48));
    }

    public MainWindow()
    {
        InitializeComponent();

        TrySetObsidianBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarDragRegion);
        try
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1);
            AppWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(0xFF, 0x24, 0x2C, 0x3B);
            AppWindow.TitleBar.ButtonHoverForegroundColor = Color.FromArgb(0xFF, 0xD9, 0xB3, 0x72);
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

        InitializeSmtc();
        InitializeGlobalHotkeys();
        UpdateSignalPurityDotBrush();
        SyncSubSelectorBarItems();
        SyncInlineInspectorTargets([]);

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ViewModel.ApplySearchSuggestionOrQuery(_pendingSearchText);
        };

        ViewModel.SubTabsChanged += SyncSubSelectorBarItems;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SignalPurityColorHex))
            {
                UpdateSignalPurityDotBrush();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsPlaying))
            {
                SyncSmtcPlaybackState();
            }
            else if (args.PropertyName == nameof(MainViewModel.CurrentTrack))
            {
                UpdateCoverArtImage(TransportCoverImage, ViewModel.CurrentTrack?.CoverArtPath);
            }
            else if (args.PropertyName == nameof(MainViewModel.InspectedTrack))
            {
                UpdateCoverArtImage(InspectedCoverImage, ViewModel.InspectedTrack?.CoverArtPath);
                var selected = TrackListView.SelectedItems.OfType<TrackItem>().ToList();
                if (selected.Count <= 1)
                {
                    SyncInlineInspectorTargets(ViewModel.InspectedTrack is null ? [] : [ViewModel.InspectedTrack]);
                }
            }
            else if (args.PropertyName is nameof(MainViewModel.PrimaryNavId) or nameof(MainViewModel.SecondarySubTab))
            {
                SyncNavigationSelection();
                if (ViewModel.PrimaryNavId == "playlists") SyncPlaylistsListViewSelection();
            }
            else if (args.PropertyName == nameof(MainViewModel.ShowClassicalSidebarSubItems))
            {
                SyncClassicalNavChildren();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsSidebarExpanded))
            {
                ApplyPaneStateForDisplayMode();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsMultiSelectMode) && TrackListView is not null)
            {
                TrackListView.SelectionMode = ViewModel.IsMultiSelectMode
                    ? ListViewSelectionMode.Multiple
                    : ListViewSelectionMode.Extended;
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
        ViewModel.TrackChanged += async track =>
        {
            try
            {
                UpdateCoverArtImage(TransportCoverImage, ViewModel.CurrentTrack?.CoverArtPath);
                UpdateCoverArtImage(InspectedCoverImage, ViewModel.InspectedTrack?.CoverArtPath);
                RefreshQueuePlayingHighlights();
                await UpdateSmtcMetadataAsync(ViewModel.CurrentTrack);
                await LoadBookletForTrackAsync(track);
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

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _isSeekDragging = true), handledEventsToo: true);
        MainNavigationView.Loaded += (_, _) =>
        {
            if (MainNavigationView.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = "设置";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(settingsItem, "NavItemSettings");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsItem, "设置");
                ToolTipService.SetToolTip(settingsItem, "设置 (Ctrl+,)：音频输出 / 存储 / 外观");
            }
            SyncNavigationSelection();
        };
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _isSeekDragging = false), handledEventsToo: true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => _isSeekDragging = false), handledEventsToo: true);

        ViewModel.RestoreSavedSettingsAndLibrary();
        ApplyAppTheme();
        ApplyPaneStateForDisplayMode();
        ApplyInspectorState();
        SyncClassicalNavChildren();
        SyncNavigationSelection();
        SyncSubSelectorBarItems();
        UpdateCoverArtImage(TransportCoverImage, ViewModel.CurrentTrack?.CoverArtPath);
        UpdateCoverArtImage(InspectedCoverImage, ViewModel.InspectedTrack?.CoverArtPath);
        SyncInlineInspectorTargets(ViewModel.InspectedTrack is null ? [] : [ViewModel.InspectedTrack]);

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
        _progressTimer.Start();

        Closed += (_, _) =>
        {
            _folderLoadCts?.Cancel();
            _folderLoadCts?.Dispose();
            _folderLoadCts = null;
            _progressTimer.Stop();
            _searchDebounceTimer.Stop();
            _notificationTimer.Stop();
            _globalHotkeys?.Dispose();
            _oledWidget?.Close();
            _smtcPlayer?.Dispose();
            ViewModel.Dispose();
        };
    }

    private void TrySetObsidianBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        catch
        {
            try
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            catch
            {
                // Non-Mica fallback via XAML surface fill
            }
        }
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

    private void InitializeSmtc()
    {
        try
        {
            _smtcPlayer = new MediaPlayer();
            _smtcPlayer.CommandManager.IsEnabled = false;
            _smtc = _smtcPlayer.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += (_, args) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    switch (args.Button)
                    {
                        case SystemMediaTransportControlsButton.Play:
                            if (!ViewModel.IsPlaying) ViewModel.TogglePlayPauseCommand.Execute(null);
                            break;
                        case SystemMediaTransportControlsButton.Pause:
                            if (ViewModel.IsPlaying) ViewModel.TogglePlayPauseCommand.Execute(null);
                            break;
                        case SystemMediaTransportControlsButton.Next:
                            ViewModel.NextTrackCommand.Execute(null);
                            break;
                        case SystemMediaTransportControlsButton.Previous:
                            ViewModel.PreviousTrackCommand.Execute(null);
                            break;
                    }
                });
            };
        }
        catch
        {
            // Headless or restricted environment without SMTC shell service
        }
    }

    private void SyncSmtcPlaybackState()
    {
        if (_smtc is null) return;
        try
        {
            _smtc.PlaybackStatus = ViewModel.IsPlaying
                ? MediaPlaybackStatus.Playing
                : (ViewModel.CurrentTrack is null ? MediaPlaybackStatus.Closed : MediaPlaybackStatus.Paused);
        }
        catch
        {
            // Non-fatal SMTC state fallback
        }
    }

    private async Task UpdateSmtcMetadataAsync(TrackItem? track)
    {
        if (_smtc is null) return;
        SyncSmtcPlaybackState();
        if (track is null) return;
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = track.ArtistsDisplay;
            updater.MusicProperties.AlbumTitle = track.Album;
            if (!string.IsNullOrWhiteSpace(track.CoverArtPath) && File.Exists(track.CoverArtPath))
            {
                try
                {
                    var thumbFile = await StorageFile.GetFileFromPathAsync(track.CoverArtPath);
                    if (ViewModel.CurrentTrack?.TrackUid == track.TrackUid)
                    {
                        updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(thumbFile);
                    }
                }
                catch
                {
                    updater.Thumbnail = null;
                }
            }
            else
            {
                updater.Thumbnail = null;
            }
            updater.Update();
        }
        catch
        {
            // Non-fatal metadata update error
        }
    }

    private async Task LoadBookletForTrackAsync(TrackItem? track)
    {
        string? pdfPath = track?.BookletPdfPath;
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            _bookletDoc = null;
            _loadedBookletPath = null;
            BookletPageImage.Source = null;
            return;
        }
        if (string.Equals(_loadedBookletPath, pdfPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(pdfPath);
            _bookletDoc = await PdfDocument.LoadFromFileAsync(file);
            _loadedBookletPath = pdfPath;
            _bookletPageIndex = 0;
            await RenderCurrentBookletPageAsync();
        }
        catch
        {
            _bookletDoc = null;
            _loadedBookletPath = null;
            BookletPageStatusText.Text = "Booklet PDF unavailable";
        }
    }

    private async Task RenderCurrentBookletPageAsync()
    {
        if (_bookletDoc is null || _bookletDoc.PageCount == 0) return;
        int pageCount = (int)_bookletDoc.PageCount;
        _bookletPageIndex = Math.Clamp(_bookletPageIndex, 0, pageCount - 1);

        using var page = _bookletDoc.GetPage((uint)_bookletPageIndex);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream);
        stream.Seek(0);
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        BookletPageImage.Source = bmp;
        BookletPageStatusText.Text = $"Booklet PDF · Page {_bookletPageIndex + 1} / {pageCount}";
    }

    private async void OnBookletPrevClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_bookletDoc is null || _bookletPageIndex <= 0) return;
            _bookletPageIndex--;
            await RenderCurrentBookletPageAsync();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("OnBookletPrevClick", ex);
        }
    }

    private async void OnBookletNextClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_bookletDoc is null || _bookletPageIndex + 1 >= (int)_bookletDoc.PageCount) return;
            _bookletPageIndex++;
            await RenderCurrentBookletPageAsync();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("OnBookletNextClick", ex);
        }
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            _folderLoadCts?.Cancel();
            _folderLoadCts?.Dispose();
            var cts = new CancellationTokenSource();
            _folderLoadCts = cts;
            await ViewModel.LoadMusicFolderAsync(folder.Path, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another folder open or window close
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("OnOpenFolderClick", ex);
            ViewModel.StatusMessage = $"Failed to load folder: {ex.Message}";
        }
    }

    private async void OnLoadEqClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ViewModel.LoadEqualizerApoFile(file.Path);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("OnLoadEqClick", ex);
            ViewModel.Notify($"加载 EQ 配置失败：{ex.Message}", "error");
        }
    }

    private async void OnLoadFirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".sofa");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ViewModel.LoadFirImpulseFile(file.Path);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("OnLoadFirClick", ex);
            ViewModel.Notify($"加载 FIR 脉冲失败：{ex.Message}", "error");
        }
    }

    private async void OnLastFmSetupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = ViewModel.GetSavedLastFmConfig();
            var apiKeyBox = new TextBox
            {
                Header = "Last.fm API Key",
                PlaceholderText = "32 位 Web API Key (last.fm/api/account)",
                Text = saved?.ApiKey ?? "",
            };
            var secretBox = new PasswordBox
            {
                Header = "Shared Secret (API Secret)",
                PlaceholderText = "双向读写推送必填（Scrobble / Loved / 浏览器 OAuth）",
                Password = saved?.ApiSecret ?? "",
            };
            var userBox = new TextBox
            {
                Header = "Last.fm 用户名",
                PlaceholderText = "使用浏览器授权时可留空自动识别",
                Text = saved?.Username ?? "",
            };

            string? pendingToken = null;
            var oauthStatusText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(saved?.SessionKey)
                    ? "推荐使用浏览器一键授权（兼容二步验证与谷歌登录），即可自动推送播放记录与 Loved tracks。"
                    : "当前已配置 SessionKey（读写已就绪）。若推送异常，可重新执行浏览器授权刷新会话。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8)),
            };

            var openBrowserAuthBtn = new Button
            {
                Content = "1. 打开浏览器授权 (推荐)",
            };
            openBrowserAuthBtn.Click += async (_, _) =>
            {
                string k = apiKeyBox.Text.Trim();
                string s = secretBox.Password.Trim();
                if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(s))
                {
                    oauthStatusText.Text = "请先填写上方的 API Key 与 Shared Secret。";
                    return;
                }
                try
                {
                    openBrowserAuthBtn.IsEnabled = false;
                    oauthStatusText.Text = "正在向 Last.fm 申请授权令牌…";
                    var client = new LastFmWebApiClient();
                    var (token, url) = await client.RequestAuthTokenAsync(k, s);
                    pendingToken = token;
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    oauthStatusText.Text = "已在浏览器打开 Last.fm 授权页面：点击「Yes, allow access」后，回到本窗口点击下方「保存 / 完成授权并同步」。";
                }
                catch (Exception ex)
                {
                    oauthStatusText.Text = $"申请浏览器授权失败：{ex.Message}";
                }
                finally
                {
                    openBrowserAuthBtn.IsEnabled = true;
                }
            };

            var passwordBox = new PasswordBox
            {
                Header = "备选：密码登录或手动 SessionKey（无需浏览器）",
                PlaceholderText = "留空则保留当前 SessionKey 或仅只读拉取",
            };
            var rawSessionCheck = new CheckBox
            {
                Content = "上述输入框填写的是已有 SessionKey 原文",
                IsChecked = false,
            };
            var fallbackExpander = new Expander
            {
                Header = "备选方式：密码登录 / 手动 SessionKey",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { passwordBox, rawSessionCheck },
                },
            };

            var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
            panel.Children.Add(apiKeyBox);
            panel.Children.Add(secretBox);
            panel.Children.Add(userBox);
            panel.Children.Add(openBrowserAuthBtn);
            panel.Children.Add(oauthStatusText);
            panel.Children.Add(fallbackExpander);

            var dialog = new ContentDialog
            {
                Title = "配置并授权 Last.fm",
                Content = panel,
                PrimaryButtonText = "保存 / 完成授权并同步",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootWorkbench.XamlRoot,
                RequestedTheme = RootWorkbench.ActualTheme,
            };

            if (await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(pendingToken) && string.IsNullOrWhiteSpace(passwordBox.Password))
                    {
                        var client = new LastFmWebApiClient();
                        var (sk, returnedUser) = await client.ExchangeAuthTokenForSessionAsync(
                            apiKeyBox.Text.Trim(),
                            secretBox.Password.Trim(),
                            pendingToken);
                        string finalUser = !string.IsNullOrWhiteSpace(returnedUser) ? returnedUser : userBox.Text.Trim();
                        await ViewModel.SaveLastFmSessionAndSyncAsync(apiKeyBox.Text, secretBox.Password, finalUser, sk);
                    }
                    else
                    {
                        await ViewModel.ConfigureAndSyncLastFmAsync(
                            apiKeyBox.Text,
                            secretBox.Password,
                            userBox.Text,
                            passwordBox.Password,
                            isRawSessionKey: rawSessionCheck.IsChecked == true);
                    }
                }
                catch (Exception ex)
                {
                    ViewModel.Notify($"Last.fm 配置或同步失败：{ex.Message}", "error");
                }
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("OnLastFmSetupClick", ex);
            ViewModel.Notify($"打开 Last.fm 配置窗口失败：{ex.Message}", "error");
        }
    }

    private async void OnExportUsbCarClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ViewModel.ExportFilteredToUsb(folder.Path);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("OnExportUsbCarClick", ex);
            ViewModel.Notify($"导出至 USB 失败：{ex.Message}", "error");
        }
    }

    private void ToggleOledWidget()
    {
        if (_oledWidget is not null)
        {
            _oledWidget.Close();
            return;
        }
        _oledWidget = new DeskClockWidgetWindow(ViewModel);
        _oledWidget.Closed += (_, _) => _oledWidget = null;
        _oledWidget.Activate();
    }

    private void OnOpenOledWidgetClick(object sender, RoutedEventArgs e)
    {
        if (_oledWidget is null)
        {
            _oledWidget = new DeskClockWidgetWindow(ViewModel);
            _oledWidget.Closed += (_, _) => _oledWidget = null;
        }
        _oledWidget.Activate();
    }

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackItem track)
        {
            ViewModel.PlayTrack(track);
        }
    }

    private void OnSwitchAlternateRecordingClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uid })
        {
            var match = ViewModel.AlternateRecordings.FirstOrDefault(t => t.TrackUid == uid);
            if (match is not null) ViewModel.SwitchAlternateRecording(match);
        }
    }

    private void OnRestoreSessionSlotClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int slotIdx })
        {
            ViewModel.RestoreSessionSlot(slotIdx);
        }
    }

    private void OnSaveSessionSlotClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int slotIdx })
        {
            ViewModel.SaveCurrentToSessionSlot(slotIdx);
        }
    }

    private bool _syncingPlaylistSelection;
    private PlaylistSummary? _rightClickedPlaylist;

    private void SyncPlaylistsListViewSelection()
    {
        if (PlaylistsListView is null || PlaylistsListView.SelectionMode != ListViewSelectionMode.Single) return;
        _syncingPlaylistSelection = true;
        try
        {
            PlaylistsListView.SelectedItem = ViewModel.SelectedPlaylist;
            ViewModel.SelectedPlaylistCount = ViewModel.SelectedPlaylist is not null ? 1 : 0;
        }
        finally
        {
            _syncingPlaylistSelection = false;
        }
    }

    private void OnPlaylistMultiSelectToggleClick(object sender, RoutedEventArgs e)
    {
        if (PlaylistsListView is null) return;
        bool multi = PlaylistMultiSelectToggleButton.IsChecked == true;
        _syncingPlaylistSelection = true;
        try
        {
            PlaylistsListView.SelectionMode = multi
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Single;
            if (!multi)
            {
                PlaylistsListView.SelectedItems.Clear();
                if (ViewModel.SelectedPlaylist is not null)
                {
                    PlaylistsListView.SelectedItem = ViewModel.SelectedPlaylist;
                }
            }
            else if (PlaylistsListView.SelectedItems.Count == 0 && ViewModel.SelectedPlaylist is not null)
            {
                PlaylistsListView.SelectedItems.Add(ViewModel.SelectedPlaylist);
            }
            ViewModel.SelectedPlaylistCount = PlaylistsListView.SelectedItems.Count;
        }
        finally
        {
            _syncingPlaylistSelection = false;
        }
    }

    private void OnSelectAllPlaylistsClick(object sender, RoutedEventArgs e)
    {
        if (PlaylistsListView is null || ViewModel.Playlists.Count == 0) return;
        if (PlaylistsListView.SelectionMode != ListViewSelectionMode.Multiple)
        {
            ViewModel.IsPlaylistMultiSelectMode = true;
            PlaylistsListView.SelectionMode = ListViewSelectionMode.Multiple;
        }
        PlaylistsListView.SelectAll();
    }

    private void OnPlaylistsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPlaylistSelection || PlaylistsListView is null) return;
        var selected = PlaylistsListView.SelectedItems.OfType<PlaylistSummary>().ToList();
        ViewModel.SelectedPlaylistCount = selected.Count;
        if (PlaylistsListView.SelectionMode == ListViewSelectionMode.Single
            && selected.Count == 1
            && !ReferenceEquals(selected[0], ViewModel.SelectedPlaylist))
        {
            ViewModel.SelectPlaylist(selected[0]);
        }
    }

    private void OnPlaylistsRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistSummary rightClicked)
        {
            _rightClickedPlaylist = rightClicked;
            if (PlaylistsListView.SelectionMode == ListViewSelectionMode.Single)
            {
                if (!ReferenceEquals(PlaylistsListView.SelectedItem, rightClicked))
                {
                    PlaylistsListView.SelectedItem = rightClicked;
                }
            }
            else if (!PlaylistsListView.SelectedItems.Contains(rightClicked))
            {
                PlaylistsListView.SelectedItems.Add(rightClicked);
            }
        }
    }

    private async void OnSendPlaylistToPhoneClick(object sender, RoutedEventArgs e)
    {
        var selected = PlaylistsListView?.SelectedItems.OfType<PlaylistSummary>().ToList() ?? [];
        await ViewModel.SendPlaylistsToPhoneAsync(selected);
    }

    private async void OnContextSendPlaylistCardToPhoneClick(object sender, RoutedEventArgs e)
    {
        var selected = PlaylistsListView?.SelectedItems.OfType<PlaylistSummary>().ToList() ?? [];
        if (selected.Count > 1)
        {
            _rightClickedPlaylist = null;
            await ViewModel.SendPlaylistsToPhoneAsync(selected);
            return;
        }
        var pl = _rightClickedPlaylist ?? (sender as FrameworkElement)?.DataContext as PlaylistSummary ?? ViewModel.SelectedPlaylist;
        _rightClickedPlaylist = null;
        await ViewModel.SendPlaylistToPhoneAsync(pl);
    }

    private async void OnContextDeletePlaylistCardClick(object sender, RoutedEventArgs e)
    {
        var target = _rightClickedPlaylist ?? (sender as FrameworkElement)?.DataContext as PlaylistSummary ?? ViewModel.SelectedPlaylist;
        _rightClickedPlaylist = null;
        await DeletePlaylistWithConfirmAsync(target);
    }

    private void OnRematchPlaylistsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPlaylistMultiSelectMode)
        {
            var selected = PlaylistsListView?.SelectedItems.OfType<PlaylistSummary>().ToList() ?? [];
            ViewModel.RematchPlaylistsAndPersist(selected.Count > 0 ? selected : null);
        }
        else
        {
            ViewModel.RematchPlaylistsAndPersist(null);
        }
    }

    private void OnContextRematchPlaylistCardClick(object sender, RoutedEventArgs e)
    {
        var selected = PlaylistsListView?.SelectedItems.OfType<PlaylistSummary>().ToList() ?? [];
        if (selected.Count > 1)
        {
            _rightClickedPlaylist = null;
            ViewModel.RematchPlaylistsAndPersist(selected);
            return;
        }
        var target = _rightClickedPlaylist ?? (sender as FrameworkElement)?.DataContext as PlaylistSummary ?? ViewModel.SelectedPlaylist;
        _rightClickedPlaylist = null;
        if (target is not null)
        {
            ViewModel.RematchPlaylistsAndPersist([target]);
        }
    }

    private void OnRemoveLanOutboxItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ViewModel.RemoveLanOutboxItem(id);
        }
    }

    private void OnUnpairLanDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string deviceId })
        {
            ViewModel.UnpairLanDevice(deviceId);
        }
    }

    private async void OnNewPlaylistClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            Header = "Playlist Name",
            PlaceholderText = "e.g. Night Coding / Classical Favorites",
        };
        var seedBox = new CheckBox
        {
            Content = $"Seed with current track list ({ViewModel.FilteredTracks.Count} tracks)",
            IsChecked = false,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(nameBox);
        panel.Children.Add(seedBox);

        var dialog = new ContentDialog
        {
            Title = "New Playlist · 新建歌单",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            RequestedTheme = RootWorkbench.ActualTheme,
            XamlRoot = RootWorkbench.XamlRoot,
        };

        if (await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
        {
            string name = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ViewModel.StatusMessage = "Playlist name cannot be empty.";
                return;
            }
            try
            {
                ViewModel.CreatePlaylist(name, seedBox.IsChecked == true);
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Create playlist failed: {ex.Message}";
            }
        }
    }

    private async void OnImportPlaylistClick(object sender, RoutedEventArgs e)
    {
        var urlBox = new TextBox
        {
            Header = "Playlist / Chart URL (Spotify / Apple Music / Billboard / Kworb)",
            PlaceholderText = "https://open.spotify.com/playlist/... | https://music.apple.com/... | https://www.billboard.com/charts/...",
        };
        var nameBox = new TextBox
        {
            Header = "Playlist Name (optional for URL)",
            PlaceholderText = "Auto-detected from webpage or file name",
        };
        var maxBox = new TextBox
        {
            Header = "Max Tracks",
            Text = "200",
        };
        var textBox = new TextBox
        {
            Header = "Or Paste Text / M3U8 Lines (Artist - Title or paths)",
            AcceptsReturn = true,
            Height = 120,
            PlaceholderText = "Lady Gaga & Bradley Cooper - Shallow\nAdele - Hello\n...",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);

        var spotifyBtn = new Button { Content = "Spotify All Out 2010s", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        spotifyBtn.Click += (_, _) =>
        {
            urlBox.Text = "https://open.spotify.com/playlist/37i9dQZF1DX5Ejj0EkURtP";
            textBox.Text = "";
        };

        var appleBtn = new Button { Content = "Apple Music 年度最佳", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        appleBtn.Click += (_, _) =>
        {
            urlBox.Text = "https://music.apple.com/cn/playlist/2026-%E5%B9%B4%E5%BA%A6%E6%9C%80%E4%BD%B3%E6%AD%8C%E6%9B%B2/pl.d398088b5b114e038297c05d152ba1e4";
            textBox.Text = "";
        };

        var bbBtn = new Button { Content = "Billboard Hot 100", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        bbBtn.Click += (_, _) =>
        {
            urlBox.Text = "https://www.billboard.com/charts/greatest-hot-100-singles/";
            textBox.Text = "";
        };

        var kworbBtn = new Button { Content = "Kworb Cumulative", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        kworbBtn.Click += (_, _) =>
        {
            urlBox.Text = "https://kworb.net/pop/cumulative.html";
            textBox.Text = "";
        };

        var fileBtn = new Button { Content = "选择文本/M3U8文件...", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        fileBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".m3u");
            picker.FileTypeFilter.Add(".m3u8");
            picker.FileTypeFilter.Add(".csv");
            picker.FileTypeFilter.Add(".tsv");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                textBox.Text = await File.ReadAllTextAsync(file.Path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    nameBox.Text = Path.GetFileNameWithoutExtension(file.Path);
                }
                urlBox.Text = "";
            }
        };

        var presetRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        presetRow1.Children.Add(spotifyBtn);
        presetRow1.Children.Add(appleBtn);
        presetRow1.Children.Add(bbBtn);
        var presetRow2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        presetRow2.Children.Add(kworbBtn);
        presetRow2.Children.Add(fileBtn);

        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        panel.Children.Add(new TextBlock { Text = "Quick Sources · 快速导入来源", FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(presetRow1);
        panel.Children.Add(presetRow2);
        panel.Children.Add(urlBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(maxBox);
        panel.Children.Add(textBox);

        var dialog = new ContentDialog
        {
            Title = "Import Playlist · 导入歌单",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            RequestedTheme = RootWorkbench.ActualTheme,
            XamlRoot = RootWorkbench.XamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 620.0;

        if (await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
        {
            int maxTracks = int.TryParse(maxBox.Text.Trim(), out int parsedMax) && parsedMax > 0 ? parsedMax : 200;
            try
            {
                if (!string.IsNullOrWhiteSpace(urlBox.Text))
                {
                    await ViewModel.ImportPlaylistFromUrlAsync(urlBox.Text.Trim(), nameBox.Text, maxTracks);
                }
                else if (!string.IsNullOrWhiteSpace(textBox.Text))
                {
                    string fallbackName = string.IsNullOrWhiteSpace(nameBox.Text)
                        ? $"Imported_{DateTime.Now:yyyyMMdd_HHmm}"
                        : nameBox.Text.Trim();
                    ViewModel.ImportPlaylistFromText(textBox.Text, fallbackName, maxTracks);
                }
                else
                {
                    ViewModel.StatusMessage = "Enter a playlist URL, pick a file, or paste track lines to import.";
                }
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Import playlist failed: {ex.Message}";
            }
        }
    }

    private async void OnEditPlaylistClick(object sender, RoutedEventArgs e)
    {
        var target = ViewModel.SelectedPlaylist;
        if (target is null)
        {
            ViewModel.StatusMessage = "Select a playlist first to edit.";
            return;
        }

        var doc = ViewModel.LoadPlaylistDocument(target);
        var workingEntries = doc.Entries.ToList();

        var nameBox = new TextBox
        {
            Header = "Playlist Name",
            Text = doc.Name,
        };

        var entriesHeader = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var entriesPanel = new StackPanel { Spacing = 4 };
        var entriesScroll = new ScrollViewer { Content = entriesPanel, MaxHeight = 200 };

        void RebuildEntriesPanel()
        {
            entriesHeader.Text = $"Tracks in Playlist ({workingEntries.Count})";
            entriesPanel.Children.Clear();
            for (int i = 0; i < workingEntries.Count; i++)
            {
                int idx = i;
                var entry = workingEntries[idx];
                var row = new Grid { ColumnSpacing = 4 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = $"{idx + 1}. {entry.DisplayLabel}",
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = entry.IsLocalMatched ? 1.0 : 0.7,
                };
                Grid.SetColumn(label, 0);

                var upBtn = new Button
                {
                    Content = "↑",
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    IsEnabled = idx > 0,
                };
                upBtn.Click += (_, _) =>
                {
                    if (idx > 0)
                    {
                        (workingEntries[idx - 1], workingEntries[idx]) = (workingEntries[idx], workingEntries[idx - 1]);
                        RebuildEntriesPanel();
                    }
                };
                Grid.SetColumn(upBtn, 1);

                var downBtn = new Button
                {
                    Content = "↓",
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    IsEnabled = idx + 1 < workingEntries.Count,
                };
                downBtn.Click += (_, _) =>
                {
                    if (idx + 1 < workingEntries.Count)
                    {
                        (workingEntries[idx + 1], workingEntries[idx]) = (workingEntries[idx], workingEntries[idx + 1]);
                        RebuildEntriesPanel();
                    }
                };
                Grid.SetColumn(downBtn, 2);

                var delBtn = new Button
                {
                    Content = "✕",
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                };
                delBtn.Click += (_, _) =>
                {
                    workingEntries.RemoveAt(idx);
                    RebuildEntriesPanel();
                };
                Grid.SetColumn(delBtn, 3);

                row.Children.Add(label);
                row.Children.Add(upBtn);
                row.Children.Add(downBtn);
                row.Children.Add(delBtn);
                entriesPanel.Children.Add(row);
            }
        }

        var searchBox = new TextBox
        {
            Header = "Add from Local Library · 从本地曲库添加",
            PlaceholderText = "Search by title, artist, album...",
        };
        var searchPanel = new StackPanel { Spacing = 4 };
        var searchScroll = new ScrollViewer { Content = searchPanel, MaxHeight = 130 };

        void RebuildSearchResults()
        {
            searchPanel.Children.Clear();
            var matches = ViewModel.SearchLibraryTracks(searchBox.Text, 25);
            foreach (var tr in matches)
            {
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var lbl = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(tr.ArtistsDisplay) ? tr.Title : $"{tr.ArtistsDisplay} — {tr.Title}",
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(lbl, 0);

                var addBtn = new Button
                {
                    Content = "+ Add",
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11,
                };
                addBtn.Click += (_, _) =>
                {
                    workingEntries.Add(ViewModel.ToPlaylistEntry(tr));
                    RebuildEntriesPanel();
                };
                Grid.SetColumn(addBtn, 1);

                row.Children.Add(lbl);
                row.Children.Add(addBtn);
                searchPanel.Children.Add(row);
            }
        }
        searchBox.TextChanged += (_, _) => RebuildSearchResults();

        var manualBox = new TextBox
        {
            PlaceholderText = "Artist - Title (manual entry)",
        };
        var manualAddBtn = new Button
        {
            Content = "+ Add Line",
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 11,
        };
        manualAddBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(manualBox.Text)) return;
            var parsed = PlaylistImporter.ParseTextPlaylist(manualBox.Text, maxTracks: 20);
            var resolved = PlaylistTrackMatcher.ResolveSeeds(parsed.Tracks, ViewModel.GetLibraryRefs());
            workingEntries.AddRange(resolved);
            manualBox.Text = "";
            RebuildEntriesPanel();
        };
        var manualRow = new Grid { ColumnSpacing = 6 };
        manualRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        manualRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(manualBox, 0);
        Grid.SetColumn(manualAddBtn, 1);
        manualRow.Children.Add(manualBox);
        manualRow.Children.Add(manualAddBtn);

        RebuildEntriesPanel();
        RebuildSearchResults();

        var panel = new StackPanel { Spacing = 8, MinWidth = 540 };
        panel.Children.Add(nameBox);
        panel.Children.Add(entriesHeader);
        panel.Children.Add(entriesScroll);
        panel.Children.Add(searchBox);
        panel.Children.Add(searchScroll);
        panel.Children.Add(manualRow);

        var dialog = new ContentDialog
        {
            Title = $"Edit Playlist · {doc.Name}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            RequestedTheme = RootWorkbench.ActualTheme,
            XamlRoot = RootWorkbench.XamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;

        if (await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
        {
            string newName = string.IsNullOrWhiteSpace(nameBox.Text) ? doc.Name : nameBox.Text.Trim();
            try
            {
                ViewModel.SaveEditedPlaylist(target, newName, workingEntries);
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Save playlist failed: {ex.Message}";
            }
        }
    }

    private async void OnDeletePlaylistClick(object sender, RoutedEventArgs e)
    {
        await DeletePlaylistWithConfirmAsync(ViewModel.SelectedPlaylist);
    }

    private async Task DeletePlaylistWithConfirmAsync(PlaylistSummary? target)
    {
        if (target is null)
        {
            ViewModel.StatusMessage = "Select a playlist first to delete.";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete Playlist · 删除歌单",
            Content = $"Delete playlist '{target.Name}' ({target.TotalCount} tracks)?\nFile: {target.FilePath}\n(Audio files on disk will NOT be deleted.)",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            RequestedTheme = RootWorkbench.ActualTheme,
            XamlRoot = RootWorkbench.XamlRoot,
        };

        if (await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
        {
            try
            {
                ViewModel.DeletePlaylist(target);
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"Delete playlist failed: {ex.Message}";
            }
        }
    }

    private void UpdateSignalPurityDotBrush()
    {
        var color = ViewModel.SignalPurityColorHex switch
        {
            "#A855F7" => Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7),
            "#38BDF8" => Color.FromArgb(0xFF, 0x38, 0xBD, 0xF8),
            "#F59E0B" => Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B),
            _ => Color.FromArgb(0xFF, 0x10, 0xB9, 0x81),
        };
        SignalPurityDot.Fill = new SolidColorBrush(color);
    }

    private void OnNavModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            ViewModel.SetLibraryNavMode(mode);
        }
    }

    private void SyncSubSelectorBarItems()
    {
        _suppressSubTabSync = true;
        try
        {
            PrimarySubSelectorBar.Items.Clear();
            SelectorBarItem? activeItem = null;
            foreach (var opt in ViewModel.CurrentSubTabs)
            {
                var item = new SelectorBarItem
                {
                    Text = opt.Label,
                    Tag = opt.Id,
                };
                PrimarySubSelectorBar.Items.Add(item);
                if (opt.IsActive)
                {
                    activeItem = item;
                }
            }
            PrimarySubSelectorBar.SelectedItem = activeItem ?? PrimarySubSelectorBar.Items.FirstOrDefault();
        }
        finally
        {
            _suppressSubTabSync = false;
        }
    }

    private void OnSubSelectorBarSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_suppressSubTabSync) return;
        if (sender.SelectedItem?.Tag is string subTabId && !string.IsNullOrWhiteSpace(subTabId))
        {
            ViewModel.SelectSubTab(subTabId);
        }
    }

    private void OnPrimaryNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string navId } && !string.IsNullOrWhiteSpace(navId))
        {
            ViewModel.NavigateToPrimary(navId);
        }
    }

    // ---------------- NavigationView (native shell) ----------------

    /// <summary>
    /// Persisted user preference (IsSidebarExpanded) only governs the wide (Expanded) layout.
    /// In Compact / Minimal layouts caused by a narrow window the pane is a transient overlay,
    /// so automatic collapse never overwrites the saved preference.
    /// </summary>
    private void ApplyPaneStateForDisplayMode()
    {
        if (MainNavigationView is null) return;
        MainNavigationView.IsPaneOpen = MainNavigationView.DisplayMode == NavigationViewDisplayMode.Expanded
            && ViewModel.IsSidebarExpanded;
    }

    private void OnNavigationDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        ApplyPaneStateForDisplayMode();
    }

    private void OnTogglePaneClick(object sender, RoutedEventArgs e)
    {
        if (MainNavigationView.DisplayMode == NavigationViewDisplayMode.Expanded)
        {
            ViewModel.ToggleSidebar();
        }
        else
        {
            MainNavigationView.IsPaneOpen = !MainNavigationView.IsPaneOpen;
        }
    }

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_syncingNavSelection) return;
        if (args.IsSettingsInvoked)
        {
            ViewModel.NavigateToPrimary("settings");
            return;
        }
        if (args.InvokedItemContainer?.Tag is string spec && !string.IsNullOrWhiteSpace(spec))
        {
            string[] parts = spec.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                ViewModel.NavigateToPrimary(parts[0], parts[1]);
            else
                ViewModel.NavigateToPrimary(spec);
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNavSelection) return;
        if (args.IsSettingsSelected)
        {
            ViewModel.NavigateToPrimary("settings");
            return;
        }
        if (args.SelectedItemContainer?.Tag is string spec && !string.IsNullOrWhiteSpace(spec))
        {
            string[] parts = spec.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                ViewModel.NavigateToPrimary(parts[0], parts[1]);
            else
                ViewModel.NavigateToPrimary(spec);
        }
    }

    private void SyncNavigationSelection()
    {
        if (MainNavigationView is null) return;
        object? target = ViewModel.PrimaryNavId == "settings"
            ? MainNavigationView.SettingsItem
            : AllNavItems().FirstOrDefault(i => (i.Tag as string) == ViewModel.PrimaryNavId);
        if (ReferenceEquals(MainNavigationView.SelectedItem, target)) return;
        _syncingNavSelection = true;
        try
        {
            MainNavigationView.SelectedItem = target;
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    private IEnumerable<NavigationViewItem> AllNavItems() =>
        MainNavigationView.MenuItems.OfType<NavigationViewItem>()
            .Concat(MainNavigationView.FooterMenuItems.OfType<NavigationViewItem>());

    private void SyncClassicalNavChildren()
    {
        if (NavItemClassical is null) return;
        // Keep NavItemClassical as a flat top-level item; WinUI NavigationView throws E_BOUNDS (0x8000000B)
        // inside ItemsRepeater if SelectedItem jumps to a collapsed child container on invocation.
        if (NavItemClassical.MenuItems.Count > 0)
        {
            NavItemClassical.MenuItems.Clear();
        }
        SyncNavigationSelection();
    }

    // ---------------- Theme ----------------

    private void ApplyAppTheme()
    {
        RootWorkbench.RequestedTheme = ViewModel.AppTheme switch
        {
            "light" => ElementTheme.Light,
            "system" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
        AttachFlyoutThemeSync(LexiconTagMatrixFlyout);
        AttachFlyoutThemeSync(AcousticRackFlyout);
        AttachFlyoutThemeSync(PlaybackQueueFlyout);
        bool isDark = RootWorkbench.ActualTheme == ElementTheme.Dark;
        try
        {
            AppWindow.TitleBar.ButtonForegroundColor = isDark ? Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1) : Color.FromArgb(0xFF, 0x1F, 0x29, 0x37);
            AppWindow.TitleBar.ButtonHoverBackgroundColor = isDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
            AppWindow.TitleBar.ButtonHoverForegroundColor = isDark ? Color.FromArgb(0xFF, 0xD9, 0xB3, 0x72) : Color.FromArgb(0xFF, 0x7E, 0x60, 0x29);
        }
        catch
        {
            // Older hosts without TitleBar customization
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

    // ---------------- Destructive / bulk staging confirmations ----------------

    private async void OnApproveAllHighConfidenceClick(object sender, RoutedEventArgs e)
    {
        int count = ViewModel.GetHighConfidencePendingCount();
        if (count == 0)
        {
            ViewModel.Notify("当前没有置信度 ≥80% 的待审提案。", "warning");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootWorkbench.XamlRoot,
            RequestedTheme = RootWorkbench.ActualTheme,
            Title = "批准高置信度提案",
            Content = $"将把 {count:N0} 条置信度 ≥80% 的待审提案标记为“已批准”。\n\n批准后不会立即写入文件；点击「应用已批准的变更」后按每批 500 条分批写入，可随时取消，并可通过「撤回上一批次」回滚。",
            PrimaryButtonText = $"批准 {count:N0} 条",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
        {
            ViewModel.ApproveAllHighConfidenceCommand.Execute(null);
        }
    }

    private async void OnClearPendingStagingClick(object sender, RoutedEventArgs e)
    {
        var (approved, pending) = ViewModel.GetStagingCounts();
        if (pending == 0)
        {
            ViewModel.Notify("没有可清除的待审提案。", "warning");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = RootWorkbench.XamlRoot,
            RequestedTheme = RootWorkbench.ActualTheme,
            Title = "清除待审提案？",
            Content = $"将永久删除 {pending:N0} 条待审提案及已驳回提案，此操作不可撤销。\n\n已批准的 {approved:N0} 条提案会保留。删除后可重新运行「扫描元数据缺口」生成提案。",
            PrimaryButtonText = $"删除 {pending:N0} 条",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary)
        {
            ViewModel.ClearPendingStagingCommand.Execute(null);
        }
    }

    private bool _syncingEntitySelection;
    private EntityGroupItem? _rightClickedEntityGroup;

    private void OnEntityGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingEntitySelection) return;
        if (sender is ListView { SelectedItem: EntityGroupItem group } &&
            !ReferenceEquals(group, ViewModel.SelectedEntityGroup))
        {
            _syncingEntitySelection = true;
            try
            {
                ViewModel.SelectEntityGroup(group);
            }
            finally
            {
                _syncingEntitySelection = false;
            }
        }
    }

    private void OnEntityBrowserRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is EntityGroupItem rightClicked)
        {
            _rightClickedEntityGroup = rightClicked;
            if (!ReferenceEquals(EntityBrowserListView.SelectedItem, rightClicked))
            {
                EntityBrowserListView.SelectedItem = rightClicked;
            }
        }
    }

    private async void OnSendSelectedEntityGroupToPhoneClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SendEntityGroupToPhoneAsync(ViewModel.SelectedEntityGroup);
    }

    private async void OnContextSendEntityGroupToPhoneClick(object sender, RoutedEventArgs e)
    {
        var group = _rightClickedEntityGroup ?? ViewModel.SelectedEntityGroup;
        _rightClickedEntityGroup = null;
        await ViewModel.SendEntityGroupToPhoneAsync(group);
    }

    private void RootWorkbench_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        bool alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        bool textFocused = IsTextInputFocused();

        if (ctrl && !alt && !shift && e.Key == VirtualKey.S)
        {
            if (_inlineDirtyCount > 0 || (InspectorSplitView?.IsPaneOpen == true && _inlineTargets.Count > 0))
            {
                _ = SaveInlineInspectorMetadataAsync();
                e.Handled = true;
                return;
            }
        }

        if (textFocused)
        {
            return;
        }

        if (ctrl && !alt && !shift && e.Key == VirtualKey.A)
        {
            if (IsTrackListActiveAndPopulated())
            {
                SelectAllTracksPreservingFocus();
                e.Handled = true;
            }
            return;
        }

        if (ctrl && !alt && !shift && e.Key == VirtualKey.Z)
        {
            if (_inlineDirtyCount > 0)
            {
                RevertInlineEditor();
            }
            else
            {
                _ = ViewModel.UndoLastCurationAsync();
            }
            e.Handled = true;
            return;
        }

        if (ctrl && !alt && ((e.Key == VirtualKey.Y && !shift) || (e.Key == VirtualKey.Z && shift)))
        {
            _ = ViewModel.RedoLastCurationAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && !alt && !shift && e.Key == VirtualKey.C)
        {
            if (IsTrackListFocused() && TrackListView is not null && TrackListView.SelectedItems.Count > 0)
            {
                CopySelectedTracksToClipboard();
                e.Handled = true;
            }
            return;
        }

        if (!ctrl && !alt && !shift && e.Key == VirtualKey.Escape)
        {
            if (_inlineDirtyCount > 0)
            {
                RevertInlineEditor();
                e.Handled = true;
                return;
            }
            if (TrackListView is not null && (TrackListView.SelectedItems.Count > 0 || ViewModel.IsMultiSelectMode))
            {
                TrackListView.SelectedItems.Clear();
                if (ViewModel.IsMultiSelectMode)
                {
                    ViewModel.IsMultiSelectMode = false;
                }
                e.Handled = true;
            }
            return;
        }

        if (!ctrl && !alt && !shift && e.Key == VirtualKey.Delete)
        {
            if (IsTrackListFocused()
                && ViewModel.IsViewingPlaylist
                && ViewModel.SelectedPlaylist is not null
                && TrackListView is not null
                && TrackListView.SelectedItems.Count > 0)
            {
                RemoveSelectedTracksFromCurrentPlaylist();
                e.Handled = true;
            }
            return;
        }

        if (!ctrl && !alt && !shift && e.Key == VirtualKey.Enter)
        {
            if (IsTrackListFocused() && TrackListView?.SelectedItem is TrackItem selectedTrack)
            {
                ViewModel.PlayFromBrowseView(selectedTrack);
                e.Handled = true;
            }
            return;
        }

        if (!ctrl && !alt && !shift && e.Key == VirtualKey.Space)
        {
            if (!IsInteractiveNonListControlFocused())
            {
                ViewModel.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void RootWorkbench_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((int)e.Key == 188 &&
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
        {
            ViewModel.NavigateToPrimary("settings");
            e.Handled = true;
        }
    }

    private bool IsTextInputFocused()
    {
        if (RootWorkbench?.XamlRoot is null) return false;
        object? focused = FocusManager.GetFocusedElement(RootWorkbench.XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox)
            return true;
        for (var cur = focused as DependencyObject; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox)
                return true;
        }
        return false;
    }

    private bool IsInteractiveNonListControlFocused()
    {
        if (RootWorkbench?.XamlRoot is null) return false;
        object? focused = FocusManager.GetFocusedElement(RootWorkbench.XamlRoot);
        if (focused is null) return false;
        for (var cur = focused as DependencyObject; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (ReferenceEquals(cur, TrackListView)) return false;
            if (cur is ButtonBase or Slider or ComboBox or ToggleSwitch) return true;
        }
        return false;
    }

    private bool IsTrackListFocused()
    {
        if (RootWorkbench?.XamlRoot is null || TrackListView is null) return false;
        object? focused = FocusManager.GetFocusedElement(RootWorkbench.XamlRoot);
        if (ReferenceEquals(focused, TrackListView)) return true;
        for (var cur = focused as DependencyObject; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (ReferenceEquals(cur, TrackListView)) return true;
        }
        return false;
    }

    private bool IsTrackListActiveAndPopulated() =>
        TrackListView is not null
        && TrackListView.Visibility == Visibility.Visible
        && !ViewModel.IsEntityBrowserVisible
        && ViewModel.FilteredTracks.Count > 0;

    private void SelectAllTracksPreservingFocus()
    {
        if (TrackListView is null || ViewModel.FilteredTracks.Count == 0) return;
        _suppressInspectorFocusSteal = true;
        try
        {
            SetInspectorOpen(true);
            TrackListView.SelectAll();
            TrackListView.Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressInspectorFocusSteal = false;
        }
    }

    private void CopySelectedTracksToClipboard()
    {
        if (TrackListView is null) return;
        var selected = TrackListView.SelectedItems.OfType<TrackItem>().ToList();
        if (selected.Count == 0) return;
        var lines = selected.Select(t =>
            string.IsNullOrWhiteSpace(t.ArtistsDisplay) ? t.Title : $"{t.ArtistsDisplay} - {t.Title}");
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, lines));
        Clipboard.SetContent(package);
        ViewModel.StatusMessage = $"已复制 {selected.Count} 首曲目信息到剪贴板。";
    }

    private void RemoveSelectedTracksFromCurrentPlaylist()
    {
        if (!ViewModel.IsViewingPlaylist || ViewModel.SelectedPlaylist is null || TrackListView is null) return;
        var removeSet = TrackListView.SelectedItems.OfType<TrackItem>().ToHashSet();
        if (removeSet.Count == 0) return;
        var remaining = ViewModel.FilteredTracks
            .Where(t => !removeSet.Contains(t))
            .Select(ViewModel.ToPlaylistEntry)
            .ToList();
        ViewModel.SaveEditedPlaylist(ViewModel.SelectedPlaylist, ViewModel.SelectedPlaylist.Name, remaining);
        ViewModel.Notify($"已从歌单「{ViewModel.SelectedPlaylist.Name}」移除 {removeSet.Count} 首曲目。", "info");
    }

    private void OnToggleInspectorShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetInspectorOpen(!InspectorSplitView.IsPaneOpen);
        args.Handled = true;
    }

    private void OnRootWorkbenchSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InspectorSplitView is null) return;
        var mode = e.NewSize.Width >= 1160 ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        if (InspectorSplitView.DisplayMode != mode)
        {
            InspectorSplitView.DisplayMode = mode;
            ApplyInspectorState();
        }
    }

    // ---------------- Inspector pane state ----------------
    // The persisted preference (IsInspectorOpen) applies only to the wide Inline layout. In a narrow
    // window the inspector is a transient overlay: light-dismiss or auto-collapse never overwrites the preference.

    private bool IsInspectorInline => InspectorSplitView.DisplayMode == SplitViewDisplayMode.Inline;

    private void ApplyInspectorState()
    {
        if (InspectorSplitView is null) return;
        InspectorSplitView.IsPaneOpen = IsInspectorInline && ViewModel.IsInspectorOpen;
        SyncInspectorToggle();
    }

    private void SetInspectorOpen(bool open)
    {
        if (IsInspectorInline)
        {
            ViewModel.IsInspectorOpen = open;
            ApplyInspectorState();
        }
        else
        {
            InspectorSplitView.IsPaneOpen = open;
            SyncInspectorToggle();
        }
    }

    private void SyncInspectorToggle()
    {
        if (InspectorToggleButton is not null)
        {
            InspectorToggleButton.IsChecked = InspectorSplitView.IsPaneOpen;
        }
    }

    private void OnToggleInspectorClick(object sender, RoutedEventArgs e) =>
        SetInspectorOpen(InspectorToggleButton.IsChecked == true);

    private void OnCloseInspectorClick(object sender, RoutedEventArgs e) => SetInspectorOpen(false);

    private void OnInspectorPaneClosed(SplitView sender, object args) => SyncInspectorToggle();

    private void OnGlobalSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.UpdateSearchSuggestions(sender.Text);
            _pendingSearchText = sender.Text;
            if (string.IsNullOrWhiteSpace(sender.Text))
            {
                _searchDebounceTimer.Stop();
                ViewModel.ApplySearchSuggestionOrQuery("");
            }
            else
            {
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }
    }

    private void OnGlobalSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string suggestion)
        {
            _searchDebounceTimer.Stop();
            ViewModel.ApplySearchSuggestionOrQuery(suggestion);
        }
    }

    private void OnGlobalSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounceTimer.Stop();
        string q = args.ChosenSuggestion as string ?? args.QueryText;
        ViewModel.ApplySearchSuggestionOrQuery(q);
    }

    private void OnPrimaryNavShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        string? navId = sender.Key switch
        {
            VirtualKey.Number1 => "library",
            VirtualKey.Number2 => "artists",
            VirtualKey.Number3 => "classical",
            VirtualKey.Number4 => "scenes",
            VirtualKey.Number5 => "playlists",
            VirtualKey.I => "inbox",
            _ => null,
        };
        if (navId is not null)
        {
            ViewModel.NavigateToPrimary(navId);
            args.Handled = true;
        }
    }

    private void OnOpenAcousticRackClick(object sender, RoutedEventArgs e)
    {
        AcousticRackFlyout.ShowAt(SignalPathRackButton);
    }

    private void OnOpenPlaybackQueueClick(object sender, RoutedEventArgs e)
    {
        PlaybackQueueFlyout.ShowAt(PlaybackQueueButton);
        RefreshQueuePlayingHighlights();
        if (ViewModel.CurrentTrack is not null && PlaybackQueueListView is not null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var match = ViewModel.PlaybackQueue.FirstOrDefault(t =>
                        string.Equals(t.TrackUid, ViewModel.CurrentTrack?.TrackUid, StringComparison.Ordinal));
                    if (match is not null) PlaybackQueueListView.ScrollIntoView(match);
                }
                catch { }
            });
        }
    }

    private void OnQuickQueueSlotChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int slotIndex })
        {
            ViewModel.QuickSwitchOrSaveSessionSlot(slotIndex);
            RefreshQueuePlayingHighlights();
        }
    }

    private void OnPlaybackQueueDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ViewModel.OnPlaybackQueueReordered();
        RefreshQueuePlayingHighlights();
    }

    private void OnQueueListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem container)
        {
            ApplyQueueRowHighlight(container, args.Item as TrackItem);
        }
    }

    private void RefreshQueuePlayingHighlights()
    {
        if (PlaybackQueueListView is null) return;
        for (int i = 0; i < ViewModel.PlaybackQueue.Count; i++)
        {
            if (PlaybackQueueListView.ContainerFromIndex(i) is ListViewItem container)
            {
                ApplyQueueRowHighlight(container, ViewModel.PlaybackQueue[i]);
            }
        }
    }

    private void ApplyQueueRowHighlight(ListViewItem container, TrackItem? track)
    {
        if (container.ContentTemplateRoot is not FrameworkElement root) return;
        bool isPlayingRow = track is not null
            && ViewModel.CurrentTrack is not null
            && string.Equals(track.TrackUid, ViewModel.CurrentTrack.TrackUid, StringComparison.Ordinal);

        if (root.FindName("QueuePlayingGlyph") is FrameworkElement glyph)
        {
            glyph.Visibility = isPlayingRow ? Visibility.Visible : Visibility.Collapsed;
        }
        if (root.FindName("QueueTitleText") is TextBlock titleText)
        {
            if (isPlayingRow)
            {
                titleText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0xB3, 0x72));
            }
            else
            {
                bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
                titleText.Foreground = new SolidColorBrush(isLight
                    ? Color.FromArgb(0xFF, 0x1E, 0x29, 0x3B)
                    : Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0));
            }
        }
    }

    private void OnTransportTrackInfoClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentTrack is null) return;
        ViewModel.InspectTrack(ViewModel.CurrentTrack);
        SetInspectorOpen(true);
    }

    private void OnTransportArtistClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigateToCurrentTrackArtist();
    }

    private void OnPlayQueueTrackClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackItem track)
        {
            ViewModel.PlayQueueItem(track);
        }
    }

    private void OnRemoveQueueTrackClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackItem track)
        {
            ViewModel.RemoveQueueItem(track);
        }
    }

    private void OnSelectEnergyArcClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string arcId } && !string.IsNullOrWhiteSpace(arcId))
        {
            ViewModel.SetEnergyArc(arcId);
        }
    }

    private async void OnFetchMissingBasicInfoClick(object sender, RoutedEventArgs e)
    {
        var targets = _inlineTargets.Count > 0
            ? _inlineTargets
            : (ViewModel.InspectedTrack is not null ? [ViewModel.InspectedTrack] : Array.Empty<TrackItem>());
        await ViewModel.FetchMissingBasicInfoForTracksAsync(targets);
        _coverThumbCache.Clear();
        SyncInlineInspectorAfterEnrichment(targets);
    }

    private async void OnFetchMissing5DTagsClick(object sender, RoutedEventArgs e)
    {
        var targets = _inlineTargets.Count > 0
            ? _inlineTargets
            : (ViewModel.InspectedTrack is not null ? [ViewModel.InspectedTrack] : Array.Empty<TrackItem>());
        await ViewModel.FetchMissing5DTagsForTracksAsync(targets);
        SyncInlineInspectorAfterEnrichment(targets);
    }

    private void SyncInlineInspectorAfterEnrichment(IReadOnlyList<TrackItem> previousTargets)
    {
        if (previousTargets.Count == 0) return;
        var uidSet = previousTargets.Select(t => t.TrackUid).ToHashSet(StringComparer.Ordinal);
        var refreshed = ViewModel.FilteredTracks.Where(t => uidSet.Contains(t.TrackUid)).ToList();
        if (refreshed.Count == 0 && ViewModel.InspectedTrack is not null && uidSet.Contains(ViewModel.InspectedTrack.TrackUid))
        {
            refreshed = [ViewModel.InspectedTrack];
        }
        if (refreshed.Count > 0)
        {
            SyncInlineInspectorTargets(refreshed);
        }
        UpdateCoverArtImage(InspectedCoverImage, ViewModel.InspectedTrack?.CoverArtPath);
        UpdateCoverArtImage(TransportCoverImage, ViewModel.CurrentTrack?.CoverArtPath);
    }

    private readonly Dictionary<string, BitmapImage> _coverThumbCache = new(StringComparer.OrdinalIgnoreCase);

    private BitmapImage? GetOrCreateRowCoverBitmap(string? coverArtPath)
    {
        if (string.IsNullOrWhiteSpace(coverArtPath) || !File.Exists(coverArtPath)) return null;
        if (_coverThumbCache.TryGetValue(coverArtPath, out var existing)) return existing;
        try
        {
            if (_coverThumbCache.Count >= 600)
            {
                _coverThumbCache.Clear();
            }
            var uri = new Uri(Path.GetFullPath(coverArtPath));
            var bmp = new BitmapImage
            {
                DecodePixelWidth = 80,
                UriSource = uri,
            };
            _coverThumbCache[coverArtPath] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static Image? FindDescendantImage(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image img) return img;
            var nested = FindDescendantImage(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnTrackListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is not FrameworkElement root) return;
        var rowImage = (root.FindName("RowCoverImage") as Image) ?? FindDescendantImage(root);
        if (rowImage is null) return;

        if (args.InRecycleQueue)
        {
            rowImage.Source = null;
            return;
        }

        if (args.Phase == 0)
        {
            rowImage.Source = args.Item is TrackItem track
                ? GetOrCreateRowCoverBitmap(track.CoverArtPath)
                : null;
        }
    }

    private void OnCreditChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entity } && !string.IsNullOrWhiteSpace(entity))
        {
            ViewModel.FilterByCreditEntity(entity);
        }
    }

    private bool _restoringSelection;
    private bool _suppressInspectorFocusSteal;
    private int _trackSelectionAnchorIndex = -1;
    private HashSet<string>? _pendingRestoreSelectedUids;
    private double? _pendingRestoreScrollOffset;
    private bool _isSavingInlineMetadata;

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject? parent)
    {
        if (parent is null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var nested = FindDescendantScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void BeginPreserveTrackListViewportAndSelection()
    {
        if (TrackListView is null) return;
        _pendingRestoreSelectedUids = TrackListView.SelectedItems
            .OfType<TrackItem>()
            .Select(t => t.TrackUid)
            .ToHashSet(StringComparer.Ordinal);
        _pendingRestoreScrollOffset = FindDescendantScrollViewer(TrackListView)?.VerticalOffset;
        _restoringSelection = true;
    }

    private void EndPreserveTrackListViewportAndSelection(IReadOnlyCollection<string> updatedUids)
    {
        if (TrackListView is null)
        {
            _restoringSelection = false;
            return;
        }
        try
        {
            var restoreUids = _pendingRestoreSelectedUids;
            if (restoreUids is { Count: > 0 })
            {
                var restored = new List<TrackItem>(restoreUids.Count);
                // Re-add only items that were deselected when their TrackItem record instance was replaced
                var currentlySelectedUids = TrackListView.SelectedItems
                    .OfType<TrackItem>()
                    .Select(t => t.TrackUid)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var item in ViewModel.FilteredTracks)
                {
                    if (restoreUids.Contains(item.TrackUid))
                    {
                        restored.Add(item);
                        if (!currentlySelectedUids.Contains(item.TrackUid))
                        {
                            TrackListView.SelectedItems.Add(item);
                        }
                    }
                }
                if (restored.Count > 0)
                {
                    SyncInlineInspectorTargets(restored);
                }
            }

            if (_pendingRestoreScrollOffset is double targetOffset)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    FindDescendantScrollViewer(TrackListView)?.ChangeView(null, targetOffset, null, disableAnimation: true);
                });
            }
        }
        finally
        {
            _pendingRestoreSelectedUids = null;
            _pendingRestoreScrollOffset = null;
            _restoringSelection = false;
        }
    }

    private void OnTrackListPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TrackListView is null) return;
        var clickedTrack = (e.OriginalSource as FrameworkElement)?.DataContext as TrackItem;
        if (clickedTrack is null) return;
        int clickedIdx = ViewModel.FilteredTracks.IndexOf(clickedTrack);
        if (clickedIdx < 0) return;

        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (TrackListView.SelectionMode == ListViewSelectionMode.Multiple && shift && _trackSelectionAnchorIndex >= 0)
        {
            int start = Math.Min(_trackSelectionAnchorIndex, clickedIdx);
            int end = Math.Max(_trackSelectionAnchorIndex, clickedIdx);
            _restoringSelection = true;
            try
            {
                for (int i = start; i <= end; i++)
                {
                    var item = ViewModel.FilteredTracks[i];
                    if (!TrackListView.SelectedItems.Contains(item))
                    {
                        TrackListView.SelectedItems.Add(item);
                    }
                }
            }
            finally
            {
                _restoringSelection = false;
            }
            var selected = TrackListView.SelectedItems.OfType<TrackItem>().ToList();
            SyncInlineInspectorTargets(selected);
            e.Handled = true;
            return;
        }

        if (!shift)
        {
            _trackSelectionAnchorIndex = clickedIdx;
        }
    }

    private void OnTrackColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string colId } && !string.IsNullOrWhiteSpace(colId))
        {
            ViewModel.ToggleTrackSort(colId);
            if (TrackListView is not null && ViewModel.FilteredTracks.Count > 0)
            {
                TrackListView.UpdateLayout();
                TrackListView.ScrollIntoView(ViewModel.FilteredTracks[0]);
            }
        }
    }

    private void OnMultiSelectToggleClick(object sender, RoutedEventArgs e)
    {
        if (TrackListView is null) return;
        TrackListView.SelectionMode = MultiSelectToggleButton.IsChecked == true
            ? ListViewSelectionMode.Multiple
            : ListViewSelectionMode.Extended;
    }

    private void OnSelectAllTracksClick(object sender, RoutedEventArgs e) =>
        SelectAllTracksPreservingFocus();

    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return;
        bool wasListFocused = IsTrackListFocused();
        var selected = TrackListView.SelectedItems.OfType<TrackItem>().ToList();
        if (selected.Count == 1)
        {
            int idx = ViewModel.FilteredTracks.IndexOf(selected[0]);
            if (idx >= 0 && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != CoreVirtualKeyStates.Down)
            {
                _trackSelectionAnchorIndex = idx;
            }
            ViewModel.InspectTrack(selected[0]);
        }
        else if (selected.Count == 0)
        {
            if (TrackListView.SelectedItem is TrackItem single)
            {
                selected = [single];
                ViewModel.InspectTrack(single);
            }
            else
            {
                ViewModel.InspectTrack(null);
            }
        }
        else if (selected.Count > 1)
        {
            UpdateCoverArtImage(InspectedCoverImage, selected[0].CoverArtPath);
            if (InspectorSplitView is not null && !InspectorSplitView.IsPaneOpen)
            {
                SetInspectorOpen(true);
                if (wasListFocused && !_suppressInspectorFocusSteal)
                {
                    TrackListView?.Focus(FocusState.Programmatic);
                }
            }
        }
        SyncInlineInspectorTargets(selected);
    }

    private void OnTrackListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackItem rightClicked &&
            !TrackListView.SelectedItems.Contains(rightClicked))
        {
            TrackListView.SelectedItem = rightClicked;
        }
    }

    private void OnTrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TrackListView.SelectedItem is TrackItem track)
        {
            ViewModel.PlayFromBrowseView(track);
        }
    }

    private TrackItem? ResolveContextTrack(object sender) =>
        (sender as FrameworkElement)?.DataContext as TrackItem ?? TrackListView.SelectedItem as TrackItem;

    private IReadOnlyList<TrackItem> ResolveTargetTracksForAction(object sender)
    {
        var clicked = ResolveContextTrack(sender);
        var selected = TrackListView.SelectedItems.OfType<TrackItem>().ToList();
        if (selected.Count > 1 && (clicked is null || selected.Any(s => s.TrackUid == clicked.TrackUid)))
        {
            return selected;
        }
        if (clicked is not null) return [clicked];
        if (selected.Count > 0) return selected;
        if (ViewModel.InspectedTrack is not null) return [ViewModel.InspectedTrack];
        if (ViewModel.CurrentTrack is not null) return [ViewModel.CurrentTrack];
        return [];
    }

    private void OnContextPlayTrackClick(object sender, RoutedEventArgs e)
    {
        if (ResolveContextTrack(sender) is TrackItem track)
        {
            ViewModel.PlayFromBrowseView(track);
        }
    }

    private void OnContextEnqueueNextClick(object sender, RoutedEventArgs e)
    {
        var targets = ResolveTargetTracksForAction(sender);
        if (targets.Count > 0)
        {
            ViewModel.EnqueueTracksNext(targets);
        }
    }

    private void OnContextEnqueueEndClick(object sender, RoutedEventArgs e)
    {
        var targets = ResolveTargetTracksForAction(sender);
        if (targets.Count > 0)
        {
            ViewModel.EnqueueTracksToEnd(targets);
        }
    }

    private void OnContextToggleLovedClick(object sender, RoutedEventArgs e)
    {
        var targets = ResolveTargetTracksForAction(sender);
        if (targets.Count > 0)
        {
            ViewModel.ToggleLovedForTracks(targets);
        }
    }

    private void OnContextTagMatrixClick(object sender, RoutedEventArgs e)
    {
        ShowLexiconTagMatrixFlyout(ResolveTargetTracksForAction(sender));
    }

    private void OnContextAddToPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (ResolveContextTrack(sender) is TrackItem track)
        {
            ViewModel.AddTrackToCurrentOrNamedPlaylist(track);
        }
    }

    private void OnContextRevealInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (ResolveContextTrack(sender) is TrackItem track && !string.IsNullOrWhiteSpace(track.FullPath) && File.Exists(track.FullPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{track.FullPath}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"打开文件所在位置失败: {ex.Message}";
            }
        }
    }

    private void OnOpenLexiconTagMatrixClick(object sender, RoutedEventArgs e)
    {
        ShowLexiconTagMatrixFlyout(ResolveTargetTracksForAction(sender));
    }

    private void OnOpenF2BasicMetadataClick(object sender, RoutedEventArgs e)
    {
        FocusInlineInspectorEditor(ResolveTargetTracksForAction(sender));
    }

    private void OnContextF2BasicMetadataClick(object sender, RoutedEventArgs e)
    {
        FocusInlineInspectorEditor(ResolveTargetTracksForAction(sender));
    }

    private async void OnToolbarSendToPhoneClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SendTracksToPhoneAsync(ResolveTargetTracksForAction(sender));
    }

    private async void OnContextSendTracksToPhoneClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SendTracksToPhoneAsync(ResolveTargetTracksForAction(sender));
    }

    private void OnOpenLexiconTagShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(RootWorkbench.XamlRoot) is TextBox or PasswordBox or RichEditBox)
            return;

        args.Handled = true;
        ShowLexiconTagMatrixFlyout(ResolveTargetTracksForAction(this));
    }

    private void OnF2BasicMetadataShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(RootWorkbench.XamlRoot) is TextBox or PasswordBox or RichEditBox)
            return;

        args.Handled = true;
        FocusInlineInspectorEditor(ResolveTargetTracksForAction(this));
    }

    private void OnFocusGlobalSearchShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        GlobalSearchBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void FocusInlineInspectorEditor(IReadOnlyList<TrackItem> targets)
    {
        if (targets.Count == 0 && ViewModel.FilteredTracks.Count > 0 && TrackListView is not null)
        {
            var fallback = ViewModel.FilteredTracks[0];
            TrackListView.SelectedItem = fallback;
            targets = [fallback];
        }
        if (targets.Count == 0)
        {
            ViewModel.Notify("当前列表为空，请先加载或切换包含曲目的视图后再编辑属性。", "warning");
            return;
        }
        SetInspectorOpen(true);
        SyncInlineInspectorTargets(targets);
        InlineBasicExpander.IsExpanded = true;
        var focusTarget = targets.Count == 1 ? InlineTitleBox : InlineAlbumBox;
        focusTarget.Focus(FocusState.Programmatic);
        focusTarget.SelectAll();
    }

    private static void UpdateCoverArtImage(Image? target, string? coverArtPath)
    {
        if (target is null) return;
        if (string.IsNullOrWhiteSpace(coverArtPath) || !File.Exists(coverArtPath))
        {
            target.Source = null;
            return;
        }

        try
        {
            var uri = new Uri(Path.GetFullPath(coverArtPath));
            var bmp = new BitmapImage
            {
                DecodePixelWidth = 160,
                UriSource = uri,
            };
            target.Source = bmp;
        }
        catch
        {
            target.Source = null;
        }
    }

    private static void SelectComboByTag(ComboBox? combo, string targetTag)
    {
        if (combo is null) return;
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item
                && item.Visibility == Visibility.Visible
                && string.Equals(item.Tag as string, targetTag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Visibility == Visibility.Visible);
    }

    private static string? GetComboSelectedTag(ComboBox? combo) =>
        (combo?.SelectedItem as ComboBoxItem)?.Tag as string;

    // ---------------- Inline inspector editor state ----------------

    private IReadOnlyList<TrackItem> _inlineTargets = [];
    private bool _syncingInlineEditor;
    private int _inlineDirtyCount;

    private void SyncInlineInspectorTargets(IReadOnlyList<TrackItem> targets)
    {
        if (InlineTitleBox is null) return;

        bool sameTargets = targets.Count == _inlineTargets.Count
            && targets.Select(t => t.TrackUid).SequenceEqual(_inlineTargets.Select(t => t.TrackUid), StringComparer.Ordinal);
        if (sameTargets && _inlineDirtyCount > 0)
        {
            _inlineTargets = targets.ToList();
            return;
        }
        if (!sameTargets && _inlineDirtyCount > 0 && _inlineTargets.Count > 0)
        {
            string label = _inlineTargets.Count == 1 ? $"「{_inlineTargets[0].Title}」" : $"{_inlineTargets.Count} 首曲目";
            ViewModel.Notify($"已放弃对{label}的 {_inlineDirtyCount} 项未保存修改。", "warning");
        }

        _inlineTargets = targets.ToList();
        ViewModel.SetHasInlineTarget(_inlineTargets.Count > 0);
        _syncingInlineEditor = true;
        try
        {
            PopulateInlineEditor(targets);
        }
        finally
        {
            _syncingInlineEditor = false;
        }
        UpdateInlineDirtyState();
    }

    private void PopulateInlineEditor(IReadOnlyList<TrackItem> targets)
    {
        TextBox[] textBoxes =
        [
            InlineTitleBox, InlineArtistsBox, InlineAlbumBox, InlineComposersBox, InlineConductorsBox,
            InlineYearBox, InlinePublisherBox, InlineGenreBox, InlineWorkBox, InlineMovementBox,
        ];
        bool batch = targets.Count > 1;
        var keepVisibility = batch ? Visibility.Visible : Visibility.Collapsed;
        InlineTempoBucketKeepItem.Visibility = keepVisibility;
        InlineVocalsKeepItem.Visibility = keepVisibility;
        InlineEnergyKeepItem.Visibility = keepVisibility;

        bool editable = targets.Count > 0;
        foreach (var tb in textBoxes) tb.IsEnabled = editable;
        InlineTempoBpmBox.IsEnabled = editable;
        InlineTempoBucketCombo.IsEnabled = editable;
        InlineVocalsCombo.IsEnabled = editable;
        InlineEnergyCombo.IsEnabled = editable;
        InlineSceneBox.IsEnabled = editable;
        InlineMoodBox.IsEnabled = editable;
        InlineRatingControl.IsEnabled = editable;
        if (InlineFetchBasicInfoButton is not null) InlineFetchBasicInfoButton.IsEnabled = editable;
        if (InlineFetch5DTagsButton is not null) InlineFetch5DTagsButton.IsEnabled = editable;

        if (targets.Count == 0)
        {
            InlineEditModeText.Text = "未选中曲目：在曲目表中选择一首或多首后可编辑";
            if (InlineTrackTechSummaryText is not null) InlineTrackTechSummaryText.Text = "";
            foreach (var tb in textBoxes)
            {
                tb.Text = "";
                tb.PlaceholderText = "";
            }
            InlineTempoBpmBox.Value = double.NaN;
            InlineSceneBox.Text = "";
            InlineMoodBox.Text = "";
            InlineRatingControl.Value = -1;
            SelectComboByTag(InlineTempoBucketCombo, "");
            SelectComboByTag(InlineVocalsCombo, "unknown");
            SelectComboByTag(InlineEnergyCombo, "");
            InlineClassicalExpander.IsExpanded = false;
            return;
        }

        if (!batch)
        {
            var first = targets[0];
            InlineEditModeText.Text = "单曲编辑 · Enter 保存 · Esc 还原";
            if (InlineTrackTechSummaryText is not null)
            {
                InlineTrackTechSummaryText.Text =
                    $"{first.SampleRateHz / 1000.0:0.#} kHz · {first.DurationDisplay} · {first.ReplayGainBadge} · 播放 {first.EffectivePlayCount} 次"
                    + (string.IsNullOrWhiteSpace(first.Genre) ? "" : $" · {first.Genre}");
            }
            InlineTitleBox.PlaceholderText = "";
            InlineTitleBox.Text = first.Title;
            InlineArtistsBox.PlaceholderText = "多名艺人用 ; 或 / 分隔";
            InlineArtistsBox.Text = first.ArtistsDisplay;
            InlineAlbumBox.PlaceholderText = "";
            InlineAlbumBox.Text = first.Album;
            InlineComposersBox.PlaceholderText = "多名作曲家用 ; 分隔";
            InlineComposersBox.Text = first.ComposersDisplay;
            InlineConductorsBox.PlaceholderText = "多名指挥家用 ; 分隔";
            InlineConductorsBox.Text = first.ConductorsDisplay;
            InlineYearBox.PlaceholderText = "如 1986";
            InlineYearBox.Text = first.Year.HasValue && first.Year.Value > 0 ? first.Year.Value.ToString() : "";
            InlinePublisherBox.PlaceholderText = "如 Deutsche Grammophon";
            InlinePublisherBox.Text = first.Publisher;
            InlineGenreBox.PlaceholderText = "";
            InlineGenreBox.Text = first.Genre;
            InlineWorkBox.PlaceholderText = "如 Symphony No. 5";
            InlineWorkBox.Text = first.WorkName;
            InlineMovementBox.PlaceholderText = "如 I. Allegro con brio";
            InlineMovementBox.Text = first.MovementName;
            InlineTempoBpmBox.PlaceholderText = "未设置";
            InlineTempoBpmBox.Value = first.TempoBpm is > 0 ? Math.Round(first.TempoBpm.Value, 1) : double.NaN;
            InlineSceneBox.PlaceholderText = "focus; coding; relax";
            InlineSceneBox.Text = string.Join("; ", first.DjScene);
            InlineMoodBox.PlaceholderText = "calm; warm; epic";
            InlineMoodBox.Text = string.Join("; ", first.Moods);
            InlineRatingControl.Value = first.Rating > 0 ? Math.Clamp(first.Rating, 1, 5) : -1;

            SelectComboByTag(InlineTempoBucketCombo, string.IsNullOrWhiteSpace(first.TempoBucket) ? "" : first.TempoBucket);
            SelectComboByTag(InlineVocalsCombo, string.IsNullOrWhiteSpace(first.DjVocals) ? "unknown" : first.DjVocals);
            SelectComboByTag(InlineEnergyCombo, string.IsNullOrWhiteSpace(first.DjEnergy) ? "" : first.DjEnergy);

            InlineClassicalExpander.IsExpanded = TrackItem.IsClassicalGenre(first.Genre);
        }
        else
        {
            const string mixedHint = "（多项不同值，输入新值统一覆盖）";
            ViewModel.InspectorHeaderTitle = $"已选中 {targets.Count} 首曲目";
            string? sharedArtistHeader = GetSharedValue(targets, t => t.ArtistsDisplay);
            string? sharedAlbumHeader = GetSharedValue(targets, t => t.Album);
            ViewModel.InspectorHeaderSubtitle = !string.IsNullOrWhiteSpace(sharedArtistHeader)
                ? $"艺人：{sharedArtistHeader}"
                : "包含多位艺人 · 修改任意字段将批量写入所选曲目";
            ViewModel.InspectorHeaderSubline = !string.IsNullOrWhiteSpace(sharedAlbumHeader)
                ? $"专辑：{sharedAlbumHeader}"
                : string.Join(" / ", targets.Take(3).Select(t => t.Title));

            InlineEditModeText.Text = $"批量编辑 {targets.Count} 首 · 仅改动字段写入所选全部曲目";
            if (InlineTrackTechSummaryText is not null)
            {
                InlineTrackTechSummaryText.Text = $"批量模式 · 共选中 {targets.Count} 首（共有字段已预填）";
            }

            static void FillSharedBox(TextBox box, IReadOnlyList<TrackItem> list, Func<TrackItem, string> selector, string hint)
            {
                string? shared = GetSharedValue(list, selector);
                box.Text = shared ?? "";
                box.PlaceholderText = shared is null ? hint : "";
            }

            InlineTitleBox.IsEnabled = false;
            InlineTitleBox.Text = "";
            InlineTitleBox.PlaceholderText = "标题仅支持单曲编辑";

            FillSharedBox(InlineArtistsBox, targets, t => t.ArtistsDisplay, mixedHint);
            FillSharedBox(InlineAlbumBox, targets, t => t.Album, mixedHint);
            FillSharedBox(InlineComposersBox, targets, t => t.ComposersDisplay, mixedHint);
            FillSharedBox(InlineConductorsBox, targets, t => t.ConductorsDisplay, mixedHint);
            FillSharedBox(InlineYearBox, targets, t => t.Year.HasValue && t.Year.Value > 0 ? t.Year.Value.ToString() : "", mixedHint);
            FillSharedBox(InlinePublisherBox, targets, t => t.Publisher, mixedHint);
            FillSharedBox(InlineGenreBox, targets, t => t.Genre, mixedHint);
            FillSharedBox(InlineWorkBox, targets, t => t.WorkName, mixedHint);
            FillSharedBox(InlineMovementBox, targets, t => t.MovementName, mixedHint);

            InlineTempoBpmBox.Value = double.NaN;
            InlineTempoBpmBox.PlaceholderText = "留空保持原值";

            string? sharedScene = GetSharedValue(targets, t => string.Join("; ", t.DjScene));
            InlineSceneBox.Text = sharedScene ?? "";
            InlineSceneBox.PlaceholderText = sharedScene is null ? mixedHint : "";

            string? sharedMood = GetSharedValue(targets, t => string.Join("; ", t.Moods));
            InlineMoodBox.Text = sharedMood ?? "";
            InlineMoodBox.PlaceholderText = sharedMood is null ? mixedHint : "";

            InlineRatingControl.Value = -1;
            SelectComboByTag(InlineTempoBucketCombo, "__keep__");
            SelectComboByTag(InlineVocalsCombo, "__keep__");
            SelectComboByTag(InlineEnergyCombo, "__keep__");
            InlineClassicalExpander.IsExpanded = targets.Any(t => TrackItem.IsClassicalGenre(t.Genre));
        }
    }

    private static string? GetSharedValue(IReadOnlyList<TrackItem> items, Func<TrackItem, string> selector)
    {
        if (items.Count == 0) return null;
        string first = selector(items[0]).Trim();
        for (int i = 1; i < items.Count; i++)
        {
            if (!string.Equals(selector(items[i]).Trim(), first, StringComparison.Ordinal))
            {
                return null;
            }
        }
        return first;
    }

    private void UpdateInlineDirtyState()
    {
        if (InlineSaveButton is null) return;
        var updates = BuildInlineUpdates(_inlineTargets, out _);
        _inlineDirtyCount = updates?.Count ?? 0;
        bool hasError = updates is null && _inlineTargets.Count > 0;
        InlineSaveButton.IsEnabled = _inlineDirtyCount > 0 || hasError;
        InlineSaveButton.Content = _inlineDirtyCount > 0 ? $"保存 {_inlineDirtyCount} 项更改" : "保存";
        InlineRevertButton.IsEnabled = _inlineDirtyCount > 0 || hasError;
    }

    private void OnInlineFieldTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingInlineEditor)
        {
            if (ReferenceEquals(sender, InlineGenreBox) && InlineClassicalExpander is not null)
            {
                InlineClassicalExpander.IsExpanded = TrackItem.IsClassicalGenre(InlineGenreBox.Text);
            }
            UpdateInlineDirtyState();
        }
    }

    private void OnInlineFieldSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingInlineEditor) UpdateInlineDirtyState();
    }

    private void OnInlineNumberBoxValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_syncingInlineEditor) UpdateInlineDirtyState();
    }

    private void OnInlineRatingValueChanged(RatingControl sender, object args)
    {
        if (!_syncingInlineEditor) UpdateInlineDirtyState();
    }

    private void OnInspectorEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _inlineDirtyCount > 0)
        {
            RevertInlineEditor();
            e.Handled = true;
        }
    }

    private void OnRevertInlineInspectorClick(object sender, RoutedEventArgs e) => RevertInlineEditor();

    private void RevertInlineEditor()
    {
        _inlineDirtyCount = 0;
        SyncInlineInspectorTargets(_inlineTargets);
        ViewModel.Notify("已还原未保存的修改。", "info");
    }

    // ---------------- Tag AutoSuggest (scene / mood vocabulary) ----------------

    private static (string Prefix, string Token, HashSet<string> Existing) SplitTagInput(string text)
    {
        int idx = text.LastIndexOf(';');
        string prefix = idx >= 0 ? text[..(idx + 1)] : "";
        string token = (idx >= 0 ? text[(idx + 1)..] : text).Trim();
        var existing = new HashSet<string>(
            prefix.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        return (prefix, token, existing);
    }

    private void OnInlineTagBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var (_, token, existing) = SplitTagInput(sender.Text ?? "");
            var vocabulary = ReferenceEquals(sender, InlineSceneBox) ? ViewModel.GetKnownSceneTags() : ViewModel.GetKnownMoodTags();
            sender.ItemsSource = vocabulary
                .Where(v => !existing.Contains(v)
                            && (token.Length == 0 || v.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(v => token.Length > 0 && v.StartsWith(token, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Take(8)
                .ToList();
        }
        if (!_syncingInlineEditor) UpdateInlineDirtyState();
    }

    private void OnInlineTagBoxSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string chosen) return;
        var (prefix, _, _) = SplitTagInput(sender.Text ?? "");
        string head = prefix.Trim();
        sender.Text = (string.IsNullOrEmpty(head) ? "" : head + " ") + chosen + "; ";
    }

    private void OnInlineTagBoxQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is null)
        {
            _ = SaveInlineInspectorMetadataAsync();
        }
    }

    private void OnInlineEditorFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _ = SaveInlineInspectorMetadataAsync();
            e.Handled = true;
        }
    }

    private void OnSaveInlineInspectorMetadataClick(object sender, RoutedEventArgs e)
    {
        _ = SaveInlineInspectorMetadataAsync();
    }

    private static string NormalizeTagList(string text) =>
        string.Join("; ", text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Computes the field updates implied by the editor relative to the original track(s).
    /// Returns null (with <paramref name="error"/>) when a field fails validation.
    /// </summary>
    private Dictionary<string, string?>? BuildInlineUpdates(IReadOnlyList<TrackItem> targets, out string? error)
    {
        error = null;
        var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (targets.Count == 0) return updates;

        bool isSingle = targets.Count == 1;
        var first = targets[0];

        void TextField(TextBox box, Func<TrackItem, string> selector, string key)
        {
            string v = box.Text.Trim();
            if (isSingle)
            {
                if (v != selector(first).Trim()) updates[key] = v;
                return;
            }
            string? shared = GetSharedValue(targets, selector);
            if (shared is not null)
            {
                if (v != shared && !string.IsNullOrWhiteSpace(v)) updates[key] = v;
            }
            else if (!string.IsNullOrWhiteSpace(v))
            {
                updates[key] = v;
            }
        }

        if (isSingle && InlineTitleBox.Text.Trim() != first.Title)
            updates["title"] = InlineTitleBox.Text.Trim();
        TextField(InlineArtistsBox, t => t.ArtistsDisplay, "artists_json");
        TextField(InlineAlbumBox, t => t.Album, "album");
        TextField(InlineComposersBox, t => t.ComposersDisplay, "composers_json");
        TextField(InlineConductorsBox, t => t.ConductorsDisplay, "conductors_json");

        string yearText = InlineYearBox.Text.Trim();
        string? origYearShared = isSingle
            ? (first.Year.HasValue && first.Year.Value > 0 ? first.Year.Value.ToString() : "")
            : GetSharedValue(targets, t => t.Year.HasValue && t.Year.Value > 0 ? t.Year.Value.ToString() : "");
        bool yearChanged = isSingle
            ? yearText != (origYearShared ?? "")
            : (origYearShared is not null ? (yearText != origYearShared && !string.IsNullOrWhiteSpace(yearText)) : !string.IsNullOrWhiteSpace(yearText));
        if (yearChanged)
        {
            if (string.IsNullOrEmpty(yearText) && isSingle)
                updates["year"] = null;
            else if (int.TryParse(yearText, out int y) && y is >= 1000 and <= 2100)
                updates["year"] = y.ToString();
            else
            {
                error = $"年份格式无效“{yearText}”，请输入 1000–2100 之间的四位数字。";
                return null;
            }
        }

        TextField(InlinePublisherBox, t => t.Publisher, "publisher");
        TextField(InlineGenreBox, t => t.Genre, "genre");
        TextField(InlineWorkBox, t => t.WorkName, "work_name");
        TextField(InlineMovementBox, t => t.MovementName, "movement_name");

        double bpmValue = InlineTempoBpmBox.Value;
        double? origBpm = first.TempoBpm is > 0 ? Math.Round(first.TempoBpm.Value, 1) : null;
        double? parsedBpm = null;
        if (!double.IsNaN(bpmValue))
        {
            parsedBpm = Math.Round(bpmValue, 1);
            if (!isSingle || origBpm is null || Math.Abs(parsedBpm.Value - origBpm.Value) > 0.05)
                updates["tempo_bpm"] = parsedBpm.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (isSingle && origBpm is not null)
        {
            updates["tempo_bpm"] = null;
        }

        string? bucketTag = GetComboSelectedTag(InlineTempoBucketCombo);
        if (bucketTag is not null && bucketTag != "__keep__")
        {
            string origBucket = first.TempoBucket ?? "";
            if (!isSingle || bucketTag != origBucket)
                updates["tempo_bucket"] = bucketTag == "" ? null : bucketTag;
        }
        if (parsedBpm.HasValue && updates.ContainsKey("tempo_bpm") && !updates.ContainsKey("tempo_bucket")
            && (!isSingle || string.IsNullOrWhiteSpace(first.TempoBucket)))
        {
            updates["tempo_bucket"] = Acoustic5DInferenceService.MapBpmToTempoBucket(parsedBpm);
        }

        string? vocalsTag = GetComboSelectedTag(InlineVocalsCombo);
        if (vocalsTag is not null && vocalsTag != "__keep__")
        {
            string origVocals = string.IsNullOrWhiteSpace(first.DjVocals) ? "unknown" : first.DjVocals;
            if (!isSingle || !string.Equals(vocalsTag, origVocals, StringComparison.OrdinalIgnoreCase))
                updates["dj_vocals"] = vocalsTag;
        }

        string? energyTag = GetComboSelectedTag(InlineEnergyCombo);
        if (energyTag is not null && energyTag != "__keep__")
        {
            string origEnergy = first.DjEnergy ?? "";
            if (!isSingle || !string.Equals(energyTag, origEnergy, StringComparison.OrdinalIgnoreCase))
                updates["dj_energy"] = energyTag == "" ? null : energyTag;
        }

        string sceneText = NormalizeTagList(InlineSceneBox.Text ?? "");
        string? origSceneShared = isSingle
            ? NormalizeTagList(string.Join("; ", first.DjScene))
            : GetSharedValue(targets, t => NormalizeTagList(string.Join("; ", t.DjScene)));
        bool sceneChanged = isSingle
            ? sceneText != (origSceneShared ?? "")
            : (origSceneShared is not null ? (sceneText != origSceneShared && !string.IsNullOrWhiteSpace(sceneText)) : !string.IsNullOrWhiteSpace(sceneText));
        if (sceneChanged)
            updates["dj_scene_json"] = string.IsNullOrWhiteSpace(sceneText) ? null : sceneText;

        string moodText = NormalizeTagList(InlineMoodBox.Text ?? "");
        string? origMoodShared = isSingle
            ? NormalizeTagList(string.Join("; ", first.Moods))
            : GetSharedValue(targets, t => NormalizeTagList(string.Join("; ", t.Moods)));
        bool moodChanged = isSingle
            ? moodText != (origMoodShared ?? "")
            : (origMoodShared is not null ? (moodText != origMoodShared && !string.IsNullOrWhiteSpace(moodText)) : !string.IsNullOrWhiteSpace(moodText));
        if (moodChanged)
            updates["mood_json"] = string.IsNullOrWhiteSpace(moodText) ? null : moodText;

        double ratingValue = InlineRatingControl.Value;
        int newRating = ratingValue < 1 ? 0 : (int)Math.Round(Math.Clamp(ratingValue, 1, 5));
        if (isSingle)
        {
            if (newRating != Math.Clamp(first.Rating, 0, 5))
                updates["rating"] = newRating.ToString();
        }
        else if (ratingValue >= 1)
        {
            updates["rating"] = newRating.ToString();
        }

        return updates;
    }

    private async Task SaveInlineInspectorMetadataAsync()
    {
        if (_isSavingInlineMetadata) return;
        var targets = _inlineTargets.Count > 0 ? _inlineTargets : ResolveTargetTracksForAction(this);
        if (targets.Count == 0)
        {
            ViewModel.Notify("未选中曲目，无法保存。", "warning");
            return;
        }

        var updates = BuildInlineUpdates(targets, out string? error);
        if (updates is null)
        {
            ViewModel.Notify(error ?? "存在无效字段，请修正后再保存。", "error");
            return;
        }
        if (updates.Count == 0)
        {
            ViewModel.Notify("没有需要保存的修改。", "info");
            return;
        }

        _isSavingInlineMetadata = true;
        if (InlineSaveButton is not null)
        {
            InlineSaveButton.IsEnabled = false;
            InlineSaveButton.Content = "保存中...";
        }

        var savedUids = targets.Select(t => t.TrackUid).ToHashSet(StringComparer.Ordinal);
        double? savedScrollOffset = FindDescendantScrollViewer(TrackListView)?.VerticalOffset;
        _inlineDirtyCount = 0;

        try
        {
            await ViewModel.ApplyLexiconTagsToTracksAsync(targets, updates);

            // Ensure selection & scroll position remain intact even if Filter/Sort triggered a view update
            if (TrackListView is not null)
            {
                _restoringSelection = true;
                try
                {
                    var currentSelectedUids = TrackListView.SelectedItems
                        .OfType<TrackItem>()
                        .Select(t => t.TrackUid)
                        .ToHashSet(StringComparer.Ordinal);
                    var restored = new List<TrackItem>(savedUids.Count);
                    foreach (var item in ViewModel.FilteredTracks)
                    {
                        if (savedUids.Contains(item.TrackUid))
                        {
                            restored.Add(item);
                            if (!currentSelectedUids.Contains(item.TrackUid))
                            {
                                TrackListView.SelectedItems.Add(item);
                            }
                        }
                    }
                    if (restored.Count > 0)
                    {
                        SyncInlineInspectorTargets(restored);
                    }
                    if (savedScrollOffset is double offset)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            FindDescendantScrollViewer(TrackListView)?.ChangeView(null, offset, null, disableAnimation: true);
                        });
                    }
                }
                finally
                {
                    _restoringSelection = false;
                }
            }
        }
        finally
        {
            _isSavingInlineMetadata = false;
            if (InlineSaveButton is not null)
            {
                InlineSaveButton.Content = "保存";
                UpdateInlineDirtyState();
            }
        }
    }

    private void ShowLexiconTagMatrixFlyout(IReadOnlyList<TrackItem> targets)
    {
        if (targets.Count == 0)
        {
            ViewModel.StatusMessage = "请先在曲目表中选中一首或多首歌曲，再按 T 呼出五维打标矩阵。";
            return;
        }

        var selectedScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedMoods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? selectedVocals = null;
        string? selectedEnergy = null;
        int? selectedRating = null;

        bool touchedScene = false;
        bool touchedVocals = false;
        bool touchedEnergy = false;
        bool touchedMood = false;
        bool touchedRating = false;

        if (targets.Count == 1)
        {
            foreach (string s in targets[0].DjScene) selectedScenes.Add(s);
            foreach (string m in targets[0].Moods) selectedMoods.Add(m);
            selectedVocals = targets[0].DjVocals;
            selectedEnergy = targets[0].DjEnergy;
            selectedRating = targets[0].Rating > 0 ? targets[0].Rating : null;
        }

        // Theme-aware: native ToggleButton checked state uses the (gold) accent fill in Dark/Light
        // and system highlight colors in Windows contrast themes; no hard-coded brushes.
        var bodyStrongStyle = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        var accentButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        static void StyleToggle(ToggleButton btn, bool active) => btn.IsChecked = active;

        StackPanel BuildSectionHeader(string title, string hint)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            sp.Children.Add(new TextBlock { Text = title, Style = bodyStrongStyle });
            sp.Children.Add(new TextBlock { Text = hint, FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        var sceneGrid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (string sc in new[] { "focus", "coding", "relax", "energy", "pop" })
        {
            var btn = new ToggleButton { Content = sc switch { "focus" => "专注", "coding" => "编程", "relax" => "放松", "energy" => "活力", "pop" => "流行", _ => sc }, Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            StyleToggle(btn, selectedScenes.Contains(sc));
            btn.Click += (_, _) =>
            {
                touchedScene = true;
                if (btn.IsChecked == true) selectedScenes.Add(sc);
                else selectedScenes.Remove(sc);
                StyleToggle(btn, selectedScenes.Contains(sc));
            };
            sceneGrid.Children.Add(btn);
        }

        var vocalsButtons = new List<(string Value, ToggleButton Btn)>();
        var vocalsGrid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (string v in new[] { "instrumental", "vocal", "unknown" })
        {
            var btn = new ToggleButton { Content = v switch { "instrumental" => "纯器乐", "vocal" => "含人声", _ => "未知" }, Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            StyleToggle(btn, string.Equals(selectedVocals, v, StringComparison.OrdinalIgnoreCase));
            vocalsButtons.Add((v, btn));
            btn.Click += (_, _) =>
            {
                touchedVocals = true;
                selectedVocals = string.Equals(selectedVocals, v, StringComparison.OrdinalIgnoreCase) ? null : v;
                foreach (var (val, b) in vocalsButtons)
                {
                    StyleToggle(b, string.Equals(selectedVocals, val, StringComparison.OrdinalIgnoreCase));
                }
            };
            vocalsGrid.Children.Add(btn);
        }

        var energyButtons = new List<(string Value, ToggleButton Btn)>();
        var energyGrid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (string en in new[] { "low", "medium", "high" })
        {
            var btn = new ToggleButton { Content = en switch { "low" => "低", "medium" => "中", _ => "高" }, Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            StyleToggle(btn, string.Equals(selectedEnergy, en, StringComparison.OrdinalIgnoreCase));
            energyButtons.Add((en, btn));
            btn.Click += (_, _) =>
            {
                touchedEnergy = true;
                selectedEnergy = string.Equals(selectedEnergy, en, StringComparison.OrdinalIgnoreCase) ? null : en;
                foreach (var (val, b) in energyButtons)
                {
                    StyleToggle(b, string.Equals(selectedEnergy, val, StringComparison.OrdinalIgnoreCase));
                }
            };
            energyGrid.Children.Add(btn);
        }

        string[] moodOptions =
        [
            "Calm", "Chill", "Mellow", "Soothing", "Focus", "Melancholic",
            "Warm", "Romantic", "Bright", "Cheerful", "Energetic", "Dramatic"
        ];
        var moodRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var moodRow2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        for (int i = 0; i < moodOptions.Length; i++)
        {
            string m = moodOptions[i];
            var btn = new ToggleButton { Content = m, Padding = new Thickness(9, 4, 9, 4), FontSize = 12 };
            StyleToggle(btn, selectedMoods.Contains(m));
            btn.Click += (_, _) =>
            {
                touchedMood = true;
                if (btn.IsChecked == true) selectedMoods.Add(m);
                else selectedMoods.Remove(m);
                StyleToggle(btn, selectedMoods.Contains(m));
            };
            if (i < 6) moodRow1.Children.Add(btn);
            else moodRow2.Children.Add(btn);
        }

        var ratingButtons = new List<(int Value, ToggleButton Btn)>();
        var ratingGrid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        for (int r = 1; r <= 5; r++)
        {
            int val = r;
            var btn = new ToggleButton { Content = $"{val} ★", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            StyleToggle(btn, selectedRating == val);
            ratingButtons.Add((val, btn));
            btn.Click += (_, _) =>
            {
                touchedRating = true;
                selectedRating = selectedRating == val ? null : val;
                foreach (var (rv, b) in ratingButtons)
                {
                    StyleToggle(b, selectedRating == rv);
                }
            };
            ratingGrid.Children.Add(btn);
        }

        string targetDesc = targets.Count == 1
            ? $"{targets[0].Title} — {targets[0].ArtistsDisplay}"
            : $"已框选 {targets.Count} 首曲目（未修改的维度将保持各曲目原值）";

        var applyBtn = new Button
        {
            Content = "写入标签",
            Style = accentButtonStyle,
            Padding = new Thickness(14, 6, 14, 6),
        };
        applyBtn.Click += async (_, _) =>
        {
            var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (touchedScene)
                updates["dj_scene"] = JsonSerializer.Serialize(selectedScenes.ToArray());
            if (touchedVocals)
                updates["dj_vocals"] = selectedVocals;
            if (touchedEnergy)
                updates["dj_energy"] = selectedEnergy;
            if (touchedMood)
                updates["mood"] = JsonSerializer.Serialize(selectedMoods.ToArray());
            if (touchedRating)
                updates["rating"] = (selectedRating ?? 0).ToString();

            LexiconTagMatrixFlyout.Hide();
            if (updates.Count == 0)
            {
                ViewModel.StatusMessage = "未修改任何标签维度，已跳过写入。";
                return;
            }
            await ViewModel.ApplyLexiconTagsToTracksAsync(targets, updates);
        };

        var closeBtn = new Button
        {
            Content = "取消",
            Padding = new Thickness(12, 6, 12, 6),
        };
        closeBtn.Click += (_, _) => LexiconTagMatrixFlyout.Hide();

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        actionRow.Children.Add(closeBtn);
        actionRow.Children.Add(applyBtn);

        var panel = new StackPanel { Spacing = 9, Width = 560 };
        panel.Children.Add(new TextBlock { Text = "五维打标（Esc 关闭）", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        panel.Children.Add(new TextBlock { Text = targetDesc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        panel.Children.Add(BuildSectionHeader("场景", "可多选"));
        panel.Children.Add(sceneGrid);
        panel.Children.Add(BuildSectionHeader("人声", "单选，再次点击取消"));
        panel.Children.Add(vocalsGrid);
        panel.Children.Add(BuildSectionHeader("能量", "单选，再次点击取消"));
        panel.Children.Add(energyGrid);
        panel.Children.Add(BuildSectionHeader("情绪", "可多选"));
        panel.Children.Add(moodRow1);
        panel.Children.Add(moodRow2);
        panel.Children.Add(BuildSectionHeader("星级", "单选，再次点击取消"));
        panel.Children.Add(ratingGrid);
        panel.Children.Add(actionRow);

        LexiconTagMatrixFlyout.Content = panel;
        LexiconTagMatrixFlyout.ShowAt(LexiconTagToolbarButton);
    }

    private async void OnOpenBookletTheaterClick(object sender, RoutedEventArgs e)
    {
        if (_bookletDoc is null || _bookletDoc.PageCount == 0) return;

        var img = new Image { Stretch = Stretch.Uniform };
        var status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

        async Task RenderModalPageAsync()
        {
            int count = (int)_bookletDoc.PageCount;
            _bookletPageIndex = Math.Clamp(_bookletPageIndex, 0, count - 1);
            using var page = _bookletDoc.GetPage((uint)_bookletPageIndex);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream);
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            img.Source = bmp;
            status.Text = $"第 {_bookletPageIndex + 1} / {count} 页";
        }

        var prevBtn = new Button { Content = "上一页", Padding = new Thickness(10, 4, 10, 4) };
        prevBtn.Click += async (_, _) =>
        {
            if (_bookletPageIndex > 0)
            {
                _bookletPageIndex--;
                await RenderModalPageAsync();
                await RenderCurrentBookletPageAsync();
            }
        };

        var nextBtn = new Button { Content = "下一页", Padding = new Thickness(10, 4, 10, 4) };
        nextBtn.Click += async (_, _) =>
        {
            if (_bookletPageIndex + 1 < (int)_bookletDoc.PageCount)
            {
                _bookletPageIndex++;
                await RenderModalPageAsync();
                await RenderCurrentBookletPageAsync();
            }
        };

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(prevBtn);
        controls.Children.Add(nextBtn);
        controls.Children.Add(status);

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(controls);
        body.Children.Add(new Border
        {
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            MaxHeight = 620,
            Child = img,
        });

        await RenderModalPageAsync();

        var dialog = new ContentDialog
        {
            Title = $"数字册页剧场 · {ViewModel.InspectedTrack?.Album ?? "Booklet"}",
            Content = body,
            CloseButtonText = "关闭",
            RequestedTheme = RootWorkbench.ActualTheme,
            XamlRoot = RootWorkbench.XamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 920.0;
        await ShowDialogSafeAsync(dialog);
    }

    private void OnRejectProposalClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pid })
        {
            ViewModel.RejectProposal(pid);
        }
    }

    private void OnApproveProposalClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pid })
        {
            ViewModel.ApproveProposal(pid, approved: true);
        }
    }

    private void OnTogglePlayShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused()) return;
        ViewModel.TogglePlayPauseCommand.Execute(null);
        args.Handled = true;
    }

    private void OnCodingQuickSceneShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.ActivateCodingQuickSceneCommand.Execute(null);
        args.Handled = true;
    }

    private void OnPrevTrackShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused()) return;
        ViewModel.PreviousTrackCommand.Execute(null);
        args.Handled = true;
    }

    private void OnNextTrackShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused()) return;
        ViewModel.NextTrackCommand.Execute(null);
        args.Handled = true;
    }

    private void OnWaveformPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        double width = WaveformCanvas.ActualWidth;
        if (width <= 0) return;
        double x = e.GetCurrentPoint(WaveformCanvas).Position.X;
        ViewModel.SeekFraction(Math.Clamp(x / width, 0.0, 1.0));
        WaveformCanvas.Invalidate();
    }

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
        float barW = Math.Clamp(step * 0.65f, 1.5f, 6f);
        float centerY = h / 2f;
        float progressX = (float)(ViewModel.PlaybackProgress * w);

        for (int i = 0; i < count; i++)
        {
            float amp = envelope is not null ? Math.Clamp(envelope[i], 0.05f, 1f) : 0.18f;
            float halfH = Math.Max(2f, amp * (h * 0.44f));
            float cx = (i + 0.5f) * step;
            var rect = new SKRect(cx - barW / 2f, centerY - halfH, cx + barW / 2f, centerY + halfH);
            canvas.DrawRoundRect(rect, barW / 2f, barW / 2f, cx <= progressX ? ActiveBarPaint : InactiveBarPaint);
        }
    }
}
