using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MachineDataBrowser.Core;
using Opc.Ua;

namespace MachineDataBrowser.Cli;

internal enum OutputFormat
{
    Text,
    Json,
    Csv,
}

/// <summary>
/// Writes value updates as aligned text, JSON lines or CSV. Updates arrive on the protocol client's threads; they are
/// queued and written by one async loop, so a slow terminal or pipe never blocks the client.
/// </summary>
internal sealed class UpdateWriter : IUpdateSink
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private readonly Channel<(string Name, string Id, ValueUpdate Update)> _queue =
        Channel.CreateUnbounded<(string, string, ValueUpdate)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly TextWriter _output;
    private readonly OutputFormat _format;
    private readonly bool _showIds;
    private readonly Task _pump;

    public UpdateWriter(TextWriter output, OutputFormat format, bool showIds = false)
    {
        _output = output;
        _format = format;
        _showIds = showIds;
        _pump = PumpAsync();
    }

    public void Post(string name, string id, ValueUpdate update) => _queue.Writer.TryWrite((name, id, update));

    /// <summary>Writes what is still queued, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        if (_format == OutputFormat.Csv)
        {
            await _output.WriteLineAsync("time,name,id,status,value").ConfigureAwait(false);
        }

        await foreach (var (name, id, update) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _output.WriteLineAsync(Line(name, id, update)).ConfigureAwait(false);
            if (!_queue.Reader.TryPeek(out _))
            {
                await _output.FlushAsync().ConfigureAwait(false);
            }
        }

        await _output.FlushAsync().ConfigureAwait(false);
    }

    private string Line(string name, string id, ValueUpdate update)
    {
        var time = update.SourceTimestamp == DateTime.MinValue ? DateTime.UtcNow : update.SourceTimestamp;
        var utc = time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var status = Output.Status(update.Status);
        return _format switch
        {
            OutputFormat.Json => new JsonObject
            {
                ["time"] = utc,
                ["name"] = name,
                ["id"] = id,
                ["status"] = status,
                ["value"] = update.Raw is null ? JsonValue.Create(update.Value) : ValueJson.ToJson(update.Raw),
            }.ToJsonString(Compact),
            OutputFormat.Csv => string.Join(',', Output.Csv(utc), Output.Csv(name), Output.Csv(id), Output.Csv(status), Output.Csv(update.Value)),
            _ => _showIds
                ? $"{time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {name,-28} {id,-28} {status,-12} {update.Value}"
                : $"{time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {name,-28} {status,-12} {update.Value}",
        };
    }
}

internal static class Output
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Status(StatusCode status) =>
        StatusText.Of(status);

    public static string Csv(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) < 0 ? text : $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>Rows as an aligned text table, a JSON array of objects or CSV.</summary>
    public static async Task RowsAsync(TextWriter output, OutputFormat format, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var all = rows.ToList();
        switch (format)
        {
            case OutputFormat.Json:
                var array = new JsonArray([.. all.Select(row =>
                {
                    var item = new JsonObject();
                    for (var i = 0; i < headers.Count; i++)
                    {
                        item[headers[i]] = row[i];
                    }

                    return (JsonNode)item;
                })]);
                await output.WriteLineAsync(array.ToJsonString(Indented)).ConfigureAwait(false);
                break;
            case OutputFormat.Csv:
                foreach (var row in all.Prepend(headers))
                {
                    await output.WriteLineAsync(string.Join(',', row.Select(Csv))).ConfigureAwait(false);
                }

                break;
            default:
                var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length))).ToArray();
                var line = new StringBuilder();
                foreach (var row in all.Prepend(headers))
                {
                    line.Clear();
                    for (var i = 0; i < row.Count; i++)
                    {
                        line.Append(i == row.Count - 1 ? row[i] : row[i].PadRight(widths[i] + 2));
                    }

                    await output.WriteLineAsync(line.ToString().TrimEnd()).ConfigureAwait(false);
                }

                break;
        }

        await output.FlushAsync().ConfigureAwait(false);
    }
}
