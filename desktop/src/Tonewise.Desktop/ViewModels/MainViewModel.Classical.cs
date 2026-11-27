using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tonewise.Desktop.Models;
using Tonewise.SyncHost;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IReadOnlyList<ClassicalComposerNode<TrackItem>> _allClassicalComposerNodes = [];
    private IReadOnlyList<ClassicalComposerViewItem> _allClassicalComposerItems = [];
    private Dictionary<string, IReadOnlyList<ClassicalComposerViewItem>> _classicalComposerItemsByEra = new(StringComparer.OrdinalIgnoreCase);
    private long _classicalHierarchyBuiltVersion = -1;
    private bool _classicalJumpMatrixDirty = true;
    private string? _pendingClassicalComposerToOpen;
    private readonly HashSet<string> _selectedClassicalTrackUids = new(StringComparer.Ordinal);
    private string? _classicalSelectionAnchorTrackUid;

    public bool IsWorkbenchCommandBarVisible =>
        IsTrackTableVisible || (IsNavClassicalWorks && IsClassicalComposerDetailOpen);

    public BulkObservableCollection<ClassicalComposerViewItem> ClassicalComposers { get; } = [];
    public BulkObservableCollection<ClassicalWorkViewItem> ClassicalVisibleWorks { get; } = [];
    public ObservableCollection<ClassicalJumpTileItem> ClassicalJumpEraTiles { get; } = [];
    public ObservableCollection<ClassicalJumpTileItem> ClassicalJumpInitialTiles { get; } = [];

    [ObservableProperty]
    private bool _hasClassicalComposers;

    [ObservableProperty]
    private bool _isClassicalComposerDetailOpen;

    [ObservableProperty]
    private bool _isClassicalComposerIndexVisible = true;

    [ObservableProperty]
    private bool _isClassicalJumpMatrixOpen;

    [ObservableProperty]
    private string _classicalSelectedEra = "";

    [ObservableProperty]
    private string _classicalSelectedForm = "all";

    [ObservableProperty]
    private string _classicalDetailComposerLower = "";

    [ObservableProperty]
    private string _classicalDetailStatsLine = "";

    private ClassicalComposerNode<TrackItem>? _selectedClassicalComposerNode;

    public bool IsClassicalEraAll => string.IsNullOrWhiteSpace(ClassicalSelectedEra);
    public bool IsClassicalEraBaroque => ClassicalSelectedEra == "baroque";
    public bool IsClassicalEraClassical => ClassicalSelectedEra == "classical";
    public bool IsClassicalEraRomantic => ClassicalSelectedEra == "romantic";
    public bool IsClassicalEraModern => ClassicalSelectedEra == "modern";

    public bool IsClassicalFormAll => ClassicalSelectedForm == "all";
    public bool IsClassicalFormSymphonies => ClassicalSelectedForm == "symphonies";
    public bool IsClassicalFormConcertos => ClassicalSelectedForm == "concertos";
    public bool IsClassicalFormChamber => ClassicalSelectedForm == "chamber";
    public bool IsClassicalFormSolo => ClassicalSelectedForm == "solo";

    partial void OnClassicalSelectedEraChanged(string value)
    {
        OnPropertyChanged(nameof(IsClassicalEraAll));
        OnPropertyChanged(nameof(IsClassicalEraBaroque));
        OnPropertyChanged(nameof(IsClassicalEraClassical));
        OnPropertyChanged(nameof(IsClassicalEraRomantic));
        OnPropertyChanged(nameof(IsClassicalEraModern));
    }

    partial void OnClassicalSelectedFormChanged(string value)
    {
        OnPropertyChanged(nameof(IsClassicalFormAll));
        OnPropertyChanged(nameof(IsClassicalFormSymphonies));
        OnPropertyChanged(nameof(IsClassicalFormConcertos));
        OnPropertyChanged(nameof(IsClassicalFormChamber));
        OnPropertyChanged(nameof(IsClassicalFormSolo));
    }

    partial void OnIsClassicalComposerDetailOpenChanged(bool value)
    {
        IsClassicalComposerIndexVisible = !value;
        if (!value)
        {
            ClearClassicalSelection();
        }
        OnPropertyChanged(nameof(IsWorkbenchCommandBarVisible));
        UpdateCanNavigateBack();
    }

    partial void OnIsTrackTableVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorkbenchCommandBarVisible));
    }

    partial void OnIsNavClassicalWorksChanged(bool value)
    {
        if (!value)
        {
            ClearClassicalSelection();
        }
        OnPropertyChanged(nameof(IsWorkbenchCommandBarVisible));
    }

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        SyncClassicalMultiSelectMode();
    }

    private void RebuildClassicalHierarchyIndex(bool force = false)
    {
        if (!force && _classicalHierarchyBuiltVersion == _libraryDataVersion && _allClassicalComposerNodes.Count > 0)
        {
            ApplyClassicalComposerFilter();
            return;
        }

        var portraitDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(_store.MusicRoot))
        {
            portraitDirs.Add(Path.Combine(_store.MusicRoot, ".tonewise", "composers"));
        }

        var seeds = _allTracks.Select(t => new ClassicalTrackSeed<TrackItem>(
            Track: t,
            TrackUid: t.TrackUid,
            Title: t.Title,
            Album: t.Album,
            ArtistsDisplay: t.ArtistsDisplay,
            ComposersDisplay: t.ComposersDisplay,
            ConductorsDisplay: t.ConductorsDisplay,
            WorkName: string.IsNullOrWhiteSpace(t.WorkName) ? null : t.WorkName,
            MovementName: string.IsNullOrWhiteSpace(t.MovementName) ? null : t.MovementName,
            MovementNumber: t.MovementNumber,
            DiscNumber: t.DiscNumber,
            TrackNumber: t.TrackNumber,
            Year: t.Year,
            Genre: t.Genre,
            DurationMs: t.DurationMs));

        _allClassicalComposerNodes = ClassicalHierarchyBuilder.Build(
            seeds,
            SplitComposers,
            SplitPerformers,
            (album, cond) => WorkAwareShuffleEngine.BuildAlbumScope(album, cond),
            portraitDirs);

        var allItems = new List<ClassicalComposerViewItem>(_allClassicalComposerNodes.Count);
        var eraBuckets = new Dictionary<string, List<ClassicalComposerViewItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (string eraKey in ClassicalHierarchyBuilder.EraKeys)
        {
            eraBuckets[eraKey] = [];
        }
        foreach (var node in _allClassicalComposerNodes)
        {
            var viewItem = new ClassicalComposerViewItem(node);
            allItems.Add(viewItem);
            if (!string.IsNullOrWhiteSpace(node.Era) && eraBuckets.TryGetValue(node.Era, out var bucket))
            {
                bucket.Add(viewItem);
            }
        }
        _allClassicalComposerItems = allItems;
        _classicalComposerItemsByEra = eraBuckets.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ClassicalComposerViewItem>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
        _classicalHierarchyBuiltVersion = _libraryDataVersion;

        ApplyClassicalComposerFilter();

        if (!string.IsNullOrWhiteSpace(_pendingClassicalComposerToOpen))
        {
            string pending = _pendingClassicalComposerToOpen;
            _pendingClassicalComposerToOpen = null;
            OpenClassicalComposerByName(pending);
        }
        else if (_selectedClassicalComposerNode is not null)
        {
            var refreshed = _allClassicalComposerNodes.FirstOrDefault(c =>
                string.Equals(c.ComposerName, _selectedClassicalComposerNode.ComposerName, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null)
            {
                OpenClassicalComposerNode(refreshed, resetForm: false);
            }
            else
            {
                CloseClassicalComposerDetail();
            }
        }
    }

    private void ApplyClassicalComposerFilter()
    {
        IReadOnlyList<ClassicalComposerViewItem> eraItems = string.IsNullOrWhiteSpace(ClassicalSelectedEra)
            ? _allClassicalComposerItems
            : (_classicalComposerItemsByEra.TryGetValue(ClassicalSelectedEra.Trim(), out var bucket) ? bucket : []);

        string q = SearchQuery.Trim();
        IReadOnlyList<ClassicalComposerViewItem> filteredItems = string.IsNullOrEmpty(q)
            ? eraItems
            : eraItems.Where(c =>
                    c.ComposerName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(c.Era) && c.Era.Contains(q, StringComparison.OrdinalIgnoreCase))
                    || c.Node.Works.Any(w => w.WorkTitle.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        ClassicalComposers.ReplaceAll(filteredItems);
        HasClassicalComposers = _allClassicalComposerItems.Count > 0;
        _classicalJumpMatrixDirty = true;

        if (IsClassicalJumpMatrixOpen)
        {
            SyncClassicalJumpMatrixTiles();
        }
    }

    private void SyncClassicalJumpMatrixTiles()
    {
        if (!_classicalJumpMatrixDirty && ClassicalJumpEraTiles.Count > 0 && ClassicalJumpInitialTiles.Count > 0)
        {
            return;
        }

        if (ClassicalJumpEraTiles.Count == 0)
        {
            foreach (string era in ClassicalHierarchyBuilder.EraKeys)
            {
                ClassicalJumpEraTiles.Add(new ClassicalJumpTileItem(
                    era,
                    isAvailable: true,
                    isSelected: string.Equals(ClassicalSelectedEra, era, StringComparison.OrdinalIgnoreCase)));
            }
        }
        else
        {
            foreach (var tile in ClassicalJumpEraTiles)
            {
                tile.IsSelected = string.Equals(ClassicalSelectedEra, tile.Key, StringComparison.OrdinalIgnoreCase);
            }
        }

        var availableInitials = ClassicalComposers.Select(c => c.Initial).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ClassicalJumpInitialTiles.Count == 0)
        {
            foreach (string key in ClassicalHierarchyBuilder.JumpMatrixKeys)
            {
                ClassicalJumpInitialTiles.Add(new ClassicalJumpTileItem(
                    key,
                    isAvailable: availableInitials.Contains(key)));
            }
        }
        else
        {
            foreach (var tile in ClassicalJumpInitialTiles)
            {
                tile.IsAvailable = availableInitials.Contains(tile.Key);
            }
        }

        _classicalJumpMatrixDirty = false;
    }

    public void SetClassicalEraFilter(string? era)
    {
        if (_allTracks.Count > 0 && (_allClassicalComposerNodes.Count == 0 || _classicalHierarchyBuiltVersion != _libraryDataVersion))
        {
            RebuildClassicalHierarchyIndex();
        }
        string clean = (era ?? "").Trim().ToLowerInvariant();
        if (clean is "all" or "all eras") clean = "";
        string nextEra = string.Equals(ClassicalSelectedEra, clean, StringComparison.Ordinal) ? "" : clean;
        if (string.Equals(ClassicalSelectedEra, nextEra, StringComparison.Ordinal)
            && !IsClassicalComposerDetailOpen
            && (ClassicalComposers.Count > 0 || _allClassicalComposerItems.Count == 0))
        {
            return;
        }
        ClassicalSelectedEra = nextEra;
        IsClassicalComposerDetailOpen = false;
        _selectedClassicalComposerNode = null;
        ApplyClassicalComposerFilter();
        if (IsNavClassicalWorks)
        {
            CurrentViewHeader = $"古典馆藏 ({ClassicalComposers.Count} 位作曲家)";
        }
    }

    public void OpenClassicalJumpMatrix()
    {
        SyncClassicalJumpMatrixTiles();
        IsClassicalJumpMatrixOpen = true;
    }

    public void CloseClassicalJumpMatrix() => IsClassicalJumpMatrixOpen = false;

    public ClassicalComposerViewItem? ResolveJumpTargetForInitial(string? initial)
    {
        if (string.IsNullOrWhiteSpace(initial)) return null;
        string clean = initial.Trim().ToLowerInvariant();
        IsClassicalJumpMatrixOpen = false;
        IsClassicalComposerDetailOpen = false;
        _selectedClassicalComposerNode = null;
        return ClassicalComposers.FirstOrDefault(c => string.Equals(c.Initial, clean, StringComparison.OrdinalIgnoreCase));
    }

    public void OpenClassicalComposer(ClassicalComposerViewItem? item)
    {
        if (item is null) return;
        OpenClassicalComposerNode(item.Node, resetForm: true);
    }

    public void OpenClassicalComposerByName(string? composerName)
    {
        if (string.IsNullOrWhiteSpace(composerName)) return;
        if (_allClassicalComposerNodes.Count == 0 && _allTracks.Count > 0)
        {
            RebuildClassicalHierarchyIndex();
        }
        var match = _allClassicalComposerNodes.FirstOrDefault(c =>
                string.Equals(c.ComposerName, composerName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? _allClassicalComposerNodes.FirstOrDefault(c =>
                c.ComposerName.Contains(composerName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            OpenClassicalComposerNode(match, resetForm: true);
        }
        else
        {
            _pendingClassicalComposerToOpen = composerName.Trim();
        }
    }

    private void OpenClassicalComposerNode(ClassicalComposerNode<TrackItem> node, bool resetForm)
    {
        _selectedClassicalComposerNode = node;
        ClassicalDetailComposerLower = node.DisplayLowercase;
        ClassicalDetailStatsLine = node.StatsLine;
        if (resetForm) ClassicalSelectedForm = "all";
        IsClassicalJumpMatrixOpen = false;
        IsClassicalComposerDetailOpen = true;
        RebuildClassicalVisibleWorks();
    }

    public void CloseClassicalComposerDetail()
    {
        _selectedClassicalComposerNode = null;
        ClassicalDetailComposerLower = "";
        ClassicalDetailStatsLine = "";
        ClassicalVisibleWorks.Clear();
        IsClassicalComposerDetailOpen = false;
    }

    public void SetClassicalFormFilter(string? form)
    {
        ClassicalSelectedForm = string.IsNullOrWhiteSpace(form) ? "all" : form.Trim().ToLowerInvariant();
        RebuildClassicalVisibleWorks();
    }

    private void RebuildClassicalVisibleWorks()
    {
        if (_selectedClassicalComposerNode is null)
        {
            ClassicalVisibleWorks.Clear();
            return;
        }
        var previouslyExpanded = ClassicalVisibleWorks
            .Where(w => w.IsExpanded)
            .Select(w => w.Key)
            .ToHashSet(StringComparer.Ordinal);
        var previousRecordingIndex = ClassicalVisibleWorks
            .ToDictionary(w => w.Key, w => w.SelectedRecordingIndex, StringComparer.Ordinal);
        var works = ClassicalHierarchyBuilder.FilterByForm(_selectedClassicalComposerNode.Works, ClassicalSelectedForm);
        bool autoExpandAll = works.Count <= 3;
        string? curUid = CurrentTrack?.TrackUid;
        var built = new List<ClassicalWorkViewItem>(works.Count);
        foreach (var w in works)
        {
            bool expanded = autoExpandAll || previouslyExpanded.Contains(w.Key);
            var item = new ClassicalWorkViewItem(
                w,
                expanded,
                curUid,
                _selectedClassicalTrackUids,
                IsMultiSelectMode);
            if (previousRecordingIndex.TryGetValue(w.Key, out int prevRecIdx)
                && prevRecIdx > 0
                && prevRecIdx < item.Recordings.Count)
            {
                item.SelectRecording(prevRecIdx, curUid, _selectedClassicalTrackUids);
            }
            built.Add(item);
        }
        ClassicalVisibleWorks.ReplaceAll(built);
    }

    public void ToggleClassicalWorkExpanded(ClassicalWorkViewItem? workItem)
    {
        if (workItem is null) return;
        workItem.IsExpanded = !workItem.IsExpanded;
    }

    public void SelectClassicalRecording(ClassicalRecordingViewItem? recItem)
    {
        if (recItem is null) return;
        var workItem = ClassicalVisibleWorks.FirstOrDefault(w => string.Equals(w.Key, recItem.WorkKey, StringComparison.Ordinal));
        workItem?.SelectRecording(recItem.Index, CurrentTrack?.TrackUid, _selectedClassicalTrackUids);
    }

    public IReadOnlyList<TrackItem> GetAllVisibleClassicalTracks()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<TrackItem>();
        foreach (var work in ClassicalVisibleWorks)
        {
            foreach (var track in work.SelectedRecordingTracks)
            {
                if (seen.Add(track.TrackUid))
                {
                    ordered.Add(track);
                }
            }
        }
        return ordered;
    }

    public IReadOnlyList<TrackItem> GetSelectedClassicalTracks()
    {
        if (_selectedClassicalTrackUids.Count == 0) return [];
        return GetAllVisibleClassicalTracks()
            .Where(t => _selectedClassicalTrackUids.Contains(t.TrackUid))
            .ToList();
    }

    public IReadOnlyList<TrackItem> SelectClassicalWorkTracks(
        ClassicalWorkViewItem? workItem,
        bool additiveToggle,
        bool rangeSelect)
    {
        if (workItem is null) return [];
        var workTracks = workItem.SelectedRecordingTracks;
        if (workTracks.Count == 0) return [];

        if (rangeSelect && !string.IsNullOrEmpty(_classicalSelectionAnchorTrackUid))
        {
            var all = GetAllVisibleClassicalTracks();
            int anchorIdx = FindTrackIndexByUid(all, _classicalSelectionAnchorTrackUid);
            int targetIdx = additiveToggle
                ? FindTrackIndexByUid(all, workTracks[^1].TrackUid)
                : FindTrackIndexByUid(all, workTracks[0].TrackUid);
            if (anchorIdx >= 0 && targetIdx >= 0)
            {
                if (!additiveToggle)
                {
                    _selectedClassicalTrackUids.Clear();
                }
                int start = Math.Min(anchorIdx, targetIdx);
                int end = Math.Max(anchorIdx, targetIdx);
                for (int i = start; i <= end; i++)
                {
                    _selectedClassicalTrackUids.Add(all[i].TrackUid);
                }
                foreach (var track in workTracks)
                {
                    _selectedClassicalTrackUids.Add(track.TrackUid);
                }
                return FinalizeClassicalSelection(updateAnchor: null);
            }
        }

        if (additiveToggle)
        {
            bool allSelected = workTracks.All(t => _selectedClassicalTrackUids.Contains(t.TrackUid));
            if (allSelected)
            {
                foreach (var track in workTracks)
                {
                    _selectedClassicalTrackUids.Remove(track.TrackUid);
                }
            }
            else
            {
                foreach (var track in workTracks)
                {
                    _selectedClassicalTrackUids.Add(track.TrackUid);
                }
                _classicalSelectionAnchorTrackUid = workTracks[0].TrackUid;
            }
            return FinalizeClassicalSelection(updateAnchor: null);
        }

        _selectedClassicalTrackUids.Clear();
        foreach (var track in workTracks)
        {
            _selectedClassicalTrackUids.Add(track.TrackUid);
        }
        return FinalizeClassicalSelection(updateAnchor: workTracks[0].TrackUid);
    }

    public IReadOnlyList<TrackItem> SelectClassicalMovementTrack(
        ClassicalMovementViewItem? movItem,
        bool additiveToggle,
        bool rangeSelect)
    {
        if (movItem is null) return [];
        string uid = movItem.TrackUid;

        if (rangeSelect && !string.IsNullOrEmpty(_classicalSelectionAnchorTrackUid))
        {
            var all = GetAllVisibleClassicalTracks();
            int anchorIdx = FindTrackIndexByUid(all, _classicalSelectionAnchorTrackUid);
            int targetIdx = FindTrackIndexByUid(all, uid);
            if (anchorIdx >= 0 && targetIdx >= 0)
            {
                if (!additiveToggle)
                {
                    _selectedClassicalTrackUids.Clear();
                }
                int start = Math.Min(anchorIdx, targetIdx);
                int end = Math.Max(anchorIdx, targetIdx);
                for (int i = start; i <= end; i++)
                {
                    _selectedClassicalTrackUids.Add(all[i].TrackUid);
                }
                return FinalizeClassicalSelection(updateAnchor: null);
            }
        }

        if (additiveToggle)
        {
            if (!_selectedClassicalTrackUids.Remove(uid))
            {
                _selectedClassicalTrackUids.Add(uid);
                _classicalSelectionAnchorTrackUid = uid;
            }
            return FinalizeClassicalSelection(updateAnchor: null);
        }

        _selectedClassicalTrackUids.Clear();
        _selectedClassicalTrackUids.Add(uid);
        return FinalizeClassicalSelection(updateAnchor: uid);
    }

    public IReadOnlyList<TrackItem> SelectAllVisibleClassicalTracks()
    {
        _selectedClassicalTrackUids.Clear();
        var all = GetAllVisibleClassicalTracks();
        foreach (var track in all)
        {
            _selectedClassicalTrackUids.Add(track.TrackUid);
        }
        return FinalizeClassicalSelection(updateAnchor: all.FirstOrDefault()?.TrackUid);
    }

    public IReadOnlyList<TrackItem> RestoreClassicalSelection(IReadOnlyCollection<string> trackUids)
    {
        _selectedClassicalTrackUids.Clear();
        var all = GetAllVisibleClassicalTracks();
        foreach (var track in all)
        {
            if (trackUids.Contains(track.TrackUid))
            {
                _selectedClassicalTrackUids.Add(track.TrackUid);
            }
        }
        return FinalizeClassicalSelection(updateAnchor: GetSelectedClassicalTracks().FirstOrDefault()?.TrackUid);
    }

    public void ClearClassicalSelection()
    {
        _selectedClassicalTrackUids.Clear();
        _classicalSelectionAnchorTrackUid = null;
        SyncClassicalSelectionVisuals();
    }

    public void SyncClassicalMultiSelectMode()
    {
        foreach (var work in ClassicalVisibleWorks)
        {
            work.SetMultiSelectVisible(IsMultiSelectMode);
        }
    }

    private IReadOnlyList<TrackItem> FinalizeClassicalSelection(string? updateAnchor)
    {
        if (updateAnchor is not null)
        {
            _classicalSelectionAnchorTrackUid = updateAnchor;
        }
        SyncClassicalSelectionVisuals();
        var selected = GetSelectedClassicalTracks();
        if (selected.Count > 0)
        {
            InspectTrack(selected[0]);
        }
        else if (IsNavClassicalWorks && IsClassicalComposerDetailOpen)
        {
            InspectTrack(null);
        }
        return selected;
    }

    private void SyncClassicalSelectionVisuals()
    {
        foreach (var work in ClassicalVisibleWorks)
        {
            work.UpdateSelectionState(_selectedClassicalTrackUids);
        }
    }

    private static int FindTrackIndexByUid(IReadOnlyList<TrackItem> tracks, string uid)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            if (string.Equals(tracks[i].TrackUid, uid, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    public void PlayClassicalWork(ClassicalWorkViewItem? workItem)
    {
        if (workItem is null) return;
        var tracks = workItem.SelectedRecordingTracks;
        if (tracks.Count > 0)
        {
            PlayClassicalRecording(tracks, 0, $"{workItem.ComposerName} · {workItem.WorkTitle}");
        }
    }

    public void PlayClassicalMovement(ClassicalMovementViewItem? movItem)
    {
        if (movItem is null) return;
        var workItem = ClassicalVisibleWorks.FirstOrDefault(w => string.Equals(w.Key, movItem.WorkKey, StringComparison.Ordinal));
        var tracks = workItem?.SelectedRecordingTracks;
        if (tracks is not null && tracks.Count > 0)
        {
            PlayClassicalRecording(tracks, movItem.Index, $"{workItem!.ComposerName} · {workItem.WorkTitle}");
        }
    }

    private void UpdateClassicalPlayingHighlight(string? trackUid)
    {
        foreach (var work in ClassicalVisibleWorks)
        {
            work.UpdatePlayingTrackUid(trackUid);
        }
    }
}
