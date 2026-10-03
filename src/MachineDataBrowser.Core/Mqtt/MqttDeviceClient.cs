using System.Globalization;
using MQTTnet;
using MQTTnet.Protocol;
using Opc.Ua;

namespace MachineDataBrowser.Core.Mqtt;

/// <summary>
/// MQTT client (MQTT 3.1.1 / 5, TCP or WebSocket, optional TLS) behind <see cref="IDeviceClient"/>.
/// It subscribes to one topic filter (default <c>#</c>), builds the tree from the messages it receives and decodes
/// Sparkplug B. MQTT pushes values; the refresh time is a maximum update rate per monitored item (the latest value at
/// most once per interval), and 0 delivers every message.
/// </summary>
public sealed class MqttDeviceClient : IDeviceClient, IDynamicAddressSpace, IConnectionDiagnosticsSource, IPausableDiscovery
{
    public const int DefaultMaxTopics = 20_000;

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Subscriber>> _subscribers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMqttClient? _client;
    private MqttClientOptions? _options;
    private MqttEndpoint? _endpoint;
    private MqttModel _model = new(DefaultMaxTopics);
    private CancellationTokenSource? _reconnect;
    private ConnectionState _state = ConnectionState.Disconnected;
    private MqttClientConnectResult? _connectResult;
    private string? _userName;
    private DateTime? _connectedAt;
    private DateTime? _lastReconnect;
    private int _reconnects;
    private long _messages;
    private long _bytes;
    private long _lastMessageTicks;
    private (long Count, DateTime At)? _rateSample;

    // Topic filters the broker currently has for us; changed only under _subscriptionGate.
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private readonly HashSet<string> _activeFilters = new(StringComparer.Ordinal);
    private volatile bool _discoveryPaused;
    private CancellationTokenSource? _autoPause;
    private DateTime? _autoPauseAtUtc;

    public MqttDeviceClient(int maxTopics = DefaultMaxTopics) => MaxTopics = maxTopics;

    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Raised (on the MQTT thread) after a message added or removed nodes.</summary>
    public event EventHandler? AddressSpaceChanged;

    public int MaxTopics { get; }

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public string? ServerUri => _endpoint is { } e ? $"MQTT {e.Host}:{e.Port} · {e.TopicFilter}" : null;

    public BrowseItem Root { get; } = new(MqttModel.Node(MqttModel.RootId), "Broker", "Broker", NodeClass.Object);

    public string ToPortableId(NodeId nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return nodeId.Identifier as string ?? nodeId.ToString();
    }

    public string ToDisplayId(NodeId nodeId) => ToPortableId(nodeId);

    public NodeId ParsePortableId(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return MqttModel.IsValidId(text.Trim()) ? MqttModel.Node(text.Trim()) : throw new ServiceResultException(StatusCodes.BadNodeIdInvalid, $"'{text}' is not an MQTT node id.");
    }

    public async Task ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            State = ConnectionState.Connecting;
            try
            {
                _endpoint = MqttEndpoint.Parse(options.EndpointUrl);
            }
            catch (FormatException ex)
            {
                State = ConnectionState.Disconnected;
                throw new ServiceResultException(StatusCodes.BadTcpEndpointUrlInvalid, ex.Message);
            }

            _model = new MqttModel(MaxTopics);
            _messages = 0;
            _bytes = 0;
            _lastMessageTicks = 0;
            _reconnects = 0;
            _lastReconnect = null;
            _rateSample = null;
            _discoveryPaused = false;
            _options = BuildOptions(_endpoint, options);
            _userName = string.IsNullOrEmpty(options.UserName) ? _endpoint.UserName : options.UserName;
            _client = new MqttClientFactory().CreateMqttClient();
            _client.ApplicationMessageReceivedAsync += OnMessageAsync;
            _client.DisconnectedAsync += OnDisconnectedAsync;
            try
            {
                await ConnectAndSubscribeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && Errors.IsRecoverable(ex))
            {
                await CloseCoreAsync().ConfigureAwait(false);
                throw new ServiceResultException(StatusCodes.BadCommunicationError, $"MQTT connection to {_endpoint?.Host ?? options.EndpointUrl} failed: {ex.Message}", ex);
            }

