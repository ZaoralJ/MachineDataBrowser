using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.Services;

namespace MachineDataBrowser.App.ViewModels;

public enum SnapshotChange
{
    Same,
    Changed,
    Added,
    Removed,
}

/// <summary>One item in a comparison: its value before (baseline) and after (live or a later snapshot).</summary>
public sealed record SnapshotDiffRow(
    string Name,
    string NodeId,
    string Before,
    string BeforeStatus,
    string After,
    string AfterStatus,
    SnapshotChange Change,
    double? Delta)
{
    public string ChangeText => Change switch
    {
        SnapshotChange.Changed when Delta is { } d => d.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture),
        SnapshotChange.Changed => "changed",
        SnapshotChange.Added => "only after",
        SnapshotChange.Removed => "only before",
        _ => string.Empty,
    };

    public bool IsChanged => Change != SnapshotChange.Same;

    /// <summary>A value worth writing back: it differs now, and the snapshot holds a usable (Good) value.</summary>
    public bool CanRestore => Change == SnapshotChange.Changed && BeforeStatus.StartsWith("Good", StringComparison.Ordinal)
        && Before != After;
}

/// <summary>Writes snapshot values back to the device; the result lists the rows that failed, with the reason.</summary>
public interface ISnapshotWriter
{
    /// <summary>False in read-only sessions and while disconnected.</summary>
    bool CanWrite { get; }

    /// <summary>The endpoint the values would go to, to warn when a snapshot was taken elsewhere.</summary>
    string EndpointUrl { get; }

    Task<IReadOnlyList<string>> WriteAsync(IReadOnlyList<SnapshotDiffRow> rows);
}

/// <summary>Compares a saved snapshot with the live watch list or another snapshot.</summary>
public sealed partial class SnapshotCompareViewModel : ObservableObject, IDisposable
{
    public const string LiveChoice = "Live watch values";

    private readonly SnapshotStore _store;
    private readonly Func<Snapshot> _live;
    private readonly ISnapshotWriter? _writer;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<SnapshotDiffRow> _pending = [];

