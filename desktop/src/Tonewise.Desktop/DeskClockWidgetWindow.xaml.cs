using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using Tonewise.Desktop.ViewModels;
using Tonewise.SyncHost.Workbench;
using Windows.Graphics;

namespace Tonewise.Desktop;

public sealed partial class DeskClockWidgetWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly DispatcherTimer _waveTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _burnInTimer;
    private int _burnInTick;
    private bool _isCompactOverlay;

    private static readonly SKColor ChampagneGold = new(0xD9, 0xB3, 0x72);
    private static readonly SKColor DimTrack = new(0x64, 0x74, 0x8B, 0x60);

    public DeskClockWidgetWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        TrySetWidgetBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WidgetTitleDragBar);

        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tonewise.ico");
        if (File.Exists(icoPath)) AppWindow.SetIcon(icoPath);

        ApplyOverlappedCompanionChrome();

        UpdateClockText();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClockText();
        _clockTimer.Start();

        _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _waveTimer.Tick += (_, _) => WidgetWaveformCanvas.Invalidate();
        _waveTimer.Start();

        _burnInTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _burnInTimer.Tick += (_, _) => ApplyOledBurnInShift(++_burnInTick);
        _burnInTimer.Start();

        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _waveTimer.Stop();
            _burnInTimer.Stop();
        };
    }

    private void TrySetWidgetBackdrop()
    {
        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            try
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            }
            catch
            {
                // Fallback to XAML border background when DWM backdrop is unavailable
            }
        }
    }

    private void ApplyOverlappedCompanionChrome()
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        AppWindow.Resize(new SizeInt32(500, 214));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
        }
    }

    public static (int Dx, int Dy) ComputeOledBurnInOffset(int tickCount) =>
        OledBurnInGuard.ComputePixelOffset(tickCount);

    public void ApplyOledBurnInShift(int tickCount)
    {
        var (dx, dy) = ComputeOledBurnInOffset(tickCount);
        OledCanvasHost.Margin = new Thickness(4 + dx, 4 + dy, 4 - dx, 4 - dy);
    }

    private void UpdateClockText()
    {
        ClockTextBlock.Text = DateTime.Now.ToString("HH : mm");
    }

    private void OnTogglePipModeClick(object sender, RoutedEventArgs e)
    {
        _isCompactOverlay = !_isCompactOverlay;
        if (_isCompactOverlay)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
            PipToggleButton.Content = "还原窗口";
        }
        else
        {
            ApplyOverlappedCompanionChrome();
            PipToggleButton.Content = "PiP 画中画";
        }
    }

    private void OnWaveformPointerPressed(object sender, PointerRoutedEventArgs e)
    {
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

        using var activePaint = new SKPaint { Color = ChampagneGold, IsAntialias = true };
        using var inactivePaint = new SKPaint { Color = DimTrack, IsAntialias = true };

        for (int i = 0; i < count; i++)
        {
            float amp = envelope is not null ? Math.Clamp(envelope[i], 0.06f, 1f) : 0.16f;
            float halfH = Math.Max(2f, amp * (h * 0.44f));
            float cx = (i + 0.5f) * step;
            var rect = new SKRect(cx - barW / 2f, centerY - halfH, cx + barW / 2f, centerY + halfH);
            canvas.DrawRoundRect(rect, barW / 2f, barW / 2f, cx <= progressX ? activePaint : inactivePaint);
        }
    }
}
