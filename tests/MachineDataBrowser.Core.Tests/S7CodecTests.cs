using MachineDataBrowser.Driver.S7;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

/// <summary>S7 addresses, data types, value encoding, symbol tables and endpoint URLs, without a PLC.</summary>
public sealed class S7CodecTests
{
    [Theory]
    [InlineData("DB1.DBX0.0", "DB1.DBX0.0")]
    [InlineData("%db1.dbd4:real", "DB1.DBD4:REAL")]
    [InlineData("DB1.DBD4", "DB1.DBD4")]
    [InlineData("DB1.DBB14:LREAL", "DB1.DBB14:LREAL")]
    [InlineData("DB1.DBW22:Int", "DB1.DBW22:INT")]
    [InlineData("E0.1", "I0.1")]
    [InlineData("AW2", "QW2")]
    [InlineData("M 10.7", "M10.7")]
    [InlineData("MB3", "MB3")]
    [InlineData("MW2:INT", "MW2:INT")]
    [InlineData("MD4:REAL", "MD4:REAL")]
    [InlineData("DB2.DBD0:REAL[10]", "DB2.DBD0:REAL[10]")]
    [InlineData("DB2.DBB0:Array[0..9] of Real", "DB2.DBD0:REAL[10]")]
    [InlineData("DB1.DBB26:String[20]", "DB1.DBB26:STRING[20]")]
    [InlineData("DB1.DBB26:STRING", "DB1.DBB26:STRING[254]")]
    [InlineData("DB2.DBX60.0:BOOL[16]", "DB2.DBX60.0:BOOL[16]")]
    [InlineData("DB3.DBD94:Time_Of_Day", "DB3.DBD94:TIME_OF_DAY")]
    public void Addresses_parse_to_canonical_form(string text, string expected) =>
        Assert.Equal(expected, S7Address.Parse(text).ToString());

    [Theory]
    [InlineData("DB1.DBX0")]
    [InlineData("M10")]
    [InlineData("DB0.DBB0")]
    [InlineData("DB1.DBB0.1")]
    [InlineData("MW2:BOOL")]
    [InlineData("M0.0:INT")]
    [InlineData("M0.8")]
    [InlineData("X1")]
    [InlineData("DB1.DBD4:DTL")]
    [InlineData("Machine.Speed")]
    public void Invalid_addresses_are_rejected(string text) =>
        Assert.False(S7Address.TryParse(text, out _));

    [Fact]
    public void Byte_lengths_cover_bits_strings_and_arrays()
    {
        Assert.Equal(1, S7Address.Parse("M0.7").ByteLength);
        Assert.Equal(2, S7Address.Parse("DB2.DBX60.0:BOOL[16]").ByteLength);
        Assert.Equal(3, S7Address.Parse("DB2.DBX60.7:BOOL[10]").ByteLength);
        Assert.Equal(22, S7Address.Parse("DB1.DBB26:STRING[20]").ByteLength);
        Assert.Equal(24, S7Address.Parse("DB3.DBB40:WSTRING[10]").ByteLength);
        Assert.Equal(40, S7Address.Parse("DB2.DBD0:REAL[10]").ByteLength);
    }

    [Theory]
    [InlineData("REAL", "1.5")]
    [InlineData("LREAL", "-2.25")]
    [InlineData("INT", "-1234")]
    [InlineData("UINT", "60000")]
    [InlineData("DINT", "-123456")]
    [InlineData("UDINT", "4000000000")]
    [InlineData("LINT", "-9000000000")]
    [InlineData("ULINT", "18000000000")]
    [InlineData("WORD", "48879")]
    [InlineData("SINT", "-5")]
    [InlineData("CHAR", "S")]
    [InlineData("WCHAR", "ř")]
    [InlineData("STRING[10]", "Hello")]
    [InlineData("WSTRING[10]", "Grüße")]
    [InlineData("TIME", "00:01:30")]
    [InlineData("TIME_OF_DAY", "13:45:30.2500000")]
    [InlineData("DATE", "2026-10-21T00:00:00.0000000")]
    [InlineData("INT[3]", "[1, -2, 3]")]
    [InlineData("BOOL[3]", "[True, False, True]")]
    public void Values_round_trip_through_the_encoding(string type, string text)
    {
        Assert.True(S7Type.TryParse(type, out var s7Type));
        var buffer = new byte[s7Type.ByteLength(0)];
        s7Type.Encode(s7Type.ParseInput(text), buffer, 0);
        Assert.Equal(text, ValueFormatter.Format(s7Type.Decode(buffer, 0)));
    }

