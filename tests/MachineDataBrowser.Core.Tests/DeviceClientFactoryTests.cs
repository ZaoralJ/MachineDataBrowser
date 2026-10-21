using MachineDataBrowser.Core;
using MachineDataBrowser.Driver.Cip;
using MachineDataBrowser.Driver.Modbus;
using MachineDataBrowser.Driver.Mqtt;
using MachineDataBrowser.Driver.OpcUa;
using MachineDataBrowser.Driver.S7;
using MachineDataBrowser.Drivers;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class DeviceClientFactoryTests
{
    [Theory]
    [InlineData("opc.tcp://localhost:4840", typeof(OpcUaClient))]
    [InlineData("eip://localhost/1,0", typeof(CipClient))]
    [InlineData("mqtt://localhost", typeof(MqttDeviceClient))]
    [InlineData("modbus://localhost:502/1", typeof(ModbusClient))]
    [InlineData("s7://localhost/0,1", typeof(S7Client))]
    [InlineData("S7://plc?tags=tags.csv", typeof(S7Client))]
    public async Task Create_selects_client_for_endpoint(string endpoint, Type expectedType)
    {
        var client = new DeviceClientFactory().Create(endpoint);
        try
        {
            Assert.Equal(expectedType, client.GetType());
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task Supports_matches_the_created_client_to_its_protocol()
    {
        var factory = new DeviceClientFactory();
        await using var client = factory.Create("mqtt://localhost");

        Assert.True(factory.Supports(client, "mqtt://localhost"));
        Assert.False(factory.Supports(client, "eip://localhost/1,0"));
        Assert.False(factory.Supports(client, "opc.tcp://localhost:4840"));
    }
}
