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
}

/// <summary>Compares a saved snapshot with the live watch list or another snapshot.</summary>
public sealed partial class SnapshotCompareViewModel : ObservableObject, IDisposable
{
    public const string LiveChoice = "Live watch values";

    private readonly SnapshotStore _store;
    private readonly Func<Snapshot> _live;
    private readonly DispatcherTimer _timer;

    public SnapshotCompareViewModel(SnapshotStore store, Func<Snapshot> live, Snapshot? select = null)
    {
        _store = store;
        _live = live;
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
        Rows.ReplaceAll(ChangedOnly ? all.Where(r => r.IsChanged) : all);
        var changed = all.Count(r => r.Change == SnapshotChange.Changed);
        var added = all.Count(r => r.Change == SnapshotChange.Added);
        var removed = all.Count(r => r.Change == SnapshotChange.Removed);
        Summary = $"{before.Name} → {(IsLive ? "now" : after.Name)} · {changed} changed, {all.Count - changed - added - removed} same"
            + (added > 0 ? $", {added} only after" : string.Empty)
            + (removed > 0 ? $", {removed} only before" : string.Empty);
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
