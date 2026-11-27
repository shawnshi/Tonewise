using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tonewise.SyncHost.Workbench;
using Windows.UI;

namespace Tonewise.Desktop.Models;

public static class ClassicalUiPalette
{
    public static readonly Color ZuneMagentaColor = Color.FromArgb(0xFF, 0xFF, 0x00, 0x99);
    public static readonly SolidColorBrush ZuneMagentaBrush = new(ZuneMagentaColor);
    public static readonly SolidColorBrush MutedBrush = new(Color.FromArgb(0xCC, 0x9E, 0x9E, 0xA8));
    public static readonly SolidColorBrush SubtleBorderBrush = new(Color.FromArgb(0x44, 0x80, 0x80, 0x90));
    public static readonly SolidColorBrush MatrixTileActiveBrush = new(ZuneMagentaColor);
    public static readonly SolidColorBrush MatrixTileIdleBrush = new(Color.FromArgb(0xFF, 0x16, 0x16, 0x16));
    public static readonly SolidColorBrush MatrixTileDisabledBrush = new(Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0B));
    public static readonly SolidColorBrush WhiteBrush = new(Colors.White);
    public static readonly SolidColorBrush DimTextBrush = new(Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
    public static readonly SolidColorBrush SelectionTintBrush = new(Color.FromArgb(0x22, 0xFF, 0x00, 0x99));
    public static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    public static Brush PickAccentOrMuted(bool active) => active ? ZuneMagentaBrush : MutedBrush;
    public static Brush PickAccentOrSubtleBorder(bool active) => active ? ZuneMagentaBrush : SubtleBorderBrush;
    public static Brush PickSelectionBackground(bool selected) => selected ? SelectionTintBrush : TransparentBrush;
    public static Brush PickSelectionBorder(bool selected) => selected ? ZuneMagentaBrush : TransparentBrush;
    public static Brush PickMatrixTileBackground(bool activeOrAvailable) => activeOrAvailable ? MatrixTileActiveBrush : MatrixTileDisabledBrush;
    public static Brush PickEraTileBackground(bool active) => active ? MatrixTileActiveBrush : MatrixTileIdleBrush;
    public static Brush PickMatrixText(bool activeOrAvailable) => activeOrAvailable ? WhiteBrush : DimTextBrush;
}

public sealed class ClassicalComposerViewItem
{
    private BitmapImage? _portraitBitmap;
    private bool _portraitLoaded;

    public ClassicalComposerViewItem(ClassicalComposerNode<TrackItem> node)
    {
        Node = node;
        HasLocalPortrait = !string.IsNullOrWhiteSpace(node.LocalPortraitPath) && File.Exists(node.LocalPortraitPath);
    }

    public ClassicalComposerNode<TrackItem> Node { get; }
    public string ComposerName => Node.ComposerName;
    public string DisplayLowercase => Node.DisplayLowercase;
    public string? Era => Node.Era;
    public string Initial => Node.Initial;
    public string InitialLowercase => Node.Initial.ToLowerInvariant();
    public string StatsLine => Node.StatsLine;
    public string? LocalPortraitPath => Node.LocalPortraitPath;
    public bool HasLocalPortrait { get; }

    public BitmapImage? PortraitBitmap
    {
        get
        {
            if (!HasLocalPortrait) return null;
            if (!_portraitLoaded)
            {
                _portraitLoaded = true;
                try
                {
                    var bmp = new BitmapImage(new Uri(Node.LocalPortraitPath!))
                    {
                        DecodePixelWidth = 88,
                    };
                    _portraitBitmap = bmp;
                }
                catch
                {
                    _portraitBitmap = null;
                }
            }
            return _portraitBitmap;
        }
    }
}

public sealed partial class ClassicalMovementViewItem : ObservableObject
{
    public ClassicalMovementViewItem(
        string workKey,
        ClassicalMovementNode<TrackItem> node,
        bool isPlaying,
        bool isSelected = false,
        bool isMultiSelectVisible = false)
    {
        WorkKey = workKey;
        Node = node;
        _isPlaying = isPlaying;
        _isSelected = isSelected;
        _isMultiSelectVisible = isMultiSelectVisible;
    }

    public string WorkKey { get; }
    public ClassicalMovementNode<TrackItem> Node { get; }
    public int Index => Node.Index;
    public string RomanNumeral => Node.RomanNumeral;
    public string DisplayTitle => Node.DisplayTitle;
    public string DurationDisplay => Node.DurationDisplay;
    public string TrackUid => Node.TrackUid;
    public TrackItem Track => Node.Track;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isMultiSelectVisible;
}