    [Fact]
    public void Values_are_big_endian_and_strings_carry_their_lengths()
    {
        var real = new byte[4];
        S7Address.Parse("MD0:REAL").Type.Encode(3.14f, real, 0);
        Assert.Equal([0x40, 0x48, 0xF5, 0xC3], real);

        var text = new byte[12];
        S7Address.Parse("DB1.DBB0:STRING[10]").Type.Encode("Hi", text, 0);
        Assert.Equal([10, 2, (byte)'H', (byte)'i', 0, 0, 0, 0, 0, 0, 0, 0], text);
        Assert.Throws<FormatException>(() => S7Address.Parse("DB1.DBB0:STRING[1]").Type.Encode("Hi", text, 0));
    }

    [Fact]
    public void Bool_arrays_at_a_bit_offset_keep_the_neighbouring_bits()
    {
        var address = S7Address.Parse("M0.6:BOOL[4]");
        byte[] buffer = [0b0000_0001, 0b1111_0000];
        address.Type.Encode(address.Type.ParseInput("1,1,1,1"), buffer, address.Bit);
        Assert.Equal([0b1100_0001, 0b1111_0011], buffer);
        Assert.Equal("[True, True, True, True]", ValueFormatter.Format(address.Type.Decode(buffer, address.Bit)));
    }

    [Fact]
    public void Array_writes_need_every_element() =>
        Assert.Throws<FormatException>(() => S7Address.Parse("MW0:INT[3]").Type.Encode(new short[] { 1, 2 }, new byte[6], 0));

