using Opc.Ua;
using OpcUaBrowser.Core.Ua;

namespace OpcUaBrowser.Core;

/// <summary>
/// A connection to one device (OPC UA server, Logix controller or MQTT broker). The OPC UA information model
/// (<see cref="NodeId"/>, <see cref="NodeClass"/>, <see cref="StatusCode"/>) is the common vocabulary: other
/// protocols map their items onto it, so the tree, watch list, recordings and exports work unchanged.
/// </summary>
public interface IDeviceClient : IAsyncDisposable
{
    event EventHandler<ConnectionState>? StateChanged;

    ConnectionState State { get; }

    /// <summary>Identity of the connected device, for display.</summary>
    string? ServerUri { get; }

    /// <summary>Top of the browse tree.</summary>
    BrowseItem Root { get; }

    /// <summary>Id stable across reconnects/restarts; used for anything persisted.</summary>
    string ToPortableId(NodeId nodeId);

    NodeId ParsePortableId(string text);

    /// <summary>Id as shown to people: in the tree, watch list, clipboard and generated code.</summary>
    string ToDisplayId(NodeId nodeId);

    Task ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    Task<IReadOnlyList<BrowseItem>> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>Ids from <see cref="Root"/> down to <paramref name="nodeId"/> (inclusive); empty when unknown.</summary>
    Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>Current values of variables; <c>null</c> where the read failed.</summary>
    Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the Value attribute of a variable, parsing <paramref name="text"/> into the variable's data type.
    /// Arrays are written as comma-separated elements, optionally in brackets.
    /// </summary>
    Task WriteValueAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Writing values is not supported for this connection type."));

