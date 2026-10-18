using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class DecimalOutputTests(OpcPlcFixture plc) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-decimal").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Run_csv_shows_static_opc_ua_decimal_as_exact_numeric_text()
    {
        var session = Path.Combine(_dir, "decimal.mdbsession");
        var (createExit, _, createError) = await RunAsync(
            "session", "create", session, plc.EndpointUrl, "ns=8;s=Scalar_Static_Decimal", "--trust-all");
        Assert.Equal(0, createExit);
        Assert.Empty(createError);

        var (exit, output, error) = await RunAsync("run", session, "--count", "1", "-f", "csv");

        Assert.Equal(0, exit);
        Assert.Empty(error);
        var fields = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)).Split(',');
        Assert.StartsWith("0.", fields[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("Scale", fields[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("Value", fields[^1], StringComparison.Ordinal);
    }
}
