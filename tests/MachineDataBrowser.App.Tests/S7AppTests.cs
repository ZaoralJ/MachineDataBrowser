using Avalonia.Headless.XUnit;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

/// <summary>S7 in the app: the tag list as the connection's tag map, imported while connected, saved with the session.</summary>
public sealed class S7AppTests(S7SimulatorFixture plc)
{
    private string Url => $"s7://{plc.Host}/0,1";

    [AvaloniaFact]
    public async Task Tag_list_is_imported_while_connected_and_saved_relative_to_the_session()
    {
        var dir = Directory.CreateTempSubdirectory("mdb-s7-app-").FullName;
        try
        {
            var tags = Path.Combine(dir, "tags.csv");
            File.Copy(S7SimulatorFixture.TagFile, tags);
            var dialogs = new TestDialogs { TagMapAnswer = tags };
            var path = Path.Combine(dir, "plc.mdbsession");
            await using (var vm = new MainWindowViewModel { EndpointUrl = Url, Dialogs = dialogs })
            {
                Assert.True(vm.SupportsTagMap);
                Assert.False(vm.HasCredentials);
                Assert.False(vm.IsOpcUaEndpoint);
                Assert.StartsWith("S7 · no tag list", vm.OptionsSummary, StringComparison.Ordinal);

                await vm.ConnectCommand.ExecuteAsync(null);
                Assert.True(vm.IsConnected);
                await Until(() => vm.RootNodes[0].Children.Count > 0 && !vm.RootNodes[0].Children[0].IsPlaceholder);
                Assert.Equal("Data blocks", vm.RootNodes[0].Children[0].DisplayName);

                await vm.ImportTagMapCommand.ExecuteAsync(null);
                Assert.Equal(tags, vm.TagMapPath);
                Assert.Equal("Tag list tags.csv", vm.StatusMessage);
                Assert.Equal("Tag list", vm.TagMapLabel);
                await Until(() => vm.RootNodes[0].Children.Any(c => c.DisplayName == "Tags"));
                Assert.StartsWith("S7 · tags.csv", vm.OptionsSummary, StringComparison.Ordinal);
                Assert.True(vm.IsDirty);
                await vm.WriteSessionAsync(path);
            }

            var saved = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
            Assert.Equal("tags.csv", (string?)saved["tagMap"]);

            await using var reopened = new MainWindowViewModel();
            await reopened.LoadSessionAsync(path);
            Assert.Equal(tags, reopened.TagMapPath);
            await reopened.ConnectCommand.ExecuteAsync(null);
            await Until(() => reopened.RootNodes[0].Children.Any(c => c.DisplayName == "Tags"));

            await reopened.RemoveTagMapCommand.ExecuteAsync(null);
            Assert.Empty(reopened.TagMapPath);
            await Until(() => reopened.RootNodes[0].Children.All(c => c.DisplayName != "Tags"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task A_tag_list_chosen_before_connecting_is_used_on_connect()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = Url, Dialogs = new TestDialogs { TagMapAnswer = S7SimulatorFixture.TagFile } };
        await vm.ImportTagMapCommand.ExecuteAsync(null);
        Assert.Equal(S7SimulatorFixture.TagFile, vm.TagMapPath);
        Assert.Equal("Tag list tags.csv", vm.StatusMessage);

        await vm.ConnectCommand.ExecuteAsync(null);
        await Until(() => vm.RootNodes[0].Children.Any(c => c.DisplayName == "Tags"));
    }

    private static async Task Until(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