    [Fact]
    public void Tia_portal_csv_export_becomes_a_symbol_tree()
    {
        var list = S7TagList.Parse("""
            Name,Path,Data Type,Logical Address,Comment,Hmi Visible
            Motor.Speed,Default tag table,Real,%MD10,"Speed, rpm",True
            Motor.Running,Default tag table,Bool,%M0.0,,True
            "Lamp",IO/Panel,Bool,%Q1.3,,True
            Recipe,Default tag table,"Array[1..5] of Int",%DB5.DBW0,,True
            Settings,Default tag table,MyUdt,%DB6.DBB0,,True
            Optimized,Default tag table,Int,,,True
            Motor.Speed,Default tag table,Real,%MD20,,True
            """);

        Assert.Equal(["Motor.Speed", "Motor.Running", "Lamp", "Recipe"], list.Symbols.Select(s => s.Name));
        var speed = list.Symbols[0];
        Assert.Equal(["Default tag table", "Motor"], speed.Folders);
        Assert.Equal("Speed", speed.DisplayName);
        Assert.Equal("MD10:REAL", speed.Address.ToString());
        Assert.Equal("Speed, rpm", speed.Comment);
        Assert.Equal(["IO", "Panel"], list.Symbols[2].Folders);
        Assert.Equal("DB5.DBW0:INT[5]", list.Symbols[3].Address.ToString());
        Assert.Equal(3, list.Skipped.Count);
        Assert.Contains(list.Skipped, s => s.StartsWith("Settings: data type 'MyUdt'", StringComparison.Ordinal));
        Assert.Contains(list.Skipped, s => s.StartsWith("Optimized: no absolute address", StringComparison.Ordinal));
        Assert.Contains(list.Skipped, s => s.StartsWith("Motor.Speed: defined more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Step7_sdf_export_without_header_is_read_positionally()
    {
        var list = S7TagList.Parse("""
            "Motor_On                ","M      0.0","BOOL      ","Motor running"
            "Level                   ","EW    10  ","INT       ",""
            "Valve                   ","A      4.1","BOOL      ",""
            """);

        Assert.Equal(["Motor_On", "Level", "Valve"], list.Symbols.Select(s => s.Name));
        Assert.Equal(["M0.0", "IW10:INT", "Q4.1"], list.Symbols.Select(s => s.Address.ToString()));
        Assert.Equal("Motor running", list.Symbols[0].Comment);
        Assert.Empty(list.Symbols[0].Folders);
    }

    [Fact]
    public void Semicolon_separated_lists_are_detected() =>
        Assert.Equal("DB1.DBD2:REAL", S7TagList.Parse("Symbol;Address;Type\nSpeed;DB1.DBD2;Real\n").Symbols.Single().Address.ToString());

    [Fact]
    public void Endpoint_defaults_to_port_102_rack_0_slot_1_and_s7_1500()
    {
        var endpoint = S7Client.S7Endpoint.Parse("s7://10.0.0.5");
        Assert.Equal(("10.0.0.5", 102, (short)0, (short)1, S7.Net.CpuType.S71500), (endpoint.Host, endpoint.Port, endpoint.Rack, endpoint.Slot, endpoint.Cpu));
        Assert.Null(endpoint.TagFile);
        Assert.Equal(64, endpoint.AreaBytes);
    }

    [Fact]
    public void Endpoint_options_come_from_path_and_query()
    {
        var endpoint = S7Client.S7Endpoint.Parse("s7://plc:1102/0,2?cpu=S7-300&tags=%2Ftmp%2Fmy%20tags.csv&db=1,DB20&bytes=16&timeout=1500");
        Assert.Equal(1102, endpoint.Port);
        Assert.Equal((short)2, endpoint.Slot);
        Assert.Equal(S7.Net.CpuType.S7300, endpoint.Cpu);
        Assert.Equal("/tmp/my tags.csv", endpoint.TagFile);
        Assert.Equal([1, 20], endpoint.DataBlocks);
        Assert.Equal(16, endpoint.AreaBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), endpoint.Timeout);
    }

    [Theory]
    [InlineData("s7://")]
    [InlineData("eip://plc")]
    [InlineData("s7://plc/1")]
    [InlineData("s7://plc/0,1?cpu=1600")]
    [InlineData("s7://plc/0,1?unknown=1")]
    public void Invalid_endpoints_are_rejected(string url) =>
        Assert.Throws<FormatException>(() => S7Client.S7Endpoint.Parse(url));

    [Fact]
    public async Task Connect_with_an_invalid_url_reports_it()
    {
        await using var client = new S7Client();
        var ex = await Assert.ThrowsAsync<DeviceClientException>(() => client.ConnectAsync(new ConnectOptions { EndpointUrl = "s7://plc/9" }, TestContext.Current.CancellationToken));
        Assert.Equal(DeviceStatusCodes.BadTcpEndpointUrlInvalid, ex.StatusCode);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public void Portable_ids_distinguish_addresses_symbols_and_folders()
    {
        var client = new S7Client();
        var address = client.ParsePortableId("%db1.dbd2:real");
        Assert.Equal(S7Client.AddressNamespace, address.NamespaceIndex);
        Assert.Equal("DB1.DBD2:REAL", client.ToPortableId(address));

        var symbol = client.ParsePortableId("\"Machine.Speed\"");
        Assert.Equal(S7Client.SymbolNamespace, symbol.NamespaceIndex);
        Assert.Equal("\"Machine.Speed\"", client.ToPortableId(symbol));
        Assert.Equal(symbol, client.ParsePortableId("Machine.Speed"));

        var folder = client.ParsePortableId("@Tags/Machine");
        Assert.Equal(S7Client.FolderNamespace, folder.NamespaceIndex);
        Assert.Equal("@Tags/Machine", client.ToPortableId(folder));
    }
}
