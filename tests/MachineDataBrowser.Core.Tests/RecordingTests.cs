using Microsoft.Extensions.Time.Testing;
using Opc.Ua;
using Xunit;
using MachineDataBrowser.Core.Ua;

namespace MachineDataBrowser.Core.Tests;

public sealed class RecordingTests(OpcPlcFixture plc) : IAsyncLifetime
{
    private static readonly NodeId StaticNode = VariableIds.Server_ServerStatus_BuildInfo_ProductName;

    private readonly OpcUaClient _client = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly string _dir = Directory.CreateTempSubdirectory("recording-tests").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Keeps_only_newest_points_per_item()
    {
        await using var recording = await StartedAsync(new RecordingOptions { Name = "count", MaxPointsPerItem = 3 });

        for (var i = 1; i <= 5; i++)
        {
            recording.Append(Update(StaticNode, $"v{i}"));
        }

        Assert.Equal(["v3", "v4", "v5"], recording.GetHistory(StaticNode).Select(s => s.Value));
    }

    [Fact]
    public async Task Drops_samples_older_than_max_age()
    {
        await using var recording = await StartedAsync(new RecordingOptions { Name = "age", MaxAge = TimeSpan.FromMinutes(10) });

        recording.Append(Update(StaticNode, "old"));
        _time.Advance(TimeSpan.FromMinutes(8));
        recording.Append(Update(StaticNode, "mid"));
        _time.Advance(TimeSpan.FromMinutes(5));
        recording.Append(Update(StaticNode, "new"));

        Assert.Equal(["mid", "new"], recording.GetHistory(StaticNode).Select(s => s.Value));
    }

    [Fact]
    public async Task Pause_drops_samples_resume_captures_and_reset_clears_history()
    {
        await using var recording = await StartedAsync(new RecordingOptions { Name = "pause" });
        Assert.Equal(0, recording.TotalSamples);

        recording.Pause();
        recording.Append(Update(StaticNode, "while-paused"));
        recording.Resume();
        recording.Append(Update(StaticNode, "after-resume"));

        Assert.Equal(["after-resume"], recording.GetHistory(StaticNode).Select(s => s.Value));
        Assert.Equal(RecordingState.Recording, recording.State);

        recording.Reset();
        Assert.Empty(recording.GetHistory(StaticNode));
        Assert.Equal(0, recording.TotalSamples);
        Assert.Equal(RecordingState.Recording, recording.State);
    }

    [Fact]
    public async Task Items_added_while_recording_are_captured_and_duplicates_ignored()
    {
        var other = VariableIds.Server_ServerStatus_BuildInfo_SoftwareVersion;
        await using var recording = await StartedAsync(new RecordingOptions { Name = "add" });

        var added = await recording.AddItemsAsync([new RecordedItem(other, "Version", "v"), new RecordedItem(StaticNode, "dup", "d")], Ct);
        recording.Append(Update(other, "1.0"));

        Assert.Equal([other], added.Select(i => i.NodeId));
        Assert.Equal(2, recording.Items.Count);
        Assert.Equal(["1.0"], recording.GetHistory(other).Select(s => s.Value).Where(v => v == "1.0"));
        Assert.Empty(await recording.AddItemsAsync([new RecordedItem(other, "Version", "v")], Ct));
    }

    [Fact]
    public async Task Scheduled_start_and_auto_stop_follow_the_clock()
    {
        await using var recording = new Recording(
            _client,
            new RecordingOptions
            {
                Name = "scheduled",
                ScheduledStart = _time.GetUtcNow().AddHours(1),
                StopAfter = TimeSpan.FromMinutes(10),
            },
            [Item(StaticNode)],
            _time);

        await recording.StartAsync(Ct);
        Assert.Equal(RecordingState.Scheduled, recording.State);

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(RecordingState.Scheduled, recording.State);

        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => recording.State == RecordingState.Recording);
        Assert.Equal(_time.GetUtcNow().AddMinutes(10), recording.PlannedStopAt);

