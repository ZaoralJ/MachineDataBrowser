using Opc.Ua;
using Xunit;

namespace OpcUaBrowser.Core.Tests;

public sealed class WriteValueParseTests
{
    [Fact]
    public void ParsesScalars()
    {
        Assert.Equal(true, ValueParser.Parse("True", BuiltInType.Boolean, isArray: false));
        Assert.Equal(42, ValueParser.Parse(" 42 ", BuiltInType.Int32, isArray: false));
        Assert.Equal((byte)7, ValueParser.Parse("7", BuiltInType.Byte, isArray: false));
        Assert.Equal(1.5, ValueParser.Parse("1.5", BuiltInType.Double, isArray: false));
        Assert.Equal("hi", ValueParser.Parse("\"hi\"", BuiltInType.String, isArray: false));
    }

    [Fact]
    public void ParsesArrays() =>
        Assert.Equal(new short[] { 1, 2, 3 }, ValueParser.Parse("[1, 2, 3]", BuiltInType.Int16, isArray: true));

    [Fact]
    public void RejectsInvalidInput()
    {
        Assert.Throws<FormatException>(() => ValueParser.Parse("abc", BuiltInType.Int32, isArray: false));
        Assert.Throws<FormatException>(() => ValueParser.Parse("300", BuiltInType.Byte, isArray: false));
    }
}
