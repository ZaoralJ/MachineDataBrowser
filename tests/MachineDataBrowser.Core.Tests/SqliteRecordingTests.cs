using Microsoft.Data.Sqlite;
using Opc.Ua;
using MachineDataBrowser.Core.Ua;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class SqliteRecordingTests(OpcPlcFixture plc) : IAsyncLifetime
{
    private static readonly NodeId ProductName = VariableIds.Server_ServerStatus_BuildInfo_ProductName;
    private static readonly NodeId CurrentTime = VariableIds.Server_ServerStatus_CurrentTime;

    private readonly OpcUaClient _client = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("sqlite-recordings").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private RecordedItem Item(NodeId id, string name, string? path = null) => new(id, name, _client.ToPortableId(id), path);

    private static ValueUpdate Update(NodeId id, string text, double? numeric = null, object? raw = null, StatusCode? status = null) =>
        new(id, text, status ?? StatusCodes.Good, new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc), DateTime.UtcNow, numeric, raw);

    private async Task<Recording> RecordAsync(string file, string name, params RecordedItem[] items)
    {
        var recording = new Recording(_client, new RecordingOptions { Name = name, LiveFilePath = file, Endpoint = "opc.tcp://plc:4840", SamplingIntervalMs = 500 }, items);
        await recording.StartAsync(Ct);
        return recording;
    }

    private static async Task<List<T>> QueryAsync<T>(string file, string sql, Func<SqliteDataReader, T> row)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(row(reader));
        }

        return rows;
    }

    [Fact]
    public async Task Samples_are_stored_typed_with_their_recording_and_items()
    {
        var file = Path.Combine(_dir, "line1.db");
        await using (var recording = await RecordAsync(file, "Line 1", Item(ProductName, "Product", "Objects/Server"), Item(CurrentTime, "Clock")))
        {
            recording.Append(Update(ProductName, "42.5", 42.5));
            recording.Append(Update(ProductName, "[1, 2, 3]", raw: new[] { 1, 2, 3 }));
            recording.Append(Update(ProductName, "-", status: StatusCodes.BadCommunicationError));
            await recording.StopAsync(Ct);
        }

        var recordings = await QueryAsync(file, "SELECT name, endpoint, refresh_ms, started_utc, stopped_utc FROM recordings", r => (r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetString(3), r.IsDBNull(4)));
        var (name, endpoint, refresh, started, notStopped) = Assert.Single(recordings);
        Assert.Equal(("Line 1", "opc.tcp://plc:4840", 500d), (name, endpoint, refresh));
        Assert.EndsWith("Z", started, StringComparison.Ordinal);
        Assert.False(notStopped);

        var samples = await QueryAsync(file, """
            SELECT name, path, source_utc, status, status_code, value_num, value_text, value_json FROM sample_view
            WHERE name = 'Product' AND source_utc = '2026-10-03T12:00:00.000Z' ORDER BY sample_id
            """, r => (Path: r.IsDBNull(1) ? null : r.GetString(1), Status: r.GetString(3), Code: r.GetInt64(4),
                Num: r.IsDBNull(5) ? (double?)null : r.GetDouble(5), Text: r.GetString(6), Json: r.IsDBNull(7) ? null : r.GetString(7)));
        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.Equal("Objects/Server", s.Path));
        Assert.Equal((42.5, "42.5", (string?)null), (samples[0].Num, samples[0].Text, samples[0].Json));
        Assert.Equal("[1,2,3]", samples[1].Json);
        Assert.Equal(("BadCommunicationError", (long)StatusCodes.BadCommunicationError), (samples[2].Status, samples[2].Code));

        Assert.Equal(["ok"], await QueryAsync(file, "PRAGMA integrity_check", r => r.GetString(0)));
    }

    [Fact]
    public async Task Each_start_adds_a_recording_to_the_file_and_the_reader_follows_it()
    {
        var file = Path.Combine(_dir, "shift.sqlite");
        var reader = RecordingFileReader.Open(file);
        Assert.IsType<SqliteRecordingFileReader>(reader);
        Assert.Empty(await reader.ReadNewAsync(Ct));   // not created yet: nothing to read, no error

        await using (var first = await RecordAsync(file, "Morning", Item(ProductName, "Product")))
        {
            first.Append(Update(ProductName, "a"));
            await WaitUntilAsync(async () => (await reader.ReadNewAsync(Ct)).Any(r => r.Value == "a"));   // followed while recording
            first.Append(Update(ProductName, "b"));
            await first.StopAsync(Ct);
        }

        Assert.Contains(await reader.ReadNewAsync(Ct), r => r.Value == "b" && r.Name == "Product");

        await using (var second = await RecordAsync(file, "Evening", Item(ProductName, "Product")))
        {
            second.Append(Update(ProductName, "c"));
            await second.StopAsync(Ct);
        }

        Assert.Equal(["Morning", "Evening"], await QueryAsync(file, "SELECT name FROM recordings ORDER BY recording_id", r => r.GetString(0)));
        var all = await RecordingFileReader.Open(file).ReadNewAsync(Ct);
        Assert.Contains(all, r => r.Value == "c" && r.Name == "Evening › Product");   // several recordings: names say which
    }

    [Fact]
    public async Task Other_extensions_still_record_csv()
    {
        var file = Path.Combine(_dir, "line1.csv");
        await using (var recording = await RecordAsync(file, "csv", Item(ProductName, "Product")))
        {
            recording.Append(Update(ProductName, "x"));
            await recording.StopAsync(Ct);
        }

        Assert.StartsWith("ReceivedAt,SourceTimestamp", await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
        Assert.IsType<RecordingFileReader>(RecordingFileReader.Open(file));
        Assert.Contains(await RecordingFileReader.Open(file).ReadNewAsync(Ct), r => r.Value == "x");
    }

    [Fact]
    public async Task Retention_deletes_old_samples_and_emptied_recordings_but_keeps_the_running_one()
    {
        var file = Path.Combine(_dir, "always-on.db");
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var item = Item(ProductName, "Product");

        await using (var old = new Recording(_client, new RecordingOptions { Name = "Old", LiveFilePath = file }, [item], time))
        {
            await old.StartAsync(Ct);
            old.Append(Update(ProductName, "old"));
            await old.StopAsync(Ct);
        }

        var options = new RecordingOptions { Name = "Current", LiveFilePath = file, FileRetention = TimeSpan.FromHours(1) };
        await using (var current = new Recording(_client, options, [item], time))
        {
            await current.StartAsync(Ct);
            current.Append(Update(ProductName, "early"));

            // The file is opened on the writer task, which also runs the first retention check; let that happen before the
            // clock jumps, or the first check is stamped "2 h later" and the next one isn't due yet.
            await WaitUntilAsync(async () => (await QueryAsync(file, "SELECT count(*) FROM samples WHERE value_text = 'early'", r => r.GetInt64(0)))[0] == 1);
            time.Advance(TimeSpan.FromHours(2));
            current.Append(Update(ProductName, "new"));   // the next write runs retention: older than 1 h is gone
            await WaitUntilAsync(async () => (await QueryAsync(file, "SELECT count(*) FROM samples WHERE value_text = 'new'", r => r.GetInt64(0)))[0] == 1
                && (await QueryAsync(file, "SELECT count(*) FROM samples WHERE value_text IN ('old', 'early')", r => r.GetInt64(0)))[0] == 0);
            await current.StopAsync(Ct);
        }

        Assert.Equal(["Current"], await QueryAsync(file, "SELECT name FROM recordings", r => r.GetString(0)));
        Assert.Equal(["ok"], await QueryAsync(file, "PRAGMA integrity_check", r => r.GetString(0)));
        Assert.Equal([2L], await QueryAsync(file, "PRAGMA auto_vacuum", r => r.GetInt64(0)));   // incremental: freed space goes back to disk
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            await Task.Delay(50, Ct);
        }
    }
}
