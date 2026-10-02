using Avalonia.Headless.XUnit;
using Opc.Ua;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;
using OpcUaBrowser.Core.Tests;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class ReadOnlyModeTests(CustomTypesServerFixture server) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("readonly").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static async Task<NodeViewModel> FindAsync(MainWindowViewModel vm, params string[] path)
    {
        var node = vm.RootNodes[0];
        foreach (var name in path)
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        return node;
    }

    [AvaloniaFact]
    public async Task Read_only_blocks_writes_calls_and_acknowledging_and_is_saved_with_the_session()
    {
        var dialogs = new TestDialogs();
        var session = Path.Combine(_dir, "ro.opcsession");
        await using (var vm = new MainWindowViewModel { EndpointUrl = server.EndpointUrl, Dialogs = dialogs })
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            var int32 = await FindAsync(vm, "Objects", "Custom", "DataTypes", "Int32");
            vm.SelectedNode = int32;
            await vm.AddToWatchCommand.ExecuteAsync(null);
            vm.SelectedWatchItem = vm.WatchItems.Single();
            Assert.True(vm.WriteWatchValueCommand.CanExecute(null));

            vm.ToggleReadOnlyCommand.Execute(null);
            Assert.True(vm.IsReadOnly);
            Assert.EndsWith("read-only", vm.OptionsSummary, StringComparison.Ordinal);
            Assert.False(vm.WriteWatchValueCommand.CanExecute(null));
            Assert.False(vm.WriteAttributeValueCommand.CanExecute(null));

            // The method form still opens (to read the arguments) but cannot call.
            var add = await FindAsync(vm, "Objects", "Custom", "Methods", "Add");
            await vm.CallMethodCommand.ExecuteAsync(add);
            var call = dialogs.MethodCall!;
            Assert.True(call.IsLoaded);
            Assert.False(call.CallCommand.CanExecute(null));

            // Turning it off while the form is open enables it again.
            vm.IsReadOnly = false;
            Assert.True(call.CallCommand.CanExecute(null));
            vm.IsReadOnly = true;
            Assert.False(call.CallCommand.CanExecute(null));

            await vm.WriteSessionAsync(session);
        }

        await using var reopened = new MainWindowViewModel { Dialogs = dialogs };
        await reopened.LoadSessionAsync(session);
        Assert.True(reopened.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task Read_only_events_window_shows_alarms_but_does_not_acknowledge()
    {
        var alarm = new EventNotification(DateTime.UtcNow, 800, "Tank", "high", "TripAlarmType", [1, 2, 3],
            new NodeId(1u, 2), "High", true, false, true);
        var source = new FakeSource(alarm);
        await using var events = new EventsViewModel(source, ObjectIds.Server, "Server", _ => { }) { IsReadOnly = true };
        await events.StartAsync();
        events.Flush();
        events.SelectedAlarm = Assert.Single(events.Alarms);
        Assert.False(events.AcknowledgeCommand.CanExecute(null));
        Assert.Contains("Read-only", events.AcknowledgeHint, StringComparison.Ordinal);
        events.IsReadOnly = false;
        Assert.True(events.AcknowledgeCommand.CanExecute(null));
    }

    private sealed class FakeSource(EventNotification first) : IEventSource
    {
        public Task<IAsyncDisposable> SubscribeEventsAsync(NodeId notifier, Action<EventNotification> onEvent, CancellationToken cancellationToken = default)
        {
            onEvent(first);
            return Task.FromResult<IAsyncDisposable>(new Nothing());
        }

        public Task AcknowledgeAsync(NodeId conditionId, byte[] eventId, string comment, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("must not be called in read-only mode");

        private sealed class Nothing : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