        _time.Advance(TimeSpan.FromMinutes(10));
        await WaitUntil(() => recording.State == RecordingState.Stopped);
        Assert.Equal(TimeSpan.FromMinutes(10), recording.Elapsed);
    }

    [Fact]
    public async Task Auto_stop_at_clock_time_wins_when_earlier_than_duration()
    {
        await using var recording = await StartedAsync(new RecordingOptions
        {
            Name = "stop-at",
            StopAfter = TimeSpan.FromHours(2),
            StopAt = _time.GetUtcNow().AddMinutes(30),
        });

        Assert.Equal(_time.GetUtcNow().AddMinutes(30), recording.PlannedStopAt);
        _time.Advance(TimeSpan.FromMinutes(30));
        await WaitUntil(() => recording.State == RecordingState.Stopped);
    }

    [Fact]
    public async Task Live_file_streams_every_sample_as_csv()
    {
        var path = Path.Combine(_dir, "live.csv");
        await using var recording = await StartedAsync(new RecordingOptions { Name = "live", LiveFilePath = path });

        recording.Append(Update(StaticNode, "a,\"quoted\""));
        recording.Append(Update(StaticNode, "b"));
        await recording.StopAsync(Ct);

        var lines = await File.ReadAllLinesAsync(path, Ct);
        Assert.Equal("ReceivedAt,SourceTimestamp,ServerTimestamp,Name,NodeId,Value,Status", lines[0]);
        Assert.Contains(lines, l => l.Contains("\"a,\"\"quoted\"\"\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("\"b\",Good", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_writes_csv_and_json_with_portable_node_ids()
    {
        await using var recording = await StartedAsync(new RecordingOptions { Name = "export" });
        recording.Append(Update(StaticNode, "42", numeric: 42));
        await recording.StopAsync(Ct);

        var csv = Path.Combine(_dir, "h.csv");
        var json = Path.Combine(_dir, "h.json");
        await recording.ExportCsvAsync(csv, Ct);
        await recording.ExportJsonAsync(json, Ct);

        Assert.Contains("\"42\",Good", await File.ReadAllTextAsync(csv, Ct), StringComparison.Ordinal);
        var text = await File.ReadAllTextAsync(json, Ct);
        Assert.Contains("\"name\": \"export\"", text, StringComparison.Ordinal);
        Assert.Contains("\"numeric\": 42", text, StringComparison.Ordinal);
        Assert.Contains($"\"nodeId\": \"{_client.ToPortableId(StaticNode)}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_export_handles_infinity_and_nan()
    {
        await using var recording = await StartedAsync(new RecordingOptions { Name = "inf" });
        recording.Append(Update(StaticNode, "∞", numeric: double.PositiveInfinity));
        recording.Append(Update(StaticNode, "NaN", numeric: double.NaN));
        recording.Append(Update(StaticNode, "-∞", numeric: double.NegativeInfinity));
        await recording.StopAsync(Ct);

        var json = Path.Combine(_dir, "inf.json");
        await recording.ExportJsonAsync(json, Ct);

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(json, Ct));
        var numerics = doc.RootElement.GetProperty("items")[0].GetProperty("samples").EnumerateArray()
            .Select(s => s.GetProperty("numeric").GetString()).ToList();
        Assert.Equal(["Infinity", "NaN", "-Infinity"], numerics);
    }

    [Fact]
    public async Task Records_real_changing_values_until_stopped_and_keeps_history_afterwards()
    {
        var node = new NodeId("StepUp", 3);
        await using var recording = new Recording(
            _client,
            new RecordingOptions { Name = "live-server", SamplingIntervalMs = 100 },
            [new RecordedItem(node, "StepUp", _client.ToPortableId(node))]);

        await recording.StartAsync(Ct);
        await WaitUntil(() => recording.GetSampleCount(node) >= 3);
        await recording.StopAsync(Ct);
        var count = recording.GetSampleCount(node);
        await Task.Delay(500, Ct);

        Assert.Equal(RecordingState.Stopped, recording.State);
        Assert.Equal(count, recording.GetSampleCount(node));
        Assert.All(recording.GetHistory(node), s => Assert.NotNull(s.Numeric));
    }

    private async Task<Recording> StartedAsync(RecordingOptions options)
    {
        var recording = new Recording(_client, options, [Item(StaticNode)], _time);
        await recording.StartAsync(Ct);
        Assert.Equal(RecordingState.Recording, recording.State);
        await WaitUntil(() => recording.TotalSamples > 0);
        recording.Reset();
        return recording;
    }

    private RecordedItem Item(NodeId nodeId) => new(nodeId, "ProductName", _client.ToPortableId(nodeId));

    private static ValueUpdate Update(NodeId nodeId, string value, double? numeric = null) =>
        new(nodeId, value, StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow, numeric);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            await Task.Delay(25, Ct);
        }
    }
}
