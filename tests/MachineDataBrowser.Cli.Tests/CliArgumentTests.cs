using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class CliArgumentTests
{
    [Theory]
    [InlineData("opc.tcp://plc:4840")]
    [InlineData("eip://10.0.0.5/1,0")]
    [InlineData("mqtt://broker:1883")]
    public void Endpoint_shorthand_opens_the_TUI(string endpoint)
    {
        Assert.Equal(new[] { "tui", endpoint }, Commands.NormalizeArgs([endpoint]));
    }

    [Fact]
    public void Endpoint_shorthand_preserves_TUI_options()
    {
        var args = new[] { "opc.tcp://plc:4840", "--user", "operator", "--secure" };

        Assert.Equal(
            new[] { "tui", "opc.tcp://plc:4840", "--user", "operator", "--secure" },
            Commands.NormalizeArgs(args));
    }

    [Theory]
    [InlineData("browse", "opc.tcp://plc:4840")]
    [InlineData("--help")]
    [InlineData("line1.mdbsession")]
    public void Other_invocations_are_unchanged(params string[] args)
    {
        Assert.Same(args, Commands.NormalizeArgs(args));
    }
}