            _connectedAt = DateTime.UtcNow;
            State = ConnectionState.Connected;
            if (options.AutoPauseDiscoverySeconds > 0)
            {
                StartAutoPause(options.AutoPauseDiscoverySeconds);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ConnectionDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        if (_endpoint is not { } endpoint || _options is not { } options)
        {
            throw new InvalidOperationException("Not connected.");
        }

        var result = _connectResult;
        var messages = Interlocked.Read(ref _messages);
        var lastTicks = Interlocked.Read(ref _lastMessageTicks);
        var topics = _model.TopicCount.ToString(CultureInfo.InvariantCulture);
        var max = _model.MaxTopics.ToString(CultureInfo.InvariantCulture);
        var info = new List<(string, string)>
        {
            ("Broker", $"{endpoint.Host}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}"),
            ("Transport", (endpoint.WebSocket ? $"WebSocket {endpoint.WebSocketUri}" : "TCP") + (endpoint.Tls ? " · TLS" : " · no TLS")),
            ("Protocol", options.ProtocolVersion switch
            {
                MQTTnet.Formatter.MqttProtocolVersion.V500 => "MQTT 5.0",
                MQTTnet.Formatter.MqttProtocolVersion.V311 => "MQTT 3.1.1",
                var v => v.ToString(),
            }),
            ("Client ID", result?.AssignedClientIdentifier is { Length: > 0 } assigned ? $"{assigned} (assigned by broker)" : options.ClientId),
            ("User", _userName is { Length: > 0 } user ? user : "Anonymous"),
            ("Topic filter", endpoint.TopicFilter),
            ("Keep-alive", result is { ServerKeepAlive: > 0 }
                ? $"every {result.ServerKeepAlive.ToString(CultureInfo.InvariantCulture)} s (set by broker)"
                : $"every {DiagnosticsFormat.Duration(options.KeepAlivePeriod.TotalMilliseconds)}"),
            ("Connected since", DiagnosticsFormat.Since(_connectedAt)),
            ("Reconnects", DiagnosticsFormat.Reconnects(_reconnects, _lastReconnect)),
            ("Messages", $"{DiagnosticsFormat.Rate(messages, ref _rateSample)} · {messages.ToString("N0", CultureInfo.InvariantCulture)} received · {DiagnosticsFormat.Bytes(Interlocked.Read(ref _bytes))}"),
            ("Last message", lastTicks == 0 ? "none yet" : DiagnosticsFormat.Ago(new DateTime(lastTicks, DateTimeKind.Utc))),
            ("Topics", _model.DroppedTopics > 0
                ? $"{topics} (limit {max} reached, {_model.DroppedTopics.ToString(CultureInfo.InvariantCulture)} messages dropped)"
                : $"{topics} of max {max}"),
        };

        var spb = _model.SparkplugSummary();
        if (spb.Edges > 0)
        {
            info.Add(("Sparkplug B", $"{spb.EdgesOnline}/{spb.Edges} edge nodes online · {spb.DevicesOnline}/{spb.Devices} devices online"));
        }

        int monitored;
        lock (_lock)
        {
            monitored = _subscribers.Values.Sum(l => l.Count);
        }

        info.Add(("Monitored items", monitored.ToString(CultureInfo.InvariantCulture)));
        info.Add(("Discovery", _discoveryPaused
            ? $"paused · {_activeFilters.Count.ToString(CultureInfo.InvariantCulture)} topic subscriptions for the monitored items"
            : _autoPauseAtUtc is { } at && _autoPause is not null
                ? $"on · pauses in {Math.Max(0, (int)Math.Ceiling((at - DateTime.UtcNow).TotalSeconds)).ToString(CultureInfo.InvariantCulture)} s"
                : "on · receiving the whole topic filter"));

        // What the broker announced in its CONNACK: explains e.g. writes that don't stick (no retain).
        if (result is not null)
        {
            var limits = new List<string>
            {
                $"max QoS {(int)result.MaximumQoS}",
                result.RetainAvailable ? "retain" : "no retain",
            };
            if (!result.WildcardSubscriptionAvailable)
            {
                limits.Add("no wildcard subscriptions");
            }

            if (result.MaximumPacketSize is { } size and > 0)
            {
                limits.Add($"max packet {DiagnosticsFormat.Bytes(size)}");
            }

            info.Add(("Broker limits", string.Join(" · ", limits)));
            if (result.ResponseInformation is { Length: > 0 } response)
            {
                info.Add(("Broker info", response));
            }
        }

        return Task.FromResult(new ConnectionDiagnostics(info, []));
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

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
        _subscriptionGate.Dispose();
    }

