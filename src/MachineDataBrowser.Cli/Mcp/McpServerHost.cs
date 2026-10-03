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

        For context: attributes gives a node's data type, description, access and engineering unit (check it before
        interpreting a number); history reads what an OPC UA server stored over a time range (bucketSeconds for long
        ranges); alarms lists the server's current alarms, events collects events for some seconds; diagnostics shows
        whether the connection is healthy when values look stale.
        """;

    public const string RecordingInstructions = """
        Recorded history is available from SQLite recording files. Start with list_recordings, then recording_items for
        per-item statistics (count, time range, min/max/average, last value). recording_samples returns samples of
        chosen items over a time range (ISO 8601, UTC); for long ranges pass bucketSeconds to get per-bucket count,
        min, max, average and the number of samples that were not Good, instead of raw samples.
        """;

    public const string PatternInstructions = """
        Besides the listed endpoints, the server may connect to endpoints the user names that match its allowed patterns
        (list_endpoints shows them); pass such an endpoint URL in each call. They connect without credentials.
        """;

    public const string RecordingControlInstructions = """
        start_recording records variables in the background into a SQLite file (stops after durationMinutes or with
        stop_recording; active_recordings lists them); analyse the file with recording_items and recording_samples.
        wait_for blocks until a value meets a condition (or times out); use it to follow a process instead of polling.
        """;

    public const string WriteInstructions = """
        write and call_method change machines: use them only when the user asked for that change. Each change is put to
        the user for confirmation first; if they decline, nothing happens. Read the value (or the method's arguments)
        before, and report what changed. Endpoints named under allow patterns and read-only sessions can't be changed.
        """;

    /// <summary>What the server offers, from the <c>mcp</c> command's options.</summary>
    public sealed record Setup(
        EndpointPool Pool,
        RecordingLibrary Recordings,
        IReadOnlyList<string> SessionFiles,
        string? RecordingFolder = null,
        bool AllowWrites = false,
        bool SkipWriteConfirmation = false);

    public static async Task<int> RunAsync(Setup setup, CancellationToken cancellationToken)
    {
        var (pool, recordings, sessionFiles, recordingFolder, allowWrites, skipWriteConfirmation) = setup;
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
                pool.HasMachines ? Instructions : null,
                pool.Patterns.Count > 0 ? PatternInstructions : null,
                recordings.IsEmpty ? null : RecordingInstructions,
                pool.HasMachines && recordingFolder is not null ? RecordingControlInstructions : null,
                allowWrites && pool.Endpoints.Count > 0 ? WriteInstructions : null,
            }.OfType<string>()),
            ToolCollection = [],
            ResourceCollection = [],
            PromptCollection = [],
        };

        var resources = new McpResources(pool, [.. sessionFiles.Select(Path.GetFullPath)], recordings);
        foreach (var method in typeof(McpResources).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerResourceAttribute>() is { } attribute
                && (attribute.Name != "session" || sessionFiles.Count > 0) && (attribute.Name != "recordings" || !recordings.IsEmpty)))
        {
            options.ResourceCollection.Add(McpServerResource.Create(method, resources));
        }

        foreach (var method in typeof(McpPrompts).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() is { } attribute
                && (attribute.Name == "summarize_recording" ? !recordings.IsEmpty : pool.HasMachines)))
        {
            options.PromptCollection.Add(McpServerPrompt.Create(method, target: null));
        }

        if (pool.HasMachines)
        {
            AddTools(options, new MachineDataTools(pool));
        }

        if (!recordings.IsEmpty)
        {
            AddTools(options, new RecordingTools(recordings));
        }

        // Changes only with --allow-writes, and only to configured endpoints (named ones stay read-only).
        if (allowWrites && pool.Endpoints.Count > 0)
        {
            AddTools(options, new WriteTools(pool, skipWriteConfirmation));
        }

        // Background recordings stop (and close their files) when the server exits.
        await using var control = pool.HasMachines && recordingFolder is not null ? new RecordingControlTools(pool, recordingFolder) : null;
        if (control is not null)
        {
            AddTools(options, control);
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
