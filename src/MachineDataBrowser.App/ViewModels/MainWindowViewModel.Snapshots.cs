using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.Services;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private SnapshotStore? _snapshots;

    private SnapshotStore Snapshots => _snapshots ??= new SnapshotStore(_settingsStore is null ? null : Path.Combine(_settingsStore.Folder, "snapshots"));

    /// <summary>The watch values right now, as a snapshot (not saved).</summary>
    private Snapshot CurrentSnapshot(string name, IEnumerable<WatchItemViewModel>? items = null) => new()
    {
        Name = name,
        TakenAt = DateTimeOffset.Now,
        EndpointUrl = EndpointUrl.Trim(),
        Items = [.. (items ?? WatchItems).Select(w => new SnapshotItem(w.DisplayName, w.PortableId, w.Value, w.Status,
            double.TryParse(w.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null))],
    };

    /// <summary>Saves the current values of the watch list, to compare with later.</summary>
    [RelayCommand(CanExecute = nameof(HasWatchItems))]
    private void TakeSnapshot() => SaveSnapshot(null);

    /// <summary>Like <see cref="TakeSnapshot"/>, but asks for a name first (e.g. a parameter set's), so it needn't be renamed later.</summary>
    [RelayCommand(CanExecute = nameof(HasWatchItems))]
    private async Task TakeNamedSnapshotAsync()
    {
        if (Dialogs is null
            || await Dialogs.AskSnapshotNameAsync(DefaultSnapshotName(DateTimeOffset.Now), WatchItems.Count) is not { } name
            || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SaveSnapshot(null, name.Trim());
    }

    private bool HasSelectedWatchRows() => SelectedWatchItems.Count > 0;

    /// <summary>Only the selected rows: a parameter set out of a large watch list.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedWatchRows))]
    private void TakeSelectionSnapshot() => SaveSnapshot([.. SelectedWatchItems]);

    private static string DefaultSnapshotName(DateTimeOffset at) =>
        $"Snapshot {at.ToLocalTime().ToString("d MMM HH:mm:ss", CultureInfo.InvariantCulture)}";

    private void SaveSnapshot(IReadOnlyList<WatchItemViewModel>? items, string? name = null)
    {
        var now = DateTimeOffset.Now;
        name ??= DefaultSnapshotName(now);
        try
        {
            var snapshot = Snapshots.Save(CurrentSnapshot(name, items) with { TakenAt = now, Partial = items is not null });
            StatusMessage = $"{name}: {snapshot.Items.Count} value(s) saved. Watch ▸ Compare with Snapshot shows what changed.";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
    }

    /// <summary>Live connection details (session, subscriptions, update rates); works for every protocol.</summary>
    [RelayCommand]
    private void ShowDiagnostics()
    {
        var diagnostics = new DiagnosticsViewModel(() => _client, () => EndpointUrl.Trim(), WatchItems);
        Dialogs?.ShowDiagnostics(diagnostics);
        _ = diagnostics.RefreshAsync();
    }

    /// <summary>Opens the comparison of the newest snapshot with the live watch values.</summary>
    [RelayCommand]
    private void CompareSnapshot() => Dialogs?.ShowSnapshotCompare(new SnapshotCompareViewModel(Snapshots, () => CurrentSnapshot("Now"), writer: new SnapshotWriter(this), dialogs: Dialogs));

    /// <summary>Writes snapshot values to the watch rows they were taken from, matched by portable id.</summary>
    private sealed class SnapshotWriter(MainWindowViewModel owner) : ISnapshotWriter
    {
        public bool CanWrite => owner.IsConnected && !owner.IsReadOnly;

        public string EndpointUrl => owner.EndpointUrl;

        public async Task<IReadOnlyList<string>> WriteAsync(IReadOnlyList<SnapshotDiffRow> rows)
        {
            // Checked again here: the session may have turned read-only while the question was shown.
            if (!CanWrite)
            {
                return [.. rows.Select(r => $"{r.Name}: read-only or disconnected")];
            }

            var items = owner.WatchItems.GroupBy(w => w.PortableId).ToDictionary(g => g.Key, g => g.First());
            var failures = new List<string>();
            foreach (var row in rows)
            {
                if (!items.TryGetValue(row.NodeId, out var item))
                {
                    failures.Add($"{row.Name}: no longer in the watch list");
                    continue;
                }

                try
                {
                    await owner._client.WriteValueAsync(item.NodeId, row.Before);
                }
                catch (Exception ex) when (AppErrors.IsRecoverable(ex))
                {
                    failures.Add($"{row.Name}: {ex.Message}");
                }
            }

            owner.StatusMessage = failures.Count == 0
                ? $"Wrote {rows.Count} snapshot value(s)"
                : $"Wrote {rows.Count - failures.Count} of {rows.Count} snapshot value(s); {failures.Count} failed";
            return failures;
        }
    }
}
