using System.Globalization;

namespace MachineDataBrowser.Core;

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

/// <summary>Compares two snapshots item by item (matched by portable NodeId), as the app and the MCP server show it.</summary>
public static class SnapshotDiff
{
    /// <summary>
    /// Rows for every item of <paramref name="before"/>, plus items only in <paramref name="after"/> unless
    /// <paramref name="before"/> is partial (then the rest of <paramref name="after"/> is not of interest).
    /// </summary>
    public static IReadOnlyList<SnapshotDiffRow> Compare(Snapshot before, Snapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var later = after.Items.GroupBy(i => i.NodeId).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<SnapshotDiffRow>();
        foreach (var item in before.Items)
        {
            if (later.Remove(item.NodeId, out var now))
            {
                // Equal numbers count as the same even when written differently (1.50 vs 1.5): values come from a watch row
                // (the driver's formatting) or a one-off read.
                var same = (item.Value == now.Value || item.Numeric is { } x && now.Numeric is { } y && x.Equals(y)) && item.Status == now.Status;
                rows.Add(new SnapshotDiffRow(item.Name, item.NodeId, item.Value, item.Status, now.Value, now.Status,
                    same ? SnapshotChange.Same : SnapshotChange.Changed,
                    !same && item.Numeric is { } a && now.Numeric is { } b ? b - a : null));
            }
            else
            {
                rows.Add(new SnapshotDiffRow(item.Name, item.NodeId, item.Value, item.Status, string.Empty, string.Empty, SnapshotChange.Removed, null));
            }
        }

        rows.AddRange(after.Items.Where(i => !before.Partial && later.ContainsKey(i.NodeId))
            .Select(i => new SnapshotDiffRow(i.Name, i.NodeId, string.Empty, string.Empty, i.Value, i.Status, SnapshotChange.Added, null)));
        return rows;
    }
}
