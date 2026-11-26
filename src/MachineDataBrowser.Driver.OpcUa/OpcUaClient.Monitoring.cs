using Opc.Ua;
using Opc.Ua.Client;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.OpcUa;

public sealed partial class OpcUaClient : IMonitoringSettings
{
    async Task<IReadOnlyList<DeviceError>> IMonitoringSettings.ApplyMonitoringOptionsAsync(
        IReadOnlyList<IAsyncDisposable> monitors,
        MonitoringOptions options,
        CancellationToken cancellationToken) =>
        await UaStatusAdapter.TranslateAsync(async () =>
            (await ApplyMonitoringOptionsAsync(monitors, options, cancellationToken).ConfigureAwait(false))
                .Select(UaStatusAdapter.ToDevice).ToList()).ConfigureAwait(false);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the connected-client API.")]
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
        var previous = owned.Select(p => (p.Handle!, p.Index, Settings: p.Handle!.Options.CurrentValue)).ToList();
        foreach (var (handle, _, settings) in previous)
        {
            handle.Options.CurrentValue = settings with
            {
                SamplingInterval = TimeSpan.FromMilliseconds((int)(options.SamplingIntervalMs ?? handle.Subscription.PublishingIntervalMs)),
                QueueSize = Math.Max(1, options.QueueSize),
                DiscardOldest = options.DiscardOldest,
                Filter = options.Deadband == DeadbandKind.None
                    ? null
                    : new DataChangeFilter
                    {
                        Trigger = DataChangeTrigger.StatusValue,
                        DeadbandType = (uint)(options.Deadband == DeadbandKind.Percent ? DeadbandType.Percent : DeadbandType.Absolute),
                        DeadbandValue = options.DeadbandValue,
                    },
            };
        }

        await UaSubscription.WaitUntilAppliedAsync(previous.Select(p => p.Item1.Item), stopOnError: true, cancellationToken).ConfigureAwait(false);

        // A rejected change (e.g. percent deadband without an EURange) goes back to what the item had.
        var rejected = previous.Where(p => ServiceResult.IsBad(p.Item1.Item.Error)).ToList();
        foreach (var (handle, index, settings) in rejected)
        {
            results[index] = handle.Item.Error;
            handle.Options.CurrentValue = settings;
        }

        if (rejected.Count > 0)
        {
            await UaSubscription.WaitUntilAppliedAsync(rejected.Select(p => p.Item1.Item), stopOnError: false, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>The values the server granted for a monitor (it may revise sampling and queue size); null for other handles.</summary>
    public static (double SamplingIntervalMs, uint QueueSize)? GetRevisedMonitoring(IAsyncDisposable monitor) =>
        monitor is MonitorHandle { Item: var item } ? (item.CurrentSamplingInterval.TotalMilliseconds, item.CurrentQueueSize) : null;
}
