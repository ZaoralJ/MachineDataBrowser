using System.Text.Json.Nodes;
using MachineDataBrowser.Cli.Tui;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>S7 on the command line: a tag list (--map), stored in session files, and loaded in the TUI (k).</summary>
public sealed class S7CliTests(S7SimulatorFixture plc) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-s7").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Url => $"s7://{plc.Host}/0,1";

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr).Parse(args).InvokeAsync(cancellationToken: Ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string CopyTags()
    {
        var tags = Path.Combine(_dir, "plc", "tags.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(tags)!);
        File.Copy(S7SimulatorFixture.TagFile, tags);
        return tags;
    }

    [Fact]
    public async Task Tags_option_names_values_by_path_and_by_symbol()
    {
        var (exit, output, _) = await RunAsync("read", Url, "--map", S7SimulatorFixture.TagFile, "/Tags/DataTypes/Int", "\"DataTypes.String\"", "DB3.DBD14:REAL", "-f", "csv");
        Assert.Equal(0, exit);
        Assert.Contains("Int,Int16,-1234", output, StringComparison.Ordinal);
        Assert.Contains("Hello", output, StringComparison.Ordinal);
        Assert.Contains("3.14", output, StringComparison.Ordinal);

        (exit, _, var error) = await RunAsync("read", Url, "--map", Path.Combine(_dir, "missing.csv"), "MW2");
        Assert.Equal(1, exit);
        Assert.Contains("missing.csv", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessions_store_the_tag_list_next_to_them_relative_and_run_uses_it_after_a_move()
    {
        var tags = CopyTags();
        var file = Path.Combine(_dir, "line.mdbsession");
        var (exit, _, _) = await RunAsync("session", "create", file, Url, "--map", tags, "/Tags/Machine/Counter");
        Assert.Equal(0, exit);
        Assert.Equal("plc/tags.csv", (string?)JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!["tagMap"]);

        var moved = Directory.CreateTempSubdirectory("mdbrowser-s7-moved").FullName;
        try
        {
            Directory.Move(Path.Combine(_dir, "plc"), Path.Combine(moved, "plc"));
            File.Move(file, Path.Combine(moved, "line.mdbsession"));
            (exit, var output, var error) = await RunAsync("run", Path.Combine(moved, "line.mdbsession"), "-n", "1", "-f", "csv");
            Assert.True(exit == 0, error);
            Assert.Contains("Counter", output, StringComparison.Ordinal);
            Assert.Contains("Good", output, StringComparison.Ordinal);

            (exit, output, _) = await RunAsync("session", "add", Path.Combine(moved, "line.mdbsession"), "/Tags/Machine/Speed");
            Assert.Equal(0, exit);
            Assert.Contains("Added 1", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(moved, recursive: true);
        }
    }

    [Fact]
    public async Task Tag_lists_outside_the_session_folder_are_stored_absolute()
    {
        var file = Path.Combine(_dir, "abs.mdbsession");
        var (exit, _, _) = await RunAsync("session", "create", file, Url, "--map", S7SimulatorFixture.TagFile, "MW2:INT");
        Assert.Equal(0, exit);
        Assert.Equal(S7SimulatorFixture.TagFile, (string?)JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!["tagMap"]);
    }

    [Fact]
    public async Task Tui_loads_a_tag_list_keeping_the_watch_and_saves_it_with_the_session()
    {
        var args = new ConnectionArgs(Url, null, null, false, false);
        var client = await Connection.ConnectAsync(args, Ct);
        var model = new BrowserModel(client, args, 250, ownsClient: true);
        try
        {
            Assert.True(model.SupportsTagMap);
            Assert.Equal("Tag list", model.TagMapLabel);
            Assert.DoesNotContain(await model.LoadChildrenAsync(model.Root, cancellationToken: Ct), e => e.Item.DisplayName == "Tags");
            await model.MonitorNodesAsync([(new Node(client.ParsePortableId("MW2:INT"), "Ticks", "MW2:INT"), 250)], Ct);

            var tags = CopyTags();
            var loaded = await model.WithTagMapAsync(tags, Ct);
            await model.DisposeAsync();
            model = loaded;
            Assert.Equal(tags, model.Args.TagMap);
            Assert.Equal("Tags", (await model.LoadChildrenAsync(model.Root, cancellationToken: Ct))[0].Item.DisplayName);
            Assert.Single(model.Watch);

            var file = Path.Combine(_dir, "tui.mdbsession");
            await model.SaveSessionAsync(file, Ct);
            Assert.Equal("plc/tags.csv", (string?)JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!["tagMap"]);
        }
        finally
        {
            await model.DisposeAsync();
        }
    }
}
