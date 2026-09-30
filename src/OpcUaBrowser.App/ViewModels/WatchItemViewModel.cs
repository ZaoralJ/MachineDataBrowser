using CommunityToolkit.Mvvm.ComponentModel;
using Opc.Ua;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class WatchItemViewModel(NodeId nodeId, string displayName) : ObservableObject
{
    public NodeId NodeId { get; } = nodeId;

    public string DisplayName { get; } = displayName;

    /// <summary>Id as shown in the NodeId column and copied; set from the client's display form.</summary>
    public string NodeIdText { get; init; } = nodeId.ToString();

    public string PortableId { get; init; } = nodeId.ToString();

    public IAsyncDisposable? Monitor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshText))]
    public partial int RefreshMs { get; set; } = 250;

    public string RefreshText => MainWindowViewModel.FormatRefresh(RefreshMs);

    [ObservableProperty]
    public partial string Value { get; private set; } = "…";

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

    public void SetRecordings(IReadOnlyList<(string Name, RecordingState State)> recordings)
    {
        HasRecording = recordings.Count > 0;
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
        LastUpdateText = receivedAt.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        RefreshAge(receivedAt);
        Value = update.Value;
        Status = update.Status.SymbolicId ?? update.Status.ToString();
        IsBad = StatusCode.IsBad(update.Status);
        IsUncertain = StatusCode.IsUncertain(update.Status);
        SourceTimestamp = update.SourceTimestamp == DateTime.MinValue
            ? string.Empty
            : update.SourceTimestamp.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
    }
}
