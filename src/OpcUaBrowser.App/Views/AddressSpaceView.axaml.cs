using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.Views;

public sealed partial class AddressSpaceView : UserControl
{
    public AddressSpaceView()
    {
        InitializeComponent();

        // Double-click expands (the first click may have toggled it) and monitors.
        AddressTree.AddHandler(InputElement.DoubleTappedEvent, OnTreeDoubleTapped, handledEventsToo: true);
        AddressTree.AddHandler(InputElement.KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        AddressTree.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddressTree.AddHandler(InputElement.PointerMovedEvent, OnTreePointerMoved, handledEventsToo: true);
        AddressTree.AddHandler(InputElement.PointerReleasedEvent, (_, _) => _dragStart = null, handledEventsToo: true);
        SearchBox.AddHandler(KeyDownEvent, OnSearchBoxKeyDown, RoutingStrategies.Tunnel);
    }

    private const double DragThreshold = 6;
    private (Point Position, NodeViewModel Node, PointerPressedEventArgs Args)? _dragStart;

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Right-click acts on the clicked node: an unselected node becomes the selection, a node that is
        // already selected keeps the whole multi-selection for the context menu.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && ClickedNode(e.Source) is { } clicked
            && AddressTree.SelectedItems?.Contains(clicked) != true)
        {
            AddressTree.SelectedItems?.Clear();
            AddressTree.SelectedItem = clicked;
        }

