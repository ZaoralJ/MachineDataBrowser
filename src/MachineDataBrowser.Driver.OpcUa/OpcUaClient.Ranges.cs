using Opc.Ua;
using Opc.Ua.Client;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.OpcUa;

/// <summary>Reads OPC UA EURange properties for write preflight.</summary>
public sealed partial class OpcUaClient : IEngineeringRangeSource
{
    Task<IReadOnlyList<EngineeringRange?>> IEngineeringRangeSource.ReadRangesAsync(
        IReadOnlyList<DeviceNodeId> nodeIds,
        CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => ReadRangesAsync(nodeIds.Select(UaNodeIdAdapter.ToUa).ToList(), cancellationToken));

    /// <summary>One Browse and one Read for all variables, regardless of how many expose EURange.</summary>
    public async Task<IReadOnlyList<EngineeringRange?>> ReadRangesAsync(
        IReadOnlyList<NodeId> nodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var ranges = new EngineeringRange?[nodeIds.Count];
        if (nodeIds.Count == 0)
        {
            return ranges;
        }

        var session = RequireSession();
        var (_, _, references, _) = await session.BrowseAsync(
            null, null, [.. nodeIds], 0, BrowseDirection.Forward, ReferenceTypeIds.HasProperty, true,
            (uint)NodeClass.Variable, cancellationToken).ConfigureAwait(false);

        var owners = new List<int>();
        var toRead = new List<ReadValueId>();
        for (var i = 0; i < nodeIds.Count && i < references.Count; i++)
        {
            if (references[i].ToList().FirstOrDefault(r => r.BrowseName.Name == BrowseNames.EURange) is { } property)
            {
                owners.Add(i);
                toRead.Add(new ReadValueId { NodeId = ExpandedNodeId.ToNodeId(property.NodeId, session.NamespaceUris), AttributeId = Attributes.Value });
            }
        }

        if (toRead.Count == 0)
        {
            return ranges;
        }

        var read = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, toRead, cancellationToken).ConfigureAwait(false);
        for (var j = 0; j < owners.Count && j < read.Results.Count; j++)
        {
            var raw = read.Results[j].WrappedValue.AsBoxedObject();
            var value = raw is ExtensionObject extension && extension.TryGetValue(out IEncodeable? body) ? body : raw;
            if (value is Opc.Ua.Range { Low: var low, High: var high } && double.IsFinite(low) && double.IsFinite(high) && low <= high)
            {
                ranges[owners[j]] = new EngineeringRange(low, high);
            }
        }

        return ranges;
    }
}
