using System.Text.Json.Nodes;
using Opc.Ua;
using Xunit;

namespace OpcUaBrowser.Core.Tests;

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
        Assert.Equal("[[1,2,3],[4,5,6]]", ValueJson.ToJson(new Matrix(new[] { 1, 2, 3, 4, 5, 6 }, BuiltInType.Int32, 2, 3))!.ToJsonString());

        var info = new BuildInfo { ProductName = "PLC", BuildNumber = "7" };
        var obj = Assert.IsType<JsonObject>(ValueJson.ToJson(new ExtensionObject(info)));
        Assert.Equal("PLC", (string?)obj["ProductName"]);
        Assert.False(obj.ContainsKey("TypeId"));

        Assert.Equal("Int32[]", ValueJson.TypeName(new[] { 1 }));
        Assert.Equal("Boolean", ValueJson.TypeName(true));
        Assert.Equal("BuildInfo", ValueJson.TypeName(new ExtensionObject(info)));
    }
}
