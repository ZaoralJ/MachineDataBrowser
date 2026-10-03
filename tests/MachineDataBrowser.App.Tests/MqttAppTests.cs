using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class MqttAppTests(MqttSimulatorFixture broker)
{
    [AvaloniaFact]
    public async Task Mqtt_topics_json_fields_and_sparkplug_metrics_can_be_browsed_and_watched()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = broker.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Assert.Equal(0, vm.DefaultRefreshMs); // MQTT: every message by default
        Assert.StartsWith("MQTT · Anonymous · all", vm.OptionsSummary, StringComparison.Ordinal);
        vm.EndpointUrl = "opc.tcp://localhost:4840";
        Assert.Equal(vm.Settings.SamplingIntervalMs, vm.DefaultRefreshMs);
        vm.EndpointUrl = broker.EndpointUrl;
        Assert.Equal(0, vm.DefaultRefreshMs);
        Assert.False(vm.IsOpcUaEndpoint);
        Assert.True(vm.HasCredentials);

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        var speed = await Find(vm, "Topics", "machines", "m1", "status", "speed");
        vm.SelectedNode = speed;
        await vm.AddToWatchCommand.ExecuteAsync(null);

        var metric = await Find(vm, "Sparkplug B", "Plant1", "Edge1", "Press1", "Temperature");
        vm.SelectedNode = metric;
        await vm.AddToWatchCommand.ExecuteAsync(null);

        await Until(() => vm.WatchItems.Count == 2 && vm.WatchItems.All(w => w.UpdateCount > 2 && !w.IsBad));
        Assert.All(vm.WatchItems, w => Assert.IsType<double>(w.RawValue));

        // Search from the whole tree (nothing with children selected) and reveal a result in the tree.
        vm.SelectedNode = null;
        vm.SearchText = "gripperVacuum";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(8, vm.SearchResults.Count); // per UNS line: the process topic and its unit in the robot's _meta JSON
        Assert.StartsWith("8 matches", vm.SearchStatus, StringComparison.Ordinal);
        await vm.RevealSearchHitCommand.ExecuteAsync(vm.SearchResults.Single(h => h.PathText.Contains("line2 › robot › process", StringComparison.Ordinal)));
        Assert.Equal("gripperVacuum", vm.SelectedNode?.DisplayName);
        Assert.Equal("line2", vm.SelectedNode?.Parent?.Parent?.Parent?.DisplayName);
        vm.ClearSearchCommand.Execute(null);
        Assert.False(vm.HasSearchPanel);
        window.Close();
    }

    /// <summary>
    /// Walks the path from the root on every check: the live MQTT tree is refreshed every second and may replace a
    /// node, so holding on to one found earlier can wait forever for children that appear under its replacement.
    /// </summary>
    private static async Task<NodeViewModel> Find(MainWindowViewModel vm, params string[] path)
    {
        NodeViewModel? found = null;
        await Until(() =>
        {
            var node = vm.RootNodes[0];
            foreach (var name in path)
            {
                node.IsExpanded = true;
                if (node.Children.FirstOrDefault(c => c.DisplayName == name) is not { } child)
                {
                    return false;
                }

                node = child;
            }

            found = node;
            return true;
        }, 30);
        return found!;
    }

    private static async Task Until(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
