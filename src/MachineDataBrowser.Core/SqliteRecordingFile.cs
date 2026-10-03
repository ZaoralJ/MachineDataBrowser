using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace MachineDataBrowser.Core;

/// <summary>
/// Records into a SQLite file: each start of a recording is a row in <c>recordings</c>, its items in <c>items</c> and
/// every sample in <c>samples</c>, with numbers, text and (for arrays and structures) JSON in separate columns. Times are
/// ISO 8601 UTC text, which SQLite's date functions understand. Samples are written in batches, one transaction each,
/// in WAL mode, so the file stays readable (and followable) while it grows and a crash loses at most the last batch.
/// </summary>
internal sealed class SqliteRecordingFile : ILiveRecordingFile
{
    public const int SchemaVersion = 1;

    internal const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
        INSERT INTO schema_info (version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
        CREATE TABLE IF NOT EXISTS recordings (
            recording_id INTEGER PRIMARY KEY,
            name         TEXT NOT NULL,
            endpoint     TEXT,
            refresh_ms   REAL,
            started_utc  TEXT NOT NULL,
            stopped_utc  TEXT);
        CREATE TABLE IF NOT EXISTS items (
            item_id      INTEGER PRIMARY KEY,
            recording_id INTEGER NOT NULL REFERENCES recordings (recording_id),
            node_id      TEXT NOT NULL,
            name         TEXT NOT NULL,
            path         TEXT,
            UNIQUE (recording_id, node_id));
        CREATE TABLE IF NOT EXISTS samples (
            item_id      INTEGER NOT NULL REFERENCES items (item_id),
            received_utc TEXT NOT NULL,
            source_utc   TEXT,
            server_utc   TEXT,
            status       TEXT NOT NULL,
            status_code  INTEGER NOT NULL,
            value_num    REAL,
            value_text   TEXT,
            value_json   TEXT);
        CREATE INDEX IF NOT EXISTS samples_by_item_time ON samples (item_id, source_utc);
        CREATE VIEW IF NOT EXISTS sample_view AS
            SELECT s.rowid AS sample_id, r.recording_id, r.name AS recording, i.name, i.path, i.node_id,
                   s.received_utc, s.source_utc, s.server_utc, s.status, s.status_code, s.value_num, s.value_text, s.value_json
            FROM samples s JOIN items i ON i.item_id = s.item_id JOIN recordings r ON r.recording_id = i.recording_id;
        """;

    private readonly SqliteConnection _connection;
    private readonly long _recordingId;
    private readonly Dictionary<string, long> _itemIds = new(StringComparer.Ordinal);

    private SqliteRecordingFile(SqliteConnection connection, long recordingId)
    {
        _connection = connection;
        _recordingId = recordingId;
    }

    public static async Task<SqliteRecordingFile> OpenAsync(string path, LiveRecordingInfo info, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = Schema;
                await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO recordings (name, endpoint, refresh_ms, started_utc) VALUES ($name, $endpoint, $refresh, $started) RETURNING recording_id";
            insert.Parameters.AddWithValue("$name", info.Name);
            insert.Parameters.AddWithValue("$endpoint", (object?)info.Endpoint ?? DBNull.Value);
            insert.Parameters.AddWithValue("$refresh", info.SamplingIntervalMs);
            insert.Parameters.AddWithValue("$started", Utc(info.StartedAt));
            var id = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            return new SqliteRecordingFile(connection, id);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task WriteAsync(IReadOnlyList<LiveSample> batch, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO samples (item_id, received_utc, source_utc, server_utc, status, status_code, value_num, value_text, value_json)
            VALUES ($item, $received, $source, $server, $status, $code, $num, $text, $json)
            """;
        var item = insert.Parameters.Add("$item", SqliteType.Integer);
        var received = insert.Parameters.Add("$received", SqliteType.Text);
        var source = insert.Parameters.Add("$source", SqliteType.Text);
        var server = insert.Parameters.Add("$server", SqliteType.Text);
        var status = insert.Parameters.Add("$status", SqliteType.Text);
        var code = insert.Parameters.Add("$code", SqliteType.Integer);
        var num = insert.Parameters.Add("$num", SqliteType.Real);
        var text = insert.Parameters.Add("$text", SqliteType.Text);
        var json = insert.Parameters.Add("$json", SqliteType.Text);
        await insert.PrepareAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (recordedItem, sample, raw) in batch)
        {
            item.Value = await ItemIdAsync(recordedItem, transaction, cancellationToken).ConfigureAwait(false);
            received.Value = Utc(sample.ReceivedAt);
            source.Value = Utc(sample.SourceTimestamp);
            server.Value = Utc(sample.ServerTimestamp);
            status.Value = StatusText.Of(sample.Status);
            code.Value = (long)sample.Status.Code;
            num.Value = sample.Numeric is { } n && double.IsFinite(n) ? n : DBNull.Value;
            text.Value = sample.Value;
            json.Value = Structured(raw) ?? (object)DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(DateTimeOffset stoppedAt, CancellationToken cancellationToken)
    {
        await using var update = _connection.CreateCommand();
        update.CommandText = "UPDATE recordings SET stopped_utc = $stopped WHERE recording_id = $id";
        update.Parameters.AddWithValue("$stopped", Utc(stoppedAt));
        update.Parameters.AddWithValue("$id", _recordingId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    internal static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static object Utc(DateTime time) => time == DateTime.MinValue
        ? DBNull.Value
        : (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Arrays and structures as JSON; scalars are already in value_num / value_text.</summary>
    private static string? Structured(object? raw) => ValueJson.ToJson(raw) is JsonArray or JsonObject ? ValueJson.ToJson(raw)!.ToJsonString() : null;

    private async Task<long> ItemIdAsync(RecordedItem item, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (_itemIds.TryGetValue(item.PortableId, out var id))
        {
            return id;
        }

        await using var upsert = _connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO items (recording_id, node_id, name, path) VALUES ($recording, $node, $name, $path)
            ON CONFLICT (recording_id, node_id) DO UPDATE SET name = excluded.name
            RETURNING item_id
            """;
        upsert.Parameters.AddWithValue("$recording", _recordingId);
        upsert.Parameters.AddWithValue("$node", item.PortableId);
        upsert.Parameters.AddWithValue("$name", item.DisplayName);
        upsert.Parameters.AddWithValue("$path", (object?)item.Path ?? DBNull.Value);
        id = (long)(await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        _itemIds[item.PortableId] = id;
        return id;
    }
}
