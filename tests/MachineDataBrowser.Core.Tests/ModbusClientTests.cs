using System.Collections.Concurrent;
using MachineDataBrowser.Driver.Modbus;
using Xunit;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Core.Tests;

/// <summary>ModbusClient against the simulator in <c>simulators/modbus</c>: generated tree, typed reads, writes, polling.</summary>
public sealed class ModbusClientTests(ModbusSimulatorFixture device) : IAsyncLifetime
{
    private readonly ModbusClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeId Value(string address) => new(address, ModbusClient.TagNamespace);

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = device.EndpointUrl }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<object?> ReadAsync(string address) => (await _client.ReadValuesAsync([Value(address)], Ct))[0];

    [Fact]
    public void Connect_sets_state_connected()
    {
        Assert.Equal(ConnectionState.Connected, _client.State);
        Assert.StartsWith("Modbus localhost:", _client.ServerUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tree_has_areas_blocks_and_single_values()
    {
        var areas = await _client.BrowseAsync(_client.Root.NodeId, Ct);
        Assert.Equal(["Coils", "Discrete Inputs", "Input Registers", "Holding Registers"], areas.Select(a => a.DisplayName));

        var blocks = await _client.BrowseAsync(areas[3].NodeId, Ct);
        Assert.Equal(656, blocks.Count);
        Assert.Equal("0-99", blocks[0].DisplayName);
        Assert.Equal("65500-65535", blocks[^1].DisplayName);
        Assert.Equal(36, (await _client.BrowseAsync(blocks[^1].NodeId, Ct)).Count);

        var values = await _client.BrowseAsync(blocks[0].NodeId, Ct);
        Assert.Equal(100, values.Count);
        Assert.Equal("hr:10", _client.ToPortableId(values[10].NodeId));
        Assert.Equal(NodeClass.Variable, values[10].NodeClass);
        Assert.False(values[10].HasChildren);

        var path = await _client.GetPathFromRootAsync(Value("hr:150:float32"), cancellationToken: Ct);
        Assert.Equal(["@Root", "@hr", "@hr/100", "hr:150:float32"], path.Select(_client.ToPortableId));
    }

    [Fact]
    public async Task Reads_typed_values()
    {
        var values = await _client.ReadValuesAsync(
            [.. new[] { "hr:10:int16", "hr:11", "hr:20:float32", "hr:22:float32:swap", "hr:30:uint32", "hr:32:int32", "hr:40:float64",
                "hr:44:int64", "hr:50:string16", "ir:10", "ir:20:float32", "co:1", "co:2", "di:3", "di:4" }.Select(Value)],
            Ct);

        Assert.Equal<object?>(
            [(short)-1234, (ushort)54321, 3.5f, 3.5f, 123456789u, -100000, 2.718281828459045, -9007199254740993L, "MDB-SIM", (ushort)4242, -12.25f, true, false, true, false],
            values);
    }

    [Fact]
    public async Task Missing_addresses_read_as_null_without_spoiling_neighbours()
    {
        var values = await _client.ReadValuesAsync([Value("hr:998"), Value("hr:999"), Value("hr:1000"), Value("nonsense")], Ct);
        Assert.NotNull(values[0]);
        Assert.NotNull(values[1]);
        Assert.Null(values[2]);
        Assert.Null(values[3]);
    }

    [Fact]
    public async Task Attributes_describe_the_address()
    {
        var attributes = (await _client.ReadAttributesAsync(Value("hr:20:float32"), Ct)).ToDictionary(a => a.Name, a => a.Value);
        Assert.Equal("Holding Registers", attributes["Area"]);
        Assert.Equal("20 (0-based) · 400021", attributes["Address"]);
        Assert.Equal("float32", attributes["DataType"]);
        Assert.Equal("2", attributes["Registers"]);
        Assert.Equal("Read, Write", attributes["AccessLevel"]);

        var input = (await _client.ReadAttributesAsync(Value("di:1"), Ct)).ToDictionary(a => a.Name, a => a.Value);
        Assert.Equal("Read", input["AccessLevel"]);
    }

    [Fact]
    public async Task Writes_registers_and_coils()
    {
        await _client.WriteValueAsync(Value("hr:300:float32:swap"), "-1.5", Ct);
        await _client.WriteValueAsync(Value("hr:302"), "65535", Ct);
        await _client.WriteValueAsync(Value("hr:310:string10"), "\"hello\"", Ct);
        await _client.WriteValueAsync(Value("co:40"), "true", Ct);

        Assert.Equal(-1.5f, await ReadAsync("hr:300:float32:swap"));
        Assert.Equal((ushort)65535, await ReadAsync("hr:302"));
        Assert.Equal("hello", await ReadAsync("hr:310:string10"));
        Assert.Equal(true, await ReadAsync("co:40"));

        await _client.WriteValueAsync(Value("co:40"), "false", Ct);
        Assert.Equal(false, await ReadAsync("co:40"));
    }

    [Fact]
    public async Task Read_only_areas_and_missing_addresses_reject_writes()
    {
        var readOnly = await Assert.ThrowsAsync<DeviceClientException>(() => _client.WriteValueAsync(Value("ir:0"), "1", Ct));
        Assert.Equal(DeviceStatusCodes.BadNotWritable, readOnly.StatusCode);

        var missing = await Assert.ThrowsAsync<DeviceClientException>(() => _client.WriteValueAsync(Value("hr:5000"), "1", Ct));
        Assert.Equal(DeviceStatusCodes.BadNodeIdUnknown, missing.StatusCode);
    }

    [Fact]
    public async Task Monitoring_reports_changes_and_bad_addresses()
    {
        var updates = new ConcurrentQueue<ValueUpdate>();
        var results = await _client.MonitorManyAsync([Value("hr:0"), Value("hr:20:float32"), Value("hr:2000"), Value("bogus")], updates.Enqueue, 100, Ct);

        Assert.Equal(3, results.Count(r => r.Handle is not null));
        Assert.Equal(DeviceStatusCodes.BadNodeIdUnknown, results[3].Error.StatusCode);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && updates.Count(u => u.NodeId == Value("hr:0")) < 3)
            {
                await Task.Delay(50, Ct);
            }

            Assert.True(updates.Count(u => u.NodeId == Value("hr:0")) >= 3, "the counter should change");
            Assert.Single(updates, u => u.NodeId == Value("hr:20:float32") && u.Value == "3.5");
            Assert.Contains(updates, u => u.NodeId == Value("hr:2000") && u.Status == DeviceStatusCodes.BadNodeIdUnknown);

            var diagnostics = await _client.GetDiagnosticsAsync(Ct);
            Assert.Contains(diagnostics.Session, i => i.Name == "Poll 100 ms");
        }
        finally
        {
            await ((IDeviceClient)_client).StopMonitoringAsync(results.Select(r => r.Handle).OfType<IAsyncDisposable>(), Ct);
        }
    }

    [Fact]
    public async Task Simulator_map_and_session_match_the_simulator()
    {
        var folder = Path.Combine(DotNet.Testcontainers.Builders.CommonDirectoryPath.GetGitDirectory().DirectoryPath, "simulators", "modbus");
        var map = await ModbusTagMap.LoadAsync(Path.Combine(folder, "simulator-map.csv"), Ct);
        var values = await _client.ReadValuesAsync([.. map.Tags.Select(t => Value(t.Address.ToString()))], Ct);
        Assert.All(map.Tags.Zip(values), pair => Assert.True(pair.Second is not null, $"{pair.First.Path} ({pair.First.Address}) has no value"));
        Assert.Equal("MDB-SIM", values[map.Tags.ToList().FindIndex(t => t.Path == "Static/Name")]);

        var session = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "simulator.mdbsession"), Ct))!;
        Assert.Equal("simulator-map.csv", (string?)session["tagMap"]);
        Assert.All(session["watch"]!.AsArray(), w => Assert.Contains(map.Tags, t => t.Address.ToString() == (string?)w!["nodeId"]));
    }

    [Fact]
    public async Task Invalid_endpoint_is_reported()
    {
        await using var client = new ModbusClient();
        var ex = await Assert.ThrowsAsync<DeviceClientException>(() => client.ConnectAsync(new ConnectOptions { EndpointUrl = "modbus://localhost/300" }, Ct));
        Assert.Equal(DeviceStatusCodes.BadTcpEndpointUrlInvalid, ex.StatusCode);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }
}

