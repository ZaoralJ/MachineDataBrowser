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
    private readonly ConcurrentDictionary<string, (ValueUpdate Update, int Count)> _latest = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _render;

    public LiveWatch(IAnsiConsole console, IReadOnlyList<Node> items)
    {
        _items = items;
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

    public void Post(string name, string id, ValueUpdate update) =>
        _latest.AddOrUpdate(id, (update, 1), (_, previous) => (update, previous.Count + 1));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _render.ConfigureAwait(false);
        _stop.Dispose();
    }

    private Table Build()
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey)
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Value[/]")
            .AddColumn(new TableColumn("[bold]Updated[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Updates[/]").RightAligned());
        foreach (var item in _items)
        {
            if (_latest.TryGetValue(item.DisplayId, out var latest))
            {
                var time = latest.Update.SourceTimestamp == DateTime.MinValue ? DateTime.Now : latest.Update.SourceTimestamp.ToLocalTime();
                table.AddRow(
                    new Text(item.Name),
                    new Markup(Terminal.StatusMarkup(latest.Update.Status)),
                    new Text(latest.Update.Value),
                    new Text(time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    new Text(latest.Count.ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                table.AddRow(new Text(item.Name), new Markup("[grey]waiting[/]"), new Text("…"), new Text(string.Empty), new Text("0"));
            }
        }

        return table;
    }
}
