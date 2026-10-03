using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MachineDataBrowser.Core;

public sealed record RecordingSummary(long Id, string Name, string? Endpoint, double? RefreshMs, string StartedUtc, string? StoppedUtc, long Items, long Samples, string? FirstSampleUtc, string? LastSampleUtc);

public sealed record RecordedItemSummary(long RecordingId, string Recording, string Name, string? Path, string NodeId, long Samples, string? FirstUtc, string? LastUtc,
    double? Min, double? Max, double? Average, string? LastValue, string? LastStatus);

public sealed record RecordedSample(string Recording, string Name, string? SourceUtc, string ReceivedUtc, string Status, double? Number, string? Text, string? Json);

public sealed record SampleBucket(string Recording, string Name, string StartUtc, long Count, double? Min, double? Max, double? Average, long NotGood);

/// <summary>Which samples to read from a recording file; times are ISO 8601 (UTC when no offset is given).</summary>
public sealed record SampleQuery
{
    public long? RecordingId { get; init; }

    /// <summary>Item names (as in <c>items.name</c>) or node ids; empty = every item.</summary>
    public IReadOnlyList<string> Items { get; init; } = [];

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    /// <summary>When set, samples are aggregated per time bucket of this many seconds instead of returned one by one.</summary>
    public int? BucketSeconds { get; init; }

    public int MaxRows { get; init; } = 1000;
}

/// <summary>
/// Read-only queries over SQLite recording files (see <see cref="SqliteRecordingFile"/>): what is in them, per item
/// statistics, and samples raw or aggregated per time bucket. The file is opened read-only; it may be recording.
/// </summary>
public static class SqliteRecordingQuery
{
    private const string Time = "COALESCE(s.source_utc, s.received_utc)";

