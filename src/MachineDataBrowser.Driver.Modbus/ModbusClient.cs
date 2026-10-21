using System.Globalization;
using MachineDataBrowser.Core;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Driver.Modbus;

/// <summary>
/// Modbus TCP client. Modbus has no names or browsable address space, so the tree is generated:
/// <list type="bullet">
/// <item>ns=2 folders: <c>Root</c>, one per area (<c>hr</c>), and blocks of <see cref="BlockSize"/> addresses (<c>hr/100</c>);</item>
/// <item>ns=1 values: a <see cref="ModbusAddress"/> such as <c>hr:100</c> or <c>hr:20:float32</c>.</item>
/// </list>
/// Browsing shows single coils, inputs and registers (uint16); typed values (float32, int32, strings, word order)
/// are added by id, or named in a register map (<see cref="ConnectOptions.TagMapPath"/>, <see cref="ModbusTagMap"/>)
/// shown under <c>Tags</c> (ns=3 folders). Mapped values keep their address as id, so watch lists work with or without
/// the map. Monitoring polls per refresh time, reading neighbouring addresses in one request.
/// </summary>
public sealed class ModbusClient : IDeviceClient, IConnectionDiagnosticsSource, IEngineeringUnitsSource
{
    public const ushort TagNamespace = 1;
    public const ushort FolderNamespace = 2;
    public const ushort MapNamespace = 3;
    private const string TagsFolder = "Tags";
    public const int BlockSize = 100;
    private const int MaxRegisterGap = 16;
    private const int MaxBitGap = 64;

    private static readonly NodeId RootId = new("Root", FolderNamespace);
    private static readonly NodeId TagsId = new(TagsFolder, MapNamespace);
    private static readonly ModbusArea[] Areas = [ModbusArea.Coil, ModbusArea.DiscreteInput, ModbusArea.InputRegister, ModbusArea.HoldingRegister];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<int, PollGroup> _groups = [];
    private ModbusTcpTransport? _transport;
    private ModbusTagMap? _map;
    private string? _mapPath;
    private Dictionary<string, List<BrowseItem>> _mapFolders = [];
    private ConnectionState _state = ConnectionState.Disconnected;
    private DateTime? _connectedAt;
    private DateTime? _lastLinkLoss;
    private int _linkLosses;
    private (long Count, DateTime At)? _rateSample;

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

    public string? ServerUri => _transport is { } t ? $"Modbus {t.Host}:{t.Port.ToString(CultureInfo.InvariantCulture)} (unit {t.UnitId.ToString(CultureInfo.InvariantCulture)})" : null;

    public BrowseItem Root { get; } = new(RootId, "Device", "Root", NodeClass.Object);

    public static NodeId AreaFolder(ModbusArea area) => new(ModbusAddress.AreaPrefix(area), FolderNamespace);

    public static NodeId BlockFolder(ModbusArea area, int start) =>
        new($"{ModbusAddress.AreaPrefix(area)}/{start.ToString(CultureInfo.InvariantCulture)}", FolderNamespace);

    /// <summary>The register map loaded at connect; null without one.</summary>
    public ModbusTagMap? TagMap => _map;

    public string ToPortableId(NodeId nodeId) =>
        nodeId.NamespaceIndex is FolderNamespace or MapNamespace ? "@" + nodeId.IdentifierAsString
        : ModbusAddress.TryParse(nodeId.IdentifierAsString, out var address) ? address.ToString()
        : nodeId.IdentifierAsString;

    /// <summary>The address with its type (<c>hr:20:float32</c>); folders as <c>@hr/100</c>.</summary>
    public string ToDisplayId(NodeId nodeId) => ToPortableId(nodeId);

    public NodeId ParsePortableId(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();
        if (trimmed.StartsWith("@" + TagsFolder, StringComparison.Ordinal) && (trimmed.Length == TagsFolder.Length + 1 || trimmed[TagsFolder.Length + 1] == '/'))
        {
            return new NodeId(trimmed[1..], MapNamespace);
        }

        return trimmed.StartsWith('@') ? new NodeId(trimmed[1..], FolderNamespace)
            : new NodeId(ModbusAddress.TryParse(trimmed, out var address) ? address.ToString() : trimmed, TagNamespace);
    }

