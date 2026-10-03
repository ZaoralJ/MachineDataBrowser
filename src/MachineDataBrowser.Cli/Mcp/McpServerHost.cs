using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary><c>mdbrowser mcp</c>: an MCP server on stdin/stdout that gives AI agents read-only access to the configured machines.</summary>
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

    public static async Task<int> RunAsync(EndpointPool pool, CancellationToken cancellationToken)
    {
        var tools = new MachineDataTools(pool);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "mdbrowser",
                Title = "Machine Data Browser",
                Version = typeof(McpServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
                WebsiteUrl = "https://zaoralj.github.io/MachineDataBrowser/",
            },
            ServerInstructions = Instructions,
            ToolCollection = [],
        };

        foreach (var method in typeof(MachineDataTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            options.ToolCollection.Add(McpServerTool.Create(method, tools));
        }

        await using var transport = new StdioServerTransport(options);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
