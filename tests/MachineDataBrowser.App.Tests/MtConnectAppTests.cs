using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class MtConnectAppTests(MtConnectSimulatorFixture agent)
{
    [AvaloniaFact]
    public async Task Mtconnect_devices_browse_and_watch_with_units()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = agent.EndpointUrl };
        Assert.False(vm.IsOpcUaEndpoint);
        Assert.False(vm.IsMqttEndpoint);
        Assert.True(vm.HasCredentials);
        Assert.False(vm.CanToggleReadOnly);
        Assert.StartsWith("MTConnect · Anonymous · ", vm.OptionsSummary, StringComparison.Ordinal);

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.True(vm.IsReadOnly);
        vm.IsReadOnly = false;
        Assert.True(vm.IsReadOnly);

        vm.SelectedNode = await Find(vm, "VMC", "base", "X", "Xpos");
        await vm.AddToWatchCommand.ExecuteAsync(null);
        vm.SelectedWatchItem = vm.WatchItems.Single();
        Assert.False(vm.WriteWatchValueCommand.CanExecute(null));
        vm.SelectedNode = await Find(vm, "VMC", "controller", "path", "execution");
        await vm.AddToWatchCommand.ExecuteAsync(null);

        await Until(() => vm.WatchItems.Count == 2 && vm.WatchItems.All(w => w.UpdateCount > 0 && !w.IsBad));
        var position = vm.WatchItems.Single(w => w.DisplayName == "Xpos");
        Assert.IsType<double>(position.RawValue);
        await Until(() => position.ServerUnit == "mm");
        Assert.IsType<string>(vm.WatchItems.Single(w => w.DisplayName == "execution").RawValue);
    }

    [AvaloniaFact]
    public async Task Copied_cli_commands_use_mtconnect_paths()
    {
        string? copied = null;
        await using var vm = new MainWindowViewModel
        {
            EndpointUrl = agent.EndpointUrl,
            CopyToClipboard = text => { copied = text; return Task.CompletedTask; },
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedNode = await Find(vm, "Lathe", "base", "C", "Cspeed");

        await vm.CopyCliMonitorCommand.ExecuteAsync(null);

        Assert.Equal($"mdbrowser monitor {agent.EndpointUrl} /Lathe/base/C/Cspeed --trust-all", copied);
    }

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
