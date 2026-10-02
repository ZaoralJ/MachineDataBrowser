using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

/// <summary>One received event, as listed in the Events tab.</summary>
public sealed record EventRow(EventNotification Event)
{
    public string TimeText => Timestamps.Format(Event.Time);

    public string TimeToolTip => Timestamps.ToolTip(new DateTimeOffset(DateTime.SpecifyKind(Event.Time, DateTimeKind.Utc)));

    public ushort Severity => Event.Severity;

    public string SeverityText => EventSeverity.Describe(Event.Severity);

    public bool IsHigh => Event.Severity >= EventSeverity.High;

    public bool IsMedium => Event.Severity is >= EventSeverity.Medium and < EventSeverity.High;

    public string Source => Event.SourceName;

    public string Type => Event.EventType;

    public string Message => Event.Message;

    /// <summary>Alarm name and state for conditions (e.g. "Gold · Active, Unacked"); empty for plain events.</summary>
    public string Condition => Event.IsCondition ? $"{Event.ConditionName} · {EventSeverity.State(Event)}" : string.Empty;
}

/// <summary>An alarm (condition) and its latest state, as listed in the Alarms tab.</summary>
public sealed partial class AlarmRow(NodeId conditionId) : ObservableObject
{
    public NodeId ConditionId { get; } = conditionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Source), nameof(Type), nameof(Message), nameof(Severity), nameof(SeverityText),
        nameof(IsHigh), nameof(IsMedium), nameof(TimeText), nameof(StateText), nameof(IsActive), nameof(IsUnacked), nameof(CanAcknowledge))]
    public partial EventNotification Latest { get; set; } = null!;

    public string Name => Latest.ConditionName ?? string.Empty;

    public string Source => Latest.SourceName;

    public string Type => Latest.EventType;

    public string Message => Latest.Message;

    public ushort Severity => Latest.Severity;

    public string SeverityText => EventSeverity.Describe(Latest.Severity);

    public bool IsHigh => Latest.Severity >= EventSeverity.High;

    public bool IsMedium => Latest.Severity is >= EventSeverity.Medium and < EventSeverity.High;

    public string TimeText => Timestamps.Format(Latest.Time);

    public string StateText => EventSeverity.State(Latest);

    public bool IsActive => Latest.IsActive == true;

    public bool IsUnacked => Latest.IsAcked == false;

    /// <summary>Any unacknowledged alarm can be tried; the server decides (see <see cref="HasEventId"/>).</summary>
    public bool CanAcknowledge => IsUnacked && Latest.EventId is not null;

    /// <summary>
    /// The server matches an acknowledgement to the alarm by the EventId of its latest notification. Some servers
    /// (opc-plc among them) send all-zero ids, and those acknowledgements are rejected.
    /// </summary>
    public bool HasEventId => Latest.EventId is { Length: > 0 } id && id.Any(b => b != 0);
}

public static class EventSeverity
{
    public const ushort High = 700;
    public const ushort Medium = 400;

    /// <summary>OPC UA severity is 1–1000; the bands follow the usual high / medium / low split.</summary>
    public static string Describe(ushort severity) => severity switch
    {
        >= High => $"High ({severity})",
        >= Medium => $"Medium ({severity})",
        _ => $"Low ({severity})",
    };

    public static string State(EventNotification e)
    {
        var parts = new List<string>(2);
        if (e.IsActive is { } active)
        {
            parts.Add(active ? "Active" : "Inactive");
        }

        if (e.IsAcked is { } acked)
        {
            parts.Add(acked ? "Acked" : "Unacked");
        }

        return parts.Count == 0 ? (e.Retain ? "Retained" : string.Empty) : string.Join(", ", parts);
    }
}

/// <summary>
/// Live events and alarms of one event notifier (the Server object for everything). Notifications arrive on SDK
/// threads and are applied to the lists in batches on the UI thread.
/// </summary>
public sealed partial class EventsViewModel : ObservableObject, IAsyncDisposable
{
    public const int MaxEvents = 5000;

