using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;

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
        Items = [.. (items ?? WatchItems).Select(w => SnapshotItem.Of(w.DisplayName, w.PortableId, w.Value, w.Status) with { Path = w.Path.Length > 0 ? $"/{w.Path}/{w.DisplayName}" : null })],
    };

    /// <summary>Saves the current values of the watch list, to compare with later.</summary>
    [RelayCommand(CanExecute = nameof(HasWatchItems))]
    private void TakeSnapshot() => SaveSnapshot(CurrentSnapshot(DefaultSnapshotName(DateTimeOffset.Now)));

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

        SaveSnapshot(CurrentSnapshot(name.Trim()));
    }

    private bool HasSelectedWatchRows() => SelectedWatchItems.Count > 0;

    /// <summary>Only the selected rows: a parameter set out of a large watch list.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedWatchRows))]
    private void TakeSelectionSnapshot() =>
        SaveSnapshot(CurrentSnapshot(DefaultSnapshotName(DateTimeOffset.Now), [.. SelectedWatchItems]) with { Partial = true });

    /// <summary>
    /// Every variable below the selected folders or structures, read once from the device: e.g. a machine's settings
    /// documented on a date, without adding them all to the watch list. Items are named by their path below the folder.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMonitorContents))]
    private async Task TakeFolderSnapshotAsync()
    {
        var folders = SelectionOrCurrent().Where(n => n.HasChildren).ToList();
        if (Dialogs is null || folders.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Reading the variables below the folder…";
        List<SnapshotItem> items;
        bool limited;
        try
        {
            var client = _client;
            var max = MaxRecursiveItems;
            (items, limited) = await Task.Run(async () =>
            {
                var found = new List<(NodeId NodeId, string Name, string Path)>();
                foreach (var folder in folders)
                {
                    // With several folders, their names keep equal paths below them apart.
                    var prefix = folders.Count > 1 ? folder.DisplayName : string.Empty;
                    var collected = await client.CollectVariablesWithPathsAsync(folder.NodeId, MaxRecursiveDepth, max - found.Count, descendIntoVariables: false);
                    found.AddRange(collected.Select(v => (v.Item.NodeId, JoinPath(JoinPath(prefix, v.Path), v.Item.DisplayName),
                        $"/{JoinPath(JoinPath(folder.Path, v.Path), v.Item.DisplayName)}")));
                    if (found.Count >= max)
                    {
                        break;
                    }
                }

                var values = found.Count == 0 ? [] : await client.ReadValuesAsync([.. found.Select(f => f.NodeId)]);
                return (found.Select((f, i) => SnapshotReader.FromValue(f.Name, client.ToPortableId(f.NodeId), values[i], f.Path)).ToList(), found.Count >= max);
            });
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (items.Count == 0)
        {
            StatusMessage = "No variables below the selected folder";
            return;
        }

        var suggested = $"{(folders.Count == 1 ? folders[0].DisplayName : $"{folders.Count} folders")} {DateTimeOffset.Now.ToLocalTime().ToString("d MMM HH:mm:ss", CultureInfo.InvariantCulture)}";
        if (await Dialogs.AskSnapshotNameAsync(suggested, items.Count) is not { } name || string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = string.Empty;
            return;
        }

        SaveSnapshot(new Snapshot { Name = name.Trim(), TakenAt = DateTimeOffset.Now, EndpointUrl = EndpointUrl.Trim(), Items = items, Partial = true });
        if (limited)
        {
            StatusMessage += $" Limited to the first {items.Count} variables (Settings ▸ maximum items).";
        }
    }

    private static string DefaultSnapshotName(DateTimeOffset at) =>
        $"Snapshot {at.ToLocalTime().ToString("d MMM HH:mm:ss", CultureInfo.InvariantCulture)}";

    private void SaveSnapshot(Snapshot snapshot)
    {
        try
        {
            var saved = Snapshots.Save(snapshot with { TakenAt = DateTimeOffset.Now });
            StatusMessage = $"{saved.Name}: {saved.Items.Count} value(s) saved. Watch ▸ Compare with Snapshot shows what changed.";
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
    private void CompareSnapshot() => Dialogs?.ShowSnapshotCompare(new SnapshotCompareViewModel(Snapshots, () => CurrentSnapshot("Now"), device: new SnapshotDevice(this), dialogs: Dialogs));

    /// <summary>
    /// The connection as the comparison sees it: values of snapshot items that aren't in the watch list are read from
    /// the device, and values are written by the snapshot's portable ids, so a folder snapshot needs no watch rows.
    /// </summary>
    private sealed class SnapshotDevice(MainWindowViewModel owner) : ISnapshotDevice
    {
        public bool CanWrite => owner.IsConnected && !owner.IsReadOnly;

        public string EndpointUrl => owner.EndpointUrl;

        public async Task<IReadOnlyList<SnapshotItem>> ReadAsync(IReadOnlyList<SnapshotItem> items)
        {
            if (!owner.IsConnected || items.Count == 0)
            {
                return [];
            }

            return (await SnapshotReader.ReadAsync(owner._client, items)).Items;
        }

        public async Task<IReadOnlyList<string>> WriteAsync(IReadOnlyList<SnapshotDiffRow> rows)
        {
            // Checked again here: the session may have turned read-only while the question was shown.
            if (!CanWrite)
            {
                return [.. rows.Select(r => $"{r.Name}: read-only or disconnected")];
            }

            var watched = owner.WatchItems.GroupBy(w => w.PortableId).ToDictionary(g => g.Key, g => g.First().NodeId);
            var failures = new List<string>();
            foreach (var row in rows)
            {
                try
                {
                    var nodeId = watched.TryGetValue(row.NodeId, out var id) ? id : owner._client.ParsePortableId(row.NodeId);
                    await owner._client.WriteValueAsync(nodeId, row.Before);
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
