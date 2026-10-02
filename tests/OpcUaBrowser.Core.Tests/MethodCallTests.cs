using Opc.Ua;
using OpcUaBrowser.Core.Ua;
using Xunit;

namespace OpcUaBrowser.Core.Tests;

/// <summary>Method calls against <c>Custom ▸ Methods</c> of <c>simulators/opcua-custom</c>.</summary>
public sealed class MethodCallTests(CustomTypesServerFixture server) : IAsyncLifetime
{
    private readonly OpcUaClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = server.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<(NodeId Folder, NodeId Method)> FindAsync(string method)
    {
        var node = ObjectIds.ObjectsFolder;
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
        var signature = await _client.GetMethodSignatureAsync(add, Ct);
        Assert.Equal(["A", "B"], signature.Inputs.Select(a => a.Name));
        Assert.All(signature.Inputs, a => Assert.Equal(BuiltInType.Double, a.BuiltInType));
        Assert.Equal("Double", signature.Inputs[0].DataType);
        Assert.Equal("First addend", signature.Inputs[0].Description);
        Assert.Equal("Sum", Assert.Single(signature.Outputs).Name);

        var (_, stats) = await FindAsync("Stats");
        var array = Assert.Single((await _client.GetMethodSignatureAsync(stats, Ct)).Inputs);
        Assert.True(array.IsArray);
        Assert.Equal("Double[]", array.DataType);
    }

    [Fact]
    public async Task Calls_parse_text_arguments_and_return_outputs()
    {
        var (folder, add) = await FindAsync("Add");
        Assert.Equal(["5.75"], await _client.CallMethodAsync(folder, add, ["2.5", "3.25"], Ct));

        var (_, greet) = await FindAsync("Greet");
        Assert.Equal(["Hello Ada! Hello Ada!"], await _client.CallMethodAsync(folder, greet, ["Ada", "2"], Ct));

        var (_, stats) = await FindAsync("Stats");
        Assert.Equal(["1", "9", "4"], await _client.CallMethodAsync(folder, stats, ["[1, 2, 9]"], Ct));
    }

    [Fact]
    public async Task Bad_input_and_server_errors_are_reported()
    {
        var (folder, add) = await FindAsync("Add");
        await Assert.ThrowsAsync<FormatException>(() => _client.CallMethodAsync(folder, add, ["two", "3"], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.CallMethodAsync(folder, add, ["2"], Ct));

        var (_, greet) = await FindAsync("Greet");
        var error = await Assert.ThrowsAsync<ServiceResultException>(() => _client.CallMethodAsync(folder, greet, ["Ada", "99"], Ct));
        Assert.Equal(StatusCodes.BadOutOfRange, error.StatusCode);
    }
}
