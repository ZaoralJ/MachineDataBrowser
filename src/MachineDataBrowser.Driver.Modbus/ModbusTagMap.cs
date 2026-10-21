using System.Text;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.Modbus;

/// <summary>A named value from a register map.</summary>
public sealed record ModbusTag(string Path, ModbusAddress Address, string? Unit, string? Description)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// A register map: names for a device's addresses, read from CSV (comma or semicolon separated, optional header,
/// <c>#</c> comments):
/// <code>
/// name,address,unit,description
/// Line1/Motor/Speed,hr:20:float32,rpm,Motor speed
/// Line1/Running,co:1
/// </code>
/// <c>/</c> in a name makes folders. Unit and description are optional.
/// </summary>
public sealed class ModbusTagMap
{
    private ModbusTagMap(IReadOnlyList<ModbusTag> tags)
    {
        Tags = tags;
        ByAddress = tags.GroupBy(t => t.Address.ToString(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyList<ModbusTag> Tags { get; }

    /// <summary>The first tag of each address (canonical <see cref="ModbusAddress.ToString"/>).</summary>
    public IReadOnlyDictionary<string, ModbusTag> ByAddress { get; }

    public static async Task<ModbusTagMap> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadConfigurationError, $"Can't read the register map '{path}': {ex.Message}", ex);
        }

        try
        {
            return Parse(text);
        }
        catch (FormatException ex)
        {
            throw new DeviceClientException(DeviceStatusCodes.BadConfigurationError, $"Register map '{Path.GetFileName(path)}': {ex.Message}", ex);
        }
    }

    public static ModbusTagMap Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tags = new List<ModbusTag>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        char? delimiter = null;
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            delimiter ??= line.Contains(';', StringComparison.Ordinal) && !line.Contains(',', StringComparison.Ordinal) ? ';' : ',';
            var fields = SplitCsv(line, delimiter.Value);
            if (tags.Count == 0 && fields[0].Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fields.Count < 2)
            {
                throw new FormatException($"line {lineNumber}: expected name{delimiter}address.");
            }

            var name = string.Join('/', fields[0].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (name.Length == 0)
            {
                throw new FormatException($"line {lineNumber}: the name is empty.");
            }

            if (!ModbusAddress.TryParse(fields[1], out var address))
            {
                throw new FormatException($"line {lineNumber}: '{fields[1]}' is not a Modbus address (e.g. hr:100, hr:20:float32, co:3).");
            }

            if (!paths.Add(name))
            {
                throw new FormatException($"line {lineNumber}: '{name}' is named twice.");
            }

            tags.Add(new ModbusTag(name, address, Optional(fields, 2), Optional(fields, 3)));
        }

        var folders = tags.SelectMany(t => Enumerable.Range(1, t.Path.Count(c => c == '/')).Select(n => string.Join('/', t.Path.Split('/').Take(n)))).ToHashSet(StringComparer.Ordinal);
        var clash = tags.FirstOrDefault(t => folders.Contains(t.Path));
        if (clash is not null)
        {
            throw new FormatException($"'{clash.Path}' is both a value and a folder.");
        }

        return new ModbusTagMap(tags);
    }

    private static string? Optional(List<string> fields, int index) =>
        fields.Count > index && fields[index].Length > 0 ? fields[index] : null;

    private static List<string> SplitCsv(string line, char delimiter)
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
            else if (c == delimiter)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }
}
