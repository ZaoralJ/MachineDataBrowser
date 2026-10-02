using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core.Tests;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class DragDropTests(OpcPlcFixture plc)
{
    [AvaloniaFact]
    public async Task Dragging_folder_label_onto_watch_grid_adds_its_variables()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        var root = vm.RootNodes[0];
        await Until(() => root.Children.Any(c => c.DisplayName == "Objects"));
        var objects = root.Children.Single(c => c.DisplayName == "Objects");
        objects.IsExpanded = true;
        await Until(() => objects.Children.Any(c => c.DisplayName == "OpcPlc"));
        var plcNode = objects.Children.Single(c => c.DisplayName == "OpcPlc");
        Dispatcher.UIThread.RunJobs();

        var label = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.DataContext == plcNode && t.Text == "OpcPlc");
        var from = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), window)!.Value;
        var started = NodeDrag.StartedCount;
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(20, 5), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        Assert.True(NodeDrag.StartedCount > started, "drag did not start from tree label");
        window.MouseUp(from + new Vector(20, 5), MouseButton.Left);

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        var to = grid.TranslatePoint(new Point(grid.Bounds.Width / 2, grid.Bounds.Height / 2), window)!.Value;
        NodeDrag.Current = [plcNode];
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("x"));
        window.DragDrop(to, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        window.DragDrop(to, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        window.DragDrop(to, RawDragEventType.Drop, data, DragDropEffects.Copy);
        NodeDrag.Current = null;

        await Until(() => vm.WatchItems.Count > 4);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Right_click_selects_the_clicked_tree_node_for_the_context_menu()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        var root = vm.RootNodes[0];
        await Until(() => root.Children.Any(c => c.DisplayName == "Objects"));
        var objects = root.Children.Single(c => c.DisplayName == "Objects");
        var types = root.Children.Single(c => c.DisplayName == "Types");
        vm.SelectedNode = types;
        Dispatcher.UIThread.RunJobs();

        var label = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.DataContext == objects && t.Text == "Objects");
        var at = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Right, RawInputModifiers.RightMouseButton);
        window.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([objects], vm.SelectedNodes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hovering_a_child_row_highlights_only_that_row()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        var root = vm.RootNodes[0];
        await Until(() => root.Children.Any(c => c.DisplayName == "Objects"));
        var objects = root.Children.Single(c => c.DisplayName == "Objects");
        objects.IsExpanded = true;
        await Until(() => objects.Children.Any(c => c.DisplayName == "OpcPlc"));
        Dispatcher.UIThread.RunJobs();

        Control RowOf(NodeViewModel node) => window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext == node)
            .GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(b => b.Name == "PART_ContentPresenter");
        static bool Lit(Control c) => c is Avalonia.Controls.Presenters.ContentPresenter { Background: Avalonia.Media.ISolidColorBrush { Color.A: > 0 } };

        var child = objects.Children.Single(c => c.DisplayName == "OpcPlc");
        var label = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.DataContext == child && t.Text == "OpcPlc");
        window.MouseMove(label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), window)!.Value);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Lit(RowOf(child)), "hovered row is not highlighted");
        Assert.False(Lit(RowOf(objects)), "parent row lights up while a child is hovered");
        Assert.False(Lit(RowOf(root)), "grandparent row lights up while a child is hovered");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Typing_selects_rows_and_collapsing_selects_the_folder()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        var root = vm.RootNodes[0];
        await Until(() => root.Children.Any(c => c.DisplayName == "Objects"));
        var objects = root.Children.Single(c => c.DisplayName == "Objects");
        objects.IsExpanded = true;
        await Until(() => objects.Children.Any(c => c.DisplayName == "OpcPlc"));
        Dispatcher.UIThread.RunJobs();

        var tree = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "AddressTree");
        tree.SelectedItem = root;
        tree.ContainerFromItem(root)!.Focus();
        window.KeyTextInput("Op");
        Dispatcher.UIThread.RunJobs();
        var opcPlc = objects.Children.Single(c => c.DisplayName == "OpcPlc");
        Assert.Same(opcPlc, vm.SelectedNode);

        objects.IsExpanded = false;
        await Until(() => vm.SelectedNode == objects);
        Assert.Same(objects, tree.SelectedItem);
        window.Close();
    }

    private static async Task Until(Func<bool> c)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!c())
        {
            Assert.True(DateTime.UtcNow < end, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
