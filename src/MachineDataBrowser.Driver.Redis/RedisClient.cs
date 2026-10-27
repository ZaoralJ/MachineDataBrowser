using System.Globalization;
using System.Net.Sockets;
using System.Text;
using MachineDataBrowser.Core;
using StackExchange.Redis;
using NodeClass = MachineDataBrowser.Core.DeviceNodeKind;
using NodeId = MachineDataBrowser.Core.DeviceNodeId;

namespace MachineDataBrowser.Driver.Redis;

/// <summary>Redis-protocol client for Redis and Valkey servers.</summary>
public sealed class RedisClient : IDeviceClient, IConnectionDiagnosticsSource
{
    private const int DefaultPort = 6379;
    private const int MaxBrowseChildren = 500;
    private const int MaxScannedKeys = 10_000;
    private const int ScanPageSize = 250;
    private const int MaxStringBytes = 64 * 1024;
    private const int MaxHashFieldBytes = 64 * 1024;
    private const ushort Namespace = 1;
    private const string WriteStringScript = "local kind=redis.call('TYPE',KEYS[1]).ok; if kind~='string' then return 0 end; redis.call('SET',KEYS[1],ARGV[1],'KEEPTTL'); return 1";
    private const string WriteHashFieldScript = "local kind=redis.call('TYPE',KEYS[1]).ok; if kind~='hash' then return 0 end; if redis.call('HEXISTS',KEYS[1],ARGV[1])==0 then return -1 end; redis.call('HSET',KEYS[1],ARGV[1],ARGV[2]); return 1";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _monitorLock = new();
    private readonly HashSet<MonitorHandle> _monitors = [];
    private ConnectionMultiplexer? _connection;
    private IDatabase? _database;
    private IServer? _server;
    private string? _host;
    private int _port;
    private int _databaseIndex;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DateTime? _connectedAt;
    private long _requests;

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

    public string? ServerUri => _host is { } host ? $"Redis / Valkey {host}:{_port} (database {_databaseIndex})" : null;

    public BrowseItem Root => new(RootId(_databaseIndex), $"Database {_databaseIndex}", $"db:{_databaseIndex}", NodeClass.Object);

    public static Uri ParseEndpoint(string endpointUrl)
    {
        ArgumentNullException.ThrowIfNull(endpointUrl);
        if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || uri.Scheme is not ("redis" or "rediss"))
        {
            throw new FormatException($"'{EndpointUrlTools.WithoutPassword(endpointUrl)}' is not a Redis address. Expected redis://host[:6379][/database] or rediss://host[:6379][/database].");
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new FormatException("Redis endpoint URLs do not support query strings or fragments.");
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.Length > 0 && (!int.TryParse(path, NumberStyles.None, CultureInfo.InvariantCulture, out var database) || database < 0))
        {
            throw new FormatException("The Redis URL path must be a non-negative logical database number, for example /0.");
        }