    public static async Task<IReadOnlyList<RecordingSummary>> RecordingsAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, """
            SELECT r.recording_id, r.name, r.endpoint, r.refresh_ms, r.started_utc, r.stopped_utc,
                   (SELECT count(*) FROM items i WHERE i.recording_id = r.recording_id),
                   (SELECT count(*) FROM samples s JOIN items i ON i.item_id = s.item_id WHERE i.recording_id = r.recording_id),
                   (SELECT min(s.received_utc) FROM samples s JOIN items i ON i.item_id = s.item_id WHERE i.recording_id = r.recording_id),
                   (SELECT max(s.received_utc) FROM samples s JOIN items i ON i.item_id = s.item_id WHERE i.recording_id = r.recording_id)
            FROM recordings r ORDER BY r.recording_id
            """, [], r => new RecordingSummary(r.GetInt64(0), r.GetString(1), Str(r, 2), Num(r, 3), r.GetString(4), Str(r, 5), r.GetInt64(6), r.GetInt64(7), Str(r, 8), Str(r, 9)),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<RecordedItemSummary>> ItemsAsync(string path, long? recordingId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, $"""
            SELECT i.recording_id, r.name, i.name, i.path, i.node_id, count(s.rowid), min({Time}), max({Time}),
                   min(s.value_num), max(s.value_num), avg(s.value_num),
                   (SELECT value_text FROM samples l WHERE l.item_id = i.item_id ORDER BY l.rowid DESC LIMIT 1),
                   (SELECT status FROM samples l WHERE l.item_id = i.item_id ORDER BY l.rowid DESC LIMIT 1)
            FROM items i JOIN recordings r ON r.recording_id = i.recording_id LEFT JOIN samples s ON s.item_id = i.item_id
            WHERE $recording IS NULL OR i.recording_id = $recording
            GROUP BY i.item_id ORDER BY i.recording_id, i.item_id
            """, [("$recording", recordingId)],
            r => new RecordedItemSummary(r.GetInt64(0), r.GetString(1), r.GetString(2), Str(r, 3), r.GetString(4), r.GetInt64(5), Str(r, 6), Str(r, 7),
                Num(r, 8), Num(r, 9), Num(r, 10), Str(r, 11), Str(r, 12)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Samples oldest first, at most <see cref="SampleQuery.MaxRows"/>; <c>Truncated</c> says when there were more.</summary>
    public static async Task<(IReadOnlyList<RecordedSample> Rows, bool Truncated)> SamplesAsync(string path, SampleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var (where, parameters) = Filter(query);
        var rows = await ReadAsync(connection, $"""
            SELECT r.name, i.name, s.source_utc, s.received_utc, s.status, s.value_num, s.value_text, s.value_json
            FROM samples s JOIN items i ON i.item_id = s.item_id JOIN recordings r ON r.recording_id = i.recording_id
            WHERE {where} ORDER BY {Time}, s.rowid LIMIT $limit
            """, [.. parameters, ("$limit", query.MaxRows + 1)],
            r => new RecordedSample(r.GetString(0), r.GetString(1), Str(r, 2), r.GetString(3), r.GetString(4), Num(r, 5), Str(r, 6), Str(r, 7)),
            cancellationToken).ConfigureAwait(false);
        return rows.Count > query.MaxRows ? (rows.Take(query.MaxRows).ToList(), true) : (rows, false);
    }

    /// <summary>Samples aggregated per item and time bucket, oldest first, at most <see cref="SampleQuery.MaxRows"/> buckets.</summary>
    public static async Task<(IReadOnlyList<SampleBucket> Buckets, bool Truncated)> BucketsAsync(string path, SampleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var seconds = Math.Max(1, query.BucketSeconds ?? 60);
        await using var connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var (where, parameters) = Filter(query);
        var bucket = $"(unixepoch({Time}) / $seconds) * $seconds";
        var rows = await ReadAsync(connection, $"""
            SELECT r.name, i.name, strftime('%Y-%m-%dT%H:%M:%SZ', {bucket}, 'unixepoch'), count(*),
                   min(s.value_num), max(s.value_num), avg(s.value_num), sum(s.status_code <> 0)
            FROM samples s JOIN items i ON i.item_id = s.item_id JOIN recordings r ON r.recording_id = i.recording_id
            WHERE {where} GROUP BY i.item_id, {bucket} ORDER BY {bucket}, i.item_id LIMIT $limit
            """, [.. parameters, ("$seconds", seconds), ("$limit", query.MaxRows + 1)],
            r => new SampleBucket(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), Num(r, 4), Num(r, 5), Num(r, 6), r.GetInt64(7)),
            cancellationToken).ConfigureAwait(false);
        return rows.Count > query.MaxRows ? (rows.Take(query.MaxRows).ToList(), true) : (rows, false);
    }

    private static (string Where, List<(string, object?)> Parameters) Filter(SampleQuery query)
    {
        var where = new List<string> { "1 = 1" };
        var parameters = new List<(string, object?)>();
        if (query.RecordingId is { } id)
        {
            where.Add("i.recording_id = $recording");
            parameters.Add(("$recording", id));
        }

        if (query.Items.Count > 0)
        {
            var names = query.Items.Select((item, n) => $"$item{n}").ToList();
            where.Add($"(i.name IN ({string.Join(", ", names)}) OR i.node_id IN ({string.Join(", ", names)}))");
            parameters.AddRange(query.Items.Select((item, n) => ($"$item{n}", (object?)item)));
        }

        if (query.From is { } from)
        {
            where.Add($"{Time} >= $from");
            parameters.Add(("$from", SqliteRecordingFile.Utc(from)));
        }

        if (query.To is { } to)
        {
            where.Add($"{Time} <= $to");
            parameters.Add(("$to", SqliteRecordingFile.Utc(to)));
        }

        return (string.Join(" AND ", where), parameters);
    }

    private static async Task<SqliteConnection> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Recording file '{path}' not found.", path);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<List<T>> ReadAsync<T>(SqliteConnection connection, string sql, IEnumerable<(string Name, object? Value)> parameters, Func<SqliteDataReader, T> map, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static string? Str(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static double? Num(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetDouble(column);

    /// <summary>ISO 8601 text as the tools receive it; times without an offset are UTC.</summary>
    public static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
