using Avalonia.Controls;
using Avalonia.Input;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class WatchView : UserControl
{
    private WatchColumnsViewModel? _columns;
    private bool _applying;

    public WatchView()
    {
        InitializeComponent();
        WatchGrid.ColumnReordered += (_, _) => CaptureLayout();
        WatchGrid.Sorting += OnSorting;
        WatchGrid.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ApplySort, Avalonia.Threading.DispatcherPriority.Loaded);
        WatchGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        WatchGrid.AddHandler(PointerMovedEvent, OnGridPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        WatchGrid.AddHandler(PointerReleasedEvent, (_, _) => _dragAnchor = null, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        WatchGrid.LayoutUpdated += (_, _) => CaptureWidths();
    }

    private MainWindowViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.WatchSelectionRequested -= OnWatchSelectionRequested;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            ApplyShortcuts(_vm);
            _vm.WatchSelectionRequested += OnWatchSelectionRequested;
        }

        if (_columns is not null)
        {
            _columns.Changed -= OnColumnsChanged;
            _columns.Reapplied -= OnColumnsChanged;
        }

        _columns = (DataContext as MainWindowViewModel)?.WatchColumns;
        if (_columns is not null)
        {
            _columns.Changed += OnColumnsChanged;
            _columns.Reapplied += OnColumnsChanged;
            _columns.Reapplied += (_, _) => ApplySort();
            ApplyColumns();
            ApplySort();
        }
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => ApplyColumns();

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (!_applying && _columns is not null && e.Column.Header?.ToString() is { } header)
        {
            _applying = true;
            try
            {
                _columns.ToggleSort(header);
            }
            finally
            {
                _applying = false;
            }
        }
    }

    private void ApplySort()
    {
        if (_columns?.SortColumn is not { } header || WatchGrid.Columns.FirstOrDefault(c => c.Header?.ToString() == header) is not { } column)
        {
            return;
        }

        _applying = true;
        try
        {
            column.Sort(_columns.SortDescending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyColumns()
    {
        if (_columns is null || _applying)
        {
            return;
        }

        _applying = true;
        try
        {
            var ordered = WatchGrid.Columns
                .Select(c => (Column: c, Option: _columns[c.Header?.ToString() ?? string.Empty]))
                .Where(p => p.Option is not null)
                .OrderBy(p => p.Option!.Order)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var (column, option) = ordered[i];
                column.IsVisible = option!.IsVisible;
                column.DisplayIndex = i;
                if (option.Width is { } width && !column.Width.IsStar)
                {
                    column.Width = new DataGridLength(width);
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void CaptureLayout()
    {
        if (_columns is null || _applying)
        {
            return;
        }

        foreach (var column in WatchGrid.Columns)
        {
            if (_columns[column.Header?.ToString() ?? string.Empty] is { } option)
            {
                option.Order = column.DisplayIndex;
            }
        }

        (DataContext as MainWindowViewModel)?.MarkLayoutChanged();
    }

    private void CaptureWidths()
    {
        if (_columns is null || _applying)
        {
            return;
        }

        foreach (var column in WatchGrid.Columns.Where(c => c.IsVisible && !c.Width.IsStar))
        {
            if (_columns[column.Header?.ToString() ?? string.Empty] is { } option && option.Width != column.ActualWidth && column.ActualWidth > 0)
            {
                var first = option.Width is null;
                option.Width = column.ActualWidth;
                if (!first)
                {
                    (DataContext as MainWindowViewModel)?.MarkLayoutChanged();
                }
            }
        }
    }

    private void OnRowDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (RowItemAt(e.Source) is not { } item || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Recorded items open their recorded values; others are revealed in the address space.
        if (item.HasRecording)
        {
            vm.ViewItemRecordingCommand.Execute(item);
        }
        else
        {
            vm.RevealInTreeCommand.Execute(item);
        }
    }

    private WatchItemViewModel? _dragAnchor;
    private WatchItemViewModel? _dragLast;

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(WatchGrid);
        if (point.Properties.IsRightButtonPressed && RowItemAt(e.Source) is { } clicked)
        {
            // Right-click on a row that is already selected keeps the whole selection for the context menu;
            // on an unselected row it selects just that row (Finder/Explorer behaviour).
            if (WatchGrid.SelectedItems.Contains(clicked))
            {
                e.Handled = true;
            }
            else
            {
                WatchGrid.SelectedItems.Clear();
                WatchGrid.SelectedItems.Add(clicked);
            }

            _dragAnchor = null;
            return;
        }

        _dragAnchor = point.Properties.IsLeftButtonPressed && (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Meta)) == 0
            ? RowItemAt(e.Source)
            : null;
        _dragLast = _dragAnchor;
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragAnchor is not { } anchor || !e.GetCurrentPoint(WatchGrid).Properties.IsLeftButtonPressed
            || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var hit = WatchGrid.InputHitTest(e.GetPosition(WatchGrid));
        if (RowItemAt(hit) is not { } current || current == _dragLast)
        {
            return;
        }

        _dragLast = current;
        var a = vm.WatchItems.IndexOf(anchor);
        var b = vm.WatchItems.IndexOf(current);
        if (a < 0 || b < 0)
        {
            return;
        }

        WatchGrid.SelectedItems.Clear();
        for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
        {
            WatchGrid.SelectedItems.Add(vm.WatchItems[i]);
        }

        WatchGrid.ScrollIntoView(current, null);
    }

    private static WatchItemViewModel? RowItemAt(object? source)
    {
        for (var v = source as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
        {
            if (v is DataGridColumnHeader)
            {
                return null;
            }

            if (v is DataGridRow { DataContext: WatchItemViewModel item })
            {
                return item;
            }
        }

        return null;
    }

    private void OnDragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        var canDrop = NodeDrag.Current is { Count: > 0 } nodes
            && DataContext is MainWindowViewModel { IsConnected: true }
            && nodes.Count > 0;
        e.DragEffects = canDrop ? Avalonia.Input.DragDropEffects.Copy : Avalonia.Input.DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        if (NodeDrag.Current is { Count: > 0 } nodes && DataContext is MainWindowViewModel vm)
        {
            e.DragEffects = Avalonia.Input.DragDropEffects.Copy;
            e.Handled = true;
            await vm.DropNodesAsync([.. nodes]);
        }
    }

    private async void OnRemoveStaleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.RemoveStaleCommand.ExecuteAsync(null);
        }
    }

    private async void OnRemoveBadDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.RemoveBadCommand.ExecuteAsync(null);
        }
    }

    private void OnWatchSelectionRequested(object? sender, IReadOnlyList<WatchItemViewModel> items)
    {
        WatchGrid.SelectedItems.Clear();
        foreach (var item in items)
        {
            WatchGrid.SelectedItems.Add(item);
        }

        if (items.Count > 0)
        {
            WatchGrid.ScrollIntoView(items[0], null);
            WatchGrid.Focus();
        }
    }

    private void OnWatchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Rebuild from the grid's full selection: incremental Added/Removed deltas miss items on
        // Shift-range and Select-All, which left only a couple of rows in SelectedWatchItems.
        var current = WatchGrid.SelectedItems.OfType<WatchItemViewModel>().ToList();
        if (current.SequenceEqual(vm.SelectedWatchItems))
        {
            return;
        }

        vm.SelectedWatchItems.Clear();
        foreach (var item in current)
        {
            vm.SelectedWatchItems.Add(item);
        }
    }


    private bool _shortcutsApplied;

    private void ApplyShortcuts(MainWindowViewModel vm)
    {
        if (_shortcutsApplied)
        {
            return;
        }

        _shortcutsApplied = true;
        Shortcuts.Apply("Watch", WatchGrid,
        [
            new("OemQuestion", new CommunityToolkit.Mvvm.Input.RelayCommand(() => { FilterBox.Focus(); FilterBox.SelectAll(); }), Description: "Filter the watch list (/)"),
            new("P", vm.ToggleWatchProblemsOnlyCommand, Description: "Show only problem rows (Bad, Uncertain, stale)"),
            new("Enter", vm.RevealInTreeCommand, Description: "Show in address space"),
            new("Delete", vm.RemoveFromWatchCommand, Description: "Remove selected"),
            new("Back", vm.RemoveFromWatchCommand, Description: "Remove selected"),
            new("Shift+Delete", vm.ClearWatchCommand, Description: "Remove all"),
            new("S", new CommunityToolkit.Mvvm.Input.RelayCommand(() => SelectOrRemove("stale", vm.SelectStaleCommand, vm.RemoveStaleCommand)),
                Description: "Select stale values (press twice to remove them)", ShowOn: vm.SelectStaleCommand),
            new("B", new CommunityToolkit.Mvvm.Input.RelayCommand(() => SelectOrRemove("bad", vm.SelectBadCommand, vm.RemoveBadCommand)),
                Description: "Select bad values (press twice to remove them)", ShowOn: vm.SelectBadCommand),
            new("Shift+S", vm.SelectStaleOrBadCommand, Description: "Select stale or bad values"),
            new("Cmd+Alt+S", vm.RemoveStaleCommand, Description: "Remove stale values"),
            new("Cmd+Alt+B", vm.RemoveBadCommand, Description: "Remove bad values"),
            new("D1", vm.SetRefreshCommand, 100, "Refresh time 100 ms"),
            new("D2", vm.SetRefreshCommand, 250, "Refresh time 250 ms"),
            new("D3", vm.SetRefreshCommand, 500, "Refresh time 500 ms"),
            new("D4", vm.SetRefreshCommand, 1000, "Refresh time 1000 ms"),
            new("D5", vm.SetRefreshCommand, 2000, "Refresh time 2000 ms"),
            new("D6", vm.SetRefreshCommand, 5000, "Refresh time 5000 ms"),
            new("D7", vm.SetRefreshCommand, 10000, "Refresh time 10000 ms"),
            new("T", new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(PromptRefreshAsync), Description: "Custom refresh time…"),
            new("R", vm.NewRecordingCommand, Description: "Record selected…"),
            new("Shift+R", vm.RecordAllCommand, Description: "Record all monitored…"),
            new("G", vm.ViewItemRecordingCommand, Description: "Show recorded values"),
            new("Space", vm.ViewItemRecordingCommand, Description: "Show recorded values (quick look)"),
            new("Cmd+Alt+C", vm.CopyWatchValueCommand, Description: "Copy value"),
            new("Cmd+Alt+N", vm.CopyWatchNodeIdCommand, Description: "Copy NodeId"),
            new("Cmd+Alt+J", vm.CopyWatchValuesJsonCommand, Description: "Copy values as JSON"),
            new("Cmd+Shift+C", vm.CopyWatchJsonCommand, Description: "Copy as JSON"),
        ]);
    }

    private static readonly TimeSpan DoublePress = TimeSpan.FromMilliseconds(800);
    private string? _lastSelectKey;
    private DateTime _lastSelectKeyAt;

    /// <summary>First press selects the stale/bad rows; a second press shortly after removes them.</summary>
    private void SelectOrRemove(string kind, System.Windows.Input.ICommand select, System.Windows.Input.ICommand remove)
    {
        var now = DateTime.UtcNow;
        if (_lastSelectKey == kind && now - _lastSelectKeyAt < DoublePress)
        {
            _lastSelectKey = null;
            remove.Execute(null);
            return;
        }

        _lastSelectKey = kind;
        _lastSelectKeyAt = now;
        select.Execute(null);
    }

    private async Task PromptRefreshAsync()
    {
        if (DataContext is MainWindowViewModel vm && await RefreshPrompt.AskAsync(this, vm.SelectedWatchItem?.RefreshMs ?? vm.DefaultRefreshMs) is { } ms)
        {
            await vm.SetRefreshCommand.ExecuteAsync(ms);
        }
    }

    private async void OnCustomRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await PromptRefreshAsync();
}
