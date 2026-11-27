using System.Collections.Concurrent;
using Opc.Ua;
using Opc.Ua.Client.Subscriptions;
using Opc.Ua.Client.Subscriptions.MonitoredItems;

namespace MachineDataBrowser.Driver.OpcUa;

/// <summary>
/// One subscription of the SDK's subscription manager. The SDK hands every notification of a subscription to a
/// single handler; this one passes each value on to the callback of the monitored item it belongs to.
/// </summary>
internal sealed class UaSubscription : ISubscriptionNotificationHandler
{
    private readonly ConcurrentDictionary<string, Action<DataValue>> _values = new(StringComparer.Ordinal);
    private readonly Action<ArrayOf<Variant>>? _onEvent;
    private long _notifications;
    private long _lastNotificationTicks;

    private UaSubscription(string name, double publishingIntervalMs, Action<ArrayOf<Variant>>? onEvent)
    {
        Name = name;
        PublishingIntervalMs = publishingIntervalMs;
        _onEvent = onEvent;
    }

    public string Name { get; }

    /// <summary>The interval asked for; the server's revised one is on <see cref="Subscription"/>.</summary>
    public double PublishingIntervalMs { get; }

    public ISubscription Subscription { get; private set; } = null!;

    public long NotificationCount => Interlocked.Read(ref _notifications);

    public DateTime? LastNotificationTime =>
        Interlocked.Read(ref _lastNotificationTicks) is var ticks and > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    /// <summary>The SDK creates the subscription on the server in the background.</summary>
    public static UaSubscription Add(
        ISubscriptionManager manager,
        string name,
        double publishingIntervalMs,
        uint keepAliveCount,
        uint lifetimeCount,
        Action<ArrayOf<Variant>>? onEvent = null)
    {
        var handler = new UaSubscription(name, publishingIntervalMs, onEvent);
        handler.Subscription = manager.Add(handler, new OptionsMonitor<SubscriptionOptions>(new SubscriptionOptions
        {
            PublishingInterval = TimeSpan.FromMilliseconds(publishingIntervalMs),
            KeepAliveCount = keepAliveCount,
            LifetimeCount = lifetimeCount,
            PublishingEnabled = true,
        }));
        return handler;
    }

    /// <summary>Register before adding the item: its first value can arrive as soon as the server created it.</summary>
    public void SetValueHandler(string itemName, Action<DataValue> onValue) => _values[itemName] = onValue;

    public void RemoveValueHandler(string itemName) => _values.TryRemove(itemName, out _);

    public ValueTask OnDataChangeNotificationAsync(
        ISubscription subscription,
        uint sequenceNumber,
        DateTime publishTime,
        ReadOnlyMemory<DataValueChange> notification,
        PublishState publishStateMask,
        IReadOnlyList<string> stringTable)
    {
        Count();
        foreach (var change in notification.Span)
        {
            if (change.MonitoredItem is { } item && change.Value is { } value && _values.TryGetValue(item.Name, out var onValue))
            {
                onValue(value);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnEventDataNotificationAsync(
        ISubscription subscription,
        uint sequenceNumber,
        DateTime publishTime,
        ReadOnlyMemory<Opc.Ua.Client.Subscriptions.EventNotification> notification,
        PublishState publishStateMask,
        IReadOnlyList<string> stringTable)
    {
        Count();
        if (_onEvent is { } onEvent)
        {
            foreach (var e in notification.Span)
            {
                onEvent(e.Fields);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnKeepAliveNotificationAsync(ISubscription subscription, uint sequenceNumber, DateTime publishTime, PublishState publishStateMask) =>
        ValueTask.CompletedTask;

    public ValueTask OnSubscriptionStateChangedAsync(ISubscription subscription, SubscriptionState state, PublishState publishStateMask, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <summary>The server id, for diagnostics; 0 until it is created.</summary>
    public uint ServerId => Subscription is IPartitionedSubscription { PartitionIds: [var id, ..] } ? id : 0;

    private void Count()
    {
        Interlocked.Increment(ref _notifications);
        Interlocked.Exchange(ref _lastNotificationTicks, DateTime.UtcNow.Ticks);
    }

    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The SDK applies monitored item changes in the background: wait until the server answered each of them, or
    /// (<paramref name="stopOnError"/>) rejected it. Gives up after a while, e.g. while the connection is down: the SDK
    /// keeps applying them once it is back.
    /// </summary>
    public static async Task WaitUntilAppliedAsync(IEnumerable<IMonitoredItem> items, bool stopOnError, CancellationToken cancellationToken)
    {
        var pending = items.ToList();
        var deadline = DateTime.UtcNow + ApplyTimeout;
        while (true)
        {
            pending.RemoveAll(item => IsApplied(item, stopOnError));
            if (pending.Count == 0 || DateTime.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsApplied(IMonitoredItem item, bool stopOnError)
    {
        var changing = item is IMonitoredItemApplyState { HasPendingChanges: true };
        // Like the SDK's own wait: BadWaitingForInitialData only means the change is still under way.
        if (stopOnError && ServiceResult.IsBad(item.Error) && !(changing && item.Error.StatusCode == StatusCodes.BadWaitingForInitialData))
        {
            return true;
        }

        return item.Created && !changing;
    }
}
