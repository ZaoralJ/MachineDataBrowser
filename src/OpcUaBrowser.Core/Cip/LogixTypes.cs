using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using Opc.Ua;

namespace OpcUaBrowser.Core.Cip;

/// <summary>Logix atomic data type codes (low byte of the CIP symbol type). Names follow Logix (INT, UINT …).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Logix type names.")]
public enum LogixAtomic : byte
{
    Bool = 0xC1,
    Sint = 0xC2,
    Int = 0xC3,
    Dint = 0xC4,
    Lint = 0xC5,
    Usint = 0xC6,
    Uint = 0xC7,
    Udint = 0xC8,
    Ulint = 0xC9,
    Real = 0xCA,
    Lreal = 0xCB,
    Byte = 0xD1,
    Word = 0xD2,
    Dword = 0xD3,
    Lword = 0xD4,
}

/// <summary>A CIP symbol type word: structure flag, array dimension count, system flag, atomic code or template id.</summary>
public readonly record struct LogixType(ushort Raw)
{
    public bool IsStruct => (Raw & 0x8000) != 0;

    public int DimensionCount => (Raw >> 13) & 0x3;

    public bool IsSystem => (Raw & 0x1000) != 0;

    /// <summary>Template instance id of a structure (UDT, STRING, AOI …).</summary>
    public ushort TemplateId => (ushort)(Raw & 0x0FFF);

    public LogixAtomic Atomic => (LogixAtomic)(Raw & 0xFF);

    public static int AtomicSize(LogixAtomic atomic) => atomic switch
    {
        LogixAtomic.Bool or LogixAtomic.Sint or LogixAtomic.Usint or LogixAtomic.Byte => 1,
        LogixAtomic.Int or LogixAtomic.Uint or LogixAtomic.Word => 2,
        LogixAtomic.Dint or LogixAtomic.Udint or LogixAtomic.Real or LogixAtomic.Dword => 4,
        LogixAtomic.Lint or LogixAtomic.Ulint or LogixAtomic.Lreal or LogixAtomic.Lword => 8,
        _ => 0,
    };

    public static string AtomicName(LogixAtomic atomic) =>
        Enum.IsDefined(atomic) ? atomic.ToString().ToUpperInvariant() : $"0x{(byte)atomic:X2}";
}

/// <summary>One entry of a <c>@tags</c> listing.</summary>
public sealed record LogixSymbol(string Name, uint InstanceId, LogixType Type, int ElementSize, int[] Dimensions)
{
    public int ElementCount => Dimensions.Aggregate(1, (a, d) => a * d);
}

/// <summary>One member of a UDT template.</summary>
public sealed record LogixMember(string Name, LogixType Type, int Info, int Offset)
{
    /// <summary>Bit number for BOOL members; element count for array members; 0 otherwise.</summary>
    public int Info { get; } = Info;

    /// <summary>Array members have the dimension bits set in their type; <see cref="Info"/> is then the element count.</summary>
    public bool IsArray => Type.DimensionCount > 0 && Info > 0;

    public int ElementCount => IsArray ? Info : 1;

    /// <summary>Compiler-generated members that host packed BOOLs or padding.</summary>
    public bool IsHidden => Name.StartsWith("ZZZZZZZZZZ", StringComparison.Ordinal) || Name.StartsWith("__", StringComparison.Ordinal) || Name.Length == 0;
}

/// <summary>A UDT (or built-in structure) definition read from <c>@udt/&lt;id&gt;</c>.</summary>
public sealed record LogixTemplate(ushort Id, string Name, int InstanceSize, ushort Handle, IReadOnlyList<LogixMember> Members)
{
    /// <summary>Logix STRING-like structures: a DINT length and a SINT data array (STRING, STRING_20, user string types).</summary>
    public bool IsString =>
        Members.Count == 2
        && Members[0].Name.Equals("LEN", StringComparison.OrdinalIgnoreCase) && Members[0].Type.Atomic == LogixAtomic.Dint
        && Members[1].Name.Equals("DATA", StringComparison.OrdinalIgnoreCase) && Members[1].Type.Atomic == LogixAtomic.Sint && Members[1].IsArray;
}

