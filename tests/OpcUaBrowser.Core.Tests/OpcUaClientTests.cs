using Opc.Ua;
using Xunit;

namespace OpcUaBrowser.Core.Tests;

public sealed class OpcUaClientTests(OpcPlcFixture plc) : IAsyncLifetime
{
    private readonly OpcUaClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    [Fact]
    public void Connect_sets_state_connected() =>
        Assert.Equal(ConnectionState.Connected, _client.State);

    [Fact]
    public async Task Browse_objects_folder_contains_server_and_plc_nodes()
    {
        var children = await _client.BrowseAsync(ObjectIds.ObjectsFolder, Ct);

        Assert.Contains(children, c => c.NodeId == ObjectIds.Server);
        Assert.Contains(children, c => c.DisplayName == "OpcPlc");
    }

    [Fact]
    public async Task Browse_reports_has_children_for_folders_but_not_leaf_variables()
    {
        var objects = await _client.BrowseAsync(ObjectIds.ObjectsFolder, Ct);
        var plc = Assert.Single(objects, c => c.DisplayName == "OpcPlc");
        Assert.True(plc.HasChildren);

        var status = await _client.BrowseAsync(VariableIds.Server_ServerStatus, Ct);
        Assert.True(Assert.Single(status, c => c.DisplayName == "BuildInfo").HasChildren);
        Assert.False(Assert.Single(status, c => c.DisplayName == "CurrentTime").HasChildren);
    }

    [Fact]
    public async Task Collect_variables_respects_depth_and_limit()
    {
        var objects = await _client.BrowseAsync(ObjectIds.ObjectsFolder, Ct);
        var plcNode = Assert.Single(objects, c => c.DisplayName == "OpcPlc").NodeId;

        var shallow = await _client.CollectVariablesAsync(plcNode, maxDepth: 1, maxCount: 1000, Ct);
        var capped = await _client.CollectVariablesAsync(plcNode, maxDepth: 10, maxCount: 5, Ct);

        Assert.All(shallow, v => Assert.Equal(NodeClass.Variable, v.NodeClass));
        Assert.Equal(5, capped.Count);
        Assert.Equal(capped.Count, capped.Select(v => v.NodeId).Distinct().Count());
    }

    [Fact]
    public async Task Monitor_many_reports_rejected_items_without_failing_the_batch()
    {
        var results = await _client.MonitorManyAsync(
            [VariableIds.Server_ServerStatus_CurrentTime, new NodeId("does-not-exist", 3)],
            _ => { },
            250,
            Ct);

        Assert.NotNull(results[0].Handle);
        Assert.Null(results[1].Handle);
        Assert.True(ServiceResult.IsBad(results[1].Error));
        await results[0].Handle!.DisposeAsync();
    }

    [Fact]
    public async Task Portable_node_ids_use_namespace_uri_and_round_trip()
    {
        var nodeId = new NodeId("StepUp", 3);

        var portable = _client.ToPortableId(nodeId);

        Assert.StartsWith("nsu=", portable, StringComparison.Ordinal);
        Assert.Equal(nodeId, _client.ParsePortableId(portable));
        Assert.Equal("i=2258", _client.ToPortableId(VariableIds.Server_ServerStatus_CurrentTime));
    }

    [Fact]
    public async Task Read_attributes_of_server_status_variable()
    {
        var attributes = await _client.ReadAttributesAsync(VariableIds.Server_ServerStatus_CurrentTime, Ct);

        Assert.Contains(attributes, a => a.Name == "NodeClass" && a.Value == "Variable");
        Assert.Contains(attributes, a => a.Name == "DataType" && a.Value.StartsWith("UtcTime", StringComparison.Ordinal));
        Assert.DoesNotContain(attributes, a => a.Name == "EventNotifier");
    }

    [Fact]
    public async Task Server_status_structure_is_decoded()
    {
        var attributes = await _client.ReadAttributesAsync(VariableIds.Server_ServerStatus, Ct);

        var value = Assert.Single(attributes, a => a.Name == "Value").Value;
        Assert.DoesNotContain("undecoded", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitor_receives_changing_values()
    {
        var updates = new List<ValueUpdate>();
        var got3 = new TaskCompletionSource();

        await using (await _client.MonitorAsync(VariableIds.Server_ServerStatus_CurrentTime, u =>
        {
            lock (updates)
            {
                updates.Add(u);
                if (updates.Count >= 3)
                {
                    got3.TrySetResult();
                }
            }
        }, 100, Ct))
        {
            await got3.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        lock (updates)
        {
            Assert.All(updates, u => Assert.True(StatusCode.IsGood(u.Status)));
            Assert.True(updates.Select(u => u.Value).Distinct().Count() > 1);
        }
    }
}
