using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Opc.Ua;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class QuitPromptTests
{
    private static MainWindow DirtyWindow()
    {
        var vm = new MainWindowViewModel();
        vm.WatchItems.Add(new WatchItemViewModel(new NodeId(1u, 2), "x"));
        Assert.True(vm.IsDirty);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Quitting_again_right_away_quits_without_saving()
    {
        var window = DirtyWindow();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.Single(window.OwnedWindows);

        window.Close(); // held shortcut: the next quit request arrives immediately
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Only_one_prompt_and_closing_it_cancels()
    {
        var window = DirtyWindow();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(1700, TestContext.Current.CancellationToken);

        window.Close(); // a later request focuses the open prompt instead of opening another
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.OwnedWindows);

        window.OwnedWindows[0].Close(); // closing the prompt without a button is Cancel, never Save
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.True(((MainWindowViewModel)window.DataContext!).IsDirty);
        window.Close();
    }
}

public sealed class GentleQuitTests(OpcUaBrowser.Core.Tests.OpcPlcFixture plc)
{
    [AvaloniaFact]
    public async Task Held_quit_still_stops_recordings_flushes_files_and_disconnects()
    {
        var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        var item = new WatchItemViewModel(new NodeId("StepUp", 3), "StepUp");
        vm.WatchItems.Add(item); // makes the session dirty, so quitting prompts
        var file = Path.Combine(Path.GetTempPath(), $"gentle-quit-{Guid.NewGuid():N}.csv");
        var recording = await vm.CreateRecordingAsync(
            new OpcUaBrowser.Core.RecordingOptions { Name = "Quit", SamplingIntervalMs = 100, LiveFilePath = file },
            [new OpcUaBrowser.Core.RecordedItem(item.NodeId, item.DisplayName, "ns=3;s=StepUp")]);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (recording!.Recording.TotalSamples < 3 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close(); // held quit shortcut
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (window.IsVisible && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.False(window.IsVisible);
        Assert.Equal(OpcUaBrowser.Core.RecordingState.Closed, recording.Recording.State);
        Assert.True(File.ReadAllLines(file).Length > 3); // header + flushed samples
        File.Delete(file);
    }

    [AvaloniaFact]
    public async Task While_the_quit_prompt_is_open_everything_keeps_running()
    {
        var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.WatchItems.Add(new WatchItemViewModel(new NodeId("StepUp", 3), "StepUp"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.OwnedWindows);
        Assert.True(vm.IsConnected);

        window.OwnedWindows[0].Close(); // cancel the prompt
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.True(vm.IsConnected);
        await vm.DisposeAsync();
    }
}
