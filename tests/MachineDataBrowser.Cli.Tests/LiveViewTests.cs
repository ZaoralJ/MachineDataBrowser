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

    [Fact]
    public async Task Live_view_keeps_rows_still_watched_when_the_watch_list_changes()
    {
        var console = new TestConsole().Width(140).Interactive();
        var a = new Node(new Opc.Ua.NodeId("A", 2), "Alpha", "ns=2;s=A");
        var b = new Node(new Opc.Ua.NodeId("B", 2), "Beta", "ns=2;s=B");
        var c = new Node(new Opc.Ua.NodeId("C", 2), "Gamma", "ns=2;s=C");
        static MachineDataBrowser.Core.ValueUpdate Value(Node node, string value) =>
            new(node.Id, value, Opc.Ua.StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow);

        await using (var live = new LiveWatch(console, [a, b]))
        {
            live.Post(a.Name, a.DisplayId, Value(a, "1"));
            live.Post(a.Name, a.DisplayId, Value(a, "2"));
            live.Post(b.Name, b.DisplayId, Value(b, "1"));

            // Run reloading its session: Beta is gone, Gamma is new, Alpha is subscribed again and repeats its value.
            live.SetItems([a, c]);
            live.Post(a.Name, a.DisplayId, Value(a, "2"));
            live.Post(a.Name, a.DisplayId, Value(a, "3"));
        }

        var lastFrame = console.Output[console.Output.LastIndexOf('╭')..];
        var alpha = lastFrame.Split('\n').Single(l => l.Contains("│ Alpha ", StringComparison.Ordinal));
        Assert.EndsWith("3 │", alpha.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("│ Gamma ", lastFrame, StringComparison.Ordinal);
        Assert.DoesNotContain("│ Beta ", lastFrame, StringComparison.Ordinal);
    }
}
