using Opc.Ua;
using Opc.Ua.Client;

namespace MachineDataBrowser.Core.Ua;

public sealed partial class OpcUaClient : IEngineeringUnitsSource
{
    /// <summary>One Browse (HasProperty) and one Read for all variables, however many.</summary>
    public async Task<IReadOnlyList<string?>> ReadUnitsAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var units = new string?[nodeIds.Count];
        if (nodeIds.Count == 0)
        {
            return units;
        }

        var session = RequireSession();
        var (_, _, references, _) = await session.BrowseAsync(
            null, null, [.. nodeIds], 0, BrowseDirection.Forward, ReferenceTypeIds.HasProperty, true,
            (uint)NodeClass.Variable, cancellationToken).ConfigureAwait(false);

        var owners = new List<int>();
        var toRead = new ReadValueIdCollection();
        for (var i = 0; i < nodeIds.Count && i < references.Count; i++)
        {
            if (references[i].FirstOrDefault(r => r.BrowseName?.Name == BrowseNames.EngineeringUnits) is { } property)
            {
                owners.Add(i);
                toRead.Add(new ReadValueId { NodeId = ExpandedNodeId.ToNodeId(property.NodeId, session.NamespaceUris), AttributeId = Attributes.Value });
            }
        }

        if (toRead.Count == 0)
        {
            return units;
        }

        var read = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, toRead, cancellationToken).ConfigureAwait(false);
        for (var j = 0; j < owners.Count && j < read.Results.Count; j++)
        {
            var value = read.Results[j].Value is ExtensionObject e ? e.Body : read.Results[j].Value;
            if (value is EUInformation { DisplayName.Text: { Length: > 0 } text })
            {
                units[owners[j]] = text;
            }
        }

        return units;
    }
}
