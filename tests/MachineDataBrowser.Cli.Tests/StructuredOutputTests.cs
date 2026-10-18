using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class StructuredOutputTests(CustomTypesServerFixture server) : IDisposable
{
    private const string StatusNode = "/Objects/Custom/Machines/Machine1/Status";

    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-structures").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Read_shows_complex_fields_in_text_json_and_csv()
    {
        var text = await RunAsync("read", server.EndpointUrl, StatusNode, "--trust-all");
        Assert.Equal(0, text.Exit);
        Assert.Contains("\"Position\"", text.Out, StringComparison.Ordinal);

        var json = await RunAsync("read", server.EndpointUrl, StatusNode, "--trust-all", "-f", "json");
        Assert.Equal(0, json.Exit);
        var structure = Assert.IsType<JsonObject>(JsonNode.Parse(json.Out)![0]!["value"]);
        Assert.True(structure.ContainsKey("State"));
        Assert.True(structure["Position"]!.AsObject().ContainsKey("X"));

        var csv = await RunAsync("read", server.EndpointUrl, StatusNode, "--trust-all", "-f", "csv");
        Assert.Equal(0, csv.Exit);
        Assert.Contains("\"\"Position\"\"", csv.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_csv_keeps_unpacked_structure_as_json_in_the_value_cell()
    {
        var session = Path.Combine(_dir, "boiler.mdbsession");
        var (createExit, _, createError) = await RunAsync("session", "create", session, server.EndpointUrl, StatusNode, "--trust-all");
        Assert.Equal(0, createExit);
        Assert.Empty(createError);

        var (exit, output, error) = await RunAsync("run", session, "--count", "1", "-f", "csv");

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.StartsWith("time,name,id,status,value", output, StringComparison.Ordinal);
        Assert.Contains("\"\"Position\"\"", output, StringComparison.Ordinal);
        Assert.Contains("\"\"Speed\"\"", output, StringComparison.Ordinal);
    }
}
