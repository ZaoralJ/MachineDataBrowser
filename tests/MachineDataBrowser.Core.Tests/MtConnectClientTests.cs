using System.Collections.Concurrent;
using System.Xml.Linq;
using MachineDataBrowser.Driver.MtConnect;
using Xunit;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Core.Tests;

/// <summary>MtConnectClient against the agent simulator in <c>simulators/mtconnect</c>: probe model, current, sample.</summary>
public sealed class MtConnectClientTests(MtConnectSimulatorFixture agent) : IAsyncLifetime
{
    private readonly MtConnectClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeId Item(string id) => new(id, MtConnectClient.DataItemNamespace);

    private static NodeId Component(string id) => new(id, MtConnectClient.ComponentNamespace);

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = agent.EndpointUrl }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<object?> ReadAsync(string id) => (await _client.ReadValuesAsync([Item(id)], Ct))[0];

    [Fact]
    public void Connect_reads_the_probe_and_reports_the_agent()
    {
        Assert.Equal(ConnectionState.Connected, _client.State);
        Assert.Contains("version 2.2", _client.ServerUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Devices_components_and_data_items_browse_as_a_tree()
    {
        var devices = await _client.BrowseAsync(_client.Root.NodeId, Ct);
        Assert.Equal(["Agent", "VMC", "Lathe"], devices.Select(d => d.DisplayName));
        Assert.All(devices, d => Assert.Equal(NodeClass.Object, d.NodeClass));

        var vmc = await _client.BrowseAsync(Component("vmc"), Ct);
        Assert.Equal(["base", "controller", "Systems", "door", "spare", "avail", "vmc_asset_chg", "operatingMode"], vmc.Select(c => c.DisplayName));
        Assert.Equal("Axes", vmc[0].BrowseName);

        var x = await _client.BrowseAsync(Component("vmc_x"), Ct);
        var position = x[0];
        Assert.Equal(("Xpos", "POSITION:ACTUAL", NodeClass.Variable, false), (position.DisplayName, position.BrowseName, position.NodeClass, position.HasChildren));
        Assert.Empty(await _client.BrowseAsync(Item("vmc_x_pos"), Ct));
    }

    [Fact]
    public async Task Path_from_root_follows_the_component_chain()
    {
        var path = await _client.GetPathFromRootAsync(Item("vmc_x_pos"), cancellationToken: Ct);

        Assert.Equal([_client.Root.NodeId, Component("vmc"), Component("vmc_axes"), Component("vmc_x"), Item("vmc_x_pos")], path);
        Assert.Empty(await _client.GetPathFromRootAsync(Item("nope"), cancellationToken: Ct));
    }

    [Fact]
    public void Portable_ids_are_data_item_ids_and_at_component_ids()
    {
        Assert.Equal("vmc_x_pos", _client.ToPortableId(Item("vmc_x_pos")));
        Assert.Equal("@vmc_axes", _client.ToPortableId(Component("vmc_axes")));
        Assert.Equal(Component("vmc_axes"), _client.ParsePortableId("@vmc_axes"));
        Assert.Equal(Item("vmc_x_pos"), _client.ParsePortableId("vmc_x_pos"));
        Assert.Equal(_client.Root.NodeId, _client.ParsePortableId(_client.ToPortableId(_client.Root.NodeId)));
    }

    [Fact]
    public async Task Attributes_describe_the_data_item()
    {
        var attributes = (await _client.ReadAttributesAsync(Item("vmc_x_pos"), Ct)).ToDictionary(a => a.Name, a => a.Value);

        Assert.Equal("SAMPLE", attributes["Category"]);
        Assert.Equal("POSITION", attributes["Type"]);
        Assert.Equal("ACTUAL", attributes["SubType"]);
        Assert.Equal("MILLIMETER", attributes["Units"]);
        Assert.Equal("Double", attributes["DataType"]);
        Assert.Equal("Read", attributes["AccessLevel"]);
        Assert.Equal("VMC", attributes["Device"]);
        Assert.True(double.TryParse(attributes["Value"], System.Globalization.CultureInfo.CurrentCulture, out _), attributes["Value"]);

        var component = (await _client.ReadAttributesAsync(Component("vmc"), Ct)).ToDictionary(a => a.Name, a => a.Value);
        Assert.Equal("Device", component["Component"]);
        Assert.Equal("mdb-sim-vmc", component["Uuid"]);
    }

    [Fact]
    public async Task Writes_are_rejected_by_the_mtconnect_driver()
    {
        Assert.True(_client.IsReadOnly);
        var error = await Assert.ThrowsAsync<DeviceClientException>(() => _client.WriteValueAsync(Item("vmc_x_pos"), "1", Ct));

        Assert.Equal(DeviceStatusCodes.BadNotWritable, error.StatusCode);
    }

    [Fact]
    public async Task Values_decode_by_category_and_representation()
    {
        Assert.IsType<double>(await ReadAsync("vmc_x_pos"));
        var vibration = Assert.IsType<DeviceArray>(await ReadAsync("vmc_s_vib"));
        Assert.Equal(10, vibration.Length);
        Assert.Equal(3, Assert.IsType<DeviceArray>(await ReadAsync("vmc_path_pos")).Length);
        Assert.IsType<long>(await ReadAsync("vmc_part_count"));
        Assert.Equal("AUTOMATIC", await ReadAsync("vmc_ctrl_mode"));
        Assert.Contains("G55=120.5,-40,0", Assert.IsType<string>(await ReadAsync("vmc_offsets")), StringComparison.Ordinal);
        Assert.Matches("^(Normal|Fault: Spindle over temperature \\[SP-OT\\])$", Assert.IsType<string>(await ReadAsync("vmc_s_cond")));

        // UNAVAILABLE, unknown ids and components have no value.
        var values = await _client.ReadValuesAsync([Item("vmc_spare"), Item("nope"), Component("vmc")], Ct);
        Assert.All(values, Assert.Null);
    }

    [Fact]
    public async Task Units_are_shown_in_their_usual_symbols()
    {
        var units = await _client.ReadUnitsAsync([Item("vmc_x_pos"), Item("vmc_coolant_temp"), Item("vmc_s_speed"), Item("vmc_exec")], Ct);

        Assert.Equal(["mm", "°C", "rpm", null], units);
    }

    [Fact]
    public async Task Monitoring_delivers_every_buffered_observation_in_order()
    {
        var updates = new ConcurrentQueue<ValueUpdate>();
        await using var handle = await _client.MonitorAsync(Item("vmc_x_pos"), updates.Enqueue, 500, Ct);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        // The simulator moves X every 100 ms: a 500 ms refresh still gets every position.
        var list = updates.ToList();
        Assert.True(list.Count >= 12, $"only {list.Count} updates");
        Assert.All(list, u => Assert.True(u.Status.IsGood));
        Assert.Equal(list.Select(u => u.SourceTimestamp).Order(), list.Select(u => u.SourceTimestamp));
        Assert.All(list, u => Assert.NotNull(u.Numeric));
    }

    [Fact]
    public async Task Monitoring_reports_unavailable_values_and_rejects_components()
    {
        var updates = new ConcurrentQueue<ValueUpdate>();
        var results = await _client.MonitorManyAsync([Item("vmc_spare"), Component("vmc_axes"), Item("nope")], updates.Enqueue, 250, Ct);
        try
        {
            Assert.NotNull(results[0].Handle);
            Assert.Equal(DeviceStatusCodes.BadAttributeIdInvalid, results[1].Error.StatusCode);
            Assert.Equal(DeviceStatusCodes.BadNodeIdUnknown, results[2].Error.StatusCode);
            var update = Assert.Single(updates);
            Assert.Equal(DeviceStatusCodes.BadNoData, update.Status);
            Assert.Equal("UNAVAILABLE", update.Value);
        }
        finally
        {
            await ((IDeviceClient)_client).StopMonitoringAsync(results.Select(r => r.Handle).OfType<IAsyncDisposable>(), Ct);
        }
    }

    [Fact]
    public async Task Stopped_monitors_get_no_more_updates()
    {
        var count = 0;
        var handle = await _client.MonitorAsync(Item("vmc_x_pos"), _ => Interlocked.Increment(ref count), 100, Ct);
        await Task.Delay(500, Ct);
        await handle.DisposeAsync();
        var stopped = Volatile.Read(ref count);

        await Task.Delay(500, Ct);

        Assert.True(stopped > 0);
        Assert.Equal(stopped, Volatile.Read(ref count));
    }

    [Fact]
    public async Task History_reads_what_the_agent_still_buffers()
    {
        await Task.Delay(1500, Ct);
        var end = DateTime.UtcNow;

        var history = await _client.ReadHistoryAsync(Item("vmc_x_pos"), end.AddSeconds(-1), end, 1000, Ct);

        Assert.False(history.Truncated);
        Assert.InRange(history.Values.Count, 5, 15);
        Assert.All(history.Values, v => Assert.InRange(v.SourceTimestamp, end.AddSeconds(-1), end));

        var limited = await _client.ReadHistoryAsync(Item("vmc_x_pos"), end.AddSeconds(-1), end, 3, Ct);
        Assert.True(limited.Truncated);
        Assert.Equal(3, limited.Values.Count);
    }

    [Fact]
    public async Task Diagnostics_show_the_agent_buffer_and_polling()
    {
        await using var handle = await _client.MonitorAsync(Item("vmc_x_pos"), _ => { }, 100, Ct);
        await Task.Delay(500, Ct);

        var rows = (await _client.GetDiagnosticsAsync(Ct)).Session.ToDictionary(r => r.Name, r => r.Value);

        Assert.Equal("0", rows["Link lost"]);
        Assert.StartsWith("3 · ", rows["Devices"], StringComparison.Ordinal);
        Assert.StartsWith("1 data items · ", rows["Poll 100 ms"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_path_shows_only_that_device()
    {
        await using var client = new MtConnectClient();
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = agent.HttpUrl + "/Lathe" }, Ct);

        Assert.Equal(["Lathe"], (await client.BrowseAsync(client.Root.NodeId, Ct)).Select(d => d.DisplayName));
        Assert.IsType<double>((await client.ReadValuesAsync([Item("lathe_x_pos")], Ct))[0]);
    }

    [Fact]
    public async Task Unknown_device_and_unreachable_agent_fail_to_connect()
    {
        await using var client = new MtConnectClient();
        var noDevice = await Assert.ThrowsAsync<DeviceClientException>(() =>
            client.ConnectAsync(new ConnectOptions { EndpointUrl = agent.HttpUrl + "/Nope" }, Ct));
        Assert.Equal(DeviceStatusCodes.BadNodeIdUnknown, noDevice.StatusCode);

        var unreachable = await Assert.ThrowsAsync<DeviceClientException>(() =>
            client.ConnectAsync(new ConnectOptions { EndpointUrl = "mtconnect://127.0.0.1:9" }, Ct));
        Assert.Equal(DeviceStatusCodes.BadCommunicationError, unreachable.StatusCode);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }
}

/// <summary>Endpoint parsing and XML decoding, without an agent.</summary>
public sealed class MtConnectModelTests
{
    [Theory]
    [InlineData("mtconnect://agent", "http://agent:5000/")]
    [InlineData("mtconnect://agent:5001/VMC", "http://agent:5001/VMC/")]
    [InlineData("http://agent:5000/VMC/current", "http://agent:5000/VMC/")]
    [InlineData("https://agent/mtc/", "https://agent/mtc/")]
    [InlineData("http://agent:5000/probe?x=1", "http://agent:5000/")]
    public void Endpoints_become_the_agent_base_url(string url, string expected) =>
        Assert.Equal(expected, MtConnectClient.ParseEndpoint(url).ToString());

    [Theory]
    [InlineData("opc.tcp://agent:4840")]
    [InlineData("mtconnect://")]
    public void Other_urls_are_rejected(string url) =>
        Assert.Throws<FormatException>(() => MtConnectClient.ParseEndpoint(url));

    [Fact]
    public void Siblings_with_the_same_name_are_told_apart_by_id()
    {
        var model = MtcModel.Parse(XDocument.Parse("""
            <MTConnectDevices xmlns="urn:mtconnect.org:MTConnectDevices:1.3">
              <Header instanceId="1" bufferSize="10"/>
              <Devices>
                <Device id="d" name="M">
                  <Components>
                    <Linear id="a"/><Linear id="b"/><Rotary id="c" name="C"/>
                  </Components>
                </Device>
              </Devices>
            </MTConnectDevices>
            """));

        Assert.Equal(["Linear (a)", "Linear (b)", "C"], model.Devices[0].Components.Select(c => c.DisplayName));
    }

    [Fact]
    public void Conditions_carry_level_text_and_native_code()
    {
        var item = new MtcDataItem("t", null, "CONDITION", "TEMPERATURE", null, null, null, null, null, new MtcComponent("c", "Linear", null, null, null, null));
        var fault = XElement.Parse("""<Fault dataItemId="t" sequence="7" timestamp="2026-01-01T00:00:00Z" nativeCode="OT">Over temperature</Fault>""");
        var unavailable = XElement.Parse("""<Unavailable dataItemId="t" sequence="8" timestamp="2026-01-01T00:00:00Z"/>""");

        var decoded = MtcXml.Decode(fault, item, isCondition: true);
        Assert.Equal("Fault: Over temperature [OT]", decoded.Value);
        Assert.Equal(7, decoded.Sequence);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), decoded.Timestamp);
        Assert.Equal(DeviceStatusCodes.BadNoData, MtcXml.Decode(unavailable, item, isCondition: true).Status);
    }

    [Fact]
    public void Agent_errors_become_device_errors()
    {
        var error = XDocument.Parse("""
            <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:2.0">
              <Header instanceId="1"/>
              <Errors><Error errorCode="OUT_OF_RANGE">'from' must be between 5 and 9</Error></Errors>
            </MTConnectError>
            """);

        var ex = Assert.Throws<DeviceClientException>(() => MtcXml.ParseStreams(error, new Dictionary<string, MtcDataItem>()));
        Assert.Equal(DeviceStatusCodes.BadOutOfRange, ex.StatusCode);
        Assert.Contains("'from' must be between", ex.Message, StringComparison.Ordinal);
    }
}
