namespace MachineDataBrowser.Core;

/// <summary>
/// An array whose element type is part of the device contract. Keeps byte-valued arrays (e.g. Logix USINT/BYTE) distinct
/// from byte strings (e.g. OPC UA ByteString), which are both represented by <see cref="byte[]"/> by the CLR.
/// </summary>
public sealed record DeviceArray(string ElementType, Array Values)
{
    public int Length => Values.Length;
}