    public async Task ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            State = ConnectionState.Connecting;
            var map = options.TagMapPath is { Length: > 0 } mapPath ? await ModbusTagMap.LoadAsync(mapPath, cancellationToken).ConfigureAwait(false) : null;
            ModbusTcpTransport transport;
            try
            {
                transport = ParseEndpoint(options.EndpointUrl);
            }
            catch (FormatException ex)
            {
                throw new DeviceClientException(DeviceStatusCodes.BadTcpEndpointUrlInvalid, ex.Message);
            }

            try
            {
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            transport.LinkChanged += ReportLink;
            _transport = transport;
            _map = map;
            _mapPath = map is null ? null : options.TagMapPath;
            _mapFolders = map is null ? [] : BuildFolders(map);
            _connectedAt = DateTime.UtcNow;
            _linkLosses = 0;
            _lastLinkLoss = null;
            _rateSample = null;
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

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public Task<IReadOnlyList<BrowseItem>> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        RequireTransport();
        IReadOnlyList<BrowseItem> items = [];
        if (nodeId == RootId)
        {
            items = [.. Areas.Select(a => new BrowseItem(AreaFolder(a), ModbusAddress.AreaName(a), ModbusAddress.AreaPrefix(a), NodeClass.Object))];
            if (_map is not null)
            {
                items = [new BrowseItem(TagsId, TagsFolder, TagsFolder, NodeClass.Object), .. items];
            }
        }
        else if (nodeId.NamespaceIndex == MapNamespace)
        {
            items = _mapFolders.TryGetValue(nodeId.IdentifierAsString, out var children) ? children : [];
        }
        else if (nodeId.NamespaceIndex == FolderNamespace && TryParseFolder(nodeId.IdentifierAsString, out var area, out var block))
        {
            items = block is { } start
                ? [.. Enumerable.Range(start, Math.Min(BlockSize, 65536 - start)).Select(a => ToBrowseItem(Default(area, (ushort)a)))]
                : [.. Enumerable.Range(0, (65536 + BlockSize - 1) / BlockSize).Select(b =>
                {
                    var first = b * BlockSize;
                    var last = Math.Min(first + BlockSize, 65536) - 1;
                    return new BrowseItem(BlockFolder(area, first), $"{first.ToString(CultureInfo.InvariantCulture)}-{last.ToString(CultureInfo.InvariantCulture)}", first.ToString(CultureInfo.InvariantCulture), NodeClass.Object);
                })];
        }

        return Task.FromResult(items);
    }

    public Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NodeId> path = [];
        if (nodeId == RootId)
        {
            path = [RootId];
        }
        else if (nodeId.NamespaceIndex == FolderNamespace && TryParseFolder(nodeId.IdentifierAsString, out var area, out var block))
        {
            path = block is null ? [RootId, nodeId] : [RootId, AreaFolder(area), nodeId];
        }
        else if (nodeId.NamespaceIndex == MapNamespace)
        {
            path = [RootId, .. MapFolderPath(nodeId.IdentifierAsString)];
        }
        else if (TryResolve(nodeId, out var address) && _map?.ByAddress.GetValueOrDefault(address.ToString()) is { } tag)
        {
            path = [RootId, .. MapFolderPath(FolderOf(tag.Path)), nodeId];
        }
        else if (TryResolve(nodeId, out address))
        {
            path = [RootId, AreaFolder(address.Area), BlockFolder(address.Area, address.Address / BlockSize * BlockSize), nodeId];
        }

