using System.Text;

namespace MachineDataBrowser.Driver.S7;

/// <summary>A named S7 address from a symbol table; <see cref="Folders"/> place it in the browse tree.</summary>
internal sealed record S7Symbol(string Name, IReadOnlyList<string> Folders, string DisplayName, S7Address Address, string? Comment);

/// <summary>
/// Symbol tables as exported by TIA Portal (PLC tags saved as CSV: Name, Path, Data Type, Logical Address, Comment)
/// or STEP 7 (.sdf: "Name","Address","Type","Comment" without header), or any CSV with such columns. Separators
/// <c>,</c> <c>;</c> and tab are detected. Dots in names become folders (<c>Machine.Speed</c>), as does the TIA tag
/// table (Path column).
/// </summary>
internal sealed record S7TagList(IReadOnlyList<S7Symbol> Symbols, IReadOnlyList<string> Skipped, string? Source)
{
    public static S7TagList Empty { get; } = new([], [], null);

    private static readonly char[] Separators = [',', ';', '\t'];
    private static readonly string[] NameColumns = ["name", "symbol", "symbol name", "tag", "symbolname"];
    private static readonly string[] AddressColumns = ["address", "logical address", "adresse", "logische adresse", "operand"];
    private static readonly string[] TypeColumns = ["data type", "datatype", "type", "datentyp"];
    private static readonly string[] CommentColumns = ["comment", "kommentar", "description"];
    private static readonly string[] PathColumns = ["path", "tag table", "pfad", "variablentabelle"];

    public static async Task<S7TagList> LoadAsync(string path, CancellationToken cancellationToken)
    {
        // STEP 7 exports are Windows-1252; Latin-1 decodes them close enough when there is no UTF-8 BOM.
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var text = IsUtf8(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        return Parse(text) with { Source = path };
    }

    public static S7TagList Parse(string text)
    {
        var lines = text.TrimStart('\uFEFF').Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
        {
            return Empty;
        }

        var separator = Separators.MaxBy(c => Split(lines[0], c).Count);
        var rows = lines.Select(l => Split(l, separator)).ToList();
        int name = 0, address = 1, type = 2, comment = 3, path = -1;
        var header = rows[0].Select(c => c.Trim().ToLowerInvariant()).ToList();
        if (Column(header, NameColumns) is >= 0 and var n && Column(header, AddressColumns) is >= 0 and var a)
        {
            (name, address, type, comment, path) = (n, a, Column(header, TypeColumns), Column(header, CommentColumns), Column(header, PathColumns));
            rows.RemoveAt(0);
        }

        var symbols = new List<S7Symbol>();
        var skipped = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var symbolName = Cell(row, name).Trim('"', ' ');
            var location = Cell(row, address);
            if (symbolName.Length == 0)
            {
                continue;
            }

            if (location.Length == 0)
            {
                skipped.Add($"{symbolName}: no absolute address (optimized block access)");
                continue;
            }

            S7Type? s7Type = null;
            var typeText = Cell(row, type);
            if (typeText.Length > 0)
            {
                if (!S7Type.TryParse(typeText, out var parsed))
                {
                    skipped.Add($"{symbolName}: data type '{typeText}' is not supported");
                    continue;
                }

                s7Type = parsed;
            }

            if (!S7Address.TryParse(location, s7Type, out var s7Address, out var error))
            {
                skipped.Add($"{symbolName}: {error}");
                continue;
            }

            if (!names.Add(symbolName))
            {
                skipped.Add($"{symbolName}: defined more than once");
                continue;
            }

            var segments = symbolName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim('"'))
                .ToList();
            var folders = new List<string>();
            if (Cell(row, path) is { Length: > 0 } table)
            {
                folders.AddRange(table.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            folders.AddRange(segments.Take(segments.Count - 1));
            var commentText = Cell(row, comment);
            symbols.Add(new S7Symbol(symbolName, folders, segments.Count > 0 ? segments[^1] : symbolName, s7Address, commentText.Length == 0 ? null : commentText));
        }

        return new S7TagList(symbols, skipped, null);
    }

    private static int Column(List<string> header, string[] names) => header.FindIndex(names.Contains);

    private static string Cell(List<string> row, int index) => index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;

    private static List<string> Split(string line, char separator)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == separator && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
