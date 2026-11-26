using Opc.Ua;
using Opc.Ua.Client;
using MachineDataBrowser.Core;
using UaItemOptions = Opc.Ua.Client.Subscriptions.MonitoredItems.MonitoredItemOptions;

namespace MachineDataBrowser.Driver.OpcUa;

public sealed partial class OpcUaClient : IEventSource
{
    Task<IAsyncDisposable> IEventSource.SubscribeEventsAsync(DeviceNodeId notifier, Action<EventNotification> onEvent, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => SubscribeEventsAsync(notifier.IsNull ? ObjectIds.Server : UaNodeIdAdapter.ToUa(notifier), onEvent, cancellationToken));

    Task IEventSource.AcknowledgeAsync(DeviceNodeId conditionId, byte[] eventId, string comment, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => AcknowledgeAsync(UaNodeIdAdapter.ToUa(conditionId), eventId, comment, cancellationToken));

    // Order matters: notifications carry the selected fields by index.
    private static readonly (NodeId Type, string[] Path)[] EventFields =
    [
        (ObjectTypeIds.BaseEventType, [BrowseNames.EventId]),
        (ObjectTypeIds.BaseEventType, [BrowseNames.EventType]),
        (ObjectTypeIds.BaseEventType, [BrowseNames.SourceName]),
        (ObjectTypeIds.BaseEventType, [BrowseNames.Time]),
        (ObjectTypeIds.BaseEventType, [BrowseNames.Message]),
        (ObjectTypeIds.BaseEventType, [BrowseNames.Severity]),
        (ObjectTypeIds.ConditionType, [BrowseNames.ConditionName]),
        (ObjectTypeIds.ConditionType, [BrowseNames.Retain]),
        (ObjectTypeIds.AlarmConditionType, [BrowseNames.ActiveState, BrowseNames.Id]),
        (ObjectTypeIds.AcknowledgeableConditionType, [BrowseNames.AckedState, BrowseNames.Id]),
    ];

    private const int ConditionIdField = 10;

