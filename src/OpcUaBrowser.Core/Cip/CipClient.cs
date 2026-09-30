using System.Globalization;
using System.Text;
using libplctag;
using Opc.Ua;

namespace OpcUaBrowser.Core.Cip;

/// <summary>
/// Read-only EtherNet/IP (CIP) client for Logix controllers (ControlLogix, CompactLogix) built on libplctag.
/// Tags are exposed through the OPC UA-shaped <see cref="IDeviceClient"/> model:
/// <list type="bullet">
/// <item>ns=2 folders: <c>Root</c>, <c>Controller</c>, <c>Programs</c>, <c>Program:&lt;name&gt;</c>;</item>
/// <item>ns=1 string ids: the Logix tag path, e.g. <c>Program:Main.Motor[2].Speed</c>.</item>
/// </list>
/// Atomics, atomic arrays and STRINGs are Variables; structures and arrays of structures are Objects whose
/// children are members/elements. There are no subscriptions in CIP, so monitoring polls per refresh time and
/// reports only changes.
/// </summary>
public sealed class CipClient : IDeviceClient
{
    internal const ushort TagNamespace = 1;
    internal const ushort FolderNamespace = 2;
    private const string ProgramPrefix = "Program:";
    private const int MaxElements = 1000;

    private static readonly NodeId RootId = new("Root", FolderNamespace);
    private static readonly NodeId ControllerId = new("Controller", FolderNamespace);
    private static readonly NodeId ProgramsId = new("Programs", FolderNamespace);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IReadOnlyList<LogixSymbol>> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ushort, LogixTemplate> _templates = [];
    private readonly Dictionary<int, PollGroup> _groups = [];
    private CipEndpoint? _endpoint;
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

    public string? ServerUri => _endpoint is { } e ? $"Logix {e.Gateway} (path {e.Path})" : null;

    public BrowseItem Root { get; } = new(RootId, "Controller", "Root", NodeClass.Object);

    public string ToPortableId(NodeId nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return nodeId.NamespaceIndex == FolderNamespace ? "@" + nodeId.Identifier : (string)nodeId.Identifier;
    }

    /// <summary>The Logix tag path (<c>Program:Main.Motor.Speed</c>); folders as <c>@Programs</c>.</summary>
    public string ToDisplayId(NodeId nodeId) => ToPortableId(nodeId);

    public NodeId ParsePortableId(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text.StartsWith('@') ? new NodeId(text[1..], FolderNamespace) : new NodeId(text.Trim(), TagNamespace);
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
                _endpoint = CipEndpoint.Parse(options.EndpointUrl);
            }
            catch (FormatException ex)
            {
                throw new ServiceResultException(StatusCodes.BadTcpEndpointUrlInvalid, ex.Message);
            }


