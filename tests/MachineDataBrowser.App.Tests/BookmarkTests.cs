using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class BookmarkTests(OpcPlcFixture plc) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bookmarks").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static async Task<NodeViewModel> FindAsync(MainWindowViewModel vm, params string[] path)
    {
        var node = vm.RootNodes[0];
        foreach (var name in path)
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        return node;
    }

    [AvaloniaFact]
    public async Task Bookmarks_reveal_their_node_and_are_saved_with_the_session()
    {
        var session = Path.Combine(_dir, "bookmarks.mdbsession");
        await using (var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl })
        {
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.ConnectCommand.ExecuteAsync(null);
            vm.SelectedNode = await FindAsync(vm, "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp");
            vm.ToggleBookmarkCommand.Execute(null);
            var bookmark = Assert.Single(vm.Bookmarks);
            Assert.Equal("StepUp", bookmark.Name);
            Assert.Equal("Objects › OpcPlc › Telemetry › Basic", bookmark.Path);
            Assert.StartsWith("nsu=", bookmark.NodeId, StringComparison.Ordinal); // portable across restarts

            // Collapse everything and select something else; the bookmark brings it back.
            vm.CollapseAllCommand.Execute(null);
            vm.SelectedNode = vm.RootNodes[0];
            await vm.GoToBookmarkCommand.ExecuteAsync(bookmark);
            await WaitUntil(() => vm.SelectedNode?.DisplayName == "StepUp");
            Assert.Equal("StepUp", vm.SelectedNode?.DisplayName);

            // Toggling on the bookmarked node removes it; add it back for the session.
            vm.ToggleBookmarkCommand.Execute(null);
            Assert.Empty(vm.Bookmarks);
            vm.ToggleBookmarkCommand.Execute(null);
            Assert.True(vm.IsDirty);
            await vm.WriteSessionAsync(session);
            window.Close();
        }

        await using var reopened = new MainWindowViewModel();
        await reopened.LoadSessionAsync(session);
        var restored = Assert.Single(reopened.Bookmarks);
        Assert.Equal("StepUp", restored.Name);
        if (!reopened.IsConnected)
        {
            await reopened.ConnectCommand.ExecuteAsync(null); // a session without watch items doesn't connect by itself
        }

        await reopened.GoToBookmarkCommand.ExecuteAsync(restored);
        await WaitUntil(() => reopened.SelectedNode?.DisplayName == "StepUp");
        Assert.Equal("StepUp", reopened.SelectedNode?.DisplayName);
        reopened.RemoveBookmarkCommand.Execute(restored);
        Assert.Empty(reopened.Bookmarks);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the bookmark navigation to finish.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
