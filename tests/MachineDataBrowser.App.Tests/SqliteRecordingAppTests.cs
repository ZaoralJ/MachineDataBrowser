using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class SqliteRecordingAppTests(OpcPlcFixture plc) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("app-sqlite").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Watch_recording_to_a_db_file_opens_in_the_viewer_with_a_chart()
    {
        var file = Path.Combine(_dir, "line1.db");
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl, AutoAcceptCertificates = true };
        await vm.ConnectCommand.ExecuteAsync(null);
        var node = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp" })
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        await vm.MonitorNodeCommand.ExecuteAsync(node);
        var step = Assert.Single(vm.WatchItems);
        var recording = await vm.CreateRecordingAsync(
            new RecordingOptions { Name = "Line 1", SamplingIntervalMs = 100, LiveFilePath = file },
            [new RecordedItem(step.NodeId, step.DisplayName, step.PortableId, step.Path)]);
        Assert.NotNull(recording);
        await Until(() => recording.Recording.TotalSamples >= 5);
        Assert.Equal(plc.EndpointUrl, recording.Recording.Options.Endpoint);   // kept with the recording
        await recording.Recording.StopAsync(TestContext.Current.CancellationToken);

        using var viewer = RecordingViewerViewModel.ForFile(file);
        await Until(() =>
        {
            viewer.Poll();
            return viewer.Rows.Count >= 5;
        });
        Assert.All(viewer.Rows, r => Assert.Equal("StepUp", r.Name));
        Assert.True(viewer.HasChart);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
