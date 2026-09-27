using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class NewRecordingViewModel(NewRecordingDraft draft) : ObservableObject
{
    public string Summary => $"Records {draft.ItemCount} item(s): the rows selected in Watch, or all watched items when none is selected.";

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
    };
}
