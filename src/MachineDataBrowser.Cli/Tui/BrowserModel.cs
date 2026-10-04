using System.Collections.Concurrent;
using MachineDataBrowser.Core;
using Opc.Ua;

namespace MachineDataBrowser.Cli.Tui;

/// <summary>A node in the address-space tree; children are browsed on first expand and cached.</summary>
internal sealed class TreeEntry(BrowseItem item, TreeEntry? parent)
{
    public BrowseItem Item { get; } = item;

    public TreeEntry? Parent { get; } = parent;

    public IReadOnlyList<TreeEntry>? Children { get; set; }

    /// <summary>A browse is under way; the tree shows "loading…" meanwhile.</summary>
    public bool Loading { get; set; }

    /// <summary>Display names from the root, without the root itself: <c>/Objects/Line1</c>.</summary>
    public string Path => Parent is null ? string.Empty : $"{Parent.Path}/{Item.DisplayName}";

    /// <summary>The parent path for session files (no leading '/').</summary>
    public string ParentPath => Parent?.Path.TrimStart('/') ?? string.Empty;

    public bool IsVariable => Item.NodeClass == NodeClass.Variable;

    public override string ToString() => Item.DisplayName;
}

/// <summary>One monitored item: its latest value, how often it changed and a short numeric history for the chart.</summary>
internal sealed class WatchRow(Node node, int refreshMs)
{
    /// <summary>Points kept for the trend chart; a wide terminal shows two per column.</summary>
    public const int MaxHistory = 600;

    private readonly Lock _lock = new();
    private readonly Queue<double> _history = new();
    private ValueUpdate? _last;
    private DateTime _receivedAt;
    private int _notifications;

    public Node Node { get; } = node;

    public int RefreshMs { get; set; } = refreshMs;

    public IAsyncDisposable? Monitor { get; set; }

    public void Apply(ValueUpdate update)
    {
        lock (_lock)
        {
            _last = update;
            _receivedAt = DateTime.Now;
            _notifications++;
            if (update.Numeric is { } number && double.IsFinite(number) && StatusCode.IsGood(update.Status))
            {
                _history.Enqueue(number);
                if (_history.Count > MaxHistory)
                {
                    _history.Dequeue();
                }
            }
        }
    }

    public IReadOnlyList<double> History
    {
        get
        {
            lock (_lock)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>
    /// Value, status, time and update count. Right after monitoring starts every protocol sends the current value once;
    /// that is not an update. The time is the source timestamp, else the server's, else when it arrived. Stale: nothing
    /// arrived for 5 refresh times (at least 5 s), like the app's Watch.
    /// </summary>
    public RowSnapshot Snapshot()
    {
        lock (_lock)
        {
            if (_last is not { } last)
            {
                return new RowSnapshot("…", "waiting", StatusCodes.Good, string.Empty, 0, null, Stale: false, Waiting: true);
            }

            var time = Output.Time(last) is { } stamp ? stamp.ToLocalTime() : _receivedAt;
            var stale = DateTime.Now - _receivedAt > TimeSpan.FromMilliseconds(Math.Max(5000, RefreshMs * 5)) && _notifications > 1;
            return new RowSnapshot(last.Value, StatusText.Of(last.Status), last.Status,
                time.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), Math.Max(0, _notifications - 1), last.Numeric, stale, Waiting: false);
        }
    }
}

internal sealed record RowSnapshot(string Value, string Status, StatusCode Code, string Time, int Updates, double? Numeric, bool Stale, bool Waiting);

internal sealed record WriteOutcome(string Name, string Before, string After, string? Error);

/// <summary>
/// What the full-screen browser does, without any UI: browse with a cached tree, attributes, monitored items, writes
/// with read-back, recording to a file, events and alarms, history, search and session files. Device callbacks arrive on
/// client threads; the UI reads snapshots on its own timer.
/// </summary>
internal sealed class BrowserModel : IAsyncDisposable
{
    private const int MaxLog = 500;
    private const int MaxEvents = 500;
    private const int MaxFolderItems = 500;
    private const int MaxNamesInLog = 5;

    private readonly Lock _watchLock = new();
    private readonly List<WatchRow> _watch = [];
    private readonly ConcurrentDictionary<NodeId, WatchRow> _byId = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly Lock _eventLock = new();
    private readonly List<EventNotification> _events = [];
    private readonly Dictionary<NodeId, EventNotification> _alarms = [];
    private IAsyncDisposable? _eventSubscription;
    private Recording? _recording;
    private string? _recordingPath;

