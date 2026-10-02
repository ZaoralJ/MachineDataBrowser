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

public sealed class WatchFilterTests
{
    private static WatchItemViewModel Item(uint id, string name, string value, StatusCode status)
    {
        var item = new WatchItemViewModel(new NodeId(id, 2), name) { NodeIdText = $"ns=2;i={id}" };
        item.Apply(new ValueUpdate(item.NodeId, value, status, DateTime.UtcNow, DateTime.UtcNow));
        return item;
    }

    [AvaloniaFact]
    public async Task Filter_by_text_and_problems_shows_matching_rows_and_copies_only_them()
    {
        await using var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        var speed = Item(1, "Motor.Speed", "1500", StatusCodes.Good);
        var temp = Item(2, "Motor.Temperature", "81.5", StatusCodes.Good);
        var sensor = Item(3, "Tank.Level", "0", StatusCodes.BadSensorFailure);
        vm.WatchItems.AddRange([speed, temp, sensor]);
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        List<WatchItemViewModel> Shown() => [.. vm.WatchView.Cast<WatchItemViewModel>()];

        Assert.Equal(3, Shown().Count);
        Assert.Empty(vm.WatchFilterSummary);

        vm.WatchFilter = "motor";
        Assert.Equal([speed, temp], Shown());
        Assert.Equal("2 of 3 shown", vm.WatchFilterSummary);

        vm.WatchFilter = "81";                       // matches the value
        Assert.Equal([temp], Shown());
        vm.WatchFilter = "ns=2;i=3";                 // matches the NodeId
        Assert.Equal([sensor], Shown());

        vm.WatchFilter = string.Empty;
        vm.ShowWatchProblemsOnly = true;             // Bad status
        Assert.Equal([sensor], Shown());

        // Live: a row that turns Bad joins the filtered view on the next flush.
        temp.Apply(new ValueUpdate(temp.NodeId, "0", StatusCodes.BadCommunicationError, DateTime.UtcNow, DateTime.UtcNow));
        await Until(() => Shown().Count == 2);

        Dispatcher.UIThread.RunJobs();
        var table = GridCopy.ToTable(grid);
        Assert.Contains("Tank.Level", table, StringComparison.Ordinal);
        Assert.DoesNotContain("Motor.Speed", table, StringComparison.Ordinal);

        vm.ClearWatchFilterCommand.Execute(null);
        Assert.False(vm.IsWatchFiltered);
        Assert.Equal(3, Shown().Count);
        window.Close();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
