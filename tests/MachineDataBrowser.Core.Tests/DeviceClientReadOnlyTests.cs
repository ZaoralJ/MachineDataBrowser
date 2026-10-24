using MachineDataBrowser.Drivers;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class DeviceClientReadOnlyTests
{
    [Theory]
    [InlineData("opc.tcp://localhost:4841", false)]
    [InlineData("eip://localhost/1,0", false)]
    [InlineData("pccc://localhost?plc=slc500", false)]
    [InlineData("modbus://localhost/1", false)]
    [InlineData("s7://localhost/0,1", false)]
    [InlineData("mqtt://localhost:1883", false)]
    [InlineData("mtconnect://localhost:5000", true)]
    public async Task Client_reports_protocol_read_only_capability(string endpointUrl, bool expected)
    {
        await using var client = new DeviceClientFactory().Create(endpointUrl);

        Assert.Equal(expected, client.IsReadOnly);
    }
}
