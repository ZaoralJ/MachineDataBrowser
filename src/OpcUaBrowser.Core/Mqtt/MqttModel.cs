using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opc.Ua;

namespace OpcUaBrowser.Core.Mqtt;

/// <summary>
/// Everything the MQTT client has seen, as a browsable model: the raw topic tree (payloads decoded as number,
/// boolean, text, JSON or bytes; JSON fields become child nodes) and the Sparkplug B view (groups, edge nodes,
/// devices and metrics with their birth/death state). All node ids are ns=1 strings with a kind prefix:
/// <list type="bullet">
/// <item><c>root</c>, <c>topics</c>, <c>spb</c> – fixed folders;</item>
/// <item><c>t:&lt;topic&gt;</c> – a topic level (a variable when it carries a payload);</item>
/// <item><c>t:&lt;topic&gt;#&lt;json pointer&gt;</c> – a field inside a JSON payload;</item>
/// <item><c>g:</c>/<c>e:</c>/<c>d:</c>/<c>f:</c>/<c>m:</c> – Sparkplug group, edge node, device, metric folder, metric
/// (parts separated by <c>|</c>).</item>
/// </list>
/// </summary>
internal sealed class MqttModel
{
    public const ushort Ns = 1;
    public const string RootId = "root";
    public const string TopicsId = "topics";
    public const string SparkplugId = "spb";
    private const char Sep = '|';

    private readonly Lock _lock = new();
    private readonly Dictionary<string, TopicEntry> _topics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<string>> _children = new(StringComparer.Ordinal) { [string.Empty] = new(StringComparer.Ordinal) };
    private readonly SortedDictionary<string, SortedDictionary<string, SpbEdge>> _groups = new(StringComparer.Ordinal);

    public MqttModel(int maxTopics) => MaxTopics = maxTopics;

    public int MaxTopics { get; }

    /// <summary>Incremented whenever a node is added or removed (new topic, device, metric, JSON shape change).</summary>
    public int Version { get; private set; }

    /// <summary>Messages dropped because <see cref="MaxTopics"/> was reached.</summary>
    public long DroppedTopics { get; private set; }

    public int TopicCount
    {
        get
        {
            lock (_lock)
            {
                return _topics.Count;
            }
        }
    }

    // ---------------------------------------------------------------- incoming messages

    /// <summary>Stores a message and returns the node keys whose value changed.</summary>
    public IReadOnlyList<string> Apply(string topic, ReadOnlySpan<byte> payload, bool retain, int qos, string? contentType, DateTime receivedUtc,
        IReadOnlyList<KeyValuePair<string, string>>? userProperties = null)
    {
        if (topic.StartsWith(SparkplugB.Namespace + "/", StringComparison.Ordinal))
        {
            return ApplySparkplug(topic, payload, receivedUtc);
        }

        lock (_lock)
        {
            if (!_topics.TryGetValue(topic, out var entry))
            {
                if (_topics.Count >= MaxTopics)
                {
                    DroppedTopics++;
                    return [];
                }

                entry = new TopicEntry(topic);
                _topics[topic] = entry;
                AddToTree(topic);
                Version++;
            }

            entry.Payload = payload.ToArray();
            entry.Retain = retain;
            entry.Qos = qos;
            entry.ContentType = contentType;
            entry.UserProperties = userProperties ?? [];
            entry.ReceivedUtc = receivedUtc;
            var previousShape = Shape(entry.Value);
            entry.Value = DecodePayload(entry.Payload);
            if (Shape(entry.Value) != previousShape)
            {
                Version++;
            }

            return ["t:" + topic];
        }
    }

    /// <summary>The field names of a JSON payload (its browse children); other payloads have no shape.</summary>
    private static string Shape(object? value) => value switch
    {
        JsonObject obj => "{" + string.Join(',', obj.Select(p => p.Key + ":" + Shape(p.Value))) + "}",
        JsonArray arr => $"[{arr.Count}:" + (arr.Count > 0 ? Shape(arr[0]) : string.Empty) + "]",
        _ => string.Empty,
    };

