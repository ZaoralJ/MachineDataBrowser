using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.S7;

/// <summary>Memory areas of an S7 CPU that can be read and written.</summary>
internal enum S7Area
{
    Input,
    Output,
    Memory,
    DataBlock,
}

/// <summary>Elementary S7 data types (STEP 7 / TIA Portal names).</summary>
internal enum S7Kind
{
    Bool,
    Byte,
    Word,
    DWord,
    LWord,
    Char,
    WChar,
    SInt,
    USInt,
    Int,
    UInt,
    DInt,
    UDInt,
    LInt,
    ULInt,
    Real,
    LReal,
    String,
    WString,
    Time,
    Date,
    TimeOfDay,
}

/// <summary>A data type: an elementary kind, the maximum length of a (W)STRING, and the element count of an array (0: scalar).</summary>
internal sealed record S7Type(S7Kind Kind, int StringLength = 0, int Count = 0)
{
    private const int DefaultStringLength = 254;

    private static readonly Regex ArrayPattern = new(
        @"^ARRAY\s*\[\s*(?<lo>-?\d+)\s*\.\.\s*(?<hi>-?\d+)\s*\]\s*OF\s+(?<type>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    private static readonly Regex SuffixPattern = new(
        @"^(?<type>[A-Z_]+)\s*(\[\s*(?<n>\d+)\s*\])?\s*(\[\s*(?<count>\d+)\s*\])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public static S7Type Bool { get; } = new(S7Kind.Bool);

    public static S7Type Byte { get; } = new(S7Kind.Byte);

    public bool IsArray => Count > 0;

    public bool IsBit => Kind == S7Kind.Bool;

    public S7Type Element => this with { Count = 0 };

    /// <summary>Bytes of one element; BOOLs are bits and take 0 here.</summary>
    public int ElementSize => Kind switch
    {
        S7Kind.Bool => 0,
        S7Kind.Byte or S7Kind.Char or S7Kind.SInt or S7Kind.USInt => 1,
        S7Kind.Word or S7Kind.WChar or S7Kind.Int or S7Kind.UInt or S7Kind.Date => 2,
        S7Kind.DWord or S7Kind.DInt or S7Kind.UDInt or S7Kind.Real or S7Kind.Time or S7Kind.TimeOfDay => 4,
        S7Kind.LWord or S7Kind.LInt or S7Kind.ULInt or S7Kind.LReal => 8,
        S7Kind.String => 2 + StringLength,
        S7Kind.WString => 4 + (2 * StringLength),
        _ => throw new NotSupportedException(Kind.ToString()),
    };

    public string KindName => Kind switch
    {
        S7Kind.TimeOfDay => "TIME_OF_DAY",
        S7Kind.String or S7Kind.WString => $"{Kind.ToString().ToUpperInvariant()}[{StringLength.ToString(CultureInfo.InvariantCulture)}]",
        _ => Kind.ToString().ToUpperInvariant(),
    };

    /// <summary>Display and address-suffix form: <c>REAL</c>, <c>STRING[20]</c>, <c>INT[10]</c> for arrays.</summary>
    public string Name => IsArray ? $"{KindName}[{Count.ToString(CultureInfo.InvariantCulture)}]" : KindName;

    public override string ToString() => Name;

    /// <summary>Bytes covered by a value starting at bit <paramref name="bit"/> of its first byte.</summary>
    public int ByteLength(int bit) => IsBit
        ? (bit + Math.Max(Count, 1) + 7) / 8
        : ElementSize * Math.Max(Count, 1);

    public DeviceDataType ValueType => Kind switch
    {
        S7Kind.Bool => DeviceDataType.Boolean,
        S7Kind.Byte or S7Kind.USInt => DeviceDataType.Byte,
        S7Kind.SInt => DeviceDataType.SByte,
        S7Kind.Word or S7Kind.UInt => DeviceDataType.UInt16,
        S7Kind.Int => DeviceDataType.Int16,
        S7Kind.DWord or S7Kind.UDInt => DeviceDataType.UInt32,
        S7Kind.DInt => DeviceDataType.Int32,
        S7Kind.LWord or S7Kind.ULInt => DeviceDataType.UInt64,
        S7Kind.LInt => DeviceDataType.Int64,
        S7Kind.Real => DeviceDataType.Float,
        S7Kind.LReal => DeviceDataType.Double,
        S7Kind.Date => DeviceDataType.DateTime,
        _ => DeviceDataType.String,
    };

    /// <summary>
    /// Parses a type as written in TIA Portal / STEP 7 exports (<c>Real</c>, <c>String[20]</c>,
    /// <c>Array[0..9] of Int</c>) or in an address suffix (<c>REAL</c>, <c>INT[10]</c>, <c>STRING[20][4]</c>).
    /// </summary>
    public static bool TryParse(string text, out S7Type type)
    {
        type = null!;
        var trimmed = text.Trim().Trim('"');
        if (ArrayPattern.Match(trimmed) is { Success: true } array)
        {
            if (!int.TryParse(array.Groups["lo"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var lo)
                || !int.TryParse(array.Groups["hi"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var hi)
                || hi < lo
                || !TryParse(array.Groups["type"].Value, out var element)
                || element.IsArray)
            {
                return false;
            }

            type = element with { Count = hi - lo + 1 };
            return true;
        }

        var match = SuffixPattern.Match(trimmed);
        if (!match.Success || !TryParseKind(match.Groups["type"].Value, out var kind))
        {
            return false;
        }

        var hasLength = match.Groups["n"].Success;
        var hasCount = match.Groups["count"].Success;
        var isString = kind is S7Kind.String or S7Kind.WString;
        int length = 0, count = 0;
        if (isString)
        {
            length = hasLength ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : DefaultStringLength;
            count = hasCount ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) : 0;
            if (length is < 1 or > 254 || (hasCount && count < 1))
            {
                return false;
            }
        }
        else
        {
            // A single bracket on a non-string type is the element count: REAL[10].
            if (hasCount)
            {
                return false;
            }

            count = hasLength ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 0;
            if (hasLength && count < 1)
            {
                return false;
            }
        }

        type = new S7Type(kind, length, count);
        return true;
    }

    private static bool TryParseKind(string name, out S7Kind kind)
    {
        switch (name.ToUpperInvariant())
        {
            case "TIME_OF_DAY" or "TOD":
                kind = S7Kind.TimeOfDay;
                return true;
            case "S5TIME" or "DATE_AND_TIME" or "DT" or "DTL" or "LTIME":
                kind = default;
                return false;
            default:
                return Enum.TryParse(name, ignoreCase: true, out kind) && Enum.IsDefined(kind) && !name.All(char.IsDigit);
        }
    }

    private static readonly DateTime DateEpoch = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>Decodes a value from <paramref name="data"/>, which starts at the value's first byte.</summary>
    public object Decode(ReadOnlySpan<byte> data, int bit)
    {
        if (!IsArray)
        {
            return DecodeOne(data, bit);
        }

        var values = Array.CreateInstance(ClrType, Count);
        for (var i = 0; i < Count; i++)
        {
            values.SetValue(IsBit
                ? DecodeOne(data[((bit + i) / 8)..], (bit + i) % 8)
                : DecodeOne(data[(i * ElementSize)..], 0), i);
        }

        return new DeviceArray(KindName, values);
    }

    /// <summary>Encodes <paramref name="value"/> (scalar or array, as parsed for <see cref="ValueType"/>) into <paramref name="buffer"/>.</summary>
    public void Encode(object value, Span<byte> buffer, int bit)
    {
        if (!IsArray)
        {
            EncodeOne(value, buffer, bit);
            return;
        }

        var values = (Array)value;
        if (values.Length != Count)
        {
            throw new FormatException($"Expected {Count} elements; {values.Length} given.");
        }

        for (var i = 0; i < Count; i++)
        {
            if (IsBit)
            {
                EncodeOne(values.GetValue(i)!, buffer[((bit + i) / 8)..], (bit + i) % 8);
            }
            else
            {
                EncodeOne(values.GetValue(i)!, buffer[(i * ElementSize)..], 0);
            }
        }
    }

    /// <summary>Parses user input for writing: arrays as comma-separated elements, optionally in brackets.</summary>
    public object ParseInput(string text)
    {
        if (Kind is not (S7Kind.Time or S7Kind.TimeOfDay or S7Kind.Date))
        {
            return ValueParser.Parse(text, ValueType, IsArray);
        }

        if (!IsArray)
        {
            return ParseSpecial(text.Trim());
        }

        var body = text.Trim();
        if (body.StartsWith('[') && body.EndsWith(']'))
        {
            body = body[1..^1];
        }

        var parts = body.Trim().Length == 0 ? [] : body.Split(',');
        var values = Array.CreateInstance(ClrType, parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            values.SetValue(ParseSpecial(parts[i].Trim()), i);
        }

        return values;
    }

    // A DATE is a calendar day: parsed as written, not shifted to UTC like other date/time input.
    private object ParseSpecial(string text) => Kind == S7Kind.Date
        ? DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : throw new FormatException($"'{text}' is not a date: use yyyy-mm-dd.")
        : ParseDuration(text);

    /// <summary>Milliseconds (<c>90000</c>) or a time span (<c>00:01:30</c>, <c>13:45:30.250</c>).</summary>
    private static TimeSpan ParseDuration(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms) ? TimeSpan.FromMilliseconds(ms)
        : TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) ? span
        : throw new FormatException($"'{text}' is not a duration: use milliseconds or hh:mm:ss[.fff].");

    private Type ClrType => Kind switch
    {
        S7Kind.Bool => typeof(bool),
        S7Kind.Byte or S7Kind.USInt => typeof(byte),
        S7Kind.SInt => typeof(sbyte),
        S7Kind.Word or S7Kind.UInt => typeof(ushort),
        S7Kind.Int => typeof(short),
        S7Kind.DWord or S7Kind.UDInt => typeof(uint),
        S7Kind.DInt => typeof(int),
        S7Kind.LWord or S7Kind.ULInt => typeof(ulong),
        S7Kind.LInt => typeof(long),
        S7Kind.Real => typeof(float),
        S7Kind.LReal => typeof(double),
        S7Kind.Time or S7Kind.TimeOfDay => typeof(TimeSpan),
        S7Kind.Date => typeof(DateTime),
        _ => typeof(string),
    };

    private object DecodeOne(ReadOnlySpan<byte> d, int bit) => Kind switch
    {
        S7Kind.Bool => (d[0] & (1 << bit)) != 0,
        S7Kind.Byte or S7Kind.USInt => d[0],
        S7Kind.SInt => (sbyte)d[0],
        S7Kind.Word or S7Kind.UInt => BinaryPrimitives.ReadUInt16BigEndian(d),
        S7Kind.Int => BinaryPrimitives.ReadInt16BigEndian(d),
        S7Kind.DWord or S7Kind.UDInt => BinaryPrimitives.ReadUInt32BigEndian(d),
        S7Kind.DInt => BinaryPrimitives.ReadInt32BigEndian(d),
        S7Kind.LWord or S7Kind.ULInt => BinaryPrimitives.ReadUInt64BigEndian(d),
        S7Kind.LInt => BinaryPrimitives.ReadInt64BigEndian(d),
        S7Kind.Real => BinaryPrimitives.ReadSingleBigEndian(d),
        S7Kind.LReal => BinaryPrimitives.ReadDoubleBigEndian(d),
        S7Kind.Char => Encoding.Latin1.GetString(d[..1]),
        S7Kind.WChar => Encoding.BigEndianUnicode.GetString(d[..2]),
        S7Kind.String => Encoding.Latin1.GetString(d.Slice(2, Math.Min(d[1], Math.Min(d[0], StringLength)))),
        S7Kind.WString => Encoding.BigEndianUnicode.GetString(d.Slice(4, 2 * Math.Min(
            BinaryPrimitives.ReadUInt16BigEndian(d[2..]),
            Math.Min(BinaryPrimitives.ReadUInt16BigEndian(d), (ushort)StringLength)))),
        S7Kind.Time => TimeSpan.FromMilliseconds(BinaryPrimitives.ReadInt32BigEndian(d)),
        S7Kind.TimeOfDay => TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt32BigEndian(d)),
        S7Kind.Date => DateEpoch.AddDays(BinaryPrimitives.ReadUInt16BigEndian(d)),
        _ => throw new NotSupportedException(Kind.ToString()),
    };

