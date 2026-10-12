namespace MachineDataBrowser.Core;

/// <summary>A browsed subtree with the current value of each variable.</summary>
public sealed class NodeTree(DeviceNodeId nodeId, string displayName, DeviceNodeKind nodeClass)
{
    public DeviceNodeId NodeId { get; } = nodeId;

    public string DisplayName { get; } = displayName;

    public DeviceNodeKind NodeClass { get; } = nodeClass;

    public object? Value { get; set; }

    public List<NodeTree> Children { get; } = [];
}
