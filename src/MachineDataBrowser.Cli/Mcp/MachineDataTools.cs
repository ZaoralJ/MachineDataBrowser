using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using MachineDataBrowser.Driver.OpcUa;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// The read-only tools an AI agent gets: find out what exists (endpoints, browse, search), read values, and watch values
/// for a while as a summary. Results are JSON text; limits keep a large address space from flooding the agent's context
/// or the device.
/// </summary>
internal sealed partial class MachineDataTools(EndpointPool pool)
{
    public const int MaxBrowseItems = 500;
    public const int MaxReadItems = 200;
    public const int MaxSampleItems = 100;
    public const int MaxSampleSeconds = 300;
    public const int MaxSnapshotItems = 500;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    [McpServerTool(Name = "list_endpoints", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The machines (endpoints) this server may connect to, their protocol and connection state. Start here.")]
    public string ListEndpoints() => Json(new JsonArray([
        .. pool.Endpoints.Select(e => (JsonNode)new JsonObject
        {
            ["endpoint"] = e.Url,
            ["protocol"] = DeviceClient.IsEip(e.Url) ? "EtherNet/IP (Logix)" : DeviceClient.IsMqtt(e.Url) ? "MQTT" : "OPC UA",
            ["state"] = pool.StateOf(e).ToString(),
        }),
        .. pool.Patterns.Select(p => (JsonNode)new JsonObject
        {
            ["allowedPattern"] = p,
            ["note"] = "Any endpoint matching this may be named in a call; it connects without credentials.",
        }),
    ]));

    [McpServerTool(Name = "browse", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the address space below a node: path, node class (Object = folder, Variable = value, Method) and id. "
        + "Paths can be passed to read, sample and browse as they are.")]
    public Task<string> BrowseAsync(
        [Description("Node: a path of display names from the root like /Objects/Line1, or an id; default the root")] string node = "/",
        [Description("Levels below the node, 1-5")] int depth = 1,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var start = node is "/" or "" ? new Node(client.Root.NodeId, client.Root.DisplayName, client.ToDisplayId(client.Root.NodeId))
            : await Connection.ResolveAsync(client, node, cancellationToken).ConfigureAwait(false);
        var rows = new List<IReadOnlyList<string>>();
        var prefix = node.StartsWith('/') ? node.TrimEnd('/') : string.Empty;
        await NodeQueries.BrowseAsync(client, start.Id, prefix, Math.Clamp(depth, 1, 5), rows, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["node"] = start.Name,
            ["items"] = new JsonArray([.. rows.Take(MaxBrowseItems).Select(r => (JsonNode)new JsonObject { ["path"] = r[0], ["class"] = r[1], ["id"] = r[2] })]),
            ["truncated"] = rows.Count > MaxBrowseItems,
        };
    });

    [McpServerTool(Name = "search", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds nodes by name or id below a node. Case-insensitive; * and ? are wildcards (Temp*, Motor?), without them the text may occur anywhere.")]
    public Task<string> SearchAsync(
        [Description("Text to find in names or ids")] string text,
        [Description("Search below this node (path or id); default the whole address space")] string under = "/",
        [Description("Most results to return, 1-200")] int maxResults = 50,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var start = client.Root;
        var prefix = string.Empty;
        if (under is not "/" and not "")
        {
            var node = await Connection.ResolveAsync(client, under, cancellationToken).ConfigureAwait(false);
            start = new BrowseItem(node.Id, node.Name, node.Name, NodeClass.Object);
            prefix = under.StartsWith('/') ? under.TrimEnd('/') : string.Empty;
        }

        var result = await client.SearchAsync(start, text, maxResults: Math.Clamp(maxResults, 1, 200), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["hits"] = new JsonArray([.. result.Hits.Select(h => (JsonNode)new JsonObject
            {
                ["path"] = prefix.Length > 0 || under is "/" or "" ? $"{prefix}/{string.Join('/', h.PathNames)}" : string.Join('/', h.PathNames),
                ["class"] = h.Item.NodeClass.ToString(),
                ["id"] = client.ToDisplayId(h.Item.NodeId),
            })]),
            ["nodesVisited"] = result.NodesVisited,
            ["truncated"] = result.Truncated,
        };
    });

    [McpServerTool(Name = "read", ReadOnly = true, OpenWorld = false)]
    [Description("Reads the current value, data type and engineering unit (when the server has one) of variables. With recursive, "
        + "folders and structures expand to every variable below them.")]
    public Task<string> ReadAsync(
        [Description("Nodes: paths like /Objects/Line1/Speed or ids")] string[] nodes,
        [Description("Expand folders and structures to the variables below them")] bool recursive = false,
        [Description("With recursive: levels below each node, 1-10")] int depth = 10,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var warnings = new JsonArray();
        var resolved = await ResolveAsync(client, nodes, recursive, depth, MaxReadItems, warnings, cancellationToken).ConfigureAwait(false);
        var values = await client.ReadValuesAsync([.. resolved.Select(n => n.Id)], cancellationToken).ConfigureAwait(false);
        var units = await UnitsAsync(client, resolved, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["values"] = new JsonArray([.. resolved.Select((n, i) => (JsonNode)new JsonObject
            {
                ["name"] = n.Name,
                ["id"] = n.DisplayId,
                ["type"] = values[i] is null ? null : ValueJson.TypeName(values[i]),
                ["value"] = ValueJson.ToJson(values[i]),
                ["unit"] = units[i],
                ["readable"] = values[i] is not null,
            })]),
            ["warnings"] = warnings,
        };
    });

    [McpServerTool(Name = "sample", ReadOnly = true, OpenWorld = false)]
    [Description("Watches values for a number of seconds and returns a summary per item: samples, changes, first and last value, "
        + "min/max/mean for numbers, and statuses seen. Use it to answer questions about behaviour over time (is it moving, "
        + "fluctuating, stuck, going bad). With correlate, also the order in which values changed and which values change "
        + "shortly after which (e.g. pressure drops 200 ms after the valve opens): cause-and-effect candidates.")]
    public Task<string> SampleAsync(
        [Description("Nodes: paths like /Objects/Line1/Speed or ids")] string[] nodes,
        [Description("How long to watch, 1-300 seconds")] int seconds = 10,
        [Description("Refresh time in ms (default 250; MQTT 0 = every message)")] int? refreshMs = null,
        [Description("Expand folders and structures to the variables below them")] bool recursive = false,
        [Description("With recursive: levels below each node, 1-10")] int depth = 10,
        [Description("Also report the order of changes and which values change shortly after which")] bool correlate = false,
        [Description("With correlate: a change counts as following another within this many ms, 10-10000")] int withinMs = 1000,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var warnings = new JsonArray();
        var resolved = await ResolveAsync(client, nodes, recursive, depth, MaxSampleItems, warnings, cancellationToken).ConfigureAwait(false);
        var stats = resolved.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => new SampleStats(g.First()));
        var interval = refreshMs ?? (DeviceClient.IsMqtt(pool.Find(endpoint).Url) ? 0 : 250);
        var results = await client.MonitorManyAsync([.. stats.Keys], u =>
        {
            if (stats.TryGetValue(u.NodeId, out var s))
            {
                s.Add(u);
            }
        }, interval, cancellationToken).ConfigureAwait(false);

        foreach (var rejected in results.Where(r => r.Handle is null))
        {
            warnings.Add($"cannot monitor {stats[rejected.NodeId].Node.Name}: {rejected.Error}");
        }

        var started = DateTime.UtcNow;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxSampleSeconds)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await client.StopMonitoringAsync([.. results.Select(r => r.Handle).OfType<IAsyncDisposable>()], CancellationToken.None).ConfigureAwait(false);
        }

        var result = new JsonObject
        {
            ["seconds"] = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
            ["refreshMs"] = interval,
            ["items"] = new JsonArray([.. stats.Values.Select(s => (JsonNode)s.ToJson())]),
            ["warnings"] = warnings,
        };
        if (correlate)
        {
            var within = TimeSpan.FromMilliseconds(Math.Clamp(withinMs, 10, 10_000));
            var duplicate = stats.Values.GroupBy(s => s.Node.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var changes = stats.Values.ToDictionary(s => duplicate.Contains(s.Node.Name) ? $"{s.Node.Name} ({s.Node.DisplayId})" : s.Node.Name, s => s.Changes());
            result["sequence"] = Sequence(changes);
            result["correlations"] = Correlations(changes, within);
            if (interval > within.TotalMilliseconds / 2)
            {
                warnings.Add($"refreshMs {interval} is coarse for withinMs {within.TotalMilliseconds}: lags are only as exact as the refresh time.");
            }
        }

        return result;
    });

    public const int MaxSequence = 100;
    public const int MaxCorrelations = 20;

    /// <summary>Every change in time order (the first changes, up to <see cref="MaxSequence"/>).</summary>
    private static JsonObject Sequence(Dictionary<string, IReadOnlyList<(DateTime Time, JsonNode? Value)>> changes)
    {
        var all = changes.SelectMany(c => c.Value.Select(v => (Name: c.Key, v.Time, v.Value))).OrderBy(c => c.Time).ToList();
        return new JsonObject
        {
            ["changes"] = new JsonArray([.. all.Take(MaxSequence).Select(c => (JsonNode)new JsonObject
            {
                ["time"] = c.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                ["name"] = c.Name,
                ["value"] = c.Value?.DeepClone(),
            })]),
            ["total"] = all.Count,
            ["truncated"] = all.Count > MaxSequence,
        };
    }

    /// <summary>
    /// Pairs where the follower changed within <paramref name="within"/> after most of the leader's changes (at least
    /// twice): candidates for cause and effect, not proof. Lag 0 means they change in the same update.
    /// </summary>
    private static JsonArray Correlations(Dictionary<string, IReadOnlyList<(DateTime Time, JsonNode? Value)>> changes, TimeSpan within)
    {
        var times = changes.ToDictionary(c => c.Key, c => c.Value.Select(v => v.Time).ToArray());
        var pairs = new List<(string Leader, string Follower, int Hits, int Of, double MedianLag)>();
        foreach (var (leader, leaderTimes) in times.Where(t => t.Value.Length > 0))
        {
            foreach (var (follower, followerTimes) in times.Where(t => t.Key != leader && t.Value.Length > 0))
            {
                var lags = new List<double>();
                foreach (var t in leaderTimes)
                {
                    var at = Array.BinarySearch(followerTimes, t);
                    at = at < 0 ? ~at : at;
                    if (at < followerTimes.Length && followerTimes[at] - t <= within)
                    {
                        lags.Add((followerTimes[at] - t).TotalMilliseconds);
                    }
                }

                if (lags.Count >= 2 && lags.Count * 2 >= leaderTimes.Length)
                {
                    lags.Sort();
                    pairs.Add((leader, follower, lags.Count, leaderTimes.Length, lags[lags.Count / 2]));
                }
            }
        }

        return new JsonArray([.. pairs.OrderByDescending(p => (double)p.Hits / p.Of).ThenByDescending(p => p.Hits).ThenBy(p => p.MedianLag)
            .Take(MaxCorrelations).Select(p => (JsonNode)new JsonObject
            {
                ["leader"] = p.Leader,
                ["follower"] = p.Follower,
                ["followed"] = p.Hits,
                ["leaderChanges"] = p.Of,
                ["medianLagMs"] = Math.Round(p.MedianLag, 1),
            })]);
    }

    internal static async Task<List<Node>> ResolveAsync(IDeviceClient client, string[] nodes, bool recursive, int depth, int maxItems, JsonArray warnings, CancellationToken cancellationToken)
    {
        if (nodes is null || nodes.Length == 0)
        {
            throw new McpException("Pass at least one node (a path like /Objects/Line1/Speed or an id).");
        }

        if (recursive)
        {
            return await NodeQueries.ExpandAsync(client, nodes, Math.Clamp(depth, 1, NodeQueries.DefaultRecursiveDepth), maxItems, w =>
            {
                warnings.Add(w.Replace("mdbrowser: ", string.Empty, StringComparison.Ordinal).Replace("--max-items", "the tool's item limit", StringComparison.Ordinal));
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }

        if (nodes.Length > maxItems)
        {
            throw new McpException($"At most {maxItems} nodes per call.");
        }

        return await NodeQueries.ResolveAllAsync(client, nodes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Engineering units (OPC UA EngineeringUnits), so a number isn't read without its unit. A server that can't say
    /// gives none; the values are what was asked for, so units never fail the read.
    /// </summary>
    private static async Task<IReadOnlyList<string?>> UnitsAsync(IDeviceClient client, List<Node> nodes, CancellationToken cancellationToken)
    {
        if (client is IEngineeringUnitsSource source && nodes.Count > 0)
        {
            try
            {
                return await source.ReadUnitsAsync([.. nodes.Select(n => n.Id)], cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceClientException)
            {
            }
        }

        return [.. nodes.Select(_ => (string?)null)];
    }

    /// <summary>Runs a tool, turning expected failures (unknown node, server errors) into tool errors the agent can read.</summary>
    private static async Task<string> Guard(Func<Task<JsonNode>> tool)
    {
        try
        {
            return Json(await tool().ConfigureAwait(false));
        }
        catch (CliException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (DeviceClientException ex)
        {
            throw new McpException($"{ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            throw new McpException(ex.Message);
        }
    }

    private static string Json(JsonNode node) => node.ToJsonString(Compact);

    /// <summary>What happened to one value while it was sampled.</summary>
    private sealed class SampleStats(Node node)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, int> _statuses = new(StringComparer.Ordinal);
        private int _samples;
        private int _changes;
        private ValueUpdate? _first;
        private ValueUpdate? _last;
        private double? _min;
        private double? _max;
        private double _sum;
        private int _numeric;
        private readonly List<(DateTime Time, JsonNode? Value)> _changeTimes = [];

        /// <summary>Changes kept for correlation; enough for 300 s at 10 ms.</summary>
        private const int MaxChangeTimes = 30_000;

        public Node Node => node;

        public void Add(ValueUpdate update)
        {
            lock (_lock)
            {
                _samples++;
                if (_last is not null && _last.Value != update.Value)
                {
                    _changes++;
                    if (_changeTimes.Count < MaxChangeTimes)
                    {
                        // The device's time says when it changed; without one (year 1), when it arrived.
                        _changeTimes.Add((update.SourceTimestamp.Year > 2000 ? DateTime.SpecifyKind(update.SourceTimestamp, DateTimeKind.Utc) : DateTime.UtcNow, Value(update)));
                    }
                }

                _first ??= update;
                _last = update;
                var status = Output.Status(update.Status);
                _statuses[status] = _statuses.GetValueOrDefault(status) + 1;
                if (update.Numeric is { } number && double.IsFinite(number))
                {
                    _min = Math.Min(_min ?? number, number);
                    _max = Math.Max(_max ?? number, number);
                    _sum += number;
                    _numeric++;
                }
            }
        }

        public IReadOnlyList<(DateTime Time, JsonNode? Value)> Changes()
        {
            lock (_lock)
            {
                return [.. _changeTimes.OrderBy(c => c.Time)];
            }
        }

        public JsonObject ToJson()
        {
            lock (_lock)
            {
                var json = new JsonObject
                {
                    ["name"] = node.Name,
                    ["id"] = node.DisplayId,
                    ["samples"] = _samples,
                    ["changes"] = _changes,
                    ["first"] = Value(_first),
                    ["last"] = Value(_last),
                    ["lastStatus"] = _last is null ? null : Output.Status(_last.Status),
                    ["statuses"] = new JsonObject([.. _statuses.Select(s => System.Collections.Generic.KeyValuePair.Create(s.Key, (JsonNode?)s.Value))]),
                };
                if (_numeric > 0)
                {
                    json["min"] = _min;
                    json["max"] = _max;
                    json["mean"] = Math.Round(_sum / _numeric, 6);
                }

                return json;
            }
        }

        private static JsonNode? Value(ValueUpdate? update) =>
            update is null ? null : update.Raw is null ? JsonValue.Create(update.Value) : ValueJson.ToJson(update.Raw);
    }
}
