namespace MachineDataBrowser.Core;

/// <summary>
/// How session files refer to a tag map (<see cref="ConnectOptions.TagMapPath"/>): relative to the session file when the
/// map is in its folder or below, so the two can be copied or shared together; absolute otherwise.
/// </summary>
public static class TagMapReference
{
    public static string ToStored(string mapPath, string sessionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        var map = Path.GetFullPath(mapPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(sessionPath)) ?? string.Empty;
        var relative = Path.GetRelativePath(folder, map);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? map : relative.Replace('\\', '/');
    }

    public static string Resolve(string stored, string sessionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        return Path.IsPathRooted(stored)
            ? stored
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sessionPath)) ?? string.Empty, stored));
    }
}
