using CommunityToolkit.Mvvm.ComponentModel;
using Opc.Ua;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class WatchItemViewModel(NodeId nodeId, string displayName) : ObservableObject
{
    public NodeId NodeId { get; } = nodeId;

    public string DisplayName { get; } = displayName;

    /// <summary>Where the item sits in the address space: its parent's path (display names joined by <c>/</c>).</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Header of the item's group when the watch list is grouped by path.</summary>
    public string Group => Path.Length == 0 ? "(no path)" : Path;

    /// <summary>Id as shown in the NodeId column and copied; set from the client's display form.</summary>
    public string NodeIdText { get; init; } = nodeId.ToString();

    public string PortableId { get; init; } = nodeId.ToString();

    public IAsyncDisposable? Monitor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshText), nameof(RefreshToolTip))]
    public partial int RefreshMs { get; set; } = 250;

    /// <summary>The tab's default refresh time; a row refreshing at another rate is marked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshText), nameof(RefreshToolTip))]
    public partial int DefaultRefreshMs { get; set; } = 250;

    /// <summary>Sampling, queue and deadband (OPC UA); <see cref="MonitoringOptions.Default"/> unless changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshText), nameof(RefreshToolTip))]
    public partial MonitoringOptions Monitoring { get; set; } = MonitoringOptions.Default;

    /// <summary>The settings worth mentioning: sampling at the refresh time is what the refresh time already says.</summary>
    private MonitoringOptions ShownMonitoring => Monitoring.SamplingIntervalMs is { } sampling && (int)sampling == RefreshMs
        ? Monitoring with { SamplingIntervalMs = null }
        : Monitoring;

    private bool IsCustomRefresh => RefreshMs != DefaultRefreshMs || !ShownMonitoring.IsDefault;

    /// <summary>The refresh time, with a ⚙ when it differs from the default or monitoring settings were changed.</summary>
    public string RefreshText => MainWindowViewModel.FormatRefresh(RefreshMs) + (IsCustomRefresh ? " ⚙" : string.Empty);

    public string RefreshToolTip
    {
        get
        {
            var parts = new List<string>(2);
            if (RefreshMs != DefaultRefreshMs)
            {
                parts.Add($"Refresh {MainWindowViewModel.FormatRefresh(RefreshMs)} (default {MainWindowViewModel.FormatRefresh(DefaultRefreshMs)})");
            }

            if (!ShownMonitoring.IsDefault)
            {
                parts.Add(ShownMonitoring.Describe());
            }

            return parts.Count == 0
                ? "Sampling/publishing interval. Right-click to change."
                : $"{string.Join(" · ", parts)}. Right-click ▸ Monitoring settings to change.";
        }
    }

    /// <summary>The device's value as text; filters, snapshots, recordings and exports use this one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueToolTip))]
    public partial string Value { get; private set; } = "…";

    /// <summary>Format, scaling and unit for <see cref="DisplayValue"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueToolTip))]
    public partial ValueDisplay Display { get; set; } = ValueDisplay.Default;

    /// <summary>The variable's engineering unit from the server (OPC UA), if it has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue))]
    public partial string? ServerUnit { get; set; }

    /// <summary>The value as shown in the Value column.</summary>
    public string DisplayValue => Display.Apply(RawValue, Value, ServerUnit);

    public string ValueToolTip => Display.IsDefault ? Value : $"{Value} as sent · shown {Display.Describe()}";

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceTimestamp { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBad { get; private set; }

    [ObservableProperty]
    public partial bool IsUncertain { get; private set; }

    [ObservableProperty]
    public partial string LastUpdateText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SinceText { get; private set; } = "never";

    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    [ObservableProperty]
    public partial int UpdateCount { get; private set; }

    /// <summary>A recording is capturing this item right now (recording, paused or scheduled).</summary>
    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    /// <summary>At least one open recording contains this item, so its recorded values can be shown.</summary>
    [ObservableProperty]
    public partial bool HasRecording { get; private set; }

    [ObservableProperty]
    public partial string RecordingToolTip { get; private set; } = string.Empty;

    /// <summary>Samples of this item in the newest recording that contains it (the one "Show recorded values" opens).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordedText))]
    public partial int RecordedSamples { get; private set; }

    public string RecordedText => HasRecording ? RecordedSamples.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;

    public void SetRecordedSamples(int count) => RecordedSamples = count;

    public void SetRecordings(IReadOnlyList<(string Name, RecordingState State)> recordings)
    {
        HasRecording = recordings.Count > 0;
        OnPropertyChanged(nameof(RecordedText));
        IsRecording = recordings.Any(r => r.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Scheduled);
        RecordingToolTip = string.Join('\n', recordings.Select(r => $"{r.Name}: {r.State}"));
    }

    public DateTimeOffset? LastUpdate { get; private set; }

    public object? RawValue { get; private set; }

    public void RefreshAge(DateTimeOffset now)
    {
        if (LastUpdate is not { } last)
        {
            SinceText = "never";
            IsStale = Monitor is not null;
            return;
        }

        var age = now - last;
        SinceText = FormatAge(age);
        IsStale = age > TimeSpan.FromMilliseconds(Math.Max(5000, RefreshMs * 5));
    }

    public static string FormatAge(TimeSpan age) => age.TotalSeconds switch
    {
        < 1 => "now",
        < 60 => $"{(int)age.TotalSeconds} s ago",
        < 3600 => $"{(int)age.TotalMinutes} min {age.Seconds} s ago",
        _ => $"{(int)age.TotalHours} h {age.Minutes} min ago",
    };

    public void Apply(ValueUpdate update) => Apply(update, DateTimeOffset.Now);

    public void Apply(ValueUpdate update, DateTimeOffset receivedAt)
    {
        LastUpdate = receivedAt;
        RawValue = update.Raw;
        UpdateCount++;
        LastUpdateText = Timestamps.Format(receivedAt);
        RefreshAge(receivedAt);
        Value = update.Value;
        Status = StatusText.Of(update.Status);
        IsBad = StatusCode.IsBad(update.Status);
        IsUncertain = StatusCode.IsUncertain(update.Status);
        SourceTimestamp = update.SourceTimestamp == DateTime.MinValue
            ? string.Empty
            : Timestamps.Format(update.SourceTimestamp);
    }
}
