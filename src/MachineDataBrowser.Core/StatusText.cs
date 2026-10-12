namespace MachineDataBrowser.Core;

/// <summary>A status code as people read it ("Good", "BadNotWritable").</summary>
public static class StatusText
{
    /// <summary>
    /// The neutral code's symbolic name is the preferred display, with its stored code as a fallback.
    /// </summary>
    public static string Of(DeviceStatusCode status) =>
        string.IsNullOrEmpty(status.Name) ? status.ToString() : status.Name;
}
