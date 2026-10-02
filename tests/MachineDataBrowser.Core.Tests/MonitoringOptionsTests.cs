using System.Collections.Concurrent;
using Opc.Ua;
using MachineDataBrowser.Core.Ua;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class MonitoringOptionsTests(OpcPlcFixture plc) : IAsyncLifetime
{
    private readonly OpcUaClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<NodeId> FindAsync(params string[] path)
    {
        var node = ObjectIds.ObjectsFolder;
        foreach (var name in path)
        {
            node = (await _client.BrowseAsync(node, Ct)).Single(c => c.DisplayName == name).NodeId;
        }

        return node;
    }

    [Fact]
    public async Task Queue_delivers_every_sample_and_deadband_suppresses_small_changes()
    {
        // StepUp counts up every 100 ms (opc-plc's default cycle).
        var stepUp = await FindAsync("OpcPlc", "Telemetry", "Basic", "StepUp");
        var updates = new ConcurrentQueue<ValueUpdate>();
        var monitor = (await _client.MonitorManyAsync([stepUp], updates.Enqueue, 2000, Ct))[0].Handle!;

        // Publishing every 2 s with sampling 100 ms and a queue of 50: each publish carries ~20 values instead of one.
        var results = await _client.ApplyMonitoringOptionsAsync([monitor], new MonitoringOptions { SamplingIntervalMs = 100, QueueSize = 50 }, Ct);
        Assert.True(ServiceResult.IsGood(results[0]));
        var revised = OpcUaClient.GetRevisedMonitoring(monitor)!.Value;
        Assert.Equal(50u, revised.QueueSize);
        updates.Clear();
        await Task.Delay(4500, Ct);
        Assert.True(updates.Count >= 15, $"{updates.Count} updates with a queue; one per publish without it");

        // A deadband larger than any change: nothing arrives after the first value.
        await _client.ApplyMonitoringOptionsAsync([monitor], new MonitoringOptions { Deadband = DeadbandKind.Absolute, DeadbandValue = 1e12 }, Ct);
        await Task.Delay(2500, Ct);
        updates.Clear();
        await Task.Delay(4500, Ct);
        Assert.True(updates.Count <= 1, $"{updates.Count} updates despite the deadband");

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task Rejected_options_keep_the_previous_settings()
    {
        // StepUp has no EURange, so a percent deadband is not allowed.
        var stepUp = await FindAsync("OpcPlc", "Telemetry", "Basic", "StepUp");
        var monitor = (await _client.MonitorManyAsync([stepUp], _ => { }, 500, Ct))[0].Handle!;
        var results = await _client.ApplyMonitoringOptionsAsync([monitor], new MonitoringOptions { Deadband = DeadbandKind.Percent, DeadbandValue = 5, QueueSize = 7 }, Ct);
        Assert.True(ServiceResult.IsBad(results[0]));
        Assert.Equal(1u, OpcUaClient.GetRevisedMonitoring(monitor)!.Value.QueueSize);
        await monitor.DisposeAsync();
    }
}
