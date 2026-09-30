using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.Core;

using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public sealed record HistoryRow(DateTimeOffset ReceivedAt, string Name, string NodeId, string Value, string Status, string SourceTime, double? Numeric = null)
{
    public string TimeText => ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

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
    private readonly List<HistoryRow> _all = [];
    private readonly DispatcherTimer _timer;

    private RecordingViewerViewModel(string title, Func<IReadOnlyList<HistoryRow>> pull, Func<string> status)
    {
        Title = title;
        _pull = pull;
        _status = status;
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
                    p.Sample.Status.SymbolicId ?? p.Sample.Status.ToString(),
                    p.Sample.SourceTimestamp == DateTime.MinValue ? string.Empty : p.Sample.SourceTimestamp.ToString("O", CultureInfo.InvariantCulture),
                    p.Sample.Numeric))];
            },
            () => $"{recording.State} · {recording.TotalSamples} samples");
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

    public static RecordingViewerViewModel ForFile(string path)
    {
        var reader = new RecordingFileReader(path);
        string? error = null;
        return new RecordingViewerViewModel(
            Path.GetFileName(path),
            () =>
            {
                try
                {
                    error = null;
                    return [.. reader.ReadNew().Select(r => new HistoryRow(r.ReceivedAt, r.Name, r.NodeId, r.Value, r.Status, r.SourceTimestamp, ParseNumeric(r.Value)))];
                }
                catch (Exception ex) when (AppErrors.IsRecoverable(ex))
                {
                    error = ex.Message;
                    return [];
                }
            },
            () => error ?? $"File · last change {File.GetLastWriteTime(path):HH:mm:ss}");
    }

    public event EventHandler? RowsAppended;

    public string Title { get; }

    public ObservableCollection<HistoryRow> Rows { get; } = [];

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
    [NotifyPropertyChangedFor(nameof(HasChart))]
    public partial IReadOnlyList<TrendPoint> ChartPoints { get; private set; } = [];

    public bool HasChart => ChartPoints.Count > 1;

    private static double? ParseNumeric(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
        : bool.TryParse(value, out var b) ? (b ? 1 : 0)
        : null;

    private void UpdateChart()
    {
        if (SelectedItem == AllItems)
        {
            ChartPoints = [];
            return;
        }

        var points = new List<TrendPoint>();
        foreach (var row in _all)
        {
            if (row.Name == SelectedItem && row.Numeric is { } value && double.IsFinite(value))
            {
                points.Add(new TrendPoint(row.ReceivedAt, value, row));
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
        if (fresh.Count == 0)
        {
            return;
        }

        _all.AddRange(fresh);
        if (_all.Count > MaxRows)
        {
            _all.RemoveRange(0, _all.Count - MaxRows);
        }

        TotalRows = _all.Count;
        foreach (var name in fresh.Select(r => r.Name).Distinct().Where(n => !ItemNames.Contains(n)))
        {
            ItemNames.Add(name);
        }

        if (Rows.Count + fresh.Count > MaxRows)
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

    private bool Matches(HistoryRow row) => SelectedItem == AllItems || row.Name == SelectedItem;

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var row in _all.Where(Matches))
        {
            Rows.Add(row);
        }

        UpdateChart();
        RowsAppended?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One chart sample; <paramref name="Row"/> links it back to its grid row for selection.</summary>
public sealed record TrendPoint(DateTimeOffset Time, double Value, HistoryRow? Row = null);
