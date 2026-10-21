using MachineDataBrowser.Driver.Pccc;
using MachineDataBrowser.Drivers;
using Xunit;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Core.Tests;

/// <summary>PcccClient against the PCCC simulator, once per PLC family (they share one data table).</summary>
public sealed class PcccClientTests(PcccSimulatorFixture plc)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeId Address(string text) => new(text, PcccClient.AddressNamespace);

    private async Task<PcccClient> ConnectAsync(string family)
    {
        var client = new PcccClient();
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.Url(family) }, Ct);
        return client;
    }

    [Theory]
    [InlineData("slc500")]
    [InlineData("micrologix")]
    [InlineData("plc5")]
    public async Task Lists_standard_and_named_files(string family)
    {
        await using var client = await ConnectAsync(family);

        var files = await client.BrowseAsync(client.Root.NodeId, Ct);

        Assert.Equal(["O0", "I1", "S2", "B3", "T4", "C5", "R6", "N7", "F8", "N9", "ST10", "L11"], files.Select(f => f.DisplayName));
        Assert.Equal("Integer[100]", files.Single(f => f.DisplayName == "N7").BrowseName);
    }

    [Theory]
    [InlineData("slc500")]
    [InlineData("plc5")]
    public async Task Finds_the_length_of_a_file(string family)
    {
        await using var client = await ConnectAsync(family);

        var elements = await client.BrowseAsync(Address("N9"), Ct);

        Assert.Equal(256, elements.Count);
        Assert.Equal("N9:255", elements[^1].DisplayName);
    }

    [Theory]
    [InlineData("slc500")]
    [InlineData("micrologix")]
    [InlineData("plc5")]
    public async Task Reads_every_file_type(string family)
    {
        await using var client = await ConnectAsync(family);

        var values = await client.ReadValuesAsync(
            [Address("N9:42"), Address("F8:12"), Address("L11:3"), Address("ST10:2"), Address("T4:1.PRE"), Address("T4:1.DN"), Address("C5:2.ACC"), Address("B3:2/0"), Address("B3:2/1")], Ct);

        Assert.Equal<object?>([(short)42, 18f, 300000, "MicroLogix", (short)150, true, (short)7, true, false], values);
    }

    [Fact]
    public async Task Browses_structures_and_bits()
    {
        await using var client = await ConnectAsync("slc500");

        var timer = await client.BrowseAsync(Address("T4:0"), Ct);
        var bits = await client.BrowseAsync(Address("B3:1"), Ct);

        Assert.Equal(["EN", "TT", "DN", "PRE", "ACC"], timer.Select(t => t.DisplayName));
        Assert.Equal(16, bits.Count);
        Assert.Equal("B3:1/15", bits[^1].NodeId.IdentifierAsString);
        Assert.Equal([client.Root.NodeId, Address("T4"), Address("T4:0"), Address("T4:0.ACC")],
            await client.GetPathFromRootAsync(client.ParsePortableId("t4:0.acc"), cancellationToken: Ct));
    }

    [Theory]
    [InlineData("slc500", "N7:30", "-1234", (short)-1234)]
    [InlineData("plc5", "N7:31", "4321", (short)4321)]
    [InlineData("slc500", "F8:30", "12.5", 12.5f)]
    [InlineData("plc5", "F8:31", "-0.25", -0.25f)]
    [InlineData("micrologix", "L11:10", "-70000", -70000)]
    [InlineData("slc500", "ST10:6", "\"written by slc\"", "written by slc")]
    [InlineData("plc5", "ST10:7", "plc-5 text", "plc-5 text")]
    [InlineData("slc500", "T4:5.PRE", "321", (short)321)]
    [InlineData("plc5", "C5:5.PRE", "99", (short)99)]
    [InlineData("slc500", "T4:6.DN", "true", true)]
    [InlineData("plc5", "B3:5/7", "true", true)]
    [InlineData("micrologix", "N7:32/3", "true", true)]
    public async Task Writes_values(string family, string address, string text, object expected)
    {
        await using var client = await ConnectAsync(family);

        await client.WriteValueAsync(Address(address), text, Ct);

        Assert.Equal(expected, (await client.ReadValuesAsync([Address(address)], Ct))[0]);
    }

    [Fact]
    public async Task Writing_a_bit_keeps_the_other_bits_of_its_word()
    {
        await using var client = await ConnectAsync("slc500");
        await client.WriteValueAsync(Address("B3:6"), "5", Ct);

        await client.WriteValueAsync(Address("B3:6/4"), "true", Ct);
        await client.WriteValueAsync(Address("B3:6/0"), "false", Ct);

        Assert.Equal((short)20, (await client.ReadValuesAsync([Address("B3:6")], Ct))[0]);
    }

    [Fact]
    public async Task Monitors_changing_values()
    {
        await using var client = await ConnectAsync("slc500");
        var updates = new System.Collections.Concurrent.ConcurrentQueue<ValueUpdate>();

        var results = await client.MonitorManyAsync([Address("N7:2"), Address("T4:0")], updates.Enqueue, 100, Ct);

        Assert.True(results[0].Error.StatusCode.IsGood);
        Assert.False(results[1].Error.StatusCode.IsGood);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (updates.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(updates.Count >= 3);
        Assert.All(updates, u => Assert.True(u.Status.IsGood));
        await ((IDeviceClient)client).StopMonitoringAsync(results.Select(r => r.Handle).OfType<IAsyncDisposable>(), Ct);
    }

    [Fact]
    public async Task Unknown_addresses_read_as_null()
    {
        await using var client = await ConnectAsync("slc500");

        var values = await client.ReadValuesAsync([Address("N7:500"), Address("N42:0"), Address("not an address")], Ct);

        Assert.All(values, Assert.Null);
    }

    [Fact]
    public async Task Reports_an_unreachable_controller()
    {
        await using var client = new PcccClient();

        await Assert.ThrowsAsync<DeviceClientException>(() =>
            client.ConnectAsync(new ConnectOptions { EndpointUrl = "pccc://127.0.0.1:1?plc=slc500" }, Ct));
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }
}

