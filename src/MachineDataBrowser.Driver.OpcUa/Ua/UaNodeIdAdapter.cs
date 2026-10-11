using Opc.Ua;

namespace MachineDataBrowser.Core.Ua;

internal static class UaNodeIdAdapter
{
    public static DeviceNodeId ToDevice(NodeId nodeId) => DeviceNodeId.FromOpaque(nodeId.ToString());

    public static NodeId ToUa(DeviceNodeId nodeId) => nodeId.IsOpaque || nodeId.IsNumeric
        ? NodeId.Parse(nodeId.IsOpaque ? nodeId.Identifier : nodeId.ToString())
        : new NodeId(nodeId.Identifier, nodeId.NamespaceIndex);

    public static DeviceNodeKind ToDevice(NodeClass nodeClass) => nodeClass switch
    {
        NodeClass.Object => DeviceNodeKind.Object,
        NodeClass.Variable => DeviceNodeKind.Variable,
        NodeClass.Method => DeviceNodeKind.Method,
        NodeClass.ObjectType => DeviceNodeKind.ObjectType,
        NodeClass.VariableType => DeviceNodeKind.VariableType,
        NodeClass.DataType => DeviceNodeKind.DataType,
        NodeClass.ReferenceType => DeviceNodeKind.ReferenceType,
        NodeClass.View => DeviceNodeKind.View,
        _ => DeviceNodeKind.Unspecified,
    };
}
