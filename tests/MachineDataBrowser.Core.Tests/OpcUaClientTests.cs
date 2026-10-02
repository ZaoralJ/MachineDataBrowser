using Opc.Ua;
using Xunit;
using MachineDataBrowser.Core.Ua;

namespace MachineDataBrowser.Core.Tests;

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
    public async Task Read_tree_exports_json_and_csharp()
    {
        var tree = await _client.ReadTreeAsync(VariableIds.Server_ServerStatus, "ServerStatus", NodeClass.Variable, 3, 100, Ct);

        var json = Assert.IsType<System.Text.Json.Nodes.JsonObject>(NodeExport.ToJson(tree));
        Assert.True(json.ContainsKey("CurrentTime"));
        Assert.IsType<System.Text.Json.Nodes.JsonObject>(json["BuildInfo"]);

        var code = NodeExport.ToCSharp(tree);
        Assert.Contains("public sealed class ServerStatus", code);
        Assert.Contains("public DateTime CurrentTime { get; set; }", code);
        Assert.Contains("public BuildInfo BuildInfo { get; set; } = new();", code);
        Assert.Contains("public sealed class BuildInfo", code);

        var record = NodeExport.ToCSharp(tree, asRecord: true);
        Assert.Contains("public sealed record ServerStatus", record);
        Assert.Contains("public DateTime CurrentTime { get; init; }", record);
        Assert.DoesNotContain("set;", record);
    }

    [Fact]
    public void Csharp_export_omits_types_without_properties()
    {
        var root = new NodeTree(new NodeId(1), "Device", NodeClass.Object);
        root.Children.Add(new NodeTree(new NodeId(2), "MethodSet", NodeClass.Object));
        var alarms = new NodeTree(new NodeId(3), "Alarms", NodeClass.Object);
        alarms.Children.Add(new NodeTree(new NodeId(4), "Empty", NodeClass.Object));
        root.Children.Add(alarms);
        root.Children.Add(new NodeTree(new NodeId(5), "Speed", NodeClass.Variable) { Value = 1.5f });

        var code = NodeExport.ToCSharp(root);

        Assert.Contains("public float Speed", code);
        Assert.DoesNotContain("MethodSet", code);
        Assert.DoesNotContain("Alarms", code);
        Assert.DoesNotContain("Empty", code);
    }

    [Fact]
    public void Cherry_picked_properties_are_grouped_under_their_parent_object()
    {
        var root = new NodeTree(new NodeId(1), "Objects", NodeClass.Object);
        var motor = new NodeTree(new NodeId(2), "Motor", NodeClass.Object);
        var pump = new NodeTree(new NodeId(3), "Pump", NodeClass.Object);
        var speed = new NodeTree(new NodeId(4), "Speed", NodeClass.Variable) { Value = 1.5f };
        var running = new NodeTree(new NodeId(5), "Running", NodeClass.Variable) { Value = true };
        var flow = new NodeTree(new NodeId(6), "Flow", NodeClass.Variable) { Value = 2.0 };

        var trees = NodeExport.ComposeSelection([([root, motor], speed), ([root, motor], running), ([root, pump], flow)]);

        Assert.Equal(["Motor", "Pump"], trees.Select(t => t.DisplayName));
        Assert.Equal(["Speed", "Running"], trees[0].Children.Select(c => c.DisplayName));
        var code = NodeExport.ToCSharp(trees[0]);
        Assert.Contains("public sealed class Motor", code);
        Assert.Contains("public float Speed", code);
        Assert.Contains("public bool Running", code);
    }

    [Fact]
    public void Picked_object_with_picked_descendants_keeps_only_those()
    {
        var root = new NodeTree(new NodeId(1), "Objects", NodeClass.Object);
        var motor = new NodeTree(new NodeId(2), "Motor", NodeClass.Object);
        var status = new NodeTree(new NodeId(3), "Status", NodeClass.Object);
        var speed = new NodeTree(new NodeId(4), "Speed", NodeClass.Variable) { Value = 1.5f };
        var fault = new NodeTree(new NodeId(5), "Fault", NodeClass.Variable) { Value = false };
        motor.Children.Add(new NodeTree(new NodeId(9), "Unpicked", NodeClass.Variable) { Value = 1 });

        var trees = NodeExport.ComposeSelection([([root], motor), ([root, motor], speed), ([root, motor, status], fault)]);

        var single = Assert.Single(trees);
        Assert.Equal("Motor", single.DisplayName);
        Assert.Equal(["Speed", "Status"], single.Children.Select(c => c.DisplayName));
        Assert.Equal("Fault", Assert.Single(single.Children[1].Children).DisplayName);
    }

    [Fact]
    public void Picks_in_a_nested_object_become_a_property_of_the_outer_group()
    {
        var server = new NodeTree(new NodeId(1), "Server", NodeClass.Object);
        var status = new NodeTree(new NodeId(2), "ServerStatus", NodeClass.Variable);
        var buildInfo = new NodeTree(new NodeId(3), "BuildInfo", NodeClass.Variable);
        var state = new NodeTree(new NodeId(4), "State", NodeClass.Variable) { Value = 0 };
        var manufacturer = new NodeTree(new NodeId(5), "ManufacturerName", NodeClass.Variable) { Value = "m" };
        var product = new NodeTree(new NodeId(6), "ProductName", NodeClass.Variable) { Value = "p" };

        var trees = NodeExport.ComposeSelection([([server, status], state), ([server, status, buildInfo], manufacturer), ([server, status, buildInfo], product)]);

        var single = Assert.Single(trees);
        Assert.Equal("ServerStatus", single.DisplayName);
        Assert.Equal(["State", "BuildInfo"], single.Children.Select(c => c.DisplayName));
        Assert.Equal(["ManufacturerName", "ProductName"], single.Children[1].Children.Select(c => c.DisplayName));
        var code = NodeExport.ToCSharp(single);
        Assert.Contains("public BuildInfo BuildInfo", code);
    }

    [Fact]
    public void Single_picked_object_is_exported_on_its_own()
    {
        var root = new NodeTree(new NodeId(1), "Objects", NodeClass.Object);
        var motor = new NodeTree(new NodeId(2), "Motor", NodeClass.Object);
        motor.Children.Add(new NodeTree(new NodeId(3), "Speed", NodeClass.Variable) { Value = 1.5f });

        Assert.Same(motor, Assert.Single(NodeExport.ComposeSelection([([root], motor)])));
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

    [Fact]
    public async Task Search_finds_nodes_by_name_from_the_objects_folder()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _client.SearchAsync(new BrowseItem(ObjectIds.ObjectsFolder, "Objects", "Objects", NodeClass.Object), "StepUp", maxDepth: 6, cancellationToken: Ct);
        Assert.Contains(result.Hits, h => h.Item.DisplayName == "StepUp" && h.PathText.EndsWith("OpcPlc › Telemetry › Basic › StepUp", StringComparison.Ordinal));
        TestContext.Current.TestOutputHelper!.WriteLine($"{result.NodesVisited} nodes in {watch.ElapsedMilliseconds} ms");
    }
}
