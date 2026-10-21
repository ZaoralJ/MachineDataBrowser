using System.Text.Json.Nodes;
using MachineDataBrowser.Cli.Tui;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>Modbus from the command line: raw addresses, a register map (--map), and the map stored in session files.</summary>
public sealed class ModbusCliTests(ModbusSimulatorFixture device) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-modbus").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string WriteMap()
    {
        var map = Path.Combine(_dir, "maps", "line1.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(map)!);
        File.WriteAllText(map, "name,address,unit\nLine1/Speed,hr:20:float32,rpm\nLine1/Name,hr:50:string16\n");
        return map;
    }

    [Fact]
    public async Task Reads_addresses_with_types()
    {
        var (exit, output, error) = await RunAsync("read", device.EndpointUrl, "hr:10:int16", "hr:22:float32:swap", "-f", "csv");

        Assert.True(exit == 0, error);
        Assert.Contains("-1234", output, StringComparison.Ordinal);
        Assert.Contains("3.5", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_names_the_tree_and_paths()
    {
        var map = WriteMap();

        var (exit, output, error) = await RunAsync("browse", device.EndpointUrl, "/Tags/Line1", "--map", map, "-f", "csv");
        Assert.True(exit == 0, error);
        Assert.Contains("hr:20:float32", output, StringComparison.Ordinal);
        Assert.Contains("Speed", output, StringComparison.Ordinal);

        (exit, output, error) = await RunAsync("read", device.EndpointUrl, "/Tags/Line1/Name", "-m", map, "-f", "csv");
        Assert.True(exit == 0, error);
        Assert.Contains("MDB-SIM", output, StringComparison.Ordinal);

        (exit, _, error) = await RunAsync("browse", device.EndpointUrl, "/Tags", "--map", Path.Combine(_dir, "missing.csv"));
        Assert.Equal(1, exit);
        Assert.Contains("register map", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_files_keep_the_map_relative_to_themselves()
    {
        var map = WriteMap();
        var file = Path.Combine(_dir, "line1.mdbsession");

        var (exit, _, error) = await RunAsync("session", "create", file, device.EndpointUrl, "/Tags/Line1", "-R", "--map", map);
        Assert.True(exit == 0, error);
        var session = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("maps/line1.csv", (string)session["tagMap"]!);
        var speed = session["watch"]!.AsArray().Single(w => (string)w!["displayName"]! == "Speed")!;
        Assert.Equal("hr:20:float32", (string)speed["nodeId"]!);
        Assert.Equal("Tags/Line1", (string)speed["path"]!);

        // `session add` connects with the stored map, so paths below Tags resolve.
        (exit, _, error) = await RunAsync("session", "add", file, "/Tags/Line1/Name");
        Assert.True(exit == 0, error);

        (exit, var output, error) = await RunAsync("run", file, "-n", "2", "-f", "csv");
        Assert.True(exit == 0, error);
        Assert.Contains("Speed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tui_loads_a_map_and_keeps_the_monitored_items()
    {
        var map = WriteMap();
        var args = new ConnectionArgs(device.EndpointUrl, null, null, false, false);
        var model = new BrowserModel(await Connection.ConnectAsync(args, TestContext.Current.CancellationToken), args, 100, ownsClient: true);
        BrowserModel? mapped = null;
        try
        {
            Assert.True(model.SupportsTagMap);
            var id = model.Client.ParsePortableId("hr:20:float32");
            await model.MonitorNodesAsync([(new Node(id, "hr:20:float32", "hr:20:float32"), 100)], TestContext.Current.CancellationToken);

            mapped = await model.WithTagMapAsync(map, TestContext.Current.CancellationToken);

            Assert.Equal(map, mapped.Args.TagMap);
            Assert.Single(mapped.Watch);
            var root = await mapped.LoadChildrenAsync(mapped.Root, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Tags", root[0].Item.DisplayName);
            Assert.Contains(mapped.LogLines, l => l.Contains("line1.csv loaded", StringComparison.Ordinal));
        }
        finally
        {
            await model.DisposeAsync();
            if (mapped is not null)
            {
                await mapped.DisposeAsync();
            }
        }
    }
}
