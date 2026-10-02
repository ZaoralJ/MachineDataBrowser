using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core.Tests;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class ConnectionTabsTests(OpcPlcFixture plc, CustomTypesServerFixture custom)
{
    private static async Task WatchAsync(MainWindowViewModel vm, params string[] path)
    {
        var node = vm.RootNodes[0];
        foreach (var name in path)
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        vm.SelectedNode = node;
        await vm.AddToWatchCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task Two_connections_in_one_window_keep_their_own_panes_and_close_separately()
    {
        var first = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = first, Width = 1280, Height = 800 };
        window.Show();
        Assert.False(window.Tabs.HasMany); // one connection: no tab strip
        await first.ConnectCommand.ExecuteAsync(null);
        await WatchAsync(first, "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp");
        var firstDock = window.FindControl<ContentControl>("DockHost")!.Content;

        window.NewConnectionTab();
        var second = Assert.IsType<MainWindowViewModel>(window.DataContext);
        Assert.NotSame(first, second);
        Assert.True(window.Tabs.HasMany);
        second.EndpointUrl = custom.EndpointUrl;
        await second.ConnectCommand.ExecuteAsync(null);
        await WatchAsync(second, "Objects", "Custom", "History", "Temperature");
        Dispatcher.UIThread.RunJobs();

        // Separate connections, watch lists and docks; the tabs show each endpoint and connection state.
        Assert.Equal(["StepUp"], first.WatchItems.Select(w => w.DisplayName));
        Assert.Equal(["Temperature"], second.WatchItems.Select(w => w.DisplayName));
        Assert.NotSame(firstDock, window.FindControl<ContentControl>("DockHost")!.Content);
        Assert.All(window.Tabs.Tabs, t => Assert.True(t.IsConnected));
        Assert.Equal(["localhost:" + new Uri(plc.EndpointUrl).Port, "localhost:" + new Uri(custom.EndpointUrl).Port], window.Tabs.Tabs.Select(t => t.Title));

        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "connection-tabs.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        // Switching back shows the first connection's own dock again.
        window.ShowNextTab(1);
        Assert.Same(first, window.DataContext);
        Assert.Same(firstDock, window.FindControl<ContentControl>("DockHost")!.Content);
        Assert.True(window.Tabs.Tabs[0].IsActive);

        // Closing a tab with unsaved changes asks; Don't Save closes it and disconnects that connection only.
        var dialogs = new TestDialogs();
        window.ShowNextTab(1);
        second.Dialogs = dialogs;
        Assert.True(second.IsDirty);
        await window.CloseConnectionTabAsync(second);
        Assert.Same(first, window.DataContext);
        Assert.False(window.Tabs.HasMany);
        Dispatcher.UIThread.RunJobs(); // the client reports its state through the dispatcher
        Assert.False(second.IsConnected);
        Assert.True(first.IsConnected);

        // The last tab stays.
        await window.CloseConnectionTabAsync(first);
        Assert.Single(window.Tabs.Tabs);

        await first.DisposeAsync();
        window.Close();
    }
}
