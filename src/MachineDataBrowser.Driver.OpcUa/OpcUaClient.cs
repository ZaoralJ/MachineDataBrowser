using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.ComplexTypes;
using Opc.Ua.Client.Subscriptions;
using Opc.Ua.Client.Subscriptions.MonitoredItems;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;
using MachineDataBrowser.Core;
using ConnectionState = MachineDataBrowser.Core.ConnectionState;
using UaItemOptions = Opc.Ua.Client.Subscriptions.MonitoredItems.MonitoredItemOptions;

namespace MachineDataBrowser.Driver.OpcUa;

/// <summary>
/// One OPC UA session: connect, browse, read attributes and monitor values.
/// Thread-safe for the operations it exposes; events are raised on SDK threads.
/// </summary>
public sealed partial class OpcUaClient : IDeviceClient, IServerCertificateTrust
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
    private ManagedSession? _session;
    private ApplicationInstance? _applicationInstance;
    private readonly Dictionary<int, UaSubscription> _subscriptions = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<UaSubscription, byte> _allSubscriptions = new();
    private long _nextItem;
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

    public bool IsReadOnly => false;

    public string? ServerUri => _session?.Endpoint?.Server?.ApplicationUri;

    public ServerCertificate? LastUntrustedCertificate { get; private set; }

    public void TrustPermanently(ServerCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ClientConfiguration.AddTrusted(certificate.RawData, certificate.Thumbprint);
    }

    public BrowseItem Root { get; } = new(UaNodeIdAdapter.ToDevice(ObjectIds.RootFolder), "Root", "Root", DeviceNodeKind.Object);

    string IDeviceClient.ToPortableId(DeviceNodeId nodeId) =>
        UaStatusAdapter.Translate(() => ToPortableId(UaNodeIdAdapter.ToUa(nodeId)));

    DeviceNodeId IDeviceClient.ParsePortableId(string text) =>
        UaStatusAdapter.Translate(() => UaNodeIdAdapter.ToDevice(ParsePortableId(text)));

    string IDeviceClient.ToDisplayId(DeviceNodeId nodeId) =>
        UaStatusAdapter.Translate(() => ToDisplayId(UaNodeIdAdapter.ToUa(nodeId)));

    Task<IReadOnlyList<BrowseItem>> IDeviceClient.BrowseAsync(DeviceNodeId nodeId, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => BrowseAsync(UaNodeIdAdapter.ToUa(nodeId), cancellationToken));

    Task<IReadOnlyList<BrowseItem>> IDeviceClient.BrowseQuickAsync(DeviceNodeId nodeId, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => BrowseQuickAsync(UaNodeIdAdapter.ToUa(nodeId), cancellationToken));

    Task<IReadOnlyList<bool>?> IDeviceClient.ProbeHasChildrenAsync(IReadOnlyList<DeviceNodeId> nodeIds, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => ProbeHasChildrenAsync(nodeIds.Select(UaNodeIdAdapter.ToUa).ToList(), cancellationToken));

    Task<IReadOnlyList<DeviceNodeId>> IDeviceClient.GetPathFromRootAsync(DeviceNodeId nodeId, int maxDepth, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync<IReadOnlyList<DeviceNodeId>>(async () =>
            (await GetPathFromRootAsync(UaNodeIdAdapter.ToUa(nodeId), maxDepth, cancellationToken).ConfigureAwait(false))
                .Select(UaNodeIdAdapter.ToDevice).ToList());

    Task<IReadOnlyList<AttributeValue>> IDeviceClient.ReadAttributesAsync(DeviceNodeId nodeId, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => ReadAttributesAsync(UaNodeIdAdapter.ToUa(nodeId), cancellationToken));

    Task<IReadOnlyList<object?>> IDeviceClient.ReadValuesAsync(IReadOnlyList<DeviceNodeId> nodeIds, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => ReadValuesAsync(nodeIds.Select(UaNodeIdAdapter.ToUa).ToList(), cancellationToken));

    Task IDeviceClient.WriteValueAsync(DeviceNodeId nodeId, string text, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => WriteValueAsync(UaNodeIdAdapter.ToUa(nodeId), text, cancellationToken));

    Task<IReadOnlyList<MonitorResult>> IDeviceClient.MonitorManyAsync(
        IReadOnlyList<DeviceNodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs,
        CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => MonitorManyAsync(nodeIds.Select(UaNodeIdAdapter.ToUa).ToList(), onUpdate, samplingIntervalMs, cancellationToken));

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

    /// <summary>The familiar <c>ns=1;s=...</c> form.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the connected-client API.")]
    public string ToDisplayId(NodeId nodeId)
    {
        return nodeId.ToString();
    }

    /// <summary>Parses <c>nsu=</c> or <c>ns=</c> NodeIds against the connected server's namespace table.</summary>
    public NodeId ParsePortableId(string text)
    {
        var session = RequireSession();
        var expanded = ExpandedNodeId.Parse(text);
        var nodeId = ExpandedNodeId.ToNodeId(expanded, session.NamespaceUris);
        return nodeId.IsNull
            ? throw new ServiceResultException(StatusCodes.BadNodeIdUnknown, $"Namespace of '{text}' is not known by this server.")
            : nodeId;
    }

    public async Task ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            State = ConnectionState.Connecting;

            var accepted = options.AcceptedCertificateThumbprints;
            LastUntrustedCertificate = null;
            var (config, applicationInstance) = await ClientConfiguration.CreateAsync(
                Telemetry,
                (certificate, error) =>
                {
                    // The trusted store doesn't lift policy failures, so a certificate trusted "always" is checked here.
                    if (options.AutoAcceptUntrustedCertificates
                        || accepted.Contains(certificate.Thumbprint, StringComparer.OrdinalIgnoreCase)
                        || ClientConfiguration.IsTrusted(certificate.Thumbprint))
                    {
                        return true;
                    }

                    LastUntrustedCertificate = new ServerCertificate(
                        certificate.Subject,
                        certificate.Issuer,
                        certificate.Thumbprint,
                        certificate.NotBefore,
                        certificate.NotAfter,
                        error.LocalizedText.Text is { Length: > 0 } text ? text : error.StatusCode.ToString(),
                        certificate.RawData);
                    return false;
                },
                cancellationToken).ConfigureAwait(false);

            _applicationInstance = applicationInstance;
            try
            {
                var description = await CoreClientUtils.SelectEndpointAsync(
                    config,
                    options.EndpointUrl,
                    options.UseSecurity,
                    Telemetry,
                    cancellationToken).ConfigureAwait(false);

                var endpoint = new ConfiguredEndpoint(null,
                    description ?? throw new ServiceResultException(StatusCodes.BadServerNotConnected), EndpointConfiguration.Create(config));

                IUserIdentity identity = string.IsNullOrEmpty(options.UserName)
                    ? new UserIdentity(new AnonymousIdentityToken())
                    : new UserIdentity(options.UserName, System.Text.Encoding.UTF8.GetBytes(options.Password ?? string.Empty));

                // The managed session reconnects on its own and keeps the subscriptions across it, also when it has
                // to create a new session after a server restart. The first connect gets a single retry, so a wrong
                // password or an untrusted certificate is reported at once; after that it retries without giving up:
                // a machine that is switched off for a while must come back on its own, like the Logix and MQTT clients.
                var reconnectPolicy = new ReconnectPolicy
                {
                    Strategy = BackoffStrategy.Constant,
                    InitialDelay = ReconnectRetryDelay,
                    MaxDelay = ReconnectRetryDelay,
                    JitterFactor = 0,
                    MaxRetries = 1,
                    MaxTotalReconnectTime = TimeSpan.Zero,
                };
                var session = await ManagedSession.CreateAsync(
                    config,
                    endpoint,
                    new DefaultSessionFactory(Telemetry),
                    identity,
                    reconnectPolicy,
                    telemetry: Telemetry,
                    sessionName: ClientConfiguration.ApplicationName,
                    sessionTimeout: options.SessionTimeoutMs,
                    transferSubscriptionsOnRecreate: true,
                    updateBeforeConnect: false,
                    ct: cancellationToken).ConfigureAwait(false);

                reconnectPolicy.MaxRetries = 0;
                session.DeleteSubscriptionsOnClose = true;
                session.ConnectionStateChanged += OnConnectionStateChanged;

                // Custom structure types load when a value first needs them (OpcUaClient.Structures.cs), not here.
                _session = session;
                _connectedAt = DateTime.UtcNow;
                _reconnects = 0;
                _lastReconnect = null;
                State = ConnectionState.Connected;
            }
            catch
            {
                await applicationInstance.DisposeAsync().ConfigureAwait(false);
                _applicationInstance = null;
                throw;
            }
        }
        catch (ServiceResultException ex)
        {
            State = ConnectionState.Disconnected;
            throw new DeviceClientException(UaStatusAdapter.ToDevice(ex.StatusCode), ex.Message, ex);
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
        catch (ServiceResultException ex)
        {
            throw new DeviceClientException(UaStatusAdapter.ToDevice(ex.StatusCode), ex.Message, ex);
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
    public Task<IReadOnlyList<BrowseItem>> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        BrowseCoreAsync(nodeId, probe: true, cancellationToken);

    /// <summary>Like <see cref="BrowseAsync"/> without the HasChildren probe: every container child reports children.</summary>
    public Task<IReadOnlyList<BrowseItem>> BrowseQuickAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        BrowseCoreAsync(nodeId, probe: false, cancellationToken);

    public async Task<IReadOnlyList<bool>?> ProbeHasChildrenAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default) =>
        nodeIds.Count == 0 ? [] : await HasHierarchicalChildrenAsync(RequireSession(), [.. nodeIds], cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<BrowseItem>> BrowseCoreAsync(NodeId nodeId, bool probe, CancellationToken cancellationToken)
    {
        var session = RequireSession();

        var (references, errors) = await session.ManagedBrowseAsync(
            null,
            null,
            new List<NodeId> { nodeId },
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

        var children = references[0].ToList()
            .Where(r => !r.NodeId.IsAbsolute)
            .Select(r => (Reference: r, NodeId: ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)))
            .ToList();

        var hasChildren = probe
            ? await HasHierarchicalChildrenAsync(session, children.Select(c => c.NodeId).ToList(), cancellationToken).ConfigureAwait(false)
            : null;

        return children
            .Select((c, i) => new BrowseItem(
                UaNodeIdAdapter.ToDevice(c.NodeId),
                c.Reference.DisplayName.Text ?? c.Reference.BrowseName.Name ?? c.Reference.NodeId.ToString(),
                    c.Reference.BrowseName.ToString(),
                UaNodeIdAdapter.ToDevice(c.Reference.NodeClass),
                hasChildren?[i] ?? true))
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
                null, null, new List<NodeId> { current }, 0, BrowseDirection.Inverse,
                ReferenceTypeIds.HierarchicalReferences, true, 0, cancellationToken).ConfigureAwait(false);
            if ((errors.Count > 0 && ServiceResult.IsBad(errors[0])) || references.Count == 0 || references[0].Count == 0)
            {
                return [];
            }

            var parents = references[0].ToList()
                .Select(r => ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris))
                .Where(p => !p.IsNull && !path.Contains(p))
                .ToList();
            if (parents.Count == 0)
            {
                return [];
            }

            var preferred = parents.FirstOrDefault(p => p == ObjectIds.ObjectsFolder || p == ObjectIds.RootFolder);
            current = preferred.IsNull ? parents[0] : preferred;
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

            var pending = continuationPoints.ToList().Where(cp => !cp.IsNull && !cp.IsEmpty).ToList();
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

        var toRead = new List<ReadValueId>(
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
            else if (attributeId == Attributes.DataType && dv.WrappedValue.AsBoxedObject() is NodeId dataType)
            {
                var name = await session.NodeCache.GetDisplayTextAsync(dataType, cancellationToken).ConfigureAwait(false);
                text = string.IsNullOrEmpty(name) ? dataType.ToString() : $"{name} ({dataType})";
            }
            else
            {
                var value = attributeId == Attributes.Value
                    ? await DecodeStructuresAsync(dv.WrappedValue.AsBoxedObject(), cancellationToken).ConfigureAwait(false)
                    : dv.WrappedValue.AsBoxedObject();
                text = FormatAttribute(attributeId, value);
            }

            result.Add(new AttributeValue(DisplayedAttributes[i].Name, text));
        }

        return result;
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

        var added = new List<(NodeId NodeId, string Name, OptionsMonitor<UaItemOptions> Options, IMonitoredItem? Item)>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            var name = "Value" + Interlocked.Increment(ref _nextItem).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var options = new OptionsMonitor<UaItemOptions>(new UaItemOptions
            {
                StartNodeId = nodeId,
                AttributeId = Attributes.Value,
                SamplingInterval = TimeSpan.FromMilliseconds((int)samplingIntervalMs),
                QueueSize = 1,
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting,
                TimestampsToReturn = TimestampsToReturn.Both,
            });
            subscription.SetValueHandler(name, OnValue(nodeId));
            if (!subscription.Subscription.MonitoredItems.TryAdd(name, options, out var item))
            {
                subscription.RemoveValueHandler(name);
                item = null;
            }

            added.Add((nodeId, name, options, item));
        }

        Action<DataValue> OnValue(NodeId nodeId)
        {
            // Values waiting for their structure type to load; later values queue behind them to keep the order.
            var pending = Task.CompletedTask;
            var pendingLock = new object();
            return dv =>
            {
                // Runs on an SDK thread: an exception here would take down the process.
                lock (pendingLock)
                {
                    if (!pending.IsCompleted || StructureTypes.HasUndecoded(dv.WrappedValue.AsBoxedObject()))
                    {
                        pending = DeliverAfterAsync(pending, nodeId, dv);
                        return;
                    }
                }

                Deliver(nodeId, dv);
            };
        }

        async Task DeliverAfterAsync(Task previous, NodeId nodeId, DataValue dv)
        {
            await previous.ConfigureAwait(false);
            try
            {
                dv = await DecodeStructuresAsync(dv, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                System.Diagnostics.Trace.TraceWarning($"Decoding a monitored structure failed: {ex.Message}");
            }

            Deliver(nodeId, dv);
        }

        void Deliver(NodeId nodeId, DataValue dv)
        {
            ValueUpdate update;
            try
            {
                update = new ValueUpdate(
                    UaNodeIdAdapter.ToDevice(nodeId),
                    UaValueFormatter.Format(dv.WrappedValue),
                    UaStatusAdapter.ToDevice(dv.StatusCode),
                    dv.SourceTimestamp.ToDateTime(),
                    dv.ServerTimestamp.ToDateTime(),
                    UaValueFormatter.ToNumeric(dv.WrappedValue),
                    dv.WrappedValue.AsBoxedObject());
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                update = new ValueUpdate(UaNodeIdAdapter.ToDevice(nodeId), $"<cannot display value: {ex.Message}>", UaStatusAdapter.ToDevice(StatusCodes.BadDecodingError), dv.SourceTimestamp.ToDateTime(), dv.ServerTimestamp.ToDateTime());
            }

            try
            {
                onUpdate(update);
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                System.Diagnostics.Trace.TraceError($"Value update handler failed: {ex}");
            }
        }

        await UaSubscription.WaitUntilAppliedAsync(added.Select(a => a.Item).OfType<IMonitoredItem>(), stopOnError: true, cancellationToken).ConfigureAwait(false);

        var results = new List<MonitorResult>(added.Count);
        foreach (var (nodeId, name, options, item) in added)
        {
            if (item is null)
            {
                results.Add(new MonitorResult(UaNodeIdAdapter.ToDevice(nodeId), null, UaStatusAdapter.ToDevice(new ServiceResult(StatusCodes.BadTooManyMonitoredItems))));
            }
            else if (!item.Created && ServiceResult.IsBad(item.Error))
            {
                // Removing the item may clear its status: keep the server's answer first.
                var error = item.Error;
                subscription.Subscription.MonitoredItems.TryRemove(item.ClientHandle);
                subscription.RemoveValueHandler(name);
                results.Add(new MonitorResult(UaNodeIdAdapter.ToDevice(nodeId), null, UaStatusAdapter.ToDevice(error)));
            }
            else
            {
                results.Add(new MonitorResult(UaNodeIdAdapter.ToDevice(nodeId), new MonitorHandle(subscription, item, options), DeviceError.Good));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var session = RequireSession();
        var result = new List<object?>(nodeIds.Count);
        foreach (var chunk in nodeIds.Chunk(500))
        {
            var toRead = new List<ReadValueId>(chunk.Select(id => new ReadValueId { NodeId = id, AttributeId = Attributes.Value }));
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, toRead, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                result.Add(i < response.Results.Count && !StatusCode.IsBad(response.Results[i].StatusCode)
                    ? await DecodeStructuresAsync(response.Results[i].WrappedValue.AsBoxedObject(), cancellationToken).ConfigureAwait(false)
                    : null);
            }
        }

        return result;
    }

    public async Task WriteValueAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var session = RequireSession();

        var toRead = new List<ReadValueId>
        {
            new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value },
            new ReadValueId { NodeId = nodeId, AttributeId = Attributes.DataType },
            new ReadValueId { NodeId = nodeId, AttributeId = Attributes.ValueRank },
            new ReadValueId { NodeId = nodeId, AttributeId = Attributes.AccessLevel },
            new ReadValueId { NodeId = nodeId, AttributeId = Attributes.UserAccessLevel },
        };
        var read = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, toRead, cancellationToken).ConfigureAwait(false);

        var userAccess = ReadAccessLevel(read.Results, 4);
        var access = ReadAccessLevel(read.Results, 3);
        if ((userAccess ?? access) is { } level && (level & AccessLevels.CurrentWrite) == 0)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadNotWritable, "The server reports that this variable is read-only.");
        }

        var current = read.Results[0];
        BuiltInType type;
        bool isArray;
        if (!StatusCode.IsBad(current.StatusCode) && !current.WrappedValue.IsNull)
        {
            type = current.WrappedValue.TypeInfo.BuiltInType;
            isArray = current.WrappedValue.TypeInfo.ValueRank >= ValueRanks.OneDimension;
        }
        else if (read.Results[1].WrappedValue.AsBoxedObject() is NodeId dataType)
        {
            type = TypeInfo.GetBuiltInType(dataType, session.TypeTree);
            isArray = read.Results[2].WrappedValue.AsBoxedObject() is int rank && rank >= ValueRanks.OneDimension;
        }
        else
        {
            throw new InvalidOperationException("Cannot determine the data type of the value.");
        }

        var value = UaValueParser.Parse(text, type, isArray);
        var toWrite = new List<WriteValue>
        {
            new WriteValue { NodeId = nodeId, AttributeId = Attributes.Value, Value = new DataValue(UaValueFormatter.ToVariant(value)) },
        };
        var response = await session.WriteAsync(null, toWrite, cancellationToken).ConfigureAwait(false);
        if (response.Results.Count > 0 && StatusCode.IsBad(response.Results[0]))
        {
            throw new ServiceResultException(response.Results[0]);
        }
    }

    private static byte? ReadAccessLevel(ArrayOf<DataValue> results, int index) =>
        results.Count > index && !StatusCode.IsBad(results[index].StatusCode)
            && results[index].WrappedValue.AsBoxedObject() is byte access ? access : null;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private const string SubscriptionPrefix = "Watch@";

    /// <summary>Items with the same refresh time share one subscription whose publishing interval equals it.</summary>
    private async Task<UaSubscription> GetOrCreateSubscriptionAsync(double refreshMs, CancellationToken cancellationToken)
    {
        var interval = Math.Max(0, (int)refreshMs);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            if (_subscriptions.TryGetValue(interval, out var existing))
            {
                return existing;
            }

            var subscription = AddSubscription(
                session,
                SubscriptionPrefix + interval.ToString(System.Globalization.CultureInfo.InvariantCulture),
                interval,
                keepAliveCount: interval >= 5000 ? 3u : 10u,
                lifetimeCount: interval >= 5000 ? 30u : 100u);
            _subscriptions[interval] = subscription;
            return subscription;
        }
        finally
        {
            _gate.Release();
        }
    }

    private UaSubscription AddSubscription(ManagedSession session, string name, double publishingIntervalMs, uint keepAliveCount, uint lifetimeCount, Action<ArrayOf<Variant>>? onEvent = null)
    {
        if (!session.TryGetSubscriptionManager(out var manager) || manager is null)
        {
            throw new InvalidOperationException("The session has no subscription manager.");
        }

        var subscription = UaSubscription.Add(manager, name, publishingIntervalMs, keepAliveCount, lifetimeCount, onEvent);
        _allSubscriptions[subscription] = 0;
        return subscription;
    }

    private async Task RemoveSubscriptionAsync(UaSubscription subscription)
    {
        _allSubscriptions.TryRemove(subscription, out _);
        try
        {
            await subscription.Subscription.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (Errors.IsRecoverable(ex))
        {
            // The server may be gone; it drops the subscription when its lifetime ends.
            System.Diagnostics.Trace.TraceWarning($"Removing a subscription failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves existing monitored items to the subscription for <paramref name="refreshMs"/> and updates their
    /// sampling interval. Returns the new handles; handles not created by this client are ignored.
    /// </summary>
    public async Task<IReadOnlyList<MonitorResult>> ChangeRefreshAsync(
        IReadOnlyList<IAsyncDisposable> monitors,
        Action<ValueUpdate> onUpdate,
        double refreshMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var owned = monitors.OfType<MonitorHandle>().ToList();
        var nodeIds = owned.Select(h => h.NodeId).ToList();
        var results = await MonitorManyAsync(nodeIds, onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        await StopMonitorsAsync(owned.Cast<IAsyncDisposable>(), cancellationToken).ConfigureAwait(false);
        await DeleteEmptySubscriptionsAsync().ConfigureAwait(false);
        return results;
    }

    private async Task DeleteEmptySubscriptionsAsync()
    {
        if (_session is not { Connected: true })
        {
            return;
        }

        foreach (var (interval, subscription) in _subscriptions.Where(p => p.Value.Subscription.MonitoredItems.Count == 0).ToList())
        {
            _subscriptions.Remove(interval);
            await RemoveSubscriptionAsync(subscription).ConfigureAwait(false);
        }
    }

    private ManagedSession RequireSession() =>
        _session is { Connected: true } s ? s : throw new InvalidOperationException("Not connected.");

    private static readonly TimeSpan ReconnectRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Between failed reconnect cycles the managed session reports Disconnected and starts the next one later: for
    /// the app the connection is still being restored until it is closed.
    /// </summary>
    private void OnConnectionStateChanged(object? sender, Opc.Ua.Client.ConnectionStateChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }

        switch (e.NewState)
        {
            case Opc.Ua.Client.ConnectionState.Connected when State == ConnectionState.Reconnecting:
                _reconnects++;
                _lastReconnect = DateTime.UtcNow;
                State = ConnectionState.Connected;
                break;
            case Opc.Ua.Client.ConnectionState.Reconnecting or Opc.Ua.Client.ConnectionState.Failover or Opc.Ua.Client.ConnectionState.Disconnected
                when State == ConnectionState.Connected:
                State = ConnectionState.Reconnecting;
                break;
        }
    }

    private async Task CloseCoreAsync()
    {
        _subscriptions.Clear();
        _allSubscriptions.Clear();

        if (_session is { } session)
        {
            _session = null;
            session.ConnectionStateChanged -= OnConnectionStateChanged;
            try
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                // Closing a session whose server is gone fails; the local session is disposed either way.
                System.Diagnostics.Trace.TraceWarning($"Session close failed: {ex.Message}");
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                System.Diagnostics.Trace.TraceWarning($"Session dispose failed: {ex.Message}");
            }
        }

        _structureTypes?.Dispose();
        _structureTypes = null;

        if (_applicationInstance is { } instance)
        {
            _applicationInstance = null;
            await instance.DisposeAsync().ConfigureAwait(false);
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
        _ => UaValueFormatter.Format(UaValueFormatter.ToVariant(value)),
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

    /// <summary>
    /// Stops many monitored items; the subscription manager deletes them on the server in one batch.
    /// Handles of other clients are ignored.
    /// </summary>
    public Task StopMonitoringAsync(IEnumerable<IAsyncDisposable> monitors, CancellationToken cancellationToken = default) =>
        StopMonitorsAsync(monitors, cancellationToken);

    private static Task StopMonitorsAsync(IEnumerable<IAsyncDisposable> handles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handles);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var handle in handles.OfType<MonitorHandle>())
        {
            handle.Subscription.RemoveValueHandler(handle.Item.Name);
            try
            {
                handle.Subscription.Subscription.MonitoredItems.TryRemove(handle.Item.ClientHandle);
            }
            catch (ObjectDisposedException)
            {
                // Closed meanwhile: the session deleted its subscriptions.
            }
        }

        return Task.CompletedTask;
    }

    private sealed class MonitorHandle(UaSubscription subscription, IMonitoredItem item, OptionsMonitor<UaItemOptions> options) : IAsyncDisposable
    {
        public UaSubscription Subscription => subscription;

        public IMonitoredItem Item => item;

        /// <summary>Changing its value applies new monitoring settings.</summary>
        public OptionsMonitor<UaItemOptions> Options => options;

        public NodeId NodeId => options.CurrentValue.StartNodeId;

        public async ValueTask DisposeAsync() => await StopMonitorsAsync([this]).ConfigureAwait(false);
    }
}
