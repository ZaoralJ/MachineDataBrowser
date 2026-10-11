using System.Globalization;
using Opc.Ua;

namespace MachineDataBrowser.Core;

public static class ValueFormatter
{
    private const int MaxArrayItems = 32;

    internal static Variant ToVariant(object? value) =>
        value is null ? Variant.Null : Variant.From((dynamic)value);

    public static string Format(Variant value) => FormatObject(value.AsBoxedObject());

    public static double? ToNumeric(Variant value) => value.AsBoxedObject() switch
    {
        bool b => b ? 1 : 0,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value.AsBoxedObject(), CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string FormatObject(object? value) => value switch
    {
        null => "null",
        string s => s,
        byte[] bytes => $"ByteString[{bytes.Length}] {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32)))}{(bytes.Length > 32 ? "…" : string.Empty)}",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        LocalizedText lt => lt.Text ?? string.Empty,
        QualifiedName qn => qn.ToString(),
        ExtensionObject eo => FormatExtensionObject(eo),
        Matrix m => $"Matrix[{string.Join('x', m.Dimensions)}] {FormatArray(m.Elements)}",
        Array array => FormatArray(array),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatArray(Array array)
    {
        var items = array.Cast<object?>().Take(MaxArrayItems).Select(FormatObject);
        var suffix = array.Length > MaxArrayItems ? $", … (+{array.Length - MaxArrayItems})" : string.Empty;
        return $"[{string.Join(", ", items)}{suffix}]";
    }

    private static string FormatExtensionObject(ExtensionObject eo)
    {
        if (eo.TryGetValue(out IEncodeable? encodeable))
        {
            return encodeable.ToString() ?? encodeable.GetType().Name;
        }

        return eo.TryGetAsBinary(out ByteString raw)
            ? $"ExtensionObject {eo.TypeId} (undecoded, {raw.Length} bytes)"
            : eo.ToString() ?? "ExtensionObject";
    }
}
