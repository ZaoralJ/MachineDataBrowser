using MachineDataBrowser.Driver.Modbus;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class ModbusAddressTests
{
    [Theory]
    [InlineData("hr:100", "hr:100")]
    [InlineData(" HR:100:uint16 ", "hr:100")]
    [InlineData("4x:20:float", "hr:20:float32")]
    [InlineData("ir:4:int32:swap", "ir:4:int32:swap")]
    [InlineData("hr:50:string16", "hr:50:string16")]
    [InlineData("co:3", "co:3")]
    [InlineData("di:0:bool", "di:0")]
    public void Parses_and_formats_canonically(string text, string expected)
    {
        Assert.True(ModbusAddress.TryParse(text, out var address));
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData("hr")]
    [InlineData("xx:1")]
    [InlineData("hr:-1")]
    [InlineData("hr:65536")]
    [InlineData("hr:65535:float32")]
    [InlineData("co:1:float32")]
    [InlineData("hr:1:bool")]
    [InlineData("hr:1:string0")]
    [InlineData("hr:1:weird")]
    public void Rejects_invalid_addresses(string text) => Assert.False(ModbusAddress.TryParse(text, out _));

    [Fact]
    public void Modicon_reference_is_one_based_with_area_digit()
    {
        Assert.True(ModbusAddress.TryParse("hr:100", out var hr));
        Assert.Equal("400101", hr.ModiconReference);
        Assert.True(ModbusAddress.TryParse("co:0", out var co));
        Assert.Equal("000001", co.ModiconReference);
    }

    [Theory]
    [InlineData("hr:0:float32", 3.5f)]
    [InlineData("hr:0:float32:swap", 3.5f)]
    [InlineData("hr:0:int32", -100000)]
    [InlineData("hr:0:uint32:swap", 123456789u)]
    [InlineData("hr:0:float64", 2.718281828459045)]
    [InlineData("hr:0:int64:swap", -9007199254740993L)]
    [InlineData("hr:0:int16", (short)-1234)]
    [InlineData("hr:0:string9", "MDB-SIM")]
    [InlineData("hr:0:string4:swap", "ab")]
    public void Encode_and_decode_round_trip(string text, object value)
    {
        Assert.True(ModbusAddress.TryParse(text, out var address));
        var registers = address.Encode(value);
        Assert.Equal(address.Length, registers.Length);
        Assert.Equal(value, address.Decode(registers));
    }

    [Fact]
    public void Words_are_big_endian_high_word_first_unless_swapped()
    {
        Assert.True(ModbusAddress.TryParse("hr:0:uint32", out var normal));
        Assert.True(ModbusAddress.TryParse("hr:0:uint32:swap", out var swapped));
        Assert.Equal([0x1234, 0x5678], normal.Encode(0x12345678u));
        Assert.Equal([0x5678, 0x1234], swapped.Encode(0x12345678u));
    }

    [Fact]
    public void Too_long_strings_are_rejected() =>
        Assert.Throws<FormatException>(() => new ModbusAddress(ModbusArea.HoldingRegister, 0, ModbusValueType.String, StringLength: 2).Encode("abc"));

    [Fact]
    public void Nearby_addresses_of_one_area_are_read_together()
    {
        ModbusAddress Parse(string text) => ModbusAddress.TryParse(text, out var a) ? a : throw new FormatException(text);
        var addresses = new[] { Parse("hr:10"), Parse("co:1"), Parse("hr:20:float32"), Parse("hr:200"), Parse("hr:0"), Parse("co:5") };

        var ranges = ModbusClient.PlanReads(addresses);

        Assert.Equal(3, ranges.Count);
        var registers = Assert.Single(ranges, r => r.Area == ModbusArea.HoldingRegister && r.Start == 0);
        Assert.Equal(22, registers.Count);
        Assert.Equal([4, 0, 2], registers.Items);
        Assert.Contains(ranges, r => r.Area == ModbusArea.HoldingRegister && r.Start == 200 && r.Count == 1);
        Assert.Contains(ranges, r => r.Area == ModbusArea.Coil && r.Start == 1 && r.Count == 5);
    }
}

public sealed class ModbusTagMapTests
{
    [Fact]
    public void Parses_names_addresses_units_and_descriptions()
    {
        var map = ModbusTagMap.Parse("""
            # plant map
            name,address,unit,description
            Line1/Motor/Speed, hr:20:float , rpm,"Motor speed, measured"
            Line1/Running,co:1

            Counter,hr:0
            """);

        Assert.Equal(["Line1/Motor/Speed", "Line1/Running", "Counter"], map.Tags.Select(t => t.Path));
        var speed = map.ByAddress["hr:20:float32"];
        Assert.Equal("Speed", speed.Name);
        Assert.Equal("rpm", speed.Unit);
        Assert.Equal("Motor speed, measured", speed.Description);
        Assert.Null(map.ByAddress["co:1"].Unit);
    }

    [Fact]
    public void Semicolons_separate_when_there_are_no_commas() =>
        Assert.Equal("°C", ModbusTagMap.Parse("Oven/Temp;ir:20:float32;°C").Tags[0].Unit);

    [Theory]
    [InlineData("Speed", "line 1: expected name,address")]
    [InlineData("Speed,hr:x", "not a Modbus address")]
    [InlineData("Speed,hr:1\nSpeed,hr:2", "named twice")]
    [InlineData("Motor,hr:1\nMotor/Speed,hr:2", "both a value and a folder")]
    public void Reports_bad_lines(string text, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => ModbusTagMap.Parse(text)).Message, StringComparison.Ordinal);

    [Fact]
    public void Session_files_refer_to_maps_beside_them_relatively()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mdb-maps");
        var session = Path.Combine(folder, "line.mdbsession");

        Assert.Equal("maps/line1.csv", TagMapReference.ToStored(Path.Combine(folder, "maps", "line1.csv"), session));
        var elsewhere = Path.GetFullPath(Path.Combine(folder, "..", "other", "map.csv"));
        Assert.Equal(elsewhere, TagMapReference.ToStored(elsewhere, session));
        Assert.Equal(Path.Combine(folder, "maps", "line1.csv"), TagMapReference.Resolve("maps/line1.csv", session));
        Assert.Equal(elsewhere, TagMapReference.Resolve(elsewhere, session));
    }
}