    public async Task<IAsyncDisposable> SubscribeEventsAsync(NodeId notifier, Action<EventNotification> onEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        var session = RequireSession();

        var filter = new EventFilter();
        var selectClauses = new List<SimpleAttributeOperand>();
        foreach (var (type, path) in EventFields)
        {
            selectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = type,
                BrowsePath = [.. path.Select(name => new QualifiedName(name))],
                AttributeId = Attributes.Value,
            });
        }

        // The condition's own NodeId: the target of Acknowledge.
        selectClauses.Add(new SimpleAttributeOperand
        {
            TypeDefinitionId = ObjectTypeIds.ConditionType,
            AttributeId = Attributes.NodeId,
        });
        filter.SelectClauses = selectClauses.ToArrayOf();

        var subscription = AddSubscription(session, "Events", 250, keepAliveCount: 10, lifetimeCount: 100, fields =>
        {
            // SDK thread: never throw.
            try
            {
                if (ToNotification(session, fields) is { } notification)
                {
                    onEvent(notification);
                }
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                System.Diagnostics.Trace.TraceError($"Event handler failed: {ex}");
            }
        });
        var options = new OptionsMonitor<UaItemOptions>(new UaItemOptions
        {
            StartNodeId = notifier,
            AttributeId = Attributes.EventNotifier,
            SamplingInterval = TimeSpan.Zero,
            QueueSize = 1000,
            DiscardOldest = true,
            Filter = filter,
        });
        if (!subscription.Subscription.MonitoredItems.TryAdd("Events", options, out var item) || item is null)
        {
            await RemoveSubscriptionAsync(subscription).ConfigureAwait(false);
            throw new ServiceResultException(StatusCodes.BadTooManyMonitoredItems);
        }

        await UaSubscription.WaitUntilAppliedAsync([item], stopOnError: true, cancellationToken).ConfigureAwait(false);
        if (!item.Created && ServiceResult.IsBad(item.Error))
        {
            // Removing the subscription may clear the item's status: keep the server's answer first.
            var error = item.Error;
            await RemoveSubscriptionAsync(subscription).ConfigureAwait(false);
            throw new ServiceResultException(error);
        }

        try
        {
            await subscription.Subscription.ConditionRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException)
        {
            // Servers without Alarms & Conditions reject the refresh; plain events still arrive.
        }

        return new EventHandle(this, subscription);
    }

    public async Task AcknowledgeAsync(NodeId conditionId, byte[] eventId, string comment, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var request = new List<CallMethodRequest>
        {
            new CallMethodRequest
            {
                ObjectId = conditionId,
                MethodId = MethodIds.AcknowledgeableConditionType_Acknowledge,
                InputArguments = [new Variant(eventId), new Variant(new LocalizedText(comment))],
            },
        };
        var response = await session.CallAsync(null, request, cancellationToken).ConfigureAwait(false);
        if (response.Results.Count > 0 && StatusCode.IsBad(response.Results[0].StatusCode))
        {
            var argument = response.Results[0].InputArgumentResults.ToList().FirstOrDefault(StatusCode.IsBad);
            throw new ServiceResultException(argument is { } bad && StatusCode.IsBad(bad) ? bad : response.Results[0].StatusCode);
        }
    }

    private static EventNotification? ToNotification(ISession session, ArrayOf<Variant> fields)
    {
        if (fields.Count <= ConditionIdField)
        {
            return null;
        }

        // ConditionRefresh brackets the resent conditions with RefreshStart/RefreshEnd events: not shown.
        var eventType = fields[1].AsBoxedObject() is NodeId type ? type : NodeId.Null;
        if (eventType == ObjectTypeIds.RefreshStartEventType || eventType == ObjectTypeIds.RefreshEndEventType)
        {
            return null;
        }

        var typeName = eventType.IsNull ? string.Empty : EventTypeName(session, eventType);
        return new EventNotification(
            fields[3].AsBoxedObject() is DateTimeUtc time ? time.ToDateTime() : DateTime.UtcNow,
            fields[5].AsBoxedObject() is ushort severity ? severity : (ushort)0,
            fields[2].AsBoxedObject() as string ?? string.Empty,
            fields[4].AsBoxedObject() is LocalizedText message ? message.Text ?? string.Empty : string.Empty,
            typeName,
            fields[0].AsBoxedObject() is ByteString eventId ? eventId.Memory.ToArray() : null,
            fields[ConditionIdField].AsBoxedObject() is NodeId conditionId && !conditionId.IsNull ? UaNodeIdAdapter.ToDevice(conditionId) : null,
            fields[6].AsBoxedObject() as string,
            fields[8].AsBoxedObject() is bool active ? active : null,
            fields[9].AsBoxedObject() is bool acked ? acked : null,
            fields[7].AsBoxedObject() is true);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NodeId, string> EventTypeNames = new(
        typeof(ObjectTypeIds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(NodeId))
            .Select(f => System.Collections.Generic.KeyValuePair.Create((NodeId)f.GetValue(null)!, f.Name))
            .DistinctBy(p => p.Key));

    /// <summary>
    /// Standard event types by name at once; server-specific ones show their NodeId until a background lookup of the
    /// display name (the handler runs on an SDK thread and must not block on a service call) fills the cache.
    /// </summary>
    private static string EventTypeName(ISession session, NodeId eventType)
    {
        if (EventTypeNames.TryGetValue(eventType, out var name))
        {
            return name;
        }

        EventTypeNames[eventType] = eventType.ToString();
        _ = Task.Run(async () =>
        {
            try
            {
                if (await session.NodeCache.FindAsync(eventType).ConfigureAwait(false) is { DisplayName.Text: { Length: > 0 } text })
                {
                    EventTypeNames[eventType] = text;
                }
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                // Keep the NodeId as the name.
            }
        });
        return eventType.ToString();
    }

    private sealed class EventHandle(OpcUaClient client, UaSubscription subscription) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await client.RemoveSubscriptionAsync(subscription).ConfigureAwait(false);
    }
}
