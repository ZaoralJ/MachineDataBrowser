using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MachineDataBrowser.Core;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Driver.MtConnect;

/// <summary>
/// Read-only client for MTConnect agents over their REST API (<c>/probe</c>, <c>/current</c>, <c>/sample</c>).
/// <list type="bullet">
/// <item>ns=2: devices and components by component id (portable <c>@id</c>), plus the root <c>@/</c>;</item>
/// <item>ns=1: data items by their id, unique within an agent.</item>
/// </list>
/// Monitoring polls <c>/sample</c> from the last sequence per refresh time, so every observation the agent buffered
/// in between is delivered, not only the latest.
/// </summary>
public sealed class MtConnectClient : IDeviceClient, IConnectionDiagnosticsSource, IEngineeringUnitsSource, IHistorySource
{
    internal const ushort DataItemNamespace = 1;
    internal const ushort ComponentNamespace = 2;
    internal const int DefaultPort = 5000;
    private const int SampleCount = 1000;
    private const int MaxIdsPerPath = 40;
    private const int MaxHistoryPages = 200;

    private static readonly NodeId RootId = new("/", ComponentNamespace);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<int, PollGroup> _groups = [];
    private HttpClient? _http;
    private Uri? _baseUri;
    private MtcModel? _model;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DateTime? _connectedAt;
    private DateTime? _lastLinkLoss;
    private int _linkLosses;
    private long _requests;
    private (long Count, DateTime At)? _rateSample;
    private MtcHeader? _lastHeader;

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

    public bool IsReadOnly => true;

    public string? ServerUri => _baseUri is { } uri
        ? $"MTConnect agent {uri}{(_model?.Header.Version is { } v ? $" (version {v})" : string.Empty)}"
        : null;

    public BrowseItem Root { get; } = new(RootId, "Devices", "Devices", NodeClass.Object);

    public string ToPortableId(NodeId nodeId) =>
        nodeId.NamespaceIndex == ComponentNamespace ? "@" + nodeId.IdentifierAsString : nodeId.IdentifierAsString;

    /// <summary>The data item id; components as <c>@id</c>.</summary>
    public string ToDisplayId(NodeId nodeId) => ToPortableId(nodeId);

    public NodeId ParsePortableId(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        text = text.Trim();
        return text.StartsWith('@') ? new NodeId(text[1..], ComponentNamespace) : new NodeId(text, DataItemNamespace);
    }

    /// <summary>
    /// The agent's base URL: <c>mtconnect://</c> becomes <c>http://</c> on port 5000 unless given; a path (a device
    /// name or a proxy prefix) is kept, so <c>/probe</c> is requested below it.
    /// </summary>
    public static Uri ParseEndpoint(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0
            || uri.Scheme.ToUpperInvariant() is not ("MTCONNECT" or "HTTP" or "HTTPS"))
        {
            throw new FormatException($"'{url}' is not an MTConnect agent address. Expected mtconnect://host[:port][/device] or http://host:port[/device], e.g. mtconnect://192.168.1.20:5000");
        }

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (uri.Scheme.Equals("mtconnect", StringComparison.OrdinalIgnoreCase))
        {
            builder.Scheme = Uri.UriSchemeHttp;
            builder.Port = uri.Port > 0 ? uri.Port : DefaultPort;
        }

        var path = builder.Path.TrimEnd('/');
        foreach (var request in new[] { "/probe", "/current", "/sample" })
        {
            if (path.EndsWith(request, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^request.Length];
            }
        }