/// <summary>Parsers for the raw <c>@tags</c> / <c>@udt</c> buffers and value decoding. Little-endian throughout.</summary>
public static class LogixCodec
{
    /// <summary>
    /// Entries: uint32 instance id, uint16 symbol type, uint16 element size, uint32[3] dimensions, uint16 name length, name.
    /// </summary>
    public static IReadOnlyList<LogixSymbol> ParseTagList(ReadOnlySpan<byte> buffer)
    {
        var result = new List<LogixSymbol>();
        var offset = 0;
        while (offset + 22 <= buffer.Length)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
            var type = new LogixType(BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 4)..]));
            var elementSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 6)..]);
            var dims = new int[3];
            for (var i = 0; i < 3; i++)
            {
                dims[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 8 + (i * 4))..]);
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 20)..]);
            offset += 22;
            if (offset + nameLength > buffer.Length)
            {
                break;
            }

            var name = Encoding.ASCII.GetString(buffer.Slice(offset, nameLength));
            offset += nameLength;
            result.Add(new LogixSymbol(name, id, type, elementSize, [.. dims.Take(type.DimensionCount)]));
        }

        return result;
    }

    /// <summary>
    /// Header: uint16 id, uint32 member description size (words), uint32 instance size, uint16 member count, uint16 handle;
    /// then 8 bytes per member (uint16 info, uint16 type, uint32 offset); then "NAME;..." and member names, NUL-terminated.
    /// </summary>
    public static LogixTemplate ParseTemplate(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 14)
        {
            throw new FormatException("UDT definition is too short.");
        }

        var id = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        var instanceSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[6..]);
        var memberCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer[10..]);
        var handle = BinaryPrimitives.ReadUInt16LittleEndian(buffer[12..]);

        var offset = 14;
        var raw = new (int Info, LogixType Type, int Offset)[memberCount];
        for (var i = 0; i < memberCount; i++)
        {
            if (offset + 8 > buffer.Length)
            {
                throw new FormatException("UDT definition is truncated.");
            }

            raw[i] = (
                BinaryPrimitives.ReadUInt16LittleEndian(buffer[offset..]),
                new LogixType(BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 2)..])),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]));
            offset += 8;
        }

        var templateName = ReadCString(buffer, ref offset);
        var semicolon = templateName.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0)
        {
            templateName = templateName[..semicolon];
        }

        var members = new List<LogixMember>(memberCount);
        for (var i = 0; i < memberCount; i++)
        {
            var name = offset < buffer.Length ? ReadCString(buffer, ref offset) : string.Empty;
            members.Add(new LogixMember(name, raw[i].Type, raw[i].Info, raw[i].Offset));
        }

        return new LogixTemplate(id, templateName, instanceSize, handle, members);
    }

    /// <summary>Decodes <paramref name="count"/> atomics (an array when count &gt; 1).</summary>
    public static object? DecodeAtomic(LogixAtomic atomic, ReadOnlySpan<byte> data, int count = 1, bool forceArray = false)
    {
        var size = LogixType.AtomicSize(atomic);
        if (size == 0)
        {
            return null;
        }

        count = Math.Min(count, data.Length / size);
        if (count == 1 && !forceArray)
        {
            return DecodeOne(atomic, data);
        }

        var array = CreateArray(atomic, count);
        for (var i = 0; i < count; i++)
        {
            array.SetValue(DecodeOne(atomic, data[(i * size)..]), i);
        }

        return array;
    }

    /// <summary>Text of a STRING-like structure.</summary>
    public static string DecodeString(LogixTemplate template, ReadOnlySpan<byte> data)
    {
        var len = template.Members[0];
        var chars = template.Members[1];
        if (data.Length < len.Offset + 4)
        {
            return string.Empty;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(data[len.Offset..]);
        length = Math.Clamp(length, 0, Math.Min(chars.ElementCount, Math.Max(0, data.Length - chars.Offset)));
        return Encoding.Latin1.GetString(data.Slice(chars.Offset, length));
    }

    /// <summary>Little-endian bytes of one atomic value or of every element of an atomic array.</summary>
    public static byte[] EncodeAtomic(LogixAtomic atomic, object value)
    {
        var size = LogixType.AtomicSize(atomic);
        if (size == 0)
        {
            throw new NotSupportedException($"Type 0x{(byte)atomic:X2} is not atomic.");
        }

        var values = value is Array array ? array.Cast<object>().ToArray() : [value];
        var data = new byte[values.Length * size];
        for (var i = 0; i < values.Length; i++)
        {
            EncodeOne(atomic, values[i], data.AsSpan(i * size, size));
        }

        return data;
    }

    /// <summary>Stores <paramref name="text"/> into a STRING-like structure buffer (LEN + DATA).</summary>
    public static void EncodeString(LogixTemplate template, Span<byte> data, string text)
    {
        var len = template.Members[0];
        var chars = template.Members[1];
        var bytes = Encoding.Latin1.GetBytes(text);
        if (bytes.Length > chars.ElementCount)
        {
            throw new FormatException($"Text is {bytes.Length} characters; {template.Name} holds at most {chars.ElementCount}.");
        }

        if (data.Length < chars.Offset + chars.ElementCount)
        {
            throw new FormatException($"{template.Name} buffer is too small.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(data[len.Offset..], bytes.Length);
        var target = data.Slice(chars.Offset, chars.ElementCount);
        target.Clear();
        bytes.CopyTo(target);
    }

    private static void EncodeOne(LogixAtomic atomic, object value, Span<byte> d)
    {
        switch (atomic)
        {
            case LogixAtomic.Bool: d[0] = Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? (byte)1 : (byte)0; break;
            case LogixAtomic.Sint: d[0] = unchecked((byte)Convert.ToSByte(value, CultureInfo.InvariantCulture)); break;
            case LogixAtomic.Usint or LogixAtomic.Byte: d[0] = Convert.ToByte(value, CultureInfo.InvariantCulture); break;
            case LogixAtomic.Int: BinaryPrimitives.WriteInt16LittleEndian(d, Convert.ToInt16(value, CultureInfo.InvariantCulture)); break;
            case LogixAtomic.Uint or LogixAtomic.Word: BinaryPrimitives.WriteUInt16LittleEndian(d, Convert.ToUInt16(value, CultureInfo.InvariantCulture)); break;
            case LogixAtomic.Dint: BinaryPrimitives.WriteInt32LittleEndian(d, Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
            case LogixAtomic.Udint or LogixAtomic.Dword: BinaryPrimitives.WriteUInt32LittleEndian(d, Convert.ToUInt32(value, CultureInfo.InvariantCulture)); break;
            case LogixAtomic.Lint: BinaryPrimitives.WriteInt64LittleEndian(d, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case LogixAtomic.Ulint or LogixAtomic.Lword: BinaryPrimitives.WriteUInt64LittleEndian(d, Convert.ToUInt64(value, CultureInfo.InvariantCulture)); break;
            case LogixAtomic.Real: BinaryPrimitives.WriteSingleLittleEndian(d, Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            case LogixAtomic.Lreal: BinaryPrimitives.WriteDoubleLittleEndian(d, Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
            default: throw new NotSupportedException($"Type 0x{(byte)atomic:X2} is not atomic.");
        }
    }

    /// <summary>The OPC UA type user input for <paramref name="atomic"/> is parsed as.</summary>
    public static BuiltInType ToBuiltInType(LogixAtomic atomic) => atomic switch
    {
        LogixAtomic.Bool => BuiltInType.Boolean,
        LogixAtomic.Sint => BuiltInType.SByte,
        LogixAtomic.Usint or LogixAtomic.Byte => BuiltInType.Byte,
        LogixAtomic.Int => BuiltInType.Int16,
        LogixAtomic.Uint or LogixAtomic.Word => BuiltInType.UInt16,
        LogixAtomic.Dint => BuiltInType.Int32,
        LogixAtomic.Udint or LogixAtomic.Dword => BuiltInType.UInt32,
        LogixAtomic.Lint => BuiltInType.Int64,
        LogixAtomic.Ulint or LogixAtomic.Lword => BuiltInType.UInt64,
        LogixAtomic.Real => BuiltInType.Float,
        LogixAtomic.Lreal => BuiltInType.Double,
        _ => throw new NotSupportedException($"Type 0x{(byte)atomic:X2} is not atomic."),
    };

    private static object DecodeOne(LogixAtomic atomic, ReadOnlySpan<byte> d) => atomic switch
    {
        LogixAtomic.Bool => d[0] != 0,
        LogixAtomic.Sint => (sbyte)d[0],
        LogixAtomic.Usint or LogixAtomic.Byte => d[0],
        LogixAtomic.Int => BinaryPrimitives.ReadInt16LittleEndian(d),
        LogixAtomic.Uint or LogixAtomic.Word => BinaryPrimitives.ReadUInt16LittleEndian(d),
        LogixAtomic.Dint => BinaryPrimitives.ReadInt32LittleEndian(d),
        LogixAtomic.Udint or LogixAtomic.Dword => BinaryPrimitives.ReadUInt32LittleEndian(d),
        LogixAtomic.Lint => BinaryPrimitives.ReadInt64LittleEndian(d),
        LogixAtomic.Ulint or LogixAtomic.Lword => BinaryPrimitives.ReadUInt64LittleEndian(d),
        LogixAtomic.Real => BinaryPrimitives.ReadSingleLittleEndian(d),
        LogixAtomic.Lreal => BinaryPrimitives.ReadDoubleLittleEndian(d),
        _ => throw new NotSupportedException($"Type 0x{(byte)atomic:X2} is not atomic."),
    };

    private static Array CreateArray(LogixAtomic atomic, int count) => atomic switch
    {
        LogixAtomic.Bool => new bool[count],
        LogixAtomic.Sint => new sbyte[count],
        LogixAtomic.Usint or LogixAtomic.Byte => new byte[count],
        LogixAtomic.Int => new short[count],
        LogixAtomic.Uint or LogixAtomic.Word => new ushort[count],
        LogixAtomic.Dint => new int[count],
        LogixAtomic.Udint or LogixAtomic.Dword => new uint[count],
        LogixAtomic.Lint => new long[count],
        LogixAtomic.Ulint or LogixAtomic.Lword => new ulong[count],
        LogixAtomic.Real => new float[count],
        LogixAtomic.Lreal => new double[count],
        _ => new object[count],
    };

    private static string ReadCString(ReadOnlySpan<byte> buffer, ref int offset)
    {
        var rest = buffer[offset..];
        var end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            end = rest.Length;
        }

        var text = Encoding.ASCII.GetString(rest[..end]);
        offset += Math.Min(rest.Length, end + 1);
        return text;
    }
}
