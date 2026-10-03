using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Opc.Ua;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// Context an agent needs to interpret values: a node's attributes and unit, the server's stored history, current
/// alarms and recent events, and the health of the connection. All read-only.
/// </summary>
internal sealed partial class MachineDataTools
{
    public const int MaxHistoryValuesPerNode = 20_000;
    public const int MaxEventSeconds = 300;
    public const int MaxEvents = 500;

    /// <summary>Raw history values returned in total across all nodes; buckets are for more.</summary>
    public const int MaxHistoryRows = 1000;

    [McpServerTool(Name = "attributes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("All attributes of a node (data type, description, access level, value rank, …) and its engineering unit when the "
        + "server has one. Use it to understand what a value means before interpreting it.")]
    public Task<string> AttributesAsync(
        [Description("Node: a path like /Objects/Line1/Speed or an id")] string node,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var target = await Connection.ResolveAsync(client, node, cancellationToken).ConfigureAwait(false);
        var attributes = await client.ReadAttributesAsync(target.Id, cancellationToken).ConfigureAwait(false);
        var unit = client is IEngineeringUnitsSource units
            ? (await units.ReadUnitsAsync([target.Id], cancellationToken).ConfigureAwait(false))[0]
            : null;
        return new JsonObject
        {
            ["name"] = target.Name,
            ["id"] = target.DisplayId,
            ["unit"] = unit,
            ["attributes"] = new JsonObject([.. attributes.Select(a => System.Collections.Generic.KeyValuePair.Create(a.Name, (JsonNode?)a.Value))]),
        };
    });

    [McpServerTool(Name = "history", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Values the OPC UA server stored for variables over a time range (HistoryRead), oldest first. With bucketSeconds "
        + "they are aggregated per time bucket (count, min, max, average, not Good), which is how to look at long ranges. "
        + "Only variables the server historizes have history.")]
    public Task<string> HistoryAsync(
        [Description("Variables: paths like /Objects/Line1/Speed or ids")] string[] nodes,
        [Description("Start of the range, ISO 8601, e.g. 2026-10-03T08:00:00Z (UTC when no offset)")] string from,
        [Description("End of the range, ISO 8601; default now")] string? to = null,
        [Description("Aggregate per bucket of this many seconds (e.g. 3600 = hourly); omit for raw values")] int? bucketSeconds = null,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (client is not IHistorySource source)
        {
            throw new McpException("History is available from OPC UA servers only; for other protocols use recordings.");
        }

        var start = Time(from, nameof(from)) ?? throw new McpException("'from' is required.");
        var end = Time(to, nameof(to)) ?? DateTimeOffset.UtcNow;
        var warnings = new JsonArray();
        var resolved = await ResolveAsync(client, nodes, recursive: false, 1, MaxReadItems, warnings, cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        var rawBudget = MaxHistoryRows;
        foreach (var node in resolved)
        {
            var result = await source.ReadHistoryAsync(node.Id, start.UtcDateTime, end.UtcDateTime, MaxHistoryValuesPerNode, cancellationToken).ConfigureAwait(false);
            if (result.Truncated)
            {
                warnings.Add($"{node.Name}: more than {MaxHistoryValuesPerNode} values; only the oldest were read. Narrow the range.");
            }

            var item = new JsonObject { ["name"] = node.Name, ["id"] = node.DisplayId, ["values"] = result.Values.Count };
            if (bucketSeconds is { } seconds)
            {
                item["buckets"] = Buckets(result.Values, Math.Max(1, seconds));
            }
            else
            {
                var take = Math.Min(result.Values.Count, rawBudget);
                rawBudget -= take;
                item["samples"] = new JsonArray([.. result.Values.Take(take).Select(v => (JsonNode)new JsonObject
                {
                    ["time"] = Iso(v.SourceTimestamp),
                    ["status"] = Output.Status(v.Status),
                    ["value"] = v.Raw is null ? JsonValue.Create(v.Value) : ValueJson.ToJson(v.Raw),
                })]);
                if (take < result.Values.Count)
                {
                    item["truncated"] = true;
                }
            }

            items.Add(item);
        }

        return new JsonObject { ["from"] = Iso(start.UtcDateTime), ["to"] = Iso(end.UtcDateTime), ["items"] = items, ["warnings"] = warnings };
    });

    [McpServerTool(Name = "alarms", ReadOnly = true, OpenWorld = false)]
    [Description("Alarms the OPC UA server currently holds (active or not yet acknowledged), most severe first: source, name, "
        + "severity (1-1000; 700+ high), active, acknowledged, message, time.")]
    public Task<string> AlarmsAsync(
        [Description("Only alarms below this object (path or id); default the whole server")] string? node = null,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        // Subscribing makes the server resend every condition it holds; a few seconds collect the current state.
        var received = await CollectEventsAsync(endpoint, node, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        var latest = new Dictionary<NodeId, EventNotification>();
        foreach (var e in received.Where(e => e.IsCondition))
        {
            if (e.Retain)
            {
                latest[e.ConditionId!] = e;
            }
            else
            {
                latest.Remove(e.ConditionId!);
            }
        }

        var alarms = latest.Values.OrderByDescending(a => a.Severity).ThenByDescending(a => a.Time).ToList();
        return new JsonObject
        {
            ["alarms"] = new JsonArray([.. alarms.Select(a => (JsonNode)new JsonObject
            {
                ["source"] = a.SourceName,
                ["name"] = a.ConditionName,
                ["severity"] = a.Severity,
                ["active"] = a.IsActive,
                ["acknowledged"] = a.IsAcked,
                ["message"] = a.Message,
                ["type"] = a.EventType,
                ["time"] = Iso(a.Time),
            })]),
            ["count"] = alarms.Count,
            ["unacknowledged"] = alarms.Count(a => a.IsAcked == false),
        };
    });

    [McpServerTool(Name = "events", ReadOnly = true, OpenWorld = false)]
    [Description("Collects OPC UA events and alarm changes for some seconds and returns them newest first: time, severity, source, "
        + "type, message and alarm state. Use alarms for the current alarm list.")]
    public Task<string> EventsAsync(
        [Description("How long to listen, 1-300 seconds")] int seconds = 10,
        [Description("Only events at least this severe (1-1000; 400+ medium, 700+ high)")] int minSeverity = 0,
        [Description("Only events below this object (path or id); default the whole server")] string? node = null,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var received = await CollectEventsAsync(endpoint, node, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxEventSeconds)), cancellationToken).ConfigureAwait(false);
        var events = received.Where(e => e.Severity >= minSeverity).OrderByDescending(e => e.Time).ToList();
        return new JsonObject
        {
            ["events"] = new JsonArray([.. events.Take(MaxEvents).Select(e => (JsonNode)new JsonObject
            {
                ["time"] = Iso(e.Time),
                ["severity"] = e.Severity,
                ["source"] = e.SourceName,
                ["type"] = e.EventType,
                ["message"] = e.Message,
                ["alarm"] = e.ConditionName,
                ["active"] = e.IsActive,
                ["acknowledged"] = e.IsAcked,
            })]),
            ["received"] = received.Count,
            ["truncated"] = events.Count > MaxEvents,
        };
    });

    [McpServerTool(Name = "diagnostics", ReadOnly = true, OpenWorld = false)]
    [Description("Health of the connection to a machine: state and session details. OPC UA: security, keep-alive, "
        + "reconnects, server state and clock offset, subscriptions. MQTT: broker, messages per second, topics, Sparkplug "
        + "nodes online. EtherNet/IP: link drops, read rate and failures, poll cycle times. Use it when values look stale "
        + "or connections fail.")]
    public Task<string> DiagnosticsAsync(
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var result = new JsonObject
        {
            ["endpoint"] = pool.Find(endpoint).Url,
            ["state"] = client.State.ToString(),
            ["server"] = client.ServerUri,
        };
        if (client is IConnectionDiagnosticsSource source)
        {
            var diagnostics = await source.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            result["session"] = new JsonObject([.. diagnostics.Session.Select(s => System.Collections.Generic.KeyValuePair.Create(s.Name, (JsonNode?)s.Value))]);
            result["subscriptions"] = new JsonArray([.. diagnostics.Subscriptions.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["publishingIntervalMs"] = s.PublishingIntervalMs,
                ["monitoredItems"] = s.MonitoredItems,
                ["notifications"] = s.Notifications,
                ["lastNotification"] = s.LastNotification is { } last ? Iso(last) : null,
                ["publishingEnabled"] = s.PublishingEnabled,
            })]);
        }

        return result;
    });

    private async Task<List<EventNotification>> CollectEventsAsync(string? endpoint, string? node, TimeSpan duration, CancellationToken cancellationToken)
    {
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (client is not IEventSource source)
        {
            throw new McpException("Events and alarms are available from OPC UA servers only.");
        }

        var notifier = node is null ? ObjectIds.Server : (await Connection.ResolveAsync(client, node, cancellationToken).ConfigureAwait(false)).Id;
        var received = new List<EventNotification>();
        var subscription = await source.SubscribeEventsAsync(notifier, e =>
        {
            lock (received)
            {
                received.Add(e);
            }
        }, cancellationToken).ConfigureAwait(false);
        await using (subscription.ConfigureAwait(false))
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }

        lock (received)
        {
            return [.. received];
        }
    }

    private static JsonArray Buckets(IReadOnlyList<ValueUpdate> values, int seconds) => new([.. values
        .GroupBy(v => DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(DateTime.SpecifyKind(v.SourceTimestamp, DateTimeKind.Utc)).ToUnixTimeSeconds() / seconds * seconds))
        .OrderBy(g => g.Key)
        .Select(g =>
        {
            var numbers = g.Select(v => v.Numeric).OfType<double>().Where(double.IsFinite).ToList();
            return (JsonNode)new JsonObject
            {
                ["start"] = g.Key.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["count"] = g.Count(),
                ["min"] = numbers.Count > 0 ? numbers.Min() : null,
                ["max"] = numbers.Count > 0 ? numbers.Max() : null,
                ["average"] = numbers.Count > 0 ? Math.Round(numbers.Average(), 6) : null,
                ["notGood"] = g.Count(v => !StatusCode.IsGood(v.Status)),
            };
        })]);

    private static DateTimeOffset? Time(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return SqliteRecordingQuery.ParseTime(text);
        }
        catch (FormatException)
        {
            throw new McpException($"'{name}' must be an ISO 8601 time like 2026-10-03T08:00:00Z, not '{text}'.");
        }
    }

    private static string Iso(DateTime time) =>
        (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
