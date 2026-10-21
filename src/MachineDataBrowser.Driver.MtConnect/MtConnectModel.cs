using System.Globalization;
using System.Xml.Linq;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.MtConnect;

/// <summary>A component (or device) of the probe model.</summary>
internal sealed class MtcComponent(string id, string element, string? name, string? uuid, string? description, MtcComponent? parent)
{
    public string Id { get; } = id;

    /// <summary>The component type: <c>Device</c>, <c>Axes</c>, <c>Linear</c>, …</summary>
    public string Element { get; } = element;

    public string? Name { get; } = name;

    public string? Uuid { get; } = uuid;

    public string? Description { get; } = description;

    public MtcComponent? Parent { get; } = parent;

    public string DisplayName { get; set; } = name ?? element;

    public List<MtcComponent> Components { get; } = [];

    public List<MtcDataItem> DataItems { get; } = [];

    public bool IsDevice => Parent is null;
}

internal sealed record MtcDataItem(
    string Id,
    string? Name,
    string Category,
    string Type,
    string? SubType,
    string? Units,
    string? NativeUnits,
    string? Representation,
    string? CoordinateSystem,
    MtcComponent Component)
{
    public string DisplayName { get; set; } = Name ?? Id;

    public bool IsCondition => Category.Equals("CONDITION", StringComparison.OrdinalIgnoreCase);

    public bool IsSample => Category.Equals("SAMPLE", StringComparison.OrdinalIgnoreCase);

    public bool IsTimeSeries => string.Equals(Representation, "TIME_SERIES", StringComparison.OrdinalIgnoreCase);

    public bool IsDataSet => Representation is { } r && (r.Equals("DATA_SET", StringComparison.OrdinalIgnoreCase) || r.Equals("TABLE", StringComparison.OrdinalIgnoreCase));

    public bool IsVector => IsTimeSeries || (Units?.EndsWith("_3D", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary><c>POSITION:ACTUAL</c>, the way MTConnect names a data item's kind.</summary>
    public string TypeName => SubType is null ? Type : $"{Type}:{SubType}";

    /// <summary>The neutral type the value is decoded to.</summary>
    public string DataType =>
        IsCondition ? "Condition"
        : IsDataSet ? "DataSet"
        : IsSample ? IsVector ? "Double[]" : "Double"
        : IsIntegerEvent ? "Int64"
        : "String";

    public bool IsIntegerEvent => !IsSample && !IsCondition && !IsDataSet && Type.ToUpperInvariant() is "PART_COUNT" or "LINE_NUMBER" or "LINE" or "PALLET_ID_NUMBER";
}

/// <summary>The agent's device model from <c>/probe</c>.</summary>
internal sealed class MtcModel
{
    public required IReadOnlyList<MtcComponent> Devices { get; init; }

    public required IReadOnlyDictionary<string, MtcComponent> Components { get; init; }

    public required IReadOnlyDictionary<string, MtcDataItem> DataItems { get; init; }

    public required MtcHeader Header { get; init; }

    public static MtcModel Parse(XDocument document)
    {
        var root = MtcXml.RequireRoot(document, "MTConnectDevices");
        var header = MtcHeader.Parse(root);
        var components = new Dictionary<string, MtcComponent>(StringComparer.Ordinal);
        var items = new Dictionary<string, MtcDataItem>(StringComparer.Ordinal);
        var devices = new List<MtcComponent>();
        var devicesElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Devices");
        foreach (var element in devicesElement?.Elements() ?? [])
        {
            if (Read(element, null, components, items) is { } device)
            {
                devices.Add(device);
            }
        }

        Disambiguate(devices.Select(d => (Action<string>)(n => d.DisplayName = n)), devices.Select(d => (d.DisplayName, d.Id)));
        return new MtcModel { Devices = devices, Components = components, DataItems = items, Header = header };
    }

    private static MtcComponent? Read(XElement element, MtcComponent? parent, Dictionary<string, MtcComponent> components, Dictionary<string, MtcDataItem> items)
    {
        var id = (string?)element.Attribute("id");
        if (id is null)
        {
            return null;
        }

        var description = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Description")?.Value.Trim();
        var component = new MtcComponent(id, element.Name.LocalName, (string?)element.Attribute("name"), (string?)element.Attribute("uuid"),
            string.IsNullOrEmpty(description) ? null : description, parent);
        components[id] = component;

        foreach (var di in element.Elements().Where(e => e.Name.LocalName == "DataItems").Elements().Where(e => e.Name.LocalName == "DataItem"))
        {
            var itemId = (string?)di.Attribute("id");
            if (itemId is null)
            {
                continue;
            }

            var item = new MtcDataItem(
                itemId,
                (string?)di.Attribute("name"),
                (string?)di.Attribute("category") ?? "EVENT",
                (string?)di.Attribute("type") ?? "UNKNOWN",
                (string?)di.Attribute("subType"),
                (string?)di.Attribute("units"),
                (string?)di.Attribute("nativeUnits"),
                (string?)di.Attribute("representation"),
                (string?)di.Attribute("coordinateSystem"),
                component);
            component.DataItems.Add(item);
            items[itemId] = item;
        }

        foreach (var child in element.Elements().Where(e => e.Name.LocalName == "Components").Elements())
        {
            if (Read(child, component, components, items) is { } sub)
            {
                component.Components.Add(sub);
            }
        }

        // Paths are built from display names, so siblings must not share one.
        var children = component.Components.Select(c => (Action<string>)(n => c.DisplayName = n))
            .Concat(component.DataItems.Select(d => (Action<string>)(n => d.DisplayName = n)));
        var names = component.Components.Select(c => (c.DisplayName, c.Id)).Concat(component.DataItems.Select(d => (d.DisplayName, d.Id)));
        Disambiguate(children, names);
        return component;
    }

    private static void Disambiguate(IEnumerable<Action<string>> setters, IEnumerable<(string Name, string Id)> names)
    {
        var list = names.ToList();
        var duplicates = list.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var set in setters)
        {
            var (name, id) = list[index++];
            if (duplicates.Contains(name))
            {
                set($"{name} ({id})");
            }
        }
    }
}

internal sealed record MtcHeader(long InstanceId, long FirstSequence, long LastSequence, long NextSequence, long BufferSize, string? Version, string? Sender)
{
    public static MtcHeader Parse(XElement root)
    {
        var header = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
        return new MtcHeader(
            Long(header, "instanceId"),
            Long(header, "firstSequence"),
            Long(header, "lastSequence"),
            Long(header, "nextSequence"),
            Long(header, "bufferSize"),
            (string?)header?.Attribute("version"),
            (string?)header?.Attribute("sender"));

        static long Long(XElement? e, string name) =>
            long.TryParse((string?)e?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

/// <summary>One observation from <c>/current</c> or <c>/sample</c>.</summary>
internal sealed record MtcObservation(string DataItemId, long Sequence, DateTime Timestamp, object? Value, DeviceStatusCode Status);

internal sealed record MtcStreams(MtcHeader Header, IReadOnlyList<MtcObservation> Observations);

internal static class MtcXml
{
    public const string Unavailable = "UNAVAILABLE";

    public static XElement RequireRoot(XDocument document, string expected)
    {
        var root = document.Root ?? throw new DeviceClientException(DeviceStatusCodes.BadDecodingError, "Empty response from the MTConnect agent.");
        if (root.Name.LocalName == "MTConnectError")
        {
            throw ToException(root);
        }

        if (root.Name.LocalName != expected)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadDecodingError, $"Expected {expected} from the MTConnect agent, got {root.Name.LocalName}.");
        }

        return root;
    }

    public static DeviceClientException ToException(XElement error)
    {
        var first = error.Descendants().FirstOrDefault(e => e.Name.LocalName == "Error");
        var code = (string?)first?.Attribute("errorCode") ?? "UNKNOWN";
        var text = first?.Value.Trim() is { Length: > 0 } t ? t : code;
        return new DeviceClientException(code switch
        {
            "NO_DEVICE" => DeviceStatusCodes.BadNodeIdUnknown,
            "OUT_OF_RANGE" => DeviceStatusCodes.BadOutOfRange,
            "INVALID_XPATH" or "INVALID_REQUEST" or "UNSUPPORTED" or "INVALID_URI" or "QUERY_ERROR" => DeviceStatusCodes.BadNodeIdInvalid,
            "TOO_MANY" => DeviceStatusCodes.BadOutOfRange,
            _ => DeviceStatusCodes.Bad,
        }, $"MTConnect {code}: {text}");
    }

    public static MtcStreams ParseStreams(XDocument document, IReadOnlyDictionary<string, MtcDataItem> items)
    {
        var root = RequireRoot(document, "MTConnectStreams");
        var header = MtcHeader.Parse(root);
        var observations = new List<MtcObservation>();
        var streams = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Streams");
        foreach (var componentStream in streams?.Descendants().Where(e => e.Name.LocalName == "ComponentStream") ?? [])
        {
            foreach (var group in componentStream.Elements())
            {
                var category = group.Name.LocalName;
                if (category is not ("Samples" or "Events" or "Condition"))
                {
                    continue;
                }

                foreach (var element in group.Elements())
                {
                    var id = (string?)element.Attribute("dataItemId");
                    if (id is null)
                    {
                        continue;
                    }

                    items.TryGetValue(id, out var item);
                    observations.Add(Decode(element, item, category == "Condition"));
                }
            }
        }

        observations.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        return new MtcStreams(header, observations);
    }

    internal static MtcObservation Decode(XElement element, MtcDataItem? item, bool isCondition)
    {
        var id = (string)element.Attribute("dataItemId")!;
        _ = long.TryParse((string?)element.Attribute("sequence"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence);
        var timestamp = DateTime.TryParse((string?)element.Attribute("timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.UtcNow;

        if (isCondition)
        {
            var level = element.Name.LocalName;
            if (level == "Unavailable")
            {
                return new MtcObservation(id, sequence, timestamp, null, DeviceStatusCodes.BadNoData);
            }

            var text = element.Value.Trim();
            var code = (string?)element.Attribute("nativeCode");
            var message = level;
            if (text.Length > 0)
            {
                message += ": " + text;
            }

            if (code is { Length: > 0 })
            {
                message += $" [{code}]";
            }

            return new MtcObservation(id, sequence, timestamp, message, DeviceStatusCodes.Good);
        }

        var raw = element.Value.Trim();
        if (raw == Unavailable)
        {
            return new MtcObservation(id, sequence, timestamp, null, DeviceStatusCodes.BadNoData);
        }

        var elementName = element.Name.LocalName;
        var entries = element.Elements().Where(e => e.Name.LocalName == "Entry").ToList();
        if (entries.Count > 0 || item?.IsDataSet == true || elementName.EndsWith("DataSet", StringComparison.Ordinal) || elementName.EndsWith("Table", StringComparison.Ordinal))
        {
            var text = string.Join("; ", entries.Select(e => $"{(string?)e.Attribute("key")}={DataSetValue(e)}"));
            return new MtcObservation(id, sequence, timestamp, text, DeviceStatusCodes.Good);
        }

        var vector = item?.IsVector ?? elementName.EndsWith("TimeSeries", StringComparison.Ordinal);
        if (item?.IsSample ?? false)
        {
            var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (vector || parts.Length > 1)
            {
                var values = new double[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    {
                        return new MtcObservation(id, sequence, timestamp, raw, DeviceStatusCodes.Good);
                    }
                }

                return new MtcObservation(id, sequence, timestamp, new DeviceArray("Double", values), DeviceStatusCodes.Good);
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? new MtcObservation(id, sequence, timestamp, number, DeviceStatusCodes.Good)
                : new MtcObservation(id, sequence, timestamp, raw, DeviceStatusCodes.Good);
        }

        if (item?.IsIntegerEvent == true && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return new MtcObservation(id, sequence, timestamp, integer, DeviceStatusCodes.Good);
        }

        return new MtcObservation(id, sequence, timestamp, raw, DeviceStatusCodes.Good);
    }

    /// <summary>Table rows hold cells; data set entries hold text.</summary>
    private static string DataSetValue(XElement entry)
    {
        var cells = entry.Elements().Where(e => e.Name.LocalName == "Cell").ToList();
        return cells.Count == 0
            ? entry.Value.Trim()
            : "{" + string.Join(", ", cells.Select(c => $"{(string?)c.Attribute("key")}={c.Value.Trim()}")) + "}";
    }

    /// <summary>Engineering unit for display (<c>MILLIMETER</c> → <c>mm</c>); unknown units as sent.</summary>
    public static string? DisplayUnit(string? units) => units?.ToUpperInvariant() switch
    {
        null => null,
        "MILLIMETER" or "MILLIMETER_3D" => "mm",
        "MILLIMETER/SECOND" => "mm/s",
        "MILLIMETER/SECOND^2" => "mm/s²",
        "MILLIMETER/REVOLUTION" => "mm/rev",
        "PERCENT" => "%",
        "CELSIUS" => "°C",
        "REVOLUTION/MINUTE" => "rpm",
        "DEGREE" or "DEGREE_3D" => "°",
        "DEGREE/SECOND" => "°/s",
        "DEGREE/SECOND^2" => "°/s²",
        "SECOND" => "s",
        "WATT" => "W",
        "WATT_SECOND" => "Ws",
        "VOLT" or "VOLT_AC" or "VOLT_DC" => "V",
        "VOLT_AMPERE" => "VA",
        "AMPERE" => "A",
        "NEWTON" => "N",
        "NEWTON_METER" => "Nm",
        "PASCAL" => "Pa",
        "HERTZ" => "Hz",
        "KILOGRAM" => "kg",
        "GRAM" => "g",
        "LITER" => "l",
        "LITER/SECOND" => "l/s",
        "MICRO_RADIAN" => "µrad",
        "OHM" => "Ω",
        "COUNT" => string.Empty,
        _ => units,
    };
}