    private void AddToTree(string topic)
    {
        var parent = string.Empty;
        foreach (var level in topic.Split('/'))
        {
            var path = parent.Length == 0 ? level : parent + "/" + level;
            _children[parent].Add(path);
            _children.TryAdd(path, new SortedSet<string>(StringComparer.Ordinal));
            parent = path;
        }
    }

    internal static object? DecodePayload(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return payload;
        }

        // Valid UTF-8 can still be binary (a little-endian int32 of 5 is "\u0005\0\0\0"): control characters other
        // than whitespace mean it is not text.
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
        {
            return payload;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                return text;
            }
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return bool.TryParse(trimmed, out var flag) ? flag : text;
    }

    private List<string> ApplySparkplug(string topic, ReadOnlySpan<byte> payload, DateTime receivedUtc)
    {
        // spBv1.0/<group>/<message type>/<edge node>[/<device>]
        var parts = topic.Split('/');
        if (parts.Length < 4)
        {
            return [];
        }

        var (group, type, edgeName, deviceName) = (parts[1], parts[2], parts[3], parts.Length > 4 ? parts[4] : string.Empty);
        if (type is "NCMD" or "DCMD")
        {
            return [];
        }

        SparkplugB.Payload? decoded = null;
        if (type is not ("NDEATH" or "DDEATH"))
        {
            try
            {
                decoded = SparkplugB.Decode(payload);
            }
            catch (FormatException)
            {
                return [];
            }
        }

        lock (_lock)
        {
            if (!_groups.TryGetValue(group, out var edges))
            {
                _groups[group] = edges = new SortedDictionary<string, SpbEdge>(StringComparer.Ordinal);
                Version++;
            }

            if (!edges.TryGetValue(edgeName, out var edge))
            {
                edges[edgeName] = edge = new SpbEdge(group, edgeName);
                Version++;
            }

            var devicesBefore = edge.Devices.Count;
            var device = deviceName.Length == 0 ? edge.Node : edge.Device(deviceName);
            var metricsBefore = device.Metrics.Count;
            var namesBefore = device.Metrics.Keys.Count(k => k.StartsWith(AliasPlaceholder, StringComparison.Ordinal));
            var changed = new List<string>();
            switch (type)
            {
                case "NBIRTH":
                    edge.Aliases.Clear();
                    SetOnline(edge.Node, true, receivedUtc, changed);
                    ApplyMetrics(edge, edge.Node, decoded!, receivedUtc, changed, birth: true);
                    break;
                case "DBIRTH":
                    SetOnline(device, true, receivedUtc, changed);
                    ApplyMetrics(edge, device, decoded!, receivedUtc, changed, birth: true);
                    break;
                case "NDATA":
                case "DDATA":
                    ApplyMetrics(edge, device, decoded!, receivedUtc, changed, birth: false);
                    break;
                case "NDEATH":
                    SetOnline(edge.Node, false, receivedUtc, changed);
                    foreach (var d in edge.Devices.Values)
                    {
                        SetOnline(d, false, receivedUtc, changed);
                    }

                    break;
                case "DDEATH":
                    SetOnline(device, false, receivedUtc, changed);
                    break;
            }

            if (decoded?.Seq is { } seq)
            {
                device.Seq = seq;
            }

            if (edge.Devices.Count != devicesBefore || device.Metrics.Count != metricsBefore
                || device.Metrics.Keys.Count(k => k.StartsWith(AliasPlaceholder, StringComparison.Ordinal)) != namesBefore)
            {
                Version++;
            }

            return changed;
        }
    }

    private static void SetOnline(SpbContainer container, bool online, DateTime receivedUtc, List<string> changed)
    {
        container.Online = online;
        container.StateChangedUtc = receivedUtc;

        // Every metric's status follows its node/device (Bad while it is dead), so all of them are reported.
        changed.AddRange(container.Metrics.Values.Select(m => m.Key));
        changed.AddRange(container.Redirects.Keys.Select(p => Key(container, p)));
    }

    private const string AliasPlaceholder = "alias ";

    private static void ApplyMetrics(SpbEdge edge, SpbContainer container, SparkplugB.Payload payload, DateTime receivedUtc, List<string> changed, bool birth)
    {
        if (birth)
        {
            // The birth names the aliases: "alias 101" placeholders disappear from the tree, but ids already watched
            // or recorded keep working by following the named metric.
            foreach (var placeholder in container.Metrics.Keys.Where(k => k.StartsWith(AliasPlaceholder, StringComparison.Ordinal)).ToList())
            {
                container.Metrics.Remove(placeholder);
                var alias = ulong.Parse(placeholder[AliasPlaceholder.Length..], CultureInfo.InvariantCulture);
                var named = payload.Metrics.FirstOrDefault(m => m.Alias == alias && !string.IsNullOrEmpty(m.Name))?.Name;
                if (named is not null)
                {
                    container.Redirects[placeholder] = named;
                }
            }
        }

        foreach (var metric in payload.Metrics)
        {
            var name = metric.Name;
            if (string.IsNullOrEmpty(name) && metric.Alias is { } alias && edge.Aliases.TryGetValue(alias, out var known))
            {
                name = known;
            }

            if (string.IsNullOrEmpty(name))
            {
                // DATA with an alias whose BIRTH was published before we connected: keep the value visible under a
                // placeholder until the next birth names it (a read-only browser cannot request a rebirth).
                if (metric.Alias is not { } unknown)
                {
                    continue;
                }

                name = AliasPlaceholder + unknown.ToString(CultureInfo.InvariantCulture);
            }

            if (metric.Alias is { } newAlias && !string.IsNullOrEmpty(metric.Name))
            {
                edge.Aliases[newAlias] = metric.Name;
            }

            if (!container.Metrics.TryGetValue(name, out var state))
            {
                state = new SpbMetric(Key(container, name), name);
                container.Metrics[name] = state;
            }

            if (birth || metric.DataType != 0)
            {
                state.DataType = metric.DataType;
            }

            state.Alias = metric.Alias ?? state.Alias;
            state.Value = metric.Value;
            state.IsNull = metric.IsNull;
            var timestamp = metric.Timestamp ?? payload.Timestamp;
            state.SourceUtc = timestamp is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime : receivedUtc;
            state.ReceivedUtc = receivedUtc;
            changed.Add(state.Key);
            changed.AddRange(container.Redirects.Where(r => r.Value == name).Select(r => Key(container, r.Key)));
        }
    }

    private static string Key(SpbContainer container, string metric) =>
        $"m:{container.Group}{Sep}{container.Edge}{Sep}{container.Device}{Sep}{metric}";

    // ---------------------------------------------------------------- browsing

    public IReadOnlyList<BrowseItem> Browse(string id)
    {
        lock (_lock)
        {
            if (id == RootId)
            {
                return
                [
                    new BrowseItem(Node(TopicsId), "Topics", "Topics", NodeClass.Object),
                    new BrowseItem(Node(SparkplugId), "Sparkplug B", "Sparkplug B", NodeClass.Object),
                ];
            }

            if (id == TopicsId)
            {
                return [.. _children[string.Empty].Where(p => p != SparkplugB.Namespace).Select(TopicItem)];
            }

            if (id == SparkplugId)
            {
                return [.. _groups.Keys.Select(g => new BrowseItem(Node("g:" + g), g, g, NodeClass.Object))];
            }

            if (id.StartsWith("t:", StringComparison.Ordinal))
            {
                return BrowseTopic(id[2..]);
            }

            var parts = id.Length > 2 ? id[2..].Split(Sep) : [];
            return id[..2] switch
            {
                "g:" when parts.Length == 1 && _groups.TryGetValue(parts[0], out var edges) =>
                    [.. edges.Values.Select(e => new BrowseItem(Node($"e:{e.Group}{Sep}{e.Name}"), e.Name, e.Name, NodeClass.Object))],
                "e:" when parts.Length == 2 && FindEdge(parts[0], parts[1]) is { } edge =>
                    [.. MetricChildren(edge.Node, string.Empty), .. edge.Devices.Values.Select(d => new BrowseItem(Node($"d:{d.Group}{Sep}{d.Edge}{Sep}{d.Device}"), d.Device, d.Device, NodeClass.Object))],
                "d:" when parts.Length == 3 && FindContainer(parts[0], parts[1], parts[2]) is { } device => MetricChildren(device, string.Empty),
                "f:" when parts.Length == 4 && FindContainer(parts[0], parts[1], parts[2]) is { } container => MetricChildren(container, parts[3] + "/"),
                _ => [],
            };
        }
    }

    private BrowseItem TopicItem(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var hasSubtopics = _children.TryGetValue(path, out var sub) && sub.Count > 0;
        if (_topics.TryGetValue(path, out var entry))
        {
            var container = entry.Value is JsonObject or JsonArray;
            return new BrowseItem(Node("t:" + path), name.Length == 0 ? "(empty)" : name, path, NodeClass.Variable, HasChildren: hasSubtopics || container);
        }

        return new BrowseItem(Node("t:" + path), name.Length == 0 ? "(empty)" : name, path, NodeClass.Object, HasChildren: hasSubtopics);
    }

    private List<BrowseItem> BrowseTopic(string rest)
    {
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        var topic = hash < 0 ? rest : rest[..hash];
        var pointer = hash < 0 ? string.Empty : rest[(hash + 1)..];
        var items = new List<BrowseItem>();
        if (_topics.TryGetValue(topic, out var entry) && Resolve(entry.Value, pointer) is JsonObject or JsonArray)
        {
            var node = Resolve(entry.Value, pointer);
            var fields = node is JsonObject obj
                ? obj.Select(p => (Name: p.Key, Pointer: pointer + "/" + Escape(p.Key), Value: p.Value))
                : ((JsonArray)node!).Take(1000).Select((v, i) => (Name: $"[{i}]", Pointer: pointer + "/" + i.ToString(CultureInfo.InvariantCulture), Value: v));
            items.AddRange(fields.Select(f => new BrowseItem(Node($"t:{topic}#{f.Pointer}"), f.Name, f.Name, NodeClass.Variable, HasChildren: f.Value is JsonObject or JsonArray)));
        }

        if (hash < 0 && _children.TryGetValue(topic, out var sub))
        {
            items.AddRange(sub.Select(TopicItem));
        }

        return items;
    }

    private static List<BrowseItem> MetricChildren(SpbContainer container, string prefix)
    {
        var items = new List<BrowseItem>();
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var metric in container.Metrics.Values.Where(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var rest = metric.Name[prefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                items.Add(new BrowseItem(Node(metric.Key), rest, metric.Name, NodeClass.Variable, HasChildren: false));
            }
            else if (folders.Add(rest[..slash]))
            {
                var folder = prefix + rest[..slash];
                items.Add(new BrowseItem(Node($"f:{container.Group}{Sep}{container.Edge}{Sep}{container.Device}{Sep}{folder}"), rest[..slash], folder, NodeClass.Object));
            }
        }

        return items;
    }

    public static IReadOnlyList<NodeId> PathFromRoot(string id)
    {
        var path = new List<string> { RootId };
        if (id == RootId)
        {
            return [.. path.Select(Node)];
        }

        if (id == TopicsId || id == SparkplugId)
        {
            path.Add(id);
        }
        else if (id.StartsWith("t:", StringComparison.Ordinal))
        {
            path.Add(TopicsId);
            var rest = id[2..];
            var hash = rest.IndexOf('#', StringComparison.Ordinal);
            var topic = hash < 0 ? rest : rest[..hash];
            var levels = topic.Split('/');
            for (var i = 1; i <= levels.Length; i++)
            {
                path.Add("t:" + string.Join('/', levels.Take(i)));
            }

            if (hash >= 0)
            {
                var segments = rest[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 1; i <= segments.Length; i++)
                {
                    path.Add($"t:{topic}#/{string.Join('/', segments.Take(i))}");
                }
            }
        }
        else if (id.Length > 2 && id[1] == ':')
        {
            path.Add(SparkplugId);
            var parts = id[2..].Split(Sep);
            path.Add("g:" + parts[0]);
            if (parts.Length > 1)
            {
                path.Add($"e:{parts[0]}{Sep}{parts[1]}");
            }

            if (parts.Length > 2 && parts[2].Length > 0)
            {
                path.Add($"d:{parts[0]}{Sep}{parts[1]}{Sep}{parts[2]}");
            }

            if (parts.Length > 3)
            {
                var levels = parts[3].Split('/');
                for (var i = 1; i < levels.Length; i++)
                {
                    path.Add($"f:{parts[0]}{Sep}{parts[1]}{Sep}{parts[2]}{Sep}{string.Join('/', levels.Take(i))}");
                }

                if (id[0] is 'm' or 'f')
                {
                    path.Add(id);
                }
            }
        }

        return [.. path.Distinct(StringComparer.Ordinal).Select(Node)];
    }

    // ---------------------------------------------------------------- values and attributes

    /// <summary>The current value of a node as a <see cref="ValueUpdate"/>, or null when it has none (yet).</summary>
    public ValueUpdate? Current(string id)
    {
        lock (_lock)
        {
            if (id.StartsWith("t:", StringComparison.Ordinal))
            {
                var rest = id[2..];
                var hash = rest.IndexOf('#', StringComparison.Ordinal);
                var topic = hash < 0 ? rest : rest[..hash];
                if (!_topics.TryGetValue(topic, out var entry))
                {
                    return null;
                }

                var value = hash < 0 ? entry.Value : Resolve(entry.Value, rest[(hash + 1)..]);
                if (hash >= 0 && value is null && !PointerExists(entry.Value, rest[(hash + 1)..]))
                {
                    return new ValueUpdate(Node(id), nameof(StatusCodes.BadNoData), StatusCodes.BadNoData, entry.ReceivedUtc, entry.ReceivedUtc);
                }

                return Update(id, Plain(value), StatusCodes.Good, entry.ReceivedUtc, entry.ReceivedUtc);
            }

            if (id.StartsWith("m:", StringComparison.Ordinal) && FindMetric(id) is var (container, metric))
            {
                var status = container.Online ? StatusCodes.Good : StatusCodes.BadNoCommunication;
                return Update(id, metric.IsNull ? null : metric.Value, status, metric.SourceUtc, metric.ReceivedUtc);
            }

            return null;
        }
    }

    public IReadOnlyList<AttributeValue> Attributes(string id)
    {
        lock (_lock)
        {
            var result = new List<AttributeValue> { new("NodeId", id) };
            if (id.StartsWith("t:", StringComparison.Ordinal))
            {
                var rest = id[2..];
                var hash = rest.IndexOf('#', StringComparison.Ordinal);
                var topic = hash < 0 ? rest : rest[..hash];
                result.Add(new("Topic", topic));
                if (!_topics.TryGetValue(topic, out var entry))
                {
                    result.Add(new("NodeClass", nameof(NodeClass.Object)));
                    result.Add(new("Payload", "none (topic level without messages)"));
                    return result;
                }

                if (hash >= 0)
                {
                    var field = Resolve(entry.Value, rest[(hash + 1)..]);
                    result.Add(new("NodeClass", nameof(NodeClass.Variable)));
                    result.Add(new("JsonPointer", rest[(hash + 1)..]));
                    result.Add(new("DataType", JsonKind(field)));
                    result.Add(new("Value", Format(Plain(field))));
                }
                else
                {
                    result.Add(new("NodeClass", nameof(NodeClass.Variable)));
                    result.Add(new("PayloadFormat", CloudEvent(entry) ?? Kind(entry.Value)));
                    result.Add(new("PayloadSize", $"{entry.Payload.Length} bytes"));
                    result.Add(new("Retained", entry.Retain ? "Yes" : "No"));
                    result.Add(new("QoS", entry.Qos.ToString(CultureInfo.InvariantCulture)));
                    if (entry.ContentType is { Length: > 0 } contentType)
                    {
                        result.Add(new("ContentType", contentType));
                    }

                    foreach (var (key, value) in entry.UserProperties)
                    {
                        result.Add(new($"UserProperty {key}", value));
                    }

                    result.Add(new("Value", Format(Plain(entry.Value))));
                }

                result.Add(new("Received", entry.ReceivedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)));
                return result;
            }

            var parts = id.Length > 2 ? id[2..].Split(Sep) : [];
            if (id.StartsWith("m:", StringComparison.Ordinal) && FindMetric(id) is var (container, metric))
            {
                result.Add(new("NodeClass", nameof(NodeClass.Variable)));
                result.Add(new("Metric", metric.Name));
                result.Add(new("DataType", SparkplugB.DataTypeName(metric.DataType)));
                if (metric.Alias is { } alias)
                {
                    result.Add(new("Alias", alias.ToString(CultureInfo.InvariantCulture)));
                }

                result.Add(new("Owner", container.Device.Length == 0 ? $"{container.Group}/{container.Edge} (edge node)" : $"{container.Group}/{container.Edge}/{container.Device}"));
                result.Add(new("Online", container.Online ? "Yes" : "No (death received)"));
                result.Add(new("Value", metric.IsNull ? "null" : Format(metric.Value)));
                result.Add(new("SourceTimestamp", metric.SourceUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)));
                return result;
            }

            result.Add(new("NodeClass", nameof(NodeClass.Object)));
            if (id.StartsWith("e:", StringComparison.Ordinal) && parts.Length == 2 && FindEdge(parts[0], parts[1]) is { } edge)
            {
                result.Add(new("SparkplugNode", $"{parts[0]}/{parts[1]}"));
                result.Add(new("Online", edge.Node.Online ? "Yes" : "No (NDEATH received)"));
                result.Add(new("Devices", edge.Devices.Count.ToString(CultureInfo.InvariantCulture)));
                if (edge.Node.Seq is { } seq)
                {
                    result.Add(new("Seq", seq.ToString(CultureInfo.InvariantCulture)));
                }
            }
            else if (id.StartsWith("d:", StringComparison.Ordinal) && parts.Length == 3 && FindContainer(parts[0], parts[1], parts[2]) is { } device)
            {
                result.Add(new("SparkplugDevice", $"{parts[0]}/{parts[1]}/{parts[2]}"));
                result.Add(new("Online", device.Online ? "Yes" : "No (DDEATH received)"));
                result.Add(new("Metrics", device.Metrics.Count.ToString(CultureInfo.InvariantCulture)));
            }
            else if (id == TopicsId)
            {
                result.Add(new("Topics", _topics.Count.ToString(CultureInfo.InvariantCulture)));
                if (DroppedTopics > 0)
                {
                    result.Add(new("Dropped", $"{DroppedTopics} messages of new topics beyond the {MaxTopics} topic limit"));
                }
            }

            return result;
        }
    }

    public static bool IsValidId(string id) =>
        id is RootId or TopicsId or SparkplugId || (id.Length > 2 && id[1] == ':' && id[0] is 't' or 'g' or 'e' or 'd' or 'f' or 'm');

    public static NodeId Node(string id) => new(id, Ns);

    // ---------------------------------------------------------------- helpers

    private SpbEdge? FindEdge(string group, string edge) =>
        _groups.TryGetValue(group, out var edges) && edges.TryGetValue(edge, out var found) ? found : null;

    private SpbContainer? FindContainer(string group, string edge, string device) =>
        FindEdge(group, edge) is not { } e ? null : device.Length == 0 ? e.Node : e.Devices.GetValueOrDefault(device);

    private (SpbContainer Container, SpbMetric Metric)? FindMetric(string id)
    {
        var parts = id[2..].Split(Sep, 4);
        if (parts.Length != 4 || FindContainer(parts[0], parts[1], parts[2]) is not { } c)
        {
            return null;
        }

        return c.Metrics.TryGetValue(parts[3], out var m) || (c.Redirects.TryGetValue(parts[3], out var named) && c.Metrics.TryGetValue(named, out m))
            ? (c, m)
            : null;
    }

    private static ValueUpdate Update(string id, object? value, StatusCode status, DateTime source, DateTime received)
    {
        var variant = new Variant(value);
        return new ValueUpdate(Node(id), StatusCode.IsBad(status) ? status.ToString() : Format(value), status, source, received, ValueFormatter.ToNumeric(variant), value);
    }

    /// <summary>Plain CLR value of a payload or JSON node (JSON containers become compact JSON text).</summary>
    internal static object? Plain(object? value) => value switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonNode node => node.ToJsonString(),
        _ => value,
    };

    private static string Format(object? value) => value switch
    {
        null => "null",
        string s => s,
        _ => ValueFormatter.Format(new Variant(value)),
    };

    /// <summary>
    /// CloudEvents 1.0 over MQTT: structured mode is a JSON envelope with <c>specversion</c>; binary mode carries the
    /// attributes as MQTT 5 user properties and the event data as the payload.
    /// </summary>
    private static string? CloudEvent(TopicEntry entry)
    {
        if (entry.Value is JsonObject obj && obj.ContainsKey("specversion") && obj.ContainsKey("type") && obj.ContainsKey("source"))
        {
            return $"CloudEvent (structured) · {obj["type"]}";
        }

        var type = entry.UserProperties.FirstOrDefault(p => p.Key is "type" or "ce_type" or "ce-type").Value;
        return entry.UserProperties.Any(p => p.Key is "specversion" or "ce_specversion" or "ce-specversion")
            ? $"CloudEvent (binary) · {type}"
            : null;
    }

    private static string Kind(object? value) => value switch
    {
        JsonNode => "JSON",
        double => "Number",
        bool => "Boolean",
        byte[] => "Binary",
        _ => "Text",
    };

    private static string JsonKind(JsonNode? node) => node switch
    {
        JsonObject => "JSON object",
        JsonArray => "JSON array",
        JsonValue v when v.TryGetValue<double>(out _) => "Number",
        JsonValue v when v.TryGetValue<bool>(out _) => "Boolean",
        null => "null",
        _ => "String",
    };

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static JsonNode? Resolve(object? value, string pointer)
    {
        if (value is not JsonNode node)
        {
            return null;
        }

        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject obj => obj[Unescape(segment)]!,
                JsonArray arr when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < arr.Count => arr[i]!,
                _ => null!,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static bool PointerExists(object? value, string pointer)
    {
        if (value is not JsonNode node)
        {
            return false;
        }

        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (node)
            {
                case JsonObject obj when obj.ContainsKey(Unescape(segment)):
                    node = obj[Unescape(segment)]!;
                    if (node is null)
                    {
                        return true;
                    }

                    break;
                case JsonArray arr when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < arr.Count:
                    node = arr[i]!;
                    if (node is null)
                    {
                        return true;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private sealed class TopicEntry(string topic)
    {
        public string Topic { get; } = topic;

        public byte[] Payload { get; set; } = [];

        public object? Value { get; set; }

        public bool Retain { get; set; }

        public int Qos { get; set; }

        public string? ContentType { get; set; }

        public IReadOnlyList<KeyValuePair<string, string>> UserProperties { get; set; } = [];

        public DateTime ReceivedUtc { get; set; }
    }

    private class SpbContainer(string group, string edge, string device)
    {
        public string Group { get; } = group;

        public string Edge { get; } = edge;

        /// <summary>Empty for the edge node's own metrics.</summary>
        public string Device { get; } = device;

        public bool Online { get; set; }

        public DateTime StateChangedUtc { get; set; }

        public ulong? Seq { get; set; }

        public Dictionary<string, SpbMetric> Metrics { get; } = new(StringComparer.Ordinal);

        /// <summary>"alias 101" placeholder → metric name, learned from a birth after the placeholder was created.</summary>
        public Dictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SpbEdge(string group, string name)
    {
        public string Group { get; } = group;

        public string Name { get; } = name;

        public SpbContainer Node { get; } = new(group, name, string.Empty);

        public SortedDictionary<string, SpbContainer> Devices { get; } = new(StringComparer.Ordinal);

        public Dictionary<ulong, string> Aliases { get; } = [];

        public SpbContainer Device(string device)
        {
            if (!Devices.TryGetValue(device, out var found))
            {
                Devices[device] = found = new SpbContainer(Group, Name, device);
            }

            return found;
        }
    }

    private sealed class SpbMetric(string key, string name)
    {
        public string Key { get; } = key;

        public string Name { get; } = name;

        public uint DataType { get; set; }

        public ulong? Alias { get; set; }

        public object? Value { get; set; }

        public bool IsNull { get; set; }

        public DateTime SourceUtc { get; set; }

        public DateTime ReceivedUtc { get; set; }
    }
}
