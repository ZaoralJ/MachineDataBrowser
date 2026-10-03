using MachineDataBrowser.Core.Tests;
using Spectre.Console.Testing;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class LiveViewTests(CustomTypesServerFixture server)
{
    [Fact]
    public async Task Live_view_shows_current_values_at_once_and_counts_only_changes()
    {
        var console = new TestConsole().Width(140).Interactive();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // Values that never change: they are read at the start, and the server's first notification only repeats them.
        var exit = await Commands.Build(stdout, stderr, console)
            .Parse(["monitor", server.EndpointUrl, "/Objects/Custom/DataTypes/Boolean", "/Objects/Custom/DataTypes/Double", "-d", "2s"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("waiting", console.Output, StringComparison.Ordinal);
        var lastFrame = console.Output[console.Output.LastIndexOf('╭')..];
        var rows = lastFrame.Split('\n').Where(l => l.Contains("│ Boolean ", StringComparison.Ordinal) || l.Contains("│ Double ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Contains("Good", row, StringComparison.Ordinal);
            Assert.EndsWith("0 │", row.TrimEnd(), StringComparison.Ordinal);
        });
    }
}
