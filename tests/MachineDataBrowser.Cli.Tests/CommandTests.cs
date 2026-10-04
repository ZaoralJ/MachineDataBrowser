using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class CommandTests(OpcPlcFixture plc, MqttSimulatorFixture broker) : IDisposable
{
    private const string StepUp = "/Objects/OpcPlc/Telemetry/Basic/StepUp";

    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(IAnsiConsole? terminal, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr, terminal).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static Task<(int Exit, string Out, string Err)> RunAsync(params string[] args) => RunAsync(null, args);

    [Fact]
    public async Task Endpoints_lists_the_security_modes_of_an_OPC_UA_server()
    {
        var (exit, output, _) = await RunAsync("endpoints", plc.EndpointUrl, "-f", "json");

        Assert.Equal(0, exit);
        var endpoints = JsonNode.Parse(output)!.AsArray();
        Assert.Contains(endpoints, e => (string)e!["mode"]! == "None");
        Assert.Contains(endpoints, e => (string)e!["mode"]! == "SignAndEncrypt");
    }

    [Fact]
    public async Task Browse_prints_full_paths_that_read_accepts()
    {
        var (exit, output, _) = await RunAsync("browse", plc.EndpointUrl, "/Objects/OpcPlc/Telemetry/Basic", "--trust-all", "-f", "csv");

        Assert.Equal(0, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("path,class,id", lines[0]);
        Assert.Contains($"{StepUp},Variable,ns=3;s=StepUp", lines);
    }

    [Fact]
    public async Task Read_by_path_and_by_id_gives_typed_json_values()
    {
        var (exit, output, _) = await RunAsync("read", plc.EndpointUrl, StepUp, "nsu=http://microsoft.com/Opc/OpcPlc/;s=AlternatingBoolean", "--trust-all", "-f", "json");

        Assert.Equal(0, exit);
        var values = JsonNode.Parse(output)!.AsArray();
        Assert.Equal("UInt32", (string)values[0]!["type"]!);
        Assert.Equal(JsonValueKind.Number, values[0]!["value"]!.GetValueKind());
        Assert.Equal("Boolean", (string)values[1]!["type"]!);
    }

    [Fact]
    public async Task Ids_are_shown_with_the_switch_and_when_names_repeat()
    {
        var (_, plain, _) = await RunAsync("read", plc.EndpointUrl, StepUp, "--trust-all", "-f", "csv");
        Assert.StartsWith("name,type,value", plain, StringComparison.Ordinal);

        var (_, withIds, _) = await RunAsync("read", plc.EndpointUrl, StepUp, "--trust-all", "-f", "csv", "--ids");
        Assert.StartsWith("name,id,type,value", withIds, StringComparison.Ordinal);
        Assert.Contains("StepUp,ns=3;s=StepUp,", withIds, StringComparison.Ordinal);

        // The same name twice: ids tell them apart without the switch.
        var (_, repeated, _) = await RunAsync("read", plc.EndpointUrl, StepUp, StepUp, "--trust-all");
        Assert.Contains("ns=3;s=StepUp", repeated.Split('\n')[1], StringComparison.Ordinal);

        var (_, lines, _) = await RunAsync("monitor", plc.EndpointUrl, StepUp, "--trust-all", "-n", "1", "-r", "100", "--ids");
        Assert.Contains("ns=3;s=StepUp", lines, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_are_one_line_on_stderr_with_exit_code_1()
    {
        var (exit, output, error) = await RunAsync("read", plc.EndpointUrl, "/Objects/Nope", "--trust-all");

        Assert.Equal(1, exit);
        Assert.Empty(output);
        Assert.Equal("mdbrowser: 'Nope' not found under 'Objects' (path /Objects/Nope).", error.Trim());
    }

    [Fact]
    public async Task Monitor_stops_after_count_and_writes_json_lines()
    {
        var (exit, output, _) = await RunAsync("monitor", plc.EndpointUrl, StepUp, "--trust-all", "-n", "3", "-r", "100", "-f", "json");

        Assert.Equal(0, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line =>
        {
            var update = JsonNode.Parse(line)!;
            Assert.Equal("StepUp", (string)update["name"]!);
            Assert.Equal("Good", (string)update["status"]!);
        });
    }

    [Fact]
    public async Task Monitor_stops_after_duration()
    {
        var started = DateTime.UtcNow;
        var (exit, output, _) = await RunAsync("monitor", plc.EndpointUrl, StepUp, "--trust-all", "-d", "1500ms");

        Assert.Equal(0, exit);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(20));
        Assert.Contains("StepUp", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_monitors_a_session_file_with_per_item_refresh_and_skips_unknown_items()
    {
        var session = Path.Combine(_dir, "line1.mdbsession");
        await File.WriteAllTextAsync(session, $$"""
            {"endpointUrl":"{{plc.EndpointUrl}}","autoAcceptCertificates":true,"defaultRefreshMs":200,
             "watch":[{"nodeId":"nsu=http://microsoft.com/Opc/OpcPlc/;s=StepUp","displayName":"Step"},
                      {"nodeId":"nsu=urn:unknown;s=X","displayName":"Ghost"}],
             "groupWatchByPath":true}
            """, TestContext.Current.CancellationToken);

        var (exit, output, error) = await RunAsync("run", session, "-n", "2", "-f", "csv");

        Assert.Equal(0, exit);
        Assert.Contains("skipped Ghost", error, StringComparison.Ordinal);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("time,name,id,status,value", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.Contains(",Step,ns=3;s=StepUp,Good,", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mqtt_paths_resolve_once_topics_have_arrived()
    {
        var (exit, output, _) = await RunAsync("monitor", broker.EndpointUrl, "/Topics/machines/m1/status/speed", "-n", "2", "-f", "json");

        Assert.Equal(0, exit);
        Assert.All(output.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.Equal(JsonValueKind.Number, JsonNode.Parse(line)!["value"]!.GetValueKind()));
    }

    [Fact]
    public async Task A_terminal_gets_a_table_and_a_tree_but_json_stays_plain()
    {
        var console = new TestConsole().Width(140);
        var (exit, _, _) = await RunAsync(console, "read", plc.EndpointUrl, StepUp, "--trust-all");
        Assert.Equal(0, exit);
        Assert.Contains("│ StepUp │ UInt32 │", console.Output, StringComparison.Ordinal);

        console = new TestConsole().Width(140);
        (exit, _, _) = await RunAsync(console, "browse", plc.EndpointUrl, "/Objects/OpcPlc/Telemetry/Basic", "--trust-all");
        Assert.Equal(0, exit);
        Assert.Contains("└── ● RandomUnsignedInt32  ns=3;s=RandomUnsignedInt32", console.Output, StringComparison.Ordinal);

        console = new TestConsole();
        var (_, json, _) = await RunAsync(console, "read", plc.EndpointUrl, StepUp, "--trust-all", "-f", "json");
        Assert.Empty(console.Output);
        Assert.StartsWith("[", json.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_view_shows_one_row_per_item_with_its_latest_value()
    {
        var console = new TestConsole().Width(140).Interactive();
        var (exit, _, _) = await RunAsync(console, "monitor", plc.EndpointUrl, StepUp, "/Objects/OpcPlc/Telemetry/Basic/AlternatingBoolean", "--trust-all", "-n", "6", "-r", "100");

        Assert.Equal(0, exit);
        var lastFrame = console.Output[console.Output.LastIndexOf('╭')..];
        Assert.Contains("│ StepUp ", lastFrame, StringComparison.Ordinal);
        Assert.Contains("│ AlternatingBoolean ", lastFrame, StringComparison.Ordinal);
        Assert.Contains("Good", lastFrame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recursive_expands_folders_to_their_variables_named_by_path()
    {
        // Not the exit code: some of opc-plc's simulated variables (anomalies, special values) have no value at times, and
        // read then rightly exits 1. This test is about which variables the folder expands to and how they are named.
        var (_, output, _) = await RunAsync("read", plc.EndpointUrl, "/Objects/OpcPlc/Telemetry", "-R", "--trust-all", "-f", "csv");

        var names = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).Select(l => l.Split(',')[0]).ToList();
        Assert.Contains("Basic/StepUp", names);
        Assert.Contains("Basic/AlternatingBoolean", names);
        Assert.Contains(names, n => n.StartsWith("Fast/", StringComparison.Ordinal));
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public async Task Recursive_respects_depth_and_max_items_and_keeps_variables_given_directly()
    {
        var (exit, _, error) = await RunAsync("read", plc.EndpointUrl, "/Objects/OpcPlc/Telemetry", "-R", "--depth", "1", "--trust-all");
        Assert.Equal(1, exit);
        Assert.Contains("try a larger --depth", error, StringComparison.Ordinal);

        (exit, var output, error) = await RunAsync("read", plc.EndpointUrl, "/Objects/OpcPlc/Telemetry", "-R", "--max-items", "3", "--trust-all", "-f", "csv");
        Assert.Contains("limited to the first 3 variables", error, StringComparison.Ordinal);
        Assert.Equal(4, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        (exit, output, _) = await RunAsync("read", plc.EndpointUrl, StepUp, "-R", "--trust-all", "-f", "csv");
        Assert.Equal(0, exit);
        Assert.StartsWith("StepUp,UInt32,", output.Split('\n')[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recursive_monitor_streams_every_variable_of_a_folder()
    {
        var (exit, output, _) = await RunAsync("monitor", plc.EndpointUrl, "/Objects/OpcPlc/Telemetry/Basic", "-R", "--trust-all", "-n", "12", "-r", "100", "-f", "json");

        Assert.Equal(0, exit);
        var names = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (string)JsonNode.Parse(l)!["name"]!).ToHashSet();
        Assert.Superset(new HashSet<string> { "StepUp", "AlternatingBoolean", "RandomSignedInt32", "RandomUnsignedInt32" }, names);
        Assert.True(names.Count >= 3, string.Join(", ", names));
    }

    [Fact]
    public async Task Recursive_on_mqtt_includes_json_fields()
    {
        // The simulator's payload has "operator": null, which read reports as unreadable (exit 1); the fields are there.
        var (_, output, _) = await RunAsync("read", broker.EndpointUrl, "/Topics/machines/m1/status", "-R", "-f", "csv");

        Assert.Contains(output.Split('\n'), l => l.StartsWith("speed,Double,", StringComparison.Ordinal));
        Assert.Contains(output.Split('\n'), l => l.StartsWith("temperature/bearing,Double,", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("1500ms", 1_500)]
    [InlineData("5m", 300_000)]
    [InlineData("8h", 28_800_000)]
    [InlineData("00:01:00", 60_000)]
    public void Durations_accept_units_and_time_spans(string text, double milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), Commands.ParseDuration(text));
}
