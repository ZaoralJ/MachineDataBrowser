using System.Collections.Concurrent;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Opc.Ua;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class EventsTests(OpcPlcFixture plc)
{
    private sealed class FakeEventSource : IEventSource
    {
        public Action<EventNotification>? Sink { get; private set; }

        public bool Unsubscribed { get; private set; }

        public ConcurrentQueue<(NodeId Condition, byte[] EventId, string Comment)> Acks { get; } = new();

        public Task<IAsyncDisposable> SubscribeEventsAsync(NodeId notifier, Action<EventNotification> onEvent, CancellationToken cancellationToken = default)
        {
            Sink = onEvent;
            return Task.FromResult<IAsyncDisposable>(new Handle(this));
        }

        public Task AcknowledgeAsync(NodeId conditionId, byte[] eventId, string comment, CancellationToken cancellationToken = default)
        {
            Acks.Enqueue((conditionId, eventId, comment));
            return Task.CompletedTask;
        }

        private sealed class Handle(FakeEventSource source) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                source.Unsubscribed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private static EventNotification Plain(string message, ushort severity) =>
        new(DateTime.UtcNow, severity, "System", message, "SystemEventType", [1, 2, 3]);

    private static EventNotification Alarm(int id, ushort severity, bool active, bool acked, bool retain = true, byte eventId = 7) =>
        new(DateTime.UtcNow, severity, "Tank", $"alarm {id}", "TripAlarmType", [eventId, 0, 0],
            new NodeId((uint)id, 2), $"Alarm{id}", active, acked, retain);

    [AvaloniaFact]
    public async Task Events_are_listed_newest_first_alarms_track_state_and_can_be_acknowledged()
    {
        var source = new FakeEventSource();
        await using var vm = new EventsViewModel(source, ObjectIds.Server, "Server", _ => { });
        await vm.StartAsync();
        Assert.True(vm.IsSubscribed);

        source.Sink!(Plain("first", 100));
        source.Sink!(Plain("second", 800));
        source.Sink!(Alarm(1, 500, active: true, acked: false));
        source.Sink!(Alarm(2, 900, active: true, acked: false));
        vm.Flush();

        Assert.Equal(["alarm 2", "alarm 1", "second", "first"], vm.Events.Select(e => e.Message));
        Assert.Equal(["Alarm2", "Alarm1"], vm.Alarms.Select(a => a.Name)); // most severe first
        Assert.All(vm.Alarms, a => Assert.Equal("Active, Unacked", a.StateText));

        vm.MinSeverity = 700;
        Assert.Equal(["alarm 2", "second"], vm.Events.Select(e => e.Message));
        vm.MinSeverity = 0;

        vm.IsPaused = true;
        source.Sink!(Plain("while paused", 100));
        vm.Flush();
        Assert.Equal(4, vm.Events.Count);
        vm.IsPaused = false;
        Assert.Equal("while paused", vm.Events[0].Message);

        // Acknowledge uses the latest notification's EventId and the comment.
        source.Sink!(Alarm(1, 500, active: true, acked: false, eventId: 9));
        vm.Flush();
        vm.SelectedAlarm = vm.Alarms.Single(a => a.Name == "Alarm1");
        vm.AckComment = "checked";
        Assert.True(vm.AcknowledgeCommand.CanExecute(null));
        await vm.AcknowledgeCommand.ExecuteAsync(null);
        var ack = Assert.Single(source.Acks);
        Assert.Equal(new NodeId(1u, 2), ack.Condition);
        Assert.Equal(9, ack.EventId[0]);
        Assert.Equal("checked", ack.Comment);
        Assert.Empty(vm.AckComment);

        // Acked and inactive with Retain=false: gone from the alarm list.
        source.Sink!(Alarm(1, 500, active: false, acked: true, retain: false));
        vm.Flush();
        Assert.Equal(["Alarm2"], vm.Alarms.Select(a => a.Name));

        // An all-zero EventId (opc-plc) can still be tried; the hint warns that the server will likely refuse.
        source.Sink!(Alarm(3, 300, active: true, acked: false, eventId: 0));
        vm.Flush();
        vm.SelectedAlarm = vm.Alarms.Single(a => a.Name == "Alarm3");
        Assert.True(vm.AcknowledgeCommand.CanExecute(null));
        Assert.Contains("no event id", vm.AcknowledgeHint, StringComparison.Ordinal);

        await vm.DisposeAsync();
        Assert.True(source.Unsubscribed);
    }

    [AvaloniaFact]
    public async Task Events_window_shows_live_events_and_alarms_of_the_server()
    {
        var dialogs = new TestDialogs();
        await using var main = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800 };
        window.Show();
        main.Dialogs = dialogs;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.True(main.SupportsEvents);

        await main.ShowEventsCommand.ExecuteAsync(null);
        var events = Assert.Single(dialogs.EventWindows);
        Assert.Equal("Server", events.NotifierName);
        await Until(() =>
        {
            events.Flush();
            return events.Events.Count > 0 && events.Alarms.Count > 0;
        });

        var eventsWindow = new EventsWindow { DataContext = events, Width = 1000, Height = 600 };
        eventsWindow.Show(window);
        Dispatcher.UIThread.RunJobs();
        eventsWindow.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = eventsWindow.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "events-alarms.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        await main.DisconnectCommand.ExecuteAsync(null);
        Assert.False(events.IsSubscribed);
        eventsWindow.Close();
        window.Close();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