    private void EncodeOne(object value, Span<byte> d, int bit)
    {
        switch (Kind)
        {
            case S7Kind.Bool:
                d[0] = (bool)value ? (byte)(d[0] | (1 << bit)) : (byte)(d[0] & ~(1 << bit));
                break;
            case S7Kind.Byte or S7Kind.USInt:
                d[0] = (byte)value;
                break;
            case S7Kind.SInt:
                d[0] = unchecked((byte)(sbyte)value);
                break;
            case S7Kind.Word or S7Kind.UInt:
                BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)value);
                break;
            case S7Kind.Int:
                BinaryPrimitives.WriteInt16BigEndian(d, (short)value);
                break;
            case S7Kind.DWord or S7Kind.UDInt:
                BinaryPrimitives.WriteUInt32BigEndian(d, (uint)value);
                break;
            case S7Kind.DInt:
                BinaryPrimitives.WriteInt32BigEndian(d, (int)value);
                break;
            case S7Kind.LWord or S7Kind.ULInt:
                BinaryPrimitives.WriteUInt64BigEndian(d, (ulong)value);
                break;
            case S7Kind.LInt:
                BinaryPrimitives.WriteInt64BigEndian(d, (long)value);
                break;
            case S7Kind.Real:
                BinaryPrimitives.WriteSingleBigEndian(d, (float)value);
                break;
            case S7Kind.LReal:
                BinaryPrimitives.WriteDoubleBigEndian(d, (double)value);
                break;
            case S7Kind.Char:
                d[0] = Encoding.Latin1.GetBytes(SingleChar((string)value))[0];
                break;
            case S7Kind.WChar:
                Encoding.BigEndianUnicode.GetBytes(SingleChar((string)value)).CopyTo(d);
                break;
            case S7Kind.String:
            {
                var bytes = Encoding.Latin1.GetBytes((string)value);
                if (bytes.Length > StringLength)
                {
                    throw new FormatException($"At most {StringLength} characters fit in {KindName}.");
                }

                d[0] = (byte)StringLength;
                d[1] = (byte)bytes.Length;
                d.Slice(2, StringLength).Clear();
                bytes.CopyTo(d[2..]);
                break;
            }

