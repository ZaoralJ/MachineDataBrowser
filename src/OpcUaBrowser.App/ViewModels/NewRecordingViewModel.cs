using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class NewRecordingViewModel(NewRecordingDraft draft) : ObservableObject
{
    private RecordingOptions? _editing;
    private RecordingState _editingState;

    /// <summary>Edits the settings of an existing recording (same form, applied instead of started).</summary>
    public static NewRecordingViewModel ForEdit(Recording recording, DateTimeOffset now)
    {
        var o = recording.Options;
        var vm = new NewRecordingViewModel(new NewRecordingDraft(o.Name, recording.Items.Count, (int)o.SamplingIntervalMs))
        {
            _editing = o,
            _editingState = recording.State,
            EditSummary = $"{recording.Items.Count} item(s) · {recording.State}. Changes apply immediately, also while recording.",
            LimitAge = o.MaxAge is not null,
            MaxAgeMinutes = o.MaxAge is { } age ? (decimal)Math.Max(1, Math.Round(age.TotalMinutes)) : 60,
            MaxPoints = o.MaxPointsPerItem,
            UseStartDelay = recording.State == RecordingState.Scheduled && o.ScheduledStart is not null,
            StartDelayMinutes = o.ScheduledStart is { } start ? (decimal)Math.Max(0, Math.Ceiling((start - now).TotalMinutes)) : 5,
            UseStopAfter = o.StopAfter is not null,
            StopAfterMinutes = o.StopAfter is { } after ? (decimal)Math.Max(1, Math.Round(after.TotalMinutes)) : 10,
            UseLiveFile = o.LiveFilePath is not null,
            LiveFilePath = o.LiveFilePath,
        };
        return vm;
    }

    public bool IsEdit => _editing is not null;

    public string Title => IsEdit ? $"Recording Settings — {draft.Name}" : "New Recording";

    public string ConfirmText => IsEdit ? "Apply" : "Start";

    /// <summary>Scheduling a start only makes sense before the recording runs.</summary>
    public bool CanSchedule => _editing is null || _editingState == RecordingState.Scheduled;

    private string EditSummary { get; init; } = string.Empty;

    public string Summary => IsEdit
        ? EditSummary
        : $"Records {draft.ItemCount} item(s): the rows selected in Watch, or all watched items when none is selected.";

    [ObservableProperty]
    public partial string Name { get; set; } = draft.Name;

    [ObservableProperty]
    public partial decimal? RefreshMs { get; set; } = draft.RefreshMs;

    [ObservableProperty]
    public partial bool LimitAge { get; set; }

    [ObservableProperty]
    public partial decimal? MaxAgeMinutes { get; set; } = 60;

    [ObservableProperty]
    public partial decimal? MaxPoints { get; set; } = 100_000;

    [ObservableProperty]
    public partial bool UseStartDelay { get; set; }

    [ObservableProperty]
    public partial decimal? StartDelayMinutes { get; set; } = 5;

    [ObservableProperty]
    public partial bool UseStopAfter { get; set; }

    [ObservableProperty]
    public partial decimal? StopAfterMinutes { get; set; } = 10;

    [ObservableProperty]
    public partial bool UseLiveFile { get; set; }

    [ObservableProperty]
    public partial string? LiveFilePath { get; set; }

    public RecordingOptions ToOptions(DateTimeOffset now) => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? draft.Name : Name.Trim(),
        SamplingIntervalMs = (double)(RefreshMs ?? draft.RefreshMs),
        MaxAge = LimitAge ? TimeSpan.FromMinutes((double)(MaxAgeMinutes ?? 60)) : null,
        MaxPointsPerItem = (int)(MaxPoints ?? 100_000),
        ScheduledStart = UseStartDelay ? now.AddMinutes((double)(StartDelayMinutes ?? 0)) : null,
        StopAfter = UseStopAfter ? TimeSpan.FromMinutes((double)(StopAfterMinutes ?? 10)) : null,
        LiveFilePath = UseLiveFile && !string.IsNullOrWhiteSpace(LiveFilePath) ? LiveFilePath : null,
        StopAt = _editing?.StopAt,
    };
}
