using MachineDataBrowser.Core.Ua;

namespace MachineDataBrowser.Core;

public static class ClientPaths
{
    public const string DataDirVariable = "MACHINEDATABROWSER_DATA_DIR";

    /// <summary>Overrides the data folder under its name from before the rename; still honoured.</summary>
    public const string LegacyDataDirVariable = "OPCUABROWSER_DATA_DIR";

    /// <summary>The data folder's name before the app was renamed from OPC UA Browser.</summary>
    public const string LegacyFolderName = "OpcUaBrowser";

    private static readonly Lazy<string> Root = new(ResolveDataRoot);

    public static string DataRoot => Root.Value;

    public static string PkiRoot => ClientConfiguration.PkiRoot;

    private static string ResolveDataRoot()
    {
        foreach (var variable in new[] { DataDirVariable, LegacyDataDirVariable })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } overridden)
            {
                return overridden;
            }
        }

        var parent = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(parent, "MachineDataBrowser");
        MigrateLegacyFolder(Path.Combine(parent, LegacyFolderName), root);
        return root;
    }

    /// <summary>
    /// First start after the rename: copies the old data folder (settings, certificates, layout, snapshots, logs) to the
    /// new one. The old folder is left in place, so an older version still finds its data. Best effort: a failure
    /// starts with defaults rather than not at all.
    /// </summary>
    public static bool MigrateLegacyFolder(string legacy, string root)
    {
        if (Directory.Exists(root) || !Directory.Exists(legacy))
        {
            return false;
        }

        var staging = root + ".migrating";
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            CopyDirectory(legacy, staging);

            // Copy into a staging folder and rename at the end: an interrupted copy is retried, never half-used.
            Directory.Move(staging, root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Data folder migration from {legacy} failed: {ex.Message}");
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
