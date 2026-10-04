using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>The context tools (attributes, history, alarms, events, diagnostics) through a real <c>mdbrowser mcp</c> process.</summary>
public sealed class McpContextToolsTests(OpcPlcFixture plc, CustomTypesServerFixture custom) : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-mcp-context").FullName;
    private McpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mdbrowser",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "mdbrowser.dll"), "mcp", "--endpoint", plc.EndpointUrl, "--endpoint", custom.EndpointUrl, "--trust-all"],
            EnvironmentVariables = new Dictionary<string, string?> { ["MACHINEDATABROWSER_DATA_DIR"] = _dir },
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<JsonNode> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.False(result.IsError ?? false, text);
        return JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task Attributes_include_the_engineering_unit()
    {
        var attributes = await CallAsync("attributes", new() { ["node"] = "/Objects/Custom/History/Temperature", ["endpoint"] = custom.EndpointUrl });

        Assert.Equal("°C", (string?)attributes["unit"]);
        Assert.Equal("Variable", (string?)attributes["attributes"]!["NodeClass"]);
    }

    [Fact]
    public async Task History_comes_raw_or_per_bucket()
    {
        // A fixed end: the simulator adds a value every 10 s, so two calls ending "now" can differ by one.
        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var to = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var node = "/Objects/Custom/History/Temperature";

        var raw = (await CallAsync("history", new() { ["nodes"] = new[] { node }, ["from"] = from, ["to"] = to, ["endpoint"] = custom.EndpointUrl }))["items"]![0]!;
        Assert.True((int)raw["values"]! > 300);
        Assert.Equal(JsonValueKind.Number, raw["samples"]![0]!["value"]!.GetValueKind());

        var hourly = (await CallAsync("history", new() { ["nodes"] = new[] { node }, ["from"] = from, ["to"] = to, ["bucketSeconds"] = 900, ["endpoint"] = custom.EndpointUrl }))["items"]![0]!;
        var buckets = hourly["buckets"]!.AsArray();
        Assert.InRange(buckets.Count, 4, 5);
        Assert.Equal((int)raw["values"]!, buckets.Sum(b => (int)b!["count"]!));
    }

    [Fact]
    public async Task Alarms_events_and_diagnostics_describe_the_server()
    {
        var alarms = await CallAsync("alarms", new() { ["endpoint"] = plc.EndpointUrl });
        Assert.True((int)alarms["count"]! > 0, alarms.ToJsonString());
        Assert.All(alarms["alarms"]!.AsArray(), a => Assert.InRange((int)a!["severity"]!, 1, 1000));

        var events = await CallAsync("events", new() { ["seconds"] = 3, ["endpoint"] = plc.EndpointUrl });
        Assert.True((int)events["received"]! > 0);

        var diagnostics = await CallAsync("diagnostics", new() { ["endpoint"] = plc.EndpointUrl });
        Assert.Equal("Connected", (string?)diagnostics["state"]);
        Assert.Equal("Running", (string?)diagnostics["session"]!["Server state"]);
    }

    [Fact]
    public async Task Events_below_an_object_without_events_explain_why()
    {
        var result = await _client.CallToolAsync("events", new Dictionary<string, object?> { ["seconds"] = 1, ["node"] = "/Objects/Custom", ["endpoint"] = custom.EndpointUrl },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("'/Objects/Custom' doesn't report events", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_tools_are_listed_as_read_only()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var name in new[] { "attributes", "history", "alarms", "events", "diagnostics" })
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, name);
        }
    }
}
