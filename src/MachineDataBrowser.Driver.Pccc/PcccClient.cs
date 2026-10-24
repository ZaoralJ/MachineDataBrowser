using System.Globalization;
using libplctag;
using MachineDataBrowser.Core;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Driver.Pccc;

/// <summary>
/// PCCC client for Allen-Bradley SLC 500, MicroLogix and PLC-5 controllers over EtherNet/IP, built on libplctag.
/// These controllers have no tag names or symbol table, only data-table files addressed like <c>N7:0</c>:
/// <list type="bullet">
/// <item>ns=2 folder <c>Root</c> lists the data files found;</item>
/// <item>ns=1 string ids are addresses: files (<c>N7</c>), elements (<c>N7:0</c>), bits (<c>B3:0/5</c>) and
/// members of timers, counters and controls (<c>T4:0.ACC</c>, <c>T4:0.DN</c>).</item>
/// </list>
/// PCCC has no file directory libplctag can read, so the standard files (<c>O0</c> … <c>F8</c>) are probed and
/// further ones are named in the endpoint (<c>?files=N10,ST11</c>). Monitoring polls per refresh time and
/// reports only changes.
/// </summary>
public sealed class PcccClient : IDeviceClient, IConnectionDiagnosticsSource
{
    internal const ushort AddressNamespace = 1;
    internal const ushort FolderNamespace = 2;
    private const int MaxBrowseElements = 1000;

    private static readonly NodeId RootId = new("Root", FolderNamespace);

    private static readonly string[] StandardFiles = ["O0", "I1", "S2", "B3", "T4", "C5", "R6", "N7", "F8"];

    /// <summary>
    /// libplctag (2.6) lists a new session before it creates the session's mutex, so a tag created at that moment can
    /// join it and have its request silently dropped: the read times out. Each connection therefore holds one tag,
    /// created alone, that keeps its session alive; every other tag joins a session that is already set up.
    /// </summary>
    private static readonly Lock SessionCreation = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Task<int>> _fileSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, PollGroup> _groups = [];
    private IReadOnlyList<PcccAddress> _files = [];
    private PcccEndpoint? _endpoint;
    private Tag? _sessionTag;
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

    public bool IsReadOnly => false;

    public string? ServerUri => _endpoint is { } e ? $"{e.FamilyName} {e.Gateway}" : null;

    public BrowseItem Root { get; } = new(RootId, "Data Files", "Root", NodeClass.Object);

    public string ToPortableId(NodeId nodeId) =>
        nodeId.NamespaceIndex == FolderNamespace ? "@" + nodeId.IdentifierAsString : nodeId.IdentifierAsString;

    /// <summary>The data-table address (<c>N7:0</c>, <c>T4:0.ACC</c>, <c>B3:0/5</c>); the root as <c>@Root</c>.</summary>
    public string ToDisplayId(NodeId nodeId) => ToPortableId(nodeId);

    public NodeId ParsePortableId(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.StartsWith('@'))
        {
            return new NodeId(text[1..], FolderNamespace);
        }

