using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core;
using OpcUaBrowser.Core.Tests;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class MainWindowTests(OpcPlcFixture plc)
{
    [AvaloniaFact]
    public async Task Connect_browse_select_and_watch_a_variable()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm };
        window.Show();

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Connected, vm.State);

        var root = Assert.Single(vm.RootNodes);
        var objects = await ExpandAndFind(root, "Objects");
        var server = await ExpandAndFind(objects, "Server");
        var status = await ExpandAndFind(server, "ServerStatus");
        var currentTime = await ExpandAndFind(status, "CurrentTime");
        Assert.Empty(currentTime.Children);
        Assert.NotEmpty(status.Children.Single(c => c.DisplayName == "BuildInfo").Children);

        vm.SelectedNode = currentTime;
        await WaitUntil(() => vm.Attributes.Any(a => a.Name == "Value"));
        Assert.Contains(vm.Attributes, a => a.Name == "NodeClass" && a.Value == "Variable");

        Assert.True(vm.AddToWatchCommand.CanExecute(null));
        await vm.AddToWatchCommand.ExecuteAsync(null);
        var watched = Assert.Single(vm.WatchItems);

        await WaitUntil(() => watched.Value != "…");
        var first = watched.Value;
        await WaitUntil(() => watched.Value != first);
        Assert.Equal("Good", watched.Status);

        await vm.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.Disconnected, vm.State);
        Assert.Empty(vm.WatchItems);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_node_label_toggles_it_and_double_click_keeps_it_open()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);

        var root = Assert.Single(vm.RootNodes);
        await WaitUntil(() => root.Children.Any(c => c.DisplayName == "Objects"));
        var objects = root.Children.Single(c => c.DisplayName == "Objects");
        Assert.False(objects.IsExpanded);

        ClickLabel(window, objects);
        await WaitUntil(() => objects.IsExpanded && objects.Children.Any(c => c.DisplayName == "Server"));
        Assert.Same(objects, vm.SelectedNode);

        await Task.Delay(700, TestContext.Current.CancellationToken);
        ClickLabel(window, objects);
        Assert.False(objects.IsExpanded);

        await Task.Delay(700, TestContext.Current.CancellationToken);
        ClickLabel(window, objects);
        Assert.True(objects.IsExpanded);

        await Task.Delay(700, TestContext.Current.CancellationToken);
        ClickLabel(window, objects);
        ClickLabel(window, objects);
        Assert.True(objects.IsExpanded);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Expand_all_is_limited_and_collapse_all_keeps_root_open()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        await vm.ConnectCommand.ExecuteAsync(null);
        var root = Assert.Single(vm.RootNodes);
        var objects = await ExpandAndFind(root, "Objects");
        var plcNode = await ExpandAndFind(objects, "OpcPlc");
        plcNode.IsExpanded = false;

        vm.SelectedNode = plcNode;
        await vm.ExpandAllCommand.ExecuteAsync(null);

        Assert.True(plcNode.IsExpanded);
        var telemetry = plcNode.Children.Single(c => c.DisplayName == "Telemetry");
        Assert.True(telemetry.IsExpanded);
        Assert.True(telemetry.Children.Single(c => c.DisplayName == "Basic").IsExpanded);
        Assert.False(objects.Children.Single(c => c.DisplayName == "Server").IsExpanded);

        vm.CollapseAllCommand.Execute(null);

        Assert.True(root.IsExpanded);
        Assert.False(objects.IsExpanded);
        Assert.False(plcNode.IsExpanded);
        Assert.False(telemetry.IsExpanded);
    }

    [AvaloniaFact]
    public void Panes_and_all_table_columns_are_resizable_by_dragging()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(), Width = 2400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var grids = window.GetVisualDescendants().OfType<DataGrid>().ToList();
        Assert.Equal(2, grids.Count);
        Assert.All(grids, g => Assert.True(g.CanUserResizeColumns));

        foreach (var grid in grids)
        {
            foreach (var column in grid.Columns.Where(c => c.IsVisible && !c.Width.IsStar).Reverse())
            {
                var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                    .Single(h => Equals(h.Content, column.Header));
                var before = column.ActualWidth;
                var rightEdge = header.TranslatePoint(new Point(header.Bounds.Width - 3, header.Bounds.Height / 2), window)!.Value;
                Drag(window, rightEdge, new Vector(40, 0));
                Assert.True(column.ActualWidth > before + 20, $"Column '{column.Header}' did not resize ({before} -> {column.ActualWidth}).");
            }
        }

        var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
        var treeWidth = tree.Bounds.Width;
        var splitter = window.GetVisualDescendants().OfType<Dock.Controls.ProportionalStackPanel.ProportionalStackPanelSplitter>()
            .Where(s => s.Bounds.Height > s.Bounds.Width)
            .OrderBy(s => s.TranslatePoint(default, window)!.Value.X)
            .First();
        Drag(window, Center(splitter, window), new Vector(120, 0));
        Assert.InRange(tree.Bounds.Width, treeWidth + 100, treeWidth + 140);

        window.Close();
    }

    private static Point Center(Visual visual, Window window) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    private static void Drag(Window window, Point from, Vector by)
    {
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        for (var step = 1; step <= 4; step++)
        {
            window.MouseMove(from + (by * step / 4), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }

        window.MouseUp(from + by, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClickLabel(Window window, NodeViewModel node)
    {
        Dispatcher.UIThread.RunJobs();
        var label = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(t => t.DataContext == node && t.Text == node.DisplayName);
        var center = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Label not in window.");

        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<NodeViewModel> ExpandAndFind(NodeViewModel parent, string name)
    {
        parent.IsExpanded = true;
        NodeViewModel? found = null;
        await WaitUntil(() => (found = parent.Children.FirstOrDefault(c => c.DisplayName == name)) is not null);
        return found!;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
