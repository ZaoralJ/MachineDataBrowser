using Xunit;

namespace OpcUaBrowser.Core.Tests;

public sealed class ValueDisplayTests
{
    [Theory]
    [InlineData((short)-1, ValueFormat.Hex, "0xFFFF")]
    [InlineData((byte)10, ValueFormat.Hex, "0x0A")]
    [InlineData(255u, ValueFormat.Hex, "0x000000FF")]
    [InlineData((ushort)0b1010_0000_0000_0101, ValueFormat.Binary, "1010 0000 0000 0101")]
    [InlineData(0b1000_1001, ValueFormat.Bits, "bits 0, 3, 7")]
    [InlineData(0, ValueFormat.Bits, "no bits set")]
    public void Integers_show_as_hex_binary_or_set_bits(object raw, ValueFormat format, string expected) =>
        Assert.Equal(expected, new ValueDisplay { Format = format }.Apply(raw, raw.ToString()!));

    [Fact]
    public void Scaling_decimals_and_units()
    {
        // A raw 0..27648 analog input scaled to 0..100 %.
        var percent = new ValueDisplay { Format = ValueFormat.Decimals, Decimals = 1, Gain = 100.0 / 27648, Unit = "%" };
        Assert.Equal("50.0 %", percent.Apply(13824, "13824"));

        Assert.Equal("81.5 °C", ValueDisplay.Default.Apply(81.5, "81.5", "°C"));                  // server unit
        Assert.Equal("81.5", (ValueDisplay.Default with { Unit = "" }).Apply(81.5, "81.5", "°C")); // unit turned off
        Assert.Equal("1500", ValueDisplay.Default.Apply(1500, "1500"));
        Assert.Equal("150.5", new ValueDisplay { Gain = 0.1, Offset = 0.5 }.Apply(1500, "1500"));
        Assert.Equal("3.14", new ValueDisplay { Format = ValueFormat.Decimals }.Apply(3.14159f, "3.14159"));
    }

    [Fact]
    public void Non_numbers_and_floats_in_integer_formats_are_left_alone()
    {
        var hex = new ValueDisplay { Format = ValueFormat.Hex, Unit = "x" };
        Assert.Equal("AUTO", hex.Apply("AUTO", "AUTO"));
        Assert.Equal("True", hex.Apply(true, "True"));
        Assert.Equal("[1, 2]", hex.Apply(new[] { 1, 2 }, "[1, 2]"));
        Assert.Equal("2.5 x", hex.Apply(2.5, "2.5"));   // a float stays a number
        Assert.Equal("…", hex.Apply(null, "…"));
    }
}
