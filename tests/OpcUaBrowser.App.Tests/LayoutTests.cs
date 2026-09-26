using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class LayoutTests : IDisposable
{
    private static readonly string[] AllPanes = ["AddressSpace", "Attributes", "Watch", "Recordings"];

    private readonly string _dir = Directory.CreateTempSubdirectory("layout-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [AvaloniaFact]
    public async Task Rearranged_layout_is_saved_and_restored()
    {
        var store = new LayoutStore(Path.Combine(_dir, "layout.json"));

        await using (var vm = new MainWindowViewModel(settingsStore: null, store))
        {
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var attributesDock = ToolDocks(vm.Layout!).Single(d => d.VisibleDockables!.Any(t => t.Id == "Attributes"));
            var watchDock = ToolDocks(vm.Layout!).Single(d => d.VisibleDockables!.Any(t => t.Id == "Watch"));
            var attributes = attributesDock.VisibleDockables!.Single(t => t.Id == "Attributes");

            vm.DockFactory.MoveDockable(attributesDock, watchDock, attributes, watchDock.VisibleDockables!.First(t => t.Id == "Watch"));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["Attributes", "Recordings", "Watch"], watchDock.VisibleDockables!.Select(t => t.Id).Order(StringComparer.Ordinal));
            vm.SaveLayout();
            window.Close();
        }

        await using var restored = new MainWindowViewModel(settingsStore: null, store);
        var groups = ToolDocks(restored.Layout!).Select(d => d.VisibleDockables!.Select(t => t.Id).Order(StringComparer.Ordinal).ToArray()).ToList();
        Assert.Contains(groups, g => g.SequenceEqual(["Attributes", "Recordings", "Watch"]));
        Assert.Contains(groups, g => g.SequenceEqual(["AddressSpace"]));

        var restoredWindow = new MainWindow { DataContext = restored };
        restoredWindow.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Single(restoredWindow.GetVisualDescendants().OfType<AddressSpaceView>());
        restoredWindow.Close();

        restored.ResetLayoutCommand.Execute(null);
        Assert.Equal(3, ToolDocks(restored.Layout!).Count());
        Assert.False(File.Exists(Path.Combine(_dir, "layout.json")));
    }

    [AvaloniaFact]
    public async Task Non_finite_proportions_are_saved_without_throwing()
    {
        var path = Path.Combine(_dir, "layout.json");
        await using var vm = new MainWindowViewModel(settingsStore: null, new LayoutStore(path));
        var docks = ToolDocks(vm.Layout!).ToList();
        docks[0].Proportion = double.PositiveInfinity;
        docks[1].Proportion = double.NegativeInfinity;
        docks[2].Proportion = double.NaN;

        vm.SaveLayout();

        Assert.Null(vm.ErrorMessage);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);

        await using var restored = new MainWindowViewModel(settingsStore: null, new LayoutStore(path));
        Assert.Equal(3, ToolDocks(restored.Layout!).Count());
    }

    [AvaloniaFact]
    public async Task Floated_hidden_or_lost_panes_can_be_brought_back()
    {
        await using var vm = new MainWindowViewModel(settingsStore: null, new LayoutStore(Path.Combine(_dir, "l.json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var root = vm.Layout!;
        IDockable Tool(string id) => ToolDocks(root).SelectMany(d => d.VisibleDockables!).Single(t => t.Id == id);

        vm.FloatPaneCommand.Execute("Watch");
        Dispatcher.UIThread.RunJobs();
        Assert.False(DockFactory.IsPaneDocked(root, "Watch"));
        vm.ShowPaneCommand.Execute("Watch");
        Assert.True(DockFactory.IsPaneDocked(root, "Watch"));
        Assert.True(root.Windows is null || root.Windows.Count == 0);

        vm.FloatPaneCommand.Execute("Attributes");
        foreach (var w in root.Windows!.ToList())
        {
            vm.DockFactory.RemoveWindow(w);
        }

        Assert.False(DockFactory.IsPaneDocked(root, "Attributes"));
        vm.ShowPaneCommand.Execute("Attributes");
        Assert.True(DockFactory.IsPaneDocked(root, "Attributes"));

        vm.DockFactory.HideDockable(Tool("Recordings"));
        vm.FloatPaneCommand.Execute("AddressSpace");
        vm.DockAllPanesCommand.Execute(null);
        Assert.All(AllPanes, id => Assert.True(DockFactory.IsPaneDocked(root, id), id));
        Assert.Equal(4, ToolDocks(root).SelectMany(d => d.VisibleDockables!).Count());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Floating_pane_shows_dock_back_bar_that_returns_it_to_main()
    {
        await using var vm = new MainWindowViewModel(settingsStore: null, new LayoutStore(Path.Combine(_dir, "l2.json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.All(window.GetVisualDescendants().OfType<PaneHost>(), h => Assert.False(h.GetVisualChildren().OfType<Avalonia.Controls.Border>().First().IsVisible));

        var root = vm.Layout!;
        vm.FloatPaneCommand.Execute("Watch");
        Dispatcher.UIThread.RunJobs();

        var floating = (root.Windows ?? []).Select(w => w.Host).OfType<Avalonia.Controls.Window>().ToList();
        Assert.NotEmpty(floating);
        var host = floating.SelectMany(w => w.GetVisualDescendants().OfType<PaneHost>()).Single(h => h.PaneId == "Watch");
        var bar = host.GetVisualChildren().OfType<Avalonia.Controls.Border>().First();
        Assert.True(bar.IsVisible);
        var button = bar.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single();
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(DockFactory.IsPaneDocked(root, "Watch"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Panes_do_not_float_by_drag_only_by_pop_out_and_reset_after_docking_back()
    {
        await using var vm = new MainWindowViewModel(settingsStore: null, new LayoutStore(Path.Combine(_dir, "l3.json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var root = vm.Layout!;
        Assert.All(ToolDocks(root).SelectMany(d => d.VisibleDockables!), t => Assert.False(t.CanFloat));

        vm.FloatPaneCommand.Execute("Attributes");
        Assert.False(DockFactory.IsPaneDocked(root, "Attributes"));
        Assert.True(Dock.Settings.DockSettings.UseOwnerForFloatingWindows);

        vm.ShowPaneCommand.Execute("Attributes");
        Assert.True(DockFactory.IsPaneDocked(root, "Attributes"));
        Assert.False(ToolDocks(root).SelectMany(d => d.VisibleDockables!).Single(t => t.Id == "Attributes").CanFloat);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Collapsed_split_from_saved_layout_is_restored_with_visible_panes()
    {
        var path = Path.Combine(_dir, "collapsed.json");
        await File.WriteAllTextAsync(path, """
            { "kind": "split", "orientation": "Horizontal", "children": [
              { "kind": "tools", "id": "LeftDock", "proportion": 1, "activeTool": "AddressSpace", "tools": ["AddressSpace"] },
              { "kind": "splitter" },
              { "kind": "tools", "proportion": 0, "activeTool": "Watch", "tools": ["Attributes", "Recordings", "Watch"] } ] }
            """, TestContext.Current.CancellationToken);

        await using var vm = new MainWindowViewModel(settingsStore: null, new LayoutStore(path));
        Assert.All(ToolDocks(vm.Layout!), d => Assert.InRange(d.Proportion, 0.1, 0.9));
    }

    [AvaloniaFact]
    public async Task Corrupt_or_incomplete_layout_falls_back_to_default()
    {
        var path = Path.Combine(_dir, "layout.json");
        await File.WriteAllTextAsync(path, """{ "kind": "tools", "tools": ["Watch"] }""", TestContext.Current.CancellationToken);

        await using (var vm = new MainWindowViewModel(settingsStore: null, new LayoutStore(path)))
        {
            Assert.Equal(3, ToolDocks(vm.Layout!).Count());
        }

        await File.WriteAllTextAsync(path, "not json", TestContext.Current.CancellationToken);
        await using (var vm = new MainWindowViewModel(settingsStore: null, new LayoutStore(path)))
        {
            Assert.Equal(3, ToolDocks(vm.Layout!).Count());
        }
    }

    private static IEnumerable<IToolDock> ToolDocks(IDockable dockable) => dockable switch
    {
        IToolDock tools => [tools],
        IDock dock => (dock.VisibleDockables ?? []).SelectMany(ToolDocks),
        _ => [],
    };
}
