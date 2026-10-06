using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class SessionTests(OpcPlcFixture plc) : IDisposable
{
    private const string Basic = "/Objects/OpcPlc/Telemetry/Basic";
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-sessions").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject Load(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    [Fact]
    public async Task Create_writes_an_app_session_with_names_paths_and_options()
    {
        var file = Path.Combine(_dir, "line1.mdbsession");

        var (exit, output, _) = await RunAsync("session", "create", file, plc.EndpointUrl, Basic, "-R", "-r", "1000", "--trust-all");

        Assert.Equal(0, exit);
        Assert.Contains("Saved 4 watch item(s)", output, StringComparison.Ordinal);
        var session = Load(file);
        Assert.Equal(1, (int)session["version"]!);
        Assert.Equal(plc.EndpointUrl, (string)session["endpointUrl"]!);
        Assert.True((bool)session["autoAcceptCertificates"]!);
        Assert.Equal(1000, (int)session["defaultRefreshMs"]!);
        var step = session["watch"]!.AsArray().Single(w => (string)w!["displayName"]! == "StepUp")!;
        Assert.Equal("nsu=http://microsoft.com/Opc/OpcPlc/;s=StepUp", (string)step["nodeId"]!);
        Assert.Equal("Objects/OpcPlc/Telemetry/Basic", (string)step["path"]!);
        Assert.DoesNotContain("password", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);

        (exit, _, var error) = await RunAsync("session", "create", file, plc.EndpointUrl, Basic, "-R");
        Assert.Equal(1, exit);
        Assert.Contains("--force", error, StringComparison.Ordinal);
        (exit, _, _) = await RunAsync("session", "create", file, plc.EndpointUrl, $"{Basic}/StepUp", "--force", "--trust-all");
        Assert.Equal(0, exit);
        Assert.Single(Load(file)["watch"]!.AsArray());
    }

    [Fact]
    public async Task Add_appends_new_nodes_and_keeps_everything_else_in_the_file()
    {
        var file = Path.Combine(_dir, "app.mdbsession");
        await File.WriteAllTextAsync(file, $$$"""
            {"version":1,"endpointUrl":"{{{plc.EndpointUrl}}}","autoAcceptCertificates":true,"readOnly":true,
             "watch":[{"nodeId":"nsu=http://microsoft.com/Opc/OpcPlc/;s=StepUp","displayName":"StepUp","display":{"format":"Hex"}}],
             "bookmarks":[{"name":"Line 1"}],"watchColumns":[{"header":"Value","visible":true,"order":0}],"groupWatchByPath":true}
            """, TestContext.Current.CancellationToken);

        var (exit, output, _) = await RunAsync("session", "add", file, Basic, "-R");

        Assert.Equal(0, exit);
        Assert.Contains("Added 3 watch item(s)", output, StringComparison.Ordinal);
        Assert.Contains("(1 already there); 4 in total", output, StringComparison.Ordinal);
        var session = Load(file);
        Assert.True((bool)session["readOnly"]!);
        Assert.Equal("Line 1", (string)session["bookmarks"]![0]!["name"]!);
        Assert.Equal("Value", (string)session["watchColumns"]![0]!["header"]!);
        Assert.Equal("Hex", (string)session["watch"]![0]!["display"]!["format"]!);
    }

    [Fact]
    public async Task Nodes_that_are_not_on_the_device_are_refused_and_nothing_is_saved()
    {
        var file = Path.Combine(_dir, "typo.mdbsession");

        var (exit, _, error) = await RunAsync("session", "create", file, plc.EndpointUrl, "ns=3;s=NoSuchNode", "--trust-all");

        Assert.Equal(1, exit);
        Assert.Contains("is not on the device", error, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Monitor_save_writes_the_session_and_run_opens_it()
    {
        var file = Path.Combine(_dir, "watched.mdbsession");
        var (exit, _, error) = await RunAsync("monitor", plc.EndpointUrl, $"{Basic}/StepUp", "ns=3;s=AlternatingBoolean", "--trust-all", "-n", "1", "--save", file);
        Assert.Equal(0, exit);
        Assert.Contains("saved 2 watch item(s)", error, StringComparison.Ordinal);
        Assert.Equal(["StepUp", "AlternatingBoolean"], Load(file)["watch"]!.AsArray().Select(w => (string)w!["displayName"]!));

        var (runExit, runOutput, _) = await RunAsync("run", file, "-n", "2", "-f", "csv");
        Assert.Equal(0, runExit);
        Assert.Contains(",StepUp,", runOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_takes_items_out_by_path_name_or_id_without_connecting()
    {
        var file = Path.Combine(_dir, "remove.mdbsession");
        Assert.Equal(0, (await RunAsync("session", "create", file, plc.EndpointUrl, Basic, "-R", "--trust-all")).Exit);
        var json = Load(file);
        json["endpointUrl"] = "opc.tcp://offline.invalid:4840";
        await File.WriteAllTextAsync(file, json.ToJsonString(), TestContext.Current.CancellationToken);

        var (exit, output, _) = await RunAsync("session", "remove", file, $"{Basic}/StepUp", "s=AlternatingBoolean");
        Assert.Equal(0, exit);
        Assert.Contains("Removed 2 watch item(s)", output, StringComparison.Ordinal);
        Assert.Equal(["RandomSignedInt32", "RandomUnsignedInt32"], Load(file)["watch"]!.AsArray().Select(w => (string)w!["displayName"]!).Order());

        var (missingExit, _, error) = await RunAsync("session", "rm", file, "RandomSignedInt32", "NotThere");
        Assert.Equal(1, missingExit);
        Assert.Contains("'NotThere' is not in the watch list", error, StringComparison.Ordinal);
        Assert.Equal(2, Load(file)["watch"]!.AsArray().Count);

        Assert.Equal(0, (await RunAsync("session", "rm", file, Basic, "-R")).Exit);
        Assert.Empty(Load(file)["watch"]!.AsArray());
        Assert.Equal("opc.tcp://offline.invalid:4840", (string)Load(file)["endpointUrl"]!);
    }

    [Fact]
    public async Task Run_follows_changes_to_its_session_file()
    {
        var file = Path.Combine(_dir, "follow.mdbsession");
        Assert.Equal(0, (await RunAsync("session", "create", file, plc.EndpointUrl, $"{Basic}/StepUp", "--trust-all", "-r", "100")).Exit);

        var output = new StringWriter();
        var errors = new StringWriter();
        using var stdout = TextWriter.Synchronized(output);
        using var stderr = TextWriter.Synchronized(errors);
        string Out() { lock (stdout) { return output.ToString(); } }
        string Err() { lock (stderr) { return errors.ToString(); } }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = Commands.Build(stdout, stderr).Parse(["run", file, "-f", "csv", "--duration", "60s"]).InvokeAsync(cancellationToken: stop.Token);

        await Until(() => Out().Contains(",StepUp,", StringComparison.Ordinal));
        Assert.Equal(0, (await RunAsync("session", "add", file, $"{Basic}/AlternatingBoolean", "-r", "100")).Exit);

        await Until(() => Out().Contains(",AlternatingBoolean,", StringComparison.Ordinal));
        Assert.Contains("follow.mdbsession changed; monitoring its 2 watch item(s)", Err(), StringComparison.Ordinal);
        await stop.CancelAsync();
        Assert.Equal(0, await run);
        Assert.Single(Out().Split('\n'), line => line.StartsWith("time,", StringComparison.Ordinal));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition never became true");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
