using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Opc.Ua;
using Opc.Ua.Encoders;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.OpcUa;

/// <summary>Converts OPC UA values to JSON keeping their type: numbers stay numbers, booleans booleans, arrays arrays, structures objects.</summary>
public static class ValueJson
{
    private const int MaxDepth = 16;

    public static JsonNode? ToJson(object? value) => Convert(value, 0);

    public static string TypeName(object? value) => value switch
    {
        null => "Null",
        DeviceArray a => $"{a.ElementType}[]",
        Matrix m => $"{TypeName(m.Elements.Length > 0 ? m.Elements.GetValue(0) : null)}[{string.Join(',', m.Dimensions)}]",
        IConvertableToMatrix m => MatrixTypeName(m.ToMatrix(BuiltInType.Null)),
        ByteString => "ByteString",
        Array a when a.GetType().GetElementType() == typeof(byte) => "ByteString",
        Array a => $"{TypeName(a.Length > 0 ? a.GetValue(0) : null)}[]",
        ExtensionObject eo when eo.TryGetValue(out IEncodeable? e) => EncodeableTypeName(e),
        ExtensionObject => "ExtensionObject",
        Uuid or Guid => "Guid",
        DateTimeUtc => "DateTime",
        float => "Float",
        _ => value.GetType().Name,
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
            Variant v => Convert(v.AsBoxedObject(), depth),
            bool b => JsonValue.Create(b),
            sbyte or byte or short or ushort or int or uint or long => JsonValue.Create(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong u => JsonValue.Create(u),
            float f => Number(f),
            double d => Number(d),
            decimal m => JsonValue.Create(m),
            string s => JsonValue.Create(s),
            DateTime dt => JsonValue.Create(dt == DateTime.MinValue ? null : dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            DateTimeUtc dt => Convert(dt.ToDateTime(), depth),
            Uuid g => JsonValue.Create(((Guid)g).ToString()),
            Guid g => JsonValue.Create(g.ToString()),
            DeviceArray array => new JsonArray([.. array.Values.Cast<object?>().Select(item => Convert(item, depth + 1))]),
            Array bytes when bytes.GetType().GetElementType() == typeof(byte) => JsonValue.Create(System.Convert.ToBase64String((byte[])bytes)),
            ByteString bytes => bytes.IsNull ? null : JsonValue.Create(bytes.ToBase64()),
            LocalizedText lt => JsonValue.Create(lt.Text),
            QualifiedName qn => JsonValue.Create(qn.ToString()),
            NodeId or ExpandedNodeId or StatusCode => JsonValue.Create(value.ToString()),
            System.Xml.XmlElement xml => JsonValue.Create(xml.OuterXml),
            Matrix matrix => MatrixToJson(matrix, depth),
            IConvertableToMatrix matrix => MatrixToJson(matrix.ToMatrix(BuiltInType.Null), depth),
            ExtensionObject eo => ExtensionObjectToJson(eo, depth),
            DataValue dv => Convert(dv.WrappedValue.AsBoxedObject(), depth + 1),
            IEncodeable encodeable => StructToJson(encodeable, depth),
            Enum e => JsonValue.Create(e.ToString()),
            IEnumerable list => new JsonArray([.. list.Cast<object?>().Select(i => Convert(i, depth + 1))]),
            _ => JsonValue.Create(value.ToString()),
        };
    }

    private static JsonValue Number(double d) =>
        double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture))!;

    private static JsonNode? ExtensionObjectToJson(ExtensionObject extensionObject, int depth)
    {
        if (extensionObject.TryGetAsBinary(out ByteString bytes))
        {
            return new JsonObject
            {
                ["typeId"] = extensionObject.TypeId.ToString(),
                ["body"] = bytes.IsNull ? null : System.Convert.ToBase64String(bytes.Memory.ToArray()),
            };
        }

        return extensionObject.TryGetValue(out IEncodeable? encodeable)
            ? Convert(encodeable, depth + 1)
            : new JsonObject { ["typeId"] = extensionObject.TypeId.ToString() };
    }

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

    private static string MatrixTypeName(Matrix matrix) =>
        $"{TypeName(matrix.Elements.Length > 0 ? matrix.Elements.GetValue(0) : null)}[{string.Join(',', matrix.Dimensions)}]";

    private static string EncodeableTypeName(IEncodeable encodeable) =>
        encodeable is Structure structure ? structure.XmlName.Name : encodeable.GetType().Name;

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