        return uri;
    }

    public string ToPortableId(NodeId nodeId) => nodeId.IdentifierAsString;

    public string ToDisplayId(NodeId nodeId) => ToPortableId(nodeId);

    public NodeId ParsePortableId(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var id = text.Trim();
        if (id == RootId(_databaseIndex).IdentifierAsString || TryParseKeyId(id, out _) || TryParsePrefixId(id, out _)
            || TryParseFieldId(id, out _, out _) || TryParseLimitId(id))
        {
            return new NodeId(id, Namespace);
        }

        throw new FormatException($"'{text}' is not a Redis node id.");
    }

    public async Task ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            State = ConnectionState.Connecting;

            Uri uri;
            try
            {
                uri = ParseEndpoint(options.EndpointUrl);
            }
            catch (FormatException ex)
            {
                throw new DeviceClientException(DeviceStatusCodes.BadTcpEndpointUrlInvalid, ex.Message, ex);
            }

            var path = uri.AbsolutePath.Trim('/');
            _databaseIndex = path.Length == 0 ? 0 : int.Parse(path, NumberStyles.None, CultureInfo.InvariantCulture);
            _host = uri.Host;
            _port = uri.IsDefaultPort ? DefaultPort : uri.Port;

            var (urlUser, urlPassword) = ParseCredentials(uri.UserInfo);
            var userFromOptions = !string.IsNullOrWhiteSpace(options.UserName);
            var configuration = new ConfigurationOptions
            {
                AbortOnConnectFail = true,
                ConnectRetry = 1,
                ConnectTimeout = 5_000,
                AsyncTimeout = 5_000,
                SyncTimeout = 5_000,
                Ssl = uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase),
                SslHost = uri.Host,
                User = userFromOptions ? options.UserName : urlUser,
                Password = userFromOptions || !string.IsNullOrEmpty(options.Password) ? options.Password : urlPassword,
                ClientName = "MachineDataBrowser",
            };
            configuration.EndPoints.Add(_host, _port);

            try
            {
                _connection = await ConnectionMultiplexer.ConnectAsync(configuration).WaitAsync(cancellationToken).ConfigureAwait(false);
                _connection.ConnectionFailed += OnConnectionFailed;
                _connection.ConnectionRestored += OnConnectionRestored;
                _database = _connection.GetDatabase(_databaseIndex);
                _server = _connection.GetServer(_host, _port);
                _connectedAt = DateTime.UtcNow;
                Interlocked.Exchange(ref _requests, 0);
                State = ConnectionState.Connected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                await CloseCoreAsync().ConfigureAwait(false);
                throw ToDeviceException(ex);
            }
        }
        catch
        {
            if (_connection is null)
            {
                State = ConnectionState.Disconnected;
            }

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
        RequireConnected();
        if (nodeId == RootId(_databaseIndex))
        {
            return await BrowsePrefixAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        if (TryParsePrefixId(nodeId.IdentifierAsString, out var prefix))
        {
            return await BrowsePrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
        }

        if (TryParseKeyId(nodeId.IdentifierAsString, out var key))
        {
            return await BrowseHashAsync(key, cancellationToken).ConfigureAwait(false);
        }

        if (TryParseFieldId(nodeId.IdentifierAsString, out _, out _) || TryParseLimitId(nodeId.IdentifierAsString))
        {
            return [];
        }

        throw UnknownNode(nodeId);
    }

    public async Task<IReadOnlyList<NodeId>> GetPathFromRootAsync(NodeId nodeId, int maxDepth = 32, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = new List<NodeId> { RootId(_databaseIndex) };
        if (nodeId == RootId(_databaseIndex))
        {
            return path;
        }

        if (TryParseFieldId(nodeId.IdentifierAsString, out var hashKey, out _))
        {
            AddKeyPath(path, hashKey);
            path.Add(nodeId);
        }
        else if (TryParseKeyId(nodeId.IdentifierAsString, out var key))
        {
            AddKeyPath(path, key);
            path.Add(nodeId);
        }
        else if (TryParsePrefixId(nodeId.IdentifierAsString, out var prefix))
        {
            AddPrefixPath(path, prefix);
        }
        else if (!TryParseLimitId(nodeId.IdentifierAsString))
        {
            return [];
        }

        return path.Count <= maxDepth ? path : [];
    }

    public async Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        var result = new List<AttributeValue> { new("NodeId", ToPortableId(nodeId)) };
        if (nodeId == RootId(_databaseIndex))
        {
            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            result.Add(new("DisplayName", Root.DisplayName));
            result.Add(new("Database", _databaseIndex.ToString(CultureInfo.InvariantCulture)));
            result.Add(new("AccessLevel", "Read"));
            return result;
        }

        if (TryParsePrefixId(nodeId.IdentifierAsString, out var prefix))
        {
            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            result.Add(new("Key prefix", prefix));
            result.Add(new("AccessLevel", "Read"));
            return result;
        }

        if (TryParseKeyId(nodeId.IdentifierAsString, out var key))
        {
            var type = await KeyTypeAsync(key, cancellationToken).ConfigureAwait(false);
            if (type == RedisType.None)
            {
                throw UnknownNode(nodeId);
            }

            result.Add(new("NodeClass", type == RedisType.String ? nameof(NodeClass.Variable) : nameof(NodeClass.Object)));
            result.Add(new("Key", key));
            result.Add(new("Type", type.ToString()));
            result.Add(new("TTL", FormatTtl(await Database.KeyTimeToLiveAsync(key).ConfigureAwait(false))));
            result.Add(new("AccessLevel", type == RedisType.String ? "Read, Write" : "Read"));
            if (type == RedisType.String)
            {
                var length = await Database.StringLengthAsync(key).ConfigureAwait(false);
                result.Add(new("Length", length.ToString(CultureInfo.InvariantCulture)));
                result.Add(new("Value", ValueFormatter.Format(await ReadStringAsync(key).ConfigureAwait(false))));
            }
            else if (type == RedisType.Hash)
            {
                result.Add(new("Fields", (await Database.HashLengthAsync(key).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                result.Add(new("Read support", $"Redis {type} values are not yet browsable."));
            }

            return result;
        }

        if (TryParseFieldId(nodeId.IdentifierAsString, out var fieldKey, out var field))
        {
            var value = await Database.HashGetAsync(fieldKey, field).ConfigureAwait(false);
            if (value.IsNull)
            {
                throw UnknownNode(nodeId);
            }

            result.Add(new("NodeClass", nameof(NodeClass.Variable)));
            result.Add(new("Key", fieldKey));
            result.Add(new("Hash field", field));
            result.Add(new("Type", "String"));
            result.Add(new("AccessLevel", "Read, Write"));
            result.Add(new("Value", Clip(value.ToString(), MaxHashFieldBytes)));
            return result;
        }

        throw UnknownNode(nodeId);
    }

    public async Task<IReadOnlyList<object?>> ReadValuesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var values = new object?[nodeIds.Count];
        for (var index = 0; index < nodeIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                values[index] = await ReadOneAsync(nodeIds[index], cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceClientException)
            {
                values[index] = null;
            }
        }

        return values;
    }

    public async Task<IReadOnlyList<ValueUpdate>> ReadValueUpdatesAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var updates = new List<ValueUpdate>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            try
            {
                var value = await ReadOneAsync(nodeId, cancellationToken).ConfigureAwait(false);
                updates.Add(new ValueUpdate(nodeId, ValueFormatter.Format(value), DeviceStatusCodes.Good, now, now,
                    ValueFormatter.ToNumeric(value), value));
            }
            catch (DeviceClientException ex)
            {
                updates.Add(new ValueUpdate(nodeId, ex.StatusCode.ToString(), ex.StatusCode, now, now));
            }
        }

        return updates;
    }

    public Task WriteValueAsync(NodeId nodeId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteCoreAsync(nodeId, text, cancellationToken);
    }

    private async Task WriteCoreAsync(NodeId nodeId, string text, CancellationToken cancellationToken)
    {
        RequireConnected();
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(text) > MaxStringBytes)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadOutOfRange, $"The value exceeds the {MaxStringBytes.ToString("N0", CultureInfo.InvariantCulture)}-byte Redis write limit.");
        }

        try
        {
            RedisResult result;
            if (TryParseKeyId(nodeId.IdentifierAsString, out var key))
            {
                result = await Database.ScriptEvaluateAsync(WriteStringScript, [key], [text]).ConfigureAwait(false);
            }
            else if (TryParseFieldId(nodeId.IdentifierAsString, out var hashKey, out var field))
            {
                result = await Database.ScriptEvaluateAsync(WriteHashFieldScript, [hashKey], [field, text]).ConfigureAwait(false);
            }
            else
            {
                throw new DeviceClientException(DeviceStatusCodes.BadNotWritable, "Only existing Redis string keys and hash fields can be written.");
            }

            Interlocked.Increment(ref _requests);
            var status = (long)result;
            if (status == 0)
            {
                throw new DeviceClientException(DeviceStatusCodes.BadNotWritable, "The Redis key no longer has the selected type; no value was changed.");
            }

            if (status < 0)
            {
                throw new DeviceClientException(DeviceStatusCodes.BadNodeIdUnknown, "The Redis hash field no longer exists; no value was changed.");
            }
        }
        catch (DeviceClientException)
        {
            throw;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            throw ToDeviceException(ex);
        }
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
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var initial = (await ReadValueUpdatesAsync([nodeId], cancellationToken).ConfigureAwait(false))[0];
                if (!initial.Status.IsGood)
                {
                    results.Add(new MonitorResult(nodeId, null, new DeviceError(initial.Status, initial.Value)));
                    continue;
                }

                onUpdate(initial);
                var handle = new MonitorHandle(this, nodeId, onUpdate, initial, Math.Clamp((int)samplingIntervalMs, 100, 60_000));
                lock (_monitorLock)
                {
                    _monitors.Add(handle);
                }

                handle.Start();
                results.Add(new MonitorResult(nodeId, handle, DeviceError.Good));
            }
            catch (DeviceClientException ex)
            {
                results.Add(new MonitorResult(nodeId, null, ex.Error));
            }
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
        var owned = monitors.OfType<MonitorHandle>().Where(h => ReferenceEquals(h.Owner, this)).ToArray();
        var results = await MonitorManyAsync([.. owned.Select(h => h.NodeId)], onUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
        foreach (var handle in owned)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    public Task<ConnectionDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        int monitorCount;
        lock (_monitorLock)
        {
            monitorCount = _monitors.Count;
        }

        var info = new List<(string, string)>
        {
            ("Protocol", "Redis protocol (Redis / Valkey)"),
            ("Server", $"{_host}:{_port}"),
            ("Database", _databaseIndex.ToString(CultureInfo.InvariantCulture)),
            ("Connected since", DiagnosticsFormat.Since(_connectedAt)),
            ("Requests", Interlocked.Read(ref _requests).ToString("N0", CultureInfo.InvariantCulture)),
            ("Monitored values", monitorCount.ToString(CultureInfo.InvariantCulture)),
            ("Discovery", "Incremental SCAN; keyspace changes appear when a folder is browsed again"),
        };
        return Task.FromResult(new ConnectionDiagnostics(info, []));
    }

    private static NodeId RootId(int databaseIndex) => new($"db/{databaseIndex.ToString(CultureInfo.InvariantCulture)}", Namespace);

    private IDatabase Database => _database ?? throw new InvalidOperationException("Not connected.");

    private void RequireConnected()
    {
        if (_database is null || _server is null || State == ConnectionState.Disconnected)
        {
            throw new InvalidOperationException("Not connected.");
        }
    }

    private async Task<IReadOnlyList<BrowseItem>> BrowsePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        var directKeys = new SortedSet<string>(StringComparer.Ordinal);
        var childPrefixes = new SortedSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        var truncated = false;
        var pattern = EscapeGlob(prefix) + "*";
        try
        {
            await foreach (var redisKey in _server!.KeysAsync(_databaseIndex, pattern, ScanPageSize).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var rawKey = redisKey.ToString();
                if (!rawKey.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (++scanned > MaxScannedKeys)
                {
                    truncated = true;
                    break;
                }

                var remainder = rawKey[prefix.Length..];
                if (remainder.Length == 0)
                {
                    continue;
                }

                var separator = remainder.IndexOf(':');
                if (separator < 0)
                {
                    directKeys.Add(rawKey);
                }
                else
                {
                    childPrefixes.Add(prefix + remainder[..(separator + 1)]);
                }
            }

            Interlocked.Increment(ref _requests);
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            throw ToDeviceException(ex);
        }

        var items = new List<BrowseItem>();
        var namesInBoth = directKeys.Select(LastSegment).Intersect(childPrefixes.Select(LastSegment), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var childPrefix in childPrefixes)
        {
            var name = LastSegment(childPrefix.TrimEnd(':'));
            items.Add(new BrowseItem(PrefixId(childPrefix), name, childPrefix, NodeClass.Object, HasChildren: true));
        }

        foreach (var key in directKeys.Take(MaxBrowseChildren - items.Count))
        {
            var type = await KeyTypeAsync(key, cancellationToken).ConfigureAwait(false);
            var name = LastSegment(key);
            if (namesInBoth.Contains(name))
            {
                name += " (key)";
            }

            items.Add(new BrowseItem(KeyId(key), name, key, type == RedisType.String ? NodeClass.Variable : NodeClass.Object,
                HasChildren: type == RedisType.Hash));
        }

        if (items.Count >= MaxBrowseChildren || truncated || directKeys.Count + childPrefixes.Count > MaxBrowseChildren)
        {
            items.Add(LimitItem($"{prefix}|keys"));
        }

        return items;
    }

    private async Task<IReadOnlyList<BrowseItem>> BrowseHashAsync(string key, CancellationToken cancellationToken)
    {
        if (await KeyTypeAsync(key, cancellationToken).ConfigureAwait(false) != RedisType.Hash)
        {
            return [];
        }

        var result = new List<BrowseItem>();
        var truncated = false;
        await foreach (var entry in Database.HashScanAsync(key, pageSize: ScanPageSize).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (result.Count == MaxBrowseChildren)
            {
                truncated = true;
                break;
            }

            var field = entry.Name.ToString();
            result.Add(new BrowseItem(FieldId(key, field), field, field, NodeClass.Variable, HasChildren: false));
        }

        Interlocked.Increment(ref _requests);
        if (truncated)
        {
            result.Add(LimitItem($"{key}|fields"));
        }

        return result;
    }

    private async Task<object?> ReadOneAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        RequireConnected();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (TryParseKeyId(nodeId.IdentifierAsString, out var key))
            {
                if (await KeyTypeAsync(key, cancellationToken).ConfigureAwait(false) != RedisType.String)
                {
                    throw new DeviceClientException(DeviceStatusCodes.BadNotReadable, $"'{key}' is not a Redis string key.");
                }

                return await ReadStringAsync(key).ConfigureAwait(false);
            }

            if (TryParseFieldId(nodeId.IdentifierAsString, out var hashKey, out var field))
            {
                if (await KeyTypeAsync(hashKey, cancellationToken).ConfigureAwait(false) != RedisType.Hash)
                {
                    throw UnknownNode(nodeId);
                }

                var value = await Database.HashGetAsync(hashKey, field).ConfigureAwait(false);
                if (value.IsNull)
                {
                    throw UnknownNode(nodeId);
                }

                return Clip(value.ToString(), MaxHashFieldBytes);
            }

            throw new DeviceClientException(DeviceStatusCodes.BadNotReadable, "Only Redis strings and individual hash fields can be read as values.");
        }
        catch (DeviceClientException)
        {
            throw;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            throw ToDeviceException(ex);
        }
    }

    private async Task<string?> ReadStringAsync(string key)
    {
        var length = await Database.StringLengthAsync(key).ConfigureAwait(false);
        Interlocked.Increment(ref _requests);
        if (length > MaxStringBytes)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadOutOfRange,
                $"Redis string '{key}' is {length.ToString(CultureInfo.InvariantCulture)} bytes; the read limit is {MaxStringBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes.");
        }

        var value = await Database.StringGetAsync(key).ConfigureAwait(false);
        Interlocked.Increment(ref _requests);
        return value.IsNull ? null : value.ToString();
    }

    private async Task<RedisType> KeyTypeAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var type = await Database.KeyTypeAsync(key).ConfigureAwait(false);
            Interlocked.Increment(ref _requests);
            return type;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            throw ToDeviceException(ex);
        }
    }

    private async Task CloseCoreAsync()
    {
        MonitorHandle[] monitors;
        lock (_monitorLock)
        {
            monitors = [.. _monitors];
            _monitors.Clear();
        }

        foreach (var monitor in monitors)
        {
            await monitor.StopAsync().ConfigureAwait(false);
        }

        if (_connection is { } connection)
        {
            connection.ConnectionFailed -= OnConnectionFailed;
            connection.ConnectionRestored -= OnConnectionRestored;
            await connection.CloseAsync().ConfigureAwait(false);
            connection.Dispose();
        }

        _connection = null;
        _database = null;
        _server = null;
        _host = null;
        _connectedAt = null;
        State = ConnectionState.Disconnected;
    }

    private void RemoveMonitor(MonitorHandle handle)
    {
        lock (_monitorLock)
        {
            _monitors.Remove(handle);
        }
    }

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs args) => State = ConnectionState.Reconnecting;

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs args) => State = ConnectionState.Connected;

    private static bool IsConnectionError(Exception ex) => ex is RedisException or SocketException or TimeoutException;

    private static DeviceClientException ToDeviceException(Exception ex) => ex switch
    {
        RedisTimeoutException or TimeoutException => new DeviceClientException(DeviceStatusCodes.BadTimeout, $"Redis request timed out: {ex.Message}", ex),
        _ => new DeviceClientException(DeviceStatusCodes.BadCommunicationError, $"Redis connection failed: {ex.Message}", ex),
    };

    private static (string? User, string? Password) ParseCredentials(string userInfo)
    {
        if (userInfo.Length == 0)
        {
            return (null, null);
        }

        var separator = userInfo.IndexOf(':');
        return separator < 0
            ? (Uri.UnescapeDataString(userInfo), null)
            : (Uri.UnescapeDataString(userInfo[..separator]), Uri.UnescapeDataString(userInfo[(separator + 1)..]));
    }

    private static string EscapeGlob(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '*' or '?' or '[' or ']' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string LastSegment(string value)
    {
        var trimmed = value.TrimEnd(':');
        var separator = trimmed.LastIndexOf(':');
        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }

    private static string FormatTtl(TimeSpan? ttl) => ttl is null ? "No expiry" : ttl.Value.ToString("c", CultureInfo.InvariantCulture);

    private static string Clip(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var clipped = value;
        while (clipped.Length > 0 && Encoding.UTF8.GetByteCount(clipped) > maxBytes)
        {
            clipped = clipped[..^1];
        }

        return clipped + "… (value truncated at 64 KiB)";
    }

    private static BrowseItem LimitItem(string identity) =>
        new(new NodeId($"limit/{Encode(identity)}", Namespace), "Result limit reached; narrow this prefix", "limit", NodeClass.Object, HasChildren: false);

    private static NodeId KeyId(string key) => new($"key/{Encode(key)}", Namespace);

    private static NodeId PrefixId(string prefix) => new($"prefix/{Encode(prefix)}", Namespace);

    private static NodeId FieldId(string key, string field) => new($"field/{Encode(key)}/{Encode(field)}", Namespace);

    private static bool TryParseKeyId(string id, out string key) => TryDecodeOne(id, "key/", out key);

    private static bool TryParsePrefixId(string id, out string prefix) => TryDecodeOne(id, "prefix/", out prefix);

    private static bool TryParseLimitId(string id) => id.StartsWith("limit/", StringComparison.Ordinal) && TryDecodeOne(id, "limit/", out _);

    private static bool TryParseFieldId(string id, out string key, out string field)
    {
        key = string.Empty;
        field = string.Empty;
        if (!id.StartsWith("field/", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = id[6..].Split('/');
        return parts.Length == 2 && TryDecode(parts[0], out key) && TryDecode(parts[1], out field);
    }

    private static bool TryDecodeOne(string id, string prefix, out string value)
    {
        value = string.Empty;
        return id.StartsWith(prefix, StringComparison.Ordinal) && TryDecode(id[prefix.Length..], out value);
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static DeviceClientException UnknownNode(NodeId nodeId) =>
        new(DeviceStatusCodes.BadNodeIdUnknown, $"Redis node '{nodeId.IdentifierAsString}' does not exist.");

    private static void AddKeyPath(List<NodeId> path, string key)
    {
        var segments = key.Split(':');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            path.Add(PrefixId(string.Join(':', segments.Take(index + 1)) + ":"));
        }
    }

    private static void AddPrefixPath(List<NodeId> path, string prefix)
    {
        var segments = prefix.TrimEnd(':').Split(':');
        for (var index = 0; index < segments.Length; index++)
        {
            path.Add(PrefixId(string.Join(':', segments.Take(index + 1)) + ":"));
        }
    }

    private sealed class MonitorHandle(RedisClient owner, NodeId nodeId, Action<ValueUpdate> onUpdate, ValueUpdate initial, int intervalMs) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private object? _lastRaw = initial.Raw;
        private string _lastValue = initial.Value;
        private DeviceStatusCode _lastStatus = initial.Status;
        private Task? _loop;

        public RedisClient Owner { get; } = owner;

        public NodeId NodeId { get; } = nodeId;

        public void Start() => _loop = Task.Run(() => RunAsync(_cancellation.Token), CancellationToken.None);

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

        public async Task StopAsync()
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
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

            Owner.RemoveMonitor(this);
            _cancellation.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = DateTime.UtcNow;
                try
                {
                    var update = (await Owner.ReadValueUpdatesAsync([NodeId], cancellationToken).ConfigureAwait(false))[0];
                    if (update.Status != _lastStatus || update.Value != _lastValue || !Equals(update.Raw, _lastRaw))
                    {
                        _lastStatus = update.Status;
                        _lastValue = update.Value;
                        _lastRaw = update.Raw;
                        onUpdate(update);
                    }

                    if (update.Status.IsGood)
                    {
                        Owner.State = ConnectionState.Connected;
                    }
                }
                catch (DeviceClientException ex)
                {
                    Owner.State = ConnectionState.Reconnecting;
                    var update = new ValueUpdate(NodeId, ex.StatusCode.ToString(), ex.StatusCode, now, now);
                    if (update.Status != _lastStatus || update.Value != _lastValue)
                    {
                        _lastStatus = update.Status;
                        _lastValue = update.Value;
                        _lastRaw = null;
                        onUpdate(update);
                    }
                }
            }
        }
    }
}
