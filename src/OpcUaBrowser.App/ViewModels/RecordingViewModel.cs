using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class RecordingViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherTimer _timer;

    public RecordingViewModel(Recording recording)
    {
        Recording = recording;
        recording.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        recording.Faulted += (_, ex) => Dispatcher.UIThread.Post(() => LastError = ex.Message);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
    }

    public Recording Recording { get; }

    public string Name => Recording.Options.Name;

    public int ItemCount => Recording.Items.Count;

    public string Details
    {
        get
        {
            var o = Recording.Options;
            var parts = new List<string> { $"{ItemCount} item(s)", MainWindowViewModel.FormatRefresh((int)o.SamplingIntervalMs) };
            if (o.MaxAge is { } age) parts.Add($"keep {age:g}");
            if (Recording.PlannedStopAt is { } stop) parts.Add($"stops {stop.ToLocalTime():HH:mm:ss}");
            else if (o.ScheduledStart is { } start && Recording.State == RecordingState.Scheduled) parts.Add($"starts {start.ToLocalTime():HH:mm:ss}");
            if (o.LiveFilePath is { } file) parts.Add($"→ {Path.GetFileName(file)}");
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    public partial RecordingState State { get; private set; }

    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = "0:00:00";

    [ObservableProperty]
    public partial long Samples { get; private set; }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    public bool CanStart => State is RecordingState.Created or RecordingState.Stopped;

    public bool CanPause => State == RecordingState.Recording;

    public bool CanResume => State == RecordingState.Paused;

    public bool CanStop => State is RecordingState.Recording or RecordingState.Paused or RecordingState.Scheduled;

    public void Refresh()
    {
        State = Recording.State;
        ElapsedText = Recording.Elapsed.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
        Samples = Recording.TotalSamples;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(Details));
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await Recording.DisposeAsync();
    }
}
