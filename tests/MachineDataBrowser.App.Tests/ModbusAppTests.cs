using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class ModbusAppTests(ModbusSimulatorFixture device)
{
    [AvaloniaFact]
    public async Task Register_map_names_the_tree_reconnects_and_is_saved_relative_to_the_session()
    {
        var dir = Directory.CreateTempSubdirectory("mdb-modbus-").FullName;
        try
        {
            var map = Path.Combine(dir, "line1.csv");
            await File.WriteAllTextAsync(map, "name,address,unit\nLine1/Speed,hr:20:float32,rpm\n", TestContext.Current.CancellationToken);
            var session = Path.Combine(dir, "line1.mdbsession");
            var dialogs = new TestDialogs { TagMapAnswer = map };
            await using (var vm = new MainWindowViewModel { EndpointUrl = device.EndpointUrl, Dialogs = dialogs })
            {
                Assert.True(vm.IsModbusEndpoint);
                Assert.False(vm.HasCredentials);
                Assert.False(vm.IsOpcUaEndpoint);
                Assert.StartsWith("Modbus TCP · ", vm.OptionsSummary, StringComparison.Ordinal);

                await vm.ConnectCommand.ExecuteAsync(null);
                await Until(() => vm.RootNodes.Count > 0 && vm.RootNodes[0].Children.Any(c => c.DisplayName == "Holding Registers"), vm);
                Assert.DoesNotContain(vm.RootNodes[0].Children, c => c.DisplayName == "Tags");

                // Importing while connected reconnects, so the names show at once.
                await vm.ImportTagMapCommand.ExecuteAsync(null);
                Assert.Equal(map, vm.TagMapPath);
                Assert.Contains("line1.csv", vm.OptionsSummary, StringComparison.Ordinal);
                Assert.True(vm.IsConnected);
                var speed = await Find(vm, "Tags", "Line1", "Speed");
                vm.SelectedNode = speed;
                await vm.AddToWatchCommand.ExecuteAsync(null);
                await Until(() => vm.WatchItems.Single().Value == "3.5", vm);
                Assert.Equal("hr:20:float32", vm.WatchItems.Single().PortableId);
                await vm.WriteSessionAsync(session);
            }

            Assert.Contains("\"tagMap\":\"line1.csv\"", (await File.ReadAllTextAsync(session, TestContext.Current.CancellationToken)).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

            await using var reopened = new MainWindowViewModel();
            await reopened.LoadSessionAsync(session);
            Assert.Equal(map, reopened.TagMapPath);
            await reopened.RemoveTagMapCommand.ExecuteAsync(null);
            Assert.False(reopened.HasTagMap);
            Assert.Equal("None", reopened.TagMapName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
        }, vm);
        return found!;
    }

    private static async Task Until(Func<bool> condition, MainWindowViewModel vm, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timeout; {vm.ErrorMessage}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
