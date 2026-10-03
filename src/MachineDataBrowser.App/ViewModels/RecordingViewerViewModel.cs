using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MachineDataBrowser.Core;

using MachineDataBrowser.App.Services;

namespace MachineDataBrowser.App.ViewModels;

public sealed record HistoryRow(DateTimeOffset ReceivedAt, string Name, string NodeId, string Value, string Status, string SourceTime, double? Numeric = null)
{
    /// <summary>Local date and time (<see cref="Timestamps"/>); the UTC offset is in <see cref="TimeToolTip"/>.</summary>
    public string TimeText => Timestamps.Format(ReceivedAt);

    public string TimeToolTip => Timestamps.ToolTip(ReceivedAt);

    /// <summary>Source timestamp in the same format as <see cref="TimeText"/> (stored as ISO 8601 UTC).</summary>
    public string SourceTimeText => ParsedSourceTime is { } t ? Timestamps.Format(t) : SourceTime;

    public string SourceTimeToolTip => ParsedSourceTime is { } t ? Timestamps.ToolTip(t) : SourceTime;

    private DateTimeOffset? ParsedSourceTime =>
        DateTimeOffset.TryParse(SourceTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public bool IsBad => Status.StartsWith("Bad", StringComparison.Ordinal);

    public bool IsUncertain => Status.StartsWith("Uncertain", StringComparison.Ordinal);
}

/// <summary>Live view of a running <see cref="Recording"/> or a recording CSV file (tailed while it grows).</summary>
public sealed partial class RecordingViewerViewModel : ObservableObject, IDisposable
{
    public const string AllItems = "All items";
    public const int MaxRows = 20_000;

    private readonly Func<IReadOnlyList<HistoryRow>> _pull;
    private readonly Func<string> _status;
    private readonly Func<HistoryLimits>? _limits;
    private readonly List<HistoryRow> _all = [];
    private readonly DispatcherTimer _timer;

    private RecordingViewerViewModel(string title, Func<IReadOnlyList<HistoryRow>> pull, Func<string> status, Func<HistoryLimits>? limits = null)
    {
        Title = title;
        _pull = pull;
        _status = status;
        _limits = limits;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Poll());
        Poll();
        _timer.Start();
    }

    public static RecordingViewerViewModel ForRecording(Recording recording, string? item = null)
    {
        long last = 0;
        var viewer = new RecordingViewerViewModel(
            recording.Options.Name,
            () =>
            {
                var samples = recording.GetSamplesAfter(last);
                if (samples.Count > 0)
                {
                    last = samples[^1].Sample.Sequence;
                }

                return [.. samples.Select(p => new HistoryRow(
                    p.Sample.ReceivedAt, p.Item.DisplayName, p.Item.PortableId, p.Sample.Value,
                    Core.StatusText.Of(p.Sample.Status),
                    p.Sample.SourceTimestamp == DateTime.MinValue ? string.Empty : p.Sample.SourceTimestamp.ToString("O", CultureInfo.InvariantCulture),
                    p.Sample.Numeric))];
            },
            () => $"{recording.State} · {recording.KeptSamples:N0} kept of {recording.TotalSamples:N0} received · max {recording.Options.MaxPointsPerItem:N0} per item",
            () => new HistoryLimits(recording.Options.MaxPointsPerItem, recording.Options.MaxAge, recording.TotalSamples == 0));
        foreach (var name in recording.Items.Select(i => i.DisplayName).Distinct().Where(n => !viewer.ItemNames.Contains(n)))
        {
            viewer.ItemNames.Add(name);
        }

        if (item is not null)
        {
            viewer.SelectedItem = item;
        }

        return viewer;
    }

    /// <summary>A fixed set of rows (e.g. history read from the server); nothing new arrives.</summary>
    public static RecordingViewerViewModel ForRows(string title, IReadOnlyList<HistoryRow> rows, string status)
    {
        var pending = rows;
        var viewer = new RecordingViewerViewModel(
            title,
            () =>
            {
                var once = pending;
                pending = [];
                return once;
            },
            () => status);
        viewer.Follow = false;
        return viewer;
    }

