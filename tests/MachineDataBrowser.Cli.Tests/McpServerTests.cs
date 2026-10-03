using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>Runs <c>mdbrowser mcp</c> as a process and talks to it like an agent does, over stdio.</summary>
public sealed class McpServerTests(OpcPlcFixture plc) : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-mcp").FullName;
    private McpClient? _client;

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        Directory.Delete(_dir, recursive: true);
    }

    private async Task<McpClient> StartAsync(params string[] args)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mdbrowser",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "mdbrowser.dll"), "mcp", .. args],
            EnvironmentVariables = new Dictionary<string, string?> { ["MACHINEDATABROWSER_DATA_DIR"] = _dir },
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
        return _client;
    }

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.False(result.IsError ?? false, text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<string> ErrorAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    [Fact]
    public async Task Offers_read_only_tools_and_instructions()
    {
        var client = await StartAsync("--endpoint", plc.EndpointUrl, "--trust-all");

        Assert.Equal("mdbrowser", client.ServerInfo.Name);
        Assert.Contains("read-only", client.ServerInstructions, StringComparison.OrdinalIgnoreCase);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["alarms", "attributes", "browse", "diagnostics", "events", "generate_code", "history", "list_endpoints", "read", "sample", "search", "wait_for"], tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, t.Name));
    }

    [Fact]
    public async Task Browse_search_and_read_find_and_read_values_by_path()
    {
        var client = await StartAsync("--endpoint", plc.EndpointUrl, "--trust-all");

        var endpoints = (await CallAsync(client, "list_endpoints", [])).AsArray();
        Assert.Equal(plc.EndpointUrl, (string)Assert.Single(endpoints)!["endpoint"]!);

        var browse = await CallAsync(client, "browse", new() { ["node"] = "/Objects/OpcPlc/Telemetry/Basic" });
        Assert.Contains(browse["items"]!.AsArray(), i => (string)i!["path"]! == "/Objects/OpcPlc/Telemetry/Basic/StepUp");

        var search = await CallAsync(client, "search", new() { ["text"] = "Random*", ["under"] = "/Objects/OpcPlc" });
        Assert.Contains(search["hits"]!.AsArray(), h => (string)h!["path"]! == "/Objects/OpcPlc/Telemetry/Basic/RandomSignedInt32");

        var read = await CallAsync(client, "read", new() { ["nodes"] = new[] { "/Objects/OpcPlc/Telemetry/Basic" }, ["recursive"] = true });
        var values = read["values"]!.AsArray();
        Assert.Equal(4, values.Count);
        var step = values.Single(v => (string)v!["name"]! == "StepUp")!;
        Assert.Equal("UInt32", (string)step["type"]!);
        Assert.True(step["value"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task Sample_summarises_how_values_behaved()
    {
        var client = await StartAsync("--endpoint", plc.EndpointUrl, "--trust-all");

        var sample = await CallAsync(client, "sample", new()
        {
            ["nodes"] = new[] { "/Objects/OpcPlc/Telemetry/Basic/StepUp", "/Objects/OpcPlc/Telemetry/Basic/AlternatingBoolean" },
            ["seconds"] = 3,
            ["refreshMs"] = 100,
        });

        var items = sample["items"]!.AsArray();
        var step = items.Single(i => (string)i!["name"]! == "StepUp")!;
        Assert.True((int)step["changes"]! >= 2, step.ToJsonString());
        Assert.True((double)step["max"]! > (double)step["min"]!);
        Assert.Equal("Good", (string)step["lastStatus"]!);
        var toggle = items.Single(i => (string)i!["name"]! == "AlternatingBoolean")!;
        Assert.True((int)toggle["samples"]! >= 1);
    }

    [Fact]
    public async Task Only_configured_endpoints_can_be_used()
    {
        var client = await StartAsync("--endpoint", plc.EndpointUrl, "--endpoint", "mqtt://localhost:1", "--trust-all");

        Assert.Contains("Pass the endpoint: one of", await ErrorAsync(client, "read", new() { ["nodes"] = new[] { "ns=3;s=StepUp" } }), StringComparison.Ordinal);
        Assert.Contains("is not one of the configured endpoints", await ErrorAsync(client, "read", new()
        {
            ["nodes"] = new[] { "ns=3;s=StepUp" },
            ["endpoint"] = "opc.tcp://somewhere-else:4840",
        }), StringComparison.Ordinal);
        Assert.Contains("'Nope' not found", await ErrorAsync(client, "read", new()
        {
            ["nodes"] = new[] { "/Objects/Nope" },
            ["endpoint"] = plc.EndpointUrl,
        }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_to_start_without_a_machine()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr).Parse(["mcp"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("--endpoint <url>, --session <file>, --recording <file.db> or --recordings-dir <folder>", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recordings_can_be_listed_and_queried_and_other_files_are_refused()
    {
        var file = Path.Combine(_dir, "line1.db");
        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            var recorded = await Commands.Build(stdout, stderr)
                .Parse(["monitor", plc.EndpointUrl, "/Objects/OpcPlc/Telemetry/Basic/StepUp", "-r", "100", "-d", "2s", "--trust-all", "--record", file])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, recorded);
        }

        var client = await StartAsync("--recording", file);   // recordings only: no machine tools
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["list_recordings", "recording_items", "recording_samples"], tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, t.Name));

        var files = (await CallAsync(client, "list_recordings", [])).AsArray();
        Assert.Equal("line1.db", (string)files[0]!["file"]!);
        var items = (await CallAsync(client, "recording_items", [])).AsArray();
        var step = items.Single(i => (string)i!["name"]! == "StepUp")!;
        Assert.True((long)step["samples"]! >= 5);
        Assert.True((double)step["max"]! > (double)step["min"]!);

        var buckets = await CallAsync(client, "recording_samples", new() { ["items"] = new[] { "StepUp" }, ["bucketSeconds"] = 1 });
        Assert.NotEmpty(buckets["buckets"]!.AsArray());
        Assert.Contains("is not one of the recording files", await ErrorAsync(client, "recording_items", new() { ["file"] = "/etc/hosts" }), StringComparison.Ordinal);
    }
}
