using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class WriteValueParseTests
{
    [Fact]
    public void ParsesScalars()
    {
        Assert.Equal(true, ValueParser.Parse("True", DeviceDataType.Boolean, isArray: false));
        Assert.Equal(42, ValueParser.Parse(" 42 ", DeviceDataType.Int32, isArray: false));
        Assert.Equal((byte)7, ValueParser.Parse("7", DeviceDataType.Byte, isArray: false));
        Assert.Equal(1.5, ValueParser.Parse("1.5", DeviceDataType.Double, isArray: false));
        Assert.Equal("hi", ValueParser.Parse("\"hi\"", DeviceDataType.String, isArray: false));
    }

    [Fact]
    public void ParsesArrays() =>
        Assert.Equal(new short[] { 1, 2, 3 }, ValueParser.Parse("[1, 2, 3]", DeviceDataType.Int16, isArray: true));

    [Fact]
    public void RejectsInvalidInput()
    {
        Assert.Throws<FormatException>(() => ValueParser.Parse("abc", DeviceDataType.Int32, isArray: false));
        Assert.Throws<FormatException>(() => ValueParser.Parse("300", DeviceDataType.Byte, isArray: false));
    }
}