            case S7Kind.WString:
            {
                var bytes = Encoding.BigEndianUnicode.GetBytes((string)value);
                if (bytes.Length > 2 * StringLength)
                {
                    throw new FormatException($"At most {StringLength} characters fit in {KindName}.");
                }

                BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)StringLength);
                BinaryPrimitives.WriteUInt16BigEndian(d[2..], (ushort)(bytes.Length / 2));
                d.Slice(4, 2 * StringLength).Clear();
                bytes.CopyTo(d[4..]);
                break;
            }

            case S7Kind.Time:
                BinaryPrimitives.WriteInt32BigEndian(d, checked((int)((TimeSpan)value).TotalMilliseconds));
                break;
            case S7Kind.TimeOfDay:
            {
                var span = (TimeSpan)value;
                if (span < TimeSpan.Zero || span >= TimeSpan.FromDays(1))
                {
                    throw new FormatException("A time of day is between 00:00:00 and 23:59:59.999.");
                }

                BinaryPrimitives.WriteUInt32BigEndian(d, (uint)span.TotalMilliseconds);
                break;
            }

            case S7Kind.Date:
            {
                var days = (((DateTime)value).Date - DateEpoch).TotalDays;
                if (days is < 0 or > ushort.MaxValue)
                {
                    throw new FormatException("A DATE is between 1990-01-01 and 2168-12-31.");
                }

                BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)days);
                break;
            }

            default:
                throw new NotSupportedException(Kind.ToString());
        }
    }

    private static string SingleChar(string text) =>
        text.Length == 1 ? text : throw new FormatException("Expected exactly one character.");
}

