using System.Globalization;
using System.Net.Sockets;
using MachineDataBrowser.Core;
using S7.Net;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Driver.S7;

/// <summary>
/// Siemens S7 client (S7-300/400/1200/1500, ISO-on-TCP) built on S7.Net. Classic S7 has no symbol browsing, so the
/// tree is made of:
/// <list type="bullet">
/// <item>ns=3 symbols from a tag list (<see cref="ConnectOptions.TagMapPath"/> or <c>?tags=</c> in the URL), in folders
/// from the dots in their names;</item>
/// <item>ns=1 absolute addresses with their type (<c>DB1.DBD4:REAL</c>, <c>M0.0</c>), which can also be typed in;</item>
/// <item>ns=2 folders: <c>Root</c>, <c>Tags/…</c>, the areas <c>I</c>, <c>Q</c>, <c>M</c>, <c>DB</c> and <c>DB1</c>…,
/// listing their first bytes (and the bits of each byte) for a look at raw memory.</item>
/// </list>
/// S7 has no subscriptions: monitoring polls per refresh time, merging neighbouring addresses into few requests, and
/// reports only changes.
/// </summary>
public sealed class S7Client : IDeviceClient, IConnectionDiagnosticsSource
{
    internal const ushort AddressNamespace = 1;
    internal const ushort FolderNamespace = 2;
    internal const ushort SymbolNamespace = 3;

    /// <summary>Addresses closer than this are read in one request, with the bytes between them.</summary>
    private const int MaxGap = 16;
    private const int MaxRangeBytes = 1024;

    private static readonly NodeId RootId = Folder("Root");
    private static readonly NodeId TagsId = Folder("Tags");
    private static readonly NodeId BlocksId = Folder("DB");
    private static readonly NodeId InputsId = Folder("I");
    private static readonly NodeId OutputsId = Folder("Q");
    private static readonly NodeId MemoryId = Folder("M");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<int, PollGroup> _groups = [];
    // Built on connect and replaced as a whole, never changed, so browsing and polling need no lock.
    private Dictionary<string, S7Symbol> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<BrowseItem>> _folders = new(StringComparer.Ordinal);
    private S7TagList _tags = S7TagList.Empty;
    private S7Endpoint? _endpoint;
    private Plc? _plc;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DateTime? _connectedAt;
    private DateTime? _lastLinkLoss;
    private int _linkLosses;
    private long _requests;
    private long _failedRequests;
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

    public bool IsReadOnly => false;

    public string? ServerUri => _endpoint is { } e ? $"{CpuName(e.Cpu)} {e.Host}:{e.Port.ToString(CultureInfo.InvariantCulture)} (rack {e.Rack.ToString(CultureInfo.InvariantCulture)}, slot {e.Slot.ToString(CultureInfo.InvariantCulture)})" : null;

    public BrowseItem Root { get; } = new(RootId, "PLC", "Root", NodeClass.Object);

    /// <summary>Addresses as <c>DB1.DBD4:REAL</c>, symbols in double quotes (<c>"Machine.Speed"</c>), folders as <c>@DB1</c>.</summary>
    public string ToPortableId(NodeId nodeId) => nodeId.NamespaceIndex switch
    {
        FolderNamespace => "@" + nodeId.IdentifierAsString,
        SymbolNamespace => $"\"{nodeId.IdentifierAsString}\"",
        _ => nodeId.IdentifierAsString,
    };

    public string ToDisplayId(NodeId nodeId) => ToPortableId(nodeId);

    /// <summary>
    /// Folders start with <c>@</c>; anything that parses as an address is one (in canonical form); everything else
    /// is a symbol name, with or without quotes.
    /// </summary>
    public NodeId ParsePortableId(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();
        if (trimmed.StartsWith('@'))
        {
            return Folder(trimmed[1..]);
        }

        if (S7Address.TryParse(trimmed, out var address))
        {
            return AddressId(address);
        }

        return new NodeId(trimmed.Trim('"'), SymbolNamespace);
    }

    public async Task ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            State = ConnectionState.Connecting;
            S7Endpoint endpoint;
            try
            {
                endpoint = S7Endpoint.Parse(options.EndpointUrl);
            }
            catch (FormatException ex)
            {
                throw new DeviceClientException(DeviceStatusCodes.BadTcpEndpointUrlInvalid, ex.Message);
            }

            var file = string.IsNullOrWhiteSpace(options.TagMapPath) ? endpoint.TagFile : options.TagMapPath;
            var tags = file is null ? S7TagList.Empty : await LoadTagListAsync(file, cancellationToken).ConfigureAwait(false);

