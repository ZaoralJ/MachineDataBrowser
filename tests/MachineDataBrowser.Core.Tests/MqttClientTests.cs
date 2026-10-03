using System.Collections.Concurrent;
using Opc.Ua;
using MachineDataBrowser.Core.Mqtt;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

/// <summary>MqttDeviceClient against the broker and publisher in <c>simulators/mqtt</c>.</summary>
public sealed class MqttClientTests(MqttSimulatorFixture broker) : IAsyncLifetime
{
    private readonly MqttDeviceClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeId Id(string id) => new(id, 1);

    public async ValueTask InitializeAsync()
    {
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = broker.EndpointUrl }, Ct);
        // Retained topics arrive at once; periodic ones (JSON every 200 ms, Edge2 data every second) shortly after.
        await Until(async () => (await _client.BrowseAsync(Id("topics"), Ct)).Any(t => t.DisplayName == "machines")
            && (await _client.BrowseAsync(Id("g:Plant1"), Ct)).Count == 2
            && await ReadAsync("t:raw/blob") is not null);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<object?> ReadAsync(string id) => (await _client.ReadValuesAsync([Id(id)], Ct))[0];

    [Fact]
    public void Connect_sets_state_connected() => Assert.Equal(ConnectionState.Connected, _client.State);

    [Fact]
    public async Task Topic_tree_shows_levels_payload_topics_and_hides_sparkplug_raw_topics()
    {
        var top = (await _client.BrowseAsync(Id("topics"), Ct)).Select(t => t.DisplayName).ToList();
        Assert.Contains("plant", top);
        Assert.Contains("machines", top);
        Assert.DoesNotContain("spBv1.0", top);

        // plant/hall1 has its own (retained) payload and subtopics: a variable with children.
        var plant = await _client.BrowseAsync(Id("t:plant"), Ct);
        var hall = Assert.Single(plant, p => p.DisplayName == "hall1");
        Assert.Equal(NodeClass.Variable, hall.NodeClass);
        Assert.True(hall.HasChildren);
        Assert.Equal("Hall 1", await ReadAsync("t:plant/hall1"));
    }

    [Fact]
    public async Task Payloads_decode_as_number_boolean_text_and_bytes()
    {
        await Until(async () => await ReadAsync("t:plant/hall1/press1/running") is bool && await ReadAsync("t:raw/blob") is byte[]);
        Assert.IsType<double>(await ReadAsync("t:plant/hall1/press1/temperature"));
        Assert.IsType<bool>(await ReadAsync("t:plant/hall1/press1/running"));
        Assert.Equal("1.2.3", await ReadAsync("t:plant/info/version") is double v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : await ReadAsync("t:plant/info/version"));
        Assert.Equal("Billund Moulding", await ReadAsync("t:plant/info/site"));
        Assert.IsType<byte[]>(await ReadAsync("t:raw/blob"));
    }

    [Fact]
    public async Task Json_payload_fields_are_browsable_and_readable()
    {
        await Until(async () => await ReadAsync("t:machines/m1/status") is string);
        var fields = (await _client.BrowseAsync(Id("t:machines/m1/status"), Ct)).Select(f => f.DisplayName).ToList();
        Assert.Equal(["state", "speed", "temperature", "alarms", "ok", "operator"], fields);

        Assert.IsType<double>(await ReadAsync("t:machines/m1/status#/temperature/bearing"));
        Assert.IsType<string>(await ReadAsync("t:machines/m1/status#/state"));
        Assert.IsType<bool>(await ReadAsync("t:machines/m1/status#/ok"));
        Assert.Null(await ReadAsync("t:machines/m1/status#/operator"));
        var limits = await _client.BrowseAsync(Id("t:config/line1#/limits"), Ct);
        Assert.Equal(["min", "max"], limits.Select(l => l.DisplayName));
        Assert.Equal(90d, await ReadAsync("t:config/line1#/limits/max"));

        var attributes = await _client.ReadAttributesAsync(Id("t:config/line1"), Ct);
        Assert.Contains(attributes, a => a.Name == "Retained" && a.Value == "Yes");
        Assert.Contains(attributes, a => a.Name == "PayloadFormat" && a.Value == "JSON");
    }

    [Fact]
    public async Task Sparkplug_b_groups_nodes_devices_and_metrics_decode_with_aliases()
    {
        // Births were published before we connected; the next (periodic) birth names the metrics.
        await Until(async () => (await _client.BrowseAsync(Id("e:Plant1|Edge1"), Ct)).Select(i => i.DisplayName).ToHashSet() is var e && e.Contains("bdSeq") && e.Contains("Press2")
            && (await _client.BrowseAsync(Id("d:Plant1|Edge1|Press1"), Ct)).Any(i => i.DisplayName == "Temperature"));
        Assert.Equal(["Plant1"], (await _client.BrowseAsync(Id("spb"), Ct)).Select(g => g.DisplayName));
        Assert.Equal(["Edge1", "Edge2"], (await _client.BrowseAsync(Id("g:Plant1"), Ct)).Select(e => e.DisplayName));

        var edge = (await _client.BrowseAsync(Id("e:Plant1|Edge1"), Ct)).Select(i => i.DisplayName).ToList();
        Assert.Contains("bdSeq", edge);
        Assert.Contains("Press1", edge);
        Assert.Contains("Press2", edge);

        var press = await _client.BrowseAsync(Id("d:Plant1|Edge1|Press1"), Ct);
        Assert.Contains(press, m => m.DisplayName == "Motor" && m.NodeClass == NodeClass.Object); // "Motor/Speed" -> folder
        Assert.Equal(["Current", "Speed"], (await _client.BrowseAsync(Id("f:Plant1|Edge1|Press1|Motor"), Ct)).Select(m => m.DisplayName));

        // DDATA carries aliases only; values must still land on the named metrics.
        var first = await ReadAsync("m:Plant1|Edge1|Press1|Temperature");
        await Until(async () => !Equals(await ReadAsync("m:Plant1|Edge1|Press1|Temperature"), first));
        Assert.IsType<double>(await ReadAsync("m:Plant1|Edge1|Press1|Temperature"));
        Assert.IsType<long>(await ReadAsync("m:Plant1|Edge1|Press1|Count"));
        Assert.Equal((sbyte)-12, await ReadAsync("m:Plant1|Edge1|Press1|Types/Int8"));
        Assert.Equal((short)-1234, await ReadAsync("m:Plant1|Edge1|Press1|Types/Int16"));
        Assert.Equal(4000000000u, await ReadAsync("m:Plant1|Edge1|Press1|Types/UInt32"));
        Assert.IsType<DateTime>(await ReadAsync("m:Plant1|Edge1|Press1|Types/DateTime"));
        Assert.Equal("BRICK_2x4", await ReadAsync("m:Plant1|Edge1|Press1|Config/Recipe"));

        var attributes = await _client.ReadAttributesAsync(Id("m:Plant1|Edge1|Press1|Temperature"), Ct);
        Assert.Contains(attributes, a => a.Name == "DataType" && a.Value == "Double");
        Assert.Contains(attributes, a => a.Name == "Alias" && a.Value == "101");
    }

    [Fact]
    public async Task Cloud_events_in_structured_and_binary_mode_are_recognised()
    {
        await Until(async () => await ReadAsync("t:events/press1/structured") is not null && await ReadAsync("t:events/press1/binary") is not null);

        var structured = await _client.ReadAttributesAsync(Id("t:events/press1/structured"), Ct);
        Assert.Contains(structured, a => a.Name == "PayloadFormat" && a.Value == "CloudEvent (structured) · com.example.press.cycle.completed");
        Assert.Contains(structured, a => a.Name == "ContentType" && a.Value == "application/cloudevents+json");
        Assert.IsType<double>(await ReadAsync("t:events/press1/structured#/data/cycle"));

        var binary = await _client.ReadAttributesAsync(Id("t:events/press1/binary"), Ct);
        Assert.Contains(binary, a => a.Name == "PayloadFormat" && a.Value == "CloudEvent (binary) · com.example.press.cycle.completed");
        Assert.Contains(binary, a => a.Name == "UserProperty source" && a.Value == "urn:plant:hall1:press1");
        Assert.Equal(["cycle", "durationMs", "ok"], (await _client.BrowseAsync(Id("t:events/press1/binary"), Ct)).Select(f => f.DisplayName));
    }

    [Fact]
    public async Task Unified_namespace_follows_isa95_levels_with_retained_metadata()
    {
        Assert.Equal(["billund"], (await _client.BrowseAsync(Id("t:acme"), Ct)).Select(b => b.DisplayName));
        Assert.Equal(["_meta", "line1", "line2", "line3", "line4"], (await _client.BrowseAsync(Id("t:acme/billund/moulding"), Ct)).Select(b => b.DisplayName));
        var line = (await _client.BrowseAsync(Id("t:acme/billund/moulding/line2"), Ct)).Select(b => b.DisplayName).ToList();
        Assert.Contains("press", line);
        Assert.Contains("order", line);
        Assert.Contains("shift", line);

        Assert.Equal("KUKA", await ReadAsync("t:acme/billund/moulding/line2/robot/_meta#/vendor")); // retained
        await Until(async () => await ReadAsync("t:acme/billund/moulding/line2/press/state") is string); // retained
        await Until(async () => await ReadAsync("t:acme/billund/moulding/line2/press/process/temperature") is double);
        await Until(async () => await ReadAsync("t:acme/billund/moulding/line2/press/kpi/oee#/oee") is double, seconds: 10);
    }

    [Fact]
    public async Task Raw_binary_payloads_stay_bytes()
    {
        await Until(async () => await ReadAsync("t:raw/int32") is not null);
        Assert.Equal(4, Assert.IsType<byte[]>(await ReadAsync("t:raw/int32")).Length);
        Assert.Equal(8, Assert.IsType<byte[]>(await ReadAsync("t:raw/float64")).Length);
    }

    [Fact]
    public async Task Diagnostics_show_broker_traffic_topics_and_sparkplug_nodes()
    {
        await using var monitor = await _client.MonitorAsync(Id("t:fast/10ms/counter"), _ => { }, 250, Ct);

        var rows = (await _client.GetDiagnosticsAsync(Ct)).Session.ToDictionary(r => r.Name, r => r.Value);

        Assert.Equal("MQTT 5.0", rows["Protocol"]);
        Assert.StartsWith("TCP · no TLS", rows["Transport"], StringComparison.Ordinal);
        Assert.Equal("#", rows["Topic filter"]);
        Assert.Equal("0", rows["Reconnects"]);
        Assert.Matches(@"received · \d", rows["Messages"]);
        Assert.Contains("of max 20000", rows["Topics"], StringComparison.Ordinal);
        Assert.Matches(@"^\d+/\d+ edge nodes online", rows["Sparkplug B"]);
        Assert.Equal("1", rows["Monitored items"]);
        Assert.Contains("max QoS", rows["Broker limits"], StringComparison.Ordinal);

        // Two samples a moment apart give a real message rate from the simulator's 10 ms topic.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        rows = (await _client.GetDiagnosticsAsync(Ct)).Session.ToDictionary(r => r.Name, r => r.Value);
        Assert.DoesNotMatch(@"^0 / s", rows["Messages"]);
    }

    [Fact]
    public async Task Paused_discovery_receives_only_monitored_topics_until_resumed()
    {
        var json = new ConcurrentQueue<ValueUpdate>();
        var metric = new ConcurrentQueue<ValueUpdate>();
        await using var jsonMonitor = await _client.MonitorAsync(Id("t:machines/m1/status#/speed"), json.Enqueue, 0, Ct);
        await using var metricMonitor = await _client.MonitorAsync(Id("m:Plant1|Edge1|Press1|Temperature"), metric.Enqueue, 0, Ct);

        await _client.SetDiscoveryPausedAsync(true, Ct);
        Assert.True(_client.IsDiscoveryPaused);
        var counter = await ReadAsync("t:fast/10ms/counter");
        var (jsonBefore, metricBefore) = (json.Count, metric.Count);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        // The 10 ms topic isn't monitored: it stands still. JSON (200 ms) and Sparkplug (50 ms) keep flowing.
        Assert.Equal(counter, await ReadAsync("t:fast/10ms/counter"));
        Assert.True(json.Count > jsonBefore, "monitored JSON field stopped");
        Assert.True(metric.Count > metricBefore, "monitored Sparkplug metric stopped");

        await _client.SetDiscoveryPausedAsync(false, Ct);
        await Until(async () => !Equals(counter, await ReadAsync("t:fast/10ms/counter")));
    }

    [Fact]
    public async Task Discovery_pauses_automatically_after_the_configured_time()
    {
        await using var client = new MqttDeviceClient();
        var changed = 0;
        client.DiscoveryPausedChanged += (_, _) => Interlocked.Increment(ref changed);
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = broker.EndpointUrl, AutoPauseDiscoverySeconds = 2 }, Ct);
        var monitored = new ConcurrentQueue<ValueUpdate>();
        await using var monitor = await client.MonitorAsync(Id("t:machines/m1/status#/speed"), monitored.Enqueue, 0, Ct);

        Assert.False(client.IsDiscoveryPaused);
        Assert.Matches(@"^on · pauses in [12] s$", Row(await client.GetDiagnosticsAsync(Ct), "Discovery"));
        var started = DateTime.UtcNow;
        await Until(() => Task.FromResult(Volatile.Read(ref changed) == 1));
        Assert.True(client.IsDiscoveryPaused);
        Assert.True(DateTime.UtcNow - started > TimeSpan.FromSeconds(1), "paused too early");
        Assert.StartsWith("paused · 1 topic subscriptions", Row(await client.GetDiagnosticsAsync(Ct), "Discovery"), StringComparison.Ordinal);

        var counter = (await client.ReadValuesAsync([Id("t:fast/10ms/counter")], Ct))[0];
        var before = monitored.Count;
        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);
        Assert.Equal(counter, (await client.ReadValuesAsync([Id("t:fast/10ms/counter")], Ct))[0]);
        Assert.True(monitored.Count > before, "monitored topic stopped");
    }

    [Fact]
    public async Task Choosing_by_hand_cancels_the_pending_automatic_pause()
    {
        await using var client = new MqttDeviceClient();
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = broker.EndpointUrl, AutoPauseDiscoverySeconds = 1 }, Ct);
        await client.SetDiscoveryPausedAsync(true, Ct);
        await client.SetDiscoveryPausedAsync(false, Ct);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.False(client.IsDiscoveryPaused);
        Assert.Equal("on · receiving the whole topic filter", Row(await client.GetDiagnosticsAsync(Ct), "Discovery"));
    }

    private static string Row(ConnectionDiagnostics diagnostics, string name) => diagnostics.Session.Single(r => r.Name == name).Value;

    [Fact]
    public async Task Device_death_turns_its_metrics_bad_and_rebirth_recovers()
    {
        var updates = new ConcurrentQueue<ValueUpdate>();
        await using var monitor = await _client.MonitorAsync(Id("m:Plant1|Edge1|Press2|Temperature"), updates.Enqueue, 50, Ct);

        await Until(() => Task.FromResult(updates.Any(u => u.Status == StatusCodes.BadNoCommunication)), seconds: 40);
        await Until(() => Task.FromResult(updates.Reverse().FirstOrDefault()?.Status == StatusCodes.Good), seconds: 40);
    }

    [Fact]
    public async Task Monitoring_delivers_fast_values_and_the_current_value_at_once()
    {
        var updates = new ConcurrentQueue<ValueUpdate>();
        await using (await _client.MonitorAsync(Id("t:config/line1#/cycleMs"), updates.Enqueue, 250, Ct))
        {
            Assert.Equal("850", Assert.Single(updates).Value); // retained, delivered immediately
        }

        var fast = new ConcurrentQueue<ValueUpdate>();
        await using var monitor = await _client.MonitorAsync(Id("t:fast/10ms/counter"), fast.Enqueue, 10, Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Assert.True(fast.Select(u => u.Value).Distinct().Count() >= 20, $"only {fast.Count} updates in 1 s");
    }

    [Fact]
    public async Task Refresh_time_limits_the_update_rate_and_zero_delivers_every_message()
    {
        var every = new ConcurrentQueue<ValueUpdate>();
        var throttled = new ConcurrentQueue<ValueUpdate>();
        await using (await _client.MonitorAsync(Id("t:fast/10ms/counter"), every.Enqueue, 0, Ct))
        await using (await _client.MonitorAsync(Id("t:fast/10ms/counter"), throttled.Enqueue, 500, Ct))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }

        Assert.True(every.Count >= 100, $"every message: only {every.Count} in 2 s");
        Assert.InRange(throttled.Count, 3, 6); // at most one per 500 ms, the latest value
        Assert.True(long.Parse(throttled.Last().Value, System.Globalization.CultureInfo.InvariantCulture) > long.Parse(throttled.First().Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Search_finds_topics_json_fields_and_metrics_with_their_paths()
    {
        var result = await _client.SearchAsync(_client.Root, "bearing", cancellationToken: Ct);
        var hit = Assert.Single(result.Hits, h => h.Item.NodeId.Identifier as string == "t:machines/m1/status#/temperature/bearing");
        Assert.Equal("Topics › machines › m1 › status › temperature › bearing", hit.PathText);
        Assert.Equal("root", hit.Path[0].Identifier);

        var wildcard = await _client.SearchAsync(_client.Root, "Press?", cancellationToken: Ct);
        Assert.Contains(wildcard.Hits, h => h.PathText.EndsWith("Edge1 › Press1", StringComparison.Ordinal));
        Assert.DoesNotContain(wildcard.Hits, h => h.Item.DisplayName == "press"); // "?" needs one more character

        var byId = await _client.SearchAsync(new BrowseItem(Id("t:acme"), "acme", "acme", NodeClass.Object), "line3/robot/_meta", cancellationToken: Ct);
        Assert.Contains(byId.Hits, h => h.Item.NodeId.Identifier as string == "t:acme/billund/moulding/line3/robot/_meta");

        var limited = await _client.SearchAsync(_client.Root, "sensor", maxNodes: 10, cancellationToken: Ct);
        Assert.True(limited.Truncated);
    }

    [Fact]
    public async Task Paths_from_root_resolve_for_topics_json_fields_and_metrics()
    {
        Assert.Equal(["root", "topics", "t:machines", "t:machines/m1", "t:machines/m1/status", "t:machines/m1/status#/temperature", "t:machines/m1/status#/temperature/bearing"],
            (await _client.GetPathFromRootAsync(Id("t:machines/m1/status#/temperature/bearing"), cancellationToken: Ct)).Select(n => (string)n.Identifier));
        Assert.Equal(["root", "spb", "g:Plant1", "e:Plant1|Edge1", "d:Plant1|Edge1|Press1", "f:Plant1|Edge1|Press1|Motor", "m:Plant1|Edge1|Press1|Motor/Speed"],
            (await _client.GetPathFromRootAsync(Id("m:Plant1|Edge1|Press1|Motor/Speed"), cancellationToken: Ct)).Select(n => (string)n.Identifier));
    }

    [Fact]
    public void Endpoint_urls_parse_ports_tls_websocket_and_topic_filters()
    {
        Assert.Equal(new MqttEndpoint("broker", 1883, false, false, string.Empty, "#", null, null), MqttEndpoint.Parse("mqtt://broker"));
        Assert.Equal("plant/#", MqttEndpoint.Parse("mqtt://broker/plant/#").TopicFilter);
        Assert.Equal("a/+/b", MqttEndpoint.Parse("mqtts://u:p@broker?topic=a/%2B/b").TopicFilter);
        var tls = MqttEndpoint.Parse("mqtts://user:secret@broker");
        Assert.Equal((8883, true, "user", "secret"), (tls.Port, tls.Tls, tls.UserName, tls.Password));
        var ws = MqttEndpoint.Parse("wss://broker/mqtt");
        Assert.Equal((443, true, true, "wss://broker:443/mqtt"), (ws.Port, ws.Tls, ws.WebSocket, ws.WebSocketUri));
        Assert.True(DeviceClient.IsMqtt(" MQTT://x"));
        Assert.IsType<MqttDeviceClient>(DeviceClient.Create("mqtt://x"));
    }

    private static async Task Until(Func<Task<bool>> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            await Task.Delay(50, Ct);
        }
    }
}