/// <summary>
/// An absolute S7 address with its type, e.g. <c>DB1.DBD4:REAL</c>, <c>M0.0</c>, <c>IW2:INT</c>,
/// <c>DB2.DBW40:INT[10]</c>. Inputs/outputs accept German mnemonics (E, A) and a leading <c>%</c> (TIA Portal).
/// </summary>
internal sealed record S7Address(S7Area Area, int Db, int Byte, int Bit, S7Type Type)
{
    private static readonly Regex Pattern = new(
        @"^(?:DB(?<db>\d+)\.DB|(?<area>[IEQAM]))(?<w>[XBWD])?(?<byte>\d+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public int ByteLength => Type.ByteLength(Bit);

    public string AreaPrefix => Area switch
    {
        S7Area.Input => "I",
        S7Area.Output => "Q",
        S7Area.Memory => "M",
        _ => $"DB{Db.ToString(CultureInfo.InvariantCulture)}.DB",
    };

    /// <summary>Address without the type, as STEP 7 writes it: <c>DB1.DBD4</c>, <c>M0.0</c>, <c>IB1</c>.</summary>
    public string Location
    {
        get
        {
            var byteText = Byte.ToString(CultureInfo.InvariantCulture);
            if (Type.IsBit)
            {
                return $"{AreaPrefix}{(Area == S7Area.DataBlock ? "X" : string.Empty)}{byteText}.{Bit.ToString(CultureInfo.InvariantCulture)}";
            }

            return $"{AreaPrefix}{WidthLetter(Type)}{byteText}";
        }
    }

    /// <summary>Canonical text: the location, plus <c>:TYPE</c> unless it is the default type of the width letter.</summary>
    public override string ToString() => Type == DefaultType(Location) ? Location : $"{Location}:{Type.Name}";

    public static S7Address Parse(string text) =>
        TryParse(text, out var address, out var error) ? address : throw new FormatException(error);

    public static bool TryParse(string text, out S7Address address) => TryParse(text, out address, out _);

    public static bool TryParse(string text, out S7Address address, out string error)
    {
        address = null!;
        var trimmed = (text ?? string.Empty).Trim();
        S7Type? explicitType = null;
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            if (!S7Type.TryParse(trimmed[(colon + 1)..], out var parsedType))
            {
                error = $"'{trimmed[(colon + 1)..].Trim()}' is not an S7 data type.";
                return false;
            }

            explicitType = parsedType;
            trimmed = trimmed[..colon];
        }

        return TryParse(trimmed, explicitType, out address, out error);
    }