    /// <summary>
    /// Monitors many values; items with the same refresh time are sampled together.
    /// Rejected items are returned with <see cref="MonitorResult.Error"/> set and no handle.
    /// </summary>
    Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<NodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default);

    /// <summary>Re-monitors items at <paramref name="refreshMs"/>; handles not created by this client are ignored.</summary>
    Task<IReadOnlyList<MonitorResult>> ChangeRefreshAsync(
        IReadOnlyList<IAsyncDisposable> monitors,
        Action<ValueUpdate> onUpdate,
        double refreshMs,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by clients whose address space grows while connected (MQTT: topics appear as messages arrive).
/// The app re-browses expanded tree nodes when this is raised.
/// </summary>
public interface IDynamicAddressSpace
{
    event EventHandler? AddressSpaceChanged;
}

public static class DeviceClient
{
    /// <summary>URL scheme of EtherNet/IP (Logix) endpoints: <c>eip://host[:port][/path]</c>, path defaults to <c>1,0</c>.</summary>
    public const string EipScheme = "eip";

    public static bool IsEip(string endpointUrl) =>
        endpointUrl.TrimStart().StartsWith(EipScheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>MQTT endpoints: <c>mqtt://</c>, <c>mqtts://</c>, <c>ws://</c>, <c>wss://</c>.</summary>
    public static bool IsMqtt(string endpointUrl) => Mqtt.MqttEndpoint.IsMqtt(endpointUrl);

    /// <summary>A new, disconnected client for the protocol of <paramref name="endpointUrl"/>.</summary>
    public static IDeviceClient Create(string endpointUrl) =>
        IsEip(endpointUrl) ? new Cip.CipClient() : IsMqtt(endpointUrl) ? new Mqtt.MqttDeviceClient() : new OpcUaClient();

    /// <summary>True when <paramref name="client"/> can connect to <paramref name="endpointUrl"/>.</summary>
    public static bool Supports(IDeviceClient client, string endpointUrl) => client switch
    {
        Cip.CipClient => IsEip(endpointUrl),
        Mqtt.MqttDeviceClient => IsMqtt(endpointUrl),
        _ => !IsEip(endpointUrl) && !IsMqtt(endpointUrl),
    };

    /// <summary>Stops monitor handles of any client, batching where the protocol allows it.</summary>
    public static async Task StopMonitoringAsync(IEnumerable<IAsyncDisposable> handles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handles);
        var list = handles.ToList();
        await OpcUaClient.StopMonitoringAsync(list, cancellationToken).ConfigureAwait(false);
        foreach (var handle in list.Where(h => !OpcUaClient.IsOwnHandle(h)))
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Protocol-independent operations built on <see cref="IDeviceClient"/>.</summary>
public static class DeviceClientExtensions
{
    /// <summary>Starts monitoring one value. Dispose the returned handle to stop.</summary>
    public static async Task<IAsyncDisposable> MonitorAsync(
        this IDeviceClient client,
        NodeId nodeId,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var result = (await client.MonitorManyAsync([nodeId], onUpdate, samplingIntervalMs, cancellationToken).ConfigureAwait(false))[0];
        return result.Handle ?? throw new ServiceResultException(result.Error);
    }

    /// <summary>
    /// Collects Variable nodes below <paramref name="nodeId"/>, breadth-first, up to <paramref name="maxDepth"/> levels
    /// and <paramref name="maxCount"/> results. Only objects are descended into, so a variable's own properties
    /// (EURange, EngineeringUnits, …) are not collected.
    /// </summary>
    public static Task<IReadOnlyList<BrowseItem>> CollectVariablesAsync(
        this IDeviceClient client,
        NodeId nodeId,
        int maxDepth,
        int maxCount,
        CancellationToken cancellationToken = default) =>
        client.CollectVariablesAsync(nodeId, maxDepth, maxCount, descendIntoVariables: false, cancellationToken);

    /// <summary>As the overload above; with <paramref name="descendIntoVariables"/> children of variables are collected too.</summary>
    public static async Task<IReadOnlyList<BrowseItem>> CollectVariablesAsync(
        this IDeviceClient client,
        NodeId nodeId,
        int maxDepth,
        int maxCount,
        bool descendIntoVariables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var found = new List<BrowseItem>();
        var visited = new HashSet<NodeId> { nodeId };
        var level = new List<NodeId> { nodeId };

        for (var depth = 0; depth < maxDepth && level.Count > 0 && found.Count < maxCount; depth++)
        {
            var next = new List<NodeId>();
            foreach (var parent in level)
            {
                foreach (var child in await client.BrowseAsync(parent, cancellationToken).ConfigureAwait(false))
                {
                    if (!visited.Add(child.NodeId))
                    {
                        continue;
                    }

                    if (child.NodeClass == NodeClass.Variable)
                    {
                        found.Add(child);
                        if (found.Count >= maxCount)
                        {
                            return found;
                        }
                    }

                    if (child.HasChildren && (child.NodeClass == NodeClass.Object || (descendIntoVariables && child.NodeClass == NodeClass.Variable)))
                    {
                        next.Add(child.NodeId);
                    }
                }
            }

            level = next;
        }

        return found;
    }

    /// <summary>
    /// Browses <paramref name="nodeId"/> and its descendants (up to <paramref name="maxDepth"/> levels and
    /// <paramref name="maxNodes"/> nodes) and reads the current value of every variable in batches.
    /// </summary>
    public static async Task<NodeTree> ReadTreeAsync(
        this IDeviceClient client,
        NodeId nodeId,
        string displayName,
        NodeClass nodeClass,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var root = new NodeTree(nodeId, displayName, nodeClass);
        var visited = new HashSet<NodeId> { nodeId };
        var level = new List<NodeTree> { root };
        var count = 1;
        for (var depth = 0; depth < maxDepth && level.Count > 0 && count < maxNodes; depth++)
        {
            var next = new List<NodeTree>();
            foreach (var parent in level)
            {
                foreach (var child in await client.BrowseAsync(parent.NodeId, cancellationToken).ConfigureAwait(false))
                {
                    if (count >= maxNodes)
                    {
                        break;
                    }

                    if (child.NodeClass is not (NodeClass.Object or NodeClass.Variable) || !visited.Add(child.NodeId))
                    {
                        continue;
                    }

                    var node = new NodeTree(child.NodeId, child.DisplayName, child.NodeClass);
                    parent.Children.Add(node);
                    count++;
                    if (child.HasChildren)
                    {
                        next.Add(node);
                    }
                }
            }

            level = next;
        }

        var variables = Flatten(root).Where(n => n.NodeClass == NodeClass.Variable).ToList();
        foreach (var chunk in variables.Chunk(500))
        {
            var values = await client.ReadValuesAsync([.. chunk.Select(v => v.NodeId)], cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length && i < values.Count; i++)
            {
                chunk[i].Value = values[i];
            }
        }

        return root;

        static IEnumerable<NodeTree> Flatten(NodeTree node) => node.Children.SelectMany(Flatten).Prepend(node);
    }
}
