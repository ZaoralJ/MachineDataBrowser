using Opc.Ua;

namespace MachineDataBrowser.Core;

/// <summary>A status code as people read it ("Good", "BadNotWritable").</summary>
public static class StatusText
{
    /// <summary>
    /// Codes built from a number (MQTT, Logix, exceptions created from a raw code) carry no name, so it is looked up;
    /// the hex code is the last resort.
    /// </summary>
    public static string Of(StatusCode status) =>
        status.SymbolicId ?? (StatusCodes.GetBrowseName(status.Code) is { Length: > 0 } name ? name : status.ToString());
}
