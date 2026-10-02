using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>History windows show at most this many values per item (the oldest first).</summary>
    public const int MaxHistoryValues = RecordingViewerViewModel.MaxRows;

    /// <summary>The connection can read stored history (OPC UA).</summary>
    public bool SupportsHistory => _client is IHistorySource;

    private bool CanShowHistory() => IsConnected && SupportsHistory;

    /// <summary>Shows the stored history of the selected variables in the address space for the last <paramref name="minutes"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanShowHistory))]
    private Task ShowHistoryAsync(int minutes) =>
        ShowHistoryForAsync([.. SelectionOrCurrent().Where(n => n.IsVariable).Select(n => (n.NodeId, n.DisplayName, n.NodeIdText))], minutes);

    /// <summary>Shows the stored history of the selected Watch rows for the last <paramref name="minutes"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanShowHistory))]
    private Task ShowWatchHistoryAsync(int minutes) =>
        ShowHistoryForAsync([.. WatchSelectionOrCurrent().Select(w => (w.NodeId, w.DisplayName, w.NodeIdText))], minutes);

    private async Task ShowHistoryForAsync(IReadOnlyList<(NodeId NodeId, string Name, string Id)> items, int minutes)
    {
        if (_client is not IHistorySource source || Dialogs is null)
        {
            return;
        }

        if (items.Count == 0)
        {
            StatusMessage = "Select one or more variables to show their history";
            return;
        }

        var end = DateTime.UtcNow;
        var start = end.AddMinutes(-minutes);
        var range = FormatRange(minutes);
        IsBusy = true;
        StatusMessage = $"Reading history of the last {range}…";
        try
        {
            var rows = new List<HistoryRow>();
            var notes = new List<string>();
            foreach (var (nodeId, name, id) in items)
            {
                try
                {
                    var result = await Task.Run(() => source.ReadHistoryAsync(nodeId, start, end, MaxHistoryValues));
                    rows.AddRange(result.Values.Select(v => ToRow(v, name, id)));
                    if (result.Values.Count == 0)
                    {
                        notes.Add($"{name}: no stored values");
                    }
                    else if (result.Truncated)
                    {
                        notes.Add($"{name}: first {MaxHistoryValues:N0} values only");
                    }
                }
                catch (ServiceResultException ex)
                {
                    notes.Add($"{name}: {AppErrors.Describe(ex)}");
                }
            }

            if (rows.Count == 0)
            {
                StatusMessage = string.Empty;
                ReportError(new InvalidOperationException(
                    $"No history in the last {range}. {string.Join("; ", notes)}. The server may not store history for {(items.Count == 1 ? "this variable" : "these variables")} (attribute Historizing)."));
                return;
            }

            rows.Sort((a, b) => a.ReceivedAt.CompareTo(b.ReceivedAt));
            var title = items.Count == 1 ? $"{items[0].Name} — history, last {range}" : $"{items.Count} items — history, last {range}";
            var status = $"History from the server · {rows.Count:N0} value(s) · {Timestamps.FormatSeconds(start)} – {Timestamps.FormatSeconds(end)}"
                + (notes.Count > 0 ? " · " + string.Join("; ", notes) : string.Empty);
            Dialogs.ShowRecordingViewer(RecordingViewerViewModel.ForRows(title, rows, status));
            StatusMessage = $"History: {rows.Count:N0} value(s)";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            StatusMessage = string.Empty;
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static HistoryRow ToRow(ValueUpdate value, string name, string id)
    {
        // History is about when the value was valid: the source timestamp, else the server's.
        var time = value.SourceTimestamp != DateTime.MinValue ? value.SourceTimestamp : value.ServerTimestamp;
        var stamp = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
        return new HistoryRow(stamp, name, id, value.Value, value.Status.SymbolicId ?? value.Status.ToString(),
            stamp.ToString("O", CultureInfo.InvariantCulture), value.Numeric);
    }

    private static string FormatRange(int minutes) => minutes switch
    {
        < 60 => $"{minutes} min",
        < 1440 when minutes % 60 == 0 => minutes == 60 ? "hour" : $"{minutes / 60} h",
        _ when minutes % 1440 == 0 => minutes == 1440 ? "24 h" : $"{minutes / 1440} days",
        _ => $"{minutes} min",
    };
}
