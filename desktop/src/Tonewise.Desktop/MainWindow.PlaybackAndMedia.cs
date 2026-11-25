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
            bool hasCover = !string.IsNullOrWhiteSpace(track.CoverArtPath)
                && await Task.Run(() => File.Exists(track.CoverArtPath));
            if (hasCover && track.CoverArtPath is not null)
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
        bool exists = !string.IsNullOrWhiteSpace(pdfPath)
            && await Task.Run(() => File.Exists(pdfPath));
        if (!exists || string.IsNullOrWhiteSpace(pdfPath))
        {
            _bookletDoc = null;
            _loadedBookletPath = null;
            BookletPageImage.Source = null;
            if (NowPlayingBookletImage is not null) NowPlayingBookletImage.Source = null;
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
            if (NowPlayingBookletStatusText is not null) NowPlayingBookletStatusText.Text = "Booklet PDF unavailable";
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
        if (NowPlayingBookletImage is not null) NowPlayingBookletImage.Source = bmp;
        if (NowPlayingBookletStatusText is not null) NowPlayingBookletStatusText.Text = $"Page {_bookletPageIndex + 1} / {pageCount}";
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
            SyncActiveInboxAuditButtons();
        }
    }

    private void SyncSubSelectorBarItems()
    {
        if (SubPivotBar is null) return;
        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        var activeBrush = new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        var inactiveBrush = new SolidColorBrush(isLight ? Color.FromArgb(0xFF, 0x59, 0x59, 0x59) : Color.FromArgb(0xFF, 0xA3, 0xA3, 0xA3));
        var accentBrush = new SolidColorBrush(Color.FromArgb(0xFF, _currentAccentRgb.R, _currentAccentRgb.G, _currentAccentRgb.B));

        var tabs = ViewModel.CurrentSubTabs;
        bool canReuseInPlace = SubPivotBar.Children.Count == tabs.Count;
        if (canReuseInPlace)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                if (SubPivotBar.Children[i] is not Button existingBtn || existingBtn.Tag as string != tabs[i].Id)
                {
                    canReuseInPlace = false;
                    break;
                }
            }
        }

        if (canReuseInPlace)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var opt = tabs[i];
                if (SubPivotBar.Children[i] is Button btn)
                {
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, opt.Label);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(btn, opt.IsActive ? "已选中" : "未选中");
                    if (btn.Content is StackPanel stack && stack.Children.Count >= 2)
                    {
                        if (stack.Children[0] is TextBlock lbl)
                        {
                            lbl.Text = opt.Label;
                            lbl.FontWeight = opt.IsActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                            lbl.Foreground = opt.IsActive ? activeBrush : inactiveBrush;
                        }
                        if (stack.Children[1] is Microsoft.UI.Xaml.Shapes.Rectangle bar)
                        {
                            bar.Fill = accentBrush;
                            bar.Visibility = opt.IsActive ? Visibility.Visible : Visibility.Collapsed;
                        }
                    }
                }
            }
            return;
        }

        SubPivotBar.Children.Clear();
        foreach (var opt in tabs)
        {
            var label = new TextBlock
            {
                Text = opt.Label,
                FontSize = 14,
                FontWeight = opt.IsActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = opt.IsActive ? activeBrush : inactiveBrush,
            };

            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(label);
            stack.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = 2,
                Fill = accentBrush,
                Visibility = opt.IsActive ? Visibility.Visible : Visibility.Collapsed,
            });

            var btn = new Button
            {
                Tag = opt.Id,
                Padding = new Thickness(0, 4, 0, 2),
                MinHeight = 28,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = stack,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, opt.Label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(btn, opt.IsActive ? "已选中" : "未选中");
            btn.Click += (_, _) => ViewModel.SelectSubTab(opt.Id);
            SubPivotBar.Children.Add(btn);
        }
    }

    private void OnPrimaryNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string spec } && !string.IsNullOrWhiteSpace(spec))
        {
            string[] parts = spec.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                ViewModel.NavigateToPrimary(parts[0], parts[1]);
            else
                ViewModel.NavigateToPrimary(spec);
        }
    }

    private void OnQuickplayNavLinkClick(object sender, RoutedEventArgs e) => OnPrimaryNavClick(sender, e);

    private void OnScenesModeSwitchClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string mode } && !string.IsNullOrWhiteSpace(mode))
        {
            ViewModel.SetScenesMode(mode);
        }
    }

    private void OnNowPlayingPanelTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tab } && !string.IsNullOrWhiteSpace(tab))
        {
            ViewModel.SetNowPlayingPanelTab(tab);
        }
    }

    private void OnTransportCoverClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigateToPrimary("now_playing");
    }

    private void OnQuickplayPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (QuickplayScrollViewer is null) return;
        int delta = e.GetCurrentPoint(QuickplayScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0) return;
        double target = Math.Clamp(
            QuickplayScrollViewer.HorizontalOffset - delta * 1.1,
            0.0,
            QuickplayScrollViewer.ScrollableWidth);
        QuickplayScrollViewer.ChangeView(target, null, null, disableAnimation: false);
        e.Handled = true;
    }

    private QuickplayTileItem? _rightClickedQuickplayTile;

    private void OnQuickplayRepeaterElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement root) return;
        var img = FindLogicalOrVisualImage(root, "TileCoverImage");
        if (img is null) return;
        var tile = (sender.ItemsSourceView?.GetAt(args.Index) as QuickplayTileItem)
            ?? (root.DataContext as QuickplayTileItem);
        img.Source = CoverArtImageCache.GetQuickplayCover(tile?.CoverArtPath);
    }

    private static Image? FindLogicalOrVisualImage(DependencyObject root, string targetName)
    {
        if (root is FrameworkElement fe && fe.FindName(targetName) is Image named)
        {
            return named;
        }
        return FindImageInLogicalOrVisualTree(root);
    }

    private static Image? FindImageInLogicalOrVisualTree(DependencyObject node)
    {
        if (node is Image img) return img;
        if (node is ContentControl { Content: DependencyObject contentObj })
        {
            var inner = FindImageInLogicalOrVisualTree(contentObj);
            if (inner is not null) return inner;
        }
        if (node is Border { Child: DependencyObject borderChild })
        {
            var inner = FindImageInLogicalOrVisualTree(borderChild);
            if (inner is not null) return inner;
        }
        if (node is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                var inner = FindImageInLogicalOrVisualTree(child);
                if (inner is not null) return inner;
            }
        }
        return FindDescendantImage(node);
    }

    private void OnQuickplayTileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuickplayTileItem tile)
        {
            ViewModel.PlayQuickplayTile(tile);
        }
    }

    private void OnQuickplayTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _rightClickedQuickplayTile = (sender as FrameworkElement)?.DataContext as QuickplayTileItem
            ?? (e.OriginalSource as FrameworkElement)?.DataContext as QuickplayTileItem;
    }

    private void OnContextPlayQuickplayTileClick(object sender, RoutedEventArgs e)
    {
        var tile = _rightClickedQuickplayTile ?? (sender as FrameworkElement)?.DataContext as QuickplayTileItem;
        _rightClickedQuickplayTile = null;
        if (tile is not null) ViewModel.PlayQuickplayTile(tile);
    }

    private void OnContextUnpinQuickplayTileClick(object sender, RoutedEventArgs e)
    {
        var tile = _rightClickedQuickplayTile ?? (sender as FrameworkElement)?.DataContext as QuickplayTileItem;
        _rightClickedQuickplayTile = null;
        if (tile is not null) ViewModel.UnpinQuickplayItem(tile.TileId);
    }

    private void OnPinSelectedEntityGroupToQuickplayClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PinEntityGroupToQuickplay(ViewModel.SelectedEntityGroup);
    }

    private void OnContextPinEntityGroupToQuickplayClick(object sender, RoutedEventArgs e)
    {
        var group = _rightClickedEntityGroup ?? ViewModel.SelectedEntityGroup;
        _rightClickedEntityGroup = null;
        ViewModel.PinEntityGroupToQuickplay(group);
    }

    private void OnPinPlaylistToQuickplayClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PinPlaylistToQuickplay(ViewModel.SelectedPlaylist);
    }

    private void OnContextPinPlaylistToQuickplayClick(object sender, RoutedEventArgs e)
    {
        var pl = _rightClickedPlaylist ?? (sender as FrameworkElement)?.DataContext as PlaylistSummary ?? ViewModel.SelectedPlaylist;
        _rightClickedPlaylist = null;
        ViewModel.PinPlaylistToQuickplay(pl);
    }

    private void OnEntityCoverListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is not FrameworkElement root) return;
        var img = (root.FindName("EntityCoverImage") as Image) ?? FindDescendantImage(root);
        if (img is null) return;
        if (args.InRecycleQueue)
        {
            img.Source = null;
            return;
        }
        if (args.Phase == 0)
        {
            img.Source = args.Item is EntityGroupItem group
                ? GetOrCreateRowCoverBitmap(group.CoverArtPath, decodeWidth: 180)
                : null;
        }
    }

    // ---------------- Now Playing Mosaic & Idle Auto-Fade ----------------

    private async Task PrewarmNowPlayingMosaicAsync()
    {
        _mosaicPrewarmCts?.Cancel();
        _mosaicPrewarmCts?.Dispose();
        var cts = new CancellationTokenSource();
        _mosaicPrewarmCts = cts;
        try
        {
            await Task.Delay(1200, cts.Token);
            if (!cts.Token.IsCancellationRequested && !_mosaicRenderer.HasCovers)
            {
                RefreshNowPlayingMosaic();
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation on folder switch or window close
        }
    }

    private void RefreshNowPlayingMosaic()
    {
        var covers = ViewModel.GetUniqueCoverArtPaths(64);
        _mosaicRenderer.UpdateCoversAsync(covers, () =>
        {
            DispatcherQueue.TryEnqueue(() => NowPlayingMosaicCanvas?.Invalidate());
        });
        NowPlayingMosaicCanvas?.Invalidate();
    }

    private static readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibilitySettings = new();

    private void OnPaintNowPlayingMosaic(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (_accessibilitySettings.HighContrast)
        {
            e.Surface.Canvas.Clear(SKColors.Transparent);
            return;
        }
        bool isDark = RootWorkbench?.ActualTheme != ElementTheme.Light;
        _mosaicRenderer.Render(e.Surface.Canvas, e.Info.Width, e.Info.Height, isDark, _currentAccentRgb);
    }

    private void OnNowPlayingPointerActivity(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsNowPlayingViewVisible)
        {
            RestoreNowPlayingChrome();
        }
    }

    private void RestoreNowPlayingChrome()
    {
        _lastNowPlayingActivityUtc = DateTime.UtcNow;
        if (_isNowPlayingChromeFaded && TopHeaderContainer is not null)
        {
            _isNowPlayingChromeFaded = false;
            TopHeaderContainer.IsHitTestVisible = true;
            ZuneMotionHelper.AnimateOpacity(TopHeaderContainer, 1.0f, durationMs: 120);
        }
    }

    private void UpdateNowPlayingChromeAutoFade()
    {
        if (TopHeaderContainer is null) return;
        if (ViewModel.IsNowPlayingViewVisible && ViewModel.IsPlaying)
        {
            if (!_isNowPlayingChromeFaded && (DateTime.UtcNow - _lastNowPlayingActivityUtc).TotalSeconds >= 4.0)
            {
                _isNowPlayingChromeFaded = true;
                TopHeaderContainer.IsHitTestVisible = false;
                ZuneMotionHelper.AnimateOpacity(TopHeaderContainer, 0.0f, durationMs: 320);
            }
        }
        else if (_isNowPlayingChromeFaded)
        {
            RestoreNowPlayingChrome();
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
        void HighlightList(ListView? listView)
        {
            if (listView is null) return;
            for (int i = 0; i < ViewModel.PlaybackQueue.Count; i++)
            {
                if (listView.ContainerFromIndex(i) is ListViewItem container)
                {
                    ApplyQueueRowHighlight(container, ViewModel.PlaybackQueue[i]);
                }
            }
        }
        HighlightList(PlaybackQueueListView);
        HighlightList(NowPlayingUpNextListView);
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
                titleText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, _currentAccentRgb.R, _currentAccentRgb.G, _currentAccentRgb.B));
            }
            else
            {
                bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
                titleText.Foreground = new SolidColorBrush(isLight
                    ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
                    : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
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

    private void OnTransportComposerClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigateToCurrentTrackComposer();
    }

    private void OnTransportConductorClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigateToCurrentTrackConductor();
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

    public Windows.UI.Text.FontWeight FilterChipFontWeight(bool isActive) =>
        isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    private static void SyncRadioMenuFlyoutSelection(object sender, string currentTag)
    {
        if (sender is not MenuFlyout flyout) return;
        foreach (var item in flyout.Items)
        {
            if (item is RadioMenuFlyoutItem radio && radio.Tag is string tag)
            {
                radio.IsChecked = string.Equals(tag, currentTag, StringComparison.OrdinalIgnoreCase);
            }
        }
    }


    private async void OnOpenBookletTheaterClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
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
                await RunSafeUiActionAsync(async () =>
                {
                    if (_bookletPageIndex > 0)
                    {
                        _bookletPageIndex--;
                        await RenderModalPageAsync();
                        await RenderCurrentBookletPageAsync();
                    }
                }, "数字册页上一页");
            };

            var nextBtn = new Button { Content = "下一页", Padding = new Thickness(10, 4, 10, 4) };
            nextBtn.Click += async (_, _) =>
            {
                await RunSafeUiActionAsync(async () =>
                {
                    if (_bookletPageIndex + 1 < (int)_bookletDoc.PageCount)
                    {
                        _bookletPageIndex++;
                        await RenderModalPageAsync();
                        await RenderCurrentBookletPageAsync();
                    }
                }, "数字册页下一页");
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
                CornerRadius = new CornerRadius(0),
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
        }, "打开数字册页剧场");
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
        float barW = Math.Clamp(step * 0.58f, 1.5f, 4f);
        float centerY = h / 2f;
        float progressX = (float)(ViewModel.PlaybackProgress * w);
        bool highContrast = _accessibilitySettings.HighContrast;
        var activePaint = _activeBarPaint;
        SKPaint? hcActivePaint = null;
        SKPaint? hcInactivePaint = null;
        if (highContrast)
        {
            bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
            hcActivePaint = new SKPaint
            {
                Color = isLight ? SKColors.Black : SKColors.White,
                Style = SKPaintStyle.Fill,
                IsAntialias = false,
            };
            hcInactivePaint = new SKPaint
            {
                Color = isLight ? new SKColor(0x55, 0x55, 0x55) : new SKColor(0xA0, 0xA0, 0xA0),
                Style = SKPaintStyle.Fill,
                IsAntialias = false,
            };
            activePaint = hcActivePaint;
        }
        try
        {
            for (int i = 0; i < count; i++)
            {
                float amp = envelope is not null ? Math.Clamp(envelope[i], 0.05f, 1f) : 0.18f;
                float halfH = Math.Max(2f, amp * (h * 0.44f));
                float cx = (i + 0.5f) * step;
                var rect = new SKRect(cx - barW / 2f, centerY - halfH, cx + barW / 2f, centerY + halfH);
                canvas.DrawRect(rect, cx <= progressX ? activePaint : (hcInactivePaint ?? InactiveBarPaint));
            }

            if (progressX > 0 && progressX < w)
            {
                canvas.DrawRect(new SKRect(progressX - 1f, 0, progressX + 1f, h), activePaint);
            }
        }
        finally
        {
            hcActivePaint?.Dispose();
            hcInactivePaint?.Dispose();
        }
    }
}