    private readonly bool _ownsClient;

    /// <summary><paramref name="ownsClient"/>: disposing the model also closes the connection (a session the TUI opened).</summary>
    public BrowserModel(IDeviceClient client, ConnectionArgs args, int defaultRefreshMs, string? sessionPath = null, bool ownsClient = false)
    {
        _ownsClient = ownsClient;
        Client = client;
        Args = args;
        DefaultRefreshMs = defaultRefreshMs;
        SessionPath = sessionPath;
        Root = new TreeEntry(client.Root, null);
        client.StateChanged += (_, state) => Log($"Connection {state}.");
        if (client is IPausableDiscovery discovery)
        {
            discovery.DiscoveryPausedChanged += (_, _) => Log(discovery.IsDiscoveryPaused
                ? "Discovery paused: only monitored topics are received, new topics don't appear (d resumes)."
                : "Discovery resumed: receiving the whole topic filter again.");
        }
    }

    public IDeviceClient Client { get; }

    public ConnectionArgs Args { get; }

    /// <summary>Refresh time of new monitored items; <see cref="ChangeRefreshAsync"/> changes it for all.</summary>
    public int DefaultRefreshMs { get; private set; }

    /// <summary>The session file this browser was opened from or last saved to.</summary>
    public string? SessionPath { get; private set; }

    public TreeEntry Root { get; }

    public bool SupportsEvents => Client is IEventSource;

    public bool SupportsHistory => Client is IHistorySource;

    /// <summary>MQTT: discovery (subscribing to the whole topic filter) can be paused on busy brokers, like in the app.</summary>
    public bool SupportsDiscoveryPause => Client is IPausableDiscovery;

    public bool IsDiscoveryPaused => Client is IPausableDiscovery { IsDiscoveryPaused: true };

    public Task ToggleDiscoveryAsync(CancellationToken cancellationToken = default) =>
        Client is IPausableDiscovery discovery
            ? discovery.SetDiscoveryPausedAsync(!discovery.IsDiscoveryPaused, cancellationToken)
            : Task.CompletedTask;

    /// <summary>Raised from any thread when something the UI shows changed outside of a snapshot (log, tree).</summary>
    public event Action? Changed;

    public IReadOnlyList<string> LogLines => [.. _log];

    public void Log(string message)
    {
        _log.Enqueue($"{DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}  {message}");
        while (_log.Count > MaxLog && _log.TryDequeue(out _))
        {
        }

        Changed?.Invoke();
    }

