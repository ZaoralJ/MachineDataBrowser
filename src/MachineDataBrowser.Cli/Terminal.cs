using System.Collections.Concurrent;
using System.Globalization;
using MachineDataBrowser.Core;
using Opc.Ua;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MachineDataBrowser.Cli;

/// <summary>Receives value updates from a monitor; written out or shown live. Disposing flushes and stops.</summary>
internal interface IUpdateSink : IAsyncDisposable
{
    /// <summary>Whether the sink shows current values before monitoring starts (the live table does; streams don't).</summary>
    bool WantsInitialValues => false;

    /// <summary>The value read before monitoring started; not an update.</summary>
    void Seed(string id, ValueUpdate current)
    {
    }

    void Post(string name, string id, ValueUpdate update);
}

/// <summary>Rich output for an interactive terminal: tables, trees and a live watch view (text format only).</summary>
internal static class Terminal
{
    public static void Table(IAnsiConsole console, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey);
        foreach (var header in headers)
        {
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(header)}[/]"));
        }

        foreach (var row in rows)
        {
            table.AddRow([.. row.Select(cell => (IRenderable)new Text(cell))]);
        }

        console.Write(table);
    }

    /// <summary>The browse result as a tree; <paramref name="rows"/> are (path, class, id) with paths below <paramref name="prefix"/>.</summary>
    public static void Tree(IAnsiConsole console, string rootLabel, string prefix, IEnumerable<IReadOnlyList<string>> rows)
    {
        var tree = new Tree($"[bold]{Markup.Escape(rootLabel)}[/]").Guide(TreeGuide.Line);
        var nodes = new Dictionary<string, IHasTreeNodes>(StringComparer.Ordinal) { [prefix] = tree };
        foreach (var row in rows)
        {
            var path = row[0];
            var parentPath = path[..path.LastIndexOf('/')];
            var parent = nodes.GetValueOrDefault(parentPath) ?? tree;
            nodes[path] = parent.AddNode($"{Icon(row[1])} {Markup.Escape(path[(parentPath.Length + 1)..])}  [grey]{Markup.Escape(row[2])}[/]");
        }

        console.Write(tree);
    }

    public static string StatusMarkup(StatusCode status)
    {
        var text = Markup.Escape(Output.Status(status));
        return StatusCode.IsGood(status) ? $"[green]{text}[/]" : StatusCode.IsUncertain(status) ? $"[yellow]{text}[/]" : $"[red]{text}[/]";
    }

    private static string Icon(string nodeClass) => nodeClass switch
    {
        nameof(NodeClass.Variable) => "[blue]●[/]",
        nameof(NodeClass.Method) => "[magenta]ƒ[/]",
        _ => "[yellow]▸[/]",
    };
}

/// <summary>
/// The watched items as a table that updates in place, like the app's Watch list: one row per item with its latest
/// status, value, time and update count. Redrawn a few times a second, however fast values arrive.
/// </summary>
internal sealed class LiveWatch : IUpdateSink
{
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyList<Node> _items;
    private readonly bool _showIds;
    private readonly ConcurrentDictionary<string, (ValueUpdate Update, int Count, bool Seeded, DateTime ReceivedAt)> _latest = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _render;

    public LiveWatch(IAnsiConsole console, IReadOnlyList<Node> items, bool showIds = false)
    {
        _items = items;
        _showIds = showIds;
        _render = console.Live(Build()).AutoClear(false).StartAsync(async context =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RedrawInterval, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Stopping: fall through to the final redraw.
                }

                context.UpdateTarget(Build());
            }
        });
    }

    public bool WantsInitialValues => true;

    public void Seed(string id, ValueUpdate current) => _latest.TryAdd(id, (current, 0, true, DateTime.Now));

    /// <summary>
    /// Counts updates. Right after subscribing, servers (and Logix polling, and MQTT retained messages) send the current
    /// value once more; when that only repeats the value read at the start, it is not counted.
    /// </summary>
    public void Post(string name, string id, ValueUpdate update) =>
        _latest.AddOrUpdate(id, (update, 1, false, DateTime.Now), (_, previous) =>
            previous.Seeded && previous.Count == 0 && previous.Update.Value == update.Value && StatusCode.IsGood(update.Status)
                ? (update, 0, false, DateTime.Now)
                : (update, previous.Count + 1, false, DateTime.Now));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _render.ConfigureAwait(false);
        _stop.Dispose();
    }

    private Table Build()
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey).AddColumn("[bold]Name[/]");
        if (_showIds)
        {
            table.AddColumn("[bold]Id[/]");
        }

        table
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Value[/]")
            .AddColumn(new TableColumn("[bold]Updated[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Updates[/]").RightAligned());
        foreach (var item in _items)
        {
            var name = _showIds ? new IRenderable[] { new Text(item.Name), new Markup($"[grey]{Markup.Escape(item.DisplayId)}[/]") } : [new Text(item.Name)];
            if (_latest.TryGetValue(item.DisplayId, out var latest))
            {
                // OPC UA servers may leave out the source timestamp: then the server's, else when it arrived ("now"
                // at every redraw would look like an update). The value read at the start has none until the first
                // notification brings it.
                var time = Output.Time(latest.Update) is { } stamp ? stamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    : latest.Seeded ? string.Empty
                    : latest.ReceivedAt.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                table.AddRow([
                    .. name,
                    new Markup(Terminal.StatusMarkup(latest.Update.Status)),
                    new Text(latest.Update.Value),
                    new Text(time),
                    new Text(latest.Count.ToString(CultureInfo.InvariantCulture))]);
            }
            else
            {
                table.AddRow([.. name, new Markup("[grey]waiting[/]"), new Text("…"), new Text(string.Empty), new Text("0")]);
            }
        }

        return table;
    }
}