        _dragStart = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ClickedNode(e.Source) is { } node
            ? (e.GetPosition(this), node, e)
            : null;
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var delta = e.GetPosition(this) - start.Position;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _dragStart = null;
        var nodes = vm.SelectedNodes.Contains(start.Node) ? vm.SelectedNodes.ToList() : [start.Node];
        NodeDrag.Current = nodes;
        NodeDrag.StartedCount++;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(string.Join(Environment.NewLine, nodes.Select(n => n.NodeIdText))));
            await DragDrop.DoDragDropAsync(start.Args, data, DragDropEffects.Copy);
        }
        finally
        {
            NodeDrag.Current = null;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            ApplyShortcuts(vm);
            vm.SearchFocusRequested -= OnSearchFocusRequested;
            vm.SearchFocusRequested += OnSearchFocusRequested;
            vm.RevealRequested -= OnRevealRequested;
            vm.RevealRequested += OnRevealRequested;
        }
    }

    private void OnRevealRequested(object? sender, NodeViewModel node) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                AddressTree.SelectedItems?.Clear();
                AddressTree.SelectedItem = node;
                FocusRow(node);
            },
            Avalonia.Threading.DispatcherPriority.Loaded);

    private void FocusRow(NodeViewModel node)
    {
        AddressTree.ScrollIntoView(node);
        AddressTree.ContainerFromItem(node)?.Focus();
    }

    /// <summary>Tree keys for the flattened list: → expands or steps into the first child, ← collapses or goes to the parent.</summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || AddressTree.SelectedItem is not NodeViewModel node)
        {
            return;
        }

        NodeViewModel? select = null;
        switch (e.Key)
        {
            case Key.Right when node.HasChildren && !node.IsExpanded:
                node.IsExpanded = true;
                break;
            case Key.Right when node.IsExpanded && node.Children.Count > 0:
                select = node.Children[0];
                break;
            case Key.Left when node.IsExpanded:
                node.IsExpanded = false;
                break;
            case Key.Left when node.Parent is { } parent:
                select = parent;
                break;
            default:
                return;
        }

        e.Handled = true;
        if (select is not null)
        {
            AddressTree.SelectedItems?.Clear();
            AddressTree.SelectedItem = select;
            FocusRow(select);
        }
    }

    private void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        var extendingSelection = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (!extendingSelection && ClickedNode(e) is { HasChildren: true } node)
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // Mirror the tree's real selection: a Clear() raises no RemovedItems, so applying deltas leaves stale nodes.
            var actual = (AddressTree.SelectedItems ?? Array.Empty<object>()).OfType<NodeViewModel>().ToList();
            foreach (var stale in vm.SelectedNodes.Except(actual).ToList())
            {
                vm.SelectedNodes.Remove(stale);
            }

            foreach (var node in actual.Where(n => !vm.SelectedNodes.Contains(n)))
            {
                vm.SelectedNodes.Add(node);
            }
        }
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ClickedNode(e) is not { } node)
        {
            return;
        }

        node.IsExpanded = true;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.MonitorNodeCommand.Execute(node);
        }
    }

    private static NodeViewModel? ClickedNode(TappedEventArgs e) => ClickedNode(e.Source);

    private static NodeViewModel? ClickedNode(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            switch (visual)
            {
                case Button:
                    return null;
                case ListBoxItem { DataContext: NodeViewModel node }:
                    return node;
            }
        }

        return null;
    }


    private bool _shortcutsApplied;

    private void ApplyShortcuts(MainWindowViewModel vm)
    {
        if (_shortcutsApplied)
        {
            return;
        }

        _shortcutsApplied = true;
        Shortcuts.Apply("Address Space", AddressTree,
        [
            new("Enter", vm.AddToWatchCommand, Description: "Monitor selected variables"),
            new("D1", vm.MonitorSelectedWithRefreshCommand, 100, "Monitor with refresh 100 ms"),
            new("D2", vm.MonitorSelectedWithRefreshCommand, 250, "Monitor with refresh 250 ms"),
            new("D3", vm.MonitorSelectedWithRefreshCommand, 500, "Monitor with refresh 500 ms"),
            new("D4", vm.MonitorSelectedWithRefreshCommand, 1000, "Monitor with refresh 1000 ms"),
            new("D5", vm.MonitorSelectedWithRefreshCommand, 2000, "Monitor with refresh 2000 ms"),
            new("D6", vm.MonitorSelectedWithRefreshCommand, 5000, "Monitor with refresh 5000 ms"),
            new("D7", vm.MonitorSelectedWithRefreshCommand, 10000, "Monitor with refresh 10000 ms"),
            new("T", new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(PromptRefreshAsync), Description: "Monitor with custom refresh time…"),
            new("F", vm.MonitorFolderCommand, Description: "Monitor all variables in folder"),
            new("E", vm.ExpandAllCommand, Description: "Expand all below"),
            new("Shift+E", vm.CollapseAllCommand, Description: "Collapse all"),
            new("Cmd+Alt+N", vm.CopyNodeIdCommand, Description: "Copy NodeId"),
            new("Cmd+Shift+C", vm.CopyNodeJsonCommand, Description: "Copy as JSON"),
            new("Cmd+Shift+K", vm.CopyNodeClassCommand, Description: "Copy as C# class"),
            new("Cmd+Alt+K", vm.CopyNodeRecordCommand, Description: "Copy as C# record"),
            new("OemQuestion", vm.FocusSearchCommand, Description: "Search the address space (/)"),
        ]);
    }

    /// <summary>↓ in the search box moves into the results, so they can be walked with the arrow keys.</summary>
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SearchResultsList.ItemCount > 0)
        {
            SearchResultsList.SelectedIndex = Math.Max(0, SearchResultsList.SelectedIndex);
            SearchResultsList.ContainerFromIndex(SearchResultsList.SelectedIndex)?.Focus();
            e.Handled = true;
        }
    }

    private void OnSearchFocusRequested(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Loaded);

    private async void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && SearchResultsList.SelectedItem is SearchHit hit)
        {
            await vm.RevealSearchHitCommand.ExecuteAsync(hit);
        }
    }

    private async Task PromptRefreshAsync()
    {
        if (DataContext is MainWindowViewModel vm && await RefreshPrompt.AskAsync(this, vm.DefaultRefreshMs) is { } ms)
        {
            await vm.MonitorSelectedWithRefreshCommand.ExecuteAsync(ms);
        }
    }

    private async void OnCustomRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await PromptRefreshAsync();
}
