using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Opc.Ua;

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

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    [McpServerTool(Name = "list_endpoints", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The machines (endpoints) this server may connect to, their protocol and connection state. Start here.")]
    public string ListEndpoints() => Json(new JsonArray([.. pool.Endpoints.Select(e => (JsonNode)new JsonObject
    {
        ["endpoint"] = e.Url,
        ["protocol"] = DeviceClient.IsEip(e.Url) ? "EtherNet/IP (Logix)" : DeviceClient.IsMqtt(e.Url) ? "MQTT" : "OPC UA",
        ["state"] = pool.StateOf(e).ToString(),
    })]));

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
    [Description("Reads the current value, data type and status of variables. With recursive, folders and structures expand to every variable below them.")]
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
        return new JsonObject
        {
            ["values"] = new JsonArray([.. resolved.Select((n, i) => (JsonNode)new JsonObject
            {
                ["name"] = n.Name,
                ["id"] = n.DisplayId,
                ["type"] = values[i] is null ? null : ValueJson.TypeName(values[i]),
                ["value"] = ValueJson.ToJson(values[i]),
                ["readable"] = values[i] is not null,
            })]),
            ["warnings"] = warnings,
        };
    });

    [McpServerTool(Name = "sample", ReadOnly = true, OpenWorld = false)]
    [Description("Watches values for a number of seconds and returns a summary per item: samples, changes, first and last value, "
        + "min/max/mean for numbers, and statuses seen. Use it to answer questions about behaviour over time (is it moving, "
        + "fluctuating, stuck, going bad).")]
    public Task<string> SampleAsync(
        [Description("Nodes: paths like /Objects/Line1/Speed or ids")] string[] nodes,
        [Description("How long to watch, 1-300 seconds")] int seconds = 10,
        [Description("Refresh time in ms (default 250; MQTT 0 = every message)")] int? refreshMs = null,
        [Description("Expand folders and structures to the variables below them")] bool recursive = false,
        [Description("With recursive: levels below each node, 1-10")] int depth = 10,
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
            await DeviceClient.StopMonitoringAsync([.. results.Select(r => r.Handle).OfType<IAsyncDisposable>()], CancellationToken.None).ConfigureAwait(false);
        }

        return new JsonObject
        {
            ["seconds"] = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
            ["refreshMs"] = interval,
            ["items"] = new JsonArray([.. stats.Values.Select(s => (JsonNode)s.ToJson())]),
            ["warnings"] = warnings,
        };
    });

    private static async Task<List<Node>> ResolveAsync(IDeviceClient client, string[] nodes, bool recursive, int depth, int maxItems, JsonArray warnings, CancellationToken cancellationToken)
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
        catch (ServiceResultException ex)
        {
            throw new McpException($"{StatusText.Of(ex.Result.StatusCode)}: {ex.Message}");
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

        public Node Node => node;

        public void Add(ValueUpdate update)
        {
            lock (_lock)
            {
                _samples++;
                if (_last is not null && _last.Value != update.Value)
                {
                    _changes++;
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