    private readonly IEventSource _source;
    private readonly NodeId _notifier;
    private readonly Action<Exception> _reportError;
    private readonly ConcurrentQueue<EventNotification> _incoming = new();
    private readonly List<EventRow> _all = [];
    private readonly Dictionary<NodeId, AlarmRow> _alarms = [];
    private readonly DispatcherTimer _timer;
    private IAsyncDisposable? _subscription;
    private bool _disposed;

    public EventsViewModel(IEventSource source, NodeId notifier, string notifierName, Action<Exception> reportError)
    {
        _source = source;
        _notifier = notifier;
        _reportError = reportError;
        NotifierName = notifierName;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Flush());
    }

    public string NotifierName { get; }

    public string Title => $"Events & Alarms — {NotifierName}";

    /// <summary>Received events, newest first, filtered by <see cref="MinSeverity"/>.</summary>
    public BulkObservableCollection<EventRow> Events { get; } = [];

    /// <summary>Alarms the server reports as retained (active or not yet acknowledged), most severe first.</summary>
    public ObservableCollection<AlarmRow> Alarms { get; } = [];

    [ObservableProperty]
    public partial EventRow? SelectedEvent { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeCommand))]
    [NotifyPropertyChangedFor(nameof(AcknowledgeHint))]
    public partial AlarmRow? SelectedAlarm { get; set; }

    /// <summary>Why the selected alarm can't be acknowledged; empty when it can (or nothing is selected).</summary>
    public string AcknowledgeHint => SelectedAlarm switch
    {
        _ when IsReadOnly => "Read-only session: acknowledging is turned off",
        null => "Select an alarm to acknowledge",
        { IsUnacked: false } => "Already acknowledged",
        { HasEventId: false } => "This server sent no event id for the alarm: it will likely refuse",
        _ => string.Empty,
    };

    [ObservableProperty]
    public partial string AckComment { get; set; } = string.Empty;

    /// <summary>While paused, events are kept but the list doesn't move; alarms keep updating.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>Only events at or above this severity are listed (0 = all).</summary>
    [ObservableProperty]
    public partial int MinSeverity { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Subscribing…";

    [ObservableProperty]
    public partial bool IsSubscribed { get; private set; }

    public int TotalEvents { get; private set; }

    public static IReadOnlyList<int> SeverityChoices { get; } = [0, 200, EventSeverity.Medium, EventSeverity.High, 900];

    partial void OnMinSeverityChanged(int value) => RebuildEvents();

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            RebuildEvents();
        }

        UpdateStatus();
    }

    public async Task StartAsync()
    {
        try
        {
            var source = _source;
            var notifier = _notifier;
            var subscription = await Task.Run(() => source.SubscribeEventsAsync(notifier, _incoming.Enqueue));
            if (_disposed)
            {
                await subscription.DisposeAsync();
                return;
            }

            _subscription = subscription;
            IsSubscribed = true;
            _timer.Start();
            UpdateStatus();
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            StatusText = $"Could not subscribe to events of {NotifierName}: {AppErrors.Describe(ex)}";
            _reportError(ex);
        }
    }

    /// <summary>Applies queued notifications; public so tests can flush without waiting for the timer.</summary>
    public void Flush()
    {
        if (_incoming.IsEmpty)
        {
            return;
        }

        var fresh = new List<EventRow>();
        while (_incoming.TryDequeue(out var notification))
        {
            var row = new EventRow(notification);
            fresh.Add(row);
            if (notification.IsCondition)
            {
                ApplyAlarm(notification);
            }
        }

        TotalEvents += fresh.Count;
        _all.AddRange(fresh);
        if (_all.Count > MaxEvents)
        {
            _all.RemoveRange(0, _all.Count - MaxEvents);
        }

        if (!IsPaused)
        {
            var shown = fresh.Where(r => r.Severity >= MinSeverity).Reverse().ToList();
            if (shown.Count > 200)
            {
                RebuildEvents();
            }
            else
            {
                Events.InsertRange(0, shown);
                if (Events.Count > MaxEvents)
                {
                    Events.RemoveRangeAt(MaxEvents, Events.Count - MaxEvents);
                }
            }
        }

        UpdateStatus();
    }

    private void ApplyAlarm(EventNotification notification)
    {
        var id = notification.ConditionId!;
        if (!notification.Retain)
        {
            // Not retained: inactive and acknowledged, nothing left to act on.
            if (_alarms.Remove(id, out var gone))
            {
                Alarms.Remove(gone);
            }

            return;
        }

        if (!_alarms.TryGetValue(id, out var alarm))
        {
            alarm = new AlarmRow(id) { Latest = notification };
            _alarms[id] = alarm;
            Alarms.Insert(InsertIndex(alarm), alarm);
        }
        else
        {
            // Severity or time changed: re-sort. Removing the row drops the grid selection, so restore it.
            var wasSelected = ReferenceEquals(SelectedAlarm, alarm);
            alarm.Latest = notification;
            Alarms.Remove(alarm);
            Alarms.Insert(InsertIndex(alarm), alarm);
            if (wasSelected)
            {
                SelectedAlarm = alarm;
            }
        }

        AcknowledgeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(AcknowledgeHint));
    }

    private int InsertIndex(AlarmRow alarm)
    {
        var i = 0;
        while (i < Alarms.Count && (Alarms[i].Severity > alarm.Severity || (Alarms[i].Severity == alarm.Severity && Alarms[i].Latest.Time >= alarm.Latest.Time)))
        {
            i++;
        }

        return i;
    }

    private void RebuildEvents()
    {
        if (IsPaused)
        {
            return;
        }

        Events.ReplaceAll(Enumerable.Reverse(_all).Where(r => r.Severity >= MinSeverity));
    }

    private void UpdateStatus()
    {
        if (!IsSubscribed)
        {
            return;
        }

        var unacked = Alarms.Count(a => a.IsUnacked);
        StatusText = $"{(IsPaused ? "Paused" : "Live")} · {TotalEvents:N0} event(s) received · {Alarms.Count} alarm(s), {unacked} unacknowledged";
    }

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Events.ReplaceAll([]);
        TotalEvents = 0;
        UpdateStatus();
    }

    private bool CanAcknowledge() => SelectedAlarm is { CanAcknowledge: true } && IsSubscribed && !IsReadOnly;

    /// <summary>The session is read-only: alarms are shown but not acknowledged.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeCommand))]
    [NotifyPropertyChangedFor(nameof(AcknowledgeHint))]
    public partial bool IsReadOnly { get; set; }

    [RelayCommand(CanExecute = nameof(CanAcknowledge))]
    private async Task AcknowledgeAsync()
    {
        if (SelectedAlarm is not { Latest.EventId: { } eventId } alarm)
        {
            return;
        }

        try
        {
            var source = _source;
            var comment = AckComment;
            await Task.Run(() => source.AcknowledgeAsync(alarm.ConditionId, eventId, comment));
            AckComment = string.Empty;
            StatusText = $"Acknowledged {alarm.Source} · {alarm.Name}";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            StatusText = alarm.HasEventId
                ? $"Acknowledge failed: {AppErrors.Describe(ex)}"
                : $"The server refused the acknowledgement ({AppErrors.Describe(ex)}): it sent this alarm without an event id, so it can't be matched. Acknowledge it on the server or in its own tools.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        IsSubscribed = false;
        StatusText = "Closed";
        if (Interlocked.Exchange(ref _subscription, null) is { } subscription)
        {
            try
            {
                await Task.Run(() => subscription.DisposeAsync().AsTask());
            }
            catch (Exception ex) when (AppErrors.IsRecoverable(ex))
            {
                AppErrors.Log(ex, "unsubscribing events");
            }
        }
    }
}
