using System.Globalization;

namespace MachineDataBrowser.Core;

public static class ValueFormatter
{
    private const int MaxArrayItems = 32;

    public static string Format(object? value) => FormatObject(value, MaxArrayItems);

    /// <summary>Formats a value without shortening arrays or byte strings, for an explicit full-value view.</summary>
    public static string FormatFull(object? value) => FormatObject(value, int.MaxValue);

    public static double? ToNumeric(object? value) => value switch
    {
        bool b => b ? 1 : 0,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string FormatObject(object? value, int maxArrayItems) => value switch
    {
        null => "null",
        string s => s,
        DeviceArray deviceArray => FormatArray(deviceArray.Values, maxArrayItems),
        Array bytes when bytes.Rank == 1 && bytes.GetType().GetElementType() == typeof(byte) =>
            $"ByteString[{bytes.Length}] {Convert.ToHexString(((byte[])bytes).AsSpan(0, Math.Min(bytes.Length, maxArrayItems)))}{(bytes.Length > maxArrayItems ? "…" : string.Empty)}",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        Array array => FormatArray(array, maxArrayItems),
        // OPC UA wraps arrays in its own types (ArrayOf<T>); they convert with ToArray().
        _ when ToArray(value) is { } converted => FormatArray(converted, maxArrayItems),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.MethodInfo?> ToArrayMethods = new();

    private static Array? ToArray(object value)
    {
        var method = ToArrayMethods.GetOrAdd(value.GetType(), type => type.IsPrimitive || type == typeof(decimal)
            ? null
            : type.GetMethod("ToArray", Type.EmptyTypes) is { } m && m.ReturnType.IsArray ? m : null);
        return method?.Invoke(value, null) as Array;
    }

    private static string FormatArray(Array array, int maxArrayItems)
    {
        var items = array.Cast<object?>().Take(maxArrayItems).Select(item => FormatObject(item, maxArrayItems));
        var suffix = array.Length > maxArrayItems ? $", … (+{array.Length - maxArrayItems})" : string.Empty;
        return $"[{string.Join(", ", items)}{suffix}]";
    }
}
