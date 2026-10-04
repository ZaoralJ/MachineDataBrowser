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

    [Fact]
    public async Task Live_view_time_of_a_value_without_source_timestamp_is_when_it_arrived()
    {
        // A write without a source timestamp (as the app sends) leaves the value without one; false is the default.
        using (var writeOut = new StringWriter())
        using (var writeErr = new StringWriter())
        {
            Assert.Equal(0, await Commands.Build(writeOut, writeErr)
                .Parse(["write", server.EndpointUrl, "/Objects/Custom/PauseSimulation", "false", "--yes"])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        var console = new TestConsole().Width(140).Interactive();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await Commands.Build(stdout, stderr, console)
            .Parse(["monitor", server.EndpointUrl, "/Objects/Custom/PauseSimulation", "-r", "1000", "-d", "2s"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        // "Now" at every redraw (5 a second) would look like updates; the time is the server's, and the value read at the
        // start shows none until the first notification brings it.
        var times = console.Output.Split('\n').Where(l => l.Contains("│ PauseSimulation ", StringComparison.Ordinal))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"\d\d:\d\d:\d\d\.\d{3}").Value).Where(t => t.Length > 0).Distinct().ToList();
        Assert.True(times.Count == 1, $"expected one time, got: {string.Join(", ", times)}");
    }
}
