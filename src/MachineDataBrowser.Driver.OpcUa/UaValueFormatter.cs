using System.Collections;
using Opc.Ua;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.OpcUa;

public static class UaValueFormatter
{
    public static Variant ToVariant(object? value) => VariantHelper.CastFromWithReflectionFallback(value);

    public static string Format(Variant value) => FormatObject(value.AsBoxedObject());

    public static string Format(object? value) => FormatObject(value);

    public static double? ToNumeric(Variant value) => ValueFormatter.ToNumeric(value.AsBoxedObject());

    private static string FormatObject(object? value)
    {
        if (ContainsDecodedStructure(value))
        {
            return ValueJson.ToJson(value)?.ToJsonString() ?? ValueFormatter.Format(value);
        }

        return value switch
        {
            ByteString bytes => ValueFormatter.Format(bytes.Memory.ToArray()),
            DateTimeUtc dt => ValueFormatter.Format(dt.ToDateTime()),
            LocalizedText text => text.Text ?? string.Empty,
            QualifiedName name => name.ToString(),
            ExtensionObject extension => FormatExtensionObject(extension),
            Matrix matrix => FormatMatrix(matrix),
            IConvertableToMatrix matrix => FormatMatrix(matrix.ToMatrix(BuiltInType.Null)),
            _ => ValueFormatter.Format(value),
        };
    }

    private static bool ContainsDecodedStructure(object? value, int depth = 0) => depth <= 16 && value switch
    {
        ExtensionObject extension => extension.TryGetValue(out IEncodeable? _),
        IEncodeable => true,
        _ when value is not null && ValueJson.TryGetArray(value, out var array) =>
            array.Cast<object?>().Any(item => ContainsDecodedStructure(item, depth + 1)),
        IEnumerable values when value is not string => values.Cast<object?>().Any(item => ContainsDecodedStructure(item, depth + 1)),
        _ => false,
    };

    private static string FormatExtensionObject(ExtensionObject extension)
    {
        if (extension.TryGetValue(out IEncodeable? encodeable))
        {
            return encodeable.ToString() ?? encodeable.GetType().Name;
        }

        return extension.TryGetAsBinary(out ByteString raw)
            ? $"ExtensionObject {extension.TypeId} (undecoded, {raw.Length} bytes)"
            : extension.ToString() ?? "ExtensionObject";
    }

    private static string FormatMatrix(Matrix matrix) =>
        $"Matrix[{string.Join('x', matrix.Dimensions)}] {ValueFormatter.Format(matrix.Elements)}";
}
