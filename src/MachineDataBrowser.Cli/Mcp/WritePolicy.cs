using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// What the agent may change, on top of the user's confirmation: with <c>--write-node</c> patterns only matching
/// variables, and with <c>--write-limit</c> only numbers inside a range. A server running unattended next to a machine
/// should not depend on a person reading every confirmation carefully.
/// </summary>
internal sealed class WritePolicy
{
    private readonly List<Regex> _allowed;
    private readonly List<string> _allowedText;
    private readonly List<(Regex Pattern, string Text, double? Min, double? Max)> _limits;

    public WritePolicy(IEnumerable<string>? allowed = null, IEnumerable<string>? limits = null)
    {
        _allowedText = [.. (allowed ?? []).Select(a => a.Trim())];
        _allowed = [.. _allowedText.Select(Glob)];
        _limits = [.. (limits ?? []).Select(ParseLimit)];
    }

    public bool IsRestricted => _allowed.Count > 0 || _limits.Count > 0;

    /// <summary>Describes the rules for the agent's instructions; empty without rules.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (_allowed.Count > 0)
        {
            parts.Add($"only variables matching {string.Join(", ", _allowedText)} may be written");
        }

        parts.AddRange(_limits.Select(l => $"{l.Text} must be {Range(l.Min, l.Max)}"));
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Throws when the value may not be written to the node. <paramref name="names"/> are what the node is known by: its
    /// path from the root when known (patterns of paths match only that) and its id (for patterns of ids).
    /// </summary>
    public void Check(IReadOnlyList<string> names, string value)
    {
        var candidates = names.Where(n => !string.IsNullOrEmpty(n)).Select(n => n.TrimEnd('/')).ToList();
        if (_allowed.Count > 0 && !candidates.Any(n => _allowed.Any(p => p.IsMatch(n))))
        {
            throw new McpException($"{candidates.FirstOrDefault()} is not among the variables this server may write (--write-node); nothing was changed. "
                + "Name variables by their path from the root so path patterns can match.");
        }

        foreach (var limit in _limits.Where(l => candidates.Any(n => l.Pattern.IsMatch(n))))
        {
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            {
                throw new McpException($"{candidates[0]} only takes numbers {Range(limit.Min, limit.Max)} (--write-limit); '{value}' is not a number.");
            }

            if (number < limit.Min || number > limit.Max)
            {
                throw new McpException($"{value} is outside the limit for {candidates[0]}: it must be {Range(limit.Min, limit.Max)} (--write-limit); nothing was changed.");
            }
        }
    }

    /// <summary>Methods have no value to limit; with <c>--write-node</c> rules they must match one like a variable.</summary>
    public void CheckMethod(IReadOnlyList<string> names)
    {
        var candidates = names.Where(n => !string.IsNullOrEmpty(n)).Select(n => n.TrimEnd('/')).ToList();
        if (_allowed.Count > 0 && !candidates.Any(n => _allowed.Any(p => p.IsMatch(n))))
        {
            throw new McpException($"{candidates.FirstOrDefault()} is not among the nodes this server may change (--write-node); nothing was called.");
        }
    }

    private static string Range(double? min, double? max) => (min, max) switch
    {
        ({ } a, { } b) => $"between {a.ToString(CultureInfo.InvariantCulture)} and {b.ToString(CultureInfo.InvariantCulture)}",
        ({ } a, null) => $"at least {a.ToString(CultureInfo.InvariantCulture)}",
        (null, { } b) => $"at most {b.ToString(CultureInfo.InvariantCulture)}",
        _ => "any number",
    };

    /// <summary><c>PATTERN=MIN..MAX</c>, either bound may be left out; the last <c>=</c> separates (ids contain <c>=</c>).</summary>
    private static (Regex, string, double?, double?) ParseLimit(string text)
    {
        var at = text.LastIndexOf('=');
        var range = at > 0 ? text[(at + 1)..].Split("..") : [];
        if (range.Length != 2)
        {
            throw new CliException($"--write-limit '{text}' must look like /Objects/Line1/Setpoint=0..100 (either bound may be left out).");
        }

        double? Bound(string part) => part.Trim().Length == 0 ? null
            : double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
            : throw new CliException($"--write-limit '{text}': '{part}' is not a number.");
        var (min, max) = (Bound(range[0]), Bound(range[1]));
        if (min > max)
        {
            throw new CliException($"--write-limit '{text}': the minimum is above the maximum.");
        }

        var pattern = text[..at].Trim();
        return (Glob(pattern), pattern, min, max);
    }

    /// <summary><c>*</c> any characters, <c>?</c> one; the whole path or id must match, case-insensitively.</summary>
    private static Regex Glob(string pattern) => new(
        "^" + Regex.Escape(pattern.Trim().TrimEnd('/')).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

/// <summary>
/// One JSON line per change the agent asked for (written, declined, refused or failed): what, where, before and after.
/// So there is a record of what an agent did to a machine, independent of the agent's own log.
/// </summary>
internal sealed class AuditLog(string? path)
{
    private readonly Lock _lock = new();

    public string? Path => path;

    public void Add(string tool, string endpoint, string node, string? id, string? value, string outcome, JsonNode? before = null, JsonNode? after = null, string? detail = null)
    {
        if (path is null)
        {
            return;
        }

        var entry = new JsonObject
        {
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["tool"] = tool,
            ["endpoint"] = endpoint,
            ["node"] = node,
            ["id"] = id,
            ["value"] = value,
            ["outcome"] = outcome,
            ["before"] = before,
            ["after"] = after,
            ["detail"] = detail,
        };
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                File.AppendAllText(path, entry.ToJsonString() + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The change itself is what matters; a full disk must not turn a done write into an error.
            Console.Error.WriteLine($"mdbrowser: could not write the audit log {path}: {ex.Message}");
        }
    }
}
