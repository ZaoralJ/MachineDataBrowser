using System.Globalization;
using System.Text;

namespace MachineDataBrowser.Driver.Pccc;

/// <summary>PLC family behind a PCCC endpoint; decides the command set libplctag uses.</summary>
public enum PcccPlcFamily
{
    Slc500,
    MicroLogix,
    Plc5,
}

/// <summary>A data-table file type: its letter in addresses and the size of one element.</summary>
internal sealed record PcccFileType(string Letter, string Name, int ElementSize, IReadOnlyList<PcccField> Fields)
{
    public static readonly PcccFileType Output = new("O", "Output", 2, []);
    public static readonly PcccFileType Input = new("I", "Input", 2, []);
    public static readonly PcccFileType Status = new("S", "Status", 2, []);
    public static readonly PcccFileType Bit = new("B", "Binary", 2, []);
    public static readonly PcccFileType Integer = new("N", "Integer", 2, []);
    public static readonly PcccFileType Float = new("F", "Float", 4, []);
    public static readonly PcccFileType Long = new("L", "Long", 4, []);
    public static readonly PcccFileType String = new("ST", "String", 84, []);

    public static readonly PcccFileType Timer = new("T", "Timer", 6,
    [
        PcccField.Flag("EN", 15), PcccField.Flag("TT", 14), PcccField.Flag("DN", 13),
        PcccField.Word("PRE", 1), PcccField.Word("ACC", 2),
    ]);

    public static readonly PcccFileType Counter = new("C", "Counter", 6,
    [
        PcccField.Flag("CU", 15), PcccField.Flag("CD", 14), PcccField.Flag("DN", 13), PcccField.Flag("OV", 12), PcccField.Flag("UN", 11),
        PcccField.Word("PRE", 1), PcccField.Word("ACC", 2),
    ]);

    public static readonly PcccFileType Control = new("R", "Control", 6,
    [
        PcccField.Flag("EN", 15), PcccField.Flag("EU", 14), PcccField.Flag("DN", 13), PcccField.Flag("EM", 12),
        PcccField.Flag("ER", 11), PcccField.Flag("UL", 10), PcccField.Flag("IN", 9), PcccField.Flag("FD", 8),
        PcccField.Word("LEN", 1), PcccField.Word("POS", 2),
    ]);

    // Longest letters first, so "ST" is not taken for the status file "S".
    public static readonly IReadOnlyList<PcccFileType> All = [String, Output, Input, Status, Bit, Integer, Float, Long, Timer, Counter, Control];

    /// <summary>T, C and R elements are structures of three words; their members are the fields.</summary>
    public bool IsStructure => Fields.Count > 0;

    /// <summary>Highest bit number allowed after <c>/</c>; null where bit addressing doesn't apply.</summary>
    public int? MaxBit => Letter switch
    {
        "O" or "I" or "S" or "B" or "N" => 15,
        "L" => 31,
        _ => null,
    };

    public string ValueTypeName => Letter switch
    {
        "F" => "REAL",
        "L" => "DINT",
        "ST" => "STRING",
        _ when IsStructure => Name.ToUpperInvariant(),
        _ => "INT",
    };

    public PcccField? Field(string name) => Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A member of a timer, counter or control element: a status bit of word 0, or a whole word.</summary>
internal sealed record PcccField(string Name, int WordIndex, int? Bit)
{
    public static PcccField Flag(string name, int bit) => new(name, 0, bit);

    public static PcccField Word(string name, int wordIndex) => new(name, wordIndex, null);
}

/// <summary>
/// A data-table address: a file (<c>N7</c>), an element (<c>N7:0</c>), a bit (<c>B3:0/5</c>) or a member of a
/// structured element (<c>T4:0.ACC</c>, <c>T4:0.DN</c>).
/// </summary>
internal sealed record PcccAddress(PcccFileType Type, int File, int? Element, PcccField? Field, int? Bit)
{
    public string FileName => Type.Letter + File.ToString(CultureInfo.InvariantCulture);

    /// <summary>The element without member or bit: what is actually read from the controller.</summary>
    public string ElementName => $"{FileName}:{Element!.Value.ToString(CultureInfo.InvariantCulture)}";

    public bool IsFile => Element is null;

    /// <summary>A whole timer, counter or control element: a folder of its fields, not a value.</summary>
    public bool IsVariable => Element is not null && (Field is not null || !Type.IsStructure);

    public bool IsBoolean => Bit is not null || Field?.Bit is not null;

