namespace MachineDataBrowser.Core;

/// <summary>Interprets the access-level attributes returned by clients that can report write permissions.</summary>
public static class DeviceWriteAccess
{
    /// <summary>
    /// Returns whether the node is writable, or null when the device does not report access metadata.
    /// UserAccessLevel takes precedence over AccessLevel because it reflects the current user's permissions.
    /// </summary>
    public static bool? IsWritable(IReadOnlyList<AttributeValue> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var access = attributes.FirstOrDefault(a => a.Name == "UserAccessLevel")?.Value
            ?? attributes.FirstOrDefault(a => a.Name == "AccessLevel")?.Value;
        return access is null
            ? null
            : access.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("Write", StringComparer.OrdinalIgnoreCase);
    }
}
