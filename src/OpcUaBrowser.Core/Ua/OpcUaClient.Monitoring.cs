using Opc.Ua;
using Opc.Ua.Client;

namespace OpcUaBrowser.Core.Ua;

public sealed partial class OpcUaClient : IMonitoringSettings
{
    public async Task<IReadOnlyList<ServiceResult>> ApplyMonitoringOptionsAsync(
        IReadOnlyList<IAsyncDisposable> monitors,
        MonitoringOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(options);
        var results = new ServiceResult[monitors.Count];
        Array.Fill(results, ServiceResult.Good);

        var owned = monitors.Select((m, i) => (Handle: m as MonitorHandle, Index: i)).Where(p => p.Handle is not null).ToList();
        foreach (var group in owned.GroupBy(p => p.Handle!.Subscription))
        {
            var subscription = group.Key;
            var previous = group.Select(p => (p.Handle!.Item, p.Index, Settings: Capture(p.Handle!.Item))).ToList();
            foreach (var (item, _, _) in previous)
            {
                item.SamplingInterval = (int)(options.SamplingIntervalMs ?? subscription.PublishingInterval);
                item.QueueSize = Math.Max(1, options.QueueSize);
                item.DiscardOldest = options.DiscardOldest;
                item.Filter = options.Deadband == DeadbandKind.None
                    ? null
                    : new DataChangeFilter
                    {
                        Trigger = DataChangeTrigger.StatusValue,
                        DeadbandType = (uint)(options.Deadband == DeadbandKind.Percent ? DeadbandType.Percent : DeadbandType.Absolute),
                        DeadbandValue = options.DeadbandValue,
                    };
            }

            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);

            // A rejected change (e.g. percent deadband without an EURange) leaves the item as it was.
            var rejected = previous.Where(p => ServiceResult.IsBad(p.Item.Status.Error)).ToList();
            foreach (var (item, index, settings) in rejected)
            {
                results[index] = item.Status.Error ?? new ServiceResult(StatusCodes.Bad);
                Restore(item, settings);
            }

            if (rejected.Count > 0)
            {
                await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>The values the server granted for a monitor (it may revise sampling and queue size); null for other handles.</summary>
    public static (double SamplingIntervalMs, uint QueueSize)? GetRevisedMonitoring(IAsyncDisposable monitor) =>
        monitor is MonitorHandle { Item.Status: { } status } ? (status.SamplingInterval, status.QueueSize) : null;

    private static (int Sampling, uint Queue, bool DiscardOldest, MonitoringFilter? Filter) Capture(MonitoredItem item) =>
        (item.SamplingInterval, item.QueueSize, item.DiscardOldest, item.Filter);

    private static void Restore(MonitoredItem item, (int Sampling, uint Queue, bool DiscardOldest, MonitoringFilter? Filter) settings)
    {
        item.SamplingInterval = settings.Sampling;
        item.QueueSize = settings.Queue;
        item.DiscardOldest = settings.DiscardOldest;
        item.Filter = settings.Filter;
    }
}
