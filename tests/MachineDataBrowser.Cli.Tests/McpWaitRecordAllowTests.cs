using System.Text.Json.Nodes;
using MachineDataBrowser.Cli.Mcp;
using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Tests;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Opc.Ua;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>Endpoint patterns, wait_for and background recordings through a real <c>mdbrowser mcp</c> process.</summary>
public sealed class McpWaitRecordAllowTests(OpcPlcFixture plc) : IAsyncLifetime
{
    private const string StepUp = "/Objects/OpcPlc/Telemetry/Basic/StepUp";
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-mcp-b").FullName;
    private McpClient _client = null!;

    private string RecordInto => Path.Combine(_dir, "recordings");

    public async ValueTask InitializeAsync()
    {
        // No fixed endpoint: the agent names it, and it is allowed by the pattern.
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mdbrowser",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "mdbrowser.dll"), "mcp", "--allow", "opc.tcp://localhost:*", "--allow-recording", RecordInto],
            EnvironmentVariables = new Dictionary<string, string?> { ["MACHINEDATABROWSER_DATA_DIR"] = _dir },
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<JsonNode> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.False(result.IsError ?? false, text);
        return JsonNode.Parse(text)!;
    }

    private async Task<string> ErrorAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    [Fact]
    public async Task Endpoints_matching_a_pattern_can_be_named_others_are_refused()
    {
        var endpoints = (await CallAsync("list_endpoints", [])).AsArray();
        Assert.Equal("opc.tcp://localhost:*", (string)Assert.Single(endpoints)!["allowedPattern"]!);

        var read = await CallAsync("read", new() { ["nodes"] = new[] { StepUp }, ["endpoint"] = plc.EndpointUrl });
        Assert.Equal("StepUp", (string)read["values"]![0]!["name"]!);
        Assert.Contains("matching the allowed patterns", await ErrorAsync("read", new() { ["nodes"] = new[] { StepUp }, ["endpoint"] = "opc.tcp://10.0.0.1:4840" }), StringComparison.Ordinal);
        Assert.Contains("Name the endpoint", await ErrorAsync("read", new() { ["nodes"] = new[] { StepUp } }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_for_returns_when_met_or_at_the_timeout()
    {
        var changed = await CallAsync("wait_for", new() { ["node"] = StepUp, ["condition"] = "changes", ["timeoutSeconds"] = 15, ["endpoint"] = plc.EndpointUrl });
        Assert.True((bool)changed["met"]!);

        var never = await CallAsync("wait_for", new() { ["node"] = StepUp, ["condition"] = "< 0", ["timeoutSeconds"] = 2, ["endpoint"] = plc.EndpointUrl });
        Assert.False((bool)never["met"]!);
        Assert.InRange((double)never["waitedSeconds"]!, 1.5, 15);

        Assert.Contains("is not a condition", await ErrorAsync("wait_for", new() { ["node"] = StepUp, ["condition"] = "about 5", ["endpoint"] = plc.EndpointUrl }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Background_recordings_write_into_the_folder_and_can_be_read_while_running()
    {
        var started = await CallAsync("start_recording", new()
        {
            ["name"] = "../line 1",
            ["nodes"] = new[] { "/Objects/OpcPlc/Telemetry/Basic" },
            ["recursive"] = true,
            ["durationMinutes"] = 5,
            ["refreshMs"] = 200,
            ["endpoint"] = plc.EndpointUrl,
        });
        var id = (string)started["id"]!;
        Assert.Equal("line_1.db", (string)started["file"]!);   // the name can't leave the folder
        Assert.True(File.Exists(Path.Combine(RecordInto, "line_1.db")));

        await Task.Delay(1500, TestContext.Current.CancellationToken);
        var active = (await CallAsync("active_recordings", [])).AsArray();
        Assert.Equal("Recording", (string)Assert.Single(active)!["state"]!);

        var items = (await CallAsync("recording_items", new() { ["file"] = "line_1.db" })).AsArray();
        Assert.Contains(items, i => (string)i!["name"]! == "StepUp" && (long)i["samples"]! > 0);

        var stopped = await CallAsync("stop_recording", new() { ["id"] = id });
        Assert.Equal("Stopped", (string)stopped["state"]!);
        Assert.Empty((await CallAsync("active_recordings", [])).AsArray());
    }

    [Theory]
    [InlineData("> 80", "81", 81.0, true)]
    [InlineData(">= 80", "80", 80.0, true)]
    [InlineData("< 10", "12", 12.0, false)]
    [InlineData("== Run", "run", null, true)]
    [InlineData("!= 0", "0", 0.0, false)]
    [InlineData("contains error", "Motor Error 12", null, true)]
    public void Conditions_compare_numbers_as_numbers_and_text_as_text(string condition, string value, double? numeric, bool met)
    {
        var update = new ValueUpdate(NodeId.Null, value, StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow, numeric);
        Assert.Equal(met, WaitCondition.Parse(condition).IsMet(update, new ValueUpdate(NodeId.Null, "first", StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow)));
    }

    [Fact]
    public async Task Recording_needs_a_machine()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr).Parse(["mcp", "--allow-recording", Path.Combine(_dir, "x")]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        Assert.Contains("--allow-recording needs a machine", stderr.ToString(), StringComparison.Ordinal);
        Assert.Throws<McpException>(() => RecordingControlTools.SafeName("../"));
    }
}
