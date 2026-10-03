using System.Text;

namespace MachineDataBrowser.Core;

/// <summary>One recorded sample on its way to the live file, with the device's typed value for formats that keep it.</summary>
internal sealed record LiveSample(RecordedItem Item, HistorySample Sample, object? Raw);

/// <summary>
/// What a live file knows about the recording it belongs to. <paramref name="Retention"/> is read each time, so a
/// change in the recording's settings applies while it runs.
/// </summary>
internal sealed record LiveRecordingInfo(string Name, string? Endpoint, double SamplingIntervalMs, DateTimeOffset StartedAt, Func<TimeSpan?> Retention, TimeProvider Time);

/// <summary>
/// The file a recording streams every sample to while it runs (<see cref="RecordingOptions.LiveFilePath"/>): CSV, or
/// SQLite for <c>.db</c> / <c>.sqlite</c> / <c>.sqlite3</c>. Called from one writer task, in order.
/// </summary>
internal interface ILiveRecordingFile : IAsyncDisposable
{
    Task WriteAsync(IReadOnlyList<LiveSample> batch, CancellationToken cancellationToken);

    /// <summary>The recording stopped (or the file changed); the file is closed after this.</summary>
    Task CompleteAsync(DateTimeOffset stoppedAt, CancellationToken cancellationToken);
}

public static class RecordingFiles
{
    private static readonly string[] SqliteExtensions = [".db", ".sqlite", ".sqlite3"];

    /// <summary>True for files recorded into SQLite, chosen by extension; anything else is CSV.</summary>
    public static bool IsSqlite(string path) =>
        SqliteExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static async Task<ILiveRecordingFile> OpenAsync(string path, LiveRecordingInfo info, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return IsSqlite(path)
            ? await SqliteRecordingFile.OpenAsync(path, info, cancellationToken).ConfigureAwait(false)
            : await CsvRecordingFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The CSV live file: one line per sample, appended; the header is written to a new or empty file.</summary>
internal sealed class CsvRecordingFile : ILiveRecordingFile
{
    private readonly StreamWriter _writer;

    private CsvRecordingFile(StreamWriter writer) => _writer = writer;

    public static async Task<CsvRecordingFile> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        var writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
        if (writeHeader)
        {
            await writer.WriteLineAsync(Recording.CsvHeader.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return new CsvRecordingFile(writer);
    }

    public async Task WriteAsync(IReadOnlyList<LiveSample> batch, CancellationToken cancellationToken)
    {
        foreach (var sample in batch)
        {
            await _writer.WriteLineAsync(Recording.CsvLine(sample.Item, sample.Sample).AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CompleteAsync(DateTimeOffset stoppedAt, CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