/// <summary>ModbusClient with a register map: named tree under Tags, attributes and units from the map.</summary>
public sealed class ModbusTagMapClientTests(ModbusSimulatorFixture device) : IAsyncLifetime
{
    private readonly ModbusClient _client = new();
    private readonly string _map = Path.Combine(Path.GetTempPath(), $"mdb-map-{Guid.NewGuid():N}.csv");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await File.WriteAllTextAsync(_map, """
            name,address,unit,description
            Line1/Motor/Speed,hr:20:float32,rpm,Motor speed
            Line1/Name,hr:50:string16
            Counter,hr:0
            """, Ct);
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = device.EndpointUrl, TagMapPath = _map }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        File.Delete(_map);
    }

    [Fact]
    public async Task Tags_folder_shows_the_map_with_addresses_as_ids()
    {
        var root = await _client.BrowseAsync(_client.Root.NodeId, Ct);
        Assert.Equal(["Tags", "Coils", "Discrete Inputs", "Input Registers", "Holding Registers"], root.Select(r => r.DisplayName));

        var tags = await _client.BrowseAsync(root[0].NodeId, Ct);
        Assert.Equal(["Line1", "Counter"], tags.Select(t => t.DisplayName));
        var line = await _client.BrowseAsync(tags[0].NodeId, Ct);
        var motor = Assert.Single(await _client.BrowseAsync(line[0].NodeId, Ct));
        Assert.Equal("Speed", motor.DisplayName);
        Assert.Equal("hr:20:float32", _client.ToPortableId(motor.NodeId));
        Assert.Equal(3.5f, (await _client.ReadValuesAsync([motor.NodeId], Ct))[0]);

        var path = await _client.GetPathFromRootAsync(motor.NodeId, cancellationToken: Ct);
        Assert.Equal(["@Root", "@Tags", "@Tags/Line1", "@Tags/Line1/Motor", "hr:20:float32"], path.Select(_client.ToPortableId));
        Assert.Equal(new DeviceNodeId("Tags/Line1", ModbusClient.MapNamespace), _client.ParsePortableId("@Tags/Line1"));
    }

    [Fact]
    public async Task Attributes_and_units_come_from_the_map()
    {
        var speed = new DeviceNodeId("hr:20:float32", ModbusClient.TagNamespace);
        var attributes = (await _client.ReadAttributesAsync(speed, Ct)).ToDictionary(a => a.Name, a => a.Value);
        Assert.Equal("Line1/Motor/Speed", attributes["Name"]);
        Assert.Equal("Motor speed", attributes["Description"]);
        Assert.Equal("rpm", attributes["EngineeringUnits"]);

        var units = await ((IEngineeringUnitsSource)_client).ReadUnitsAsync([speed, new DeviceNodeId("hr:0", ModbusClient.TagNamespace)], Ct);
        Assert.Equal(["rpm", null], units);
    }

    [Fact]
    public async Task A_bad_map_fails_the_connect()
    {
        var bad = Path.Combine(Path.GetTempPath(), $"mdb-bad-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(bad, "Speed,hr:nope", Ct);
        try
        {
            await using var client = new ModbusClient();
            var ex = await Assert.ThrowsAsync<DeviceClientException>(() => client.ConnectAsync(new ConnectOptions { EndpointUrl = device.EndpointUrl, TagMapPath = bad }, Ct));
            Assert.Equal(DeviceStatusCodes.BadConfigurationError, ex.StatusCode);
            Assert.Contains("line 1", ex.Message, StringComparison.Ordinal);
            Assert.Equal(ConnectionState.Disconnected, client.State);
        }
        finally
        {
            File.Delete(bad);
        }
    }
}
