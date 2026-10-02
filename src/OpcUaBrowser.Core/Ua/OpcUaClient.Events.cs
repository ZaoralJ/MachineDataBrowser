using Opc.Ua;
using Opc.Ua.Client;

namespace OpcUaBrowser.Core.Ua;

public sealed partial class OpcUaClient : IEventSource
{
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
        foreach (var (type, path) in EventFields)
        {
            filter.SelectClauses.Add(new SimpleAttributeOperand
            {
                TypeDefinitionId = type,
                BrowsePath = [.. path.Select(name => new QualifiedName(name))],
                AttributeId = Attributes.Value,
            });
        }

        // The condition's own NodeId: the target of Acknowledge.
        filter.SelectClauses.Add(new SimpleAttributeOperand
        {
            TypeDefinitionId = ObjectTypeIds.ConditionType,
            AttributeId = Attributes.NodeId,
        });

        var subscription = new Subscription(session.DefaultSubscription)
        {
            DisplayName = "Events",
            PublishingInterval = 250,
            PublishingEnabled = true,
            KeepAliveCount = 10,
            LifetimeCount = 100,
        };
        var item = new MonitoredItem(Telemetry)
        {
            StartNodeId = notifier,
            AttributeId = Attributes.EventNotifier,
            SamplingInterval = 0,
            QueueSize = 1000,
            DiscardOldest = true,
            Filter = filter,
        };
        item.Notification += (_, e) =>
        {
            // SDK thread: never throw.
            try
            {
                if (e.NotificationValue is EventFieldList list && ToNotification(session, list.EventFields) is { } notification)
                {
                    onEvent(notification);
                }
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                System.Diagnostics.Trace.TraceError($"Event handler failed: {ex}");
            }
        };

        subscription.AddItem(item);
        session.AddSubscription(subscription);
        await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
        if (ServiceResult.IsBad(item.Status.Error))
        {
            await session.RemoveSubscriptionAsync(subscription, cancellationToken).ConfigureAwait(false);
            throw new ServiceResultException(item.Status.Error);
        }

        try
        {
            await subscription.ConditionRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException)
        {
            // Servers without Alarms & Conditions reject the refresh; plain events still arrive.
        }

        return new EventHandle(subscription);
    }

    public async Task AcknowledgeAsync(NodeId conditionId, byte[] eventId, string comment, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var request = new CallMethodRequestCollection
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
            var argument = response.Results[0].InputArgumentResults?.FirstOrDefault(StatusCode.IsBad);
            throw new ServiceResultException(argument is { } bad && StatusCode.IsBad(bad) ? bad : response.Results[0].StatusCode);
        }
    }

    private static EventNotification? ToNotification(ISession session, VariantCollection fields)
    {
        if (fields.Count <= ConditionIdField)
        {
            return null;
        }

        // ConditionRefresh brackets the resent conditions with RefreshStart/RefreshEnd events: not shown.
        var eventType = fields[1].Value as NodeId;
        if (eventType == ObjectTypeIds.RefreshStartEventType || eventType == ObjectTypeIds.RefreshEndEventType)
        {
            return null;
        }

        var typeName = eventType is null ? string.Empty : EventTypeName(session, eventType);
        return new EventNotification(
            fields[3].Value is DateTime time ? time : DateTime.UtcNow,
            fields[5].Value is ushort severity ? severity : (ushort)0,
            fields[2].Value as string ?? string.Empty,
            (fields[4].Value as LocalizedText)?.Text ?? string.Empty,
            typeName,
            fields[0].Value as byte[],
            fields[ConditionIdField].Value as NodeId,
            fields[6].Value as string,
            fields[8].Value as bool?,
            fields[9].Value as bool?,
            fields[7].Value is true);
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

    private sealed class EventHandle(Subscription subscription) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (subscription.Session is { Connected: true } session)
            {
                await session.RemoveSubscriptionAsync(subscription).ConfigureAwait(false);
            }
        }
    }
}
