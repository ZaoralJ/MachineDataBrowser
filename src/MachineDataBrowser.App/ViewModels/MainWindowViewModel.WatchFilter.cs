using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private DataGridCollectionView? _watchView;
    private HashSet<WatchItemViewModel> _watchShown = new(ReferenceEqualityComparer.Instance);

    /// <summary>What the Watch grid shows: <see cref="WatchItems"/> through the filter (sorting is applied by the grid).</summary>
    public DataGridCollectionView WatchView => _watchView ??= CreateWatchView();

    /// <summary>Text that a row's name, path, NodeId, value or status must contain (case-insensitive); empty shows all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatchFiltered), nameof(WatchFilterSummary))]
    public partial string WatchFilter { get; set; } = string.Empty;

    /// <summary>Groups the watch list under collapsible headers, one per parent path in the address space.</summary>
    [ObservableProperty]
    public partial bool GroupWatchByPath { get; set; }

    public bool IsWatchFiltered => !string.IsNullOrWhiteSpace(WatchFilter);

    /// <summary>"12 of 40 shown" while a filter is on; empty otherwise.</summary>
    public string WatchFilterSummary => IsWatchFiltered ? $"{_watchShown.Count} of {WatchItems.Count} shown" : string.Empty;

    partial void OnWatchFilterChanged(string value) => RefreshWatchFilter(force: true);

    partial void OnGroupWatchByPathChanged(bool value)
    {
        ApplyWatchGrouping();
        MarkDirty();
    }

    [RelayCommand]
    private void ToggleGroupWatchByPath() => GroupWatchByPath = !GroupWatchByPath;

    private void ApplyWatchGrouping()
    {
        if (_watchView is not { } view)
        {
            return;
        }

        view.GroupDescriptions.Clear();
        if (GroupWatchByPath)
        {
            view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(WatchItemViewModel.Group)));
        }
    }

    [RelayCommand]
    private void ClearWatchFilter()
    {
        WatchFilter = string.Empty;
    }

    private DataGridCollectionView CreateWatchView()
    {
        var view = new DataGridCollectionView(WatchItems) { Filter = item => !IsWatchFiltered || (item is WatchItemViewModel w && _watchShown.Contains(w)) };
        WatchItems.CollectionChanged += (_, _) => RefreshWatchFilter(force: false);
        _watchView = view;
        ApplyWatchGrouping();
        RefreshWatchFilter(force: true);
        return view;
    }

    public bool MatchesWatchFilter(WatchItemViewModel item)
    {
        var text = WatchFilter.Trim();
        return text.Length == 0
            || item.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.NodeIdText.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.Path.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.Value.Contains(text, StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-evaluates the filter. Values change many times a second, so the view is only refreshed (which resets the
    /// grid) when the set of matching rows changed, or when the filter itself changed.
    /// </summary>
    private void RefreshWatchFilter(bool force)
    {
        var shown = new HashSet<WatchItemViewModel>(WatchItems.Where(MatchesWatchFilter), ReferenceEqualityComparer.Instance);
        if (!force && shown.SetEquals(_watchShown))
        {
            return;
        }

        _watchShown = shown;
        if (force || IsWatchFiltered)
        {
            _watchView?.Refresh();
        }

        OnPropertyChanged(nameof(WatchFilterSummary));
    }
}
