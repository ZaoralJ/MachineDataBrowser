namespace MachineDataBrowser.Core;

public enum RecordingState
{
    Created,
    Scheduled,
    Recording,
    Paused,
    Stopped,
    Closed,
}

public sealed record RecordingOptions
{
    public required string Name { get; init; }

    public double SamplingIntervalMs { get; init; } = 250;

    /// <summary>Samples older than this (relative to now) are dropped. Null = no age limit.</summary>
    public TimeSpan? MaxAge { get; init; }

    public int MaxPointsPerItem { get; init; } = 100_000;

    public DateTimeOffset? ScheduledStart { get; init; }

    public TimeSpan? StopAfter { get; init; }

    public DateTimeOffset? StopAt { get; init; }

    /// <summary>When set, every sample is also written to a live recording file.</summary>
    public string? LiveFilePath { get; init; }

    /// <summary>The endpoint recorded from, kept in SQLite files with the recording.</summary>
    public string? Endpoint { get; init; }

    /// <summary>SQLite live files only: samples older than this are deleted from the file. Null = keep everything.</summary>
    public TimeSpan? FileRetention { get; init; }
}

/// <summary>A recorded node; <paramref name="Path"/> is its parent path in the address space when known.</summary>
public sealed record RecordedItem(DeviceNodeId NodeId, string DisplayName, string PortableId, string? Path = null);

public sealed record HistorySample(
    DateTimeOffset ReceivedAt,
    DateTime SourceTimestamp,
    DateTime ServerTimestamp,
    string Value,
    double? Numeric,
    DeviceStatusCode Status,
    long Sequence = 0);

public sealed record RecordingSnapshot(
    string Name,
    double SamplingIntervalMs,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    IReadOnlyList<RecordingSnapshotItem> Items);

public sealed record RecordingSnapshotItem(RecordedItem Item, IReadOnlyList<HistorySample> Samples);

/// <summary>A sample queued for writing to a recording target.</summary>
public sealed record LiveSample(RecordedItem Item, HistorySample Sample, object? Raw);

/// <summary>Recording metadata supplied to a live recording target when it is opened.</summary>
public sealed record LiveRecordingInfo(
    string Name,
    string? Endpoint,
    double SamplingIntervalMs,
    DateTimeOffset StartedAt,
    Func<TimeSpan?> Retention,
    TimeProvider Time);

/// <summary>A live recording target. A single writer task calls these methods in order.</summary>
public interface ILiveRecordingFile : IAsyncDisposable
{
    Task WriteAsync(IReadOnlyList<LiveSample> batch, CancellationToken cancellationToken);

    Task CompleteAsync(DateTimeOffset stoppedAt, CancellationToken cancellationToken);
}

/// <summary>Opens the selected target implementation for a live recording.</summary>
public interface ILiveRecordingFileFactory
{
    Task<ILiveRecordingFile> OpenAsync(string path, LiveRecordingInfo info, CancellationToken cancellationToken);
}

public sealed record RecordingFileRow(
    DateTimeOffset ReceivedAt,
    string SourceTimestamp,
    string Name,
    string NodeId,
    string Value,
    string Status);

/// <summary>A recording file read in steps: each call returns what was added since the last one.</summary>
public interface IRecordingFileReader
{
    string Path { get; }

    Task<IReadOnlyList<RecordingFileRow>> ReadNewAsync(CancellationToken cancellationToken = default);
}
