using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Tests;
using StackExchange.Redis;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class RedisAppTests(RedisServerFixture fixture)
{
    [AvaloniaFact]
    public async Task Existing_string_value_is_writable_from_the_app()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync($"{fixture.Host}:{fixture.Port}");
        await multiplexer.GetDatabase(2).StringSetAsync("mdb-app:test", "before");

        await using var vm = new MainWindowViewModel { EndpointUrl = fixture.EndpointUrl };
        Assert.True(vm.CanToggleReadOnly);
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.False(vm.IsReadOnly);

        var node = await Find(vm, "mdb-app", "test");
        vm.SelectedNode = node;
        await Until(() => vm.Attributes.Any(attribute => attribute.Name == "Value"));
        vm.SelectedAttribute = vm.Attributes.Single(attribute => attribute.Name == "Value");
        Assert.True(vm.WriteAttributeValueCommand.CanExecute(null));

        await vm.AddToWatchCommand.ExecuteAsync(null);
        var watch = Assert.Single(vm.WatchItems);
        var recording = await vm.CreateRecordingAsync(
            new RecordingOptions { Name = "Stable value", SamplingIntervalMs = 100 },
            [new RecordedItem(watch.NodeId, watch.DisplayName, watch.PortableId)]);
        Assert.NotNull(recording);
        await Until(() => recording.Recording.TotalSamples > 0);
        await Task.Delay(350, TestContext.Current.CancellationToken);
        Assert.Equal(1, recording.Recording.TotalSamples);

        await multiplexer.GetDatabase(2).StringSetAsync("mdb-app:test", "after");
        await Until(() => recording.Recording.TotalSamples == 2);
        Assert.Equal("after", recording.Recording.CreateSnapshot().Items[0].Samples[^1].Value);
    }

    private static async Task<NodeViewModel> Find(MainWindowViewModel vm, params string[] path)
    {
        NodeViewModel? found = null;
        await Until(() =>
        {
            if (vm.RootNodes.Count == 0)
            {
                return false;
            }

            var node = vm.RootNodes[0];
            foreach (var name in path)
            {
                node.IsExpanded = true;
                if (node.Children.FirstOrDefault(child => child.DisplayName == name) is not { } child)
                {
                    return false;
                }

                node = child;
            }

            found = node;
            return true;
        });
        return found!;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for Redis data in the app.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
