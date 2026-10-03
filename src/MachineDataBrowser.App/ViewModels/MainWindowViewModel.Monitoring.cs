using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Monitored items can be tuned (OPC UA).</summary>
    public bool SupportsMonitoringSettings => _client is IMonitoringSettings;

    private bool CanEditMonitoring() => IsConnected && SupportsMonitoringSettings && HasSelectedWatchItem();

    /// <summary>Sampling interval, queue and deadband for the selected Watch rows.</summary>
    [RelayCommand(CanExecute = nameof(CanEditMonitoring))]
    private async Task EditMonitoringAsync()
    {
        var items = WatchSelectionOrCurrent().Where(i => i.Monitor is not null).ToList();
        if (items.Count == 0 || Dialogs is null)
        {
            return;
        }

        var label = items.Count == 1 ? items[0].DisplayName : $"{items.Count} items";
        var refresh = items.Select(i => i.RefreshMs).Distinct().Count() == 1 ? items[0].RefreshMs : (int?)null;
        if (await Dialogs.EditMonitoringAsync(items[0].Monitoring, label, refresh) is not { } options)
        {
            return;
        }

        var failed = await ApplyMonitoringAsync(items, options);
        MarkDirty();
        StatusMessage = failed == 0
            ? $"Monitoring {options.Describe()} for {label}"
            : $"Monitoring changed for {items.Count - failed} of {items.Count} item(s)";
    }

    /// <summary>Applies the items' own settings after their monitors were re-created (refresh change, session open).</summary>
    private async Task ReapplyMonitoringAsync(IReadOnlyList<WatchItemViewModel> items)
    {
        foreach (var group in items.GroupBy(i => i.Monitoring))
        {
            await ApplyMonitoringAsync([.. group], group.Key);
        }
    }

    /// <summary>Returns how many items the server rejected; those keep their previous settings.</summary>
    private async Task<int> ApplyMonitoringAsync(List<WatchItemViewModel> items, MonitoringOptions options)
    {
        if (_client is not IMonitoringSettings settings || items.Count == 0)
        {
            return 0;
        }

        try
        {
            var monitors = items.Select(i => i.Monitor!).ToList();
            var results = await Task.Run(() => settings.ApplyMonitoringOptionsAsync(monitors, options));
            var failures = new List<string>();
            for (var i = 0; i < items.Count; i++)
            {
                if (ServiceResult.IsGood(results[i]))
                {
                    items[i].Monitoring = options;
                }
                else
                {
                    failures.Add($"{items[i].DisplayName}: {StatusText.Of(results[i].StatusCode)}");
                }
            }

            if (failures.Count > 0)
            {
                var hint = options.Deadband == DeadbandKind.Percent ? " A percent deadband needs an EURange on the variable." : string.Empty;
                ErrorMessage = $"The server rejected the monitoring settings for {string.Join("; ", failures.Take(3))}{(failures.Count > 3 ? "…" : string.Empty)}.{hint}";
            }

            return failures.Count;
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
            return items.Count;
        }
    }
}
