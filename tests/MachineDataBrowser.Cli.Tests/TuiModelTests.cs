using System.Text.Json.Nodes;
using MachineDataBrowser.Cli.Tui;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>What <c>mdbrowser tui</c> does, through its UI-free model, against the simulators.</summary>
public sealed class TuiModelTests(CustomTypesServerFixture custom, OpcPlcFixture plc, MqttSimulatorFixture broker, LogixSimulatorFixture logix) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-tui").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<BrowserModel> OpenAsync(string url, bool readOnly = false, int refreshMs = 100)
    {
        var args = new ConnectionArgs(url, null, null, false, true, readOnly);
        var client = await Connection.ConnectAsync(args, Ct);
        return new BrowserModel(client, args, refreshMs);
    }

    private static async Task<TreeEntry> FindAsync(BrowserModel model, string name)
    {
        var hit = (await model.SearchAsync(name, cancellationToken: Ct)).Hits.First(h => h.Item.DisplayName == name);
        return await model.RevealAsync(hit.Path, Ct) ?? throw new InvalidOperationException($"{name} not revealed");
    }

    private static async Task Until(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Browses_shows_attributes_monitors_a_folder_and_unmonitors()
    {
        await using var model = await OpenAsync(custom.EndpointUrl);
        await using var _ = model.Client;

        var objects = (await model.LoadChildrenAsync(model.Root, cancellationToken: Ct)).Single(c => c.Item.DisplayName == "Objects");
        Assert.Contains(await model.LoadChildrenAsync(objects, cancellationToken: Ct), c => c.Item.DisplayName == "Custom");

        // Search reveals the node in the tree with its path, so its parents are loaded and cached.
        var pause = await FindAsync(model, "PauseSimulation");
        Assert.Equal("/Objects/Custom/PauseSimulation", pause.Path);
        Assert.Contains(await model.ReadAttributesAsync(pause, Ct), a => a.Name == "NodeClass" && a.Value == "Variable");

        // A folder monitors every variable below it, named by their path below it.
        var fast = await FindAsync(model, "Every10ms");
        Assert.True(await model.MonitorAsync(fast, Ct) >= 4);
        Assert.Contains(model.Watch, r => r.Node.Name == "Counter" && r.Node.ParentPath == "Objects/Custom/Fast/Every10ms");
        await Until(() => model.Watch.All(r => r.Snapshot().Updates > 0));

        // Monitoring again adds nothing twice.
        Assert.Equal(0, await model.MonitorAsync(fast, Ct));
        var count = model.Watch.Count;
        await model.UnmonitorAsync(model.Watch[0]);
        Assert.Equal(count - 1, model.Watch.Count);
    }

    [Fact]
    public async Task Writes_read_back_and_read_only_sessions_refuse()
    {
        await using (var model = await OpenAsync(custom.EndpointUrl))
        await using (model.Client)
        {
            // Int64: a variable no other test class writes (they run in parallel against the same server).
            var int64 = await FindAsync(model, "Int64");
            var written = await model.WriteAsync(int64.Item.NodeId, "Int64", "-4242", Ct);
            Assert.Null(written.Error);
            Assert.Equal("-4242", written.After);

            var rejected = await model.WriteAsync(int64.Item.NodeId, "Int64", "not a number", Ct);
            Assert.NotNull(rejected.Error);
            Assert.Equal("-4242", rejected.After);
        }

        await using var readOnly = await OpenAsync(custom.EndpointUrl, readOnly: true);
        await using var client = readOnly.Client;
        var target = await FindAsync(readOnly, "Int64");
        Assert.NotNull(readOnly.WriteBlockedReason);
        await Assert.ThrowsAsync<CliException>(() => readOnly.WriteAsync(target.Item.NodeId, "Int64", "1", Ct));
    }

    [Theory]
    [InlineData("tui.db")]
    [InlineData("tui.csv")]
    public async Task Records_the_monitored_items_to_a_file(string name)
    {
        await using var model = await OpenAsync(custom.EndpointUrl);
        await using var _ = model.Client;
        await Assert.ThrowsAsync<CliException>(() => model.StartRecordingAsync(Path.Combine(_dir, name), Ct)); // nothing monitored yet

        await model.MonitorAsync(await FindAsync(model, "Every10ms"), Ct);
        var path = Path.Combine(_dir, name);
        await model.StartRecordingAsync(path, Ct);
        Assert.Equal(path, model.RecordingPath);
        await Until(() => model.RecordedSamples > 20);
        await model.StopRecordingAsync();

        Assert.Null(model.RecordingPath);
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Contains(model.LogLines, l => l.Contains("sample(s) to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saving_a_session_replaces_its_watch_list_and_keeps_everything_else()
    {
        var path = Path.Combine(_dir, "tui.mdbsession");
        await using var model = await OpenAsync(custom.EndpointUrl);
        await using var _ = model.Client;
        var pause = await FindAsync(model, "PauseSimulation");
        var int32 = await FindAsync(model, "Int32");
        var keptId = model.Client.ToPortableId(pause.Item.NodeId);
        await File.WriteAllTextAsync(path, new JsonObject
        {
            ["version"] = 1,
            ["endpointUrl"] = custom.EndpointUrl,
            ["bookmarks"] = new JsonArray("kept bookmark"),
            ["watch"] = new JsonArray(
                new JsonObject { ["nodeId"] = keptId, ["displayName"] = "PauseSimulation", ["display"] = new JsonObject { ["format"] = "kept" } },
                new JsonObject { ["nodeId"] = "nsu=urn:gone;s=Removed", ["displayName"] = "Removed" }),
        }.ToJsonString(), Ct);

        await model.MonitorAsync(pause, Ct);
        await model.MonitorAsync(int32, Ct);
        var result = await model.SaveSessionAsync(path, Ct);

        Assert.Equal(2, result.Total);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!;
        Assert.Equal("kept bookmark", (string)saved["bookmarks"]![0]!);
        var watch = saved["watch"]!.AsArray();
        Assert.Equal("kept", (string)watch.Single(w => (string)w!["nodeId"]! == keptId)!["display"]!["format"]!);
        Assert.DoesNotContain(watch, w => (string)w!["displayName"]! == "Removed");
        Assert.Contains(watch, w => (string)w!["displayName"]! == "Int32" && (string)w["path"]! == "Objects/Custom/DataTypes");

        // The CLI reads it back like any app session.
        var file = await SessionFile.LoadAsync(path, Ct);
        Assert.Equal(2, file.Watch!.Count);
    }

    [Fact]
    public async Task History_of_a_historized_variable()
    {
        await using var model = await OpenAsync(custom.EndpointUrl);
        await using var _ = model.Client;
        Assert.True(model.SupportsHistory);
        var temperature = await FindAsync(model, "Temperature");
        var history = await model.ReadHistoryAsync(temperature.Item.NodeId, TimeSpan.FromMinutes(30), Ct);
        Assert.NotEmpty(history.Values);
    }

    [Fact]
    public async Task Events_and_current_alarms()
    {
        await using var model = await OpenAsync(plc.EndpointUrl);
        await using var _ = model.Client;
        Assert.True(model.SupportsEvents);
        await model.StartEventsAsync(Ct);
        await Until(() => model.Events.Count > 0 && model.Alarms.Count > 0, seconds: 30);
        Assert.All(model.Alarms, a => Assert.True(a.IsCondition));
        var diagnostics = await model.DiagnosticsAsync(Ct);
        Assert.Contains(diagnostics, d => d.Name == "State" && d.Value == "Connected");
    }

    [Fact]
    public async Task Mqtt_folder_monitors_the_json_fields_below_it()
    {
        await using var model = await OpenAsync(broker.EndpointUrl, refreshMs: 0);
        await using var _ = model.Client;
        Assert.False(model.SupportsEvents);
        var m1 = await FindAsync(model, "m1");
        Assert.True(await model.MonitorAsync(m1, Ct) >= 5);
        Assert.Contains(model.Watch, r => r.Node.Name == "status/speed");
        await Until(() => model.Watch.Any(r => r.Node.Name == "status/speed" && r.Snapshot().Updates > 0));

        // d in the TUI: discovery off and on again, like Pause discovery in the app; monitored topics keep coming.
        Assert.True(model.SupportsDiscoveryPause);
        await model.ToggleDiscoveryAsync(Ct);
        Assert.True(model.IsDiscoveryPaused);
        var speed = model.Watch.Single(r => r.Node.Name == "status/speed");
        var updates = speed.Snapshot().Updates;
        await Until(() => speed.Snapshot().Updates > updates);
        await model.ToggleDiscoveryAsync(Ct);
        Assert.False(model.IsDiscoveryPaused);
        await Until(() => model.LogLines.Any(l => l.Contains("Discovery resumed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Logix_structure_monitors_its_members()
    {
        await using var model = await OpenAsync(logix.EndpointUrl);
        await using var _ = model.Client;
        var medium = await FindAsync(model, "Medium");
        Assert.Equal(4, await model.MonitorAsync(medium, Ct));
        await Until(() => model.Watch.All(r => r.Snapshot().Status == "Good"));
    }
}
