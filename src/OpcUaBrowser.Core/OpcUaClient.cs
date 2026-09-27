using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.ComplexTypes;

namespace OpcUaBrowser.Core;

/// <summary>
/// One OPC UA session: connect, browse, read attributes and monitor values.
/// Thread-safe for the operations it exposes; events are raised on SDK threads.
/// </summary>
public sealed class OpcUaClient : IAsyncDisposable
{
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });

    private static readonly (uint Id, string Name)[] DisplayedAttributes =
    [
        (Attributes.NodeId, "NodeId"),
        (Attributes.NodeClass, "NodeClass"),
        (Attributes.BrowseName, "BrowseName"),
        (Attributes.DisplayName, "DisplayName"),
        (Attributes.Description, "Description"),
        (Attributes.Value, "Value"),
        (Attributes.DataType, "DataType"),
        (Attributes.ValueRank, "ValueRank"),
        (Attributes.ArrayDimensions, "ArrayDimensions"),
        (Attributes.AccessLevel, "AccessLevel"),
        (Attributes.UserAccessLevel, "UserAccessLevel"),
        (Attributes.MinimumSamplingInterval, "MinimumSamplingInterval"),
        (Attributes.Historizing, "Historizing"),
        (Attributes.EventNotifier, "EventNotifier"),
        (Attributes.Executable, "Executable"),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISession? _session;
    private readonly Dictionary<int, Subscription> _subscriptions = [];
    private SessionReconnectHandler? _reconnectHandler;
    private ConnectionState _state = ConnectionState.Disconnected;

    public event EventHandler<ConnectionState>? StateChanged;

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public string? ServerUri => _session?.Endpoint?.Server?.ApplicationUri;

    /// <summary>
    /// Formats a NodeId with its namespace URI (<c>nsu=...</c>) instead of the index, which servers may
    /// renumber between restarts. Use for anything persisted.
    /// </summary>
    public string ToPortableId(NodeId nodeId)
    {
        var session = RequireSession();
        var uri = session.NamespaceUris.GetString(nodeId.NamespaceIndex);
        return nodeId.NamespaceIndex == 0 || uri is null
            ? nodeId.ToString()
            : new ExpandedNodeId(nodeId, uri).ToString();
    }

    /// <summary>Parses <c>nsu=</c> or <c>ns=</c> NodeIds against the connected server's namespace table.</summary>
    public NodeId ParsePortableId(string text)
    {
        var session = RequireSession();
        var expanded = ExpandedNodeId.Parse(text);
        return ExpandedNodeId.ToNodeId(expanded, session.NamespaceUris)
            ?? throw new ServiceResultException(StatusCodes.BadNodeIdUnknown, $"Namespace of '{text}' is not known by this server.");
    }

    public async Task ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            State = ConnectionState.Connecting;

            var config = await ClientConfiguration.CreateAsync(
                Telemetry,
                _ => options.AutoAcceptUntrustedCertificates,
                cancellationToken).ConfigureAwait(false);

            var description = await CoreClientUtils.SelectEndpointAsync(
                config,
                options.EndpointUrl,
                options.UseSecurity,
                Telemetry,
                cancellationToken).ConfigureAwait(false);

            var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));

            IUserIdentity identity = string.IsNullOrEmpty(options.UserName)
                ? new UserIdentity(new AnonymousIdentityToken())
                : new UserIdentity(options.UserName, System.Text.Encoding.UTF8.GetBytes(options.Password ?? string.Empty));

            var session = await new DefaultSessionFactory(Telemetry).CreateAsync(
                config,
                endpoint,
                updateBeforeConnect: false,
                ClientConfiguration.ApplicationName,
                options.SessionTimeoutMs,
                identity,
                null,
                cancellationToken).ConfigureAwait(false);

            session.DeleteSubscriptionsOnClose = true;
            session.TransferSubscriptionsOnReconnect = true;
            session.KeepAlive += OnKeepAlive;

            // Custom structures: without this, ExtensionObjects of server-specific types display as raw bytes.
            try
            {
                await new ComplexTypeSystem(session, Telemetry).LoadAsync(false, true, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResultException)
            {
                // Server without type info: values still show, just undecoded.
            }

            _session = session;
            State = ConnectionState.Connected;
        }
        catch
        {
            State = ConnectionState.Disconnected;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Hierarchical forward references of <paramref name="nodeId"/>, following continuation points.
    /// Each child's <see cref="BrowseItem.HasChildren"/> is resolved with one extra batched Browse for all children.
    /// </summary>
    public async Task<IReadOnlyList<BrowseItem>> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();

        var (references, errors) = await session.ManagedBrowseAsync(
            null,
            null,
            new NodeIdCollection { nodeId },
            0,
            BrowseDirection.Forward,
            ReferenceTypeIds.HierarchicalReferences,
            true,
            0,
            cancellationToken).ConfigureAwait(false);

        if (errors.Count > 0 && ServiceResult.IsBad(errors[0]))
        {
            throw new ServiceResultException(errors[0]);
        }

        if (references.Count == 0 || references[0].Count == 0)
        {
            return [];
        }

        var children = references[0]
            .Where(r => !r.NodeId.IsAbsolute)
            .Select(r => (Reference: r, NodeId: ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)))
            .ToList();

        var hasChildren = await HasHierarchicalChildrenAsync(session, children.Select(c => c.NodeId).ToList(), cancellationToken)
            .ConfigureAwait(false);

        return children
            .Select((c, i) => new BrowseItem(
                c.NodeId,
                c.Reference.DisplayName?.Text ?? c.Reference.BrowseName?.Name ?? c.Reference.NodeId.ToString(),
                c.Reference.BrowseName?.ToString() ?? string.Empty,
                c.Reference.NodeClass,
                hasChildren[i]))
            .ToList();
    }

    /// <summary>
    /// NodeIds from the Root folder down to <paramref name="nodeId"/> (inclusive), following inverse
    /// hierarchical references. Empty when no path to Root is found within <paramref name="maxDepth"/>.
    /// </summary>
    public async Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var path = new List<NodeId> { nodeId };
        var current = nodeId;
        while (current != ObjectIds.RootFolder)
        {
            if (path.Count > maxDepth)
            {
                return [];
            }

            var (references, errors) = await session.ManagedBrowseAsync(
                null, null, new NodeIdCollection { current }, 0, BrowseDirection.Inverse,
                ReferenceTypeIds.HierarchicalReferences, true, 0, cancellationToken).ConfigureAwait(false);
            if ((errors.Count > 0 && ServiceResult.IsBad(errors[0])) || references.Count == 0 || references[0].Count == 0)
            {
                return [];
            }

            var parents = references[0].Select(r => ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)).Where(p => p is not null && !path.Contains(p)).ToList();
            if (parents.Count == 0)
            {
                return [];
            }

            current = parents.FirstOrDefault(p => p == ObjectIds.ObjectsFolder || p == ObjectIds.RootFolder) ?? parents[0];
            path.Insert(0, current);
        }

        return path;
    }

    private static async Task<bool[]> HasHierarchicalChildrenAsync(
        ISession session,
        List<NodeId> nodeIds,
        CancellationToken cancellationToken)
    {
        var result = new bool[nodeIds.Count];
        Array.Fill(result, true);

        try
        {
            var (_, continuationPoints, referenceLists, errors) = await session.BrowseAsync(
                null,
                null,
                nodeIds,
                1,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                0,
                cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < result.Length && i < referenceLists.Count; i++)
            {
                var failed = i < errors.Count && ServiceResult.IsBad(errors[i]);
                var truncated = i < continuationPoints.Count && continuationPoints[i] is { Length: > 0 };
                result[i] = failed || truncated || referenceLists[i].Count > 0;
            }

            var pending = new ByteStringCollection(continuationPoints.Where(cp => cp is { Length: > 0 }));
            if (pending.Count > 0)
            {
                await session.BrowseNextAsync(null, pending, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ServiceResultException)
        {
            // Probe is best-effort: on failure every child keeps an expander, as before.
        }

        return result;
    }

    /// <summary>Reads the common attributes; attributes the node class doesn't have are omitted.</summary>
    public async Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();

        var toRead = new ReadValueIdCollection(
            DisplayedAttributes.Select(a => new ReadValueId { NodeId = nodeId, AttributeId = a.Id }));

        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, toRead, cancellationToken)
            .ConfigureAwait(false);

        var result = new List<AttributeValue>(DisplayedAttributes.Length);
        for (var i = 0; i < DisplayedAttributes.Length && i < response.Results.Count; i++)
        {
            var dv = response.Results[i];
            if (dv.StatusCode == StatusCodes.BadAttributeIdInvalid)
            {
                continue;
            }

            var attributeId = DisplayedAttributes[i].Id;
            string text;
            if (StatusCode.IsBad(dv.StatusCode))
            {
                text = dv.StatusCode.ToString();
            }
            else if (attributeId == Attributes.DataType && dv.Value is NodeId dataType)
            {
                var name = await session.NodeCache.GetDisplayTextAsync(dataType, cancellationToken).ConfigureAwait(false);
                text = string.IsNullOrEmpty(name) ? dataType.ToString() : $"{name} ({dataType})";
            }
            else
            {
                text = FormatAttribute(attributeId, dv.Value);
            }

            result.Add(new AttributeValue(DisplayedAttributes[i].Name, text));
        }

        return result;
    }

    /// <summary>Starts monitoring the Value attribute. Dispose the returned handle to stop.</summary>
    public async Task<IAsyncDisposable> MonitorAsync(
        NodeId nodeId,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        var result = (await MonitorManyAsync([nodeId], onUpdate, samplingIntervalMs, cancellationToken).ConfigureAwait(false))[0];
        return result.Handle ?? throw new ServiceResultException(result.Error);
    }

    /// <summary>
    /// Monitors many Value attributes with a single CreateMonitoredItems round trip.
    /// Items the server rejects are returned with <see cref="MonitorResult.Error"/> set and no handle.
    /// </summary>
    public async Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<NodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(onUpdate);
        if (nodeIds.Count == 0)
        {
            return [];
        }

        var subscription = await GetOrCreateSubscriptionAsync(samplingIntervalMs, cancellationToken).ConfigureAwait(false);

        var items = nodeIds.Select(nodeId =>
        {
            var item = new MonitoredItem(Telemetry)
            {
                StartNodeId = nodeId,
                AttributeId = Attributes.Value,
                SamplingInterval = (int)samplingIntervalMs,
                QueueSize = 1,
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting,
            };
            item.Notification += (monitoredItem, e) =>
            {
                if (e.NotificationValue is MonitoredItemNotification n && n.Value is { } dv)
                {
                    onUpdate(new ValueUpdate(
                        monitoredItem.StartNodeId,
                        ValueFormatter.Format(dv.WrappedValue),
                        dv.StatusCode,
                        dv.SourceTimestamp,
                        dv.ServerTimestamp,
                        ValueFormatter.ToNumeric(dv.WrappedValue),
                        dv.WrappedValue.Value));
                }
            };
            return item;
        }).ToList();

        subscription.AddItems(items);
        await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<MonitorResult>(items.Count);
        var rejected = new List<MonitoredItem>();
        foreach (var item in items)
        {
            if (ServiceResult.IsBad(item.Status.Error))
            {
                rejected.Add(item);
                results.Add(new MonitorResult(item.StartNodeId, null, item.Status.Error ?? new ServiceResult(StatusCodes.Bad)));
            }
            else
            {
                results.Add(new MonitorResult(item.StartNodeId, new MonitorHandle(subscription, item), ServiceResult.Good));
            }
        }

        if (rejected.Count > 0)
        {
            subscription.RemoveItems(rejected);
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>
    /// Collects Variable nodes below <paramref name="nodeId"/> following hierarchical references,
    /// breadth-first, up to <paramref name="maxDepth"/> levels and <paramref name="maxCount"/> results.
    /// Only folders/objects are descended into, so a variable's own properties (EURange, EngineeringUnits, …)
    /// are not collected.
    /// </summary>
    public Task<IReadOnlyList<BrowseItem>> CollectVariablesAsync(
        NodeId nodeId,
        int maxDepth,
        int maxCount,
        CancellationToken cancellationToken = default) =>
        CollectVariablesAsync(nodeId, maxDepth, maxCount, descendIntoVariables: false, cancellationToken);

    /// <summary>
    /// As <see cref="CollectVariablesAsync(NodeId,int,int,CancellationToken)"/>; with <paramref name="descendIntoVariables"/>
    /// the children of variables (structure components, properties) are collected too.
    /// </summary>
    public async Task<IReadOnlyList<BrowseItem>> CollectVariablesAsync(
        NodeId nodeId,
        int maxDepth,
        int maxCount,
        bool descendIntoVariables,
        CancellationToken cancellationToken = default)
    {
        var found = new List<BrowseItem>();
        var visited = new HashSet<NodeId> { nodeId };
        var level = new List<NodeId> { nodeId };

        for (var depth = 0; depth < maxDepth && level.Count > 0 && found.Count < maxCount; depth++)
        {
            var next = new List<NodeId>();
            foreach (var parent in level)
            {
                foreach (var child in await BrowseAsync(parent, cancellationToken).ConfigureAwait(false))
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
    public async Task<NodeTree> ReadTreeAsync(
        NodeId nodeId,
        string displayName,
        NodeClass nodeClass,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var root = new NodeTree(nodeId, displayName, nodeClass);
        var visited = new HashSet<NodeId> { nodeId };
        var level = new List<NodeTree> { root };
        var count = 1;
        for (var depth = 0; depth < maxDepth && level.Count > 0 && count < maxNodes; depth++)
        {
            var next = new List<NodeTree>();
            foreach (var parent in level)
            {
                foreach (var child in await BrowseAsync(parent.NodeId, cancellationToken).ConfigureAwait(false))
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
            var toRead = new ReadValueIdCollection(chunk.Select(v => new ReadValueId { NodeId = v.NodeId, AttributeId = Attributes.Value }));
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, toRead, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length && i < response.Results.Count; i++)
            {
                chunk[i].Value = StatusCode.IsBad(response.Results[i].StatusCode) ? null : response.Results[i].Value;
            }
        }

        return root;

        static IEnumerable<NodeTree> Flatten(NodeTree node) => node.Children.SelectMany(Flatten).Prepend(node);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private const string SubscriptionPrefix = "Watch@";

    /// <summary>Items with the same refresh time share one subscription whose publishing interval equals it.</summary>
    private async Task<Subscription> GetOrCreateSubscriptionAsync(double refreshMs, CancellationToken cancellationToken)
    {
        var interval = Math.Max(0, (int)refreshMs);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            if (_subscriptions.TryGetValue(interval, out var existing) && existing.Created)
            {
                return existing;
            }

            var subscription = new Subscription(session.DefaultSubscription)
            {
                DisplayName = SubscriptionPrefix + interval.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PublishingInterval = interval,
                KeepAliveCount = interval >= 5000 ? 3u : 10u,
                LifetimeCount = interval >= 5000 ? 30u : 100u,
                PublishingEnabled = true,
                TimestampsToReturn = TimestampsToReturn.Both,
            };

            session.AddSubscription(subscription);
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
            _subscriptions[interval] = subscription;
            return subscription;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves existing monitored items to the subscription for <paramref name="refreshMs"/> and updates their
    /// sampling interval. Returns the new handles; handles not created by this client are ignored.
    /// </summary>
    public async Task<IReadOnlyList<MonitorResult>> ChangeRefreshAsync(
        IReadOnlyList<IAsyncDisposable> handles,
        Action<ValueUpdate> onUpdate,
        double refreshMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handles);
        var owned = handles.OfType<MonitorHandle>().ToList();
        var nodeIds = owned.Select(h => h.Item.StartNodeId).ToList();
        var results = await MonitorManyAsync(nodeIds, onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        await StopMonitoringAsync(owned.Cast<IAsyncDisposable>(), cancellationToken).ConfigureAwait(false);
        await DeleteEmptySubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private async Task DeleteEmptySubscriptionsAsync(CancellationToken cancellationToken)
    {
        if (_session is not { Connected: true } session)
        {
            return;
        }

        foreach (var (interval, subscription) in _subscriptions.Where(p => p.Value.MonitoredItemCount == 0).ToList())
        {
            _subscriptions.Remove(interval);
            await session.RemoveSubscriptionAsync(subscription, cancellationToken).ConfigureAwait(false);
        }
    }

    private ISession RequireSession() =>
        _session is { Connected: true } s ? s : throw new InvalidOperationException("Not connected.");

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (!ReferenceEquals(session, _session) || !ServiceResult.IsBad(e.Status) || _reconnectHandler is not null)
        {
            return;
        }

        State = ConnectionState.Reconnecting;
        _reconnectHandler = new SessionReconnectHandler(Telemetry, true, 30_000);
        _reconnectHandler.BeginReconnect(session, 1_000, OnReconnectComplete);
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        var handler = _reconnectHandler;
        if (handler is null)
        {
            return;
        }

        if (handler.Session is { } newSession && !ReferenceEquals(newSession, _session))
        {
            if (_session is not null)
            {
                _session.KeepAlive -= OnKeepAlive;
            }

            _session = newSession;
            _session.KeepAlive += OnKeepAlive;
            _subscriptions.Clear();
            foreach (var sub in _session.Subscriptions.Where(s => s.DisplayName?.StartsWith(SubscriptionPrefix, StringComparison.Ordinal) == true))
            {
                _subscriptions[(int)sub.PublishingInterval] = sub;
            }
        }

        handler.Dispose();
        _reconnectHandler = null;
        State = _session is { Connected: true } ? ConnectionState.Connected : ConnectionState.Disconnected;
    }

    private async Task CloseCoreAsync()
    {
        _reconnectHandler?.Dispose();
        _reconnectHandler = null;
        _subscriptions.Clear();

        if (_session is { } session)
        {
            _session = null;
            session.KeepAlive -= OnKeepAlive;
            try
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
            catch (ServiceResultException) when (!session.Connected)
            {
            }

            session.Dispose();
        }

        State = ConnectionState.Disconnected;
    }

    private static string FormatAttribute(uint attributeId, object? value) => attributeId switch
    {
        Attributes.NodeClass when value is int nc => ((NodeClass)nc).ToString(),
        Attributes.ValueRank when value is int rank => rank switch
        {
            ValueRanks.Scalar => "Scalar",
            ValueRanks.OneDimension => "OneDimension",
            ValueRanks.ScalarOrOneDimension => "ScalarOrOneDimension",
            ValueRanks.Any => "Any",
            ValueRanks.OneOrMoreDimensions => "OneOrMoreDimensions",
            _ => rank.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        Attributes.AccessLevel or Attributes.UserAccessLevel when value is byte access => FormatAccessLevel(access),
        _ => ValueFormatter.Format(new Variant(value)),
    };

    private static string FormatAccessLevel(byte access)
    {
        var parts = new List<string>(4);
        if ((access & AccessLevels.CurrentRead) != 0) parts.Add("Read");
        if ((access & AccessLevels.CurrentWrite) != 0) parts.Add("Write");
        if ((access & AccessLevels.HistoryRead) != 0) parts.Add("HistoryRead");
        if ((access & AccessLevels.HistoryWrite) != 0) parts.Add("HistoryWrite");
        return parts.Count == 0 ? "None" : string.Join(", ", parts);
    }

    /// <summary>Stops many monitored items with one DeleteMonitoredItems round trip per subscription.</summary>
    public static async Task StopMonitoringAsync(IEnumerable<IAsyncDisposable> handles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handles);
        foreach (var group in handles.OfType<MonitorHandle>().GroupBy(h => h.Subscription))
        {
            group.Key.RemoveItems(group.Select(h => h.Item));
            if (group.Key.Session is { Connected: true })
            {
                await group.Key.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class MonitorHandle(Subscription subscription, MonitoredItem item) : IAsyncDisposable
    {
        public Subscription Subscription => subscription;

        public MonitoredItem Item => item;

        public async ValueTask DisposeAsync() => await StopMonitoringAsync([this]).ConfigureAwait(false);
    }
}
