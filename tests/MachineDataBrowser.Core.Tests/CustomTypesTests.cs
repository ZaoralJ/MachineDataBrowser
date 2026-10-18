using System.Text.Json.Nodes;
using Opc.Ua;
using Opc.Ua.Encoders;
using MachineDataBrowser.Driver.OpcUa;
using Xunit;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;

namespace MachineDataBrowser.Core.Tests;

/// <summary>OpcUaClient against the custom-types server in <c>simulators/opcua-custom</c>.</summary>
public sealed class CustomTypesTests(CustomTypesServerFixture server) : IAsyncLifetime
{
    private readonly IDeviceClient _client = new OpcUaClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = server.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<BrowseItem> FindAsync(params string[] path)
    {
        var current = new BrowseItem(NodeId.FromOpaque(ObjectIds.ObjectsFolder.ToString()), "Objects", "Objects", NodeClass.Object);
        foreach (var name in path)
        {
            var children = await _client.BrowseAsync(current.NodeId, Ct);
            current = Assert.Single(children, c => c.DisplayName == name);
        }

        return current;
    }

    [Fact]
    public async Task Events_from_an_object_without_events_report_the_server_status()
    {
        var custom = await FindAsync("Custom");
        var ex = await Assert.ThrowsAsync<DeviceClientException>(async () => await ((IEventSource)_client).SubscribeEventsAsync(custom.NodeId, _ => { }, Ct));
        Assert.True(ex.StatusCode.IsBad);
    }

    private async Task<object?> ReadAsync(params string[] path) =>
        (await _client.ReadValuesAsync([(await FindAsync(path)).NodeId], Ct))[0];

    [Fact]
    public async Task Nested_structure_with_arrays_decodes_through_the_type_system()
    {
        var value = Assert.IsType<ExtensionObject>(await ReadAsync("Custom", "Machines", "Machine1", "Status"));

        Assert.True(value.TryGetValue(out IEncodeable? body));
        Assert.NotNull(body);
        Assert.Equal("MachineStatus", Assert.IsType<Structure>(body).XmlName.Name);
        var text = UaValueFormatter.Format(new Variant(value));
        Assert.StartsWith("{", text, StringComparison.Ordinal); // decoded fields: {State|Speed|{X|Y|Z}|…}
        Assert.DoesNotContain("undecoded", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dynamic_structures_format_as_named_nested_fields()
    {
        var value = Assert.IsType<ExtensionObject>(await ReadAsync("Custom", "Machines", "Machine1", "Status"));
        var json = Assert.IsType<JsonObject>(ValueJson.ToJson(value));

        Assert.True(json.ContainsKey("State"));
        Assert.True(json.ContainsKey("Speed"));
        Assert.True(json["Position"]!.AsObject().ContainsKey("X"));
        Assert.True(json["Tool"]!["Offset"]!.AsObject().ContainsKey("Z"));
        Assert.IsType<JsonArray>(json["Alarms"]);
        Assert.IsType<JsonArray>(json["Messages"]);
        Assert.True(json["Waypoints"] is JsonArray, json.ToJsonString());

        var text = UaValueFormatter.Format(new Variant(value));
        Assert.Contains("\"Position\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Speed\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structure_arrays_optional_fields_and_unions_decode()
    {
        var history = Assert.IsType<ArrayOf<ExtensionObject>>(await ReadAsync("Custom", "Structures", "StatusHistory")).ToList();
        Assert.Equal(5, history.Count);
        Assert.All(history, h =>
        {
            Assert.True(h.TryGetValue(out IEncodeable? body));
            Assert.NotNull(body);
        });

        foreach (var name in new[] { "QualityFull", "QualityPartial", "SetpointNumeric", "SetpointText", "Position" })
        {
            var value = Assert.IsType<ExtensionObject>(await ReadAsync("Custom", "Structures", name));
            Assert.True(value.TryGetValue(out IEncodeable? body));
            Assert.NotNull(body);
        }
    }

    [Fact]
    public async Task Monitored_structure_is_decoded_from_the_first_update()
    {
        // Types load on first use, so the first notification arrives before its type is known.
        var node = await FindAsync("Custom", "Structures", "Position");
        var updates = new System.Collections.Concurrent.ConcurrentQueue<ValueUpdate>();
        await using var handle = await _client.MonitorAsync(node.NodeId, updates.Enqueue, 100, Ct);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (updates.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(updates.Count >= 3, $"only {updates.Count} updates");
        Assert.All(updates, u =>
        {
            Assert.True(Assert.IsType<ExtensionObject>(u.Raw).TryGetValue(out IEncodeable? body));
            Assert.NotNull(body);
        });
        var times = updates.Select(u => u.SourceTimestamp).ToList();
        Assert.Equal(times.Order(), times); // waiting for the type load doesn't reorder updates
    }

    [Fact]
    public async Task Multi_dimensional_arrays_read_as_matrices()
    {
        var matrix = await ReadAsync("Custom", "DataTypes", "Arrays", "Matrix3x4");
        Assert.NotNull(matrix);
        Assert.Contains("3", UaValueFormatter.Format(UaValueFormatter.ToVariant(matrix)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flat_folder_with_thousands_of_children_browses_completely()
    {
        var flat = await FindAsync("Custom", "Flat");
        Assert.Equal(2500, (await _client.BrowseAsync(flat.NodeId, Ct)).Count);
    }

    [Fact]
    public async Task Typed_machine_exports_to_csharp()
    {
        var machine = await FindAsync("Custom", "Machines", "Machine2");
        var tree = await _client.ReadTreeAsync(machine.NodeId, "Machine2", NodeClass.Object, 3, 100, Ct);
        var code = NodeExport.ToCSharp(tree);

        Assert.Contains("MachineStatus Status", code, StringComparison.Ordinal);
        Assert.Contains("SerialNumber", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fast_values_change_every_10_ms()
    {
        var node = await FindAsync("Custom", "Fast", "Every10ms", "Counter");
        var values = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using var handle = await _client.MonitorAsync(node.NodeId, u => values.Add(u.Value), 10, Ct);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.True(values.Distinct().Count() >= 20, $"only {values.Distinct().Count()} distinct values");
    }

}
