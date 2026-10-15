using Opc.Ua;
using MachineDataBrowser.Driver.OpcUa;
using Xunit;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Core.Tests;

/// <summary>Method calls against <c>Custom ▸ Methods</c> of <c>simulators/opcua-custom</c>.</summary>
public sealed class MethodCallTests(CustomTypesServerFixture server) : IAsyncLifetime
{
    private readonly IDeviceClient _client = new OpcUaClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = server.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<(NodeId Folder, NodeId Method)> FindAsync(string method)
    {
        var node = NodeId.FromOpaque(ObjectIds.ObjectsFolder.ToString());
        foreach (var name in new[] { "Custom", "Methods" })
        {
            node = (await _client.BrowseAsync(node, Ct)).Single(c => c.DisplayName == name).NodeId;
        }

        return (node, (await _client.BrowseAsync(node, Ct)).Single(c => c.DisplayName == method).NodeId);
    }

    [Fact]
    public async Task Signature_lists_names_types_and_descriptions()
    {
        var (_, add) = await FindAsync("Add");
        var caller = (IMethodCaller)_client;
        var signature = await caller.GetMethodSignatureAsync(add, Ct);
        Assert.Equal(["A", "B"], signature.Inputs.Select(a => a.Name));
        Assert.All(signature.Inputs, a => Assert.Equal(DeviceDataType.Double, a.ValueType));
        Assert.Equal("Double", signature.Inputs[0].DataType);
        Assert.Equal("First addend", signature.Inputs[0].Description);
        Assert.Equal("Sum", Assert.Single(signature.Outputs).Name);

        var (_, stats) = await FindAsync("Stats");
        var array = Assert.Single((await caller.GetMethodSignatureAsync(stats, Ct)).Inputs);
        Assert.True(array.IsArray);
        Assert.Equal("Double[]", array.DataType);
    }

    [Fact]
    public async Task Calls_parse_text_arguments_and_return_outputs()
    {
        var (folder, add) = await FindAsync("Add");
        var caller = (IMethodCaller)_client;
        Assert.Equal(["5.75"], await caller.CallMethodAsync(folder, add, ["2.5", "3.25"], Ct));

        var (_, greet) = await FindAsync("Greet");
        Assert.Equal(["Hello Ada! Hello Ada!"], await caller.CallMethodAsync(folder, greet, ["Ada", "2"], Ct));

        var (_, stats) = await FindAsync("Stats");
        Assert.Equal(["1", "9", "4"], await caller.CallMethodAsync(folder, stats, ["[1, 2, 9]"], Ct));
    }

    [Fact]
    public async Task Bad_input_and_server_errors_are_reported()
    {
        var (folder, add) = await FindAsync("Add");
        var caller = (IMethodCaller)_client;
        await Assert.ThrowsAsync<FormatException>(() => caller.CallMethodAsync(folder, add, ["two", "3"], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => caller.CallMethodAsync(folder, add, ["2"], Ct));

        var (_, greet) = await FindAsync("Greet");
        var error = await Assert.ThrowsAsync<DeviceClientException>(() => caller.CallMethodAsync(folder, greet, ["Ada", "99"], Ct));
        Assert.Equal(DeviceStatusCodes.BadOutOfRange.Name, error.StatusCode.Name);
    }
}
