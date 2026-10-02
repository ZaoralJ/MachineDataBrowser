using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core;
using OpcUaBrowser.Core.Tests;
using OpcUaBrowser.Core.Ua;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class MonitoringSettingsTests(OpcPlcFixture plc) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("monitoring").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static async Task<WatchItemViewModel> WatchStepUpAsync(MainWindowViewModel vm)
    {
        var node = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp" })
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        vm.SelectedNode = node;
        await vm.AddToWatchCommand.ExecuteAsync(null);
        var item = Assert.Single(vm.WatchItems);
        vm.SelectedWatchItem = item;
        return item;
    }

    [AvaloniaFact]
    public async Task Settings_apply_survive_refresh_changes_and_sessions_and_rejections_keep_the_old()
    {
        var options = new MonitoringOptions { SamplingIntervalMs = 100, QueueSize = 20 };
        var dialogs = new TestDialogs { MonitoringAnswer = options };
        var session = Path.Combine(_dir, "test.opcsession");
        await using (var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl, Dialogs = dialogs })
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            var item = await WatchStepUpAsync(vm);
            Assert.True(vm.EditMonitoringCommand.CanExecute(null));

            await vm.EditMonitoringCommand.ExecuteAsync(null);
            Assert.Equal(options, item.Monitoring);
            Assert.EndsWith("⚙", item.RefreshText, StringComparison.Ordinal);
            Assert.Contains("queue 20", item.RefreshToolTip, StringComparison.Ordinal);
            Assert.Equal(20u, OpcUaClient.GetRevisedMonitoring(item.Monitor!)!.Value.QueueSize);

            // Changing the refresh time re-creates the monitor: the settings are applied again.
            await vm.SetRefreshCommand.ExecuteAsync(1000);
            Assert.Equal(1000, item.RefreshMs);
            Assert.Equal(20u, OpcUaClient.GetRevisedMonitoring(item.Monitor!)!.Value.QueueSize);

            // Rejected (StepUp has no EURange for a percent deadband): the row keeps its settings.
            dialogs.MonitoringAnswer = new MonitoringOptions { Deadband = DeadbandKind.Percent, DeadbandValue = 5 };
            await vm.EditMonitoringCommand.ExecuteAsync(null);
            Assert.Equal(options, item.Monitoring);
            await Until(() => vm.ErrorMessage?.Contains("EURange", StringComparison.Ordinal) == true);

            await vm.WriteSessionAsync(session);
        }

        await using (var reopened = new MainWindowViewModel { Dialogs = dialogs })
        {
            await reopened.LoadSessionAsync(session);
            var item = Assert.Single(reopened.WatchItems);
            Assert.Equal(options, item.Monitoring);
            Assert.Equal(20u, OpcUaClient.GetRevisedMonitoring(item.Monitor!)!.Value.QueueSize);
        }

        var window = new MonitoringSettingsWindow { DataContext = new MonitoringSettingsViewModel(options with { Deadband = DeadbandKind.Absolute, DeadbandValue = 0.5 }, "StepUp", 1000) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "monitoring-settings.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        window.Close();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
