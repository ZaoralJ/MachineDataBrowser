using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

public sealed record HistoryRow(DateTimeOffset ReceivedAt, string Name, string NodeId, string Value, string Status, string SourceTime)
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

    public static RecordingViewerViewModel ForRecording(Recording recording)
    {
        long last = 0;
        return new RecordingViewerViewModel(
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
                    p.Sample.SourceTimestamp == DateTime.MinValue ? string.Empty : p.Sample.SourceTimestamp.ToString("O", CultureInfo.InvariantCulture)))];
            },
            () => $"{recording.State} · {recording.TotalSamples} samples");
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
                    return [.. reader.ReadNew().Select(r => new HistoryRow(r.ReceivedAt, r.Name, r.NodeId, r.Value, r.Status, r.SourceTimestamp))];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    partial void OnSelectedItemChanged(string value) => Rebuild();

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

        RowsAppended?.Invoke(this, EventArgs.Empty);
    }
}
