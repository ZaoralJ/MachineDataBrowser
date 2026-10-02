namespace MachineDataBrowser.Core.Mqtt;

/// <summary>
/// Minimal protobuf reader for the Sparkplug B payload (org.eclipse.tahu.protobuf.Payload). Only the fields the
/// browser shows are decoded; everything else (metadata, properties, datasets, templates) is skipped or summarised.
/// </summary>
public static class SparkplugB
{
    public const string Namespace = "spBv1.0";

    public sealed record Payload(ulong? Timestamp, ulong? Seq, IReadOnlyList<Metric> Metrics);

    public sealed record Metric(string? Name, ulong? Alias, ulong? Timestamp, uint DataType, bool IsNull, bool IsHistorical, object? Value);

    public static string DataTypeName(uint type) => type switch
    {
        1 => "Int8", 2 => "Int16", 3 => "Int32", 4 => "Int64",
        5 => "UInt8", 6 => "UInt16", 7 => "UInt32", 8 => "UInt64",
        9 => "Float", 10 => "Double", 11 => "Boolean", 12 => "String",
        13 => "DateTime", 14 => "Text", 15 => "UUID", 16 => "DataSet",
        17 => "Bytes", 18 => "File", 19 => "Template", 20 => "PropertySet", 21 => "PropertySetList",
        >= 22 and <= 34 => "Array",
        _ => $"Unknown({type})",
    };

    /// <summary>The type user input for a metric of Sparkplug <paramref name="dataType"/> is parsed as; null when not writable.</summary>
    public static Opc.Ua.BuiltInType? ToBuiltInType(uint dataType) => dataType switch
    {
        1 => Opc.Ua.BuiltInType.SByte, 2 => Opc.Ua.BuiltInType.Int16, 3 => Opc.Ua.BuiltInType.Int32, 4 => Opc.Ua.BuiltInType.Int64,
        5 => Opc.Ua.BuiltInType.Byte, 6 => Opc.Ua.BuiltInType.UInt16, 7 => Opc.Ua.BuiltInType.UInt32, 8 => Opc.Ua.BuiltInType.UInt64,
        9 => Opc.Ua.BuiltInType.Float, 10 => Opc.Ua.BuiltInType.Double, 11 => Opc.Ua.BuiltInType.Boolean,
        12 or 14 or 15 => Opc.Ua.BuiltInType.String, 13 => Opc.Ua.BuiltInType.DateTime,
        _ => null,
    };

    /// <summary>
    /// Encodes a command payload (NCMD/DCMD) with one metric. <paramref name="name"/> or <paramref name="alias"/> (or
    /// both) identify it; <paramref name="value"/> must match <paramref name="dataType"/> (see <see cref="ToBuiltInType"/>).
    /// </summary>
    public static byte[] EncodeCommand(string? name, ulong? alias, uint dataType, object value, DateTime timestampUtc)
    {
        var ms = (ulong)new DateTimeOffset(DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var metric = new Writer();
        if (!string.IsNullOrEmpty(name))
        {
            metric.Bytes(1, System.Text.Encoding.UTF8.GetBytes(name));
        }

        if (alias is { } a)
        {
            metric.Varint(2, a);
        }

        metric.Varint(3, ms);
        metric.Varint(4, dataType);
        switch (dataType)
        {
            case 1: metric.Varint(10, unchecked((uint)(sbyte)value)); break;
            case 2: metric.Varint(10, unchecked((uint)(short)value)); break;
            case 3: metric.Varint(10, unchecked((uint)(int)value)); break;
            case 5: metric.Varint(10, (byte)value); break;
            case 6: metric.Varint(10, (ushort)value); break;
            case 7: metric.Varint(10, (uint)value); break;
            case 4: metric.Varint(11, unchecked((ulong)(long)value)); break;
            case 8: metric.Varint(11, (ulong)value); break;
            case 13: metric.Varint(11, (ulong)new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc)).ToUnixTimeMilliseconds()); break;
            case 9: metric.Fixed32(12, BitConverter.SingleToUInt32Bits((float)value)); break;
            case 10: metric.Fixed64(13, BitConverter.DoubleToUInt64Bits((double)value)); break;
            case 11: metric.Varint(14, (bool)value ? 1UL : 0UL); break;
            case 12 or 14 or 15: metric.Bytes(15, System.Text.Encoding.UTF8.GetBytes((string)value)); break;
            default: throw new NotSupportedException($"Writing Sparkplug {DataTypeName(dataType)} metrics is not supported.");
        }