        return Task.FromResult(path.Count > maxDepth ? [] : path);
    }

    public async Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        RequireTransport();
        var result = new List<AttributeValue> { new("NodeId", ToPortableId(nodeId)) };
        if (nodeId.NamespaceIndex is FolderNamespace or MapNamespace)
        {
            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            return result;
        }

        var address = Resolve(nodeId);
        result.Add(new("NodeClass", nameof(NodeClass.Variable)));
        if (_map?.ByAddress.GetValueOrDefault(address.ToString()) is { } tag)
        {
            result.Add(new("Name", tag.Path));
            if (tag.Description is { } description)
            {
                result.Add(new("Description", description));
            }

            if (tag.Unit is { } unit)
            {
                result.Add(new("EngineeringUnits", unit));
            }
        }

        result.Add(new("Area", ModbusAddress.AreaName(address.Area)));
        result.Add(new("Address", $"{address.Address.ToString(CultureInfo.InvariantCulture)} (0-based) · {address.ModiconReference}"));
        result.Add(new("DataType", address.TypeName));
        if (!address.IsBit)
        {
            result.Add(new("Registers", address.Length.ToString(CultureInfo.InvariantCulture)));
            if (address.Length > 1)
            {
                result.Add(new("ByteOrder", address.Swap ? "Big-endian words, low word first" : "Big-endian, high word first"));
            }
        }

        var (value, status, message) = (await ReadManyAsync([address], null, cancellationToken).ConfigureAwait(false))[0];
        result.Add(new("Value", status.IsGood ? ValueFormatter.Format(value) : $"{status}: {message}"));
        result.Add(new("AccessLevel", address.IsWritable ? "Read, Write" : "Read"));
        return result;
    }

    public async Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        RequireTransport();
        var addresses = nodeIds.Select(id => TryResolve(id, out var a) ? a : null).ToList();
        var valid = addresses.OfType<ModbusAddress>().ToList();
        var reads = await ReadManyAsync(valid, null, cancellationToken).ConfigureAwait(false);
        var values = new object?[nodeIds.Count];
        var next = 0;
        for (var i = 0; i < addresses.Count; i++)
        {
            if (addresses[i] is not null)
            {
                var read = reads[next++];
                values[i] = read.Status.IsGood ? read.Value : null;
            }
        }

        return values;
    }

    /// <summary>Writes a coil (FC 5) or holding registers (FC 6 for one register, FC 16 for more).</summary>
    public async Task WriteValueAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var transport = RequireTransport();
        var address = Resolve(nodeId);
        if (!address.IsWritable)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadNotWritable, $"{ModbusAddress.AreaName(address.Area)} are read-only.");
        }

        if (address.Type == ModbusValueType.Bool)
        {
            await transport.WriteCoilAsync(address.Address, (bool)ValueParser.Parse(text, DeviceDataType.Boolean, false), cancellationToken).ConfigureAwait(false);
            return;
        }

        object value;
        if (address.Type == ModbusValueType.String)
        {
            var trimmed = text.Trim();
            value = trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"') ? trimmed[1..^1] : trimmed;
        }
        else
        {
            value = ValueParser.Parse(text, address.DataType, false);
        }

        await transport.WriteRegistersAsync(address.Address, address.Encode(value), cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<NodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(onUpdate);
        RequireTransport();
        var interval = Math.Max(50, (int)samplingIntervalMs);
        var results = new List<MonitorResult>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            if (!TryResolve(nodeId, out var address))
            {
                results.Add(new MonitorResult(nodeId, null, new DeviceError(DeviceStatusCodes.BadNodeIdUnknown, $"'{nodeId.IdentifierAsString}' is not a Modbus address (e.g. hr:100, hr:20:float32, co:3).")));
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

            var entry = group.Add(nodeId, address, onUpdate);
            results.Add(new MonitorResult(nodeId, new ModbusMonitorHandle(group, entry), DeviceError.Good));
        }

        return Task.FromResult<IReadOnlyList<MonitorResult>>(results);
    }

    public async Task<IReadOnlyList<MonitorResult>> ChangeRefreshAsync(
        IReadOnlyList<IAsyncDisposable> monitors,
        Action<ValueUpdate> onUpdate,
        double refreshMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var owned = monitors.OfType<ModbusMonitorHandle>().Where(h => ReferenceEquals(h.Group.Owner, this)).ToList();
        var results = await MonitorManyAsync([.. owned.Select(h => h.Entry.NodeId)], onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        foreach (var handle in owned)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    public Task<ConnectionDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var transport = RequireTransport();
        List<PollGroup> groups;
        lock (_lock)
        {
            groups = [.. _groups.Values.OrderBy(g => g.IntervalMs)];
        }

        var requests = transport.Requests;
        var info = new List<(string, string)>
        {
            ("Device", $"{transport.Host}:{transport.Port.ToString(CultureInfo.InvariantCulture)}"),
            ("Unit id", transport.UnitId.ToString(CultureInfo.InvariantCulture)),
            ("Protocol", "Modbus TCP"),
            ("Timeout", DiagnosticsFormat.Duration(transport.Timeout.TotalMilliseconds)),
            ("Connected since", DiagnosticsFormat.Since(_connectedAt)),
            ("Link lost", DiagnosticsFormat.Reconnects(_linkLosses, _lastLinkLoss)),
            ("Register map", _map is { } map ? $"{Path.GetFileName(_mapPath)} · {map.Tags.Count.ToString(CultureInfo.InvariantCulture)} tags" : "none"),
            ("Requests", $"{DiagnosticsFormat.Rate(requests, ref _rateSample)} · {requests.ToString("N0", CultureInfo.InvariantCulture)} total · {transport.Failures.ToString("N0", CultureInfo.InvariantCulture)} failed"),
        };

        // Modbus has no subscriptions: everything monitored is read in one poll loop per refresh time.
        if (groups.Count == 0)
        {
            info.Add(("Polling", "nothing monitored"));
        }

        foreach (var group in groups)
        {
            var cycle = group.LastCycleMs is { } ms ? $" · cycle {DiagnosticsFormat.Duration(ms)}" : string.Empty;
            var late = group.LastCycleMs > group.IntervalMs ? " (slower than the refresh time)" : string.Empty;
            var error = group.LastError is { } last ? $" · last error: {last}" : string.Empty;
            info.Add(($"Poll {DiagnosticsFormat.Duration(group.IntervalMs)}",
                $"{group.Count.ToString(CultureInfo.InvariantCulture)} values · {group.LastRequests.ToString(CultureInfo.InvariantCulture)} requests · last {DiagnosticsFormat.Ago(group.LastCycleUtc ?? DateTime.MinValue)}{cycle}{late}{error}"));
        }

        return Task.FromResult(new ConnectionDiagnostics(info, []));
    }

    /// <summary>
    /// Reads many values with as few requests as possible: addresses of one area close to each other are read in one
    /// request. When a combined read hits an address the device doesn't have, its values are read one by one so the
    /// others still get a value.
    /// </summary>
    internal async Task<IReadOnlyList<(object? Value, DeviceStatusCode Status, string Message)>> ReadManyAsync(
        IReadOnlyList<ModbusAddress> addresses,
        Action? onRequest,
        CancellationToken cancellationToken)
    {
        var transport = RequireTransport();
        var results = new (object? Value, DeviceStatusCode Status, string Message)[addresses.Count];
        foreach (var range in PlanReads(addresses))
        {
            try
            {
                await ReadRangeAsync(transport, range, addresses, results, onRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceClientException ex) when (ex.StatusCode == DeviceStatusCodes.BadNodeIdUnknown && range.Items.Count > 1)
            {
                foreach (var index in range.Items)
                {
                    var single = new ReadRange(addresses[index].Area, addresses[index].Address, addresses[index].Length, [index]);
                    try
                    {
                        await ReadRangeAsync(transport, single, addresses, results, onRequest, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DeviceClientException singleError)
                    {
                        results[index] = (null, singleError.StatusCode, singleError.Message);
                    }
                }
            }
            catch (DeviceClientException ex)
            {
                foreach (var index in range.Items)
                {
                    results[index] = (null, ex.StatusCode, ex.Message);
                }
            }
        }

        return results;
    }

    private static async Task ReadRangeAsync(
        ModbusTcpTransport transport,
        ReadRange range,
        IReadOnlyList<ModbusAddress> addresses,
        (object? Value, DeviceStatusCode Status, string Message)[] results,
        Action? onRequest,
        CancellationToken cancellationToken)
    {
        onRequest?.Invoke();
        if (range.Area is ModbusArea.Coil or ModbusArea.DiscreteInput)
        {
            var bits = await transport.ReadBitsAsync(range.Area, range.Start, range.Count, cancellationToken).ConfigureAwait(false);
            foreach (var index in range.Items)
            {
                results[index] = (bits[addresses[index].Address - range.Start], DeviceStatusCodes.Good, string.Empty);
            }

            return;
        }

        var registers = await transport.ReadRegistersAsync(range.Area, range.Start, range.Count, cancellationToken).ConfigureAwait(false);
        foreach (var index in range.Items)
        {
            var address = addresses[index];
            results[index] = (address.Decode(registers.AsSpan(address.Address - range.Start, address.Length)), DeviceStatusCodes.Good, string.Empty);
        }
    }

    internal static List<ReadRange> PlanReads(IReadOnlyList<ModbusAddress> addresses)
    {
        var ranges = new List<ReadRange>();
        foreach (var area in addresses.Select((a, i) => (a, i)).GroupBy(x => x.a.Area))
        {
            var bit = area.Key is ModbusArea.Coil or ModbusArea.DiscreteInput;
            var max = bit ? ModbusTcpTransport.MaxBits : ModbusTcpTransport.MaxRegisters;
            var gap = bit ? MaxBitGap : MaxRegisterGap;
            ReadRange? current = null;
            foreach (var (address, index) in area.OrderBy(x => x.a.Address))
            {
                var end = address.Address + address.Length;
                if (current is { } c && address.Address <= c.Start + c.Count + gap && Math.Max(end, c.Start + c.Count) - c.Start <= max)
                {
                    c.Items.Add(index);
                    current = c with { Count = Math.Max(end, c.Start + c.Count) - c.Start };
                    ranges[^1] = current;
                    continue;
                }

                current = new ReadRange(area.Key, address.Address, address.Length, [index]);
                ranges.Add(current);
            }
        }

        return ranges;
    }

    public Task<IReadOnlyList<string?>> ReadUnitsAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        return Task.FromResult<IReadOnlyList<string?>>([.. nodeIds.Select(id =>
            TryResolve(id, out var address) ? _map?.ByAddress.GetValueOrDefault(address.ToString())?.Unit : null)]);
    }

    /// <summary>Children of every map folder, keyed by folder id (<c>Tags</c>, <c>Tags/Line1</c>), in file order.</summary>
    private static Dictionary<string, List<BrowseItem>> BuildFolders(ModbusTagMap map)
    {
        var folders = new Dictionary<string, List<BrowseItem>>(StringComparer.Ordinal) { [TagsFolder] = [] };
        foreach (var tag in map.Tags)
        {
            var parent = TagsFolder;
            var segments = tag.Path.Split('/');
            foreach (var segment in segments[..^1])
            {
                var folder = $"{parent}/{segment}";
                if (!folders.ContainsKey(folder))
                {
                    folders[folder] = [];
                    folders[parent].Add(new BrowseItem(new NodeId(folder, MapNamespace), segment, segment, NodeClass.Object));
                }

                parent = folder;
            }

            folders[parent].Add(new BrowseItem(new NodeId(tag.Address.ToString(), TagNamespace), tag.Name, tag.Address.ToString(), NodeClass.Variable, HasChildren: false));
        }

        return folders;
    }

    private static string FolderOf(string tagPath)
    {
        var slash = tagPath.LastIndexOf('/');
        return slash < 0 ? TagsFolder : $"{TagsFolder}/{tagPath[..slash]}";
    }

    /// <summary>Ids of <c>Tags</c> down to <paramref name="folder"/> (<c>Tags/Line1/Motor</c>).</summary>
    private static IEnumerable<NodeId> MapFolderPath(string folder)
    {
        var segments = folder.Split('/');
        for (var i = 1; i <= segments.Length; i++)
        {
            yield return new NodeId(string.Join('/', segments[..i]), MapNamespace);
        }
    }

    private static ModbusAddress Default(ModbusArea area, ushort address) =>
        new(area, address, area is ModbusArea.Coil or ModbusArea.DiscreteInput ? ModbusValueType.Bool : ModbusValueType.UInt16);

    private static BrowseItem ToBrowseItem(ModbusAddress address) => new(
        new NodeId(address.ToString(), TagNamespace),
        address.Address.ToString(CultureInfo.InvariantCulture),
        address.TypeName,
        NodeClass.Variable,
        HasChildren: false);

    private static bool TryParseFolder(string text, out ModbusArea area, out int? block)
    {
        block = null;
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return ModbusAddress.TryParseArea(text, out area) && text.Length == 2;
        }

        if (!ModbusAddress.TryParseArea(text[..slash], out area)
            || !int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || start % BlockSize != 0 || start > 65535)
        {
            return false;
        }

        block = start;
        return true;
    }

    private static bool TryResolve(NodeId nodeId, out ModbusAddress address)
    {
        address = null!;
        return nodeId.NamespaceIndex == TagNamespace && ModbusAddress.TryParse(nodeId.IdentifierAsString, out address);
    }

    private static ModbusAddress Resolve(NodeId nodeId) => TryResolve(nodeId, out var address)
        ? address
        : throw new DeviceClientException(DeviceStatusCodes.BadNodeIdUnknown, $"'{nodeId.IdentifierAsString}' is not a Modbus address (e.g. hr:100, hr:20:float32, co:3).");

    private ModbusTcpTransport RequireTransport() =>
        _transport is { } t && State != ConnectionState.Disconnected ? t : throw new InvalidOperationException("Not connected.");

    internal static ModbusTcpTransport ParseEndpoint(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(DeviceClient.ModbusScheme, StringComparison.OrdinalIgnoreCase) || uri.Host.Length == 0)
        {
            throw new FormatException($"'{url}' is not a Modbus TCP address. Expected modbus://host[:port][/unit], e.g. modbus://192.168.1.10:502/1");
        }

        var unitText = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        byte unit = 1;
        if (unitText.Length > 0 && !byte.TryParse(unitText, NumberStyles.None, CultureInfo.InvariantCulture, out unit))
        {
            throw new FormatException($"'{unitText}' is not a Modbus unit id (0–255).");
        }

        return new ModbusTcpTransport(uri.Host, uri.IsDefaultPort || uri.Port < 0 ? 502 : uri.Port, unit, TimeSpan.FromSeconds(3));
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

        if (_transport is { } transport)
        {
            transport.LinkChanged -= ReportLink;
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        _transport = null;
        _map = null;
        _mapPath = null;
        _mapFolders = [];
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

    /// <summary>Request results drive the connection state: the transport reconnects on the next request.</summary>
    private void ReportLink(bool ok)
    {
        if (_transport is null || State == ConnectionState.Disconnected)
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

    internal sealed record ReadRange(ModbusArea Area, ushort Start, int Count, List<int> Items);

    /// <summary>All values sharing one refresh time, read together on a timer.</summary>
    private sealed class PollGroup(ModbusClient owner, int intervalMs) : IDisposable
    {
        private readonly List<PollEntry> _entries = [];
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _stopped;

        public ModbusClient Owner => owner;

        public int IntervalMs => intervalMs;

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

        public int LastRequests { get; private set; }

        public DateTime? LastCycleUtc { get; private set; }

        public double? LastCycleMs { get; private set; }

        public string? LastError { get; private set; }

        public PollEntry Add(NodeId nodeId, ModbusAddress address, Action<ValueUpdate> onUpdate)
        {
            var entry = new PollEntry(nodeId, address, onUpdate);
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
                owner.RemoveGroup(this);
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

        public void Dispose() => _cts.Dispose();

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
            do
            {
                PollEntry[] entries;
                lock (_entries)
                {
                    entries = [.. _entries];
                }

                if (entries.Length == 0)
                {
                    continue;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var requests = 0;
                IReadOnlyList<(object? Value, DeviceStatusCode Status, string Message)> reads;
                try
                {
                    reads = await owner.ReadManyAsync([.. entries.Select(e => e.Address)], () => requests++, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (owner.State == ConnectionState.Disconnected)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException && Errors.IsRecoverable(ex))
                {
                    // Decoding surprises must not stop the poll loop.
                    System.Diagnostics.Trace.TraceError($"Modbus poll failed: {ex}");
                    reads = [.. entries.Select(_ => ((object?)null, DeviceStatusCodes.BadUnexpectedError, ex.Message))];
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                LastCycleMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                LastCycleUtc = DateTime.UtcNow;
                LastRequests = requests;
                var now = DateTime.UtcNow;
                for (var i = 0; i < entries.Length; i++)
                {
                    var (value, status, message) = reads[i];
                    if (status.IsBad)
                    {
                        LastError = $"{entries[i].Address}: {message}";
                    }

                    var text = status.IsGood ? ValueFormatter.Format(value) : status.ToString();
                    if (status == entries[i].LastStatus && text == entries[i].LastText)
                    {
                        continue;
                    }

                    entries[i].LastStatus = status;
                    entries[i].LastText = text;
                    entries[i].OnUpdate(new ValueUpdate(entries[i].NodeId, text, status, now, now, ValueFormatter.ToNumeric(value), value));
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private sealed class PollEntry(NodeId nodeId, ModbusAddress address, Action<ValueUpdate> onUpdate)
    {
        public NodeId NodeId { get; } = nodeId;

        public ModbusAddress Address { get; } = address;

        public Action<ValueUpdate> OnUpdate { get; } = onUpdate;

        public DeviceStatusCode? LastStatus { get; set; }

        public string? LastText { get; set; }
    }

    private sealed class ModbusMonitorHandle(PollGroup group, PollEntry entry) : IAsyncDisposable
    {
        public PollGroup Group => group;

        public PollEntry Entry => entry;

        public async ValueTask DisposeAsync() => await group.RemoveAsync(entry).ConfigureAwait(false);
    }
}
