using Opc.Ua;
using OpcUaBrowser.Core;
using CommunityToolkit.Mvvm.Input;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly List<EventsViewModel> _eventViewers = [];

    /// <summary>The connection delivers events and alarms (OPC UA).</summary>
    public bool SupportsEvents => _client is IEventSource;

    private bool CanShowEvents() => IsConnected && SupportsEvents;

    /// <summary>
    /// Opens Events &amp; Alarms for the selected object (its area or source), or for the whole server when nothing
    /// that can notify events is selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowEvents))]
    private async Task ShowEventsAsync(NodeViewModel? node)
    {
        if (_client is not IEventSource source || Dialogs is null)
        {
            return;
        }

        var target = node ?? (SelectedNode is { IsVariable: false, IsPlaceholder: false } selected && selected.Depth > 0 ? selected : null);
        var (notifier, name) = target is null || target.NodeId == ObjectIds.RootFolder || target.NodeId == ObjectIds.ObjectsFolder
            ? (ObjectIds.Server, "Server")
            : (target.NodeId, target.DisplayName);

        var events = new EventsViewModel(source, notifier, name, ReportError);
        _eventViewers.Add(events);
        Dialogs.ShowEvents(events);
        await events.StartAsync();
    }

    /// <summary>Event windows end with the connection (their subscriptions belong to its session).</summary>
    private async Task CloseEventViewersAsync()
    {
        foreach (var viewer in _eventViewers.ToList())
        {
            await viewer.DisposeAsync();
        }

        _eventViewers.Clear();
    }
}
