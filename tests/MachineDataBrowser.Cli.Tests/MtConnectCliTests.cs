using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>The CLI and the MCP server against the MTConnect agent simulator.</summary>
public sealed class MtConnectCliTests(MtConnectSimulatorFixture agent) : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-mtconnect").FullName;
    private McpClient? _mcp;

    public async ValueTask DisposeAsync()
    {
        if (_mcp is not null)
        {
            await _mcp.DisposeAsync();
        }

        Directory.Delete(_dir, recursive: true);
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr, null).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Browse_lists_devices_and_component_paths()
    {
        var (exit, output, _) = await RunAsync("browse", agent.EndpointUrl, "/VMC/base/X", "-f", "csv");

        Assert.Equal(0, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("path,class,id", lines[0]);
        Assert.Contains("/VMC/base/X/Xpos,Variable,vmc_x_pos", lines);
    }

    [Fact]
    public async Task Read_by_path_and_by_data_item_id_gives_typed_values()
    {
        var (exit, output, _) = await RunAsync("read", agent.EndpointUrl, "/VMC/base/X/Xpos", "vmc_part_count", "/VMC/controller/path/execution", "-f", "json");

        Assert.Equal(0, exit);
        var values = JsonNode.Parse(output)!.AsArray();
        Assert.Equal("Double", (string)values[0]!["type"]!);
        Assert.Equal(JsonValueKind.Number, values[0]!["value"]!.GetValueKind());
        Assert.Equal("Int64", (string)values[1]!["type"]!);
        Assert.Equal(JsonValueKind.String, values[2]!["value"]!.GetValueKind());
    }

    [Fact]
    public async Task Monitor_streams_every_observation()
    {
        var (exit, output, _) = await RunAsync("monitor", agent.EndpointUrl, "/VMC/base/X/Xpos", "-n", "5", "-f", "csv");

        Assert.Equal(0, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(6, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.Contains(",Xpos,vmc_x_pos,Good,", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Endpoints_and_write_explain_what_mtconnect_does_not_offer()
    {
        var (exit, _, error) = await RunAsync("endpoints", agent.EndpointUrl);
        Assert.NotEqual(0, exit);
        Assert.Contains("OPC UA", error, StringComparison.Ordinal);

        (exit, var output, _) = await RunAsync("write", agent.EndpointUrl, "/VMC/base/X/Xpos", "1", "--yes");
        Assert.NotEqual(0, exit);
        Assert.Contains("not supported", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mcp_lists_the_protocol_and_reads_values_and_history()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mdbrowser",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "mdbrowser.dll"), "mcp", "--endpoint", agent.EndpointUrl],
            EnvironmentVariables = new Dictionary<string, string?> { ["MACHINEDATABROWSER_DATA_DIR"] = _dir },
        });
        _mcp = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);

        var endpoints = (await CallAsync("list_endpoints", [])).AsArray();
        Assert.Equal("MTConnect", (string)Assert.Single(endpoints)!["protocol"]!);

        var read = await CallAsync("read", new() { ["nodes"] = new[] { "/VMC/base/S" }, ["recursive"] = true });
        Assert.Contains(read["values"]!.AsArray(), v => (string)v!["name"]! == "Sspeed");

        var attributes = await CallAsync("attributes", new() { ["node"] = "/VMC/base/S/Sspeed" });
        Assert.Contains("rpm", attributes.ToJsonString(), StringComparison.Ordinal);

        var now = DateTime.UtcNow;
        var history = (await CallAsync("history", new()
        {
            ["nodes"] = new[] { "/VMC/base/X/Xpos" },
            ["from"] = now.AddSeconds(-2).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["to"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }))["items"]![0]!;
        Assert.True((int)history["values"]! > 5, history.ToJsonString());
    }

    private async Task<JsonNode> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _mcp!.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.False(result.IsError ?? false, text);
        return JsonNode.Parse(text)!;
    }
}