public sealed partial class ClassicalRecordingViewItem : ObservableObject
{
    public ClassicalRecordingViewItem(string workKey, int index, ClassicalRecordingNode<TrackItem> node, bool isSelected)
    {
        WorkKey = workKey;
        Index = index;
        Node = node;
        _isSelected = isSelected;
    }

    public string WorkKey { get; }
    public int Index { get; }
    public ClassicalRecordingNode<TrackItem> Node { get; }
    public string ScopeKey => Node.ScopeKey;
    public string BadgeLabel => Node.BadgeLabel;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class ClassicalWorkViewItem : ObservableObject
{
    public ClassicalWorkViewItem(
        ClassicalWorkNode<TrackItem> node,
        bool isExpanded,
        string? activeTrackUid,
        IReadOnlySet<string>? selectedTrackUids = null,
        bool isMultiSelectVisible = false)
    {
        Node = node;
        _isExpanded = isExpanded;
        _isMultiSelectVisible = isMultiSelectVisible;
        for (int i = 0; i < node.Recordings.Count; i++)
        {
            Recordings.Add(new ClassicalRecordingViewItem(node.Key, i, node.Recordings[i], i == 0));
        }
        RebuildActiveMovements(activeTrackUid, selectedTrackUids);
    }

    public ClassicalWorkNode<TrackItem> Node { get; }
    public string Key => Node.Key;
    public string WorkTitle => Node.WorkTitle;
    public string ComposerName => Node.ComposerName;
    public string VersionCountLabel => Node.VersionCountLabel;
    public ObservableCollection<ClassicalRecordingViewItem> Recordings { get; } = [];
    public ObservableCollection<ClassicalMovementViewItem> ActiveMovements { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private int _selectedRecordingIndex;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isMultiSelectVisible;

    public IReadOnlyList<TrackItem> SelectedRecordingTracks
    {
        get
        {
            int idx = Math.Clamp(SelectedRecordingIndex, 0, Math.Max(0, Node.Recordings.Count - 1));
            return Node.Recordings.Count > 0
                ? Node.Recordings[idx].Movements.Select(m => m.Track).ToList()
                : [];
        }
    }

    public void SelectRecording(int recIndex, string? activeTrackUid, IReadOnlySet<string>? selectedTrackUids = null)
    {
        if (Node.Recordings.Count == 0) return;
        int clamped = Math.Clamp(recIndex, 0, Node.Recordings.Count - 1);
        SelectedRecordingIndex = clamped;
        for (int i = 0; i < Recordings.Count; i++)
        {
            Recordings[i].IsSelected = i == clamped;
        }
        RebuildActiveMovements(activeTrackUid, selectedTrackUids);
    }

    public void UpdatePlayingTrackUid(string? activeTrackUid)
    {
        foreach (var mov in ActiveMovements)
        {
            mov.IsPlaying = activeTrackUid is not null
                && string.Equals(mov.TrackUid, activeTrackUid, StringComparison.Ordinal);
        }
    }

    public void UpdateSelectionState(IReadOnlySet<string> selectedTrackUids)
    {
        foreach (var mov in ActiveMovements)
        {
            mov.IsSelected = selectedTrackUids.Contains(mov.TrackUid);
        }
        IsSelected = ActiveMovements.Count > 0 && ActiveMovements.All(m => m.IsSelected);
    }

    public void SetMultiSelectVisible(bool visible)
    {
        IsMultiSelectVisible = visible;
        foreach (var mov in ActiveMovements)
        {
            mov.IsMultiSelectVisible = visible;
        }
    }

    private void RebuildActiveMovements(string? activeTrackUid, IReadOnlySet<string>? selectedTrackUids)
    {
        ActiveMovements.Clear();
        if (Node.Recordings.Count == 0)
        {
            IsSelected = false;
            return;
        }
        int idx = Math.Clamp(SelectedRecordingIndex, 0, Node.Recordings.Count - 1);
        foreach (var movNode in Node.Recordings[idx].Movements)
        {
            bool playing = activeTrackUid is not null
                && string.Equals(movNode.TrackUid, activeTrackUid, StringComparison.Ordinal);
            bool selected = selectedTrackUids?.Contains(movNode.TrackUid) == true;
            ActiveMovements.Add(new ClassicalMovementViewItem(Key, movNode, playing, selected, IsMultiSelectVisible));
        }
        IsSelected = ActiveMovements.Count > 0 && ActiveMovements.All(m => m.IsSelected);
    }
}

public sealed partial class ClassicalJumpTileItem : ObservableObject
{
    public ClassicalJumpTileItem(string key, bool isAvailable, bool isSelected = false)
    {
        Key = key;
        _isAvailable = isAvailable;
        _isSelected = isSelected;
    }

    public string Key { get; }

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private bool _isSelected;
}
