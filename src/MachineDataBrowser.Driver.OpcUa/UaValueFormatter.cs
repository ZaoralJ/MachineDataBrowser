using Opc.Ua;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.OpcUa;

public static class UaValueFormatter
{
    public static Variant ToVariant(object? value) => VariantHelper.CastFromWithReflectionFallback(value);

    public static string Format(Variant value) => FormatObject(value.AsBoxedObject());

    public static double? ToNumeric(Variant value) => ValueFormatter.ToNumeric(value.AsBoxedObject());

    private static string FormatObject(object? value) => value switch
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
