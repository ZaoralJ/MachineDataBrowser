using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

/// <summary>The monitoring settings form: sampling interval, queue and deadband.</summary>
public sealed partial class MonitoringSettingsViewModel : ObservableObject
{
    public MonitoringSettingsViewModel(MonitoringOptions current, string target, int? refreshMs)
    {
        Target = target;
        RefreshText = refreshMs is { } ms ? MainWindowViewModel.FormatRefresh(ms) : "each item's refresh time";
        UseOwnSampling = current.SamplingIntervalMs is not null;
        SamplingIntervalMs = (decimal)(current.SamplingIntervalMs ?? refreshMs ?? 250);
        QueueSize = current.QueueSize;
        DiscardOldest = current.DiscardOldest;
        Deadband = current.Deadband;
        DeadbandValue = (decimal)current.DeadbandValue;
    }

    public string Target { get; }

    public string RefreshText { get; }

    public static IReadOnlyList<DeadbandKind> DeadbandKinds { get; } = [DeadbandKind.None, DeadbandKind.Absolute, DeadbandKind.Percent];

    /// <summary>Sample faster (or slower) than values are published; off = sample at the refresh time.</summary>
    [ObservableProperty]
    public partial bool UseOwnSampling { get; set; }

    [ObservableProperty]
    public partial decimal? SamplingIntervalMs { get; set; }

    [ObservableProperty]
    public partial decimal? QueueSize { get; set; }

    [ObservableProperty]
    public partial bool DiscardOldest { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeadband), nameof(DeadbandUnit))]
    public partial DeadbandKind Deadband { get; set; }

    [ObservableProperty]
    public partial decimal? DeadbandValue { get; set; }

    public bool HasDeadband => Deadband != DeadbandKind.None;

    public string DeadbandUnit => Deadband == DeadbandKind.Percent ? "% of the variable's EURange" : "in the value's units";

    public MonitoringOptions ToOptions() => new()
    {
        SamplingIntervalMs = UseOwnSampling ? (double)(SamplingIntervalMs ?? 0) : null,
        QueueSize = (uint)Math.Clamp(QueueSize ?? 1, 1, 100_000),
        DiscardOldest = DiscardOldest,
        Deadband = Deadband,
        DeadbandValue = Deadband == DeadbandKind.None ? 0 : (double)(DeadbandValue ?? 0),
    };
}