            var plc = new Plc(endpoint.Cpu, endpoint.Host, endpoint.Port, endpoint.Rack, endpoint.Slot)
            {
                ReadTimeout = (int)endpoint.Timeout.TotalMilliseconds,
                WriteTimeout = (int)endpoint.Timeout.TotalMilliseconds,
            };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(endpoint.Timeout);
                await plc.OpenAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                plc.Close();
                var reason = ex is OperationCanceledException ? $"no answer within {DiagnosticsFormat.Duration(endpoint.Timeout.TotalMilliseconds)}" : Describe(ex);
                throw new DeviceClientException(DeviceStatusCodes.BadCommunicationError, $"Can't connect to {endpoint.Host}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)} (rack {endpoint.Rack.ToString(CultureInfo.InvariantCulture)}, slot {endpoint.Slot.ToString(CultureInfo.InvariantCulture)}): {reason}");
            }

            _endpoint = endpoint;
            _plc = plc;
            LoadTags(tags);
            _connectedAt = DateTime.UtcNow;
            _linkLosses = 0;
            _lastLinkLoss = null;
            _requests = 0;
            _failedRequests = 0;
            _rateSample = null;
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
        _io.Dispose();
    }

    /// <summary>The tag list in use: its file, how many symbols it gave, and the entries skipped with the reason.</summary>
    internal (string Path, int Tags, IReadOnlyList<string> Skipped)? TagList =>
        _tags.Source is { } source ? (source, _symbols.Count, _tags.Skipped) : null;

    private static async Task<S7TagList> LoadTagListAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await S7TagList.LoadAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadConfigurationError, $"Tag list '{path}' can't be read: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<BrowseItem>> BrowseAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var endpoint = RequireEndpoint();
        return Task.FromResult<IReadOnlyList<BrowseItem>>(Browse(endpoint, nodeId));
    }

    private List<BrowseItem> Browse(S7Endpoint endpoint, NodeId nodeId)
    {
        if (nodeId == RootId)
        {
            var items = new List<BrowseItem>();
            if (_symbols.Count > 0)
            {
                items.Add(new BrowseItem(TagsId, "Tags", "Tags", NodeClass.Object));
            }

            items.Add(new BrowseItem(BlocksId, "Data blocks", "DB", NodeClass.Object, HasChildren: DataBlocks(endpoint).Count > 0));
            items.Add(new BrowseItem(InputsId, "Inputs", "I", NodeClass.Object));
            items.Add(new BrowseItem(OutputsId, "Outputs", "Q", NodeClass.Object));
            items.Add(new BrowseItem(MemoryId, "Memory", "M", NodeClass.Object));
            return items;
        }

        if (nodeId.NamespaceIndex == FolderNamespace)
        {
            var name = nodeId.IdentifierAsString;
            if (_folders.TryGetValue(name, out var children))
            {
                return [.. children];
            }

            if (nodeId == BlocksId)
            {
                return [.. DataBlocks(endpoint).Select(db => new BrowseItem(Folder(DbFolder(db)), DbFolder(db), DbFolder(db), NodeClass.Object))];
            }

            if (TryParseAreaFolder(name, out var area, out var dbNumber))
            {
                return [.. Enumerable.Range(0, endpoint.AreaBytes).Select(i => ToBrowseItem(new S7Address(area, dbNumber, i, 0, S7Type.Byte), hasChildren: true))];
            }

            throw new DeviceClientException(DeviceStatusCodes.BadNodeIdUnknown, $"'{ToPortableId(nodeId)}' is not a folder of this PLC.");
        }

        if (nodeId.NamespaceIndex == AddressNamespace && S7Address.TryParse(nodeId.IdentifierAsString, out var address) && address.Type == S7Type.Byte)
        {
            return [.. Enumerable.Range(0, 8).Select(bit => ToBrowseItem(address with { Bit = bit, Type = S7Type.Bool }, hasChildren: false))];
        }

        return [];
    }

    public Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default)
    {
        var path = PathFromRoot(nodeId);
        return Task.FromResult<IReadOnlyList<NodeId>>(path.Count > maxDepth ? [] : path);
    }

    private List<NodeId> PathFromRoot(NodeId nodeId)
    {
        var path = new List<NodeId> { RootId };
        if (nodeId == RootId)
        {
            return path;
        }

        switch (nodeId.NamespaceIndex)
        {
            case FolderNamespace:
            {
                var name = nodeId.IdentifierAsString;
                if (name == TagsId.IdentifierAsString || name.StartsWith(TagsId.IdentifierAsString + "/", StringComparison.Ordinal))
                {
                    path.AddRange(FolderPrefixes(name.Split('/')));
                }
                else if (TryParseAreaFolder(name, out var area, out _) && area == S7Area.DataBlock)
                {
                    path.Add(BlocksId);
                    path.Add(nodeId);
                }
                else
                {
                    path.Add(nodeId);
                }

                return path;
            }

            case SymbolNamespace when _symbols.TryGetValue(nodeId.IdentifierAsString, out var symbol):
                path.AddRange(FolderPrefixes([TagsId.IdentifierAsString, .. symbol.Folders]));
                path.Add(nodeId);
                return path;

            case AddressNamespace when S7Address.TryParse(nodeId.IdentifierAsString, out var address)
                && address.Byte < (_endpoint?.AreaBytes ?? 0)
                && (address.Type == S7Type.Byte || address.Type == S7Type.Bool):
                if (address.Area == S7Area.DataBlock)
                {
                    path.Add(BlocksId);
                }

                path.Add(AreaFolder(address));
                path.Add(AddressId(address with { Bit = 0, Type = S7Type.Byte }));
                if (address.Type.IsBit)
                {
                    path.Add(AddressId(address));
                }

                return path;

            default:
                return [];
        }
    }

    public async Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        RequireEndpoint();
        var result = new List<AttributeValue> { new("NodeId", ToPortableId(nodeId)) };
        if (nodeId.NamespaceIndex == FolderNamespace)
        {
            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            result.Add(new("DisplayName", nodeId.IdentifierAsString.Split('/')[^1]));
            return result;
        }

        var address = Resolve(nodeId);
        result.Add(new("NodeClass", nameof(NodeClass.Variable)));
        if (nodeId.NamespaceIndex == SymbolNamespace && _symbols.TryGetValue(nodeId.IdentifierAsString, out var symbol))
        {
            result.Add(new("Symbol", symbol.Name));
            if (symbol.Comment is { } comment)
            {
                result.Add(new("Description", comment));
            }
        }

        result.Add(new("Address", address.ToString()));
        result.Add(new("Area", AreaName(address)));
        result.Add(new("DataType", address.Type.Name));
        result.Add(new("Size", address.Type.IsBit && !address.Type.IsArray ? "1 bit" : $"{address.ByteLength.ToString(CultureInfo.InvariantCulture)} bytes"));

        var (data, status, error) = (await ReadRawAsync([address], cancellationToken).ConfigureAwait(false))[0];
        result.Add(new("Value", data is null ? $"{status}{(error is null ? string.Empty : $": {error}")}" : ValueFormatter.Format(address.Type.Decode(data, address.Bit))));
        result.Add(new("AccessLevel", "Read, Write"));
        return result;
    }

    public async Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        RequireEndpoint();
        var addresses = nodeIds.Select(id => TryResolve(id, out var address) ? address : null).ToList();
        var reads = await ReadRawAsync([.. addresses.OfType<S7Address>()], cancellationToken).ConfigureAwait(false);
        var values = new object?[nodeIds.Count];
        var next = 0;
        for (var i = 0; i < addresses.Count; i++)
        {
            if (addresses[i] is { } address)
            {
                var data = reads[next++].Data;
                values[i] = data is null ? null : address.Type.Decode(data, address.Bit);
            }
        }

        return values;
    }

    /// <summary>
    /// Writes a value of the address's type. A single BOOL is written as one bit; BOOL arrays are read, changed and
    /// written back, so other bits in the same bytes keep their values unless the PLC changes them in between.
    /// </summary>
    public async Task WriteValueAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        RequireEndpoint();
        var address = Resolve(nodeId);
        var value = address.Type.ParseInput(text);
        var dataType = ToDataType(address.Area);
        if (address.Type.IsBit && !address.Type.IsArray)
        {
            await RunAsync(async (plc, ct) =>
            {
                await plc.WriteBitAsync(dataType, address.Db, address.Byte, address.Bit, (bool)value, ct).ConfigureAwait(false);
                return true;
            }, address, write: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] buffer;
        if (address.Type.IsBit)
        {
            var (data, status, error) = (await ReadRawAsync([address], cancellationToken).ConfigureAwait(false))[0];
            buffer = data ?? throw new DeviceClientException(status, $"{address}: {error}");
        }
        else
        {
            buffer = new byte[address.ByteLength];
        }

        address.Type.Encode(value, buffer, address.Bit);
        await RunAsync(async (plc, ct) =>
        {
            await plc.WriteBytesAsync(dataType, address.Db, address.Byte, buffer, ct).ConfigureAwait(false);
            return true;
        }, address, write: true, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<NodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(onUpdate);
        RequireEndpoint();
        var interval = Math.Max(50, (int)samplingIntervalMs);
        var results = new List<MonitorResult>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            if (nodeId.NamespaceIndex == FolderNamespace)
            {
                results.Add(new MonitorResult(nodeId, null, new DeviceError(DeviceStatusCodes.BadAttributeIdInvalid, $"'{ToPortableId(nodeId)}' is a folder; watch the values in it.")));
                continue;
            }

            if (!TryResolve(nodeId, out var address, out var error))
            {
                results.Add(new MonitorResult(nodeId, null, new DeviceError(DeviceStatusCodes.BadNodeIdUnknown, error)));
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
            results.Add(new MonitorResult(nodeId, new S7MonitorHandle(group, entry), DeviceError.Good));
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
        var owned = monitors.OfType<S7MonitorHandle>().Where(h => ReferenceEquals(h.Group.Owner, this)).ToList();
        var results = await MonitorManyAsync([.. owned.Select(h => h.Entry.NodeId)], onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        foreach (var handle in owned)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    public async Task<ConnectionDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = RequireEndpoint();
        List<PollGroup> groups;
        lock (_lock)
        {
            groups = [.. _groups.Values.OrderBy(g => g.IntervalMs)];
        }

        string cpuState;
        try
        {
            var status = await RunAsync((plc, ct) => plc.ReadStatusAsync(ct), null, write: false, cancellationToken).ConfigureAwait(false);
            cpuState = status switch
            {
                0x08 => "RUN",
                0x04 => "STOP",
                _ => $"0x{status:X2}",
            };
        }
        catch (DeviceClientException ex)
        {
            cpuState = $"unknown ({ex.Message})";
        }

        var info = new List<(string, string)>
        {
            ("Host", $"{endpoint.Host}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}"),
            ("CPU", $"{CpuName(endpoint.Cpu)} · rack {endpoint.Rack.ToString(CultureInfo.InvariantCulture)}, slot {endpoint.Slot.ToString(CultureInfo.InvariantCulture)} · {cpuState}"),
            ("Protocol", "S7comm over ISO-on-TCP via S7.Net"),
            ("PDU size", $"{(_plc?.MaxPDUSize ?? 0).ToString(CultureInfo.InvariantCulture)} bytes"),
            ("Timeout", DiagnosticsFormat.Duration(endpoint.Timeout.TotalMilliseconds)),
            ("Connected since", DiagnosticsFormat.Since(_connectedAt)),
            ("Link lost", DiagnosticsFormat.Reconnects(_linkLosses, _lastLinkLoss)),
            ("Tags", _tags.Source is null
                ? "none (import one, or --map / ?tags=<file>)"
                : $"{_symbols.Count.ToString(CultureInfo.InvariantCulture)} from {Path.GetFileName(_tags.Source)}{(_tags.Skipped.Count > 0 ? $" · {_tags.Skipped.Count.ToString(CultureInfo.InvariantCulture)} skipped: {string.Join("; ", _tags.Skipped.Take(3))}{(_tags.Skipped.Count > 3 ? "; …" : string.Empty)}" : string.Empty)}"),
        };

        var requests = Interlocked.Read(ref _requests);
        info.Add(("Requests", $"{DiagnosticsFormat.Rate(requests, ref _rateSample)} · {requests.ToString("N0", CultureInfo.InvariantCulture)} total · {Interlocked.Read(ref _failedRequests).ToString("N0", CultureInfo.InvariantCulture)} failed"));
        if (groups.Count == 0)
        {
            info.Add(("Polling", "nothing monitored"));
        }

        foreach (var group in groups)
        {
            var cycle = group.LastCycleMs is { } ms ? $" · cycle {DiagnosticsFormat.Duration(ms)} in {group.LastRequests.ToString(CultureInfo.InvariantCulture)} requests" : string.Empty;
            var late = group.LastCycleMs > group.IntervalMs ? " (slower than the refresh time)" : string.Empty;
            var error = group.LastError is { } last ? $" · last error: {last}" : string.Empty;
            info.Add(($"Poll {DiagnosticsFormat.Duration(group.IntervalMs)}",
                $"{group.Count.ToString(CultureInfo.InvariantCulture)} values · last {DiagnosticsFormat.Ago(group.LastCycleUtc ?? DateTime.MinValue)}{cycle}{late}{error}"));
        }

        return new ConnectionDiagnostics(info, []);
    }

    private void LoadTags(S7TagList tags)
    {
        var symbols = new Dictionary<string, S7Symbol>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, List<BrowseItem>>(StringComparer.Ordinal);
        foreach (var symbol in tags.Symbols)
        {
            symbols[symbol.Name] = symbol;
            var parent = TagsId.IdentifierAsString;
            foreach (var folder in symbol.Folders)
            {
                var path = $"{parent}/{folder}";
                if (!folders.ContainsKey(path))
                {
                    Children(parent).Add(new BrowseItem(Folder(path), folder, folder, NodeClass.Object));
                    folders[path] = [];
                }

                parent = path;
            }

            Children(parent).Add(new BrowseItem(new NodeId(symbol.Name, SymbolNamespace), symbol.DisplayName, symbol.Address.Type.Name, NodeClass.Variable, HasChildren: false));
        }

        _symbols = symbols;
        _folders = folders;
        _tags = tags;

        List<BrowseItem> Children(string folder) =>
            folders.TryGetValue(folder, out var list) ? list : folders[folder] = [];
    }

    /// <summary>DB numbers to list: from the tag list and the URL's <c>db=</c>.</summary>
    private List<int> DataBlocks(S7Endpoint endpoint) =>
        [.. _symbols.Values.Where(s => s.Address.Area == S7Area.DataBlock).Select(s => s.Address.Db).Concat(endpoint.DataBlocks).Distinct().Order()];

    private S7Address Resolve(NodeId nodeId) =>
        TryResolve(nodeId, out var address, out var error) ? address : throw new DeviceClientException(DeviceStatusCodes.BadNodeIdUnknown, error);

    private bool TryResolve(NodeId nodeId, out S7Address address) => TryResolve(nodeId, out address, out _);

    private bool TryResolve(NodeId nodeId, out S7Address address, out string error)
    {
        address = null!;
        error = string.Empty;
        switch (nodeId.NamespaceIndex)
        {
            case AddressNamespace:
                return S7Address.TryParse(nodeId.IdentifierAsString, out address, out error);
            case SymbolNamespace when _symbols.TryGetValue(nodeId.IdentifierAsString, out var symbol):
                address = symbol.Address;
                return true;
            case SymbolNamespace:
                error = _tags.Source is null
                    ? $"'{nodeId.IdentifierAsString}' is neither an S7 address nor a symbol (no tag list is loaded)."
                    : $"Symbol '{nodeId.IdentifierAsString}' is not in {Path.GetFileName(_tags.Source)}.";
                return false;
            default:
                error = $"'{ToPortableId(nodeId)}' is not a value of this PLC.";
                return false;
        }
    }

    /// <summary>
    /// Reads the bytes of each address. Addresses in the same area that are close together are read in one request;
    /// when such a request fails with an error from the PLC (e.g. one address is past the end of its DB), its addresses
    /// are read one by one so the others still get values.
    /// </summary>
    private async Task<(byte[]? Data, DeviceStatusCode Status, string? Error)[]> ReadRawAsync(IReadOnlyList<S7Address> addresses, CancellationToken cancellationToken, Action? onRequest = null)
    {
        var results = new (byte[]?, DeviceStatusCode, string?)[addresses.Count];
        var ranges = new List<(S7Address First, int Start, int End, List<int> Items)>();
        foreach (var group in addresses.Select((a, i) => (Address: a, Index: i)).GroupBy(x => (x.Address.Area, x.Address.Db)))
        {
            (S7Address First, int Start, int End, List<int> Items)? current = null;
            foreach (var (address, index) in group.OrderBy(x => x.Address.Byte))
            {
                var end = address.Byte + address.ByteLength;
                if (current is { } c && address.Byte <= c.End + MaxGap && Math.Max(end, c.End) - c.Start <= MaxRangeBytes)
                {
                    c.Items.Add(index);
                    current = c with { End = Math.Max(end, c.End) };
                }
                else
                {
                    if (current is { } done)
                    {
                        ranges.Add(done);
                    }

                    current = (address, address.Byte, end, [index]);
                }
            }

            if (current is { } last)
            {
                ranges.Add(last);
            }
        }

        DeviceClientException? linkDown = null;
        foreach (var range in ranges)
        {
            if (linkDown is not null)
            {
                foreach (var index in range.Items)
                {
                    results[index] = (null, linkDown.StatusCode, linkDown.Message);
                }

                continue;
            }

            try
            {
                onRequest?.Invoke();
                var data = await ReadBytesAsync(range.First, range.Start, range.End - range.Start, cancellationToken).ConfigureAwait(false);
                foreach (var index in range.Items)
                {
                    var address = addresses[index];
                    results[index] = (data.AsSpan(address.Byte - range.Start, address.ByteLength).ToArray(), DeviceStatusCodes.Good, null);
                }
            }
            catch (DeviceClientException ex) when (IsLinkLoss(ex))
            {
                linkDown = ex;
                foreach (var index in range.Items)
                {
                    results[index] = (null, ex.StatusCode, ex.Message);
                }
            }
            catch (DeviceClientException) when (range.Items.Count > 1)
            {
                foreach (var index in range.Items)
                {
                    var address = addresses[index];
                    try
                    {
                        onRequest?.Invoke();
                        results[index] = (await ReadBytesAsync(address, address.Byte, address.ByteLength, cancellationToken).ConfigureAwait(false), DeviceStatusCodes.Good, null);
                    }
                    catch (DeviceClientException link) when (IsLinkLoss(link))
                    {
                        linkDown = link;
                        results[index] = (null, link.StatusCode, link.Message);
                    }
                    catch (DeviceClientException single)
                    {
                        results[index] = (null, single.StatusCode, single.Message);
                    }
                }
            }
            catch (DeviceClientException ex)
            {
                results[range.Items[0]] = (null, ex.StatusCode, ex.Message);
            }
        }

        return results;
    }

    private Task<byte[]> ReadBytesAsync(S7Address address, int start, int count, CancellationToken cancellationToken) =>
        RunAsync((plc, ct) => plc.ReadBytesAsync(ToDataType(address.Area), address.Db, start, count, ct), address with { Byte = start }, write: false, cancellationToken);

    /// <summary>
    /// Runs one request on the shared connection, reopening it first after a loss. Errors the PLC reports (unknown
    /// DB, address out of range, access denied) leave the connection open; anything else may have left the stream
    /// mid-telegram, so the connection is closed and reopened by the next request.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<Plc, CancellationToken, Task<T>> action, S7Address? address, bool write, CancellationToken cancellationToken)
    {
        var endpoint = RequireEndpoint();
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plc = _plc ?? throw new InvalidOperationException("Not connected.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(endpoint.Timeout);
            try
            {
                if (!plc.IsConnected)
                {
                    await plc.OpenAsync(timeout.Token).ConfigureAwait(false);
                }

                Interlocked.Increment(ref _requests);
                var result = await action(plc, timeout.Token).ConfigureAwait(false);
                ReportLink(true);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _failedRequests);
                var where = address is null ? string.Empty : $"{address.Location}: ";
                if (!IsLinkFailure(ex) && plc.IsConnected)
                {
                    var text = PlcError(ex) ?? Describe(ex);
                    var key = text.Replace(" ", string.Empty, StringComparison.Ordinal);
                    var status = key.Contains("notallowed", StringComparison.OrdinalIgnoreCase)
                        ? write ? DeviceStatusCodes.BadNotWritable : DeviceStatusCodes.BadNotReadable
                        : key.Contains("outofrange", StringComparison.OrdinalIgnoreCase) ? DeviceStatusCodes.BadOutOfRange
                        : key.Contains("doesnotexist", StringComparison.OrdinalIgnoreCase) ? DeviceStatusCodes.BadNodeIdUnknown
                        : DeviceStatusCodes.Bad;
                    throw new DeviceClientException(status, where + (PlcError(ex) is null ? $"the PLC rejected the request ({text})" : text));
                }

                plc.Close();
                ReportLink(false);
                var timedOut = ex is OperationCanceledException or TimeoutException;
                throw new DeviceClientException(
                    timedOut ? DeviceStatusCodes.BadTimeout : DeviceStatusCodes.BadCommunicationError,
                    where + (timedOut ? $"no answer within {DiagnosticsFormat.Duration(endpoint.Timeout.TotalMilliseconds)}" : Describe(ex)));
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// The request didn't get a complete answer: socket errors, timeouts, a closed stream. The connection may be left
    /// mid-telegram, so it is closed. Anything else (an error answer, or one S7.Net can't parse) leaves it open.
    /// </summary>
    private static bool IsLinkFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException
                || (e is PlcException plc && plc.ErrorCode is ErrorCode.ConnectionError or ErrorCode.IPAddressNotAvailable or ErrorCode.SendData))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The error text when the PLC answered with an error (S7.Net: "Received error from PLC: …").</summary>
    private static string? PlcError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            const string Prefix = "Received error from PLC: ";
            var index = e.Message.IndexOf(Prefix, StringComparison.Ordinal);
            if (index >= 0)
            {
                return e.Message[(index + Prefix.Length)..].TrimEnd('.');
            }
        }

        return null;
    }

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null && inner is PlcException)
        {
            inner = inner.InnerException;
        }

        return inner is SocketException socket ? $"{socket.SocketErrorCode} ({socket.Message})" : inner.Message;
    }

    private void ReportLink(bool ok)
    {
        if (_endpoint is null)
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

    private S7Endpoint RequireEndpoint() =>
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

        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            _plc?.Close();
            _plc = null;
        }
        finally
        {
            _io.Release();
        }

        _endpoint = null;
        _connectedAt = null;
        LoadTags(S7TagList.Empty);
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

    private static NodeId Folder(string name) => new(name, FolderNamespace);

    private static NodeId AddressId(S7Address address) => new(address.ToString(), AddressNamespace);

    private static string DbFolder(int db) => "DB" + db.ToString(CultureInfo.InvariantCulture);

    private static NodeId AreaFolder(S7Address address) => address.Area switch
    {
        S7Area.Input => InputsId,
        S7Area.Output => OutputsId,
        S7Area.Memory => MemoryId,
        _ => Folder(DbFolder(address.Db)),
    };

    private static bool TryParseAreaFolder(string name, out S7Area area, out int db)
    {
        db = 0;
        (var known, area) = name switch
        {
            "I" => (true, S7Area.Input),
            "Q" => (true, S7Area.Output),
            "M" => (true, S7Area.Memory),
            _ => (false, S7Area.DataBlock),
        };
        return known
            || (name.StartsWith("DB", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out db)
                && db > 0);
    }

    private static IEnumerable<NodeId> FolderPrefixes(string[] segments)
    {
        for (var i = 1; i <= segments.Length; i++)
        {
            yield return Folder(string.Join('/', segments.Take(i)));
        }
    }

    private static BrowseItem ToBrowseItem(S7Address address, bool hasChildren)
    {
        var location = address.Location;
        var display = address.Area == S7Area.DataBlock ? location[(location.IndexOf('.', StringComparison.Ordinal) + 1)..] : location;
        return new BrowseItem(AddressId(address), display, address.Type.Name, NodeClass.Variable, hasChildren);
    }

    private static string AreaName(S7Address address) => address.Area switch
    {
        S7Area.Input => "Inputs (I)",
        S7Area.Output => "Outputs (Q)",
        S7Area.Memory => "Memory (M)",
        _ => $"Data block {address.Db.ToString(CultureInfo.InvariantCulture)}",
    };

    private static DataType ToDataType(S7Area area) => area switch
    {
        S7Area.Input => DataType.Input,
        S7Area.Output => DataType.Output,
        S7Area.Memory => DataType.Memory,
        _ => DataType.DataBlock,
    };

    private static string CpuName(CpuType cpu) => cpu switch
    {
        CpuType.S7200 => "S7-200",
        CpuType.S7200Smart => "S7-200 SMART",
        CpuType.S7300 => "S7-300",
        CpuType.S7400 => "S7-400",
        CpuType.S71200 => "S7-1200",
        CpuType.S71500 => "S7-1500",
        _ => cpu.ToString(),
    };

    /// <summary>The connection failed (not an error answer from the PLC); <see cref="RunAsync"/> reopens it on the next request.</summary>
    private static bool IsLinkLoss(DeviceClientException ex) =>
        ex.StatusCode == DeviceStatusCodes.BadCommunicationError || ex.StatusCode == DeviceStatusCodes.BadTimeout;

    /// <summary>
    /// <c>s7://host[:port][/rack,slot][?cpu=1500&amp;tags=&lt;file&gt;&amp;db=1,2&amp;bytes=64&amp;timeout=5000]</c>.
    /// Port 102, rack 0, slot 1 (S7-1200/1500) by default; S7-300 CPUs are usually in slot 2.
    /// </summary>
    internal sealed record S7Endpoint(
        string Host,
        int Port,
        short Rack,
        short Slot,
        CpuType Cpu,
        string? TagFile,
        IReadOnlyList<int> DataBlocks,
        int AreaBytes,
        TimeSpan Timeout)
    {
        public const string Example = "s7://192.168.0.1/0,1?tags=plc-tags.csv";

        public static S7Endpoint Parse(string url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(DeviceClient.S7Scheme, StringComparison.OrdinalIgnoreCase) || uri.Host.Length == 0)
            {
                throw new FormatException($"'{url}' is not an S7 address. Expected s7://host[:port][/rack,slot][?tags=file], e.g. {Example}");
            }

            short rack = 0, slot = 1;
            var path = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (path.Length > 0)
            {
                var parts = path.Split(',');
                if (parts.Length != 2
                    || !short.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out rack)
                    || !short.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out slot)
                    || rack > 7 || slot > 31)
                {
                    throw new FormatException($"'{path}' is not rack,slot (e.g. 0,1 for S7-1200/1500, 0,2 for S7-300).");
                }
            }

            var cpu = CpuType.S71500;
            string? tags = null;
            var blocks = new List<int>();
            var areaBytes = 64;
            var timeout = TimeSpan.FromSeconds(5);
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]).ToLowerInvariant();
                var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                switch (key)
                {
                    case "cpu":
                        cpu = value.ToUpperInvariant().Replace("S7", string.Empty, StringComparison.Ordinal).Trim('-', ' ') switch
                        {
                            "200" => CpuType.S7200,
                            "200SMART" or "200 SMART" => CpuType.S7200Smart,
                            "300" => CpuType.S7300,
                            "400" => CpuType.S7400,
                            "1200" => CpuType.S71200,
                            "1500" => CpuType.S71500,
                            _ => throw new FormatException($"cpu={value}: expected 300, 400, 1200 or 1500."),
                        };
                        break;
                    case "tags":
                        tags = value.StartsWith('~') ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[1..]) : value;
                        break;
                    case "db":
                        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            blocks.Add(int.TryParse(item.StartsWith("DB", StringComparison.OrdinalIgnoreCase) ? item[2..] : item, NumberStyles.None, CultureInfo.InvariantCulture, out var db) && db > 0
                                ? db
                                : throw new FormatException($"db={value}: expected DB numbers, e.g. db=1,2,10."));
                        }

                        break;
                    case "bytes":
                        areaBytes = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 4096
                            ? n
                            : throw new FormatException($"bytes={value}: expected 1 to 4096.");
                        break;
                    case "timeout":
                        timeout = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0
                            ? TimeSpan.FromMilliseconds(ms)
                            : throw new FormatException($"timeout={value}: expected milliseconds.");
                        break;
                    default:
                        throw new FormatException($"Unknown S7 option '{key}'. Options: cpu, tags, db, bytes, timeout.");
                }
            }

            return new S7Endpoint(uri.Host, uri.IsDefaultPort || uri.Port < 0 ? 102 : uri.Port, rack, slot, cpu, tags, blocks, areaBytes, timeout);
        }
    }

    /// <summary>All values sharing one refresh time, read together on a timer.</summary>
    private sealed class PollGroup : IDisposable
    {
        private readonly List<PollEntry> _entries = [];
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _stopped;

        public PollGroup(S7Client owner, int intervalMs)
        {
            Owner = owner;
            IntervalMs = intervalMs;
        }

        public S7Client Owner { get; }

        public int IntervalMs { get; }

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

        public DateTime? LastCycleUtc { get; private set; }

        /// <summary>How long the last round of reads took; above <see cref="IntervalMs"/> the PLC can't keep up.</summary>
        public double? LastCycleMs { get; private set; }

        public int LastRequests { get; private set; }

        public string? LastError { get; private set; }

        public PollEntry Add(NodeId nodeId, S7Address address, Action<ValueUpdate> onUpdate)
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

                if (entries.Length == 0)
                {
                    continue;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var requests = 0;
                (byte[]? Data, DeviceStatusCode Status, string? Error)[] reads;
                try
                {
                    reads = await Owner.ReadRawAsync([.. entries.Select(e => e.Address)], cancellationToken, () => requests++).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (Owner._endpoint is null)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && Errors.IsRecoverable(ex))
                {
                    // Surprises must not stop the poll loop; the next tick tries again.
                    System.Diagnostics.Trace.TraceError($"Polling S7 values failed: {ex}");
                    reads = [.. entries.Select(_ => ((byte[]?)null, DeviceStatusCodes.BadUnexpectedError, (string?)ex.Message))];
                }

                LastCycleMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                LastCycleUtc = DateTime.UtcNow;
                LastRequests = requests;
                for (var i = 0; i < entries.Length; i++)
                {
                    Report(entries[i], reads[i].Data, reads[i].Status, reads[i].Error);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }

        private void Report(PollEntry entry, byte[]? data, DeviceStatusCode status, string? error)
        {
            if (status.IsBad)
            {
                LastError = error ?? status.ToString();
            }

            if (status == entry.LastStatus && (data is null || data.AsSpan().SequenceEqual(entry.LastData)))
            {
                return;
            }

            entry.LastStatus = status;
            entry.LastData = data;
            object? value = null;
            if (data is not null)
            {
                try
                {
                    value = entry.Address.Type.Decode(data, entry.Address.Bit);
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or System.Text.DecoderFallbackException)
                {
                    status = DeviceStatusCodes.BadDecodingError;
                }
            }

            var now = DateTime.UtcNow;
            entry.OnUpdate(new ValueUpdate(
                entry.NodeId,
                status.IsGood ? ValueFormatter.Format(value) : status.ToString(),
                status,
                now,
                now,
                ValueFormatter.ToNumeric(value),
                value));
        }
    }

    private sealed class PollEntry(NodeId nodeId, S7Address address, Action<ValueUpdate> onUpdate)
    {
        public NodeId NodeId { get; } = nodeId;

        public S7Address Address { get; } = address;

        public Action<ValueUpdate> OnUpdate { get; } = onUpdate;

        public DeviceStatusCode? LastStatus { get; set; }

        public byte[]? LastData { get; set; }
    }

    private sealed class S7MonitorHandle(PollGroup group, PollEntry entry) : IAsyncDisposable
    {
        public PollGroup Group => group;

        public PollEntry Entry => entry;

        public async ValueTask DisposeAsync() => await group.RemoveAsync(entry).ConfigureAwait(false);
    }
}
