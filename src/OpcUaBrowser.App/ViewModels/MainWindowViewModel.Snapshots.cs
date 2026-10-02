using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private SnapshotStore? _snapshots;

    private SnapshotStore Snapshots => _snapshots ??= new SnapshotStore(_settingsStore is null ? null : Path.Combine(_settingsStore.Folder, "snapshots"));

    /// <summary>The watch values right now, as a snapshot (not saved).</summary>
    private Snapshot CurrentSnapshot(string name) => new()
    {
        Name = name,
        TakenAt = DateTimeOffset.Now,
        EndpointUrl = EndpointUrl.Trim(),
        Items = [.. WatchItems.Select(w => new SnapshotItem(w.DisplayName, w.PortableId, w.Value, w.Status,
            double.TryParse(w.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null))],
    };

    /// <summary>Saves the current values of the watch list, to compare with later.</summary>
    [RelayCommand(CanExecute = nameof(HasWatchItems))]
    private void TakeSnapshot()
    {
        var now = DateTimeOffset.Now;
        var name = $"Snapshot {now.ToLocalTime().ToString("d MMM HH:mm:ss", CultureInfo.InvariantCulture)}";
        try
        {
            var snapshot = Snapshots.Save(CurrentSnapshot(name) with { TakenAt = now });
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
    private void CompareSnapshot() => Dialogs?.ShowSnapshotCompare(new SnapshotCompareViewModel(Snapshots, () => CurrentSnapshot("Now")));
}
