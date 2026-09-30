using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Opc.Ua;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class SelectStaleTests
{
    [AvaloniaFact]
    public async Task Select_stale_selects_only_items_without_recent_updates()
    {
        await using var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();

        var now = DateTimeOffset.Now;
        WatchItemViewModel Item(string name, TimeSpan age)
        {
            var item = new WatchItemViewModel(new NodeId(name, 2), name) { RefreshMs = 250 };
            item.Apply(new ValueUpdate(item.NodeId, "1", StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow), now - age);
            item.RefreshAge(now);
            vm.WatchItems.Add(item);
            return item;
        }

        var fresh = Item("fresh", TimeSpan.Zero);
        var old = Item("old", TimeSpan.FromMinutes(1));
        var older = Item("older", TimeSpan.FromHours(1));
        Dispatcher.UIThread.RunJobs();

        vm.SelectStaleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        Assert.Equal([old, older], grid.SelectedItems.OfType<WatchItemViewModel>().OrderBy(i => i.DisplayName));
        Assert.Equal([old, older], vm.SelectedWatchItems.OrderBy(i => i.DisplayName));
        Assert.DoesNotContain(fresh, vm.SelectedWatchItems);
        Assert.Equal("2 stale values selected", vm.StatusMessage);

        vm.WatchItems.Remove(old);
        vm.WatchItems.Remove(older);
        vm.SelectStaleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.SelectedWatchItems);
        Assert.Equal("No stale values", vm.StatusMessage);
        window.Close();
    }
}
