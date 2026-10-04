namespace MachineDataBrowser.Cli.Tui;

/// <summary>Shell-like Tab completion of a file path, for the TUI's file prompts.</summary>
internal static class PathCompletion
{
    /// <summary>A leading <c>~</c> is the home folder, as in a shell.</summary>
    public static string Expand(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;

    /// <summary>
    /// Completes the last part of <paramref name="text"/> against what is on disk: one match is completed (a folder
    /// with a trailing '/'), several to their common start. Returns the new text and the names that match.
    /// </summary>
    public static (string Text, IReadOnlyList<string> Matches) Complete(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var typedDirectory = text.Contains('/', StringComparison.Ordinal) ? text[..(text.LastIndexOf('/') + 1)] : string.Empty;
        var prefix = text[typedDirectory.Length..];
        var directory = Expand(typedDirectory.Length == 0 ? "." : typedDirectory);
        if (!Directory.Exists(directory))
        {
            return (text, []);
        }

        List<(string Name, bool IsDirectory)> matches;
        try
        {
            matches = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Where(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (prefix.StartsWith('.') || !e.Name.StartsWith('.')))
                .Select(e => (e.Name, e is DirectoryInfo))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return (text, []);
        }

        var names = matches.Select(m => m.IsDirectory ? m.Name + "/" : m.Name).ToList();
        return matches.Count switch
        {
            0 => (text, names),
            1 => (typedDirectory + names[0], names),
            _ => (typedDirectory + CommonStart(matches.Select(m => m.Name).ToList(), prefix), names),
        };
    }

    /// <summary>The longest start all names share, keeping what was typed when the case differs.</summary>
    private static string CommonStart(List<string> names, string typed)
    {
        var first = names[0];
        var length = first.Length;
        foreach (var name in names.Skip(1))
        {
            length = Math.Min(length, name.Length);
            for (var i = 0; i < length; i++)
            {
                if (char.ToLowerInvariant(name[i]) != char.ToLowerInvariant(first[i]))
                {
                    length = i;
                    break;
                }
            }
        }

        return length <= typed.Length ? typed : first[..length];
    }
}
