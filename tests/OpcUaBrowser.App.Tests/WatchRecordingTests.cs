using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core;
using OpcUaBrowser.Core.Tests;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class WatchRecordingTests(OpcPlcFixture plc)
{
    private static readonly string OutputDir = Path.Combine(AppContext.BaseDirectory, "screenshots");

    [AvaloniaFact]
    public async Task Recorded_items_show_indicator_and_open_their_values_with_a_chart()
    {
        var dialogs = new CapturingDialogs();
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        vm.Dialogs = dialogs; // the window registers itself as the dialog service
        await vm.ConnectCommand.ExecuteAsync(null);
        var basic = await Find(await Find(await Find(await Find(vm.RootNodes[0], "Objects"), "OpcPlc"), "Telemetry"), "Basic");
        foreach (var name in new[] { "StepUp", "RandomSignedInt32" })
        {
            vm.SelectedNode = await Find(basic, name);
            await vm.AddToWatchCommand.ExecuteAsync(null);
        }

        var step = vm.WatchItems.Single(w => w.DisplayName == "StepUp");
        var random = vm.WatchItems.Single(w => w.DisplayName == "RandomSignedInt32");
        await Until(() => step.Monitor is not null && random.Monitor is not null);

        var recording = await vm.CreateRecordingAsync(
            new RecordingOptions { Name = "Trend", SamplingIntervalMs = 100 },
            [new RecordedItem(step.NodeId, step.DisplayName, step.PortableId)]);
        Assert.NotNull(recording);
        Assert.True(step.IsRecording);
        Assert.True(step.HasRecording);
        Assert.Contains("Trend", step.RecordingToolTip, StringComparison.Ordinal);
        Assert.False(random.HasRecording);

        await Until(() => recording.Recording.GetSampleCount(step.NodeId) >= 5);
        Directory.CreateDirectory(OutputDir);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        using (var frame = window.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(OutputDir, "watch-recording.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        vm.ViewItemRecordingCommand.Execute(step);
        var viewer = Assert.IsType<RecordingViewerViewModel>(dialogs.Viewer);
        Assert.Equal("StepUp", viewer.SelectedItem);
        Assert.True(viewer.HasChart);
        Assert.All(viewer.Rows, r => Assert.Equal("StepUp", r.Name));

        var viewerWindow = new RecordingViewerWindow { DataContext = viewer, Width = 900, Height = 560 };
        viewerWindow.Show();
        Directory.CreateDirectory(OutputDir);
        Dispatcher.UIThread.RunJobs();
        viewerWindow.CaptureRenderedFrame()?.Dispose();
        using (var frame = viewerWindow.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(OutputDir, "recording-trend.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        Assert.True(viewerWindow.GetVisualDescendants().OfType<TrendChart>().Single().IsEffectivelyVisible);
        viewerWindow.Close();

        vm.SelectedRecording = recording;
        await vm.StopRecordingCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(step.IsRecording);
        Assert.True(step.HasRecording);

        await vm.CloseRecordingCommand.ExecuteAsync(null);
        Assert.False(step.HasRecording);
        window.Close();
    }

    private static async Task<NodeViewModel> Find(NodeViewModel parent, string name)
    {
        parent.IsExpanded = true;
        NodeViewModel? found = null;
        await Until(() => (found = parent.Children.FirstOrDefault(c => c.DisplayName == name)) is not null);
        return found!;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private sealed class CapturingDialogs : IDialogService
    {
        public RecordingViewerViewModel? Viewer { get; private set; }

        public void ShowRecordingViewer(RecordingViewerViewModel viewer) => Viewer = viewer;

        public Task<string?> PickSessionToOpenAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSessionSaveTargetAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task<string?> PickCsvSaveTargetAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string documentName) => Task.FromResult(UnsavedChangesChoice.Discard);

        public Task<AppSettings?> EditSettingsAsync(AppSettings current) => Task.FromResult<AppSettings?>(null);

        public Task ShowAboutAsync() => Task.CompletedTask;

        public Task<string?> PickRecordingFileAsync() => Task.FromResult<string?>(null);

        public Task<RecordingOptions?> EditNewRecordingAsync(NewRecordingDraft draft) => Task.FromResult<RecordingOptions?>(null);

        public Task<string?> PickExportTargetAsync(string suggestedName, string extension) => Task.FromResult<string?>(null);

        public void RevealInFileManager(string path)
        {
        }
    }
}