    public Task<IReadOnlyList<BrowseItem>> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_model.Browse(Id(nodeId)));

    public Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default)
    {
        var path = MqttModel.PathFromRoot(Id(nodeId));
        return Task.FromResult<IReadOnlyList<NodeId>>(path.Count > maxDepth ? [] : path);
    }

    public Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_model.Attributes(Id(nodeId)));

    /// <summary>
    /// Publishes a write: topics and JSON fields get a new payload on their own topic (the broker accepting it is all
    /// MQTT confirms); Sparkplug metrics get an NCMD/DCMD that the edge node may apply and report back.
    /// </summary>
    public async Task WriteValueAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_client is not { IsConnected: true } client)
        {
            throw new InvalidOperationException("Not connected.");
        }

        var write = _model.PrepareWrite(Id(nodeId), text, DateTime.UtcNow);
        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(write.Topic)
            .WithPayload(write.Payload)
            .WithRetainFlag(write.Retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (write.ContentType is { Length: > 0 } contentType)
        {
            builder = builder.WithContentType(contentType);
        }

        foreach (var (key, value) in write.UserProperties)
        {
            builder = builder.WithUserProperty(key, System.Text.Encoding.UTF8.GetBytes(value));
        }

        var result = await client.PublishAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new ServiceResultException(StatusCodes.BadNotWritable, $"Broker rejected the publish to '{write.Topic}': {result.ReasonCode} {result.ReasonString}");
        }
    }

    public Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        return Task.FromResult<IReadOnlyList<object?>>([.. nodeIds.Select(id => _model.Current(Id(id)) is { } u && StatusCode.IsGood(u.Status) ? u.Raw : null)]);
    }

    public async Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<NodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(onUpdate);
        var results = new List<MonitorResult>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            var id = Id(nodeId);
            if (!(id.StartsWith("t:", StringComparison.Ordinal) || id.StartsWith("m:", StringComparison.Ordinal)))
            {
                results.Add(new MonitorResult(nodeId, null, new ServiceResult(StatusCodes.BadNodeIdInvalid, $"'{id}' is a folder, not a value.")));
                continue;
            }

            var subscriber = new Subscriber(this, id, onUpdate, samplingIntervalMs);
            lock (_lock)
            {
                if (!_subscribers.TryGetValue(id, out var list))
                {
                    _subscribers[id] = list = [];
                }

                list.Add(subscriber);
            }

            // Like an OPC UA subscription, deliver the value already known (e.g. a retained message) at once.
            if (_model.Current(id) is { } current)
            {
                subscriber.Offer(current);
            }

            results.Add(new MonitorResult(nodeId, subscriber, ServiceResult.Good));
        }

        if (_discoveryPaused)
        {
            await SyncSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    public async Task<IReadOnlyList<MonitorResult>> ChangeRefreshAsync(
        IReadOnlyList<IAsyncDisposable> monitors,
        Action<ValueUpdate> onUpdate,
        double refreshMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var own = monitors.OfType<Subscriber>().Where(s => ReferenceEquals(s.Owner, this)).ToList();
        var results = await MonitorManyAsync([.. own.Select(s => MqttModel.Node(s.Id))], onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        foreach (var subscriber in own)
        {
            await subscriber.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    private static string Id(NodeId nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return nodeId.Identifier as string ?? throw new ServiceResultException(StatusCodes.BadNodeIdInvalid, $"'{nodeId}' is not an MQTT node id.");
    }

    private static MqttClientOptions BuildOptions(MqttEndpoint endpoint, ConnectOptions options)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"machinedatabrowser-{Guid.NewGuid():N}"[..23])
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithCleanStart()
            // Short keep-alive: a broker behind a port proxy (Docker, load balancer) can vanish without closing the TCP
            // connection, and only the missing PINGRESP reveals it (after 1.5x the period).
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(5))
            .WithTimeout(TimeSpan.FromSeconds(5));
        builder = endpoint.WebSocket
            ? builder.WithWebSocketServer(o => o.WithUri(endpoint.WebSocketUri))
            : builder.WithTcpServer(endpoint.Host, endpoint.Port);

        var user = string.IsNullOrEmpty(options.UserName) ? endpoint.UserName : options.UserName;
        var password = string.IsNullOrEmpty(options.UserName) ? endpoint.Password : options.Password;
        if (!string.IsNullOrEmpty(user))
        {
            builder = builder.WithCredentials(user, password ?? string.Empty);
        }

        if (endpoint.Tls)
        {
            builder = builder.WithTlsOptions(tls =>
            {
                tls.UseTls();

                // MQTTnet checks revocation online, which fails for valid certificates whose CA publishes no OCSP
                // (Let's Encrypt, since 2025). Chain, expiry and host name are still verified, as browsers and
                // WebSocket TLS (SslStream defaults) do.
                tls.WithTargetHost(endpoint.Host);
                tls.WithIgnoreCertificateRevocationErrors();
                if (options.AutoAcceptUntrustedCertificates)
                {
                    tls.WithCertificateValidationHandler(_ => true);
                }
            });
        }

        return builder.Build();
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken)
    {
        _connectResult = await _client!.ConnectAsync(_options!, cancellationToken).ConfigureAwait(false);
        await _subscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Clean start: the broker has forgotten our subscriptions.
            _activeFilters.Clear();
            await SyncSubscriptionsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }

    public bool IsDiscoveryPaused => _discoveryPaused;

    public event EventHandler? DiscoveryPausedChanged;

    public async Task SetDiscoveryPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        if (_client is not { IsConnected: true })
        {
            throw new InvalidOperationException("Not connected.");
        }

        // The user decided: a pending automatic pause must not override it.
        CancelAutoPause();
        await ApplyDiscoveryPausedAsync(paused, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyDiscoveryPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        var changed = _discoveryPaused != paused;
        _discoveryPaused = paused;
        await SyncSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            DiscoveryPausedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StartAutoPause(int seconds)
    {
        var cts = new CancellationTokenSource();
        _autoPauseAtUtc = DateTime.UtcNow.AddSeconds(seconds);
        _autoPause = cts;
        _ = Task.Run(async () =>
        {
            // The task owns the source: cancelling only signals it, so the token stays usable until here.
            using (cts)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ConfigureAwait(false);

                    // Also while reconnecting: the flag is set and the reconnect subscribes only to monitored topics.
                    await ApplyDiscoveryPausedAsync(true, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (Errors.IsRecoverable(ex))
                {
                    System.Diagnostics.Trace.TraceWarning($"MQTT automatic discovery pause failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.CompareExchange(ref _autoPause, null, cts);
                }
            }
        });
    }

    private void CancelAutoPause() => Interlocked.Exchange(ref _autoPause, null)?.Cancel();

    private async Task SyncSubscriptionsAsync(CancellationToken cancellationToken)
    {
        await _subscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncSubscriptionsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }

    /// <summary>Subscribes to the topic filter, or while discovery is paused only to the monitored topics.</summary>
    private async Task SyncSubscriptionsCoreAsync(CancellationToken cancellationToken)
    {
        if (_client is not { IsConnected: true } client || _endpoint is not { } endpoint)
        {
            return;
        }

        HashSet<string> wanted;
        if (_discoveryPaused)
        {
            lock (_lock)
            {
                wanted = [.. _subscribers.Keys.SelectMany(FiltersFor)];
            }
        }
        else
        {
            wanted = [endpoint.TopicFilter];
        }

        // Subscribe before unsubscribing, so values of topics in both sets never stop.
        var added = wanted.Where(f => !_activeFilters.Contains(f)).ToList();
        if (added.Count > 0)
        {
            var subscribe = new MqttClientSubscribeOptionsBuilder();
            foreach (var filter in added)
            {
                subscribe = subscribe.WithTopicFilter(f => f.WithTopic(filter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce));
            }

            await client.SubscribeAsync(subscribe.Build(), cancellationToken).ConfigureAwait(false);
            _activeFilters.UnionWith(added);
        }

        var removed = _activeFilters.Where(f => !wanted.Contains(f)).ToList();
        if (removed.Count > 0)
        {
            var unsubscribe = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var filter in removed)
            {
                unsubscribe = unsubscribe.WithTopicFilter(filter);
            }

            await client.UnsubscribeAsync(unsubscribe.Build(), cancellationToken).ConfigureAwait(false);
            _activeFilters.ExceptWith(removed);
        }
    }

    /// <summary>
    /// The topics a monitored node's values arrive on: its topic (JSON fields too), and for Sparkplug metrics every
    /// message of the edge node (births, deaths) and of the device.
    /// </summary>
    internal static IEnumerable<string> FiltersFor(string id)
    {
        if (id.StartsWith("t:", StringComparison.Ordinal))
        {
            var rest = id[2..];
            var hash = rest.IndexOf('#', StringComparison.Ordinal);
            yield return hash < 0 ? rest : rest[..hash];
        }
        else if (id.StartsWith("m:", StringComparison.Ordinal) && id[2..].Split('|') is [var group, var edge, var device, ..])
        {
            yield return $"{SparkplugB.Namespace}/{group}/+/{edge}";
            if (device.Length > 0)
            {
                yield return $"{SparkplugB.Namespace}/{group}/+/{edge}/{device}";
            }
        }
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        // MQTTnet thread: never let an exception escape into the library.
        try
        {
            var message = e.ApplicationMessage;
            Interlocked.Increment(ref _messages);
            Interlocked.Add(ref _bytes, message.Payload.Length);
            Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);
            var version = _model.Version;
            var changed = _model.Apply(
                message.Topic,
                message.Payload.IsSingleSegment ? message.Payload.FirstSpan : System.Buffers.BuffersExtensions.ToArray(message.Payload),
                message.Retain,
                (int)message.QualityOfServiceLevel,
                message.ContentType,
                DateTime.UtcNow,
                message.UserProperties?.Select(p => new KeyValuePair<string, string>(p.Name, System.Text.Encoding.UTF8.GetString(p.ValueBuffer.Span))).ToList());
            Notify(changed);
            if (_model.Version != version)
            {
                AddressSpaceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (Errors.IsRecoverable(ex))
        {
            System.Diagnostics.Trace.TraceError($"MQTT message on '{e.ApplicationMessage?.Topic}' could not be processed: {ex}");
        }

        return Task.CompletedTask;
    }

    private void Notify(IReadOnlyList<string> changedKeys)
    {
        if (changedKeys.Count == 0)
        {
            return;
        }

        List<(Subscriber Subscriber, string Id)> targets;
        lock (_lock)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            targets = [];
            foreach (var key in changedKeys)
            {
                // A topic message also updates the JSON fields inside it (keys "t:<topic>#...").
                foreach (var (id, list) in _subscribers)
                {
                    if (id == key || (key.StartsWith("t:", StringComparison.Ordinal) && id.StartsWith(key + "#", StringComparison.Ordinal)))
                    {
                        targets.AddRange(list.Select(s => (s, id)));
                    }
                }
            }
        }

        foreach (var group in targets.GroupBy(t => t.Id))
        {
            if (_model.Current(group.Key) is not { } update)
            {
                continue;
            }

            foreach (var (subscriber, _) in group)
            {
                subscriber.Offer(update);
            }
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_reconnect is not null || _client is null || State == ConnectionState.Disconnected)
        {
            return Task.CompletedTask;
        }

        State = ConnectionState.Reconnecting;
        var cts = new CancellationTokenSource();
        _reconnect = cts;
        _ = Task.Run(() => ReconnectLoopAsync(cts));
        return Task.CompletedTask;
    }

    private async Task ReconnectLoopAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(ReconnectDelay, cts.Token).ConfigureAwait(false);
                try
                {
                    await ConnectAndSubscribeAsync(cts.Token).ConfigureAwait(false);
                    _reconnects++;
                    _lastReconnect = DateTime.UtcNow;
                    State = ConnectionState.Connected;
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && Errors.IsRecoverable(ex))
                {
                    System.Diagnostics.Trace.TraceWarning($"MQTT reconnect failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_reconnect, cts))
            {
                _reconnect = null;
            }

            cts.Dispose();
        }
    }

    private async Task CloseCoreAsync()
    {
        CancelAutoPause();
        var reconnect = Interlocked.Exchange(ref _reconnect, null);
        if (reconnect is not null)
        {
            await reconnect.CancelAsync().ConfigureAwait(false);
        }

        if (_client is { } client)
        {
            _client = null;
            State = ConnectionState.Disconnected;
            client.ApplicationMessageReceivedAsync -= OnMessageAsync;
            client.DisconnectedAsync -= OnDisconnectedAsync;
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                System.Diagnostics.Trace.TraceWarning($"MQTT disconnect failed: {ex.Message}");
            }

            client.Dispose();
        }

        lock (_lock)
        {
            _subscribers.Clear();
        }

        _endpoint = null;
        _connectResult = null;
        _connectedAt = null;
        _discoveryPaused = false;
        State = ConnectionState.Disconnected;
    }

    /// <summary>While paused, stop receiving a topic nobody monitors any more; a failure only costs some traffic.</summary>
    private async ValueTask UnsubscribeUnusedAsync()
    {
        try
        {
            await SyncSubscriptionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (Errors.IsRecoverable(ex))
        {
            System.Diagnostics.Trace.TraceWarning($"MQTT unsubscribe failed: {ex.Message}");
        }
    }

    private void Remove(Subscriber subscriber)
    {
        lock (_lock)
        {
            if (_subscribers.TryGetValue(subscriber.Id, out var list) && list.Remove(subscriber) && list.Count == 0)
            {
                _subscribers.Remove(subscriber.Id);
            }
        }
    }

    /// <summary>
    /// One monitored item. With an interval it forwards at most one update per interval: the first at once, later
    /// ones as the latest value when the interval has passed (like an OPC UA sampling interval). 0 forwards every message.
    /// </summary>
    private sealed class Subscriber(MqttDeviceClient owner, string id, Action<ValueUpdate> onUpdate, double intervalMs) : IAsyncDisposable
    {
        private readonly Lock _lock = new();
        private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(Math.Max(0, intervalMs));
        private long _lastDelivered = long.MinValue / 2;
        private ValueUpdate? _pending;
        private Timer? _timer;
        private bool _disposed;

        public MqttDeviceClient Owner { get; } = owner;

        public string Id { get; } = id;

        public void Offer(ValueUpdate update)
        {
            if (_interval == TimeSpan.Zero)
            {
                Deliver(update);
                return;
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                var now = Environment.TickCount64;
                var due = _lastDelivered + (long)_interval.TotalMilliseconds - now;
                if (due <= 0 && _pending is null)
                {
                    _lastDelivered = now;
                }
                else
                {
                    // Keep only the newest value; the timer sends it when the interval is over.
                    var schedule = _pending is null;
                    _pending = update;
                    if (schedule)
                    {
                        _timer ??= new Timer(_ => Flush());
                        _timer.Change(TimeSpan.FromMilliseconds(Math.Max(0, due)), Timeout.InfiniteTimeSpan);
                    }

                    return;
                }
            }

            Deliver(update);
        }

        public ValueTask DisposeAsync()
        {
            lock (_lock)
            {
                _disposed = true;
                _pending = null;
                _timer?.Dispose();
                _timer = null;
            }

            Owner.Remove(this);
            return Owner.IsDiscoveryPaused ? Owner.UnsubscribeUnusedAsync() : ValueTask.CompletedTask;
        }

        private void Flush()
        {
            ValueUpdate? update;
            lock (_lock)
            {
                update = _pending;
                _pending = null;
                _lastDelivered = Environment.TickCount64;
            }

            if (update is not null)
            {
                Deliver(update);
            }
        }

        private void Deliver(ValueUpdate update)
        {
            // MQTT or timer thread: never let a handler exception escape.
            try
            {
                onUpdate(update);
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                System.Diagnostics.Trace.TraceError($"MQTT value handler failed: {ex}");
            }
        }
    }
}

/// <summary>
/// <c>mqtt://[user:password@]host[:port][/topic/filter]</c> (TLS: <c>mqtts://</c>; WebSocket: <c>ws://</c>/<c>wss://</c>,
/// where the path is the WebSocket path). The topic filter can also be given as <c>?topic=plant/%23</c>; default <c>#</c>.
/// </summary>
public sealed record MqttEndpoint(string Host, int Port, bool Tls, bool WebSocket, string WebSocketUri, string TopicFilter, string? UserName, string? Password)
{
    public static readonly string[] Schemes = ["mqtt", "mqtts", "ws", "wss"];

    public static bool IsMqtt(string endpointUrl)
    {
        var trimmed = endpointUrl.TrimStart();
        return Schemes.Any(s => trimmed.StartsWith(s + "://", StringComparison.OrdinalIgnoreCase));
    }

    public static MqttEndpoint Parse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !Schemes.Contains(uri.Scheme.ToLowerInvariant()) || uri.Host.Length == 0)
        {
            throw new FormatException($"'{url}' is not an MQTT address. Expected mqtt://host[:port][/topic/#], mqtts://, ws:// or wss://");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var tls = scheme is "mqtts" or "wss";
        var webSocket = scheme is "ws" or "wss";
        var port = uri.IsDefaultPort || uri.Port <= 0 ? scheme switch { "mqtt" => 1883, "mqtts" => 8883, "ws" => 80, _ => 443 } : uri.Port;

        string? filter = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0].Equals("topic", StringComparison.OrdinalIgnoreCase) && kv.Length == 2)
            {
                filter = Uri.UnescapeDataString(kv[1]);
            }
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (!webSocket && filter is null && path.Length > 0)
        {
            filter = path;
        }

        // '#' in the URL is a fragment: mqtt://host/plant/# arrives as path "plant/" plus fragment "".
        if (!webSocket && filter is not null && url.TrimEnd().EndsWith('#') && !filter.EndsWith('#'))
        {
            filter += "#";
        }

        string? user = null, password = null;
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }

        var wsUri = webSocket ? $"{scheme}://{uri.Host}:{port.ToString(CultureInfo.InvariantCulture)}{(uri.AbsolutePath.Length > 1 ? uri.AbsolutePath : "/mqtt")}" : string.Empty;
        return new MqttEndpoint(uri.Host, port, tls, webSocket, wsUri, string.IsNullOrWhiteSpace(filter) ? "#" : filter, user, password);
    }
}
