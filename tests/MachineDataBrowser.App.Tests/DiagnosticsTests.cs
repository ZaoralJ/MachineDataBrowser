using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class DiagnosticsTests(OpcPlcFixture plc)
{
    [AvaloniaFact]
    public async Task Diagnostics_show_session_server_subscriptions_and_update_rate()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl, Dialogs = dialogs, DefaultRefreshMs = 250 };

        vm.ShowDiagnosticsCommand.Execute(null);
        using (var offline = dialogs.Diagnostics!)
        {
            await offline.RefreshAsync();
            Assert.Contains(offline.Session, r => r.Name == "State" && r.Value == "Disconnected");
        }

        await vm.ConnectCommand.ExecuteAsync(null);
        var node = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "OpcPlc", "Telemetry", "Basic" })
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        await node.EnsureChildrenLoadedAsync();
        vm.SelectedNode = node.Children.Single(c => c.DisplayName == "StepUp");
        await vm.AddToWatchCommand.ExecuteAsync(null);

        vm.ShowDiagnosticsCommand.Execute(null);
        using var diagnostics = dialogs.Diagnostics!;
        await diagnostics.RefreshAsync();
        Assert.Contains(diagnostics.Session, r => r.Name == "Security" && r.Value.StartsWith("None", StringComparison.Ordinal));
        Assert.Contains(diagnostics.Session, r => r.Name == "Server state" && r.Value == "Running");
        Assert.Contains(diagnostics.Session, r => r.Name == "Server clock");
        var watch = Assert.Single(diagnostics.Subscriptions, s => s.Name == "Watch@250");
        Assert.InRange(watch.Items, 1u, 2u); // the watched value, plus the Attributes pane's live value once it starts

        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        await diagnostics.RefreshAsync();
        Assert.Contains(diagnostics.App, r => r.Name == "Value updates" && !r.Value.StartsWith("0 /", StringComparison.Ordinal) && r.Value.Contains("/ s", StringComparison.Ordinal));

        var window = new DiagnosticsWindow { DataContext = diagnostics };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "diagnostics.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        window.Close();
    }
}
