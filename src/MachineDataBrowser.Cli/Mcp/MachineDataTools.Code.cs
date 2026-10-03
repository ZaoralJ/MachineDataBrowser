using System.ComponentModel;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Opc.Ua;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>Code for a machine's structure: C# classes or records mirroring it, or a JSON snapshot of its values.</summary>
internal sealed partial class MachineDataTools
{
    public const int MaxCodeNodes = 2000;

    [McpServerTool(Name = "generate_code", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Generates code for a node and everything below it, like Copy as C# in the app: 'csharp-class' or 'csharp-record' "
        + "(types mirroring the structure, with the current values' types and node ids in comments) or 'json' (a snapshot of the "
        + "current values). Use it to write client code for a machine.")]
    public Task<string> GenerateCodeAsync(
        [Description("Node: a path like /Objects/Line1 or an id")] string node,
        [Description("'csharp-record', 'csharp-class' or 'json'")] string format = "csharp-record",
        [Description("Levels below the node, 1-10")] int depth = 5,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => GuardText(async () =>
    {
        var kind = format.Trim().ToLowerInvariant();
        if (kind is not ("csharp-record" or "csharp-class" or "json"))
        {
            throw new McpException("format must be 'csharp-record', 'csharp-class' or 'json'.");
        }

        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var root = await Connection.ResolveAsync(client, node, cancellationToken).ConfigureAwait(false);
        var attributes = await client.ReadAttributesAsync(root.Id, cancellationToken).ConfigureAwait(false);
        var nodeClass = Enum.TryParse<NodeClass>(attributes.FirstOrDefault(a => a.Name == "NodeClass")?.Value, out var parsed) ? parsed : NodeClass.Object;
        var tree = await client.ReadTreeAsync(root.Id, root.DisplayName ?? root.Name, nodeClass, Math.Clamp(depth, 1, 10), MaxCodeNodes, cancellationToken).ConfigureAwait(false);
        return kind == "json"
            ? NodeExport.ToJsonString(tree)
            : NodeExport.ToCSharp(tree, asRecord: kind == "csharp-record", formatId: client.ToDisplayId);
    });

    /// <summary>Like <see cref="Guard"/>, for tools whose result is text (code) rather than JSON.</summary>
    private static async Task<string> GuardText(Func<Task<string>> tool)
    {
        try
        {
            return await tool().ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (ServiceResultException ex)
        {
            throw new McpException($"{StatusText.Of(ex.Result.StatusCode)}: {ex.Message}");
        }
    }
}
