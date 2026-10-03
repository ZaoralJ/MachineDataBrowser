using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// <c>mdbrowser mcp</c>: an MCP server on stdin/stdout that gives AI agents read-only access to the configured machines
/// and recording files. Machine tools are offered when endpoints are configured, recording tools when recordings are.
/// </summary>
internal static class McpServerHost
{
    public const string Instructions = """
        Read-only access to industrial machines over OPC UA, EtherNet/IP (Allen-Bradley Logix) and MQTT (Sparkplug B,
        CloudEvents). Nothing can be written or called.

        Start with list_endpoints, then browse from the root ("/") a level or two at a time, or search by name.
        Nodes are paths of display names from the root, like /Objects/Line1/Speed, "/Controller Tags/Motor.Speed",
        /Topics/plant/line1/status, or ids like ns=3;s=Speed (OPC UA), Program:Main.Speed (Logix). Paths from browse and
        search can be passed to read and sample as they are.

        read gives current values; sample watches values for some seconds and summarises them (changes, min/max/mean,
        statuses), which is how to judge behaviour over time. recursive=true expands a folder or structure to every
        variable below it. Status other than Good means the value is not reliable (Bad: no value, Uncertain: questionable).
        Results are limited in size; narrow the node or depth when a result says truncated.
        """;

    public const string RecordingInstructions = """
        Recorded history is available from SQLite recording files. Start with list_recordings, then recording_items for
        per-item statistics (count, time range, min/max/average, last value). recording_samples returns samples of
        chosen items over a time range (ISO 8601, UTC); for long ranges pass bucketSeconds to get per-bucket count,
        min, max, average and the number of samples that were not Good, instead of raw samples.
        """;

    public static async Task<int> RunAsync(EndpointPool pool, RecordingLibrary recordings, CancellationToken cancellationToken)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "mdbrowser",
                Title = "Machine Data Browser",
                Version = typeof(McpServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
                WebsiteUrl = "https://zaoralj.github.io/MachineDataBrowser/",
            },
            ServerInstructions = string.Join("\n", new[]
            {
                pool.Endpoints.Count > 0 ? Instructions : null,
                recordings.IsEmpty ? null : RecordingInstructions,
            }.OfType<string>()),
            ToolCollection = [],
        };

        if (pool.Endpoints.Count > 0)
        {
            AddTools(options, new MachineDataTools(pool));
        }

        if (!recordings.IsEmpty)
        {
            AddTools(options, new RecordingTools(recordings));
        }

        await using var transport = new StdioServerTransport(options);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static void AddTools(McpServerOptions options, object tools)
    {
        foreach (var method in tools.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            options.ToolCollection!.Add(McpServerTool.Create(method, tools));
        }
    }
}
