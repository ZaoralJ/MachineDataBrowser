using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MachineDataBrowser.Core;

/// <summary>
/// Reads the samples of a SQLite recording file in steps (by row id), so the viewer can follow a file that is still
/// being recorded. With several recordings in the file, names are prefixed with the recording's name.
/// </summary>
public sealed class SqliteRecordingFileReader(string path) : IRecordingFileReader
{
    private const int MaxRowsPerRead = 50_000;
    private long _lastRowId;

    public string Path { get; } = path;

    public async Task<IReadOnlyList<RecordingFileRow>> ReadNewAsync(CancellationToken cancellationToken = default)
    {
        // A recording that is about to start may not have created the file or its tables yet: nothing new so far.
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.Ordinal))
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<RecordingFileRow>> ReadAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM recordings";
        var several = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 1;

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT s.rowid, s.received_utc, s.source_utc, i.name, i.node_id, s.value_text, s.status, r.name
            FROM samples s JOIN items i ON i.item_id = s.item_id JOIN recordings r ON r.recording_id = i.recording_id
            WHERE s.rowid > $after ORDER BY s.rowid LIMIT $limit
            """;
        select.Parameters.AddWithValue("$after", _lastRowId);
        select.Parameters.AddWithValue("$limit", MaxRowsPerRead);
        var rows = new List<RecordingFileRow>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _lastRowId = reader.GetInt64(0);
            var received = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var name = several ? $"{reader.GetString(7)} › {reader.GetString(3)}" : reader.GetString(3);
            rows.Add(new RecordingFileRow(
                received,
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(2),
                name,
                reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(5),
                reader.GetString(6)));
        }

        return rows;
    }
}
