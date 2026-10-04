using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class HistoryAppTests(CustomTypesServerFixture server)
{
    [AvaloniaFact]
    public async Task Show_history_opens_the_stored_values_as_table_and_chart()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel { EndpointUrl = server.EndpointUrl, Dialogs = dialogs };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.SupportsHistory);

        var node = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "Custom", "History", "Temperature" })
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        vm.SelectedNode = node;
        await vm.ShowHistoryCommand.ExecuteAsync(60);
        var viewer = Assert.IsType<RecordingViewerViewModel>(dialogs.Viewer);
        Assert.Contains("Temperature", viewer.Title, StringComparison.Ordinal);
        // 360 prefilled samples (one per 10 s) plus one per second since the shared container started.
        Assert.InRange(viewer.Rows.Count, 355, 360 + 3600);
        Assert.True(viewer.HasChart);
        Assert.StartsWith("History from the server", viewer.StatusText, StringComparison.Ordinal);

        var window = new RecordingViewerWindow { DataContext = viewer, Width = 900, Height = 560 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "history.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        window.Close();
    }
}
