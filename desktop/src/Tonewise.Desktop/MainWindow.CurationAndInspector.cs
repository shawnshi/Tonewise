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
        await RunSafeUiActionAsync(async () =>
        {
            var selected = PlaylistsListView?.SelectedItems.OfType<PlaylistSummary>().ToList() ?? [];
            await ViewModel.SendPlaylistsToPhoneAsync(selected);
        }, "发送歌单到手机");
    }

    private async void OnContextSendPlaylistCardToPhoneClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
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
        }, "发送歌单卡片到手机");
    }

    private async void OnContextDeletePlaylistCardClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
        {
            var target = _rightClickedPlaylist ?? (sender as FrameworkElement)?.DataContext as PlaylistSummary ?? ViewModel.SelectedPlaylist;
            _rightClickedPlaylist = null;
            await DeletePlaylistWithConfirmAsync(target);
        }, "删除歌单");
    }

    private async Task<bool> ConfirmDestructiveActionAsync(string title, string message, string confirmLabel = "确认")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = confirmLabel,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = RootWorkbench.ActualTheme,
        };
        return await ShowDialogSafeAsync(dialog) == ContentDialogResult.Primary;
    }

    private void ShowUndoableNotification(string message, Action undoAction, string severity = "info")
    {
        _settingUndoNotification = true;
        try
        {
            _pendingUndoAction = undoAction;
            if (GlobalNotificationUndoButton is not null)
            {
                GlobalNotificationUndoButton.Visibility = Visibility.Visible;
            }
            ViewModel.Notify(message, severity);
        }
        finally
        {
            _settingUndoNotification = false;
        }
    }

    private void ClearUndoNotificationState()
    {
        _pendingUndoAction = null;
        if (GlobalNotificationUndoButton is not null)
        {
            GlobalNotificationUndoButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnGlobalNotificationBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ClearUndoNotificationState();
    }

    private void OnGlobalNotificationUndoClick(object sender, RoutedEventArgs e)
    {
        var undo = _pendingUndoAction;
        ClearUndoNotificationState();
        if (undo is null) return;
        try
        {
            undo();
        }
        catch (Exception ex)
        {
            ViewModel.Notify($"撤销失败: {ex.Message}", "error");
        }
    }

    private async void OnRematchPlaylistsClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
        {
            List<PlaylistSummary>? targets = null;
            if (ViewModel.IsPlaylistMultiSelectMode)
            {
                var selected = PlaylistsListView?.SelectedItems.OfType<PlaylistSummary>().ToList() ?? [];
                if (selected.Count > 0) targets = selected;
            }
            string scopeDesc = targets is null ? "全部歌单" : $"{targets.Count} 个选中歌单";
            if (!await ConfirmDestructiveActionAsync("重匹曲库并写回歌单", $"将重新匹配本地曲库并写回 {scopeDesc} 的 .m3u8 歌单文件。确认继续？", "开始重匹"))
            {
                return;
            }
            ViewModel.RematchPlaylistsAndPersist(targets);
        }, "重匹歌单");
    }

    private async void OnContextRematchPlaylistCardClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
        {
            var selected = PlaylistsListView?.SelectedItems.OfType<PlaylistSummary>().ToList() ?? [];
            List<PlaylistSummary> targets;
            if (selected.Count > 1)
            {
                _rightClickedPlaylist = null;
                targets = selected;
            }
            else
            {
                var target = _rightClickedPlaylist ?? (sender as FrameworkElement)?.DataContext as PlaylistSummary ?? ViewModel.SelectedPlaylist;
                _rightClickedPlaylist = null;
                if (target is null) return;
                targets = [target];
            }
            string scopeDesc = targets.Count == 1 ? $"「{targets[0].Name}」" : $"{targets.Count} 个歌单";
            if (!await ConfirmDestructiveActionAsync("重匹曲库并写回歌单", $"将重新匹配本地曲库并写回 {scopeDesc} 的 .m3u8 文件。确认继续？", "开始重匹"))
            {
                return;
            }
            ViewModel.RematchPlaylistsAndPersist(targets);
        }, "重匹歌单");
    }

    private void OnRemoveLanOutboxItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ViewModel.RemoveLanOutboxItem(id);
        }
    }

    private async void OnUnpairLanDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string deviceId })
        {
            await RunSafeUiActionAsync(async () =>
            {
                if (!await ConfirmDestructiveActionAsync(
                        "解除设备配对",
                        $"解除设备「{deviceId}」后，手机将失去同步凭证并需重新扫描 6 位配对码。确认解除？",
                        "解除配对"))
                {
                    return;
                }
                ViewModel.UnpairLanDevice(deviceId);
            }, "解除设备配对");
        }
    }

    private async void OnClearLanOutboxClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
        {
            int count = ViewModel.SyncOutboxItems.Count;
            if (count == 0) return;
            if (!await ConfirmDestructiveActionAsync(
                    "清空发送队列",
                    $"确认清空全部 {count} 项待同步条目？若已开启镜像删除，清空清单可能会影响后续镜像比对。",
                    "清空队列"))
            {
                return;
            }
            ViewModel.ClearLanOutboxCommand.Execute(null);
        }, "清空发送队列");
    }

    private void OnClearPlaybackQueueClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PlaybackQueue.Count == 0) return;
        var prevItems = ViewModel.PlaybackQueue.ToList();
        string prevTitle = ViewModel.PlaybackQueueTitle;
        ViewModel.ClearPlaybackQueueCommand.Execute(null);
        ShowUndoableNotification(
            $"已清空播放队列（{prevItems.Count} 首）。",
            () => ViewModel.RestoreClearedQueueSnapshot(prevItems, prevTitle),
            "info");
    }

    private async void OnMirrorPruneSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (!_mirrorPruneConfirmArmed || _suppressMirrorPruneConfirm) return;
        if (MirrorPruneToggleSwitch is null || !MirrorPruneToggleSwitch.IsOn) return;

        await RunSafeUiActionAsync(async () =>
        {
            string scopeHint = string.Equals(ViewModel.MirrorPruneScope, "managed_only", StringComparison.OrdinalIgnoreCase)
                ? "当前范围为 managed_only（仅清理 Tonewise 托管目录内未在列表中的文件）"
                : "当前范围为 music_folder_all（将清理手机 Music 目录内未在同步清单中的文件）";
            bool confirmed = await ConfirmDestructiveActionAsync(
                "开启手机端镜像删除",
                $"开启后，同步时会删除手机上不在长期订阅列表中的曲目。{scopeHint}。确认开启？",
                "确认开启");
            if (!confirmed)
            {
                _suppressMirrorPruneConfirm = true;
                try
                {
                    ViewModel.IsMirrorPruneEnabled = false;
                }
                finally
                {
                    _suppressMirrorPruneConfirm = false;
                }
            }
        }, "确认镜像删除开关");
    }

    private async void OnOrganizeLibraryFoldersClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
        {
            var preview = await ViewModel.PreviewOrganizeLibraryFoldersAsync();
            if (preview is null) return;
            if (preview.MovedCount == 0 && preview.StaleRemovedCount == 0)
            {
                ViewModel.Notify($"检查 {preview.TotalChecked:N0} 首曲目，所有路径已符合规范，无需移动文件。", "info");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"预计迁移规范文件：{preview.MovedCount:N0} 首（含修复超长路径 {preview.LongPathFixedCount:N0} 首）");
            if (preview.StaleRemovedCount > 0)
            {
                sb.AppendLine($"预计清理失效索引：{preview.StaleRemovedCount:N0} 条");
            }
            if (preview.SampleMoves.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("变更预览（前 10 条）：");
                foreach (string sample in preview.SampleMoves.Take(10))
                {
                    sb.AppendLine("• " + sample);
                }
            }

            if (!await ConfirmDestructiveActionAsync("确认文件夹整理", sb.ToString().TrimEnd(), "执行整理"))
            {
                return;
            }
            await ViewModel.OrganizeLibraryFoldersCommand.ExecuteAsync(null);
        }, "文件夹整理");
    }

    private async void OnNewPlaylistClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
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
        }, "新建歌单");
    }

    private async void OnImportPlaylistClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
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
            await RunSafeUiActionAsync(async () =>
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
            }, "读取歌单文件");
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
        }, "导入歌单");
    }

    private async void OnEditPlaylistClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(OnEditPlaylistCoreAsync, "编辑歌单");
    }

    private async Task OnEditPlaylistCoreAsync()
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
                    MinWidth = 28,
                    MinHeight = 28,
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 12,
                    IsEnabled = idx > 0,
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(upBtn, "上移曲目");
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
                    MinWidth = 28,
                    MinHeight = 28,
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 12,
                    IsEnabled = idx + 1 < workingEntries.Count,
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(downBtn, "下移曲目");
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
                    MinWidth = 28,
                    MinHeight = 28,
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 12,
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delBtn, "从歌单移除曲目");
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
        await RunSafeUiActionAsync(() => DeletePlaylistWithConfirmAsync(ViewModel.SelectedPlaylist), "删除歌单");
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
        (string NavId, string? SubTab)? target = sender.Key switch
        {
            VirtualKey.Number1 => ("quickplay", null),
            VirtualKey.Number2 => ("collection", null),
            VirtualKey.Number3 => ("now_playing", null),
            VirtualKey.Number4 => ("studio", null),
            VirtualKey.Number5 => ("collection", "playlists"),
            VirtualKey.I => ("studio", "inbox"),
            _ => null,
        };
        if (target is { } dest)
        {
            ViewModel.NavigateToPrimary(dest.NavId, dest.SubTab);
            args.Handled = true;
        }
    }


    // ---------------- Inline inspector editor state ----------------

    private sealed record InlineDraftSnapshot(
        string? Title,
        string Artists,
        string Album,
        string Composers,
        string Conductors,
        string Year,
        string Publisher,
        string Genre,
        string Work,
        string Movement,
        double TempoBpm,
        string TempoBucketTag,
        string VocalsTag,
        string EnergyTag,
        string Scene,
        string Mood,
        double Rating);

    private readonly Dictionary<string, InlineDraftSnapshot> _inlineUnsavedDrafts = new(StringComparer.Ordinal);
    private DateTime _lastEscRevertPromptUtc = DateTime.MinValue;
    private IReadOnlyList<TrackItem> _inlineTargets = [];
    private bool _syncingInlineEditor;
    private int _inlineDirtyCount;

    private static string BuildInlineTargetsKey(IReadOnlyList<TrackItem> targets) =>
        targets.Count == 0 ? "" : string.Join("|", targets.Select(t => t.TrackUid).OrderBy(x => x, StringComparer.Ordinal));

    private InlineDraftSnapshot CaptureInlineDraftSnapshot() =>
        new(
            Title: _inlineTargets.Count == 1 ? InlineTitleBox.Text : null,
            Artists: InlineArtistsBox.Text,
            Album: InlineAlbumBox.Text,
            Composers: InlineComposersBox.Text,
            Conductors: InlineConductorsBox.Text,
            Year: InlineYearBox.Text,
            Publisher: InlinePublisherBox.Text,
            Genre: InlineGenreBox.Text,
            Work: InlineWorkBox.Text,
            Movement: InlineMovementBox.Text,
            TempoBpm: InlineTempoBpmBox.Value,
            TempoBucketTag: GetComboSelectedTag(InlineTempoBucketCombo) ?? "",
            VocalsTag: GetComboSelectedTag(InlineVocalsCombo) ?? "unknown",
            EnergyTag: GetComboSelectedTag(InlineEnergyCombo) ?? "",
            Scene: InlineSceneBox.Text,
            Mood: InlineMoodBox.Text,
            Rating: InlineRatingControl.Value);

    private void ApplyInlineDraftSnapshot(InlineDraftSnapshot draft)
    {
        if (draft.Title is not null && InlineTitleBox.IsEnabled) InlineTitleBox.Text = draft.Title;
        InlineArtistsBox.Text = draft.Artists;
        InlineAlbumBox.Text = draft.Album;
        InlineComposersBox.Text = draft.Composers;
        InlineConductorsBox.Text = draft.Conductors;
        InlineYearBox.Text = draft.Year;
        InlinePublisherBox.Text = draft.Publisher;
        InlineGenreBox.Text = draft.Genre;
        InlineWorkBox.Text = draft.Work;
        InlineMovementBox.Text = draft.Movement;
        InlineTempoBpmBox.Value = draft.TempoBpm;
        SelectComboByTag(InlineTempoBucketCombo, draft.TempoBucketTag);
        SelectComboByTag(InlineVocalsCombo, draft.VocalsTag);
        SelectComboByTag(InlineEnergyCombo, draft.EnergyTag);
        InlineSceneBox.Text = draft.Scene;
        InlineMoodBox.Text = draft.Mood;
        InlineRatingControl.Value = draft.Rating;
        InlineClassicalExpander.IsExpanded = TrackItem.IsClassicalGenre(InlineGenreBox.Text);
    }

    private void TryHandleEscapeWithDirtyCheck()
    {
        var now = DateTime.UtcNow;
        if (now - _lastEscRevertPromptUtc <= TimeSpan.FromSeconds(4))
        {
            _lastEscRevertPromptUtc = DateTime.MinValue;
            RevertInlineEditor();
            return;
        }
        _lastEscRevertPromptUtc = now;
        ViewModel.Notify($"当前有 {_inlineDirtyCount} 项未保存编辑，4 秒内再按一次 Esc 将放弃修改（或按 Ctrl+S 保存）。", "warning");
    }

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
            _inlineUnsavedDrafts[BuildInlineTargetsKey(_inlineTargets)] = CaptureInlineDraftSnapshot();
            string label = _inlineTargets.Count == 1 ? $"「{_inlineTargets[0].Title}」" : $"{_inlineTargets.Count} 首曲目";
            ViewModel.Notify($"已暂存对{label}的 {_inlineDirtyCount} 项未保存草稿（切回该曲目可继续编辑或保存）。", "info");
        }

        _inlineTargets = targets.ToList();
        ViewModel.SetHasInlineTarget(_inlineTargets.Count > 0);
        _syncingInlineEditor = true;
        try
        {
            PopulateInlineEditor(targets);
            string targetKey = BuildInlineTargetsKey(_inlineTargets);
            if (!string.IsNullOrEmpty(targetKey) && _inlineUnsavedDrafts.TryGetValue(targetKey, out var savedDraft))
            {
                ApplyInlineDraftSnapshot(savedDraft);
            }
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
            TryHandleEscapeWithDirtyCheck();
            e.Handled = true;
        }
    }

    private void OnRevertInlineInspectorClick(object sender, RoutedEventArgs e) => RevertInlineEditor();

    private void RevertInlineEditor()
    {
        string targetKey = BuildInlineTargetsKey(_inlineTargets);
        if (!string.IsNullOrEmpty(targetKey))
        {
            _inlineUnsavedDrafts.Remove(targetKey);
        }
        _lastEscRevertPromptUtc = DateTime.MinValue;
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
        string savedKey = BuildInlineTargetsKey(targets);
        if (!string.IsNullOrEmpty(savedKey))
        {
            _inlineUnsavedDrafts.Remove(savedKey);
        }
        _lastEscRevertPromptUtc = DateTime.MinValue;
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
                    var missingIndices = new List<int>();
                    for (int i = 0; i < ViewModel.FilteredTracks.Count; i++)
                    {
                        var item = ViewModel.FilteredTracks[i];
                        if (savedUids.Contains(item.TrackUid))
                        {
                            restored.Add(item);
                            if (!currentSelectedUids.Contains(item.TrackUid))
                            {
                                missingIndices.Add(i);
                            }
                        }
                    }
                    SelectTrackIndicesByRanges(TrackListView, missingIndices);
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
            "Calm", "Chill", "Mellow", "Soothing", "Contemplative", "Melancholic",
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
        FrameworkElement anchor =
            ViewModel.IsCollectionOrStudioMainVisible && ViewModel.IsTrackTableVisible && LexiconTagToolbarButton is not null
                ? LexiconTagToolbarButton
                : (InspectorSplitView?.IsPaneOpen == true && InspectorLexiconTagButton is not null
                    ? InspectorLexiconTagButton
                    : (FrameworkElement?)InspectorToggleButton ?? RootWorkbench);
        LexiconTagMatrixFlyout.ShowAt(anchor);
    }

}
