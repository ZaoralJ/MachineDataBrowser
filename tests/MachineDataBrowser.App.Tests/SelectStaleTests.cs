using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Opc.Ua;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class SelectStaleTests
{
    [AvaloniaFact]
    public async Task Select_stale_and_bad_selects_matching_items_only()
    {
        await using var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();

        var now = DateTimeOffset.Now;
        WatchItemViewModel Item(string name, TimeSpan age, uint status = StatusCodes.Good)
        {
            var item = new WatchItemViewModel(new NodeId(name, 2), name) { RefreshMs = 250 };
            item.Apply(new ValueUpdate(item.NodeId, "1", status, DateTime.UtcNow, DateTime.UtcNow), now - age);
            item.RefreshAge(now);
            vm.WatchItems.Add(item);
            return item;
        }

        var fresh = Item("fresh", TimeSpan.Zero);
        var old = Item("old", TimeSpan.FromMinutes(1));
        var older = Item("older", TimeSpan.FromHours(1));
        var bad = Item("bad", TimeSpan.Zero, StatusCodes.BadNodeIdUnknown);
        Dispatcher.UIThread.RunJobs();

        vm.SelectStaleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        Assert.Equal([old, older], grid.SelectedItems.OfType<WatchItemViewModel>().OrderBy(i => i.DisplayName));
        Assert.Equal([old, older], vm.SelectedWatchItems.OrderBy(i => i.DisplayName));
        Assert.DoesNotContain(fresh, vm.SelectedWatchItems);
        Assert.Equal("2 stale values selected", vm.StatusMessage);

        vm.SelectBadCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([bad], vm.SelectedWatchItems);
        Assert.Equal("1 bad value selected", vm.StatusMessage);

        vm.SelectStaleOrBadCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([bad, old, older], vm.SelectedWatchItems.OrderBy(i => i.DisplayName));

        await vm.RemoveBadCommand.ExecuteAsync(null);
        Assert.Equal([fresh, old, older], vm.WatchItems);
        Assert.Equal("Removed 1 bad value from watch", vm.StatusMessage);

        await vm.RemoveStaleCommand.ExecuteAsync(null);
        Assert.Equal([fresh], vm.WatchItems);
        Assert.Equal("Removed 2 stale values from watch", vm.StatusMessage);
        vm.SelectStaleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.SelectedWatchItems);
        Assert.Equal("No stale values", vm.StatusMessage);

        await vm.ClearWatchCommand.ExecuteAsync(null);
        Assert.Empty(vm.WatchItems);
        Assert.Equal("Removed 1 item from watch", vm.StatusMessage);
        window.Close();
    }
}
