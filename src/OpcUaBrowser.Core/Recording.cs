using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Opc.Ua;

namespace OpcUaBrowser.Core;

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

    /// <summary>When set, every sample is also appended to this CSV file while recording.</summary>
    public string? LiveFilePath { get; init; }
}

public sealed record RecordedItem(NodeId NodeId, string DisplayName, string PortableId);

public sealed record HistorySample(
    DateTimeOffset ReceivedAt,
    DateTime SourceTimestamp,
    DateTime ServerTimestamp,
    string Value,
    double? Numeric,
    StatusCode Status,
    long Sequence = 0);

/// <summary>
/// A named capture of value history for a set of nodes on one <see cref="IDeviceClient"/> connection.
/// History is kept in bounded in-memory buffers (per item) and can optionally be streamed to a CSV file.
/// </summary>
public sealed class Recording : IAsyncDisposable
{
    private const string CsvHeader = "ReceivedAt,SourceTimestamp,ServerTimestamp,Name,NodeId,Value,Status";

    private readonly IDeviceClient _client;
    private readonly TimeProvider _time;
    private readonly Dictionary<NodeId, RecordedItem> _itemsById;
    private readonly Dictionary<NodeId, Queue<HistorySample>> _buffers;
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private List<IAsyncDisposable> _monitors = [];
    private ITimer? _startTimer;
    private ITimer? _stopTimer;
    private Channel<string>? _liveChannel;
    private Task? _liveWriter;
    private long _totalSamples;
    private long _sequence;

    public Recording(IDeviceClient client, RecordingOptions options, IReadOnlyList<RecordedItem> items, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("A recording needs at least one item.", nameof(items));
        }