    public static RecordingViewerViewModel ForFile(string path)
    {
        // The file is read on the pool (opening a large file used to block the window); polls take what has been read.
        var reader = RecordingFileReader.Open(path);
        var pending = new System.Collections.Concurrent.ConcurrentQueue<HistoryRow>();
        Task? reading = null;
        string? error = null;
        return new RecordingViewerViewModel(
            Path.GetFileName(path),
            () =>
            {
                if (reading is null or { IsCompleted: true })
                {
                    reading = Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var r in await reader.ReadNewAsync().ConfigureAwait(false))
                            {
                                pending.Enqueue(new HistoryRow(r.ReceivedAt, r.Name, r.NodeId, r.Value, r.Status, r.SourceTimestamp, ParseNumeric(r.Value)));
                            }

                            error = null;
                        }
                        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
                        {
                            error = ex.Message;
                        }
                    });
                }

                var rows = new List<HistoryRow>();
                while (pending.TryDequeue(out var row))
                {
                    rows.Add(row);
                }

                return rows;
            },
            () => error ?? $"File · last change {Timestamps.FormatSeconds(File.GetLastWriteTime(path))}");
    }

    public event EventHandler? RowsAppended;

    public string Title { get; }

    public BulkObservableCollection<HistoryRow> Rows { get; } = [];

    public ObservableCollection<string> ItemNames { get; } = [AllItems];

    [ObservableProperty]
    public partial string SelectedItem { get; set; } = AllItems;

    [ObservableProperty]
    public partial bool Follow { get; set; } = true;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int TotalRows { get; private set; }

    /// <summary>Numeric samples of the selected item for the trend chart; empty for "All items" or non-numeric values.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChart), nameof(IsChartVisible))]
    public partial IReadOnlyList<TrendPoint> ChartPoints { get; private set; } = [];

    public bool HasChart => ChartPoints.Count > 1;

    /// <summary>User toggle for the chart; it only appears when the selected item has numeric samples.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChartVisible))]
    public partial bool ShowChart { get; set; } = true;

    public bool IsChartVisible => HasChart && ShowChart;

    private static double? ParseNumeric(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
        : bool.TryParse(value, out var b) ? (b ? 1 : 0)
        : null;

    /// <summary>Every numeric item gets a line: the selected item alone, or one line per item for "All items".</summary>
    private void UpdateChart()
    {
        var points = new List<TrendPoint>();
        foreach (var row in _all)
        {
            if (Matches(row) && (row.Numeric ?? ParseNumeric(row.Value)) is { } value && double.IsFinite(value))
            {
                points.Add(new TrendPoint(row.ReceivedAt, value, row, row.Name));
            }
        }

        ChartPoints = points;

        // Keep the highlight on the selected sample after the point list is rebuilt.
        var selected = SelectedRow;
        HighlightedPoint = selected is null ? null : points.FirstOrDefault(p => ReferenceEquals(p.Row, selected));
    }

    partial void OnSelectedItemChanged(string value) => Rebuild();

    /// <summary>Row selected in the grid; its sample is highlighted in the chart.</summary>
    [ObservableProperty]
    public partial HistoryRow? SelectedRow { get; set; }

    /// <summary>Sample highlighted in the chart; clicking the chart selects the matching row.</summary>
    [ObservableProperty]
    public partial TrendPoint? HighlightedPoint { get; set; }

    partial void OnSelectedRowChanged(HistoryRow? value)
    {
        var point = value is null ? null : ChartPoints.FirstOrDefault(p => ReferenceEquals(p.Row, value));
        if (!ReferenceEquals(point, HighlightedPoint) && (point is not null || value is null || !HasChart))
        {
            HighlightedPoint = point;
        }
    }

    partial void OnHighlightedPointChanged(TrendPoint? value)
    {
        if (value?.Row is { } row && !ReferenceEquals(row, SelectedRow))
        {
            if (Follow)
            {
                Follow = false; // picking a sample would otherwise be scrolled away by the next update
            }

            SelectedRow = row;
        }
    }

    public void Poll()
    {
        var fresh = _pull();
        StatusText = _status();
        var trimmed = ApplyLimits();
        if (fresh.Count == 0)
        {
            if (trimmed)
            {
                TotalRows = _all.Count;
                Rebuild();
            }

            return;
        }

        _all.AddRange(fresh);
        trimmed |= ApplyLimits();
        if (_all.Count > MaxRows)
        {
            _all.RemoveRange(0, _all.Count - MaxRows);
            trimmed = true;
        }

        TotalRows = _all.Count;
        foreach (var name in fresh.Select(r => r.Name).Distinct().Where(n => !ItemNames.Contains(n)))
        {
            ItemNames.Add(name);
        }

        // Large batches (opening a file, catching up) are one Reset; a trickle of new samples is appended row by row.
        if (trimmed || Rows.Count + fresh.Count > MaxRows || fresh.Count > 200)
        {
            Rebuild();
        }
        else
        {
            foreach (var row in fresh.Where(Matches))
            {
                Rows.Add(row);
            }
        }

        UpdateChart();
        RowsAppended?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer.Stop();

    /// <summary>
    /// Keeps the view in line with the recording's history: at most MaxPointsPerItem rows per item, none older than
    /// MaxAge, and nothing after a reset. Returns true when rows were dropped.
    /// </summary>
    private bool ApplyLimits()
    {
        if (_limits?.Invoke() is not { } limits || _all.Count == 0)
        {
            return false;
        }

        if (limits.IsEmpty)
        {
            _all.Clear();
            return true;
        }

        var before = _all.Count;
        if (limits.MaxAge is { } maxAge)
        {
            var cutoff = DateTimeOffset.UtcNow - maxAge;
            _all.RemoveAll(r => r.ReceivedAt < cutoff);
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in _all)
        {
            counts[row.NodeId] = counts.GetValueOrDefault(row.NodeId) + 1;
        }

        if (counts.Values.Any(c => c > limits.MaxPerItem))
        {
            // Rows are in arrival order, so the first (count - max) rows of an item are its oldest.
            var excess = counts.ToDictionary(p => p.Key, p => p.Value - limits.MaxPerItem, StringComparer.Ordinal);
            var kept = new List<HistoryRow>(_all.Count);
            foreach (var row in _all)
            {
                if (excess[row.NodeId] > 0)
                {
                    excess[row.NodeId]--;
                    continue;
                }

                kept.Add(row);
            }

            _all.Clear();
            _all.AddRange(kept);
        }

        return _all.Count != before;
    }

    private bool Matches(HistoryRow row) => SelectedItem == AllItems || row.Name == SelectedItem;

    private void Rebuild()
    {
        // One Reset instead of up to MaxRows Add events (each re-laid out the grid) when the item filter changes.
        Rows.ReplaceAll(_all.Where(Matches));
        UpdateChart();
        RowsAppended?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One chart sample; <paramref name="Row"/> links it back to its grid row for selection.</summary>
/// <summary>History limits of a live recording that the viewer mirrors.</summary>
public readonly record struct HistoryLimits(int MaxPerItem, TimeSpan? MaxAge, bool IsEmpty);

public sealed record TrendPoint(DateTimeOffset Time, double Value, HistoryRow? Row = null, string Series = "");
