using System.Globalization;
using System.Text;

namespace OpcUaBrowser.Core;

public sealed record RecordingFileRow(DateTimeOffset ReceivedAt, string SourceTimestamp, string Name, string NodeId, string Value, string Status);

/// <summary>
/// Reads the CSV written by <see cref="Recording"/> (export or live file). Call <see cref="ReadNew"/> repeatedly
/// to tail a file that is still being recorded; a partially written last line is kept until it is complete.
/// </summary>
public sealed class RecordingFileReader(string path)
{
    private long _offset;
    private string _partial = string.Empty;
    private bool _headerSkipped;

    public string Path { get; } = path;

    public IReadOnlyList<RecordingFileRow> ReadNew()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _offset)
        {
            _offset = 0;
            _partial = string.Empty;
            _headerSkipped = false;
        }

        stream.Seek(_offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = _partial + reader.ReadToEnd();
        _offset = stream.Length;

        var lines = text.Split('\n');
        _partial = lines[^1];
        var rows = new List<RecordingFileRow>();
        foreach (var raw in lines.AsSpan(0, lines.Length - 1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (!_headerSkipped)
            {
                _headerSkipped = true;
                if (line.StartsWith("ReceivedAt,", StringComparison.Ordinal))
                {
                    continue;
                }
            }

            var f = ParseCsv(line);
            if (f.Count >= 7 && DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                rows.Add(new RecordingFileRow(at, f[1], f[3], f[4], f[5], f[6]));
            }
        }

        return rows;
    }

    internal static List<string> ParseCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