    public SnapshotCompareViewModel(SnapshotStore store, Func<Snapshot> live, Snapshot? select = null, ISnapshotWriter? writer = null)
    {
        _store = store;
        _live = live;
        _writer = writer;
        Reload(select);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (IsLive)
            {
                Compare();
            }
        });
        _timer.Start();
    }

    public ObservableCollection<Snapshot> Snapshots { get; } = [];

    /// <summary>What the baseline is compared with: <see cref="LiveChoice"/> or a snapshot.</summary>
    public ObservableCollection<object> Targets { get; } = [];

    public BulkObservableCollection<SnapshotDiffRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial Snapshot? Baseline { get; set; }

    [ObservableProperty]
    public partial object? Target { get; set; }

    [ObservableProperty]
    public partial bool ChangedOnly { get; set; } = true;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>
    /// NodeIds of the rows selected in the grid, kept by id because the live comparison replaces the rows every second;
    /// restoring uses them, or every shown row when none is selected.
    /// </summary>
    public HashSet<string> SelectedIds { get; } = new(StringComparer.Ordinal);

    /// <summary>True while <see cref="Rows"/> is being replaced, so the grid's selection reset isn't taken as the user's.</summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>After <see cref="Rows"/> were replaced; the view selects <see cref="SelectedIds"/> again.</summary>
    public event EventHandler? RowsRefreshed;

    /// <summary>The question shown before writing, with what would change; null when nothing is pending.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    public partial string? PendingWrite { get; private set; }

    public bool IsConfirming => PendingWrite is not null;

    /// <summary>The outcome of the last write: how many values were written and which failed.</summary>
    [ObservableProperty]
    public partial string WriteResult { get; private set; } = string.Empty;

    public bool CanRestoreAny => _writer is not null;

    public bool IsLive => Target is string;

    partial void OnBaselineChanged(Snapshot? value) => Compare();

    partial void OnTargetChanged(object? value) => Compare();

    partial void OnChangedOnlyChanged(bool value) => Compare();

    private void Reload(Snapshot? select)
    {
        Snapshots.Clear();
        foreach (var snapshot in _store.List())
        {
            Snapshots.Add(snapshot);
        }

        Targets.Clear();
        Targets.Add(LiveChoice);
        foreach (var snapshot in Snapshots)
        {
            Targets.Add(snapshot);
        }

        Target = LiveChoice;
        Baseline = select is null ? Snapshots.FirstOrDefault() : Snapshots.FirstOrDefault(s => s.TakenAt == select.TakenAt) ?? Snapshots.FirstOrDefault();
    }

    public void Compare()
    {
        if (Baseline is not { } before)
        {
            Rows.ReplaceAll([]);
            Summary = "No snapshots yet: Watch ▸ Take Snapshot saves the current values.";
            return;
        }

        var after = Target as Snapshot ?? _live();
        var all = Diff(before, after);
        IsRefreshing = true;
        try
        {
            Rows.ReplaceAll(ChangedOnly ? all.Where(r => r.IsChanged) : all);
        }
        finally
        {
            IsRefreshing = false;
        }

        RowsRefreshed?.Invoke(this, EventArgs.Empty);
        var changed = all.Count(r => r.Change == SnapshotChange.Changed);
        var added = all.Count(r => r.Change == SnapshotChange.Added);
        var removed = all.Count(r => r.Change == SnapshotChange.Removed);
        Summary = $"{before.Name} → {(IsLive ? "now" : after.Name)} · {changed} changed, {all.Count - changed - added - removed} same"
            + (added > 0 ? $", {added} only after" : string.Empty)
            + (removed > 0 ? $", {removed} only before" : string.Empty);
        RestoreCommand.NotifyCanExecuteChanged();
    }

    public static IReadOnlyList<SnapshotDiffRow> Diff(Snapshot before, Snapshot after)
    {
        var later = after.Items.GroupBy(i => i.NodeId).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<SnapshotDiffRow>();
        foreach (var item in before.Items)
        {
            if (later.Remove(item.NodeId, out var now))
            {
                var same = item.Value == now.Value && item.Status == now.Status;
                rows.Add(new SnapshotDiffRow(item.Name, item.NodeId, item.Value, item.Status, now.Value, now.Status,
                    same ? SnapshotChange.Same : SnapshotChange.Changed,
                    !same && item.Numeric is { } a && now.Numeric is { } b ? b - a : null));
            }
            else
            {
                rows.Add(new SnapshotDiffRow(item.Name, item.NodeId, item.Value, item.Status, string.Empty, string.Empty, SnapshotChange.Removed, null));
            }
        }

        rows.AddRange(after.Items.Where(i => later.ContainsKey(i.NodeId))
            .Select(i => new SnapshotDiffRow(i.Name, i.NodeId, string.Empty, string.Empty, i.Value, i.Status, SnapshotChange.Added, null)));
        return rows;
    }

    private bool CanDelete() => Baseline is not null;

    /// <summary>Rows to write back: the selection (or every shown row) that changed and had a Good value before.</summary>
    public IReadOnlyList<SnapshotDiffRow> RestoreCandidates()
    {
        var rows = SelectedIds.Count > 0 ? Rows.Where(r => SelectedIds.Contains(r.NodeId)) : Rows;
        return [.. rows.Where(r => r.CanRestore)];
    }

    // Only against live values: writing makes sense for the machine as it is now, not for another snapshot.
    private bool CanRestore() => _writer is { CanWrite: true } && IsLive && !IsConfirming && Rows.Any(r => r.CanRestore);

    /// <summary>Asks first: shows what would be written (now → snapshot value) and waits for <see cref="ConfirmRestoreCommand"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        _pending = RestoreCandidates();
        if (_pending.Count == 0)
        {
            WriteResult = "Nothing to write: select changed rows whose snapshot value was Good.";
            return;
        }

        var lines = _pending.Take(10).Select(r => $"{r.Name}: {r.After} → {r.Before}").ToList();
        if (_pending.Count > lines.Count)
        {
            lines.Add($"… and {_pending.Count - lines.Count} more");
        }

        var elsewhere = Baseline is { EndpointUrl.Length: > 0 } b && _writer is { } w
            && !string.Equals(CliCommand.WithoutPassword(b.EndpointUrl), CliCommand.WithoutPassword(w.EndpointUrl.Trim()), StringComparison.OrdinalIgnoreCase)
            ? $"\nThe snapshot was taken on {CliCommand.WithoutPassword(b.EndpointUrl)}, not on this connection."
            : string.Empty;
        PendingWrite = $"Write {_pending.Count} value(s) from the snapshot to the device?{elsewhere}\n{string.Join("\n", lines)}";
        RestoreCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ConfirmRestoreAsync()
    {
        var rows = _pending;
        CancelRestore();
        if (_writer is not { CanWrite: true } writer || rows.Count == 0)
        {
            WriteResult = "Not written: the connection is read-only or disconnected.";
            return;
        }

        var failures = await writer.WriteAsync(rows);
        WriteResult = failures.Count == 0
            ? $"Wrote {rows.Count} value(s) from {Baseline?.Name}."
            : $"Wrote {rows.Count - failures.Count} of {rows.Count} value(s). Failed: {string.Join("; ", failures)}";
        Compare();
    }

    [RelayCommand]
    private void CancelRestore()
    {
        _pending = [];
        PendingWrite = null;
        RestoreCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Baseline is { } snapshot)
        {
            _store.Delete(snapshot);
            Reload(null);
        }
    }

    public void Dispose() => _timer.Stop();
}
