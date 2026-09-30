using OpcUaBrowser.Core.Ua;

namespace OpcUaBrowser.Core;

public static class ClientPaths
{
    public const string DataDirVariable = "OPCUABROWSER_DATA_DIR";

    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpcUaBrowser");

    public static string PkiRoot => ClientConfiguration.PkiRoot;
}
