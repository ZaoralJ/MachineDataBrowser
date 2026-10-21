using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.Modbus;

public enum ModbusArea
{
    Coil,
    DiscreteInput,
    InputRegister,
    HoldingRegister,
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These names are the Modbus value types.")]
public enum ModbusValueType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Float32,
    Int64,
    UInt64,
    Float64,
    String,
}

/// <summary>
/// A value in a Modbus device: area, 0-based address and how its registers are decoded, written as
/// <c>area:address[:type][:swap]</c>, e.g. <c>hr:100</c>, <c>hr:20:float32</c>, <c>ir:4:int32:swap</c>,
/// <c>hr:50:string16</c>, <c>co:3</c>. Areas: <c>co</c> coils, <c>di</c> discrete inputs, <c>ir</c> input registers,
/// <c>hr</c> holding registers. Multi-register values are big-endian, high word first; <c>swap</c> puts the low word
/// first (strings: low byte first).
/// </summary>
public sealed record ModbusAddress(ModbusArea Area, ushort Address, ModbusValueType Type = ModbusValueType.UInt16, bool Swap = false, int StringLength = 0)
{
    public bool IsBit => Area is ModbusArea.Coil or ModbusArea.DiscreteInput;

    public bool IsWritable => Area is ModbusArea.Coil or ModbusArea.HoldingRegister;

    /// <summary>Bits (coils, inputs) or 16-bit registers the value occupies.</summary>
    public int Length => Type switch
    {
        ModbusValueType.Bool or ModbusValueType.Int16 or ModbusValueType.UInt16 => 1,
        ModbusValueType.Int32 or ModbusValueType.UInt32 or ModbusValueType.Float32 => 2,
        ModbusValueType.String => (StringLength + 1) / 2,
        _ => 4,
    };

    public string TypeName => Type == ModbusValueType.String
        ? "string" + StringLength.ToString(CultureInfo.InvariantCulture)
        : Type.ToString().ToLowerInvariant();

    public DeviceDataType DataType => Type switch
    {
        ModbusValueType.Bool => DeviceDataType.Boolean,
        ModbusValueType.Int16 => DeviceDataType.Int16,
        ModbusValueType.UInt16 => DeviceDataType.UInt16,
        ModbusValueType.Int32 => DeviceDataType.Int32,
        ModbusValueType.UInt32 => DeviceDataType.UInt32,
        ModbusValueType.Float32 => DeviceDataType.Float,
        ModbusValueType.Int64 => DeviceDataType.Int64,
        ModbusValueType.UInt64 => DeviceDataType.UInt64,
        ModbusValueType.Float64 => DeviceDataType.Double,
        _ => DeviceDataType.String,
    };

    public static string AreaPrefix(ModbusArea area) => area switch
    {
        ModbusArea.Coil => "co",
        ModbusArea.DiscreteInput => "di",
        ModbusArea.InputRegister => "ir",
        _ => "hr",
    };

    public static string AreaName(ModbusArea area) => area switch
    {
        ModbusArea.Coil => "Coils",
        ModbusArea.DiscreteInput => "Discrete Inputs",
        ModbusArea.InputRegister => "Input Registers",
        _ => "Holding Registers",
    };

