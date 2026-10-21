using System.Text.Json.Nodes;
using Opc.Ua;
using MachineDataBrowser.Driver.OpcUa;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class ValueJsonTests
{
    [Fact]
    public void Scalars_keep_json_types()
    {
        Assert.Equal("true", ValueJson.ToJson(true)!.ToJsonString());
        Assert.Equal("42", ValueJson.ToJson((ushort)42)!.ToJsonString());
        Assert.Equal("18446744073709551615", ValueJson.ToJson(ulong.MaxValue)!.ToJsonString());
        Assert.Equal("1.5", ValueJson.ToJson(1.5f)!.ToJsonString());
        Assert.Equal("\"Infinity\"", ValueJson.ToJson(double.PositiveInfinity)!.ToJsonString());
        Assert.Equal("\"abc\"", ValueJson.ToJson("abc")!.ToJsonString());
        Assert.Null(ValueJson.ToJson(null));
        Assert.Equal("\"AQI=\"", ValueJson.ToJson(new byte[] { 1, 2 })!.ToJsonString());
        Assert.Equal("\"2026-09-27T10:00:00.0000000Z\"", ValueJson.ToJson(new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc))!.ToJsonString());
    }

    [Fact]
    public void Arrays_matrices_and_structures()
    {
        Assert.Equal("[1,2,3]", ValueJson.ToJson(new[] { 1, 2, 3 })!.ToJsonString());
        var twoDimensional = new[,] { { 1, 2, 3 }, { 4, 5, 6 } };
        var matrix = twoDimensional.ToMatrixOf();
        Assert.Equal("[[1,2,3],[4,5,6]]", ValueJson.ToJson(matrix)!.ToJsonString());
        Assert.Equal("Int32[2,3]", ValueJson.TypeName(matrix));
        Assert.Equal("[[1,2,3],[4,5,6]]", ValueJson.ToJson(twoDimensional)!.ToJsonString());
        Assert.Equal("Int32[2,3]", ValueJson.TypeName(twoDimensional));
        Assert.Equal("[[1,2,3],[4,5,6]]", UaValueFormatter.Format(twoDimensional));

        var info = new BuildInfo { ProductName = "PLC", BuildNumber = "7" };
        var obj = Assert.IsType<JsonObject>(ValueJson.ToJson(new ExtensionObject(info)));
        Assert.Equal("PLC", (string?)obj["ProductName"]);
        Assert.False(obj.ContainsKey("TypeId"));

        Assert.Equal("SByte[]", ValueJson.TypeName(new sbyte[] { -1, 1 }));
        Assert.Equal("[-1,1]", ValueJson.ToJson(new sbyte[] { -1, 1 })!.ToJsonString());
        Assert.Equal("Int32[]", ValueJson.TypeName(new[] { 1 }));
        Assert.Equal("Boolean", ValueJson.TypeName(true));
        Assert.Equal("BuildInfo", ValueJson.TypeName(new ExtensionObject(info)));
    }

    [Fact]
    public void Opc_ua_byte_strings_and_utc_timestamps_keep_their_json_types()
    {
        var bytes = ByteString.From([1, 2, 3]);
        var timestamp = DateTimeUtc.From(new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal("\"AQID\"", ValueJson.ToJson(bytes)!.ToJsonString());
        Assert.Equal("\"2026-09-27T10:00:00.0000000Z\"", ValueJson.ToJson(timestamp)!.ToJsonString());
        Assert.Equal("ByteString", ValueJson.TypeName(bytes));
        Assert.Equal("ByteString[3] 010203", UaValueFormatter.Format(Variant.From(bytes)));
    }

    [Fact]
    public void Typed_byte_arrays_remain_numeric_and_distinct_from_byte_strings()
    {
        var usints = new DeviceArray("USINT", new byte[] { 240, 241 });
        Assert.Equal("USINT[]", ValueJson.TypeName(usints));
        Assert.Equal("[240,241]", ValueJson.ToJson(usints)!.ToJsonString());
        Assert.Equal("[240, 241]", ValueFormatter.Format(usints));

        var bytes = new byte[] { 1, 2 };
        Assert.Equal("ByteString", ValueJson.TypeName(bytes));
        Assert.Equal("\"AQI=\"", ValueJson.ToJson(bytes)!.ToJsonString());
        Assert.Equal("ByteString[2] 0102", ValueFormatter.Format(bytes));
    }

    [Fact]
    public void Full_value_formatter_does_not_shorten_arrays_or_byte_strings()
    {
        var values = Enumerable.Range(0, 40).ToArray();
        var bytes = Enumerable.Range(0, 40).Select(value => (byte)value).ToArray();

        Assert.Contains("… (+8)", ValueFormatter.Format(values), StringComparison.Ordinal);
        Assert.EndsWith("37, 38, 39]", ValueFormatter.FormatFull(values), StringComparison.Ordinal);
        Assert.Contains("…", ValueFormatter.Format(bytes), StringComparison.Ordinal);
        Assert.EndsWith(Convert.ToHexString(bytes), ValueFormatter.FormatFull(bytes), StringComparison.Ordinal);
    }
}
