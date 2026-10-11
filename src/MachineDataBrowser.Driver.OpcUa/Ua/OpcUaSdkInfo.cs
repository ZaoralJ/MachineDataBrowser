namespace MachineDataBrowser.Core;

/// <summary>OPC UA SDK metadata for the application's About dialog.</summary>
public static class OpcUaSdkInfo
{
    public static Version? Version { get; } =
        typeof(Opc.Ua.Client.Session).Assembly.GetName().Version;
}
