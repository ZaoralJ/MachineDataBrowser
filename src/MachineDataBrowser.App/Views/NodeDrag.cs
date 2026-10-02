using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

/// <summary>In-process payload of an address-space drag (the DataTransfer only carries NodeId text for external targets).</summary>
internal static class NodeDrag
{
    public static IReadOnlyList<NodeViewModel>? Current { get; set; }

    internal static int StartedCount { get; set; }
}