            // Listing the controller tags proves the connection and fills the first browse level.
            _scopes[string.Empty] = await ReadTagListAsync("@tags", cancellationToken).ConfigureAwait(false);
            State = ConnectionState.Connected;
        }
        catch
        {
            _endpoint = null;
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

    public async Task<IReadOnlyList<BrowseItem>> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        RequireEndpoint();

        if (nodeId == RootId)
        {
            return
            [
                new BrowseItem(ControllerId, "Controller Tags", "Controller", NodeClass.Object),
                new BrowseItem(ProgramsId, "Programs", "Programs", NodeClass.Object),
            ];
        }

        if (nodeId == ControllerId)
        {
            return await ListScopeAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        if (nodeId == ProgramsId)
        {
            var controller = await GetScopeAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            return
            [
                .. controller
                    .Where(s => s.Name.StartsWith(ProgramPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new BrowseItem(new NodeId(s.Name, FolderNamespace), s.Name[ProgramPrefix.Length..], s.Name, NodeClass.Object)),
            ];
        }

        if (nodeId.NamespaceIndex == FolderNamespace && nodeId.Identifier is string program && program.StartsWith(ProgramPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await ListScopeAsync(program, cancellationToken).ConfigureAwait(false);
        }

        var item = await ResolveAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return await ChildrenAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        var path = new List<NodeId> { RootId };
        if (nodeId == RootId)
        {
            return Task.FromResult<IReadOnlyList<NodeId>>(path);
        }

        if (nodeId.NamespaceIndex == FolderNamespace)
        {
            var name = (string)nodeId.Identifier;
            if (name.StartsWith(ProgramPrefix, StringComparison.OrdinalIgnoreCase))
            {
                path.Add(ProgramsId);
            }

            path.Add(nodeId);
            return Task.FromResult<IReadOnlyList<NodeId>>(path);
        }

        if (!TagPath.TryParse((string)nodeId.Identifier, out var parsed))
        {
            return Task.FromResult<IReadOnlyList<NodeId>>([]);
        }

        if (parsed.Program is null)
        {
            path.Add(ControllerId);
        }
        else
        {
            path.Add(ProgramsId);
            path.Add(new NodeId(parsed.Program, FolderNamespace));
        }

        path.AddRange(parsed.Prefixes().Select(p => new NodeId(p, TagNamespace)));
        return Task.FromResult<IReadOnlyList<NodeId>>(path.Count > maxDepth ? [] : path);
    }

    public async Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        RequireEndpoint();
        var result = new List<AttributeValue> { new("NodeId", ToPortableId(nodeId)) };

        if (nodeId.NamespaceIndex == FolderNamespace)
        {
            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            result.Add(new("DisplayName", ((string)nodeId.Identifier).Replace(ProgramPrefix, string.Empty, StringComparison.OrdinalIgnoreCase)));
            return result;
        }

        var item = await ResolveAsync(nodeId, cancellationToken).ConfigureAwait(false);
        result.Add(new("NodeClass", item.IsVariable ? nameof(NodeClass.Variable) : nameof(NodeClass.Object)));
        result.Add(new("TagPath", item.Path));
        result.Add(new("Scope", item.Program ?? "Controller"));
        result.Add(new("DataType", item.TypeName));
        if (item.Dimensions.Length > 0)
        {
            result.Add(new("ArrayDimensions", string.Join(',', item.Dimensions)));
        }

        if (item.Template is { } template)
        {
            result.Add(new("StructureSize", template.InstanceSize.ToString(CultureInfo.InvariantCulture)));
        }

        if (item.IsVariable)
        {
            string value;
            try
            {
                value = ValueFormatter.Format(new Variant(await ReadValueAsync(item, cancellationToken).ConfigureAwait(false)));
            }
            catch (ServiceResultException ex)
            {
                value = new StatusCode(ex.StatusCode).ToString();
            }

            result.Add(new("Value", value));
            result.Add(new("AccessLevel", "Read"));
        }

        return result;
    }

    public async Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        RequireEndpoint();
        var reads = nodeIds.Select(async id =>
        {
            try
            {
                var item = await ResolveAsync(id, cancellationToken).ConfigureAwait(false);
                return item.IsVariable ? await ReadValueAsync(item, cancellationToken).ConfigureAwait(false) : null;
            }
            catch (ServiceResultException)
            {
                return null;
            }
        });
        return await Task.WhenAll(reads).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<NodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(onUpdate);
        var endpoint = RequireEndpoint();
        if (nodeIds.Count == 0)
        {
            return [];
        }

        var interval = Math.Max(50, (int)samplingIntervalMs);
        var results = new List<MonitorResult>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            CipItem item;
            try
            {
                item = await ResolveAsync(nodeId, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResultException ex)
            {
                results.Add(new MonitorResult(nodeId, null, ex.Result));
                continue;
            }

            if (!item.IsVariable)
            {
                results.Add(new MonitorResult(nodeId, null, new ServiceResult(StatusCodes.BadAttributeIdInvalid, $"'{item.Path}' is a structure; watch its members.")));
                continue;
            }

            PollGroup group;
            lock (_lock)
            {
                if (!_groups.TryGetValue(interval, out group!))
                {
                    group = new PollGroup(this, interval);
                    _groups[interval] = group;
                }
            }

            var entry = group.Add(nodeId, item, CreateTag(endpoint, item), onUpdate);
            results.Add(new MonitorResult(nodeId, new CipMonitorHandle(group, entry), ServiceResult.Good));
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
        var owned = monitors.OfType<CipMonitorHandle>().Where(h => ReferenceEquals(h.Group.Owner, this)).ToList();
        var results = await MonitorManyAsync([.. owned.Select(h => h.Entry.NodeId)], onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        foreach (var handle in owned)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    private CipEndpoint RequireEndpoint() =>
        _endpoint is { } e && State != ConnectionState.Disconnected ? e : throw new InvalidOperationException("Not connected.");

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

        _scopes.Clear();
        _templates.Clear();
        _endpoint = null;
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

    /// <summary>Poll results drive the connection state: libplctag reconnects by itself.</summary>
    private void ReportLink(bool ok)
    {
        if (_endpoint is null)
        {
            return;
        }

        State = ok ? ConnectionState.Connected : ConnectionState.Reconnecting;
    }

    private async Task<IReadOnlyList<BrowseItem>> ListScopeAsync(string scope, CancellationToken cancellationToken)
    {
        var symbols = await GetScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        var items = new List<BrowseItem>();
        foreach (var symbol in symbols.Where(IsUserTag).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var path = scope.Length == 0 ? symbol.Name : $"{scope}.{symbol.Name}";
            var item = await CreateItemAsync(path, symbol.Name, scope.Length == 0 ? null : scope, symbol.Type, symbol.Dimensions, cancellationToken).ConfigureAwait(false);
            items.Add(item.ToBrowseItem());
        }

        return items;
    }

    private static bool IsUserTag(LogixSymbol symbol) =>
        !symbol.Type.IsSystem
        && !symbol.Name.Contains(':', StringComparison.Ordinal)
        && !symbol.Name.StartsWith("__", StringComparison.Ordinal);

    private async Task<IReadOnlyList<LogixSymbol>> GetScopeAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_scopes.TryGetValue(scope, out var cached))
            {
                return cached;
            }
        }

        var list = await ReadTagListAsync(scope.Length == 0 ? "@tags" : $"{scope}.@tags", cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _scopes[scope] = list;
        }

        return list;
    }

    private async Task<IReadOnlyList<LogixSymbol>> ReadTagListAsync(string name, CancellationToken cancellationToken)
    {
        using var tag = CreateTag(_endpoint ?? throw new InvalidOperationException("Not connected."), name, null);
        await ReadTagAsync(tag, cancellationToken).ConfigureAwait(false);
        return LogixCodec.ParseTagList(tag.GetBuffer());
    }

    private async Task<LogixTemplate> GetTemplateAsync(ushort id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_templates.TryGetValue(id, out var cached))
            {
                return cached;
            }
        }

        using var tag = CreateTag(RequireEndpoint(), $"@udt/{id.ToString(CultureInfo.InvariantCulture)}", null);
        await ReadTagAsync(tag, cancellationToken).ConfigureAwait(false);
        LogixTemplate template;
        try
        {
            template = LogixCodec.ParseTemplate(tag.GetBuffer());
        }
        catch (FormatException ex)
        {
            throw new ServiceResultException(StatusCodes.BadDecodingError, $"UDT {id}: {ex.Message}");
        }

        lock (_lock)
        {
            _templates[id] = template;
        }

        return template;
    }

    private async Task<CipItem> CreateItemAsync(string path, string displayName, string? program, LogixType type, int[] dimensions, CancellationToken cancellationToken)
    {
        var template = type.IsStruct ? await GetTemplateAsync(type.TemplateId, cancellationToken).ConfigureAwait(false) : null;
        return new CipItem(path, displayName, program, type, dimensions, template);
    }

    /// <summary>Walks a tag path from its scope listing through UDT templates.</summary>
    private async Task<CipItem> ResolveAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        if (nodeId.NamespaceIndex != TagNamespace || nodeId.Identifier is not string text || !TagPath.TryParse(text, out var path))
        {
            throw new ServiceResultException(StatusCodes.BadNodeIdUnknown, $"'{nodeId}' is not a Logix tag.");
        }

        var symbols = await GetScopeAsync(path.Program ?? string.Empty, cancellationToken).ConfigureAwait(false);
        var symbol = symbols.FirstOrDefault(s => s.Name.Equals(path.Segments[0], StringComparison.OrdinalIgnoreCase))
            ?? throw new ServiceResultException(StatusCodes.BadNodeIdUnknown, $"Tag '{path.Segments[0]}' not found.");

        var current = await CreateItemAsync(path.BaseName, symbol.Name, path.Program, symbol.Type, symbol.Dimensions, cancellationToken).ConfigureAwait(false);
        var built = new StringBuilder(path.BaseName);
        foreach (var segment in path.Segments.Skip(1))
        {
            built.Append(segment);
            if (segment[0] == '[')
            {
                if (current.Dimensions.Length == 0)
                {
                    throw new ServiceResultException(StatusCodes.BadNodeIdUnknown, $"'{current.Path}' is not an array.");
                }

                current = current with { Path = built.ToString(), DisplayName = segment, Dimensions = [] };
            }
            else
            {
                var name = segment[1..];
                var member = current.Template is { } t && current.Dimensions.Length == 0
                    ? t.Members.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (member is null)
                {
                    throw new ServiceResultException(StatusCodes.BadNodeIdUnknown, $"'{current.Path}' has no member '{name}'.");
                }

                current = await CreateItemAsync(built.ToString(), member.Name, path.Program, member.Type, member.IsArray ? [member.ElementCount] : [], cancellationToken).ConfigureAwait(false);
            }
        }

        return current;
    }

    private async Task<IReadOnlyList<BrowseItem>> ChildrenAsync(CipItem item, CancellationToken cancellationToken)
    {
        if (item.IsVariable)
        {
            return [];
        }

        if (item.Dimensions.Length > 0)
        {
            var element = item with { Dimensions = [] };
            return [.. ElementIndexes(item.Dimensions).Take(MaxElements).Select(index => (element with { Path = item.Path + index, DisplayName = index }).ToBrowseItem())];
        }

        var children = new List<BrowseItem>();
        foreach (var member in item.Template!.Members.Where(m => !m.IsHidden))
        {
            var child = await CreateItemAsync($"{item.Path}.{member.Name}", member.Name, item.Program, member.Type, member.IsArray ? [member.ElementCount] : [], cancellationToken).ConfigureAwait(false);
            children.Add(child.ToBrowseItem());
        }

        return children;
    }

    private static IEnumerable<string> ElementIndexes(int[] dims)
    {
        var index = new int[dims.Length];
        var total = dims.Aggregate(1L, (a, d) => a * Math.Max(d, 1));
        for (long n = 0; n < total; n++)
        {
            yield return "[" + string.Join(',', index) + "]";
            for (var d = dims.Length - 1; d >= 0; d--)
            {
                if (++index[d] < dims[d])
                {
                    break;
                }

                index[d] = 0;
            }
        }
    }

    private async Task<object?> ReadValueAsync(CipItem item, CancellationToken cancellationToken)
    {
        using var tag = CreateTag(RequireEndpoint(), item);
        await ReadTagAsync(tag, cancellationToken).ConfigureAwait(false);
        return item.Decode(tag.GetBuffer());
    }

    /// <summary>Reads a tag, reporting libplctag failures as <see cref="ServiceResultException"/> like the OPC UA client.</summary>
    private static async Task ReadTagAsync(Tag tag, CancellationToken cancellationToken)
    {
        try
        {
            await InitializeTagAsync(tag, cancellationToken).ConfigureAwait(false);
            await tag.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            throw new ServiceResultException(ToStatus(ex), $"{tag.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the native tag synchronously on a pool thread. libplctag.NET 1.5 <c>InitializeAsync</c> leaves the
    /// native tag and its callback registered when creation fails (e.g. the tag does not exist); after the wrapper is
    /// collected, the next event libplctag raises for it (such as the PLC disconnecting) crashes the process with
    /// "callback was made on a garbage collected delegate". The synchronous create destroys a failed tag natively.
    /// </summary>
    private static Task InitializeTagAsync(Tag tag, CancellationToken cancellationToken) =>
        tag.IsInitialized ? Task.CompletedTask : Task.Run(tag.Initialize, cancellationToken);

    private static Tag CreateTag(CipEndpoint endpoint, CipItem item) =>
        CreateTag(endpoint, item.Path, item.Template is null && item.Dimensions.Length > 0 ? item.ElementCount : null);

    private static Tag CreateTag(CipEndpoint endpoint, string name, int? elementCount) => new()
    {
        Gateway = endpoint.Gateway,
        Path = endpoint.Path,
        PlcType = PlcType.ControlLogix,
        Protocol = Protocol.ab_eip,
        Name = name,
        ElementCount = elementCount,
        Timeout = endpoint.Timeout,
    };

    internal static StatusCode ToStatus(LibPlcTagException ex) => ex.Message switch
    {
        var m when m.Contains(nameof(Status.ErrorNotFound), StringComparison.Ordinal) => StatusCodes.BadNodeIdUnknown,
        var m when m.Contains(nameof(Status.ErrorNotAllowed), StringComparison.Ordinal) => StatusCodes.BadNotReadable,
        var m when m.Contains(nameof(Status.ErrorTimeout), StringComparison.Ordinal) => StatusCodes.BadTimeout,
        var m when m.Contains(nameof(Status.ErrorBadConnection), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorBadGateway), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorOpen), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorWinsock), StringComparison.Ordinal) => StatusCodes.BadCommunicationError,
        _ => StatusCodes.Bad,
    };

    /// <summary>A resolved tag, member or element with its type.</summary>
    private sealed record CipItem(string Path, string DisplayName, string? Program, LogixType Type, int[] Dimensions, LogixTemplate? Template)
    {
        public bool IsVariable => Template is null || (Template.IsString && Dimensions.Length == 0);

        public int ElementCount => Dimensions.Aggregate(1, (a, d) => a * d);

        public string TypeName
        {
            get
            {
                var name = Template?.Name ?? LogixType.AtomicName(Type.Atomic);
                return Dimensions.Length == 0 ? name : $"{name}[{string.Join(',', Dimensions)}]";
            }
        }

        public BrowseItem ToBrowseItem() => new(
            new NodeId(Path, TagNamespace),
            DisplayName,
            TypeName,
            IsVariable ? NodeClass.Variable : NodeClass.Object,
            HasChildren: !IsVariable);

        public object? Decode(byte[] data) => Template is { } template
            ? LogixCodec.DecodeString(template, data)
            : LogixCodec.DecodeAtomic(Type.Atomic, data, ElementCount, forceArray: Dimensions.Length > 0);
    }

    private sealed record CipEndpoint(string Gateway, string Path, TimeSpan Timeout)
    {
        public static CipEndpoint Parse(string url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(DeviceClient.EipScheme, StringComparison.OrdinalIgnoreCase) || uri.Host.Length == 0)
            {
                throw new FormatException($"'{url}' is not an EtherNet/IP address. Expected eip://host[:port][/backplane,slot], e.g. eip://192.168.1.10/1,0");
            }

            var gateway = uri.IsDefaultPort || uri.Port < 0 ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
            var path = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            return new CipEndpoint(gateway, path.Length == 0 ? "1,0" : path, TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>All items sharing one refresh time, read together on a timer.</summary>
    private sealed class PollGroup : IDisposable
    {
        private readonly List<PollEntry> _entries = [];
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _stopped;

        public PollGroup(CipClient owner, int intervalMs)
        {
            Owner = owner;
            IntervalMs = intervalMs;
        }

        public CipClient Owner { get; }

        public int IntervalMs { get; }

        public PollEntry Add(NodeId nodeId, CipItem item, Tag tag, Action<ValueUpdate> onUpdate)
        {
            var entry = new PollEntry(nodeId, item, tag, onUpdate);
            lock (_entries)
            {
                _entries.Add(entry);
                _loop ??= Task.Run(() => RunAsync(_cts.Token));
            }

            return entry;
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
            else
            {
                await entry.Gate.WaitAsync().ConfigureAwait(false);
                entry.Tag.Dispose();
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

            PollEntry[] entries;
            lock (_entries)
            {
                entries = [.. _entries];
                _entries.Clear();
            }

            foreach (var entry in entries)
            {
                entry.Tag.Dispose();
            }

            Dispose();
        }

        public void Dispose() => _cts.Dispose();

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMs));
            do
            {
                PollEntry[] entries;
                lock (_entries)
                {
                    entries = [.. _entries];
                }

                var outcomes = await Task.WhenAll(entries.Select(e => PollAsync(e, cancellationToken))).ConfigureAwait(false);
                if (outcomes.Length > 0 && !cancellationToken.IsCancellationRequested)
                {
                    Owner.ReportLink(outcomes.Any(ok => ok));
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }

        /// <summary>Reads one item and reports it when value or status changed. Returns false on a communication error.</summary>
        private static async Task<bool> PollAsync(PollEntry entry, CancellationToken cancellationToken)
        {
            if (!await entry.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            try
            {
                StatusCode status;
                object? value = null;
                byte[]? data = null;
                var ok = true;
                try
                {
                    await InitializeTagAsync(entry.Tag, cancellationToken).ConfigureAwait(false);
                    await entry.Tag.ReadAsync(cancellationToken).ConfigureAwait(false);
                    data = entry.Tag.GetBuffer();
                    value = entry.Item.Decode(data);
                    status = StatusCodes.Good;
                }
                catch (LibPlcTagException ex)
                {
                    status = ToStatus(ex);
                    ok = status != StatusCodes.BadCommunicationError && status != StatusCodes.BadTimeout;
                }

                if (status == entry.LastStatus && (data is null || data.AsSpan().SequenceEqual(entry.LastData)))
                {
                    return ok;
                }

                entry.LastStatus = status;
                entry.LastData = data;
                var now = DateTime.UtcNow;
                var variant = new Variant(value);
                entry.OnUpdate(new ValueUpdate(
                    entry.NodeId,
                    StatusCode.IsGood(status) ? ValueFormatter.Format(variant) : status.ToString(),
                    status,
                    now,
                    now,
                    ValueFormatter.ToNumeric(variant),
                    value));
                return ok;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    private sealed class PollEntry(NodeId nodeId, CipItem item, Tag tag, Action<ValueUpdate> onUpdate)
    {
        public NodeId NodeId { get; } = nodeId;

        public CipItem Item { get; } = item;

        public Tag Tag { get; } = tag;

        public Action<ValueUpdate> OnUpdate { get; } = onUpdate;

        /// <summary>Held while a read is in flight, so a tag is never disposed mid-read.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public StatusCode? LastStatus { get; set; }

        public byte[]? LastData { get; set; }
    }

    private sealed class CipMonitorHandle(PollGroup group, PollEntry entry) : IAsyncDisposable
    {
        public PollGroup Group => group;

        public PollEntry Entry => entry;

        public async ValueTask DisposeAsync() => await group.RemoveAsync(entry).ConfigureAwait(false);
    }
}

/// <summary>A Logix tag path split into scope, base tag and <c>.member</c> / <c>[index]</c> segments.</summary>
internal sealed record TagPath(string? Program, IReadOnlyList<string> Segments)
{
    /// <summary>Scope-qualified base tag, e.g. <c>Program:Main.Motor</c>.</summary>
    public string BaseName => Program is null ? Segments[0] : $"{Program}.{Segments[0]}";

    /// <summary>Qualified paths of the base tag and each nested segment, outermost first.</summary>
    public IEnumerable<string> Prefixes()
    {
        var current = BaseName;
        yield return current;
        foreach (var segment in Segments.Skip(1))
        {
            current += segment;
            yield return current;
        }
    }

    public static bool TryParse(string text, out TagPath path)
    {
        path = null!;
        string? program = null;
        var rest = text.Trim();
        if (rest.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
        {
            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                return false;
            }

            program = rest[..dot];
            rest = rest[(dot + 1)..];
        }

        var segments = new List<string>();
        var start = 0;
        var i = 0;
        while (i < rest.Length)
        {
            var c = rest[i];
            if (c == '.' || c == '[')
            {
                if (i > start)
                {
                    segments.Add(rest[start..i]);
                }

                if (c == '[')
                {
                    var close = rest.IndexOf(']', i);
                    if (close < 0)
                    {
                        return false;
                    }

                    segments.Add(rest[i..(close + 1)].Replace(" ", string.Empty, StringComparison.Ordinal));
                    i = close + 1;
                    start = i;
                    continue;
                }

                start = i;
            }

            i++;
        }

        if (start < rest.Length)
        {
            segments.Add(rest[start..]);
        }

        if (segments.Count == 0 || segments[0][0] is '.' or '[' || segments.Skip(1).Any(s => s.Length < 2))
        {
            return false;
        }

        path = new TagPath(program, segments);
        return true;
    }
}
