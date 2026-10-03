using MachineDataBrowser.Core;
using Opc.Ua;

namespace MachineDataBrowser.Cli;

/// <summary>Finding nodes from command-line or tool arguments; shared by the commands and the MCP server.</summary>
internal static class NodeQueries
{
    public static async Task<List<Node>> ResolveAllAsync(IDeviceClient client, IEnumerable<string> texts, CancellationToken cancellationToken)
    {
        var result = new List<Node>();
        foreach (var text in texts)
        {
            result.Add(await Cli.Connection.ResolveAsync(client, text, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>The app's "Monitor folder" limits: levels below a node and variables in total.</summary>
    public const int DefaultRecursiveDepth = 10;

    public const int DefaultMaxItems = 500;

    /// <summary>
    /// Every variable below each node (OPC UA: a variable's own properties, like EURange, are left out; MQTT: JSON fields
    /// are included). Names are the path below
    /// the node given, so equal names in different folders stay apart. A variable given directly is kept as it is, except
    /// an MQTT topic with JSON fields.
    /// </summary>
    public static async Task<List<Node>> ExpandAsync(IDeviceClient client, IEnumerable<string> texts, int depth, int maxItems, Func<string, Task> warn, CancellationToken cancellationToken)
    {
        var result = new List<Node>();
        var seen = new HashSet<NodeId>();

        // MQTT: a JSON topic's fields are what you want to watch. OPC UA: a variable's children are properties (EURange …).
        var descendIntoVariables = client is IDynamicAddressSpace;
        foreach (var root in await ResolveAllAsync(client, texts, cancellationToken).ConfigureAwait(false))
        {
            if (result.Count >= maxItems)
            {
                break;
            }

            // A variable given directly is watched itself (OPC UA: collecting below it would return its properties);
            // an MQTT topic with a JSON payload expands to its fields.
            var attributes = await client.ReadAttributesAsync(root.Id, cancellationToken).ConfigureAwait(false);
            var isVariable = attributes.Any(a => a.Name == "NodeClass" && a.Value == nameof(NodeClass.Variable));
            var found = isVariable && !descendIntoVariables
                ? []
                : await client.CollectVariablesWithPathsAsync(root.Id, depth, maxItems - result.Count, descendIntoVariables, cancellationToken).ConfigureAwait(false);
            if (isVariable && found.Count == 0)
            {
                if (seen.Add(root.Id))
                {
                    result.Add(root);
                }

                continue;
            }

            if (found.Count == 0)
            {
                await warn($"mdbrowser: no variables within {depth} level(s) below {root.Name}.").ConfigureAwait(false);
                continue;
            }

            foreach (var (item, path) in found.Where(f => seen.Add(f.Item.NodeId)))
            {
                result.Add(new Node(item.NodeId, path.Length == 0 ? item.DisplayName : $"{path}/{item.DisplayName}", client.ToDisplayId(item.NodeId)));
            }
        }

        if (result.Count == 0)
        {
            throw new CliException("No variables found; try a larger --depth.");
        }

        if (result.Count >= maxItems)
        {
            await warn($"mdbrowser: limited to the first {maxItems} variables (--max-items).").ConfigureAwait(false);
        }

        return result;
    }

    public static async Task BrowseAsync(IDeviceClient client, NodeId parent, string path, int levels, List<IReadOnlyList<string>> rows, CancellationToken cancellationToken)
    {
        foreach (var child in await client.BrowseAsync(parent, cancellationToken).ConfigureAwait(false))
        {
            var childPath = $"{path}/{child.DisplayName}";
            rows.Add([childPath, child.NodeClass.ToString(), client.ToDisplayId(child.NodeId)]);
            if (levels > 1 && child.HasChildren)
            {
                await BrowseAsync(client, child.NodeId, childPath, levels - 1, rows, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
