using System.Globalization;

namespace MachineDataBrowser.Core;

public static class ValueFormatter
{
    private const int MaxArrayItems = 32;

    public static string Format(object? value) => FormatObject(value);

    public static double? ToNumeric(object? value) => value switch
    {
        bool b => b ? 1 : 0,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string FormatObject(object? value) => value switch
    {
        null => "null",
        string s => s,
        DeviceArray deviceArray => FormatArray(deviceArray.Values),
        Array bytes when bytes.GetType().GetElementType() == typeof(byte) =>
            $"ByteString[{bytes.Length}] {Convert.ToHexString(((byte[])bytes).AsSpan(0, Math.Min(bytes.Length, 32)))}{(bytes.Length > 32 ? "…" : string.Empty)}",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
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
}
