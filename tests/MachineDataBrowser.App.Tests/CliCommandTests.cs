using Avalonia.Headless.XUnit;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class CliCommandTests(OpcPlcFixture plc)
{
    private static readonly CliCommand.Connection Lab = new("opc.tcp://plc-01:4840", null, UseSecurity: false, TrustAll: true);

    [Theory]
    [InlineData("ns=3;s=StepUp", "'ns=3;s=StepUp'")]
    [InlineData("Program:Main.Speed", "Program:Main.Speed")]
    [InlineData("t:plant/line 1/speed", "'t:plant/line 1/speed'")]
    [InlineData("it's", "'it'\\''s'")]
    public void Arguments_are_quoted_for_the_shell_only_when_needed(string argument, string expected) =>
        Assert.Equal(expected, CliCommand.Quote(argument));

    [Theory]
    [InlineData("mqtt://user:secret@broker:1883/plant/#", "mqtt://user@broker:1883/plant/#")]
    [InlineData("mqtt://user@broker", "mqtt://user@broker")]
    [InlineData("opc.tcp://plc:4840", "opc.tcp://plc:4840")]
    public void Passwords_in_the_endpoint_are_left_out(string url, string expected) =>
        Assert.Equal(expected, CliCommand.WithoutPassword(url));

    [Fact]
    public void Options_carry_over_except_what_the_protocol_does_not_use()
    {
        Assert.Equal("mdbrowser read opc.tcp://plc-01:4840 'ns=3;s=A' 'ns=3;s=B' --trust-all", CliCommand.Read(Lab, ["ns=3;s=A", "ns=3;s=B"], recursive: false));
        Assert.Equal("mdbrowser monitor opc.tcp://plc:4840 'ns=3;s=Line1' -R -r 1000 --user op --secure",
            CliCommand.Monitor(new("opc.tcp://plc:4840", "op", UseSecurity: true, TrustAll: false), ["ns=3;s=Line1"], recursive: true, refreshMs: 1000));
        Assert.Equal("mdbrowser browse eip://10.0.0.5/1,0", CliCommand.Browse(new("eip://10.0.0.5/1,0", null, UseSecurity: true, TrustAll: false), null));
    }

    [AvaloniaFact]
    public async Task Address_space_and_watch_selections_become_commands_on_the_clipboard()
    {
        string? copied = null;
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl, AutoAcceptCertificates = true, CopyToClipboard = text => { copied = text; return Task.CompletedTask; } };
        await vm.ConnectCommand.ExecuteAsync(null);
        var basic = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        var stepUp = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp");
        const string ns = "nsu=http://microsoft.com/Opc/OpcPlc/";

        vm.SelectedNode = basic;
        await vm.CopyCliBrowseCommand.ExecuteAsync(null);
        Assert.Equal($"mdbrowser browse {plc.EndpointUrl} /Objects/OpcPlc/Telemetry/Basic --trust-all", copied);

        await vm.CopyCliMonitorCommand.ExecuteAsync(null);
        Assert.Equal($"mdbrowser monitor {plc.EndpointUrl} /Objects/OpcPlc/Telemetry/Basic -R --trust-all", copied);

        vm.SelectedNode = stepUp;
        await vm.CopyCliReadCommand.ExecuteAsync(null);
        Assert.Equal($"mdbrowser read {plc.EndpointUrl} /Objects/OpcPlc/Telemetry/Basic/StepUp --trust-all", copied);

        vm.SelectedNode = basic;
        await vm.MonitorFolderCommand.ExecuteAsync(null);
        foreach (var item in vm.WatchItems)
        {
            vm.SelectedWatchItems.Add(item);
        }

        await vm.SetRefreshCommand.ExecuteAsync(1000);
        vm.SelectedWatchItems.Clear();
        await vm.CopyWatchCliMonitorCommand.ExecuteAsync(null);   // nothing selected: every row
        Assert.StartsWith($"mdbrowser monitor {plc.EndpointUrl} /Objects/OpcPlc/Telemetry/Basic/", copied, StringComparison.Ordinal);
        Assert.Equal(4, copied!.Split(' ').Count(a => a.StartsWith("/Objects/OpcPlc/Telemetry/Basic/", StringComparison.Ordinal)));
        Assert.EndsWith(" -r 1000 --trust-all", copied, StringComparison.Ordinal);

        vm.SelectedWatchItems.Add(vm.WatchItems.Single(w => w.DisplayName == "StepUp"));
        await vm.CopyWatchCliReadCommand.ExecuteAsync(null);
        Assert.Equal($"mdbrowser read {plc.EndpointUrl} /Objects/OpcPlc/Telemetry/Basic/StepUp --trust-all", copied);

        // A row from an older session has no path: its portable id is used instead.
        var legacy = new WatchItemViewModel(vm.WatchItems[0].NodeId, "Legacy") { PortableId = $"{ns};s=StepUp" };
        vm.WatchItems.Add(legacy);
        vm.SelectedWatchItems.Clear();
        vm.SelectedWatchItems.Add(legacy);
        await vm.CopyWatchCliReadCommand.ExecuteAsync(null);
        Assert.Equal($"mdbrowser read {plc.EndpointUrl} '{ns};s=StepUp' --trust-all", copied);
    }

    private static async Task<NodeViewModel> Navigate(MainWindowViewModel vm, params string[] path)
    {
        var node = vm.RootNodes[0];
        foreach (var name in path)
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        return node;
    }
}
