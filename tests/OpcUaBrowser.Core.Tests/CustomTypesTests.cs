using Opc.Ua;
using OpcUaBrowser.Core.Ua;
using Xunit;

namespace OpcUaBrowser.Core.Tests;

/// <summary>OpcUaClient against the custom-types server in <c>simulators/opcua-custom</c>.</summary>
public sealed class CustomTypesTests(CustomTypesServerFixture server) : IAsyncLifetime
{
    private readonly OpcUaClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = server.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<BrowseItem> FindAsync(params string[] path)
    {
        var current = new BrowseItem(ObjectIds.ObjectsFolder, "Objects", "Objects", NodeClass.Object);
        foreach (var name in path)
        {
            var children = await _client.BrowseAsync(current.NodeId, Ct);
            current = Assert.Single(children, c => c.DisplayName == name);
        }

        return current;
    }

    private async Task<object?> ReadAsync(params string[] path) =>
        (await _client.ReadValuesAsync([(await FindAsync(path)).NodeId], Ct))[0];

    [Fact]
    public async Task Nested_structure_with_arrays_decodes_through_the_type_system()
    {
        var value = Assert.IsType<ExtensionObject>(await ReadAsync("Custom", "Machines", "Machine1", "Status"));

        var body = Assert.IsAssignableFrom<IEncodeable>(value.Body);
        Assert.Equal("MachineStatus", body.GetType().Name);
        var text = ValueFormatter.Format(new Variant(value));
        Assert.StartsWith("{", text, StringComparison.Ordinal); // decoded fields: {State|Speed|{X|Y|Z}|…}
        Assert.DoesNotContain("undecoded", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structure_arrays_optional_fields_and_unions_decode()
    {
        var history = Assert.IsType<ExtensionObject[]>(await ReadAsync("Custom", "Structures", "StatusHistory"));
        Assert.Equal(5, history.Length);
        Assert.All(history, h => Assert.IsAssignableFrom<IEncodeable>(h.Body));

        foreach (var name in new[] { "QualityFull", "QualityPartial", "SetpointNumeric", "SetpointText", "Position" })
        {
            var value = Assert.IsType<ExtensionObject>(await ReadAsync("Custom", "Structures", name));
            Assert.IsAssignableFrom<IEncodeable>(value.Body);
        }
    }

    [Fact]
    public async Task Multi_dimensional_arrays_read_as_matrices()
    {
        var matrix = await ReadAsync("Custom", "DataTypes", "Arrays", "Matrix3x4");
        Assert.NotNull(matrix);
        Assert.Contains("3", ValueFormatter.Format(new Variant(matrix)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flat_folder_with_thousands_of_children_browses_completely()
    {
        var flat = await FindAsync("Custom", "Flat");
        Assert.Equal(2500, (await _client.BrowseAsync(flat.NodeId, Ct)).Count);
    }

    [Fact]
    public async Task Typed_machine_exports_to_csharp()
    {
        var machine = await FindAsync("Custom", "Machines", "Machine2");
        var tree = await _client.ReadTreeAsync(machine.NodeId, "Machine2", NodeClass.Object, 3, 100, Ct);
        var code = NodeExport.ToCSharp(tree);

        Assert.Contains("MachineStatus Status", code, StringComparison.Ordinal);
        Assert.Contains("SerialNumber", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fast_values_change_every_10_ms()
    {
        var node = await FindAsync("Custom", "Fast", "Every10ms", "Counter");
        var values = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using var handle = await _client.MonitorAsync(node.NodeId, u => values.Add(u.Value), 10, Ct);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.True(values.Distinct().Count() >= 20, $"only {values.Distinct().Count()} distinct values");
    }

}
