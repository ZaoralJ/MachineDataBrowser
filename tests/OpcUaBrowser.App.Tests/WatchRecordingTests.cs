using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.LogicalTree;
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
        await Until(() => step.RecordedSamples >= 5); // Recorded column follows the recording's sample count
        Assert.Equal(string.Empty, random.RecordedText);
        Directory.CreateDirectory(OutputDir);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        using (var frame = window.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(OutputDir, "watch-recording.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        // Space (or G) on the selected watch row opens its recorded values.
        var watchGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        watchGrid.SelectedItems.Clear();
        watchGrid.SelectedItems.Add(step);
        watchGrid.Focus();
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("StepUp", dialogs.Viewer?.SelectedItem);
        dialogs.Viewer!.Dispose();
        dialogs.Viewer = null;

        vm.ViewItemRecordingCommand.Execute(step);
        var viewer = Assert.IsType<RecordingViewerViewModel>(dialogs.Viewer);
        Assert.Equal("StepUp", viewer.SelectedItem);
        Assert.True(viewer.HasChart);
        Assert.All(viewer.Rows, r => Assert.Equal("StepUp", r.Name));

        var viewerWindow = new RecordingViewerWindow { DataContext = viewer, Width = 900, Height = 560 };
        viewerWindow.Show();
        Dispatcher.UIThread.RunJobs();

        // Default column widths show full cell texts (the time used to be cut to "21:46:58.6…").
        foreach (var cellText in viewerWindow.GetVisualDescendants().OfType<DataGridCell>().SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>()).Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Take(12))
        {
            var probe = new TextBlock { Text = cellText.Text, FontSize = cellText.FontSize, FontFamily = cellText.FontFamily, FontWeight = cellText.FontWeight };
            probe.Measure(Size.Infinity);
            Assert.True(cellText.Bounds.Width >= probe.DesiredSize.Width - 0.5, $"'{cellText.Text}' clipped: {cellText.Bounds.Width:0} of {probe.DesiredSize.Width:0}px");
        }

        // Columns can be shown and hidden from the Columns flyout.
        var nodeIdColumn = viewerWindow.GetVisualDescendants().OfType<DataGrid>().Single().Columns.Single(c => Equals(c.Header, "NodeId"));
        Assert.False(nodeIdColumn.IsVisible);
        var columnsButton = viewerWindow.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Columns");
        columnsButton.Flyout!.ShowAt(columnsButton);
        Dispatcher.UIThread.RunJobs();
        var toggle = ((StackPanel)((Flyout)columnsButton.Flyout).Content!).Children.OfType<CheckBox>().Single(c => Equals(c.Content, "NodeId"));
        toggle.IsChecked = true;
        Assert.True(nodeIdColumn.IsVisible);
        columnsButton.Flyout.Hide();

        // Selecting a row highlights its sample; clicking the chart selects the nearest sample's row.
        var picked = viewer.Rows[2];
        viewer.SelectedRow = picked;
        Assert.Same(picked, viewer.HighlightedPoint?.Row);
        var chart = viewerWindow.GetVisualDescendants().OfType<TrendChart>().Single();
        var leftEdge = chart.TranslatePoint(new Avalonia.Point(70, chart.Bounds.Height / 2), viewerWindow)!.Value;
        viewerWindow.MouseDown(leftEdge, Avalonia.Input.MouseButton.Left);
        viewerWindow.MouseUp(leftEdge, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(viewer.ChartPoints[0].Row, viewer.SelectedRow);
        Assert.Same(viewer.SelectedRow, viewer.HighlightedPoint?.Row);
        Assert.False(viewer.Follow);
        viewer.SelectedRow = picked;
        Directory.CreateDirectory(OutputDir);
        Dispatcher.UIThread.RunJobs();
        viewerWindow.CaptureRenderedFrame()?.Dispose();
        using (var frame = viewerWindow.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(OutputDir, "recording-trend.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        Assert.True(viewerWindow.GetVisualDescendants().OfType<TrendChart>().Single().IsEffectivelyVisible);
        // G toggles the chart and F follow-latest, also while the rows grid has focus.
        viewerWindow.GetVisualDescendants().OfType<DataGrid>().Single().Focus();
        viewerWindow.KeyPressQwerty(Avalonia.Input.PhysicalKey.G, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewer.ShowChart);
        Assert.False(viewerWindow.GetVisualDescendants().OfType<TrendChart>().Single().IsEffectivelyVisible);
        viewerWindow.KeyPressQwerty(Avalonia.Input.PhysicalKey.G, Avalonia.Input.RawInputModifiers.None);
        var follow = viewer.Follow;
        viewerWindow.KeyPressQwerty(Avalonia.Input.PhysicalKey.F, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewer.ShowChart);
        Assert.NotEqual(follow, viewer.Follow);
        viewerWindow.Close();

        // Add another watched item to the running recording, from the Watch context menu.
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(random);
        grid.ContextMenu!.Open(grid);
        Dispatcher.UIThread.RunJobs();
        var addTo = grid.ContextMenu.Items.OfType<MenuItem>().Single(m => (m.Header as string)?.StartsWith("Add to recording", StringComparison.Ordinal) == true);
        Assert.True(addTo.IsEnabled);
        addTo.Open();
        Dispatcher.UIThread.RunJobs();
        var target = addTo.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, "Trend"));
        Assert.NotNull(target.Command);
        target.Command!.Execute(target.CommandParameter);
        grid.ContextMenu.Close();
        await Until(() => random.HasRecording && recording.Recording.GetSampleCount(random.NodeId) > 0);
        Assert.Equal(2, recording.Recording.Items.Count);

        vm.SelectedRecording = recording;
        await vm.StopRecordingCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(step.IsRecording);
        Assert.True(step.HasRecording);

        await vm.CloseRecordingCommand.ExecuteAsync(null);
        Assert.False(step.HasRecording);

        // Max points per item bounds the history, the Samples count and the viewer, also after changing it later.
        var bounded = await vm.CreateRecordingAsync(new RecordingOptions { Name = "Bounded", SamplingIntervalMs = 100, MaxPointsPerItem = 4 }, [new RecordedItem(random.NodeId, random.DisplayName, random.PortableId)]);
        await Until(() => bounded!.Recording.TotalSamples > 8);
        bounded!.Refresh();
        Assert.Equal(4, bounded.Recording.GetSampleCount(random.NodeId));
        Assert.Equal(4, bounded.Samples);
        using (var boundedViewer = RecordingViewerViewModel.ForRecording(bounded.Recording))
        {
            Assert.True(boundedViewer.Rows.Count <= 4, $"viewer shows {boundedViewer.Rows.Count} rows");
            await bounded.Recording.UpdateOptionsAsync(bounded.Recording.Options with { MaxPointsPerItem = 2 }, TestContext.Current.CancellationToken);
            boundedViewer.Poll();
            Assert.True(boundedViewer.Rows.Count <= 2, $"viewer shows {boundedViewer.Rows.Count} rows after lowering the limit");
            Assert.Equal(2, bounded.Recording.GetSampleCount(random.NodeId));
        }

        vm.SelectedRecording = bounded;
        await vm.CloseRecordingCommand.ExecuteAsync(null);

        // Emptying the watch list while a recording runs asks what to do with it.
        var running = await vm.CreateRecordingAsync(new RecordingOptions { Name = "Left over" }, [new RecordedItem(random.NodeId, random.DisplayName, random.PortableId)]);
        dialogs.RecordingsAnswer = ActiveRecordingsChoice.Stop;
        await vm.ClearWatchCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.RecordingsAsked);
        Assert.Equal(RecordingState.Stopped, running!.Recording.State);
        Assert.Contains(running, vm.Recordings);
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
        public RecordingViewerViewModel? Viewer { get; set; }

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

        public RecordingOptions? SettingsAnswer { get; set; }

        public Task<RecordingOptions?> EditRecordingSettingsAsync(NewRecordingViewModel form) => Task.FromResult(SettingsAnswer);

        public ActiveRecordingsChoice RecordingsAnswer { get; set; } = ActiveRecordingsChoice.Keep;

        public int RecordingsAsked { get; private set; }

        public Task<ActiveRecordingsChoice> AskActiveRecordingsAsync(int count)
        {
            RecordingsAsked++;
            return Task.FromResult(RecordingsAnswer);
        }
    }
}
