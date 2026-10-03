using Microsoft.Data.Sqlite;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class RecordTests(OpcPlcFixture plc) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-record").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static async Task<long> ScalarAsync(string file, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Monitor_records_to_sqlite_with_names_paths_and_a_stop_time()
    {
        var file = Path.Combine(_dir, "line1.db");

        var (exit, _, error) = await RunAsync("monitor", plc.EndpointUrl, "/Objects/OpcPlc/Telemetry/Basic", "-R", "-r", "100", "-d", "2s", "--trust-all", "--record", file);

        Assert.Equal(0, exit);
        Assert.Contains("recorded", error, StringComparison.Ordinal);
        Assert.Equal(4, await ScalarAsync(file, "SELECT count(*) FROM items WHERE path = 'Objects/OpcPlc/Telemetry/Basic'"));
        Assert.True(await ScalarAsync(file, "SELECT count(*) FROM sample_view WHERE name = 'StepUp' AND value_num IS NOT NULL") >= 5);
        Assert.Equal(1, await ScalarAsync(file, "SELECT count(*) FROM recordings WHERE stopped_utc IS NOT NULL AND name = 'mdbrowser monitor'"));
    }

    [Fact]
    public async Task Run_records_a_session_and_csv_still_works()
    {
        var session = Path.Combine(_dir, "line1.mdbsession");
        var (created, _, _) = await RunAsync("session", "create", session, plc.EndpointUrl, "/Objects/OpcPlc/Telemetry/Basic/StepUp", "-r", "100", "--trust-all");
        Assert.Equal(0, created);

        var db = Path.Combine(_dir, "shift.sqlite");
        var (exit, _, _) = await RunAsync("run", session, "-d", "1500ms", "--record", db);
        Assert.Equal(0, exit);
        Assert.Equal(1, await ScalarAsync(db, "SELECT count(*) FROM recordings WHERE name = 'line1'"));

        var csv = Path.Combine(_dir, "shift.csv");
        (exit, _, _) = await RunAsync("run", session, "-d", "1500ms", "--record", csv);
        Assert.Equal(0, exit);
        Assert.StartsWith("ReceivedAt,", await File.ReadAllTextAsync(csv, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