    public void ClearLog()
    {
        _log.Clear();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ tree

    public async Task<IReadOnlyList<TreeEntry>> LoadChildrenAsync(TreeEntry entry, bool reload = false, CancellationToken cancellationToken = default)
    {
        if (entry.Children is { } cached && !reload)
        {
            return cached;
        }

        entry.Loading = true;
        try
        {
            var items = await Client.BrowseAsync(entry.Item.NodeId, cancellationToken).ConfigureAwait(false);
            // Keep entries that are still there, so their own children and expansion survive a reload.
            var previous = entry.Children?.ToDictionary(c => c.Item.NodeId) ?? [];
            entry.Children = [.. items.Select(i => previous.TryGetValue(i.NodeId, out var old) && old.Item == i ? old : new TreeEntry(i, entry))];
            return entry.Children;
        }
        finally
        {
            entry.Loading = false;
        }
    }

    /// <summary>Loads the tree along a path of ids from the root (search results) and returns the last entry.</summary>
    public async Task<TreeEntry?> RevealAsync(IReadOnlyList<NodeId> path, CancellationToken cancellationToken = default)
    {
        var entry = Root;
        foreach (var id in path.SkipWhile(id => id == Root.Item.NodeId))
        {
            var children = await LoadChildrenAsync(entry, cancellationToken: cancellationToken).ConfigureAwait(false);
            entry = children.FirstOrDefault(c => c.Item.NodeId == id);
            if (entry is null)
            {
                return null;
            }
        }

        return entry;
    }

    public Task<IReadOnlyList<AttributeValue>> ReadAttributesAsync(TreeEntry entry, CancellationToken cancellationToken = default) =>
        Client.ReadAttributesAsync(entry.Item.NodeId, cancellationToken);

    public Task<SearchResult> SearchAsync(string text, TreeEntry? under = null, CancellationToken cancellationToken = default) =>
        Client.SearchAsync((under ?? Root).Item, text, maxResults: 200, cancellationToken: cancellationToken);

    // ------------------------------------------------------------------ monitored items

    public IReadOnlyList<WatchRow> Watch
    {
        get
        {
            lock (_watchLock)
            {
                return [.. _watch];
            }
        }
    }

    /// <summary>Monitors a variable, or every variable below a folder or structure (like Monitor folder in the app).</summary>
    public async Task<int> MonitorAsync(TreeEntry entry, CancellationToken cancellationToken = default)
    {
        List<Node> nodes;
        var descendIntoVariables = Client is IDynamicAddressSpace;
        if (entry.IsVariable && !(descendIntoVariables && entry.Item.HasChildren))
        {
            nodes = [ToNode(entry)];
        }
        else
        {
            var found = await Client.CollectVariablesWithPathsAsync(entry.Item.NodeId, 10, MaxFolderItems, descendIntoVariables, cancellationToken).ConfigureAwait(false);
            if (found.Count == 0 && entry.IsVariable)
            {
                nodes = [ToNode(entry)];
            }
            else
            {
                var basePath = entry.Path.TrimStart('/');
                nodes = [.. found.Select(f => new Node(
                    f.Item.NodeId,
                    f.Path.Length == 0 ? f.Item.DisplayName : $"{f.Path}/{f.Item.DisplayName}",
                    Client.ToDisplayId(f.Item.NodeId),
                    f.Item.DisplayName,
                    f.Path.Length == 0 ? basePath : $"{basePath}/{f.Path}"))];
                if (found.Count >= MaxFolderItems)
                {
                    Log($"Monitoring limited to the first {MaxFolderItems} variables below {entry.Item.DisplayName}.");
                }
            }
        }

        if (nodes.Count == 0)
        {
            Log($"No variables below {entry.Item.DisplayName}.");
            return 0;
        }

        return await MonitorNodesAsync([.. nodes.Select(n => (n, DefaultRefreshMs))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Monitors nodes not monitored yet; returns how many were added.</summary>
    public async Task<int> MonitorNodesAsync(IReadOnlyList<(Node Node, int RefreshMs)> nodes, CancellationToken cancellationToken = default)
    {
        List<WatchRow> rows;
        lock (_watchLock)
        {
            var existing = _watch.Select(w => w.Node.Id).ToHashSet();
            rows = [.. nodes.Where(n => existing.Add(n.Node.Id)).Select(n => new WatchRow(n.Node, n.RefreshMs))];
            _watch.AddRange(rows);
        }

        foreach (var row in rows)
        {
            _byId[row.Node.Id] = row;
        }

        foreach (var group in rows.GroupBy(r => r.RefreshMs))
        {
            var byId = group.ToDictionary(r => r.Node.Id);
            var results = await Client.MonitorManyAsync([.. byId.Keys], OnUpdate, group.Key, cancellationToken).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (result.Handle is null)
                {
                    lock (_watchLock)
                    {
                        _watch.Remove(byId[result.NodeId]);
                    }

                    _byId.TryRemove(result.NodeId, out _);

                    Log($"Can't monitor {Describe(byId[result.NodeId].Node)}: {StatusText.Of(result.Error.StatusCode)} {result.Error.LocalizedText}".TrimEnd());
                }
                else
                {
                    byId[result.NodeId].Monitor = result.Handle;
                }
            }
        }

        var added = rows.Count(r => r.Monitor is not null);
        if (added > 0)
        {
            var monitored = rows.Where(r => r.Monitor is not null).ToList();
            Log(added == 1
                ? $"Monitoring {Describe(monitored[0].Node)}."
                : $"Monitoring {added} items: {string.Join(", ", monitored.Take(MaxNamesInLog).Select(r => Describe(r.Node)))}{(added > MaxNamesInLog ? $" and {added - MaxNamesInLog} more" : string.Empty)}.");
            if (_recording is not null)
            {
                await _recording.AddItemsAsync([.. rows.Where(r => r.Monitor is not null).Select(ToRecordedItem)], cancellationToken).ConfigureAwait(false);
            }
        }

        return added;
    }

    public async Task UnmonitorAsync(WatchRow row)
    {
        lock (_watchLock)
        {
            _watch.Remove(row);
        }

        _byId.TryRemove(row.Node.Id, out _);
        if (row.Monitor is { } monitor)
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }

        Log($"Stopped monitoring {Describe(row.Node)}.");
    }

    /// <summary>
    /// Re-monitors every item at <paramref name="refreshMs"/> (the TUI's - / +), and uses it for items added later.
    /// Recordings have their own monitored items and keep their rate.
    /// </summary>
    public async Task ChangeRefreshAsync(int refreshMs, CancellationToken cancellationToken = default)
    {
        DefaultRefreshMs = refreshMs;
        var rows = Watch.Where(r => r.Monitor is not null && r.RefreshMs != refreshMs).ToList();
        if (rows.Count > 0)
        {
            var byId = rows.ToDictionary(r => r.Node.Id);
            var results = await Client.ChangeRefreshAsync([.. rows.Select(r => r.Monitor!)], OnUpdate, refreshMs, cancellationToken).ConfigureAwait(false);
            foreach (var result in results.Where(r => byId.ContainsKey(r.NodeId)))
            {
                var row = byId[result.NodeId];
                row.Monitor = result.Handle;
                if (result.Handle is not null)
                {
                    row.RefreshMs = refreshMs;
                }
                else
                {
                    Log($"Can't monitor {Describe(row.Node)} at {RefreshText(refreshMs)}: {StatusText.Of(result.Error.StatusCode)}");
                }
            }
        }

        Log($"Refresh time {RefreshText(refreshMs)}.");
    }

    /// <summary>"250 ms", "1 s", or "every message" for 0 (MQTT).</summary>
    public static string RefreshText(int ms) => ms switch
    {
        <= 0 => "every message",
        >= 1000 when ms % 1000 == 0 => $"{ms / 1000} s",
        _ => $"{ms} ms",
    };

    private void OnUpdate(ValueUpdate update)
    {
        if (_byId.TryGetValue(update.NodeId, out var row))
        {
            row.Apply(update);
        }
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Why a write must not happen here, or null when it may.</summary>
    public string? WriteBlockedReason => Args.ReadOnly ? "The session is read-only (Connection ▸ Read-Only in the app)." : null;

    public async Task<string> ReadValueTextAsync(NodeId id, CancellationToken cancellationToken = default)
    {
        var values = await Client.ReadValuesAsync([id], cancellationToken).ConfigureAwait(false);
        return values[0] is { } value ? ValueFormatter.Format(new Variant(value)) : "-";
    }

    /// <summary>Writes a value and reads it back, so a write the device accepted but didn't apply shows.</summary>
    public async Task<WriteOutcome> WriteAsync(NodeId id, string name, string text, CancellationToken cancellationToken = default)
    {
        if (WriteBlockedReason is { } reason)
        {
            throw new CliException(reason);
        }

        var before = await ReadValueTextAsync(id, cancellationToken).ConfigureAwait(false);
        string? error = null;
        try
        {
            await Client.WriteValueAsync(id, text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Errors.IsRecoverable(ex))
        {
            error = ex is ServiceResultException sre ? $"{StatusText.Of(sre.StatusCode)}: {sre.Message}" : ex.Message;
        }

        // MQTT values come back through the broker a moment later.
        if (error is null && Client is IDynamicAddressSpace)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        var after = await ReadValueTextAsync(id, cancellationToken).ConfigureAwait(false);
        var outcome = new WriteOutcome(name, before, after, error);
        var label = Describe(name, Client.ToDisplayId(id));
        Log(error is null ? $"Wrote {label}: {before} → {after}." : $"Write failed for {label}: {error}");
        return outcome;
    }

    // ------------------------------------------------------------------ recording

    public string? RecordingPath => _recordingPath;

    public long RecordedSamples => _recording?.TotalSamples ?? 0;

    /// <summary>Records every monitored item to a file: SQLite for .db/.sqlite, CSV otherwise (like --record).</summary>
    public async Task StartRecordingAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_recording is not null)
        {
            throw new CliException($"Already recording to {_recordingPath}.");
        }

        var rows = Watch;
        if (rows.Count == 0)
        {
            throw new CliException("Monitor something first (m); the recording takes the monitored items.");
        }

        var options = new RecordingOptions
        {
            Name = "mdbrowser tui",
            SamplingIntervalMs = rows.Min(r => r.RefreshMs),
            MaxPointsPerItem = 1,
            LiveFilePath = System.IO.Path.GetFullPath(path),
            Endpoint = Args.Url,
        };
        var recording = new Recording(Client, options, [.. rows.Select(ToRecordedItem)]);
        recording.Faulted += (_, ex) => Log($"Recording to {path} failed: {ex.Message}");
        await recording.StartAsync(cancellationToken).ConfigureAwait(false);
        _recording = recording;
        _recordingPath = path;
        Log($"Recording {rows.Count} item(s) to {path}.");
    }

    public async Task StopRecordingAsync()
    {
        if (_recording is not { } recording)
        {
            return;
        }

        _recording = null;
        await recording.StopAsync(CancellationToken.None).ConfigureAwait(false);
        var samples = recording.TotalSamples;
        await recording.DisposeAsync().ConfigureAwait(false);
        Log($"Recorded {samples} sample(s) to {_recordingPath}.");
        _recordingPath = null;
    }

    // ------------------------------------------------------------------ events and alarms

    /// <summary>Starts collecting events and alarms from the Server object (OPC UA), once.</summary>
    public async Task StartEventsAsync(CancellationToken cancellationToken = default)
    {
        if (_eventSubscription is not null || Client is not IEventSource source)
        {
            return;
        }

        _eventSubscription = await source.SubscribeEventsAsync(ObjectIds.Server, e =>
        {
            lock (_eventLock)
            {
                _events.Insert(0, e);
                if (_events.Count > MaxEvents)
                {
                    _events.RemoveAt(_events.Count - 1);
                }

                if (e.IsCondition)
                {
                    if (e.Retain)
                    {
                        _alarms[e.ConditionId!] = e;
                    }
                    else
                    {
                        _alarms.Remove(e.ConditionId!);
                    }
                }
            }

            Changed?.Invoke();
        }, cancellationToken).ConfigureAwait(false);
        Log("Receiving events and alarms.");
    }

    public IReadOnlyList<EventNotification> Events
    {
        get
        {
            lock (_eventLock)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>Current alarms (active or not acknowledged), most severe first.</summary>
    public IReadOnlyList<EventNotification> Alarms
    {
        get
        {
            lock (_eventLock)
            {
                return [.. _alarms.Values.OrderByDescending(a => a.Severity).ThenByDescending(a => a.Time)];
            }
        }
    }

    // ------------------------------------------------------------------ history and diagnostics

    public async Task<HistoryResult> ReadHistoryAsync(NodeId id, TimeSpan back, CancellationToken cancellationToken = default)
    {
        if (Client is not IHistorySource history)
        {
            throw new CliException("History is available from OPC UA servers only.");
        }

        var end = DateTime.UtcNow;
        return await history.ReadHistoryAsync(id, end - back, end, 10_000, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Name, string Value)>> DiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var lines = new List<(string, string)>();
        if (Client is IConnectionDiagnosticsSource source)
        {
            var diagnostics = await source.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            lines.Add(("State", Client.State.ToString()));
            lines.AddRange(diagnostics.Session.Select(s => (s.Name, s.Value)));
            lines.AddRange(diagnostics.Subscriptions.Select(s => ($"Subscription {s.Name}", $"{s.MonitoredItems} items · every {s.PublishingIntervalMs} ms")));
        }
        else
        {
            lines.AddRange([("Endpoint", Args.Url), ("State", Client.State.ToString()), ("Server", Client.ServerUri ?? "-")]);
        }

        lines.Add(("Monitored items", Watch.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        lines.Add(("Recording", _recordingPath is null ? "no" : $"{_recordingPath} · {RecordedSamples} samples"));
        return lines;
    }

    // ------------------------------------------------------------------ sessions

    /// <summary>Saves the monitored items as the session's watch list; an existing file keeps everything else.</summary>
    public async Task<SessionWriter.Result> SaveSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        var items = Watch.Select(r => new SessionWriter.Item(r.Node, r.RefreshMs)).ToList();
        var result = await SessionWriter.SaveWatchAsync(path, Args, DefaultRefreshMs, Client, items, cancellationToken).ConfigureAwait(false);
        SessionPath = path;
        Log($"Saved {result.Total} watch item(s) to {path}.");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await StopRecordingAsync().ConfigureAwait(false);
        if (_eventSubscription is { } events)
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var row in Watch)
        {
            if (row.Monitor is { } monitor)
            {
                await monitor.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (_ownsClient)
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Name and id (or tag / topic) for the log: names alone repeat across folders.</summary>
    internal static string Describe(Node node) => Describe(node.Name, node.DisplayId);

    internal static string Describe(string name, string id) => name == id ? name : $"{name} ({id})";

    private Node ToNode(TreeEntry entry) =>
        new(entry.Item.NodeId, entry.Item.DisplayName, Client.ToDisplayId(entry.Item.NodeId), entry.Item.DisplayName, entry.ParentPath);

    private RecordedItem ToRecordedItem(WatchRow row) =>
        new(row.Node.Id, row.Node.DisplayName ?? row.Node.Name, Client.ToPortableId(row.Node.Id), row.Node.ParentPath);
}
