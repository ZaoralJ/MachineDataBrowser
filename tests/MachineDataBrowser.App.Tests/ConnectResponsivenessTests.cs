using System.Diagnostics;
using Avalonia.Headless.XUnit;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class ConnectResponsivenessTests(OpcPlcFixture plc)
{
    /// <summary>Connect and disconnect run the client work off the UI thread: the commands return to the caller at once.</summary>
    [AvaloniaFact]
    public async Task Connect_and_disconnect_do_not_block_the_ui_thread()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl, AutoAcceptCertificates = true };

        var watch = Stopwatch.StartNew();
        var connect = vm.ConnectCommand.ExecuteAsync(null);
        var blocked = watch.ElapsedMilliseconds;
        await connect;
        Assert.True(vm.IsConnected);
        Assert.True(blocked < 100, $"connect blocked the UI thread for {blocked} ms (total {watch.ElapsedMilliseconds} ms)");

        watch.Restart();
        var disconnect = vm.DisconnectCommand.ExecuteAsync(null);
        blocked = watch.ElapsedMilliseconds;
        await disconnect;
        Assert.True(vm.IsDisconnected);
        Assert.True(blocked < 100, $"disconnect blocked the UI thread for {blocked} ms (total {watch.ElapsedMilliseconds} ms)");
    }
}
