using DotNet.Testcontainers.Builders;
using MachineDataBrowser.Driver.S7;
using Xunit;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Core.Tests;

/// <summary>Writes to the S7 simulator: each test writes values no other test reads, and restores them.</summary>
public sealed class S7WriteTests(S7SimulatorFixture plc) : IAsyncLifetime
{
    private readonly S7Client _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private NodeId Id(string text) => _client.ParsePortableId(text);

    private async Task<object?> ReadAsync(string text) => (await _client.ReadValuesAsync([Id(text)], Ct))[0];

    [Fact]
    public async Task Scalars_and_strings_are_written_in_their_type()
    {
        await _client.WriteValueAsync(Id("Machine.Setpoint"), "1234.5", Ct);
        await _client.WriteValueAsync(Id("Machine.Recipe"), "-12", Ct);
        await _client.WriteValueAsync(Id("Machine.Name"), "\"Line 42\"", Ct);
        try
        {
            Assert.Equal(1234.5f, await ReadAsync("Machine.Setpoint"));
            Assert.Equal((short)-12, await ReadAsync("Machine.Recipe"));
            Assert.Equal("Line 42", await ReadAsync("Machine.Name"));
            Assert.Equal((byte)20, await ReadAsync("DB1.DBB26"));
        }
        finally
        {
            await _client.WriteValueAsync(Id("Machine.Setpoint"), "1500", Ct);
            await _client.WriteValueAsync(Id("Machine.Recipe"), "7", Ct);
            await _client.WriteValueAsync(Id("Machine.Name"), "Line 1", Ct);
        }
    }

    [Fact]
    public async Task A_bit_is_written_alone_and_bool_arrays_keep_the_neighbouring_bits()
    {
        await _client.WriteValueAsync(Id("MB30"), "255", Ct);
        await _client.WriteValueAsync(Id("M30.2:BOOL[4]"), "[false, false, false, false]", Ct);
        Assert.Equal((byte)0b1100_0011, await ReadAsync("MB30"));

        await _client.WriteValueAsync(Id("M30.3"), "true", Ct);
        Assert.Equal((byte)0b1100_1011, await ReadAsync("MB30"));
        await _client.WriteValueAsync(Id("MB30"), "0", Ct);
    }

    [Fact]
    public async Task Arrays_need_every_element_and_bad_input_is_rejected()
    {
        await _client.WriteValueAsync(Id("MW40:INT[3]"), "[1, -2, 3]", Ct);
        Assert.Equal(new short[] { 1, -2, 3 }, Assert.IsType<DeviceArray>(await ReadAsync("MW40:INT[3]")).Values);

        await Assert.ThrowsAsync<FormatException>(() => _client.WriteValueAsync(Id("MW40:INT[3]"), "1, 2", Ct));
        await Assert.ThrowsAsync<FormatException>(() => _client.WriteValueAsync(Id("MW40:INT"), "70000", Ct));
        await Assert.ThrowsAsync<FormatException>(() => _client.WriteValueAsync(Id("MB46:STRING[4]"), "too long", Ct));
        await _client.WriteValueAsync(Id("MW40:INT[3]"), "0,0,0", Ct);
    }

    [Fact]
    public async Task Writing_to_a_missing_data_block_fails_with_the_plc_error()
    {
        var ex = await Assert.ThrowsAsync<DeviceClientException>(() => _client.WriteValueAsync(Id("DB99.DBW0"), "1", Ct));
        Assert.True(ex.StatusCode.IsBad);
        Assert.Contains("DB99.DBW0", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Connected, _client.State);
    }
}

/// <summary>A PLC that disappears must surface as Reconnecting and Bad values, and recover when it is back.</summary>
public sealed class S7ConnectionLossTests
{
    [Fact]
    public async Task Losing_the_plc_reports_reconnecting_and_bad_values_then_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = S7SimulatorFixture.CreateImage("test-connection-loss");
        await image.CreateAsync(ct);
        await using var container = new ContainerBuilder(image)
            .WithPortBinding(12102, S7SimulatorFixture.S7Port)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S7 server ready"))
            .Build();
        await container.StartAsync(ct);

        await using var client = new S7Client();
        var states = new System.Collections.Concurrent.ConcurrentQueue<ConnectionState>();
        client.StateChanged += (_, s) => states.Enqueue(s);
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = "s7://localhost:12102/0,1?timeout=1000" }, ct);

        var updates = new System.Collections.Concurrent.ConcurrentQueue<ValueUpdate>();
        await using var monitor = await client.MonitorAsync(client.ParsePortableId("DB1.DBD6:DINT"), updates.Enqueue, 100, ct);
        await S7ClientTests.Until(() => updates.Any(u => u.Status.IsGood));

        await container.StopAsync(ct);
        await S7ClientTests.Until(() => states.Contains(ConnectionState.Reconnecting), seconds: 30);
        await S7ClientTests.Until(() => updates.Any(u => u.Status.IsBad), seconds: 30);
        Assert.Null((await client.ReadValuesAsync([client.ParsePortableId("DB1.DBD6:DINT")], ct))[0]);

        await container.StartAsync(ct);
        var bad = updates.Count;
        await S7ClientTests.Until(() => updates.Skip(bad).Any(u => u.Status.IsGood), seconds: 30);
        Assert.Equal(ConnectionState.Connected, client.State);

        await client.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }
}
