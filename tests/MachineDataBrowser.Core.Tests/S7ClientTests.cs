using MachineDataBrowser.Driver.S7;
using Xunit;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Core.Tests;

/// <summary>S7Client against the S7 simulator in <c>simulators/s7</c> with its tag list: browsing, reading, polling.</summary>
public sealed class S7ClientTests(S7SimulatorFixture plc) : IAsyncLifetime
{
    private readonly S7Client _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeId Symbol(string name) => new(name, S7Client.SymbolNamespace);

    private static NodeId Address(string address) => new(address, S7Client.AddressNamespace);

    private static NodeId Folder(string name) => new(name, S7Client.FolderNamespace);

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<object?> ReadAsync(NodeId nodeId) => (await _client.ReadValuesAsync([nodeId], Ct))[0];

    [Fact]
    public void Connect_sets_state_and_describes_the_plc()
    {
        Assert.Equal(ConnectionState.Connected, _client.State);
        Assert.StartsWith("S7-1500 localhost:", _client.ServerUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_lists_tags_and_memory_areas()
    {
        var root = await _client.BrowseAsync(_client.Root.NodeId, Ct);
        Assert.Equal(["Tags", "Data blocks", "Inputs", "Outputs", "Memory"], root.Select(i => i.DisplayName));
        Assert.All(root, i => Assert.Equal(NodeClass.Object, i.NodeClass));

        var blocks = await _client.BrowseAsync(Folder("DB"), Ct);
        Assert.Equal(["DB1", "DB2", "DB3"], blocks.Select(b => b.DisplayName));
    }

    [Fact]
    public async Task Tags_are_grouped_by_the_dots_in_their_names_in_file_order()
    {
        var tags = await _client.BrowseAsync(Folder("Tags"), Ct);
        Assert.Equal(["Machine", "Arrays", "DataTypes", "IO", "PauseSimulation", "Ticks", "Uptime"], tags.Select(t => t.DisplayName));

        var machine = await _client.BrowseAsync(Folder("Tags/Machine"), Ct);
        var speed = Assert.Single(machine, t => t.DisplayName == "Speed");
        Assert.Equal(Symbol("Machine.Speed"), speed.NodeId);
        Assert.Equal("REAL", speed.BrowseName);
        Assert.Equal(NodeClass.Variable, speed.NodeClass);
        Assert.False(speed.HasChildren);

        Assert.Equal(["Inputs", "Outputs"], (await _client.BrowseAsync(Folder("Tags/IO"), Ct)).Select(t => t.DisplayName));
        Assert.Equal(
            [_client.Root.NodeId, Folder("Tags"), Folder("Tags/IO"), Folder("Tags/IO/Inputs"), Symbol("Inputs.Analog")],
            await _client.GetPathFromRootAsync(Symbol("Inputs.Analog"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Every_supported_data_type_reads_with_its_value()
    {
        string[] names = ["Bool", "Byte", "Word", "Int", "DWord", "DInt", "Real", "LReal", "Char", "String", "WString", "USInt", "SInt", "UInt", "UDInt", "LInt", "ULInt", "Date", "Time", "TimeOfDay", "LWord"];
        var values = await _client.ReadValuesAsync([.. names.Select(n => Symbol("DataTypes." + n))], Ct);

        Assert.Equal<object?>(
            [
                true, (byte)0xAB, (ushort)0xBEEF, (short)-1234, 0xDEADBEEF, -123456, 3.14f, 2.718281828459045, "S", "Hello", "Grüße",
                (byte)200, (sbyte)-5, (ushort)60000, 4000000000u, -9000000000L, 18000000000UL, new DateTime(2026, 10, 21),
                TimeSpan.FromSeconds(90), new TimeSpan(0, 13, 45, 30, 250), 0x0123456789ABCDEFUL,
            ],
            values);
    }

    [Fact]
    public async Task Arrays_read_as_device_arrays()
    {
        var reals = Assert.IsType<DeviceArray>(await ReadAsync(Symbol("Arrays.Reals")));
        Assert.Equal("REAL", reals.ElementType);
        Assert.Equal(10, reals.Length);

        var bytes = Assert.IsType<DeviceArray>(await ReadAsync(Symbol("Arrays.Bytes")));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Assert.IsType<byte[]>(bytes.Values));

        var bools = Assert.IsType<bool[]>(Assert.IsType<DeviceArray>(await ReadAsync(Symbol("Arrays.Bools"))).Values);
        Assert.Equal(16, bools.Length);
    }

    [Fact]
    public async Task Typed_in_addresses_read_without_a_symbol()
    {
        Assert.Equal((short)-1234, await ReadAsync(_client.ParsePortableId("DB3.DBW4:INT")));
        Assert.Equal((ushort)0xFB2E, await ReadAsync(_client.ParsePortableId("%DB3.DBW4")));
        Assert.Equal("Hello", await ReadAsync(_client.ParsePortableId("DB3.DBB28:STRING[10]")));
        Assert.Equal(true, await ReadAsync(_client.ParsePortableId("DB1.DBX0.0")));
    }

    [Fact]
    public async Task Data_block_folders_show_bytes_with_their_bits()
    {
        var bytes = await _client.BrowseAsync(Folder("DB3"), Ct);
        Assert.Equal(64, bytes.Count);
        Assert.Equal("DBB1", bytes[1].DisplayName);
        Assert.Equal(Address("DB3.DBB1"), bytes[1].NodeId);
        Assert.True(bytes[1].HasChildren);
        Assert.Equal((byte)0xAB, await ReadAsync(bytes[1].NodeId));

        var bits = await _client.BrowseAsync(bytes[1].NodeId, Ct);
        Assert.Equal(["DBX1.0", "DBX1.1", "DBX1.2", "DBX1.3", "DBX1.4", "DBX1.5", "DBX1.6", "DBX1.7"], bits.Select(b => b.DisplayName));
        Assert.Equal<object?>([true, true, false, true, false, true, false, true], await _client.ReadValuesAsync([.. bits.Select(b => b.NodeId)], Ct));

        Assert.Equal(
            [_client.Root.NodeId, Folder("DB"), Folder("DB3"), Address("DB3.DBB1"), Address("DB3.DBX1.5")],
            await _client.GetPathFromRootAsync(Address("DB3.DBX1.5"), cancellationToken: Ct));
        Assert.Equal(
            [_client.Root.NodeId, Folder("M"), Address("MB0"), Address("M0.0")],
            await _client.GetPathFromRootAsync(Address("M0.0"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Attributes_show_symbol_address_type_and_value()
    {
        var attributes = (await _client.ReadAttributesAsync(Symbol("DataTypes.Int"), Ct)).ToDictionary(a => a.Name, a => a.Value);
        Assert.Equal("\"DataTypes.Int\"", attributes["NodeId"]);
        Assert.Equal("DB3.DBW4:INT", attributes["Address"]);
        Assert.Equal("Data block 3", attributes["Area"]);
        Assert.Equal("INT", attributes["DataType"]);
        Assert.Equal("2 bytes", attributes["Size"]);
        Assert.Equal("-1234", attributes["Value"]);
        Assert.Equal("Read, Write", attributes["AccessLevel"]);

        var comment = (await _client.ReadAttributesAsync(Symbol("Machine.Speed"), Ct)).Single(a => a.Name == "Description");
        Assert.Equal("Spindle speed (rpm)", comment.Value);
    }

    [Fact]
    public async Task Missing_data_blocks_and_unknown_symbols_read_as_null_next_to_good_values()
    {
        var values = await _client.ReadValuesAsync([Address("DB99.DBW0"), Symbol("DataTypes.Int"), Symbol("NoSuchTag"), Address("DB3.DBD126")], Ct);
        Assert.Equal<object?>([null, (short)-1234, null, null], values);

        var attributes = (await _client.ReadAttributesAsync(Address("DB99.DBW0"), Ct)).ToDictionary(a => a.Name, a => a.Value);
        Assert.StartsWith("Bad", attributes["Value"], StringComparison.Ordinal);
        await Assert.ThrowsAsync<DeviceClientException>(() => _client.ReadAttributesAsync(Symbol("NoSuchTag"), Ct));
    }

    [Fact]
    public async Task Monitoring_polls_changes_and_reports_bad_items_without_stopping_good_ones()
    {
        var updates = new System.Collections.Concurrent.ConcurrentQueue<ValueUpdate>();
        var results = await _client.MonitorManyAsync([Symbol("Machine.Counter"), Address("DB99.DBW0"), Symbol("DataTypes.String"), Symbol("NoSuchTag")], updates.Enqueue, 100, Ct);

        Assert.NotNull(results[0].Handle);
        Assert.NotNull(results[1].Handle);
        Assert.Null(results[3].Handle);
        Assert.Equal(DeviceStatusCodes.BadNodeIdUnknown, results[3].Error.StatusCode);

        await Until(() => updates.Count(u => u.NodeId == Symbol("Machine.Counter") && u.Status.IsGood) >= 3);
        Assert.Contains(updates, u => u.NodeId == Address("DB99.DBW0") && u.Status.IsBad);
        Assert.Single(updates, u => u.NodeId == Symbol("DataTypes.String")); // static: reported once
        Assert.All(updates.Where(u => u.NodeId == Symbol("Machine.Counter")), u => Assert.IsType<int>(u.Raw));

        var diagnostics = await _client.GetDiagnosticsAsync(Ct);
        Assert.Contains(diagnostics.Session, i => i.Name == "Poll 100 ms" && i.Value.StartsWith("3 values", StringComparison.Ordinal));
        Assert.Contains(diagnostics.Session, i => i.Name == "Tags" && i.Value.StartsWith("46 from tags.csv", StringComparison.Ordinal));

        await ((IDeviceClient)_client).StopMonitoringAsync(results.Select(r => r.Handle).OfType<IAsyncDisposable>(), Ct);
        var count = updates.Count;
        await Task.Delay(400, Ct);
        Assert.Equal(count, updates.Count);
    }

    [Fact]
    public async Task Without_a_tag_list_the_areas_and_listed_data_blocks_remain()
    {
        await using var bare = new S7Client();
        await bare.ConnectAsync(new ConnectOptions { EndpointUrl = plc.BareUrl }, Ct);
        Assert.Equal(["Data blocks", "Inputs", "Outputs", "Memory"], (await bare.BrowseAsync(bare.Root.NodeId, Ct)).Select(i => i.DisplayName));
        Assert.Equal(["DB1", "DB2", "DB3"], (await bare.BrowseAsync(Folder("DB"), Ct)).Select(i => i.DisplayName));
        Assert.Equal(3.14f, (await bare.ReadValuesAsync([bare.ParsePortableId("DB3.DBD14:REAL")], Ct))[0]);
        var ex = await Assert.ThrowsAsync<DeviceClientException>(() => bare.ReadAttributesAsync(bare.ParsePortableId("Machine.Speed"), Ct));
        Assert.Contains("no tag list", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_tag_file_fails_the_connection()
    {
        await using var client = new S7Client();
        var ex = await Assert.ThrowsAsync<DeviceClientException>(() => client.ConnectAsync(new ConnectOptions { EndpointUrl = $"s7://{plc.Host}/0,1?tags=/nonexistent/tags.csv" }, Ct));
        Assert.Equal(DeviceStatusCodes.BadConfigurationError, ex.StatusCode);
        Assert.Contains("/nonexistent/tags.csv", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connecting_to_a_closed_port_fails_with_a_communication_error()
    {
        await using var client = new S7Client();
        var ex = await Assert.ThrowsAsync<DeviceClientException>(() => client.ConnectAsync(new ConnectOptions { EndpointUrl = "s7://127.0.0.1:1/0,1?timeout=2000" }, Ct));
        Assert.Equal(DeviceStatusCodes.BadCommunicationError, ex.StatusCode);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    internal static async Task Until(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>The tag map option (app, --map, sessions) and which tag list wins when the URL names one too.</summary>
public sealed class S7TagMapOptionTests(S7SimulatorFixture plc)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_tag_map_option_gives_the_tags_folder()
    {
        await using var client = new S7Client();
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.BareUrl, TagMapPath = S7SimulatorFixture.TagFile }, Ct);
        Assert.Equal((S7SimulatorFixture.TagFile, 46), (client.TagList!.Value.Path, client.TagList.Value.Tags));
        Assert.Empty(client.TagList.Value.Skipped);
        Assert.Equal("Tags", (await client.BrowseAsync(client.Root.NodeId, Ct))[0].DisplayName);
        Assert.Equal(3.14f, (await client.ReadValuesAsync([client.ParsePortableId("DataTypes.Real")], Ct))[0]);
    }

    [Fact]
    public async Task The_tag_map_option_takes_precedence_over_the_url()
    {
        var other = Path.Combine(Directory.CreateTempSubdirectory("mdb-s7-tags").FullName, "two.csv");
        await File.WriteAllTextAsync(other, "Name,Address,Type\nA,MW2,Int\nB,MD4,Real\n", Ct);
        await using var client = new S7Client();
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl, TagMapPath = other }, Ct);
        Assert.Equal((other, 2), (client.TagList!.Value.Path, client.TagList.Value.Tags));
    }

    [Fact]
    public async Task An_unreadable_tag_map_fails_the_connection_with_a_configuration_error()
    {
        await using var client = new S7Client();
        var ex = await Assert.ThrowsAsync<DeviceClientException>(() => client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.BareUrl, TagMapPath = "/nonexistent.csv" }, Ct));
        Assert.Equal(DeviceStatusCodes.BadConfigurationError, ex.StatusCode);
        Assert.Contains("/nonexistent.csv", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }
}
