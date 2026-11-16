using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Tonewise.Desktop.Models;
using Tonewise.Desktop.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Tonewise.Desktop;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private readonly DispatcherTimer _progressTimer;

    private static readonly SKColor ActiveBarColor = new(0x00, 0x78, 0xD4);
    private static readonly SKColor InactiveBarColor = new(0x88, 0x88, 0x88, 0x70);

    public MainWindow()
    {
        InitializeComponent();
        ViewModel.WaveformUpdated += () => WaveformCanvas.Invalidate();
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
            ViewModel.Dispose();
        };
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

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackItem track)
        {
            ViewModel.PlayTrack(track);
        }
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