        // Normalised, so N7:0 and n7:0 are the same node in watch lists and sessions.
        return new NodeId(PcccAddress.TryParse(text, out var address) ? address.ToString() : text.Trim(), AddressNamespace);
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
                _endpoint = PcccEndpoint.Parse(options.EndpointUrl);
            }
            catch (FormatException ex)
            {
                throw new DeviceClientException(DeviceStatusCodes.BadTcpEndpointUrlInvalid, ex.Message);
            }

            _sessionTag = await OpenSessionAsync(_endpoint, cancellationToken).ConfigureAwait(false);

            // Probing the files proves the connection and fills the first browse level.
            var candidates = StandardFiles.Concat(_endpoint.ExtraFiles)
                .Select(f => PcccAddress.TryParse(f, out var a) ? a : null)
                .OfType<PcccAddress>()
                .DistinctBy(a => a.File)
                .ToList();
            var probes = await Task.WhenAll(candidates.Select(async file =>
            {
                try
                {
                    await ReadElementAsync(_endpoint, file with { Element = 0 }, cancellationToken).ConfigureAwait(false);
                    return (File: file, Error: (DeviceClientException?)null);
                }
                catch (DeviceClientException ex)
                {
                    return (File: file, Error: ex);
                }
            })).ConfigureAwait(false);

            _files = [.. probes.Where(p => p.Error is null).Select(p => p.File).OrderBy(f => f.File)];
            if (_files.Count == 0)
            {
                var error = probes.Select(p => p.Error!).FirstOrDefault(e => e.StatusCode != DeviceStatusCodes.BadNodeIdUnknown) ?? probes[0].Error!;
                throw new DeviceClientException(error.StatusCode, $"No data files found on {_endpoint.Gateway} ({_endpoint.FamilyName}): {error.Message}");
            }

            _connectedAt = DateTime.UtcNow;
            _linkLosses = 0;
            _lastLinkLoss = null;
            _rateSample = null;
            State = ConnectionState.Connected;
        }
        catch
        {
            _endpoint = null;
            CloseSession();
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
        var endpoint = RequireEndpoint();
        if (nodeId == RootId)
        {
            var items = new List<BrowseItem>();
            foreach (var file in _files)
            {
                var size = await GetFileSizeAsync(endpoint, file, cancellationToken).ConfigureAwait(false);
                items.Add(new BrowseItem(ToNodeId(file), file.FileName, $"{file.Type.Name}[{size.ToString(CultureInfo.InvariantCulture)}]", NodeClass.Object));
            }

            return items;
        }

        var address = Resolve(nodeId);
        if (address.IsFile)
        {
            var size = await GetFileSizeAsync(endpoint, address, cancellationToken).ConfigureAwait(false);
            return [.. Enumerable.Range(0, Math.Min(size, MaxBrowseElements)).Select(i => ToBrowseItem(address with { Element = i }))];
        }

        if (address.Type.IsStructure && address.Field is null)
        {
            return [.. address.Type.Fields.Select(f => ToBrowseItem(address with { Field = f }))];
        }

        // Binary-file words show their bits; elsewhere bits are addressed directly (N7:0/3).
        if (address.Type == PcccFileType.Bit && address.Bit is null)
        {
            return [.. Enumerable.Range(0, 16).Select(b => ToBrowseItem(address with { Bit = b }))];
        }

        return [];
    }

    public Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default)
    {
        var path = new List<NodeId> { RootId };
        if (nodeId == RootId)
        {
            return Task.FromResult<IReadOnlyList<NodeId>>(path);
        }

        if (nodeId.NamespaceIndex != AddressNamespace || !PcccAddress.TryParse(nodeId.IdentifierAsString, out var address))
        {
            return Task.FromResult<IReadOnlyList<NodeId>>([]);
        }

        path.Add(ToNodeId(address.FileAddress));
        if (!address.IsFile)
        {
            path.Add(ToNodeId(address.ElementAddress));
            if (address.Field is not null || address.Bit is not null)
            {
                path.Add(ToNodeId(address));
            }
        }

        return Task.FromResult<IReadOnlyList<NodeId>>(path.Count > maxDepth ? [] : path);
    }

    public async Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var endpoint = RequireEndpoint();
        var result = new List<AttributeValue> { new("NodeId", ToPortableId(nodeId)) };
        if (nodeId.NamespaceIndex == FolderNamespace)
        {
            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            result.Add(new("DisplayName", Root.DisplayName));
            return result;
        }

        var address = Resolve(nodeId);
        result.Add(new("NodeClass", address.IsVariable ? nameof(NodeClass.Variable) : nameof(NodeClass.Object)));
        result.Add(new("Address", address.ToString()));
        result.Add(new("FileType", $"{address.Type.Name} ({address.Type.Letter})"));
        if (address.IsFile)
        {
            var size = await GetFileSizeAsync(endpoint, address, cancellationToken).ConfigureAwait(false);
            result.Add(new("Elements", size.ToString(CultureInfo.InvariantCulture)));
            result.Add(new("ElementSize", $"{address.Type.ElementSize.ToString(CultureInfo.InvariantCulture)} bytes"));
            return result;
        }

        result.Add(new("DataType", address.TypeName));
        if (address.IsVariable)
        {
            string value;
            try
            {
                value = ValueFormatter.Format(await ReadValueAsync(endpoint, address, cancellationToken).ConfigureAwait(false));
            }
            catch (DeviceClientException ex)
            {
                value = ex.StatusCode.ToString();
            }

            result.Add(new("Value", value));
            result.Add(new("AccessLevel", "Read, Write"));
        }

        return result;
    }

    public async Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var endpoint = RequireEndpoint();
        var reads = nodeIds.Select(async id =>
        {
            try
            {
                var address = Resolve(id);
                return address.IsVariable ? await ReadValueAsync(endpoint, address, cancellationToken).ConfigureAwait(false) : null;
            }
            catch (DeviceClientException)
            {
                return null;
            }
        });
        return await Task.WhenAll(reads).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an element, a timer/counter/control word, or a bit. Bits are read-modify-write of their word in two
    /// requests, so a bit the program changes in between can be overwritten.
    /// </summary>
    public async Task WriteValueAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var endpoint = RequireEndpoint();
        var address = Resolve(nodeId);
        if (!address.IsVariable)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadNotWritable, $"{address} is a {address.Type.Name.ToLowerInvariant()}; write its members.");
        }

        using var tag = CreateTag(endpoint, WordTagName(address));
        await ReadTagAsync(tag, cancellationToken).ConfigureAwait(false);
        var buffer = tag.GetBuffer();
        if (address.IsBoolean)
        {
            var on = (bool)ValueParser.Parse(text, DeviceDataType.Boolean, isArray: false);
            var bit = address.Field?.Bit ?? address.Bit!.Value;
            var mask = 1 << (bit % 8);
            buffer[bit / 8] = (byte)(on ? buffer[bit / 8] | mask : buffer[bit / 8] & ~mask);
        }
        else if (address.Field is not null)
        {
            BitConverter.TryWriteBytes(buffer, (short)ValueParser.Parse(text, DeviceDataType.Int16, isArray: false));
        }
        else
        {
            switch (address.Type.Letter)
            {
                case "F":
                    BitConverter.TryWriteBytes(buffer, (float)ValueParser.Parse(text, DeviceDataType.Float, isArray: false));
                    break;
                case "L":
                    BitConverter.TryWriteBytes(buffer, (int)ValueParser.Parse(text, DeviceDataType.Int32, isArray: false));
                    break;
                case "ST":
                    PcccCodec.EncodeString(Unquote(text), buffer);
                    break;
                default:
                    BitConverter.TryWriteBytes(buffer, (short)ValueParser.Parse(text, DeviceDataType.Int16, isArray: false));
                    break;
            }
        }

        tag.SetBuffer(buffer);
        try
        {
            await tag.WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            throw new DeviceClientException(ToStatus(ex), $"{tag.Name}: {ex.Message}");
        }
    }

    public Task<IReadOnlyList<MonitorResult>> MonitorManyAsync(
        IReadOnlyList<NodeId> nodeIds,
        Action<ValueUpdate> onUpdate,
        double samplingIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(onUpdate);
        var endpoint = RequireEndpoint();
        var interval = Math.Max(50, (int)samplingIntervalMs);
        var results = new List<MonitorResult>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            PcccAddress address;
            try
            {
                address = Resolve(nodeId);
            }
            catch (DeviceClientException ex)
            {
                results.Add(new MonitorResult(nodeId, null, new DeviceError(ex.StatusCode, ex.Message)));
                continue;
            }

            if (!address.IsVariable)
            {
                results.Add(new MonitorResult(nodeId, null, new DeviceError(DeviceStatusCodes.BadAttributeIdInvalid, $"'{address}' is not a value; watch its members.")));
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

            var entry = group.Add(nodeId, address, CreateTag(endpoint, address.ElementName), onUpdate);
            results.Add(new MonitorResult(nodeId, new PcccMonitorHandle(group, entry), DeviceError.Good));
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
        var owned = monitors.OfType<PcccMonitorHandle>().Where(h => ReferenceEquals(h.Group.Owner, this)).ToList();
        var results = await MonitorManyAsync([.. owned.Select(h => h.Entry.NodeId)], onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        foreach (var handle in owned)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    public Task<ConnectionDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = RequireEndpoint();
        List<PollGroup> groups;
        lock (_lock)
        {
            groups = [.. _groups.Values.OrderBy(g => g.IntervalMs)];
        }

        var info = new List<(string, string)>
        {
            ("Gateway", endpoint.Gateway),
            ("Controller", endpoint.FamilyName),
            ("Protocol", "EtherNet/IP · PCCC via libplctag"),
            ("Timeout", DiagnosticsFormat.Duration(endpoint.Timeout.TotalMilliseconds)),
            ("Connected since", DiagnosticsFormat.Since(_connectedAt)),
            ("Link lost", DiagnosticsFormat.Reconnects(_linkLosses, _lastLinkLoss)),
            ("Data files", string.Join(", ", _files.Select(f => f.FileName))),
        };
        if (endpoint.Path.Length > 0)
        {
            info.Insert(1, ("Path", endpoint.Path));
        }

        var reads = groups.Sum(g => g.Reads);
        var failures = groups.Sum(g => g.Failures);
        info.Add(("Reads", $"{DiagnosticsFormat.Rate(reads, ref _rateSample)} · {reads.ToString("N0", CultureInfo.InvariantCulture)} total · {failures.ToString("N0", CultureInfo.InvariantCulture)} failed"));
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
                $"{group.Count.ToString(CultureInfo.InvariantCulture)} addresses · last {DiagnosticsFormat.Ago(group.LastCycleUtc ?? DateTime.MinValue)}{cycle}{late}{error}"));
        }

        return Task.FromResult(new ConnectionDiagnostics(info, []));
    }

    private PcccEndpoint RequireEndpoint() =>
        _endpoint is { } e && State != ConnectionState.Disconnected ? e : throw new InvalidOperationException("Not connected.");

    private static PcccAddress Resolve(NodeId nodeId)
    {
        if (nodeId.NamespaceIndex != AddressNamespace || !nodeId.TryGetValue(out var text) || !PcccAddress.TryParse(text, out var address))
        {
            throw new DeviceClientException(DeviceStatusCodes.BadNodeIdUnknown, $"'{nodeId}' is not a PCCC data-table address (e.g. N7:0, F8:1, B3:0/5, T4:0.ACC).");
        }

        return address;
    }

    private static NodeId ToNodeId(PcccAddress address) => new(address.ToString(), AddressNamespace);

    private static BrowseItem ToBrowseItem(PcccAddress address)
    {
        // Not "/5": display names make up slash-separated browse paths.
        var name = address.Bit is { } bit ? "Bit " + bit.ToString(CultureInfo.InvariantCulture)
            : address.Field is { } field ? field.Name
            : address.ElementName;
        var hasChildren = !address.IsVariable || (address.Type == PcccFileType.Bit && address.Bit is null);
        return new BrowseItem(ToNodeId(address), name, address.TypeName, address.IsVariable ? NodeClass.Variable : NodeClass.Object, hasChildren);
    }

    private async Task CloseCoreAsync()
    {
        List<PollGroup> groups;
        lock (_lock)
        {
            groups = [.. _groups.Values];
            _groups.Clear();
            _fileSizes.Clear();
        }

        foreach (var group in groups)
        {
            await group.StopAsync().ConfigureAwait(false);
        }

        CloseSession();
        _files = [];
        _endpoint = null;
        _connectedAt = null;
        State = ConnectionState.Disconnected;
    }

    private void CloseSession()
    {
        _sessionTag?.Dispose();
        _sessionTag = null;
    }

    /// <summary>The tag that holds the connection's libplctag session (see <see cref="SessionCreation"/>); never read.</summary>
    private static async Task<Tag> OpenSessionAsync(PcccEndpoint endpoint, CancellationToken cancellationToken)
    {
        var tag = CreateTag(endpoint, "N7:0");
        try
        {
            await Task.Run(() =>
            {
                lock (SessionCreation)
                {
                    tag.Initialize();
                }
            }, cancellationToken).ConfigureAwait(false);
            return tag;
        }
        catch (LibPlcTagException ex)
        {
            tag.Dispose();
            throw new DeviceClientException(ToStatus(ex), $"{endpoint.Gateway}: {ex.Message}");
        }
        catch
        {
            tag.Dispose();
            throw;
        }
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

        if (!ok && State == ConnectionState.Connected)
        {
            _linkLosses++;
            _lastLinkLoss = DateTime.UtcNow;
        }

        State = ok ? ConnectionState.Connected : ConnectionState.Reconnecting;
    }

    private Task<int> GetFileSizeAsync(PcccEndpoint endpoint, PcccAddress file, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_fileSizes.TryGetValue(file.FileName, out var size) || size.IsFaulted || size.IsCanceled)
            {
                size = ProbeFileSizeAsync(endpoint, file.FileAddress, cancellationToken);
                _fileSizes[file.FileName] = size;
            }

            return size;
        }
    }

    /// <summary>
    /// PCCC can't ask a file for its length: binary-search the highest element that reads, one request per step
    /// (about ten for a PLC-5 file of 1000 elements). Only "doesn't exist" narrows the search; other errors fail it.
    /// </summary>
    private static async Task<int> ProbeFileSizeAsync(PcccEndpoint endpoint, PcccAddress file, CancellationToken cancellationToken)
    {
        var low = 1;
        var high = endpoint.MaxElements;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            try
            {
                await ReadElementAsync(endpoint, file with { Element = middle - 1 }, cancellationToken).ConfigureAwait(false);
                low = middle;
            }
            catch (DeviceClientException ex) when (ex.StatusCode == DeviceStatusCodes.BadNodeIdUnknown)
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private static async Task<object> ReadValueAsync(PcccEndpoint endpoint, PcccAddress address, CancellationToken cancellationToken) =>
        PcccCodec.Decode(address, await ReadElementAsync(endpoint, address, cancellationToken).ConfigureAwait(false));

    private static async Task<byte[]> ReadElementAsync(PcccEndpoint endpoint, PcccAddress address, CancellationToken cancellationToken)
    {
        using var tag = CreateTag(endpoint, address.ElementName);
        await ReadTagAsync(tag, cancellationToken).ConfigureAwait(false);
        return tag.GetBuffer();
    }

    /// <summary>The word a write goes to: the element, the field of a structure, or the control word of its flags.</summary>
    private static string WordTagName(PcccAddress address) => address.Field switch
    {
        { Bit: null } field => $"{address.ElementName}.{field.Name}",
        not null => $"{address.ElementName}.CON",
        null => address.ElementName,
    };

    private static string Unquote(string text)
    {
        var value = text.Trim();
        return value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"') ? value[1..^1] : value;
    }

    private static async Task ReadTagAsync(Tag tag, CancellationToken cancellationToken)
    {
        try
        {
            await InitializeTagAsync(tag, cancellationToken).ConfigureAwait(false);
            await tag.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LibPlcTagException ex)
        {
            throw new DeviceClientException(ToStatus(ex), $"{tag.Name}: {ex.Message}");
        }
    }

    /// <summary>Synchronous native create on a pool thread; see <c>CipClient.InitializeTagAsync</c> for why.</summary>
    private static Task InitializeTagAsync(Tag tag, CancellationToken cancellationToken) =>
        tag.IsInitialized ? Task.CompletedTask : Task.Run(tag.Initialize, cancellationToken);

    private static Tag CreateTag(PcccEndpoint endpoint, string name) => new()
    {
        Gateway = endpoint.Gateway,
        Path = endpoint.Path.Length > 0 ? endpoint.Path : null,
        PlcType = endpoint.Family switch
        {
            PcccPlcFamily.Plc5 => PlcType.Plc5,
            PcccPlcFamily.MicroLogix => PlcType.MicroLogix,
            _ => PlcType.Slc500,
        },
        Protocol = Protocol.ab_eip,
        Name = name,
        Timeout = endpoint.Timeout,
    };

    /// <summary>
    /// A PCCC error reply (no such file, element past the end, wrong file type) comes back as a remote error; the
    /// controller answered, so it means the address doesn't exist rather than a broken link.
    /// </summary>
    internal static DeviceStatusCode ToStatus(LibPlcTagException ex) => ex.Message switch
    {
        var m when m.Contains(nameof(Status.ErrorRemoteErr), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorNotFound), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorBadParam), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorOutOfBounds), StringComparison.Ordinal) => DeviceStatusCodes.BadNodeIdUnknown,
        var m when m.Contains(nameof(Status.ErrorNotAllowed), StringComparison.Ordinal) => DeviceStatusCodes.BadNotReadable,
        var m when m.Contains(nameof(Status.ErrorTimeout), StringComparison.Ordinal) => DeviceStatusCodes.BadTimeout,
        var m when m.Contains(nameof(Status.ErrorBadConnection), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorBadGateway), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorOpen), StringComparison.Ordinal)
            || m.Contains(nameof(Status.ErrorWinsock), StringComparison.Ordinal) => DeviceStatusCodes.BadCommunicationError,
        _ => DeviceStatusCodes.Bad,
    };

    /// <summary><c>pccc://host[:port][/path][?plc=slc500|micrologix|plc5][&amp;files=N10,F11]</c>.</summary>
    internal sealed record PcccEndpoint(string Gateway, string Path, PcccPlcFamily Family, IReadOnlyList<string> ExtraFiles, TimeSpan Timeout)
    {
        public string FamilyName => Family switch
        {
            PcccPlcFamily.Plc5 => "PLC-5",
            PcccPlcFamily.MicroLogix => "MicroLogix",
            _ => "SLC 500",
        };

        /// <summary>Elements per data file: 256 on SLC 500 and MicroLogix, 1000 on a PLC-5.</summary>
        public int MaxElements => Family == PcccPlcFamily.Plc5 ? 1000 : 256;

        public static PcccEndpoint Parse(string url)
        {
            const string Usage = "Expected pccc://host[:port][?plc=slc500|micrologix|plc5][&files=N10,F11], e.g. pccc://192.168.1.20?plc=micrologix";
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(DeviceClient.PcccScheme, StringComparison.OrdinalIgnoreCase) || uri.Host.Length == 0)
            {
                throw new FormatException($"'{url}' is not a PCCC address. {Usage}");
            }

            var gateway = uri.IsDefaultPort || uri.Port < 0 ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
            var path = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            var family = PcccPlcFamily.Slc500;
            var files = new List<string>();
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                if (key.Equals("plc", StringComparison.OrdinalIgnoreCase))
                {
                    family = value.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
                    {
                        "SLC" or "SLC500" => PcccPlcFamily.Slc500,
                        "MICROLOGIX" or "MLGX" => PcccPlcFamily.MicroLogix,
                        "PLC5" => PcccPlcFamily.Plc5,
                        _ => throw new FormatException($"Unknown plc '{value}'. {Usage}"),
                    };
                }
                else if (key.Equals("files", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var file in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!PcccAddress.TryParse(file, out var address) || !address.IsFile)
                        {
                            throw new FormatException($"'{file}' is not a data file like N10 or ST12. {Usage}");
                        }

                        files.Add(address.FileName);
                    }
                }
                else
                {
                    throw new FormatException($"Unknown option '{key}'. {Usage}");
                }
            }

            return new PcccEndpoint(gateway, path, family, files, TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>All addresses sharing one refresh time, read together on a timer.</summary>
    private sealed class PollGroup : IDisposable
    {
        private readonly List<PollEntry> _entries = [];
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _stopped;
        private long _reads;
        private long _failures;

        public PollGroup(PcccClient owner, int intervalMs)
        {
            Owner = owner;
            IntervalMs = intervalMs;
        }

        public PcccClient Owner { get; }

        public int IntervalMs { get; }

        public long Reads => Interlocked.Read(ref _reads);

        public long Failures => Interlocked.Read(ref _failures);

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

        public double? LastCycleMs { get; private set; }

        public string? LastError { get; private set; }

        public PollEntry Add(NodeId nodeId, PcccAddress address, Tag tag, Action<ValueUpdate> onUpdate)
        {
            var entry = new PollEntry(nodeId, address, tag, onUpdate);
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

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var outcomes = await Task.WhenAll(entries.Select(e => PollAsync(e, cancellationToken))).ConfigureAwait(false);
                if (outcomes.Length > 0 && !cancellationToken.IsCancellationRequested)
                {
                    LastCycleMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    LastCycleUtc = DateTime.UtcNow;
                    foreach (var (read, status) in outcomes)
                    {
                        if (!read)
                        {
                            continue;
                        }

                        Interlocked.Increment(ref _reads);
                        if (status.IsBad)
                        {
                            Interlocked.Increment(ref _failures);
                            LastError = status.ToString();
                        }
                    }

                    Owner.ReportLink(outcomes.Any(o => o.Status != DeviceStatusCodes.BadCommunicationError && o.Status != DeviceStatusCodes.BadTimeout));
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }

        /// <summary>Reads one element and reports the value when it or the status changed.</summary>
        private static async Task<(bool Read, DeviceStatusCode Status)> PollAsync(PollEntry entry, CancellationToken cancellationToken)
        {
            if (!await entry.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return (false, DeviceStatusCodes.Good);
            }

            try
            {
                DeviceStatusCode status;
                object? value = null;
                try
                {
                    await InitializeTagAsync(entry.Tag, cancellationToken).ConfigureAwait(false);
                    await entry.Tag.ReadAsync(cancellationToken).ConfigureAwait(false);
                    value = PcccCodec.Decode(entry.Address, entry.Tag.GetBuffer());
                    status = DeviceStatusCodes.Good;
                }
                catch (LibPlcTagException ex)
                {
                    status = ToStatus(ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException && Errors.IsRecoverable(ex))
                {
                    System.Diagnostics.Trace.TraceError($"Polling {entry.Address} failed: {ex}");
                    status = DeviceStatusCodes.BadUnexpectedError;
                }

                // Compared by value, not bytes: a bit shouldn't report when another bit of its word changes.
                if (status == entry.LastStatus && Equals(value, entry.LastValue))
                {
                    return (true, status);
                }

                entry.LastStatus = status;
                entry.LastValue = value;
                var now = DateTime.UtcNow;
                entry.OnUpdate(new ValueUpdate(
                    entry.NodeId,
                    status.IsGood ? ValueFormatter.Format(value) : status.ToString(),
                    status,
                    now,
                    now,
                    ValueFormatter.ToNumeric(value),
                    value));
                return (true, status);
            }
            catch (ObjectDisposedException)
            {
                return (false, DeviceStatusCodes.Good);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    private sealed class PollEntry(NodeId nodeId, PcccAddress address, Tag tag, Action<ValueUpdate> onUpdate)
    {
        public NodeId NodeId { get; } = nodeId;

        public PcccAddress Address { get; } = address;

        public Tag Tag { get; } = tag;

        public Action<ValueUpdate> OnUpdate { get; } = onUpdate;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DeviceStatusCode? LastStatus { get; set; }

        public object? LastValue { get; set; }
    }

    private sealed class PcccMonitorHandle(PollGroup group, PollEntry entry) : IAsyncDisposable
    {
        public PollGroup Group => group;

        public PollEntry Entry => entry;

        public async ValueTask DisposeAsync() => await group.RemoveAsync(entry).ConfigureAwait(false);
    }
}
