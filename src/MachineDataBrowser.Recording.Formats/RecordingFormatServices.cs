using MachineDataBrowser.Core;
using MachineDataBrowser.Recording.Csv;
using MachineDataBrowser.Recording.Json;
using MachineDataBrowser.Recording.Sqlite;

namespace MachineDataBrowser.Recording.Formats;

public sealed class RecordingTargetFactory : ILiveRecordingFileFactory
{
    private readonly CsvRecordingFileFactory _csv = new();
    private readonly SqliteRecordingFileFactory _sqlite = new();

    public Task<ILiveRecordingFile> OpenAsync(string path, LiveRecordingInfo info, CancellationToken cancellationToken) =>
        IsSqlite(path)
            ? _sqlite.OpenAsync(path, info, cancellationToken)
            : _csv.OpenAsync(path, info, cancellationToken);

    public static bool IsSqlite(string path) =>
        Path.GetExtension(path) is var extension
        && (extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase));
}

public static class RecordingFormatServices
{
    public static ILiveRecordingFileFactory LiveFileFactory { get; } = new RecordingTargetFactory();

    public static IRecordingFileReader OpenReader(string path) =>
        RecordingTargetFactory.IsSqlite(path)
            ? new SqliteRecordingFileReader(path)
            : new RecordingFileReader(path);

    public static Task ExportCsvAsync(Recording recording, string path, CancellationToken cancellationToken = default) =>
        CsvRecordingExporter.ExportAsync(recording.CreateSnapshot(), path, cancellationToken);

    public static Task ExportJsonAsync(Recording recording, string path, CancellationToken cancellationToken = default) =>
        JsonRecordingExporter.ExportAsync(recording.CreateSnapshot(), path, cancellationToken);
}
