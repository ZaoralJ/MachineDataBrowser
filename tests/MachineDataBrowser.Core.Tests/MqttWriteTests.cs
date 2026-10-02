using System.Text;
using Opc.Ua;
using MachineDataBrowser.Core.Mqtt;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

/// <summary>Writes through MqttDeviceClient: topics, JSON fields and Sparkplug commands the simulator applies.</summary>
public sealed class MqttWriteTests(MqttSimulatorFixture broker) : IAsyncLifetime
{
    private readonly MqttDeviceClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeId Id(string id) => new(id, 1);

    public async ValueTask InitializeAsync()
    {
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = broker.EndpointUrl }, Ct);
        await Until(async () => await ReadAsync("t:bulk/sensor/0049") is not null
            && await ReadAsync("t:acme/billund/moulding/line2/shift#/crew") is not null
            && await ReadAsync("m:Plant1|Edge1|Press1|Types/UInt8") is not null);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<object?> ReadAsync(string id) => (await _client.ReadValuesAsync([Id(id)], Ct))[0];

    [Fact]
    public async Task Topic_write_publishes_a_retained_payload_of_the_same_kind()
    {
        await _client.WriteValueAsync(Id("t:bulk/sensor/0049"), "123.5", Ct);
        await Until(async () => Equals(await ReadAsync("t:bulk/sensor/0049"), 123.5));

        // Live deliveries carry no retain flag; a new connection gets the retained message.
        await using var fresh = new MqttDeviceClient();
        await fresh.ConnectAsync(new ConnectOptions { EndpointUrl = broker.EndpointUrl }, Ct);
        await Until(async () => Equals((await fresh.ReadValuesAsync([Id("t:bulk/sensor/0049")], Ct))[0], 123.5));
        Assert.Contains(await fresh.ReadAttributesAsync(Id("t:bulk/sensor/0049"), Ct), a => a.Name == "Retained" && a.Value == "Yes");
        await Assert.ThrowsAsync<FormatException>(() => _client.WriteValueAsync(Id("t:bulk/sensor/0049"), "not a number", Ct));
    }

    [Fact]
    public async Task Json_field_write_changes_only_that_field()
    {
        await _client.WriteValueAsync(Id("t:acme/billund/moulding/line2/shift#/crew"), "7", Ct);
        await Until(async () => Equals(await ReadAsync("t:acme/billund/moulding/line2/shift#/crew"), 7d));
        Assert.Equal("Operator 02", await ReadAsync("t:acme/billund/moulding/line2/shift#/supervisor"));

        await _client.WriteValueAsync(Id("t:acme/billund/moulding/line2/shift#/supervisor"), "Jane", Ct);
        await Until(async () => Equals(await ReadAsync("t:acme/billund/moulding/line2/shift#/supervisor"), "Jane"));
        Assert.Equal(7d, await ReadAsync("t:acme/billund/moulding/line2/shift#/crew"));
    }

    [Fact]
    public async Task Sparkplug_metric_write_sends_a_dcmd_the_device_applies()
    {
        await _client.WriteValueAsync(Id("m:Plant1|Edge1|Press1|Types/UInt8"), "77", Ct);
        await Until(async () => Equals(await ReadAsync("m:Plant1|Edge1|Press1|Types/UInt8"), (byte)77));
        await Assert.ThrowsAsync<FormatException>(() => _client.WriteValueAsync(Id("m:Plant1|Edge1|Press1|Types/UInt8"), "300", Ct));
    }

    [Fact]
    public void Payloads_keep_their_kind()
    {
        Assert.Equal("false", Encoding.UTF8.GetString(MqttModel.EncodeTopicPayload(true, "off")));
        Assert.Equal([0xFF, 0xFE, 0x00], MqttModel.EncodeTopicPayload(new byte[] { 1 }, "ByteString[3] FFFE00"));
        Assert.Equal([0xAB, 0x01], MqttModel.EncodeTopicPayload(new byte[] { 1 }, "0xAB, 0x01"));
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(MqttModel.EncodeTopicPayload(System.Text.Json.Nodes.JsonNode.Parse("{}"), "{ \"a\": 1 }")));
        Assert.Throws<FormatException>(() => MqttModel.EncodeTopicPayload(System.Text.Json.Nodes.JsonNode.Parse("{}"), "{broken"));
        Assert.Equal("plain text", Encoding.UTF8.GetString(MqttModel.EncodeTopicPayload("old", "plain text")));
    }

    [Fact]
    public void Sparkplug_command_round_trips_through_the_decoder()
    {
        var payload = SparkplugB.Decode(SparkplugB.EncodeCommand("Types/Int16", 110, 2, (short)-5, DateTime.UtcNow));
        var metric = Assert.Single(payload.Metrics);
        Assert.Equal("Types/Int16", metric.Name);
        Assert.Equal(110UL, metric.Alias);
        Assert.Equal((short)-5, metric.Value);
        Assert.Equal(1.5f, Assert.Single(SparkplugB.Decode(SparkplugB.EncodeCommand("F", null, 9, 1.5f, DateTime.UtcNow)).Metrics).Value);
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
