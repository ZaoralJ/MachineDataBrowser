using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class JsonValueFormattingTests
{
    [Theory]
    [InlineData("{\"enterprise\":\"ACME Toys\",\"areas\":[\"moulding\"]}", "{\n  \"enterprise\": \"ACME Toys\",\n  \"areas\": [\n    \"moulding\"\n  ]\n}")]
    [InlineData("[1,2]", "[\n  1,\n  2\n]")]
    public void Valid_json_is_indented(string value, string expected)
    {
        Assert.True(JsonValueFormatting.TryFormat(value, out var formatted));
        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"unfinished\":")]
    [InlineData("")]
    public void Invalid_or_empty_text_is_not_formatted(string value)
    {
        Assert.False(JsonValueFormatting.TryFormat(value, out var formatted));
        Assert.Equal(value, formatted);
    }
}