    /// <summary>Parses a location (<c>%DB1.DBD4</c>, <c>M 10.0</c>) with a type given separately, as in symbol tables.</summary>
    public static bool TryParse(string location, S7Type? type, out S7Address address, out string error)
    {
        address = null!;
        var compact = string.Concat(location.Where(c => !char.IsWhiteSpace(c))).TrimStart('%').ToUpperInvariant();
        var match = Pattern.Match(compact);
        if (!match.Success)
        {
            error = $"'{location.Trim()}' is not an S7 address. Expected e.g. DB1.DBX0.0, DB1.DBD4:REAL, M0.0, MW2, IB1, Q0.1.";
            return false;
        }

        var area = match.Groups["area"].Success
            ? match.Groups["area"].Value switch
            {
                "I" or "E" => S7Area.Input,
                "Q" or "A" => S7Area.Output,
                _ => S7Area.Memory,
            }
            : S7Area.DataBlock;
        var db = match.Groups["db"].Success ? int.Parse(match.Groups["db"].Value, CultureInfo.InvariantCulture) : 0;
        var width = match.Groups["w"].Success ? match.Groups["w"].Value[0] : (char?)null;
        var hasBit = match.Groups["bit"].Success;
        if (!int.TryParse(match.Groups["byte"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var byteOffset)
            || (area == S7Area.DataBlock && (db < 1 || width is null)))
        {
            error = $"'{location.Trim()}' is not an S7 address: data block addresses look like DB1.DBX0.0, DB1.DBB2, DB1.DBW2, DB1.DBD4.";
            return false;
        }

        var isBitAddress = width == 'X' || (width is null && hasBit);
        if (isBitAddress != hasBit || (width is null && !hasBit))
        {
            error = $"'{location.Trim()}': bit addresses need a bit number (M0.0, DB1.DBX0.0), byte/word addresses none (MB0, DB1.DBW2).";
            return false;
        }

        var resolved = type ?? (isBitAddress ? S7Type.Bool : DefaultType(width!.Value));
        if (resolved.IsBit != isBitAddress)
        {
            error = isBitAddress
                ? $"'{location.Trim()}' is a bit address; only BOOL fits there."
                : $"BOOL needs a bit address such as {location.Trim().TrimStart('%')}.0.";
            return false;
        }

        address = new S7Address(area, db, byteOffset, hasBit ? match.Groups["bit"].Value[0] - '0' : 0, resolved);
        error = string.Empty;
        return true;
    }

    private static char WidthLetter(S7Type type) => type.ElementSize switch
    {
        2 => 'W',
        4 => 'D',
        _ => 'B',
    };

    private static S7Type DefaultType(char width) => width switch
    {
        'W' => new S7Type(S7Kind.Word),
        'D' => new S7Type(S7Kind.DWord),
        _ => S7Type.Byte,
    };

    private static S7Type? DefaultType(string location)
    {
        if (location.Contains('.', StringComparison.Ordinal) && char.IsDigit(location[^1]) && location[^2] == '.')
        {
            return S7Type.Bool;
        }

        var digits = location.Length - location.Reverse().TakeWhile(char.IsDigit).Count();
        return DefaultType(location[digits - 1]);
    }
}
