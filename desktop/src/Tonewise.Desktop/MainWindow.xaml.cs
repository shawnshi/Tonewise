using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Tonewise.Desktop.Models;
using Tonewise.Desktop.Services;
using Tonewise.Desktop.ViewModels;
using Windows.Data.Pdf;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Tonewise.Desktop;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private readonly DispatcherTimer _progressTimer;
    private Win32GlobalHotkeyService? _globalHotkeys;
    private DeskClockWidgetWindow? _oledWidget;
    private MediaPlayer? _smtcPlayer;
    private SystemMediaTransportControls? _smtc;

    private PdfDocument? _bookletDoc;
    private string? _loadedBookletPath;
    private int _bookletPageIndex;

    private static readonly SKColor ActiveBarColor = new(0xD9, 0xB3, 0x72);
    private static readonly SKColor InactiveBarColor = new(0x64, 0x74, 0x8B, 0x60);

    public MainWindow()
    {
        InitializeComponent();

        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tonewise.ico");
        if (File.Exists(icoPath)) AppWindow.SetIcon(icoPath);

        InitializeSmtc();
        InitializeGlobalHotkeys();

        ViewModel.WaveformUpdated += () => WaveformCanvas.Invalidate();
        ViewModel.TrackChanged += async track =>
        {
            UpdateSmtcMetadata(track);
            await LoadBookletForTrackAsync(track);
        };

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _progressTimer.Tick += (_, _) =>
        {
            ViewModel.RefreshProgress();
            WaveformCanvas.Invalidate();
        };
        _progressTimer.Start();

        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _globalHotkeys?.Dispose();
            _oledWidget?.Close();
            _smtcPlayer?.Dispose();
            ViewModel.Dispose();
        };
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
            _globalHotkeys.TryRegister(3, mod, 0x4D, () => DispatcherQueue.TryEnqueue(() => OnOpenOledWidgetClick(this, new RoutedEventArgs())));
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
                        case SystemMediaTransportControlsButton.Pause:
                            ViewModel.TogglePlayPauseCommand.Execute(null);
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

    private void UpdateSmtcMetadata(TrackItem? track)
    {
        if (_smtc is null || track is null) return;
        try
        {
            _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = track.ArtistsDisplay;
            updater.MusicProperties.AlbumTitle = track.Album;
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
        if (_bookletDoc is null || _bookletPageIndex <= 0) return;
        _bookletPageIndex--;
        await RenderCurrentBookletPageAsync();
    }

    private async void OnBookletNextClick(object sender, RoutedEventArgs e)
    {
        if (_bookletDoc is null || _bookletPageIndex + 1 >= (int)_bookletDoc.PageCount) return;
        _bookletPageIndex++;
        await RenderCurrentBookletPageAsync();
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.LoadMusicFolder(folder.Path);
        }
    }

    private async void OnLoadEqClick(object sender, RoutedEventArgs e)
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

    private async void OnLoadFirClick(object sender, RoutedEventArgs e)
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

    private async void OnLastFmSetupClick(object sender, RoutedEventArgs e)
    {
        var apiKeyBox = new TextBox { Header = "Last.fm API Key", PlaceholderText = "Required for Web API v2.0" };
        var secretBox = new PasswordBox { Header = "API Secret" };
        var userBox = new TextBox { Header = "Username" };
        var passwordBox = new PasswordBox { Header = "Password (used once for getMobileSession)" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(apiKeyBox);
        panel.Children.Add(secretBox);
        panel.Children.Add(userBox);
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            Title = "Configure & Sync Last.fm",
            Content = panel,
            PrimaryButtonText = "Authenticate & Sync",
            CloseButtonText = "Cancel",
            XamlRoot = RootWorkbench.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ConfigureAndSyncLastFmAsync(
                apiKeyBox.Text,
                secretBox.Password,
                userBox.Text,
                passwordBox.Password
            );
        }
    }

    private async void OnExportUsbCarClick(object sender, RoutedEventArgs e)
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

    private void OnApproveProposalClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pid })
        {
            ViewModel.ApproveProposal(pid, approved: true);
        }
    }

    private void OnTogglePlayShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
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
        ViewModel.PreviousTrackCommand.Execute(null);
        args.Handled = true;
    }

    private void OnNextTrackShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
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

        using var activePaint = new SKPaint { Color = ActiveBarColor, IsAntialias = true };
        using var inactivePaint = new SKPaint { Color = InactiveBarColor, IsAntialias = true };

        for (int i = 0; i < count; i++)
        {
            float amp = envelope is not null ? Math.Clamp(envelope[i], 0.05f, 1f) : 0.18f;
            float halfH = Math.Max(2f, amp * (h * 0.44f));
            float cx = (i + 0.5f) * step;
            var rect = new SKRect(cx - barW / 2f, centerY - halfH, cx + barW / 2f, centerY + halfH);
            canvas.DrawRoundRect(rect, barW / 2f, barW / 2f, cx <= progressX ? activePaint : inactivePaint);
        }
    }
}