    public static bool TryParseArea(string text, out ModbusArea area)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "co" or "coil" or "coils" or "0x":
                area = ModbusArea.Coil;
                return true;
            case "di" or "input" or "inputs" or "1x":
                area = ModbusArea.DiscreteInput;
                return true;
            case "ir" or "3x":
                area = ModbusArea.InputRegister;
                return true;
            case "hr" or "4x":
                area = ModbusArea.HoldingRegister;
                return true;
            default:
                area = default;
                return false;
        }
    }

    /// <summary>Modicon reference as shown in device manuals: 1-based with the area digit, e.g. <c>400101</c> for hr:100.</summary>
    public string ModiconReference
    {
        get
        {
            var digit = Area switch
            {
                ModbusArea.Coil => 0,
                ModbusArea.DiscreteInput => 1,
                ModbusArea.InputRegister => 3,
                _ => 4,
            };
            return digit.ToString(CultureInfo.InvariantCulture) + (Address + 1).ToString("D5", CultureInfo.InvariantCulture);
        }
    }

    public override string ToString()
    {
        var text = $"{AreaPrefix(Area)}:{Address.ToString(CultureInfo.InvariantCulture)}";
        if (!IsBit && Type != ModbusValueType.UInt16)
        {
            text += ":" + TypeName;
        }

        return Swap ? text + ":swap" : text;
    }

    public static bool TryParse(string text, out ModbusAddress address)
    {
        address = null!;
        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !TryParseArea(parts[0], out var area)
            || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var bit = area is ModbusArea.Coil or ModbusArea.DiscreteInput;
        var type = bit ? ModbusValueType.Bool : ModbusValueType.UInt16;
        var swap = false;
        var length = 0;
        foreach (var part in parts.Skip(2))
        {
            var lower = part.ToLowerInvariant();
            if (lower == "swap")
            {
                swap = true;
            }
            else if (lower.StartsWith("string", StringComparison.Ordinal))
            {
                if (bit || !int.TryParse(lower.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out length) || length is < 1 or > 250)
                {
                    return false;
                }

                type = ModbusValueType.String;
            }
            else if (TryParseType(lower, out var parsed) && (bit ? parsed == ModbusValueType.Bool : parsed != ModbusValueType.Bool))
            {
                type = parsed;
            }
            else
            {
                return false;
            }
        }

        address = new ModbusAddress(area, number, type, swap, length);
        return number + address.Length <= 65536;
    }

    private static bool TryParseType(string text, out ModbusValueType type)
    {
        type = text switch
        {
            "bool" => ModbusValueType.Bool,
            "int16" or "short" => ModbusValueType.Int16,
            "uint16" or "word" => ModbusValueType.UInt16,
            "int32" or "dint" => ModbusValueType.Int32,
            "uint32" or "dword" => ModbusValueType.UInt32,
            "float32" or "float" or "real" => ModbusValueType.Float32,
            "int64" => ModbusValueType.Int64,
            "uint64" => ModbusValueType.UInt64,
            "float64" or "double" => ModbusValueType.Float64,
            _ => ModbusValueType.String,
        };
        return type != ModbusValueType.String;
    }

    /// <summary>Decodes this value's registers (<see cref="Length"/> of them).</summary>
    public object Decode(ReadOnlySpan<ushort> registers)
    {
        if (Type == ModbusValueType.String)
        {
            var bytes = new byte[registers.Length * 2];
            for (var i = 0; i < registers.Length; i++)
            {
                var (first, second) = ((byte)(registers[i] >> 8), (byte)registers[i]);
                (bytes[2 * i], bytes[(2 * i) + 1]) = Swap ? (second, first) : (first, second);
            }

            var text = Encoding.Latin1.GetString(bytes, 0, StringLength);
            var end = text.IndexOf('\0', StringComparison.Ordinal);
            return end < 0 ? text : text[..end];
        }

        var data = ToBytes(registers);
        return Type switch
        {
            ModbusValueType.Int16 => (object)BinaryPrimitives.ReadInt16BigEndian(data),
            ModbusValueType.UInt16 => (object)BinaryPrimitives.ReadUInt16BigEndian(data),
            ModbusValueType.Int32 => (object)BinaryPrimitives.ReadInt32BigEndian(data),
            ModbusValueType.UInt32 => (object)BinaryPrimitives.ReadUInt32BigEndian(data),
            ModbusValueType.Float32 => (object)BinaryPrimitives.ReadSingleBigEndian(data),
            ModbusValueType.Int64 => (object)BinaryPrimitives.ReadInt64BigEndian(data),
            ModbusValueType.UInt64 => (object)BinaryPrimitives.ReadUInt64BigEndian(data),
            ModbusValueType.Float64 => (object)BinaryPrimitives.ReadDoubleBigEndian(data),
            _ => throw new InvalidOperationException($"{Type} is not a register type."),
        };
    }

    /// <summary>Encodes a value parsed by <see cref="ValueParser"/> (or a string) into registers.</summary>
    public ushort[] Encode(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Type == ModbusValueType.String)
        {
            var bytes = Encoding.Latin1.GetBytes((string)value);
            if (bytes.Length > StringLength)
            {
                throw new FormatException($"{this} holds {StringLength} characters; {bytes.Length} given.");
            }

            Array.Resize(ref bytes, Length * 2);
            var text = new ushort[Length];
            for (var i = 0; i < text.Length; i++)
            {
                var (first, second) = Swap ? (bytes[(2 * i) + 1], bytes[2 * i]) : (bytes[2 * i], bytes[(2 * i) + 1]);
                text[i] = (ushort)((first << 8) | second);
            }

            return text;
        }

        var data = new byte[Length * 2];
        switch (value)
        {
            case short v: BinaryPrimitives.WriteInt16BigEndian(data, v); break;
            case ushort v: BinaryPrimitives.WriteUInt16BigEndian(data, v); break;
            case int v: BinaryPrimitives.WriteInt32BigEndian(data, v); break;
            case uint v: BinaryPrimitives.WriteUInt32BigEndian(data, v); break;
            case float v: BinaryPrimitives.WriteSingleBigEndian(data, v); break;
            case long v: BinaryPrimitives.WriteInt64BigEndian(data, v); break;
            case ulong v: BinaryPrimitives.WriteUInt64BigEndian(data, v); break;
            case double v: BinaryPrimitives.WriteDoubleBigEndian(data, v); break;
            default: throw new FormatException($"Can't write {value.GetType().Name} to {this}.");
        }

        var registers = new ushort[Length];
        for (var i = 0; i < registers.Length; i++)
        {
            registers[Swap ? registers.Length - 1 - i : i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2 * i));
        }

        return registers;
    }

    private byte[] ToBytes(ReadOnlySpan<ushort> registers)
    {
        var data = new byte[registers.Length * 2];
        for (var i = 0; i < registers.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2 * i), registers[Swap ? registers.Length - 1 - i : i]);
        }

        return data;
    }
}
