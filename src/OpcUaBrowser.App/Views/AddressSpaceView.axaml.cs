using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class AddressSpaceView : UserControl
{
    public AddressSpaceView()
    {
        InitializeComponent();

        // TreeViewItem toggles IsExpanded on double-tap and marks the event handled, which would collapse
        // the node the first click just expanded. handledEventsToo lets us see it and keep the node open.
        AddressTree.AddHandler(InputElement.DoubleTappedEvent, OnTreeDoubleTapped, handledEventsToo: true);
        AddressTree.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddressTree.AddHandler(InputElement.PointerMovedEvent, OnTreePointerMoved, handledEventsToo: true);
        AddressTree.AddHandler(InputElement.PointerReleasedEvent, (_, _) => _dragStart = null, handledEventsToo: true);
    }

    private const double DragThreshold = 6;
    private (Point Position, NodeViewModel Node, PointerPressedEventArgs Args)? _dragStart;

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Right-click acts on the clicked node: an unselected node becomes the selection, a node that is
        // already selected keeps the whole multi-selection for the context menu.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && ClickedNode(e.Source) is { } clicked
            && !AddressTree.SelectedItems.Contains(clicked))
        {
            AddressTree.SelectedItems.Clear();
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
            vm.RevealRequested -= OnRevealRequested;
            vm.RevealRequested += OnRevealRequested;
        }
    }

    private void OnRevealRequested(object? sender, NodeViewModel node) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                AddressTree.SelectedItems.Clear();
                AddressTree.SelectedItem = node;
                if (AddressTree.TreeContainerFromItem(node) is TreeViewItem container)
                {
                    container.BringIntoView();
                    container.Focus();
                }
            },
            Avalonia.Threading.DispatcherPriority.Loaded);

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
            var actual = AddressTree.SelectedItems.OfType<NodeViewModel>().ToList();
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
                case TreeViewItem { DataContext: NodeViewModel node }:
                    return node;
            }
        }

        return null;
    }

    private async void OnCustomRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && await RefreshPrompt.AskAsync(this, vm.DefaultRefreshMs) is { } ms)
        {
            await vm.MonitorSelectedWithRefreshCommand.ExecuteAsync(ms);
        }
    }
}
