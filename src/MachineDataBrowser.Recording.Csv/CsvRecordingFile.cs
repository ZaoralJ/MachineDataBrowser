using System.Globalization;
using System.Text;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Recording.Csv;

public sealed class CsvRecordingFileFactory : ILiveRecordingFileFactory
{
    public async Task<ILiveRecordingFile> OpenAsync(string path, LiveRecordingInfo info, CancellationToken cancellationToken)
    {
        CreateDirectory(path);
        var writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        var writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
        if (writeHeader)
        {
            await writer.WriteLineAsync(CsvRecordingExporter.Header.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return new CsvLiveRecordingFile(writer);
    }

    internal static void CreateDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

internal sealed class CsvLiveRecordingFile(StreamWriter writer) : ILiveRecordingFile
{
    public async Task WriteAsync(IReadOnlyList<LiveSample> batch, CancellationToken cancellationToken)
    {
        foreach (var sample in batch)
        {
            await writer.WriteLineAsync(CsvRecordingExporter.Line(sample.Item, sample.Sample).AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CompleteAsync(DateTimeOffset stoppedAt, CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => writer.DisposeAsync();
}

public static class CsvRecordingExporter
{
    public const string Header = "ReceivedAt,SourceTimestamp,ServerTimestamp,Name,NodeId,Value,Status";

    public static async Task ExportAsync(RecordingSnapshot snapshot, string path, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder(Header).Append('\n');
        foreach (var entry in snapshot.Items)
        {
            foreach (var sample in entry.Samples)
            {
                builder.Append(Line(entry.Item, sample)).Append('\n');
            }
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    internal static string Line(RecordedItem item, HistorySample sample) => string.Join(',',
        sample.ReceivedAt.ToString("O", CultureInfo.InvariantCulture),
        Timestamp(sample.SourceTimestamp),
        Timestamp(sample.ServerTimestamp),
        Csv(item.DisplayName),
        Csv(item.PortableId),
        Csv(sample.Value),
        MachineDataBrowser.Core.StatusText.Of(sample.Status));

    private static string Timestamp(DateTime value) =>
        value == DateTime.MinValue ? string.Empty : value.ToString("O", CultureInfo.InvariantCulture);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
