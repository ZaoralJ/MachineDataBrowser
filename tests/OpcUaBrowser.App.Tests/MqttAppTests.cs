using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core.Tests;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class MqttAppTests(MqttSimulatorFixture broker)
{
    [AvaloniaFact]
    public async Task Mqtt_topics_json_fields_and_sparkplug_metrics_can_be_browsed_and_watched()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = broker.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Assert.StartsWith("MQTT · Anonymous", vm.OptionsSummary, StringComparison.Ordinal);
        Assert.False(vm.IsOpcUaEndpoint);
        Assert.True(vm.HasCredentials);

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        var root = vm.RootNodes[0];
        var topics = await Find(root, "Topics");
        var speed = await Find(await Find(await Find(await Find(topics, "machines"), "m1"), "status"), "speed");
        vm.SelectedNode = speed;
        await vm.AddToWatchCommand.ExecuteAsync(null);

        var metric = await Find(await Find(await Find(await Find(await Find(root, "Sparkplug B"), "Plant1"), "Edge1"), "Press1"), "Temperature");
        vm.SelectedNode = metric;
        await vm.AddToWatchCommand.ExecuteAsync(null);

        await Until(() => vm.WatchItems.Count == 2 && vm.WatchItems.All(w => w.UpdateCount > 2 && !w.IsBad));
        Assert.All(vm.WatchItems, w => Assert.IsType<double>(w.RawValue));
        window.Close();
    }

    private static async Task<NodeViewModel> Find(NodeViewModel parent, string name)
    {
        parent.IsExpanded = true;
        NodeViewModel? found = null;
        await Until(() => (found = parent.Children.FirstOrDefault(c => c.DisplayName == name)) is not null, 20);
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
