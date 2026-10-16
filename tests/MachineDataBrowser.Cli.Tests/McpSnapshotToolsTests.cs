using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>
/// Units in read, correlation in sample, the snapshot tools, restore_snapshot and the write rules and audit log, through a
/// real <c>mdbrowser mcp</c> process. SByte and Float are written only here, so other tests aren't disturbed.
/// </summary>
public sealed class McpSnapshotToolsTests(CustomTypesServerFixture custom) : IDisposable
{
    private const string SByte = "/Objects/Custom/DataTypes/SByte";
    private const string Float = "/Objects/Custom/DataTypes/Float";
    private const string LimitedInt32 = "/Objects/Custom/DataTypes/LimitedInt32";
    private const string ReadOnlyTemperature = "/Objects/Custom/History/Temperature";
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-mcp-snapshots").FullName;
    private readonly List<McpClient> _clients = [];

    private string Snapshots => Path.Combine(_dir, "snapshots");

    private string Audit => Path.Combine(_dir, "audit.jsonl");

    private List<string> Questions { get; } = [];

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        Directory.Delete(_dir, recursive: true);
    }

    private async Task<McpClient> StartAsync(params string[] args)
    {
        var options = new McpClientOptions
        {
            Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    Questions.Add(request?.Message ?? string.Empty);
                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) },
                    });
                },
            },
        };
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mdbrowser",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "mdbrowser.dll"), "mcp", "--endpoint", custom.EndpointUrl, "--snapshots-dir", Snapshots, .. args],
            EnvironmentVariables = new Dictionary<string, string?> { ["MACHINEDATABROWSER_DATA_DIR"] = _dir },
        });
        var client = await McpClient.CreateAsync(transport, options, cancellationToken: TestContext.Current.CancellationToken);
        _clients.Add(client);
        return client;
    }

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        return (result.IsError ?? false, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private static async Task<JsonNode> OkAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task Read_gives_engineering_units_where_the_server_has_them()
    {
        var client = await StartAsync();
        var values = (await OkAsync(client, "read", new() { ["nodes"] = new[] { "/Objects/Custom/History/Temperature", "/Objects/Custom/DataTypes/Int32" } }))["values"]!;
        Assert.Equal("°C", (string?)values[0]!["unit"]);
        Assert.Null(values[1]!["unit"]);
    }

    [Fact]
    public async Task Sample_with_correlate_reports_the_order_of_changes_and_values_that_change_together()
    {
        var client = await StartAsync();
        var result = await OkAsync(client, "sample", new()
        {
            ["nodes"] = new[] { "/Objects/Custom/Fast/Every50ms/Counter", "/Objects/Custom/Fast/Every50ms/Toggle" },
            ["seconds"] = 3,
            ["correlate"] = true,
        });

        Assert.True((int)result["sequence"]!["total"]! > 4);
        var pairs = result["correlations"]!.AsArray();
        var pair = Assert.Single(pairs, p => (string?)p!["leader"] == "Counter" && (string?)p["follower"] == "Toggle")!;
        Assert.True((double)pair["medianLagMs"]! < 100, pair.ToJsonString());
        Assert.True((int)pair["followed"]! >= 2);
    }

    [Fact]
    public async Task Snapshots_are_taken_compared_and_restored_and_every_change_is_audited()
    {
        var readOnly = await StartAsync();
        var tools = await readOnly.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "compare_snapshot");
        Assert.DoesNotContain(tools, t => t.Name is "take_snapshot" or "restore_snapshot");

        var client = await StartAsync("--allow-snapshots", "--allow-writes", "--audit-log", Audit);
        var original = await OkAsync(client, "read", new() { ["nodes"] = new[] { SByte } });
        var sbyteBefore = (long)original["values"]![0]!["value"]!;
        try
        {
            var taken = await OkAsync(client, "take_snapshot", new() { ["name"] = "Recipe A", ["nodes"] = new[] { SByte, Float } });
            Assert.Equal(2, (int)taken["items"]!);
            var listed = (await OkAsync(client, "list_snapshots", new()))["snapshots"]!.AsArray();
            Assert.Equal("Recipe A", (string?)Assert.Single(listed)!["name"]);
            Assert.Single(Directory.GetFiles(Snapshots, "*.json"));   // the app's format, so the app shows it too

            var compared = await OkAsync(client, "compare_snapshot", new() { ["snapshot"] = "Recipe", ["changedOnly"] = false });
            Assert.Equal(2, (int)compared["same"]!);

            await OkAsync(client, "write", new() { ["node"] = SByte, ["value"] = "5" });
            compared = await OkAsync(client, "compare_snapshot", new() { ["snapshot"] = "Recipe A" });
            var changed = Assert.Single(compared["items"]!.AsArray())!;
            Assert.Equal((SByte, "5", "+" + (5 - sbyteBefore)), ((string?)changed["path"], (string?)changed["after"], (string?)changed["change"]));
            Assert.Equal(1, (int)compared["restorable"]!);

            var restored = await OkAsync(client, "restore_snapshot", new() { ["snapshot"] = "Recipe A" });
            Assert.Equal(1, (int)restored["written"]!);
            Assert.Equal(sbyteBefore, (long)restored["items"]![0]!["after"]!);
            Assert.Contains(Questions, q => q.Contains($"SByte: 5 → {sbyteBefore}", StringComparison.Ordinal));
            Assert.Equal(0, (int)(await OkAsync(client, "compare_snapshot", new() { ["snapshot"] = "Recipe A" }))["changed"]!);

            var audit = (await File.ReadAllLinesAsync(Audit, TestContext.Current.CancellationToken)).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.Equal(["write:written", "restore_snapshot:written"], audit.Select(a => $"{a["tool"]}:{a["outcome"]}"));
            Assert.Equal("5", (string?)audit[0]["value"]);
        }
        finally
        {
            await OkAsync(client, "write", new() { ["node"] = SByte, ["value"] = sbyteBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    [Fact]
    public async Task Write_rules_limit_what_and_how_much_the_agent_may_change()
    {
        var client = await StartAsync("--allow-writes", "--audit-log", Audit,
            "--write-node", "/Objects/Custom/DataTypes/SByte", "--write-limit", "/Objects/Custom/DataTypes/SByte=-10..10");
        var sbyteBefore = (long)(await OkAsync(client, "read", new() { ["nodes"] = new[] { SByte } }))["values"]![0]!["value"]!;
        try
        {
            var (error, text) = await CallAsync(client, "write", new() { ["node"] = Float, ["value"] = "1" });
            Assert.True(error);
            Assert.Contains("--write-node", text, StringComparison.Ordinal);

            (error, text) = await CallAsync(client, "write", new() { ["node"] = SByte, ["value"] = "50" });
            Assert.True(error);
            Assert.Contains("between -10 and 10", text, StringComparison.Ordinal);

            (error, text) = await CallAsync(client, "write", new() { ["node"] = SByte, ["value"] = "fast" });
            Assert.True(error);
            Assert.Contains("not a number", text, StringComparison.Ordinal);
            Assert.Empty(Questions);   // refused before the user was asked

            await OkAsync(client, "write", new() { ["node"] = SByte, ["value"] = "3" });
            Assert.Single(Questions);

            var audit = (await File.ReadAllLinesAsync(Audit, TestContext.Current.CancellationToken)).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.Equal(["refused", "refused", "refused", "written"], audit.Select(a => (string?)a["outcome"]));
            Assert.Contains("--write-node", (string?)audit[0]["detail"], StringComparison.Ordinal);
        }
        finally
        {
            await OkAsync(client, "write", new() { ["node"] = SByte, ["value"] = sbyteBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    [Fact]
    public async Task Write_preflight_refuses_bad_type_read_only_access_and_eurange_before_confirmation()
    {
        var client = await StartAsync("--allow-writes", "--audit-log", Audit);
        var initial = await OkAsync(client, "read", new() { ["nodes"] = new[] { SByte, LimitedInt32 } });
        var sbyteBefore = (long)initial["values"]![0]!["value"]!;
        var limitedBefore = (long)initial["values"]![1]!["value"]!;
        try
        {
            var (error, text) = await CallAsync(client, "write", new() { ["node"] = SByte, ["value"] = "not an integer" });
            Assert.True(error);
            Assert.Contains("not a valid SByte", text, StringComparison.Ordinal);

            (error, text) = await CallAsync(client, "write", new() { ["node"] = ReadOnlyTemperature, ["value"] = "12" });
            Assert.True(error);
            Assert.Contains("not writable", text, StringComparison.OrdinalIgnoreCase);

            var attrs = await OkAsync(client, "attributes", new() { ["node"] = LimitedInt32 });
            Assert.Contains(attrs["attributes"]!.AsObject(), a => a.Key == "DataType");
            (error, text) = await CallAsync(client, "write", new() { ["node"] = LimitedInt32, ["value"] = "11" });
            Assert.True(error);
            Assert.Contains("EURange 0..10", text, StringComparison.Ordinal);
            Assert.Empty(Questions);   // all three failures happen before asking the user

            await OkAsync(client, "write", new() { ["node"] = LimitedInt32, ["value"] = "8" });
            Assert.Single(Questions);
            Assert.Equal(8, (long)(await OkAsync(client, "read", new() { ["nodes"] = new[] { LimitedInt32 } }))["values"]![0]!["value"]!);

            var audit = (await File.ReadAllLinesAsync(Audit, TestContext.Current.CancellationToken)).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.Equal(["refused", "refused", "refused", "written"], audit.Select(a => (string?)a["outcome"]));
            Assert.Contains("EURange", (string?)audit[2]["detail"], StringComparison.Ordinal);
        }
        finally
        {
            await OkAsync(client, "write", new() { ["node"] = SByte, ["value"] = sbyteBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            await OkAsync(client, "write", new() { ["node"] = LimitedInt32, ["value"] = limitedBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    [Fact]
    public async Task Restore_snapshot_refuses_everything_before_confirmation_if_one_item_is_read_only_or_out_of_range()
    {
        var client = await StartAsync("--allow-writes", "--allow-snapshots", "--audit-log", Audit);
        var values = await OkAsync(client, "read", new() { ["nodes"] = new[] { SByte, LimitedInt32, ReadOnlyTemperature } });
        var beforeSByte = (long)values["values"]![0]!["value"]!;
        var beforeLimited = (long)values["values"]![1]!["value"]!;
        var temp = (double)values["values"]![2]!["value"]!;
        await OkAsync(client, "take_snapshot", new() { ["name"] = "Unsafe recipe", ["nodes"] = new[] { SByte, LimitedInt32, ReadOnlyTemperature } });
        var store = new MachineDataBrowser.Core.SnapshotStore(Snapshots);
        var saved = store.List().Single();
        store.Save(saved with
        {
            Items =
            [
                saved.Items.Single(i => i.Name == "SByte") with { Value = "0", Numeric = 0 },
                saved.Items.Single(i => i.Name == "LimitedInt32") with { Value = "50", Numeric = 50 },
                saved.Items.Single(i => i.Name == "Temperature") with { Value = (temp + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Numeric = temp + 1 },
            ],
        });

        var (error, text) = await CallAsync(client, "restore_snapshot", new() { ["snapshot"] = "Unsafe recipe" });
        Assert.True(error);
        Assert.Contains("Nothing was written", text, StringComparison.Ordinal);
        Assert.Contains("EURange", text, StringComparison.Ordinal);
        Assert.Contains("not writable", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Questions);
        var unchanged = await OkAsync(client, "read", new() { ["nodes"] = new[] { SByte, LimitedInt32 } });
        Assert.Equal(beforeSByte, (long)unchanged["values"]![0]!["value"]!);
        Assert.Equal(beforeLimited, (long)unchanged["values"]![1]!["value"]!);
    }
}
