using System.Globalization;
using System.Text;

namespace MachineDataBrowser.Core;

public enum ValueFormat
{
    /// <summary>As the device sends it (scaled when a gain or offset is set).</summary>
    Auto,

    /// <summary>A fixed number of decimals.</summary>
    Decimals,

    /// <summary>Integers as hexadecimal, padded to their type's width (0x00FF).</summary>
    Hex,

    /// <summary>Integers as binary in groups of four bits.</summary>
    Binary,

    /// <summary>Integers as the numbers of their set bits (status and alarm words).</summary>
    Bits,
}

/// <summary>
/// How a watched value is shown: format, scaling of raw values (value × gain + offset) and a unit. Display only:
/// recordings, exports, filters and snapshots keep the device's value.
/// </summary>
public sealed record ValueDisplay
{
    public static ValueDisplay Default { get; } = new();

    public ValueFormat Format { get; init; }

    public int Decimals { get; init; } = 2;

    public double Gain { get; init; } = 1;

    public double Offset { get; init; }

    /// <summary>Unit shown after numbers; null uses the server's engineering unit, empty shows none.</summary>
    public string? Unit { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => this == Default;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsScaled => Gain != 1 || Offset != 0;

    /// <summary>
    /// The value as shown. <paramref name="raw"/> is the decoded value; <paramref name="text"/> is the device's own
    /// formatting, used as is for anything that is not a single number.
    /// </summary>
    public string Apply(object? raw, string text, string? serverUnit = null)
    {
        var unit = Unit ?? serverUnit;
        var shown = FormatNumber(raw);
        return shown is null ? text : string.IsNullOrEmpty(unit) ? shown : $"{shown} {unit}";
    }

    private string? FormatNumber(object? raw)
    {
        if (raw is null or bool or string || raw is not IConvertible || !IsNumber(raw))
        {
            return null;
        }

        if (Format is ValueFormat.Hex or ValueFormat.Binary or ValueFormat.Bits && IntegerBits(raw) is { } bits)
        {
            var value = ToUInt64(raw, bits);
            return Format switch
            {
                ValueFormat.Hex => "0x" + value.ToString("X" + (bits / 4).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                ValueFormat.Binary => Binary(value, bits),
                _ => SetBits(value, bits),
            };
        }

        var number = Convert.ToDouble(raw, CultureInfo.InvariantCulture) * Gain + Offset;
        if (Format == ValueFormat.Decimals)
        {
            return number.ToString("F" + Math.Clamp(Decimals, 0, 15).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        // Auto: integers stay integers unless scaled; doubles use the shortest round-trip form.
        return !IsScaled && IntegerBits(raw) is not null
            ? Convert.ToString(raw, CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool IsNumber(object raw) => raw is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static int? IntegerBits(object raw) => raw switch
    {
        sbyte or byte => 8,
        short or ushort => 16,
        int or uint => 32,
        long or ulong => 64,
        _ => null,
    };

    // Two's complement for signed values, so -1 in an Int16 shows as 0xFFFF.
    private static ulong ToUInt64(object raw, int bits) => raw switch
    {
        sbyte v => (byte)v,
        short v => (ushort)v,
        int v => (uint)v,
        long v => (ulong)v,
        _ => Convert.ToUInt64(raw, CultureInfo.InvariantCulture),
    } & (bits == 64 ? ulong.MaxValue : (1UL << bits) - 1);

    private static string Binary(ulong value, int bits)
    {
        var text = new StringBuilder(bits + (bits / 4));
        for (var bit = bits - 1; bit >= 0; bit--)
        {
            text.Append(((value >> bit) & 1) == 1 ? '1' : '0');
            if (bit % 4 == 0 && bit > 0)
            {
                text.Append(' ');
            }
        }

        return text.ToString();
    }

    private static string SetBits(ulong value, int bits)
    {
        var set = Enumerable.Range(0, bits).Where(bit => ((value >> bit) & 1) == 1).ToList();
        return set.Count == 0 ? "no bits set" : "bits " + string.Join(", ", set);
    }

    public string Describe()
    {
        var parts = new List<string>(3);
        switch (Format)
        {
            case ValueFormat.Decimals:
                parts.Add($"{Decimals} decimals");
                break;
            case ValueFormat.Hex or ValueFormat.Binary or ValueFormat.Bits:
                parts.Add(Format.ToString().ToLowerInvariant());
                break;
        }

        if (IsScaled)
        {
            parts.Add($"× {Gain.ToString("R", CultureInfo.InvariantCulture)}{(Offset == 0 ? string.Empty : $" {(Offset < 0 ? "−" : "+")} {Math.Abs(Offset).ToString("R", CultureInfo.InvariantCulture)}")}");
        }

        if (Unit is { } unit)
        {
            parts.Add(unit.Length == 0 ? "no unit" : $"unit {unit}");
        }

        return parts.Count == 0 ? "as sent" : string.Join(" · ", parts);
    }
}

/// <summary>Clients that know the engineering units of variables (OPC UA EngineeringUnits property).</summary>
public interface IEngineeringUnitsSource
{
    /// <summary>The unit of each variable (e.g. "°C"); null where it has none.</summary>
    Task<IReadOnlyList<string?>> ReadUnitsAsync(IReadOnlyList<Opc.Ua.NodeId> nodeIds, CancellationToken cancellationToken = default);
}