public sealed class PcccAddressTests
{
    [Theory]
    [InlineData("N7:0", "N7:0")]
    [InlineData("n7:0", "N7:0")]
    [InlineData("B3:0/15", "B3:0/15")]
    [InlineData("t4:1.acc", "T4:1.ACC")]
    [InlineData("C5:0.DN", "C5:0.DN")]
    [InlineData("R6:2.POS", "R6:2.POS")]
    [InlineData("ST10:1", "ST10:1")]
    [InlineData("S:4", "S2:4")]
    [InlineData("I:0/3", "I1:0/3")]
    [InlineData("O:1", "O0:1")]
    [InlineData("L11:0/31", "L11:0/31")]
    [InlineData("F8", "F8")]
    public void Parses_and_normalises(string text, string expected)
    {
        Assert.True(PcccAddress.TryParse(text, out var address));
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("X7:0")]
    [InlineData("N7:")]
    [InlineData("N7:0/16")]
    [InlineData("F8:0/1")]
    [InlineData("T4:0/13")]
    [InlineData("T4:0.XYZ")]
    [InlineData("N7:0.ACC")]
    [InlineData("Program:Main.Speed")]
    public void Rejects_invalid_addresses(string text) => Assert.False(PcccAddress.TryParse(text, out _));

    [Fact]
    public void Strings_round_trip_with_swapped_bytes()
    {
        var element = new byte[84];
        PcccCodec.EncodeString("Hello", element);

        Assert.Equal([5, 0, (byte)'e', (byte)'H', (byte)'l', (byte)'l', 0, (byte)'o'], element[..8]);
        Assert.Equal("Hello", PcccCodec.DecodeString(element));
    }

    [Theory]
    [InlineData("pccc://10.0.0.5", "10.0.0.5", PcccPlcFamily.Slc500)]
    [InlineData("pccc://10.0.0.5:2222?plc=micrologix", "10.0.0.5:2222", PcccPlcFamily.MicroLogix)]
    [InlineData("PCCC://plc5.local?plc=PLC-5&files=N10,st12", "plc5.local", PcccPlcFamily.Plc5)]
    public void Parses_endpoints(string url, string gateway, PcccPlcFamily family)
    {
        var endpoint = PcccClient.PcccEndpoint.Parse(url);

        Assert.Equal(gateway, endpoint.Gateway);
        Assert.Equal(family, endpoint.Family);
    }

    [Theory]
    [InlineData("pccc://")]
    [InlineData("pccc://host?plc=s7")]
    [InlineData("pccc://host?files=N7:0")]
    [InlineData("pccc://host?speed=1")]
    public void Rejects_invalid_endpoints(string url) =>
        Assert.Throws<FormatException>(() => PcccClient.PcccEndpoint.Parse(url));

    [Fact]
    public async Task Factory_creates_pccc_clients()
    {
        var factory = new DeviceClientFactory();
        await using var client = factory.Create("pccc://10.0.0.5?plc=slc500");

        Assert.IsType<PcccClient>(client);
        Assert.True(factory.Supports(client, "pccc://other"));
        Assert.False(factory.Supports(client, "eip://10.0.0.5/1,0"));
        await using var opcUa = factory.Create("opc.tcp://x:4840");
        Assert.False(factory.Supports(opcUa, "pccc://10.0.0.5"));
        Assert.Equal("EtherNet/IP (PCCC)", DeviceClient.ProtocolName("pccc://x"));
    }
}
