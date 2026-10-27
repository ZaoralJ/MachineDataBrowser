using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Core;

/// <summary>
/// A connection to one device (OPC UA server, Logix controller or MQTT broker), using shared device-neutral node identities.
/// </summary>
public interface IDeviceClient : IAsyncDisposable
{
    event EventHandler<ConnectionState>? StateChanged;

    ConnectionState State { get; }

    /// <summary>Whether this protocol connection is read-only regardless of the selected node.</summary>
    bool IsReadOnly { get; }

    /// <summary>Identity of the connected device, for display.</summary>
    string? ServerUri { get; }

    /// <summary>Top of the browse tree.</summary>
    BrowseItem Root { get; }

    /// <summary>Id stable across reconnects/restarts; used for anything persisted.</summary>
    string ToPortableId(DeviceNodeId nodeId);

    DeviceNodeId ParsePortableId(string text);

    /// <summary>Id as shown to people: in the tree, watch list, clipboard and generated code.</summary>
    string ToDisplayId(DeviceNodeId nodeId);

    Task ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    Task<IReadOnlyList<BrowseItem>> BrowseAsync(DeviceNodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Children in one round trip, possibly with a provisional <see cref="BrowseItem.HasChildren"/>; resolve it later
    /// with <see cref="ProbeHasChildrenAsync"/>. Clients whose browse is already exact keep the default.
    /// </summary>
    Task<IReadOnlyList<BrowseItem>> BrowseQuickAsync(DeviceNodeId nodeId, CancellationToken cancellationToken = default) =>
        BrowseAsync(nodeId, cancellationToken);

    /// <summary>Which of <paramref name="nodeIds"/> have children; <c>null</c> when <see cref="BrowseQuickAsync"/> is already exact.</summary>
    Task<IReadOnlyList<bool>?> ProbeHasChildrenAsync(IReadOnlyList<DeviceNodeId> nodeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<bool>?>(null);

    /// <summary>Ids from <see cref="Root"/> down to <paramref name="nodeId"/> (inclusive); empty when unknown.</summary>
    Task<IReadOnlyList<DeviceNodeId>> GetPathFromRootAsync(DeviceNodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(DeviceNodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>Current values of variables; <c>null</c> where the read failed.</summary>
    Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<DeviceNodeId> nodeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Current values together with their status, so a good null value can be distinguished from a failed read.
    /// Clients that cannot represent good null values may use the default implementation.
    /// </summary>
    async Task<IReadOnlyList<ValueUpdate>> ReadValueUpdatesAsync(IReadOnlyList<DeviceNodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var values = await ReadValuesAsync(nodeIds, cancellationToken).ConfigureAwait(false);
        return [.. values.Select((value, index) => new ValueUpdate(nodeIds[index], ValueFormatter.Format(value),
            value is null ? DeviceStatusCodes.BadNoData : DeviceStatusCodes.Good, DateTime.MinValue, DateTime.MinValue,
            ValueFormatter.ToNumeric(value), value))];
    }

    /// <summary>
    /// Writes the Value attribute of a variable, parsing <paramref name="text"/> into the variable's data type.
    /// Arrays are written as comma-separated elements, optionally in brackets.
    /// </summary>
    Task WriteValueAsync(DeviceNodeId nodeId, string text, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Writing values is not supported for this connection type."));

    /// <summary>
    /// Monitors many values; items with the same refresh time are sampled together.
    /// Rejected items are returned with <see cref="MonitorResult.Error"/> set and no handle.
    /// </summary>
    Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<DeviceNodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default);

    /// <summary>Re-monitors items at <paramref name="refreshMs"/>; handles not created by this client are ignored.</summary>
    Task<IReadOnlyList<MonitorResult>> ChangeRefreshAsync(
        IReadOnlyList<IAsyncDisposable> monitors,
        Action<ValueUpdate> onUpdate,
        double refreshMs,
        CancellationToken cancellationToken = default);

    async Task StopMonitoringAsync(IEnumerable<IAsyncDisposable> monitors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        foreach (var monitor in monitors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Implemented by clients whose address space grows while connected (MQTT: topics appear as messages arrive).
/// The app re-browses expanded tree nodes when this is raised.
/// </summary>
public interface IDynamicAddressSpace
{
    event EventHandler? AddressSpaceChanged;
}

/// <summary>
/// Clients that discover their address space from a stream of messages (MQTT subscribes to the whole topic filter).
/// Paused, only what is monitored is received: the tree stops growing, but a busy broker no longer floods the client.
/// </summary>
public interface IPausableDiscovery
{
    bool IsDiscoveryPaused { get; }

    /// <summary>Raised (on any thread) when discovery is paused or resumed, also by <see cref="ConnectOptions.AutoPauseDiscoverySeconds"/>.</summary>
    event EventHandler? DiscoveryPausedChanged;

    Task SetDiscoveryPausedAsync(bool paused, CancellationToken cancellationToken = default);
}

public static class DeviceClient
{
    /// <summary>URL scheme of EtherNet/IP (Logix) endpoints: <c>eip://host[:port][/path]</c>, path defaults to <c>1,0</c>.</summary>
    public const string EipScheme = "eip";

    public static bool IsEip(string endpointUrl) =>
        endpointUrl.TrimStart().StartsWith(EipScheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>URL scheme of Modbus TCP endpoints: <c>modbus://host[:port][/unit]</c>, port 502 and unit 1 by default.</summary>
    public const string ModbusScheme = "modbus";

    public static bool IsModbus(string endpointUrl) =>
        endpointUrl.TrimStart().StartsWith(ModbusScheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// URL scheme of PCCC endpoints (SLC 500, MicroLogix, PLC-5 over EtherNet/IP):
    /// <c>pccc://host[:port][?plc=slc500|micrologix|plc5][&amp;files=N10,F11]</c>.
    /// </summary>
    public const string PcccScheme = "pccc";

    public static bool IsPccc(string endpointUrl) =>
        endpointUrl.TrimStart().StartsWith(PcccScheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Allen-Bradley controllers over EtherNet/IP: Logix (<c>eip://</c>) or PCCC (<c>pccc://</c>). No security or credentials.</summary>
    public static bool IsAllenBradley(string endpointUrl) => IsEip(endpointUrl) || IsPccc(endpointUrl);

    /// <summary>URL scheme of Siemens S7 endpoints: <c>s7://host[:port][/rack,slot]</c>, port 102, rack 0, slot 1 by default.</summary>
    public const string S7Scheme = "s7";

    public static bool IsS7(string endpointUrl) =>
        endpointUrl.TrimStart().StartsWith(S7Scheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Protocols without names in the device, which take a tag map (<see cref="ConnectOptions.TagMapPath"/>): a Modbus
    /// register map or an S7 tag list (symbol table).
    /// </summary>
    public static bool UsesTagMap(string endpointUrl) => IsModbus(endpointUrl) || IsS7(endpointUrl);

    /// <summary>What the protocol calls its tag map, lower case: "register map" (Modbus), "tag list" (S7).</summary>
    public static string TagMapName(string endpointUrl) => IsS7(endpointUrl) ? "tag list" : "register map";

    /// <summary>Protocol name for people, from the endpoint URL.</summary>
    public static string ProtocolName(string endpointUrl) =>
        IsEip(endpointUrl) ? "EtherNet/IP (Logix)"
        : IsPccc(endpointUrl) ? "EtherNet/IP (PCCC)"
        : IsModbus(endpointUrl) ? "Modbus TCP"
        : IsS7(endpointUrl) ? "S7 (Siemens)"
        : IsMqtt(endpointUrl) ? "MQTT"
        : IsMtConnect(endpointUrl) ? "MTConnect"
        : "OPC UA";

    /// <summary>MQTT endpoints: <c>mqtt://</c>, <c>mqtts://</c>, <c>ws://</c>, <c>wss://</c>.</summary>
    public static bool IsMqtt(string endpointUrl)
    {
        var trimmed = endpointUrl.TrimStart();
        return trimmed.StartsWith("mqtt://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mqtts://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>MTConnect agents: <c>mtconnect://host[:port][/device]</c> (HTTP, port 5000), <c>http://</c>, <c>https://</c>.</summary>
    public static bool IsMtConnect(string endpointUrl)
    {
        var trimmed = endpointUrl.TrimStart();
        return trimmed.StartsWith("mtconnect://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Everything that is not another protocol is OPC UA (<c>opc.tcp://</c>, <c>opc.https://</c>, …).</summary>
    public static bool IsOpcUa(string endpointUrl) =>
        !IsAllenBradley(endpointUrl) && !UsesTagMap(endpointUrl) && !IsMqtt(endpointUrl) && !IsMtConnect(endpointUrl);
}

public interface IDeviceClientFactory
{
    IDeviceClient Create(string endpointUrl);

    bool Supports(IDeviceClient client, string endpointUrl);
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
        return result.Handle ?? throw new DeviceClientException(result.Error);
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
        CancellationToken cancellationToken = default) =>
        [.. (await client.CollectVariablesWithPathsAsync(nodeId, maxDepth, maxCount, descendIntoVariables, cancellationToken).ConfigureAwait(false)).Select(v => v.Item)];

    /// <summary>
    /// As <see cref="CollectVariablesAsync(IDeviceClient, NodeId, int, int, bool, CancellationToken)"/>, with each
    /// variable's parent path below <paramref name="nodeId"/> (display names joined by <c>/</c>; empty for direct children).
    /// </summary>
    public static async Task<IReadOnlyList<(BrowseItem Item, string Path)>> CollectVariablesWithPathsAsync(
        this IDeviceClient client,
        NodeId nodeId,
        int maxDepth,
        int maxCount,
        bool descendIntoVariables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var found = new List<(BrowseItem, string)>();
        var visited = new HashSet<NodeId> { nodeId };
        var level = new List<(NodeId NodeId, string Path)> { (nodeId, string.Empty) };

        for (var depth = 0; depth < maxDepth && level.Count > 0 && found.Count < maxCount; depth++)
        {
            var next = new List<(NodeId, string)>();
            foreach (var parent in level)
            {
                foreach (var child in await client.BrowseAsync(parent.NodeId, cancellationToken).ConfigureAwait(false))
                {
                    if (!visited.Add(child.NodeId))
                    {
                        continue;
                    }

                    if (child.NodeClass == NodeClass.Variable)
                    {
                        found.Add((child, parent.Path));
                        if (found.Count >= maxCount)
                        {
                            return found;
                        }
                    }

                    if (child.HasChildren && (child.NodeClass == NodeClass.Object || (descendIntoVariables && child.NodeClass == NodeClass.Variable)))
                    {
                        next.Add((child.NodeId, parent.Path.Length == 0 ? child.DisplayName : $"{parent.Path}/{child.DisplayName}"));
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
