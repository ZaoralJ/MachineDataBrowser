using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// The SQLite recording files an agent may read: the files given with <c>--recording</c>, and the SQLite files in the
/// folders given with <c>--recordings-dir</c> (listed on every call, so new recordings appear). Nothing else.
/// </summary>
internal sealed class RecordingLibrary(IReadOnlyList<string> files, IReadOnlyList<string> folders)
{
    public bool IsEmpty => files.Count == 0 && folders.Count == 0;

    public IReadOnlyList<string> Files() =>
    [
        .. files.Select(Path.GetFullPath),
        .. folders.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(Path.GetFullPath(d)).Where(RecordingFiles.IsSqlite).Order(StringComparer.Ordinal)),
    ];

    /// <summary>An allowed file by full path or by file name; anything else is refused.</summary>
    public string Resolve(string? file)
    {
        var all = Files().Distinct(StringComparer.Ordinal).ToList();
        if (string.IsNullOrWhiteSpace(file))
        {
            return all.Count == 1 ? all[0] : throw new McpException($"Several recording files are available; pass one of: {string.Join(", ", all.Select(Path.GetFileName))}.");
        }

        var byPath = all.FirstOrDefault(f => string.Equals(f, Path.GetFullPath(file), StringComparison.Ordinal));
        var byName = all.Where(f => string.Equals(Path.GetFileName(f), file, StringComparison.Ordinal)).ToList();
        return byPath ?? (byName.Count == 1 ? byName[0] : throw new McpException(
            byName.Count > 1 ? $"Several files are called '{file}'; pass the full path." : $"'{file}' is not one of the recording files this server may read: {string.Join(", ", all.Select(Path.GetFileName))}."));
    }
}

/// <summary>Read-only tools over SQLite recordings: what was recorded, per item statistics, and samples raw or per time bucket.</summary>
internal sealed class RecordingTools(RecordingLibrary library)
{
    public const int MaxRows = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "list_recordings", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The SQLite recording files this server may read and the recordings in each: name, endpoint, start and stop, items, samples, time range.")]
    public Task<string> ListRecordingsAsync(CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var files = new List<object>();
        foreach (var file in library.Files())
        {
            try
            {
                files.Add(new { File = Path.GetFileName(file), Path = file, Recordings = await SqliteRecordingQuery.RecordingsAsync(file, cancellationToken).ConfigureAwait(false) });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                files.Add(new { File = Path.GetFileName(file), Path = file, Error = $"not a recording file: {ex.Message}" });
            }
        }

        return files;
    });

    [McpServerTool(Name = "recording_items", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The items of a recording file with per-item statistics: samples, first and last time, min/max/average of numbers, last value and status.")]
    public Task<string> RecordingItemsAsync(
        [Description("File name or path from list_recordings; may be omitted when only one file is available")] string? file = null,
        [Description("Recording id from list_recordings; default every recording in the file")] long? recording = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
        await SqliteRecordingQuery.ItemsAsync(library.Resolve(file), recording, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "recording_samples", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Recorded samples of items over a time range, oldest first. With bucketSeconds the samples are aggregated per item and "
        + "time bucket (count, min, max, average, samples not Good), which is how to look at long ranges. Times are ISO 8601, UTC when no offset is given.")]
    public Task<string> RecordingSamplesAsync(
        [Description("Item names or node ids from recording_items; empty = every item")] string[]? items = null,
        [Description("File name or path from list_recordings; may be omitted when only one file is available")] string? file = null,
        [Description("Recording id from list_recordings; default every recording in the file")] long? recording = null,
        [Description("Start of the range, ISO 8601, e.g. 2026-10-03T08:00:00Z")] string? from = null,
        [Description("End of the range, ISO 8601")] string? to = null,
        [Description("Aggregate per bucket of this many seconds (e.g. 60 = per minute); omit for raw samples")] int? bucketSeconds = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var query = new SampleQuery
        {
            RecordingId = recording,
            Items = items ?? [],
            From = Time(from, nameof(from)),
            To = Time(to, nameof(to)),
            BucketSeconds = bucketSeconds is { } s ? Math.Max(1, s) : null,
            MaxRows = MaxRows,
        };
        var path = library.Resolve(file);
        if (query.BucketSeconds is not null)
        {
            var (buckets, truncated) = await SqliteRecordingQuery.BucketsAsync(path, query, cancellationToken).ConfigureAwait(false);
            return new { Buckets = buckets, Truncated = truncated };
        }

        var (rows, more) = await SqliteRecordingQuery.SamplesAsync(path, query, cancellationToken).ConfigureAwait(false);
        return (object)new { Samples = rows, Truncated = more, Hint = more ? "More samples than returned: narrow the range or use bucketSeconds." : null };
    });

    private static DateTimeOffset? Time(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return SqliteRecordingQuery.ParseTime(text);
        }
        catch (FormatException)
        {
            throw new McpException($"'{name}' must be an ISO 8601 time like 2026-10-03T08:00:00Z, not '{text}'.");
        }
    }

    private static async Task<string> Guard(Func<Task<object>> tool)
    {
        try
        {
            return JsonSerializer.Serialize(await tool().ConfigureAwait(false), Json);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or FileNotFoundException or IOException)
        {
            throw new McpException(ex.Message);
        }
    }
}
