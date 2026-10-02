using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class BulkWatchTests(OpcPlcFixture plc)
{
    /// <summary>Dropping a folder with hundreds of variables must not freeze the window.</summary>
    [AvaloniaFact]
    public async Task Dropping_a_large_folder_keeps_the_ui_responsive()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        typeof(MainWindowViewModel).GetMethod("UpdateSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, [vm.Settings with { MaxRecursiveItems = 2000 }]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        var acme = await Find(vm.RootNodes[0], "Objects");

        // Measure the longest time the UI thread is blocked while the drop runs.
        var longest = TimeSpan.Zero;
        var last = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Normal, (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var gap = Stopwatch.GetElapsedTime(last, now);
            longest = gap > longest ? gap : longest;
            last = now;
        });
        timer.Start();
        var total = Stopwatch.StartNew();
        await vm.DropNodesAsync([acme]);
        timer.Stop();

        Assert.True(vm.WatchItems.Count >= 1000, $"only {vm.WatchItems.Count} items");
        TestContext.Current.TestOutputHelper!.WriteLine($"{vm.WatchItems.Count} items in {total.ElapsedMilliseconds} ms, longest UI block {longest.TotalMilliseconds:0} ms");
        Assert.True(longest < TimeSpan.FromMilliseconds(500), $"UI blocked for {longest.TotalMilliseconds:0} ms ({vm.WatchItems.Count} items in {total.ElapsedMilliseconds} ms)");
        window.Close();
    }

    private static async Task<NodeViewModel> Find(NodeViewModel parent, string name)
    {
        parent.IsExpanded = true;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        NodeViewModel? found;
        while ((found = parent.Children.FirstOrDefault(c => c.DisplayName == name)) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return found;
    }
}