        builder.Path = path + "/";
        return builder.Uri;
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
                _baseUri = ParseEndpoint(options.EndpointUrl);
            }
            catch (FormatException ex)
            {
                throw new DeviceClientException(DeviceStatusCodes.BadTcpEndpointUrlInvalid, ex.Message);
            }

            var trustAll = options.AutoAcceptUntrustedCertificates;
            var handler = new SocketsHttpHandler
            {
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, errors) => trustAll || errors == System.Net.Security.SslPolicyErrors.None },
            };
            _http = new HttpClient(handler, disposeHandler: true);
            _http.Timeout = TimeSpan.FromSeconds(10);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            if (!string.IsNullOrEmpty(options.UserName))
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.UserName}:{options.Password}"));
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            _model = MtcModel.Parse(await GetAsync("probe", cancellationToken).ConfigureAwait(false));
            _connectedAt = DateTime.UtcNow;
            _linkLosses = 0;
            _lastLinkLoss = null;
            _rateSample = null;
            State = ConnectionState.Connected;
        }
        catch
        {
            _http?.Dispose();
            _http = null;
            _baseUri = null;
            _model = null;
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

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public Task<IReadOnlyList<BrowseItem>> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var model = RequireModel();
        if (nodeId == RootId)
        {
            return Task.FromResult<IReadOnlyList<BrowseItem>>([.. model.Devices.Select(ToBrowseItem)]);
        }

        if (nodeId.NamespaceIndex == ComponentNamespace && model.Components.TryGetValue(nodeId.IdentifierAsString, out var component))
        {
            return Task.FromResult<IReadOnlyList<BrowseItem>>([.. component.Components.Select(ToBrowseItem), .. component.DataItems.Select(ToBrowseItem)]);
        }

        if (nodeId.NamespaceIndex == DataItemNamespace && model.DataItems.ContainsKey(nodeId.IdentifierAsString))
        {
            return Task.FromResult<IReadOnlyList<BrowseItem>>([]);
        }

        throw new DeviceClientException(DeviceStatusCodes.BadNodeIdUnknown, $"'{ToPortableId(nodeId)}' is not in the agent's device model.");
    }

    public Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default)
    {
        var model = RequireModel();
        var path = new List<NodeId>();
        MtcComponent? component;
        if (nodeId == RootId)
        {
            component = null;
        }
        else if (nodeId.NamespaceIndex == DataItemNamespace && model.DataItems.TryGetValue(nodeId.IdentifierAsString, out var item))
        {
            path.Add(nodeId);
            component = item.Component;
        }
        else if (nodeId.NamespaceIndex == ComponentNamespace && model.Components.TryGetValue(nodeId.IdentifierAsString, out var found))
        {
            component = found;
        }
        else
        {
            return Task.FromResult<IReadOnlyList<NodeId>>([]);
        }

        for (; component is not null; component = component.Parent)
        {
            path.Add(new NodeId(component.Id, ComponentNamespace));
        }

        path.Add(RootId);
        path.Reverse();
        return Task.FromResult<IReadOnlyList<NodeId>>(path.Count > maxDepth ? [] : path);
    }

    public async Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var model = RequireModel();
        var result = new List<AttributeValue> { new("NodeId", ToPortableId(nodeId)) };
        if (nodeId == RootId)
        {
            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            result.Add(new("DisplayName", Root.DisplayName));
            result.Add(new("Agent", _baseUri?.ToString() ?? string.Empty));
            if (model.Header.Version is { } version)
            {
                result.Add(new("Version", version));
            }

            return result;
        }

        if (nodeId.NamespaceIndex == ComponentNamespace && model.Components.TryGetValue(nodeId.IdentifierAsString, out var component))
        {
            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            result.Add(new("DisplayName", component.DisplayName));
            result.Add(new("Component", component.Element));
            result.Add(new("Id", component.Id));
            Add(result, "Name", component.Name);
            Add(result, "Uuid", component.Uuid);
            Add(result, "Description", component.Description);
            result.Add(new("Device", Device(component).DisplayName));
            return result;
        }

        if (nodeId.NamespaceIndex != DataItemNamespace || !model.DataItems.TryGetValue(nodeId.IdentifierAsString, out var item))
        {
            throw new DeviceClientException(DeviceStatusCodes.BadNodeIdUnknown, $"'{ToPortableId(nodeId)}' is not in the agent's device model.");
        }

        result.Add(new("NodeClass", nameof(NodeClass.Variable)));
        result.Add(new("DisplayName", item.DisplayName));
        result.Add(new("Id", item.Id));
        Add(result, "Name", item.Name);
        result.Add(new("Category", item.Category));
        result.Add(new("Type", item.Type));
        Add(result, "SubType", item.SubType);
        result.Add(new("DataType", item.DataType));
        Add(result, "Units", item.Units);
        if (item.NativeUnits is { } native && native != item.Units)
        {
            result.Add(new("NativeUnits", native));
        }

        Add(result, "Representation", item.Representation);
        Add(result, "CoordinateSystem", item.CoordinateSystem);
        result.Add(new("Component", $"{item.Component.Element} {item.Component.DisplayName}"));
        result.Add(new("Device", Device(item.Component).DisplayName));

        var observation = (await ReadCurrentAsync([item.Id], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(item.Id);
        result.Add(new("Value", observation is null ? DeviceStatusCodes.BadNoData.ToString()
            : observation.Status.IsGood ? ValueFormatter.Format(observation.Value) : MtcXml.Unavailable));
        if (observation is not null)
        {
            result.Add(new("SourceTimestamp", observation.Timestamp.ToString("O", CultureInfo.InvariantCulture)));
            result.Add(new("Sequence", observation.Sequence.ToString(CultureInfo.InvariantCulture)));
        }

        result.Add(new("AccessLevel", "Read"));
        return result;

        static void Add(List<AttributeValue> list, string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                list.Add(new(name, value));
            }
        }
    }

    public async Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var model = RequireModel();
        var ids = nodeIds.Where(n => n.NamespaceIndex == DataItemNamespace && model.DataItems.ContainsKey(n.IdentifierAsString))
            .Select(n => n.IdentifierAsString).Distinct(StringComparer.Ordinal).ToList();
        var current = await ReadCurrentAsync(ids, cancellationToken).ConfigureAwait(false);
        return [.. nodeIds.Select(n => n.NamespaceIndex == DataItemNamespace && current.TryGetValue(n.IdentifierAsString, out var o) && o.Status.IsGood ? o.Value : null)];
    }

    public Task WriteValueAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.FromException(new DeviceClientException(DeviceStatusCodes.BadNotWritable, "Writing values is not supported for MTConnect data items; they are read-only."));
    }

    public Task<IReadOnlyList<string?>> ReadUnitsAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var model = RequireModel();
        return Task.FromResult<IReadOnlyList<string?>>([.. nodeIds.Select(n =>
            n.NamespaceIndex == DataItemNamespace && model.DataItems.TryGetValue(n.IdentifierAsString, out var item) ? MtcXml.DisplayUnit(item.Units) : null)]);
    }

    /// <summary>What the agent still holds in its buffer for the data item, scanned page by page with <c>/sample</c>.</summary>
    public async Task<HistoryResult> ReadHistoryAsync(NodeId nodeId, DateTime startTime, DateTime endTime, int maxValues, CancellationToken cancellationToken = default)
    {
        var model = RequireModel();
        if (nodeId.NamespaceIndex != DataItemNamespace || !model.DataItems.TryGetValue(nodeId.IdentifierAsString, out var item))
        {
            throw new DeviceClientException(DeviceStatusCodes.BadNodeIdUnknown, $"'{ToPortableId(nodeId)}' is not a data item.");
        }

        var start = startTime.ToUniversalTime();
        var end = endTime.ToUniversalTime();
        var values = new List<ValueUpdate>();
        var path = PathFilter([item.Id]);
        long? from = null;
        for (var page = 0; page < MaxHistoryPages; page++)
        {
            var query = $"sample?count={SampleCount.ToString(CultureInfo.InvariantCulture)}&path={Uri.EscapeDataString(path)}"
                + (from is { } f ? $"&from={f.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
            MtcStreams streams;
            try
            {
                streams = MtcXml.ParseStreams(await GetAsync(query, cancellationToken).ConfigureAwait(false), model.DataItems);
            }
            catch (DeviceClientException ex) when (ex.StatusCode == DeviceStatusCodes.BadOutOfRange && from is not null)
            {
                // The buffer moved past the page start while scanning: continue from what is still there.
                from = null;
                continue;
            }

            foreach (var o in streams.Observations)
            {
                if (o.Timestamp > end)
                {
                    return new HistoryResult(values, false);
                }

                if (o.Timestamp >= start)
                {
                    if (values.Count >= maxValues)
                    {
                        return new HistoryResult(values, true);
                    }

                    values.Add(ToUpdate(nodeId, o));
                }
            }

            if (streams.Header.NextSequence <= 0 || streams.Header.NextSequence > streams.Header.LastSequence)
            {
                break;
            }

            from = streams.Header.NextSequence;
        }

        return new HistoryResult(values, false);
    }

    public async Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<NodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(onUpdate);
        var model = RequireModel();
        if (nodeIds.Count == 0)
        {
            return [];
        }

        // MTConnect delivers every buffered observation, so 0 (as fast as possible) still needs some polling interval.
        var interval = samplingIntervalMs <= 0 ? 100 : Math.Max(50, (int)samplingIntervalMs);
        var results = new List<MonitorResult>(nodeIds.Count);
        var entries = new List<PollEntry>();
        PollGroup group;
        lock (_lock)
        {
            if (!_groups.TryGetValue(interval, out group!))
            {
                group = new PollGroup(this, interval);
                _groups[interval] = group;
            }
        }

        foreach (var nodeId in nodeIds)
        {
            if (nodeId.NamespaceIndex != DataItemNamespace || !model.DataItems.ContainsKey(nodeId.IdentifierAsString))
            {
                results.Add(new MonitorResult(nodeId, null, nodeId.NamespaceIndex == ComponentNamespace
                    ? new DeviceError(DeviceStatusCodes.BadAttributeIdInvalid, $"'{ToPortableId(nodeId)}' is a component; watch its data items.")
                    : new DeviceError(DeviceStatusCodes.BadNodeIdUnknown, $"'{ToPortableId(nodeId)}' is not in the agent's device model.")));
                continue;
            }

            var entry = new PollEntry(nodeId, onUpdate);
            entries.Add(entry);
            results.Add(new MonitorResult(nodeId, new MonitorHandle(group, entry), DeviceError.Good));
        }

        if (entries.Count > 0)
        {
            await group.AddAsync(entries, cancellationToken).ConfigureAwait(false);
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
        var owned = monitors.OfType<MonitorHandle>().Where(h => ReferenceEquals(h.Group.Owner, this)).ToList();
        var results = await MonitorManyAsync([.. owned.Select(h => h.Entry.NodeId)], onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        foreach (var handle in owned)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    public Task<ConnectionDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var model = RequireModel();
        List<PollGroup> groups;
        lock (_lock)
        {
            groups = [.. _groups.Values.OrderBy(g => g.IntervalMs)];
        }

        var header = _lastHeader ?? model.Header;
        var requests = Interlocked.Read(ref _requests);
        var info = new List<(string, string)>
        {
            ("Agent", _baseUri?.ToString() ?? string.Empty),
            ("Protocol", "MTConnect REST (HTTP, XML)"),
            ("Version", header.Version ?? model.Header.Version ?? "unknown"),
            ("Sender", header.Sender ?? string.Empty),
            ("Instance", header.InstanceId.ToString(CultureInfo.InvariantCulture)),
            ("Buffer", $"{header.BufferSize.ToString("N0", CultureInfo.InvariantCulture)} observations · sequences {header.FirstSequence.ToString(CultureInfo.InvariantCulture)}–{header.LastSequence.ToString(CultureInfo.InvariantCulture)}"),
            ("Devices", $"{model.Devices.Count.ToString(CultureInfo.InvariantCulture)} · {model.Components.Count.ToString(CultureInfo.InvariantCulture)} components · {model.DataItems.Count.ToString(CultureInfo.InvariantCulture)} data items"),
            ("Connected since", DiagnosticsFormat.Since(_connectedAt)),
            ("Link lost", DiagnosticsFormat.Reconnects(_linkLosses, _lastLinkLoss)),
            ("Requests", $"{DiagnosticsFormat.Rate(requests, ref _rateSample)} · {requests.ToString("N0", CultureInfo.InvariantCulture)} total"),
        };

        if (groups.Count == 0)
        {
            info.Add(("Polling", "nothing monitored"));
        }

        foreach (var group in groups)
        {
            var error = group.LastError is { } last ? $" · last error: {last}" : string.Empty;
            info.Add(($"Poll {DiagnosticsFormat.Duration(group.IntervalMs)}",
                $"{group.Count.ToString(CultureInfo.InvariantCulture)} data items · {group.Observations.ToString("N0", CultureInfo.InvariantCulture)} observations · next sequence {group.NextSequence.ToString(CultureInfo.InvariantCulture)} · last {DiagnosticsFormat.Ago(group.LastPollUtc ?? DateTime.MinValue)}{error}"));
        }

        return Task.FromResult(new ConnectionDiagnostics(info, []));
    }

    private static MtcComponent Device(MtcComponent component)
    {
        while (component.Parent is { } parent)
        {
            component = parent;
        }

        return component;
    }

    private static BrowseItem ToBrowseItem(MtcComponent component) => new(
        new NodeId(component.Id, ComponentNamespace),
        component.DisplayName,
        component.Element,
        NodeClass.Object,
        HasChildren: component.Components.Count > 0 || component.DataItems.Count > 0);

    private static BrowseItem ToBrowseItem(MtcDataItem item) => new(
        new NodeId(item.Id, DataItemNamespace),
        item.DisplayName,
        item.TypeName,
        NodeClass.Variable,
        HasChildren: false);

    private static ValueUpdate ToUpdate(NodeId nodeId, MtcObservation o) => new(
        nodeId,
        o.Status.IsGood ? ValueFormatter.Format(o.Value) : MtcXml.Unavailable,
        o.Status,
        o.Timestamp,
        DateTime.UtcNow,
        o.Status.IsGood ? ValueFormatter.ToNumeric(o.Value) : null,
        o.Status.IsGood ? o.Value : null);

    /// <summary>The XPath filter agents accept for a set of data items.</summary>
    internal static string PathFilter(IEnumerable<string> ids) =>
        "//DataItem[" + string.Join(" or ", ids.Select(id => $"@id=\"{id}\"")) + "]";

    private MtcModel RequireModel() =>
        _model is { } m && State != ConnectionState.Disconnected ? m : throw new InvalidOperationException("Not connected.");

    private async Task<Dictionary<string, MtcObservation>> ReadCurrentAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var model = RequireModel();
        var result = new Dictionary<string, MtcObservation>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(MaxIdsPerPath))
        {
            var streams = MtcXml.ParseStreams(await GetAsync($"current?path={Uri.EscapeDataString(PathFilter(chunk))}", cancellationToken).ConfigureAwait(false), model.DataItems);
            foreach (var o in streams.Observations)
            {
                // A condition can have several active states; the newest wins.
                result[o.DataItemId] = o;
            }

            _lastHeader = streams.Header;
        }

        return result;
    }

    private async Task<XDocument> GetAsync(string relative, CancellationToken cancellationToken)
    {
        var http = _http ?? throw new InvalidOperationException("Not connected.");
        var uri = new Uri(_baseUri!, relative);
        Interlocked.Increment(ref _requests);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                XDocument document;
                try
                {
                    document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
                }
                catch (XmlException ex)
                {
                    throw new DeviceClientException(
                        response.IsSuccessStatusCode ? DeviceStatusCodes.BadDecodingError : DeviceStatusCodes.BadCommunicationError,
                        response.IsSuccessStatusCode
                            ? $"{uri} did not return MTConnect XML: {ex.Message}"
                            : $"{uri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                if (document.Root?.Name.LocalName == "MTConnectError")
                {
                    throw MtcXml.ToException(document.Root);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new DeviceClientException(DeviceStatusCodes.BadCommunicationError, $"{uri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                return document;
            }
        }
        catch (HttpRequestException ex)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadCommunicationError, $"{uri}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadTimeout, $"{uri}: no answer within {http.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.", ex);
        }
    }

    private async Task CloseCoreAsync()
    {
        List<PollGroup> groups;
        lock (_lock)
        {
            groups = [.. _groups.Values];
            _groups.Clear();
        }

        foreach (var group in groups)
        {
            await group.StopAsync().ConfigureAwait(false);
        }

        _http?.Dispose();
        _http = null;
        _model = null;
        _baseUri = null;
        _lastHeader = null;
        _connectedAt = null;
        State = ConnectionState.Disconnected;
    }

    private void RemoveGroup(PollGroup group)
    {
        lock (_lock)
        {
            if (_groups.TryGetValue(group.IntervalMs, out var existing) && ReferenceEquals(existing, group))
            {
                _groups.Remove(group.IntervalMs);
            }
        }
    }

    private void ReportLink(bool ok)
    {
        if (_model is null)
        {
            return;
        }

        if (!ok && State == ConnectionState.Connected)
        {
            _linkLosses++;
            _lastLinkLoss = DateTime.UtcNow;
        }

        State = ok ? ConnectionState.Connected : ConnectionState.Reconnecting;
    }

    /// <summary>Data items sharing one refresh time, read together with <c>/sample</c> from the group's sequence.</summary>
    private sealed class PollGroup(MtConnectClient owner, int intervalMs) : IDisposable
    {
        private readonly List<PollEntry> _entries = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _poll = new(1, 1);
        private Task? _loop;
        private int _stopped;
        private long _next;
        private long _instance;
        private long _observations;

        public MtConnectClient Owner { get; } = owner;

        public int IntervalMs { get; } = intervalMs;

        public long NextSequence => Interlocked.Read(ref _next);

        public long Observations => Interlocked.Read(ref _observations);

        public DateTime? LastPollUtc { get; private set; }

        public string? LastError { get; private set; }

        public int Count
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>Adds entries and reports their current values, then polls for what changes after that.</summary>
        public async Task AddAsync(IReadOnlyList<PollEntry> entries, CancellationToken cancellationToken)
        {
            await _poll.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_entries)
                {
                    _entries.AddRange(entries);
                }

                try
                {
                    await SyncAsync(entries, cancellationToken).ConfigureAwait(false);
                }
                catch (DeviceClientException ex)
                {
                    LastError = ex.Message;
                    var now = DateTime.UtcNow;
                    foreach (var entry in entries)
                    {
                        entry.Deliver(new ValueUpdate(entry.NodeId, ex.StatusCode.ToString(), ex.StatusCode, now, now), 0);
                    }
                }

                lock (_entries)
                {
                    _loop ??= Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
                }
            }
            finally
            {
                _poll.Release();
            }
        }

        public async Task RemoveAsync(PollEntry entry)
        {
            bool empty;
            lock (_entries)
            {
                if (!_entries.Remove(entry))
                {
                    return;
                }

                empty = _entries.Count == 0;
            }

            if (empty)
            {
                Owner.RemoveGroup(this);
                await StopAsync().ConfigureAwait(false);
            }
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1)
            {
                return;
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            if (_loop is { } loop)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            lock (_entries)
            {
                _entries.Clear();
            }

            Dispose();
        }

        public void Dispose()
        {
            _cts.Dispose();
            _poll.Dispose();
        }

        private PollEntry[] Snapshot()
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }

        /// <summary>Reports the current values of <paramref name="entries"/> and moves the group's cursor past them.</summary>
        private async Task SyncAsync(IReadOnlyList<PollEntry> entries, CancellationToken cancellationToken)
        {
            var ids = entries.Select(e => e.NodeId.IdentifierAsString).Distinct(StringComparer.Ordinal).ToList();
            var current = await Owner.ReadCurrentAsync(ids, cancellationToken).ConfigureAwait(false);
            var header = Owner._lastHeader!;
            foreach (var entry in entries)
            {
                if (current.TryGetValue(entry.NodeId.IdentifierAsString, out var o))
                {
                    entry.Deliver(ToUpdate(entry.NodeId, o), o.Sequence);
                }
            }

            // A cursor already running keeps its place (entries skip observations older than what they got here).
            if (Interlocked.Read(ref _next) == 0 || header.InstanceId != _instance)
            {
                Interlocked.Exchange(ref _next, header.NextSequence);
                _instance = header.InstanceId;
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _poll.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await PollAsync(cancellationToken).ConfigureAwait(false);
                    LastError = null;
                    Owner.ReportLink(true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (DeviceClientException ex) when (ex.StatusCode == DeviceStatusCodes.BadOutOfRange)
                {
                    // Fell behind the agent's buffer (or the agent restarted): start again from its current values.
                    LastError = ex.Message;
                    await ResyncAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is DeviceClientException or InvalidOperationException || (ex is not OperationCanceledException && Errors.IsRecoverable(ex)))
                {
                    LastError = ex.Message;
                    Owner.ReportLink(ex is not DeviceClientException { StatusCode: var s } || (s != DeviceStatusCodes.BadCommunicationError && s != DeviceStatusCodes.BadTimeout));
                    if (ex is not DeviceClientException)
                    {
                        System.Diagnostics.Trace.TraceError($"MTConnect poll failed: {ex}");
                    }
                }
                finally
                {
                    _poll.Release();
                }
            }
        }

        private async Task ResyncAsync(CancellationToken cancellationToken)
        {
            try
            {
                Interlocked.Exchange(ref _next, 0);
                var entries = Snapshot();
                foreach (var entry in entries)
                {
                    entry.Reset();
                }

                await SyncAsync(entries, cancellationToken).ConfigureAwait(false);
                Owner.ReportLink(true);
            }
            catch (DeviceClientException ex)
            {
                LastError = ex.Message;
            }
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            var entries = Snapshot();
            if (entries.Length == 0)
            {
                return;
            }

            var model = Owner.RequireModel();
            var byId = entries.GroupBy(e => e.NodeId.IdentifierAsString, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

            // Few items: let the agent filter. Many: one unfiltered request is cheaper than a huge XPath.
            var filter = byId.Count <= MaxIdsPerPath ? "&path=" + Uri.EscapeDataString(PathFilter(byId.Keys)) : string.Empty;
            for (var page = 0; page < 10; page++)
            {
                var from = Interlocked.Read(ref _next);
                var streams = MtcXml.ParseStreams(await Owner.GetAsync(
                    $"sample?from={from.ToString(CultureInfo.InvariantCulture)}&count={SampleCount.ToString(CultureInfo.InvariantCulture)}{filter}",
                    cancellationToken).ConfigureAwait(false), model.DataItems);
                Owner._lastHeader = streams.Header;
                LastPollUtc = DateTime.UtcNow;
                if (_instance != 0 && streams.Header.InstanceId != _instance)
                {
                    throw new DeviceClientException(DeviceStatusCodes.BadOutOfRange, "The agent restarted (new instance id).");
                }

                foreach (var o in streams.Observations)
                {
                    if (byId.TryGetValue(o.DataItemId, out var targets))
                    {
                        Interlocked.Increment(ref _observations);
                        foreach (var entry in targets)
                        {
                            entry.Deliver(ToUpdate(entry.NodeId, o), o.Sequence);
                        }
                    }
                }

                if (streams.Header.NextSequence > 0)
                {
                    Interlocked.Exchange(ref _next, streams.Header.NextSequence);
                }

                // Caught up when the cursor is past the agent's last observation.
                if (streams.Header.NextSequence <= 0 || streams.Header.NextSequence > streams.Header.LastSequence)
                {
                    return;
                }
            }
        }
    }

    private sealed class PollEntry(NodeId nodeId, Action<ValueUpdate> onUpdate)
    {
        private long _lastSequence = -1;

        public NodeId NodeId { get; } = nodeId;

        /// <summary>After an agent restart sequences start again from 1.</summary>
        public void Reset() => Interlocked.Exchange(ref _lastSequence, -1);

        /// <summary>Reports an update unless it is not newer than what was already reported.</summary>
        public void Deliver(ValueUpdate update, long sequence)
        {
            if (sequence > 0 && sequence <= Interlocked.Read(ref _lastSequence))
            {
                return;
            }

            if (sequence > 0)
            {
                Interlocked.Exchange(ref _lastSequence, sequence);
            }

            onUpdate(update);
        }
    }

    private sealed class MonitorHandle(PollGroup group, PollEntry entry) : IAsyncDisposable
    {
        public PollGroup Group => group;

        public PollEntry Entry => entry;

        public async ValueTask DisposeAsync() => await group.RemoveAsync(entry).ConfigureAwait(false);
    }
}
