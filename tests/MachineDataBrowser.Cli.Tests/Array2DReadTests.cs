using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class Array2DReadTests(OpcPlcFixture plc)
{
    [Fact]
    public async Task Recursive_read_handles_multidimensional_values()
    {
        using var textOut = new StringWriter();
        using var textErr = new StringWriter();
        var textExit = await Commands.Build(textOut, textErr)
            .Parse(["read", plc.EndpointUrl, "/Objects/ReferenceTest/Scalar/Scalar_Static", "-R", "--max-items", "500", "--trust-all"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, textExit);
        Assert.Contains("limited to the first 500 variables", textErr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Arrays2D/Double", textOut.ToString(), StringComparison.Ordinal);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr)
            .Parse(["read", plc.EndpointUrl, "/Objects/ReferenceTest/Scalar/Scalar_Static", "-R", "--max-items", "500", "--trust-all", "-f", "json"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        var values = JsonNode.Parse(stdout.ToString())!.AsArray();
        var doubleArray = Assert.Single(values, item => (string?)item!["name"] == "Arrays2D/Double");
        Assert.StartsWith("Double[", doubleArray!["type"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.IsType<JsonArray>(doubleArray["value"]![0]);
        Assert.Contains("limited to the first 500 variables", stderr.ToString(), StringComparison.Ordinal);
    }
}