        var payload = new Writer();
        payload.Varint(1, ms);
        payload.Bytes(2, metric.ToArray());
        return payload.ToArray();
    }

    public static Payload Decode(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        ulong? timestamp = null, seq = null;
        var metrics = new List<Metric>();
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0:
                    timestamp = reader.ReadVarint();
                    break;
                case 2 when wire == 2:
                    metrics.Add(DecodeMetric(reader.ReadBytes()));
                    break;
                case 3 when wire == 0:
                    seq = reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new Payload(timestamp, seq, metrics);
    }

    private static Metric DecodeMetric(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        string? name = null;
        ulong? alias = null, timestamp = null;
        uint type = 0;
        bool isNull = false, historical = false;
        ulong? intValue = null, longValue = null;
        float? floatValue = null;
        double? doubleValue = null;
        bool? boolValue = null;
        string? stringValue = null;
        byte[]? bytesValue = null;
        string? complex = null;
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: name = reader.ReadString(); break;
                case 2 when wire == 0: alias = reader.ReadVarint(); break;
                case 3 when wire == 0: timestamp = reader.ReadVarint(); break;
                case 4 when wire == 0: type = (uint)reader.ReadVarint(); break;
                case 5 when wire == 0: historical = reader.ReadVarint() != 0; break;
                case 7 when wire == 0: isNull = reader.ReadVarint() != 0; break;
                case 10 when wire == 0: intValue = reader.ReadVarint(); break;
                case 11 when wire == 0: longValue = reader.ReadVarint(); break;
                case 12 when wire == 5: floatValue = BitConverter.Int32BitsToSingle((int)reader.ReadFixed32()); break;
                case 13 when wire == 1: doubleValue = BitConverter.Int64BitsToDouble((long)reader.ReadFixed64()); break;
                case 14 when wire == 0: boolValue = reader.ReadVarint() != 0; break;
                case 15 when wire == 2: stringValue = reader.ReadString(); break;
                case 16 when wire == 2: bytesValue = reader.ReadBytes().ToArray(); break;
                case 17 when wire == 2: reader.ReadBytes(); complex = "DataSet"; break;
                case 18 when wire == 2: reader.ReadBytes(); complex = "Template"; break;
                default: reader.Skip(wire); break;
            }
        }

        object? value = isNull ? null : type switch
        {
            1 => (sbyte)(uint)(intValue ?? 0),
            2 => (short)(uint)(intValue ?? 0),
            3 => (int)(uint)(intValue ?? 0),
            5 => (byte)(intValue ?? 0),
            6 => (ushort)(intValue ?? 0),
            7 => (uint)(intValue ?? 0),
            4 => (long)(longValue ?? 0),
            8 => longValue ?? 0,
            13 => DateTimeOffset.FromUnixTimeMilliseconds((long)(longValue ?? 0)).UtcDateTime,
            9 => floatValue ?? 0f,
            10 => doubleValue ?? 0d,
            11 => boolValue ?? false,
            12 or 14 or 15 => stringValue ?? string.Empty,
            17 or 18 => bytesValue ?? [],
            _ => complex is not null ? $"<{complex}>" : (object?)stringValue ?? (object?)bytesValue ?? (object?)intValue ?? (object?)longValue ?? (object?)doubleValue ?? (object?)floatValue ?? boolValue,
        };
        return new Metric(name, alias, timestamp, type, isNull, historical, value);
    }

    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];

        public void Varint(int field, ulong value)
        {
            Raw((ulong)field << 3);
            Raw(value);
        }

        public void Bytes(int field, byte[] data)
        {
            Raw(((ulong)field << 3) | 2);
            Raw((ulong)data.Length);
            _bytes.AddRange(data);
        }

        public void Fixed32(int field, uint value)
        {
            Raw(((ulong)field << 3) | 5);
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            _bytes.AddRange(b.ToArray());
        }

        public void Fixed64(int field, ulong value)
        {
            Raw(((ulong)field << 3) | 1);
            Span<byte> b = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b, value);
            _bytes.AddRange(b.ToArray());
        }

        public byte[] ToArray() => [.. _bytes];

        private void Raw(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }

            _bytes.Add((byte)value);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public bool TryReadTag(out int field, out int wire)
        {
            if (_pos >= _data.Length)
            {
                field = wire = 0;
                return false;
            }

            var tag = ReadVarint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_pos >= _data.Length)
                {
                    throw new FormatException("Truncated varint.");
                }

                var b = _data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }
            }

            throw new FormatException("Varint too long.");
        }

        public uint ReadFixed32()
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            return value;
        }

        public ulong ReadFixed64() => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

        public ReadOnlySpan<byte> ReadBytes() => Take(checked((int)ReadVarint()));

        public string ReadString() => System.Text.Encoding.UTF8.GetString(ReadBytes());

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: ReadVarint(); break;
                case 1: Take(8); break;
                case 2: ReadBytes(); break;
                case 5: Take(4); break;
                default: throw new FormatException($"Unsupported wire type {wire}.");
            }
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || _pos + length > _data.Length)
            {
                throw new FormatException("Truncated field.");
            }

            var slice = _data.Slice(_pos, length);
            _pos += length;
            return slice;
        }
    }
}
