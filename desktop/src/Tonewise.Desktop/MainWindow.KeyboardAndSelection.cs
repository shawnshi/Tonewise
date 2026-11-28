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
    // ---------------- Destructive / bulk staging confirmations ----------------

    private async void OnApproveAllHighConfidenceClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
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
        }, "批量批准提案");
    }

    private async void OnClearPendingStagingClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
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
        }, "清除待审提案");
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
            if (sender is ListView listView && !ReferenceEquals(listView.SelectedItem, rightClicked))
            {
                listView.SelectedItem = rightClicked;
            }
        }
    }

    private async void OnSendSelectedEntityGroupToPhoneClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(() => ViewModel.SendEntityGroupToPhoneAsync(ViewModel.SelectedEntityGroup), "发送实体组到手机");
    }

    private async void OnContextSendEntityGroupToPhoneClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
        {
            var group = _rightClickedEntityGroup ?? ViewModel.SelectedEntityGroup;
            _rightClickedEntityGroup = null;
            await ViewModel.SendEntityGroupToPhoneAsync(group);
        }, "发送实体组到手机");
    }


    private void RootWorkbench_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.IsNowPlayingViewVisible)
        {
            RestoreNowPlayingChrome();
        }
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

        if ((alt && !ctrl && !shift && e.Key == VirtualKey.Left) || e.Key == VirtualKey.GoBack)
        {
            if (ViewModel.CanNavigateBack)
            {
                ViewModel.GoBack();
                e.Handled = true;
            }
            return;
        }

        if (ctrl && !alt && !shift && e.Key == VirtualKey.A)
        {
            if (IsTrackListActiveAndPopulated() || IsClassicalComposerDetailSelectionActive())
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
                TryHandleEscapeWithDirtyCheck();
                e.Handled = true;
                return;
            }
            if (ViewModel.IsNavClassicalWorks && ViewModel.IsClassicalJumpMatrixOpen)
            {
                ViewModel.CloseClassicalJumpMatrix();
                e.Handled = true;
                return;
            }
            if (ViewModel.IsNavClassicalWorks && ViewModel.IsClassicalComposerDetailOpen)
            {
                ViewModel.CloseClassicalComposerDetail();
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
            return;
        }

        if (ViewModel.IsNowPlayingViewVisible && !ctrl && !alt && e.Key == VirtualKey.V)
        {
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

    private void RootWorkbench_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((int)e.Key == 188 &&
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
        {
            ViewModel.NavigateToPrimary("settings");
            e.Handled = true;
        }
    }

    private bool IsTextInputFocused() => IsInteractiveInputFocused(includeComboBox: false);

    private bool IsInteractiveInputFocused(bool includeComboBox = true)
    {
        if (RootWorkbench?.XamlRoot is null) return false;
        object? focused = FocusManager.GetFocusedElement(RootWorkbench.XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox
            || (includeComboBox && focused is ComboBox))
            return true;
        for (var cur = focused as DependencyObject; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox
                || (includeComboBox && cur is ComboBox))
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

    private bool IsClassicalComposerDetailSelectionActive() =>
        ViewModel.IsNavClassicalWorks
        && ViewModel.IsClassicalComposerDetailOpen
        && ViewModel.ClassicalVisibleWorks.Count > 0;

    private bool IsTrackListActiveAndPopulated() =>
        TrackListView is not null
        && TrackListView.Visibility == Visibility.Visible
        && !ViewModel.IsEntityBrowserVisible
        && ViewModel.FilteredTracks.Count > 0;

    private void SelectAllTracksPreservingFocus()
    {
        if (IsClassicalComposerDetailSelectionActive())
        {
            SetInspectorOpen(true);
            var all = ViewModel.SelectAllVisibleClassicalTracks();
            SyncInlineInspectorTargets(all);
            return;
        }
        if (TrackListView is null || ViewModel.FilteredTracks.Count == 0) return;
        _suppressInspectorFocusSteal = true;
        try
        {
            SetInspectorOpen(true);
            TrackListView.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(0, (uint)ViewModel.FilteredTracks.Count));
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
        var playlist = ViewModel.SelectedPlaylist;
        var removeSet = TrackListView.SelectedItems.OfType<TrackItem>().ToHashSet();
        if (removeSet.Count == 0) return;
        var previousEntries = ViewModel.FilteredTracks
            .Select(ViewModel.ToPlaylistEntry)
            .ToList();
        var remaining = ViewModel.FilteredTracks
            .Where(t => !removeSet.Contains(t))
            .Select(ViewModel.ToPlaylistEntry)
            .ToList();
        ViewModel.SaveEditedPlaylist(playlist, playlist.Name, remaining);
        ShowUndoableNotification(
            $"已从歌单「{playlist.Name}」移除 {removeSet.Count} 首曲目。",
            () =>
            {
                ViewModel.SaveEditedPlaylist(playlist, playlist.Name, previousEntries);
                ViewModel.Notify($"已撤销：恢复歌单「{playlist.Name}」中移除的 {removeSet.Count} 首曲目。", "success");
            },
            "info");
    }

    private void OnToggleInspectorShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetInspectorOpen(!InspectorSplitView.IsPaneOpen);
        args.Handled = true;
    }

    private void OnRootWorkbenchSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTransportAndCaptionForWidth(e.NewSize.Width);
        if (InspectorSplitView is null) return;
        var mode = e.NewSize.Width >= 1160 ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        if (InspectorSplitView.DisplayMode != mode)
        {
            InspectorSplitView.DisplayMode = mode;
            ApplyInspectorState();
        }
    }


    private void OnSceneMenuFlyoutOpening(object sender, object e) =>
        SyncRadioMenuFlyoutSelection(sender, ViewModel.FilterScene);

    private void OnEnergyMenuFlyoutOpening(object sender, object e) =>
        SyncRadioMenuFlyoutSelection(sender, ViewModel.FilterEnergy);

    private void OnVocalsMenuFlyoutOpening(object sender, object e) =>
        SyncRadioMenuFlyoutSelection(sender, ViewModel.CurrentVocalsOptionTag);

    private void OnTempoMenuFlyoutOpening(object sender, object e) =>
        SyncRadioMenuFlyoutSelection(sender, ViewModel.FilterTempoBucket);

    private void OnMoodMenuFlyoutOpening(object sender, object e) =>
        SyncRadioMenuFlyoutSelection(sender, ViewModel.FilterMoodCluster);

    private void OnArcMenuFlyoutOpening(object sender, object e) =>
        SyncRadioMenuFlyoutSelection(sender, ViewModel.SelectedEnergyArc);

    private void OnSceneFilterItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag }) ViewModel.FilterScene = tag;
    }

    private void OnEnergyFilterItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag }) ViewModel.FilterEnergy = tag;
    }

    private void OnVocalsFilterItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag }) ViewModel.SetVocalsFilterOption(tag);
    }

    private void OnTempoFilterItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag }) ViewModel.FilterTempoBucket = tag;
    }

    private void OnMoodFilterItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag }) ViewModel.FilterMoodCluster = tag;
    }

    private void OnArcFilterItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag }) ViewModel.SetEnergyArc(tag);
    }

    private void OnGenreFilterFlyoutOpening(object sender, object e)
    {
        if (sender is not Flyout flyout) return;

        bool isLight = RootWorkbench?.ActualTheme == ElementTheme.Light;
        var accentBrush = new SolidColorBrush(Color.FromArgb(0xFF, _currentAccentRgb.R, _currentAccentRgb.G, _currentAccentRgb.B));
        var mutedBrush = new SolidColorBrush(isLight
            ? Color.FromArgb(0xFF, 0x52, 0x52, 0x52)
            : Color.FromArgb(0xFF, 0xA3, 0xA3, 0xA3));

        var allFacets = ViewModel.GetAvailableGenreFacets();

        var root = new StackPanel
        {
            Width = 310,
            Spacing = 8,
        };

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = "流派筛选",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleText, 0);

        var countText = new TextBlock
        {
            FontSize = 11,
            Foreground = mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 1);

        var clearBtn = new Button
        {
            Content = "清空",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(clearBtn, 2);

        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(countText);
        headerGrid.Children.Add(clearBtn);
        root.Children.Add(headerGrid);

        var searchBox = new TextBox
        {
            PlaceholderText = "搜索流派（如 Classical、Jazz、Pop）...",
            FontSize = 12,
            Height = 30,
        };
        root.Children.Add(searchBox);

        var listPanel = new StackPanel { Spacing = 2 };
        var scroll = new ScrollViewer
        {
            MaxHeight = 290,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = listPanel,
        };
        root.Children.Add(scroll);

        void SyncHeader()
        {
            int count = ViewModel.SelectedGenres.Count;
            countText.Text = count > 0 ? $"已选 {count} 项" : $"共 {allFacets.Count} 类";
            clearBtn.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RenderRows(string query)
        {
            listPanel.Children.Clear();
            string q = query.Trim();

            var matches = allFacets
                .Where(f => string.IsNullOrEmpty(q) || f.CanonicalName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = allFacets.Count == 0 ? "当前曲库暂无流派标签" : $"未找到匹配 \"{q}\" 的流派",
                    FontSize = 12,
                    Foreground = mutedBrush,
                    Margin = new Thickness(4, 8, 4, 8),
                });
                return;
            }

            foreach (var facet in matches)
            {
                var row = new Grid
                {
                    ColumnSpacing = 4,
                    Padding = new Thickness(2, 1, 2, 1),
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var check = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = facet.CanonicalName,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    IsChecked = ViewModel.IsGenreSelected(facet.CanonicalName),
                    FontSize = 12,
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                check.Click += (_, _) =>
                {
                    ViewModel.ToggleGenreFilter(facet.CanonicalName);
                    SyncHeader();
                };
                Grid.SetColumn(check, 0);

                var onlyBtn = new Button
                {
                    Content = "仅选",
                    FontSize = 11,
                    Padding = new Thickness(6, 2, 6, 2),
                    Height = 24,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = accentBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTipService.SetToolTip(onlyBtn, $"仅按 {facet.CanonicalName} 筛选");
                onlyBtn.Click += (_, _) =>
                {
                    ViewModel.SelectSingleGenreFilter(facet.CanonicalName);
                    flyout.Hide();
                };
                Grid.SetColumn(onlyBtn, 1);

                var totalText = new TextBlock
                {
                    Text = facet.TrackCount.ToString("N0"),
                    FontSize = 11,
                    Foreground = mutedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 2, 0),
                };
                Grid.SetColumn(totalText, 2);

                row.Children.Add(check);
                row.Children.Add(onlyBtn);
                row.Children.Add(totalText);
                listPanel.Children.Add(row);
            }
        }

        clearBtn.Click += (_, _) =>
        {
            ViewModel.ClearGenreFilters();
            SyncHeader();
            RenderRows(searchBox.Text ?? "");
        };

        searchBox.TextChanged += (_, _) => RenderRows(searchBox.Text ?? "");

        SyncHeader();
        RenderRows("");
        flyout.Content = root;
    }

    private async void OnFetchMissingBasicInfoClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
        {
            var targets = _inlineTargets.Count > 0
                ? _inlineTargets
                : (ViewModel.InspectedTrack is not null ? [ViewModel.InspectedTrack] : Array.Empty<TrackItem>());
            await ViewModel.FetchMissingBasicInfoForTracksAsync(targets);
            CoverArtImageCache.Clear();
            SyncInlineInspectorAfterEnrichment(targets);
        }, "补全基础信息");
    }

    private async void OnFetchMissing5DTagsClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(async () =>
        {
            var targets = _inlineTargets.Count > 0
                ? _inlineTargets
                : (ViewModel.InspectedTrack is not null ? [ViewModel.InspectedTrack] : Array.Empty<TrackItem>());
            await ViewModel.FetchMissing5DTagsForTracksAsync(targets);
            SyncInlineInspectorAfterEnrichment(targets);
        }, "补全五维标签");
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

    private static BitmapImage? GetOrCreateRowCoverBitmap(string? coverArtPath, int decodeWidth = 80) =>
        CoverArtImageCache.GetBitmap(coverArtPath, decodeWidth);

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
                var missingIndices = new List<int>();
                var currentlySelectedUids = TrackListView.SelectedItems
                    .OfType<TrackItem>()
                    .Select(t => t.TrackUid)
                    .ToHashSet(StringComparer.Ordinal);
                for (int i = 0; i < ViewModel.FilteredTracks.Count; i++)
                {
                    var item = ViewModel.FilteredTracks[i];
                    if (restoreUids.Contains(item.TrackUid))
                    {
                        restored.Add(item);
                        if (!currentlySelectedUids.Contains(item.TrackUid))
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
                TrackListView.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(start, (uint)(end - start + 1)));
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
        ViewModel.SyncClassicalMultiSelectMode();
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

    private TrackItem? ResolveContextTrack(object sender)
    {
        object? ctx = (sender as FrameworkElement)?.Tag ?? (sender as FrameworkElement)?.DataContext;
        if (ctx is TrackItem item) return item;
        if (ctx is ClassicalMovementViewItem mov) return mov.Track;
        if (ctx is ClassicalWorkViewItem work) return work.SelectedRecordingTracks.FirstOrDefault();
        if (ViewModel.IsNavClassicalWorks && ViewModel.IsClassicalComposerDetailOpen)
        {
            return ViewModel.GetSelectedClassicalTracks().FirstOrDefault() ?? ViewModel.InspectedTrack;
        }
        return TrackListView.SelectedItem as TrackItem;
    }

    private IReadOnlyList<TrackItem> ResolveTargetTracksForAction(object sender)
    {
        object? ctx = (sender as FrameworkElement)?.Tag ?? (sender as FrameworkElement)?.DataContext;
        if (ctx is ClassicalWorkViewItem work)
        {
            var workTracks = work.SelectedRecordingTracks;
            var classicalSelected = ViewModel.GetSelectedClassicalTracks();
            if (classicalSelected.Count > 1
                && workTracks.Any(wt => classicalSelected.Any(sel => sel.TrackUid == wt.TrackUid)))
            {
                return classicalSelected;
            }
            return workTracks;
        }
        if (ctx is ClassicalMovementViewItem mov)
        {
            var classicalSelected = ViewModel.GetSelectedClassicalTracks();
            if (classicalSelected.Count > 1 && classicalSelected.Any(s => s.TrackUid == mov.TrackUid))
            {
                return classicalSelected;
            }
            return [mov.Track];
        }
        if (ViewModel.IsNavClassicalWorks && ViewModel.IsClassicalComposerDetailOpen)
        {
            var classicalSelected = ViewModel.GetSelectedClassicalTracks();
            if (classicalSelected.Count > 0) return classicalSelected;
            if (ViewModel.InspectedTrack is not null) return [ViewModel.InspectedTrack];
            if (ViewModel.CurrentTrack is not null) return [ViewModel.CurrentTrack];
            return [];
        }

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

    private async void OnContextToggleLovedClick(object sender, RoutedEventArgs e)
    {
        var targets = ResolveTargetTracksForAction(sender);
        if (targets.Count > 0)
        {
            await RunSafeUiActionAsync(() => ViewModel.ToggleLovedForTracksAsync(targets), "切换喜爱状态");
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
        await RunSafeUiActionAsync(() => ViewModel.SendTracksToPhoneAsync(ResolveTargetTracksForAction(sender)), "发送曲目到手机");
    }

    private async void OnContextSendTracksToPhoneClick(object sender, RoutedEventArgs e)
    {
        await RunSafeUiActionAsync(() => ViewModel.SendTracksToPhoneAsync(ResolveTargetTracksForAction(sender)), "发送曲目到手机");
    }

    private void OnOpenLexiconTagShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsInteractiveInputFocused())
            return;

        args.Handled = true;
        ShowLexiconTagMatrixFlyout(ResolveTargetTracksForAction(this));
    }

    private void OnF2BasicMetadataShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsInteractiveInputFocused())
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
        if (targets.Count == 0 && IsClassicalComposerDetailSelectionActive())
        {
            targets = ViewModel.SelectClassicalWorkTracks(ViewModel.ClassicalVisibleWorks[0], false, false);
        }
        else if (targets.Count == 0 && ViewModel.FilteredTracks.Count > 0 && TrackListView is not null)
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

    private static void UpdateCoverArtImage(Image? target, string? coverArtPath, int decodeWidth = 160)
    {
        if (target is null) return;
        target.Source = CoverArtImageCache.GetBitmap(coverArtPath, decodeWidth);
    }

    private static void SelectTrackIndicesByRanges(ListView listView, IReadOnlyList<int> sortedIndices)
    {
        if (sortedIndices.Count == 0) return;
        int runStart = sortedIndices[0];
        int prev = runStart;
        for (int i = 1; i < sortedIndices.Count; i++)
        {
            int idx = sortedIndices[i];
            if (idx == prev + 1)
            {
                prev = idx;
                continue;
            }
            listView.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(runStart, (uint)(prev - runStart + 1)));
            runStart = idx;
            prev = idx;
        }
        listView.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(runStart, (uint)(prev - runStart + 1)));
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

}
