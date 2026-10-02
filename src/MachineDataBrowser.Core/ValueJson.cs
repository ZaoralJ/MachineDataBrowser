using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Opc.Ua;

namespace MachineDataBrowser.Core;

/// <summary>Converts OPC UA values to JSON keeping their type: numbers stay numbers, booleans booleans, arrays arrays, structures objects.</summary>
public static class ValueJson
{
    private const int MaxDepth = 16;

    public static JsonNode? ToJson(object? value) => Convert(value, 0);

    public static string TypeName(object? value) => value switch
    {
        null => "Null",
        Matrix m => $"{TypeName(m.Elements.Length > 0 ? m.Elements.GetValue(0) : null)}[{string.Join(',', m.Dimensions)}]",
        byte[] => "ByteString",
        Array a => $"{TypeName(a.Length > 0 ? a.GetValue(0) : null)}[]",
        ExtensionObject { Body: IEncodeable e } => e.GetType().Name,
        ExtensionObject => "ExtensionObject",
        Uuid => "Guid",
        _ => new Variant(value).TypeInfo?.BuiltInType.ToString() ?? value.GetType().Name,
    };

    private static JsonNode? Convert(object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            return JsonValue.Create(value?.ToString());
        }

        return value switch
        {
            null => null,
            Variant v => Convert(v.Value, depth),
            bool b => JsonValue.Create(b),
            sbyte or byte or short or ushort or int or uint or long => JsonValue.Create(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong u => JsonValue.Create(u),
            float f => Number(f),
            double d => Number(d),
            decimal m => JsonValue.Create(m),
            string s => JsonValue.Create(s),
            DateTime dt => JsonValue.Create(dt == DateTime.MinValue ? null : dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            Uuid g => JsonValue.Create(((Guid)g).ToString()),
            Guid g => JsonValue.Create(g.ToString()),
            byte[] bytes => JsonValue.Create(System.Convert.ToBase64String(bytes)),
            LocalizedText lt => JsonValue.Create(lt.Text),
            QualifiedName qn => JsonValue.Create(qn.ToString()),
            NodeId or ExpandedNodeId or StatusCode => JsonValue.Create(value.ToString()),
            System.Xml.XmlElement xml => JsonValue.Create(xml.OuterXml),
            Matrix matrix => MatrixToJson(matrix, depth),
            ExtensionObject eo => eo.Body is byte[] raw
                ? new JsonObject { ["typeId"] = eo.TypeId?.ToString(), ["body"] = System.Convert.ToBase64String(raw) }
                : Convert(eo.Body, depth + 1),
            DataValue dv => Convert(dv.Value, depth + 1),
            IEncodeable encodeable => StructToJson(encodeable, depth),
            Enum e => JsonValue.Create(e.ToString()),
            IEnumerable list => new JsonArray([.. list.Cast<object?>().Select(i => Convert(i, depth + 1))]),
            _ => JsonValue.Create(value.ToString()),
        };
    }

    private static JsonValue Number(double d) =>
        double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture))!;

    private static JsonArray MatrixToJson(Matrix matrix, int depth)
    {
        var flat = matrix.Elements.Cast<object?>().ToList();
        return Build(0, 0).AsArray();

        JsonNode Build(int dim, int offset)
        {
            var size = matrix.Dimensions[dim];
            var stride = matrix.Dimensions.Skip(dim + 1).Aggregate(1, (a, b) => a * b);
            var array = new JsonArray();
            for (var i = 0; i < size; i++)
            {
                array.Add(dim == matrix.Dimensions.Length - 1
                    ? Convert(flat[offset + i], depth + 1)
                    : Build(dim + 1, offset + (i * stride)));
            }

            return array;
        }
    }

    private static readonly HashSet<string> SkippedProperties =
        ["TypeId", "BinaryEncodingId", "XmlEncodingId", "JsonEncodingId", "XmlName", "StructureType"];

    private static JsonObject StructToJson(IEncodeable encodeable, int depth)
    {
        var result = new JsonObject();
        foreach (var property in encodeable.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || SkippedProperties.Contains(property.Name) || !property.CanRead)
            {
                continue;
            }

            result[property.Name] = Convert(property.GetValue(encodeable), depth + 1);
        }

        return result;
    }
}
