using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Core;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class SortPersistenceTests(OpcPlcFixture plc)
{
    [AvaloniaFact]
    public async Task Watch_sort_survives_switching_tabs_and_session_reload()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        var node = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "OpcPlc", "Telemetry", "Basic" })
        {
            node.IsExpanded = true;
            var parent = node;
            await Until(() => parent.Children.Any(c => c.DisplayName == name));
            node = parent.Children.Single(c => c.DisplayName == name);
        }

        vm.SelectedNode = node;
        await vm.MonitorFolderCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        ClickHeader(window, "Name");
        ClickHeader(window, "Name");
        Assert.Equal("Name", vm.WatchColumns.SortColumn);
        Assert.True(vm.WatchColumns.SortDescending);
        Assert.Equal(Order(vm).OrderByDescending(n => n, StringComparer.Ordinal), VisibleOrder(window));

        var watchDock = FindDock(vm.Layout!, "Watch")!;
        var recordings = watchDock.VisibleDockables!.Single(d => d.Id == "Recordings");
        var watch = watchDock.VisibleDockables!.Single(d => d.Id == "Watch");
        vm.DockFactory.SetActiveDockable(recordings);
        Dispatcher.UIThread.RunJobs();
        vm.DockFactory.SetActiveDockable(watch);
        await Until(() => VisibleOrder(window).Count == 4);
        for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(30, TestContext.Current.CancellationToken); }

        Assert.Equal(Order(vm).OrderByDescending(n => n, StringComparer.Ordinal), VisibleOrder(window));
        window.Close();
    }

    private static List<string> Order(MainWindowViewModel vm) => [.. vm.WatchItems.Select(w => w.DisplayName)];

    private static List<string> VisibleOrder(Window window) =>
        [.. window.GetVisualDescendants().OfType<DataGrid>().Where(g => g.Name == "WatchGrid" && g.IsEffectivelyVisible)
            .SelectMany(g => g.GetVisualDescendants().OfType<DataGridRow>())
            .Where(r => r.IsVisible && r.DataContext is WatchItemViewModel)
            .OrderBy(r => r.TranslatePoint(default, window)!.Value.Y)
            .Select(r => ((WatchItemViewModel)r.DataContext!).DisplayName)];

    private static IDock? FindDock(IDockable d, string toolId) => d switch
    {
        IDock dock when dock.VisibleDockables?.Any(x => x.Id == toolId) == true => dock,
        IDock dock => dock.VisibleDockables?.Select(x => FindDock(x, toolId)).FirstOrDefault(x => x is not null),
        _ => null,
    };

    private static void ClickHeader(Window window, string header)
    {
        Dispatcher.UIThread.RunJobs();
        var h = window.GetVisualDescendants().OfType<DataGridColumnHeader>().First(x => Equals(x.Content, header) && x.IsEffectivelyVisible);
        var p = h.TranslatePoint(new Point(h.Bounds.Width / 2, h.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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