        _client = client;
        _time = timeProvider ?? TimeProvider.System;
        Options = options;
        Items = items;
        _itemsById = items.ToDictionary(i => i.NodeId);
        _buffers = items.ToDictionary(i => i.NodeId, _ => new Queue<HistorySample>());
    }

    public event EventHandler<RecordingState>? StateChanged;

    /// <summary>Raised when the recording fails in the background (e.g. auto-start, live file I/O).</summary>
    public event EventHandler<Exception>? Faulted;

    public RecordingOptions Options { get; }

    public IReadOnlyList<RecordedItem> Items { get; }

    public RecordingState State { get; private set; } = RecordingState.Created;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? StoppedAt { get; private set; }

    public long TotalSamples => Interlocked.Read(ref _totalSamples);

    public TimeSpan Elapsed => StartedAt is not { } start
        ? TimeSpan.Zero
        : (StoppedAt ?? _time.GetUtcNow()) - start;

    public DateTimeOffset? PlannedStopAt => StartedAt is not { } start
        ? null
        : Min(Options.StopAt, Options.StopAfter is { } after ? start + after : null);

    /// <summary>Starts now, or arms the scheduled start if <see cref="RecordingOptions.ScheduledStart"/> is in the future.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not (RecordingState.Created or RecordingState.Stopped))
            {
                throw new InvalidOperationException($"Cannot start a recording that is {State}.");
            }

            var now = _time.GetUtcNow();
            if (Options.ScheduledStart is { } at && at > now)
            {
                _startTimer = _time.CreateTimer(_ => _ = BackgroundAsync(StartScheduledAsync), null, at - now, Timeout.InfiniteTimeSpan);
                SetState(RecordingState.Scheduled);
                return;
            }

            await BeginAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (State != RecordingState.Recording)
            {
                throw new InvalidOperationException($"Cannot pause a recording that is {State}.");
            }

            SetState(RecordingState.Paused);
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (State != RecordingState.Paused)
            {
                throw new InvalidOperationException($"Cannot resume a recording that is {State}.");
            }

            SetState(RecordingState.Recording);
        }
    }

    /// <summary>Stops capturing and releases the monitored items. Captured history stays available.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Clears captured history and the sample counter; the recording keeps its current state.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            foreach (var buffer in _buffers.Values)
            {
                buffer.Clear();
            }

            Interlocked.Exchange(ref _totalSamples, 0);
            if (State is RecordingState.Recording or RecordingState.Paused)
            {
                StartedAt = _time.GetUtcNow();
            }
        }
    }

    public IReadOnlyList<HistorySample> GetHistory(NodeId nodeId)
    {
        lock (_sync)
        {
            PruneByAge(_time.GetUtcNow());
            return _buffers.TryGetValue(nodeId, out var buffer) ? [.. buffer] : [];
        }
    }

    /// <summary>All buffered samples with a sequence number greater than <paramref name="afterSequence"/>, oldest first.</summary>
    public IReadOnlyList<(RecordedItem Item, HistorySample Sample)> GetSamplesAfter(long afterSequence)
    {
        return [.. Snapshot()
            .SelectMany(p => p.Samples.Where(s => s.Sequence > afterSequence).Select(s => (p.Item, s)))
            .OrderBy(p => p.s.Sequence)];
    }

    public int GetSampleCount(NodeId nodeId)
    {
        lock (_sync)
        {
            return _buffers.TryGetValue(nodeId, out var buffer) ? buffer.Count : 0;
        }
    }

    public async Task ExportCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder(CsvHeader).Append('\n');
        foreach (var (item, samples) in Snapshot())
        {
            foreach (var sample in samples)
            {
                builder.Append(CsvLine(item, sample)).Append('\n');
            }
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("name", Options.Name);
        if (StartedAt is { } started)
        {
            json.WriteString("startedAt", started);
        }

        if (StoppedAt is { } stopped)
        {
            json.WriteString("stoppedAt", stopped);
        }

        json.WriteNumber("samplingIntervalMs", Options.SamplingIntervalMs);
        json.WriteStartArray("items");
        foreach (var (item, samples) in Snapshot())
        {
            json.WriteStartObject();
            json.WriteString("nodeId", item.PortableId);
            json.WriteString("displayName", item.DisplayName);
            json.WriteStartArray("samples");
            foreach (var sample in samples)
            {
                json.WriteStartObject();
                json.WriteString("receivedAt", sample.ReceivedAt);
                json.WriteString("sourceTimestamp", sample.SourceTimestamp);
                json.WriteString("value", sample.Value);
                if (sample.Numeric is { } numeric)
                {
                    if (double.IsFinite(numeric))
                    {
                        json.WriteNumber("numeric", numeric);
                    }
                    else
                    {
                        json.WriteString("numeric", numeric.ToString(CultureInfo.InvariantCulture));
                    }
                }

                json.WriteString("status", StatusText(sample.Status));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(RecordingState.Closed);
        }
        finally
        {
            _lifecycle.Release();
        }

        _lifecycle.Dispose();
    }

    internal void Append(ValueUpdate update)
    {
        if (!_itemsById.TryGetValue(update.NodeId, out var item))
        {
            return;
        }

        var sample = new HistorySample(_time.GetUtcNow(), update.SourceTimestamp, update.ServerTimestamp, update.Value, update.Numeric, update.Status, Interlocked.Increment(ref _sequence));
        lock (_sync)
        {
            if (State != RecordingState.Recording)
            {
                return;
            }

            var buffer = _buffers[update.NodeId];
            buffer.Enqueue(sample);
            while (buffer.Count > Options.MaxPointsPerItem)
            {
                buffer.Dequeue();
            }

            PruneByAge(sample.ReceivedAt);
            Interlocked.Increment(ref _totalSamples);
        }

        _liveChannel?.Writer.TryWrite(CsvLine(item, sample));
    }

    private async Task StartScheduledAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == RecordingState.Scheduled)
            {
                await BeginAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task BeginAsync(CancellationToken cancellationToken)
    {
        DisposeTimers();
        StartedAt = _time.GetUtcNow();
        StoppedAt = null;

        if (Options.LiveFilePath is { } livePath)
        {
            OpenLiveFile(livePath);
        }

        lock (_sync)
        {
            SetState(RecordingState.Recording);
        }

        try
        {
            var results = await _client.MonitorManyAsync(
                [.. Items.Select(i => i.NodeId)],
                Append,
                Options.SamplingIntervalMs,
                cancellationToken).ConfigureAwait(false);
            _monitors = [.. results.Select(r => r.Handle).OfType<IAsyncDisposable>()];
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (PlannedStopAt is { } stopAt)
        {
            var due = stopAt - _time.GetUtcNow();
            _stopTimer = _time.CreateTimer(_ => _ = BackgroundAsync(() => StopAsync()), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        DisposeTimers();
        if (State is RecordingState.Stopped or RecordingState.Closed or RecordingState.Created)
        {
            return;
        }

        var wasCapturing = State is RecordingState.Recording or RecordingState.Paused;
        lock (_sync)
        {
            SetState(RecordingState.Stopped);
        }

        if (wasCapturing)
        {
            StoppedAt = _time.GetUtcNow();
        }

        var monitors = _monitors;
        _monitors = [];
        try
        {
            await DeviceClient.StopMonitoringAsync(monitors, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex)
        {
            Faulted?.Invoke(this, ex);
        }

        await CloseLiveFileAsync().ConfigureAwait(false);
    }

    private void OpenLiveFile(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _liveChannel = channel;
        _liveWriter = Task.Run(async () =>
        {
            try
            {
                await using var writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
                if (writeHeader)
                {
                    await writer.WriteLineAsync(CsvHeader).ConfigureAwait(false);
                }

                while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var line))
                    {
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }

                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Faulted?.Invoke(this, ex);
            }
        });
    }

    private async Task CloseLiveFileAsync()
    {
        _liveChannel?.Writer.TryComplete();
        if (_liveWriter is { } writer)
        {
            await writer.ConfigureAwait(false);
        }

        _liveChannel = null;
        _liveWriter = null;
    }

    private void PruneByAge(DateTimeOffset now)
    {
        if (Options.MaxAge is not { } maxAge)
        {
            return;
        }

        var cutoff = now - maxAge;
        foreach (var buffer in _buffers.Values)
        {
            while (buffer.Count > 0 && buffer.Peek().ReceivedAt < cutoff)
            {
                buffer.Dequeue();
            }
        }
    }

    private List<(RecordedItem Item, HistorySample[] Samples)> Snapshot()
    {
        lock (_sync)
        {
            PruneByAge(_time.GetUtcNow());
            return [.. Items.Select(i => (i, _buffers[i.NodeId].ToArray()))];
        }
    }

    private void SetState(RecordingState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void DisposeTimers()
    {
        _startTimer?.Dispose();
        _startTimer = null;
        _stopTimer?.Dispose();
        _stopTimer = null;
    }

    private async Task BackgroundAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServiceResultException or InvalidOperationException or ObjectDisposedException)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    private static string CsvLine(RecordedItem item, HistorySample sample) => string.Join(',',
        sample.ReceivedAt.ToString("O", CultureInfo.InvariantCulture),
        Timestamp(sample.SourceTimestamp),
        Timestamp(sample.ServerTimestamp),
        Csv(item.DisplayName),
        Csv(item.PortableId),
        Csv(sample.Value),
        StatusText(sample.Status));

    private static string Timestamp(DateTime value) =>
        value == DateTime.MinValue ? string.Empty : value.ToString("O", CultureInfo.InvariantCulture);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string StatusText(StatusCode status) => status.SymbolicId ?? status.ToString();

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;
}