    public PcccAddress FileAddress => this with { Element = null, Field = null, Bit = null };

    public PcccAddress ElementAddress => this with { Field = null, Bit = null };

    public string TypeName => IsBoolean ? "BOOL" : Field is not null ? "INT" : Type.ValueTypeName;

    public override string ToString()
    {
        if (Element is null)
        {
            return FileName;
        }

        var text = new StringBuilder(ElementName);
        if (Field is not null)
        {
            text.Append('.').Append(Field.Name);
        }

        if (Bit is { } bit)
        {
            text.Append('/').Append(bit.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    public static bool TryParse(string text, out PcccAddress address)
    {
        address = null!;
        var rest = text.Trim().ToUpperInvariant();
        var type = PcccFileType.All.FirstOrDefault(t => rest.StartsWith(t.Letter, StringComparison.Ordinal) && (rest.Length == t.Letter.Length || !char.IsLetter(rest[t.Letter.Length])));
        if (type is null)
        {
            return false;
        }

        rest = rest[type.Letter.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        var fileText = colon < 0 ? rest : rest[..colon];
        int file;
        if (fileText.Length == 0 && (type == PcccFileType.Output || type == PcccFileType.Input || type == PcccFileType.Status))
        {
            // O:0, I:1 and S:2 omit the file number, as in RSLogix.
            file = type == PcccFileType.Output ? 0 : type == PcccFileType.Input ? 1 : 2;
        }
        else if (!TryNumber(fileText, out file))
        {
            return false;
        }

        if (colon < 0)
        {
            address = new PcccAddress(type, file, null, null, null);
            return true;
        }

        rest = rest[(colon + 1)..];
        var end = rest.IndexOfAny(['.', '/']);
        if (!TryNumber(end < 0 ? rest : rest[..end], out var element))
        {
            return false;
        }

        PcccField? field = null;
        int? bit = null;
        rest = end < 0 ? string.Empty : rest[end..];
        if (rest.StartsWith('.'))
        {
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            field = type.Field(slash < 0 ? rest[1..] : rest[1..slash]);
            if (field is null)
            {
                return false;
            }

            rest = slash < 0 ? string.Empty : rest[slash..];
        }

        if (rest.StartsWith('/'))
        {
            var max = field is null ? type.MaxBit : field.Bit is null ? 15 : null;
            if (max is null || !TryNumber(rest[1..], out var b) || b > max)
            {
                return false;
            }

            bit = b;
            rest = string.Empty;
        }

        if (rest.Length > 0)
        {
            return false;
        }

        address = new PcccAddress(type, file, element, field, bit);
        return true;
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= 65535;
}

/// <summary>Encodes and decodes data-table values in the controller's little-endian layout.</summary>
internal static class PcccCodec
{
    private const int StringCapacity = 82;

    /// <summary>Decodes a value of <paramref name="address"/> from the bytes of its whole element.</summary>
    public static object Decode(PcccAddress address, ReadOnlySpan<byte> element)
    {
        if (address.Field is { } field)
        {
            var word = BitConverter.ToInt16(element.Slice(field.WordIndex * 2, 2));
            var bit = field.Bit ?? address.Bit;
            return bit is { } b ? ((word >> b) & 1) != 0 : word;
        }

        return address.Type.Letter switch
        {
            "F" => BitConverter.ToSingle(element[..4]),
            "L" => address.Bit is { } lb ? ((BitConverter.ToInt32(element[..4]) >> lb) & 1) != 0 : BitConverter.ToInt32(element[..4]),
            "ST" => DecodeString(element),
            _ => address.Bit is { } wb ? ((BitConverter.ToInt16(element[..2]) >> wb) & 1) != 0 : BitConverter.ToInt16(element[..2]),
        };
    }

    /// <summary>ST elements: a 16-bit length, then 82 characters with each byte pair swapped.</summary>
    public static string DecodeString(ReadOnlySpan<byte> element)
    {
        var length = Math.Clamp((int)BitConverter.ToInt16(element[..2]), 0, StringCapacity);
        var chars = new byte[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = element[2 + (i ^ 1)];
        }

        return Encoding.Latin1.GetString(chars);
    }

    public static void EncodeString(string text, Span<byte> element)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        if (bytes.Length > StringCapacity)
        {
            throw new FormatException($"A string holds at most {StringCapacity} characters; {bytes.Length} given.");
        }

        element.Clear();
        BitConverter.TryWriteBytes(element[..2], (short)bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            element[2 + (i ^ 1)] = bytes[i];
        }
    }
}
