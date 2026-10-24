using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

/// <summary>
/// "Copy mdbrowser command": the selection as a command line for the CLI, on the clipboard. Nodes are written as paths
/// from the root (/Objects/Line1/Speed), except MQTT where portable topic ids preserve JSON pointers and can subscribe
/// to a topic that is no longer in the app's live message cache.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private CliCommand.Connection CliConnection() => new(EndpointUrl, UserName, UseSecurity, AutoAcceptCertificates, TagMapForConnect);

    private bool CanCopyNodeCommand() => IsConnected && HasSelectedNode();

    private bool CanCopyWatchCommand() => IsConnected && WatchItems.Count > 0;

    /// <summary>The selected node, or the whole address space when only the root is selected.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyNodeCommand))]
    private Task CopyCliBrowseAsync()
    {
        var node = SelectionOrCurrent()[0];
        return CopyCliCommandAsync(CliCommand.Browse(CliConnection(), node.Parent is null ? null : CliNode(node)));
    }

    /// <summary>Selected variables as they are; selected folders and structures with -R (every variable below them).</summary>
    [RelayCommand(CanExecute = nameof(CanCopyNodeCommand))]
    private Task CopyCliReadAsync()
    {
        var (ids, recursive) = SelectedNodesForCli();
        return CopyCliCommandAsync(CliCommand.Read(CliConnection(), ids, recursive));
    }

    [RelayCommand(CanExecute = nameof(CanCopyNodeCommand))]
    private Task CopyCliMonitorAsync()
    {
        var (ids, recursive) = SelectedNodesForCli();
        return CopyCliCommandAsync(CliCommand.Monitor(CliConnection(), ids, recursive, refreshMs: null));
    }

    /// <summary>The selected rows (all rows when none is selected), with their refresh time when they share one.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyWatchCommand))]
    private Task CopyWatchCliMonitorAsync()
    {
        var items = WatchItemsForCli();
        var refresh = items.Select(w => w.RefreshMs).Distinct().ToList();
        var refreshMs = refresh.Count == 1 && refresh[0] != CliCommand.DefaultRefreshMs(EndpointUrl) ? refresh[0] : (int?)null;
        return CopyCliCommandAsync(CliCommand.Monitor(CliConnection(), [.. items.Select(CliNode)], recursive: false, refreshMs));
    }

    [RelayCommand(CanExecute = nameof(CanCopyWatchCommand))]
    private Task CopyWatchCliReadAsync() =>
        CopyCliCommandAsync(CliCommand.Read(CliConnection(), [.. WatchItemsForCli().Select(CliNode)], recursive: false));

    private (List<string> Ids, bool Recursive) SelectedNodesForCli()
    {
        var nodes = SelectionOrCurrent().Where(n => !n.IsPlaceholder).ToList();
        return ([.. nodes.Select(CliNode)], nodes.Any(n => !n.IsVariable));
    }

    private string CliNode(NodeViewModel node) => DeviceClient.IsMqtt(EndpointUrl) ? node.NodeIdText : $"/{node.Path}";

    private string CliNode(WatchItemViewModel item) => DeviceClient.IsMqtt(EndpointUrl) || item.Path.Length == 0
        ? item.PortableId
        : $"/{item.Path}/{item.DisplayName}";

    private List<WatchItemViewModel> WatchItemsForCli()
    {
        var selected = WatchSelectionOrCurrent();
        return selected.Count > 0 ? selected : [.. WatchItems];
    }

    private async Task CopyCliCommandAsync(string command)
    {
        await CopyAsync(command);
        StatusMessage = $"Copied: {command}";
    }
}
