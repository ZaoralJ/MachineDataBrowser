using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DotNet.Testcontainers.Builders;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class ResilienceTests
{
    [AvaloniaFact]
    public async Task Reported_errors_are_logged_and_shown_in_the_error_bar()
    {
        // The dispatcher hook itself cannot be exercised here: the headless test host fails any test that lets an
        // exception reach the dispatcher. It forwards to Report, which is what this covers.
        AppErrors.Install();
        AppErrors.LogPath = Path.Combine(Path.GetTempPath(), $"machinedatabrowser-test-{Guid.NewGuid():N}.log");
        await using var vm = new MainWindowViewModel();

        AppErrors.Report(new InvalidDataException("boom from a handler"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("InvalidDataException: boom from a handler", vm.ErrorMessage);
        Assert.Contains("boom from a handler", await File.ReadAllTextAsync(AppErrors.LogPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        File.Delete(AppErrors.LogPath);
    }

    [Fact]
    public void Dock_drag_glitch_is_recognised_only_from_dock_pointer_handling()
    {
        Assert.True(AppErrors.IsDockDragGlitch(new TracedArgumentException(
            "   at Avalonia.VisualExtensions.PointToClient(Visual visual, PixelPoint point)\n   at Dock.Avalonia.Internal.DockControlState.Over(Point point)")));
        Assert.False(AppErrors.IsDockDragGlitch(new TracedArgumentException("   at MachineDataBrowser.App.Something()")));
        Assert.False(AppErrors.IsDockDragGlitch(new InvalidOperationException("Visual does not belong to a visual tree.")));
    }

    private sealed class TracedArgumentException(string trace) : ArgumentException("Visual does not belong to a visual tree.", "visual")
    {
        public override string StackTrace => trace;
    }

    [AvaloniaFact]
    public async Task Losing_the_server_shows_an_error_and_disconnect_still_works()
    {
        await using var container = new ContainerBuilder("mcr.microsoft.com/iotedge/opc-plc:latest")
            .WithPortBinding(50000, true)
            .WithCommand("--pn=50000", "--autoaccept", "--unsecuretransport", "--ph=localhost")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("OPC UA Server started"))
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);

        await using var vm = new MainWindowViewModel { EndpointUrl = $"opc.tcp://localhost:{container.GetMappedPublicPort(50000)}" };
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        await Until(() => vm.RootNodes.Count == 1 && vm.RootNodes[0].Children.Count > 0);

        await container.StopAsync(TestContext.Current.CancellationToken);
        await Until(() => vm.State != ConnectionState.Connected, seconds: 60);
        Assert.NotNull(vm.ErrorMessage);

        // Browsing and disconnecting against a dead server report errors instead of throwing.
        vm.RootNodes[0].Children[0].IsExpanded = true;
        await vm.DisconnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsDisconnected);
    }

    private static async Task Until(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
