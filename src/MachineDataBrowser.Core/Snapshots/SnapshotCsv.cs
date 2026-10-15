using System.Text;

namespace MachineDataBrowser.Core;

/// <summary>
/// Snapshots as CSV (Name, NodeId, Value, Status, Path), for documenting a machine's parameters in a spreadsheet and loading
/// edited values back. Reading accepts a comma or semicolon (Excel in many locales) as separator.
/// </summary>
public static class SnapshotCsv
{
    private static readonly string[] Header = ["Name", "NodeId", "Value", "Status", "Path"];

    public static void Write(Snapshot snapshot, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(string.Join(',', Header));
        foreach (var item in snapshot.Items)
        {
            writer.WriteLine(string.Join(',', new[] { item.Name, item.NodeId, item.Value, item.Status, item.Path ?? string.Empty }.Select(Escape)));
        }
    }

    /// <summary>
    /// Reads a CSV written by <see cref="Write"/> (or edited in a spreadsheet). The file has no snapshot name or time,
    /// so <paramref name="name"/> and now are used; it counts as a partial snapshot, like one of selected rows.
    /// A missing Status column means Good: a hand-written file lists values meant to be used.
    /// </summary>
    /// <exception cref="InvalidDataException">No NodeId column, or a row without a NodeId.</exception>
    public static Snapshot Read(TextReader reader, string name)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("The file is empty.");
        var separator = headerLine.Count(c => c == ';') > headerLine.Count(c => c == ',') ? ';' : ',';
        var header = Split(headerLine, separator).Select(h => h.Trim()).ToList();
        int Column(string title) => header.FindIndex(h => string.Equals(h, title, StringComparison.OrdinalIgnoreCase));
        var (nameAt, idAt, valueAt, statusAt, pathAt) = (Column("Name"), Column("NodeId"), Column("Value"), Column("Status"), Column("Path"));
        if (idAt < 0 || valueAt < 0)
        {
            throw new InvalidDataException("A snapshot CSV needs NodeId and Value columns.");
        }

        var items = new List<SnapshotItem>();
        var line = 1;
        while (ReadRecord(reader) is { } record)
        {
            line++;
            if (string.IsNullOrWhiteSpace(record))
            {
                continue;
            }

            var cells = Split(record, separator);
            string Cell(int at) => at >= 0 && at < cells.Count ? cells[at] : string.Empty;
            var id = Cell(idAt).Trim();
            if (id.Length == 0)
            {
                throw new InvalidDataException($"Line {line} has no NodeId.");
            }

            var status = Cell(statusAt);
            var path = Cell(pathAt).Trim();
            items.Add(SnapshotItem.Of(nameAt >= 0 && Cell(nameAt).Length > 0 ? Cell(nameAt) : id, id, Cell(valueAt), status.Length > 0 ? status : "Good")
                with { Path = path.Length > 0 ? path : null });
        }

        return new Snapshot { Name = name, TakenAt = DateTimeOffset.Now, Items = items, Partial = true };
    }

    private static string Escape(string text) =>
        text.IndexOfAny([',', ';', '"', '\n', '\r']) >= 0 || text.Trim().Length != text.Length
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;

    // A record can span lines when a quoted value contains a line break.
    private static string? ReadRecord(TextReader reader)
    {
        var line = reader.ReadLine();
        if (line is null)
        {
            return null;
        }

        var record = new StringBuilder(line);
        while (record.ToString().Count(c => c == '"') % 2 == 1 && reader.ReadLine() is { } next)
        {
            record.Append('\n').Append(next);
        }

        return record.ToString();
    }

    private static List<string> Split(string record, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < record.Length && record[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}
