using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class WriteTests(CustomTypesServerFixture server, MqttSimulatorFixture broker)
{
    private static async Task<(int Exit, string Out, string Err)> RunAsync(IAnsiConsole? terminal, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Commands.Build(stdout, stderr, terminal).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static Task<(int Exit, string Out, string Err)> RunAsync(params string[] args) => RunAsync(null, args);

    private async Task<JsonNode?> ReadAsync(string node)
    {
        var (_, output, _) = await RunAsync("read", server.EndpointUrl, node, "-f", "json");
        return JsonNode.Parse(output)![0]!["value"];
    }

    [Fact]
    public async Task Writes_a_value_converted_to_the_variable_type_and_reads_it_back()
    {
        var (exit, output, _) = await RunAsync("write", server.EndpointUrl, "/Objects/Custom/DataTypes/Int16", "-321", "--yes", "-f", "json");

        Assert.Equal(0, exit);
        var result = Assert.Single(JsonNode.Parse(output)!.AsArray())!;
        Assert.True((bool)result["written"]!);
        Assert.Equal(-321, (int)result["after"]!);
        Assert.Equal(-321, (int)(await ReadAsync("/Objects/Custom/DataTypes/Int16"))!);
    }

    [Fact]
    public async Task Several_values_with_set_report_each_result_and_fail_if_any_failed()
    {
        var (exit, output, _) = await RunAsync("write", server.EndpointUrl,
            "--set", "/Objects/Custom/DataTypes/String=a=b",
            "--set", "/Objects/Custom/EdgeCases/EmptyString=x",
            "--set", "/Objects/Custom/DataTypes/Byte=300",
            "--yes", "-f", "json");

        Assert.Equal(1, exit);
        var results = JsonNode.Parse(output)!.AsArray();
        Assert.Equal("a=b", (string)results[0]!["after"]!);          // a path splits at the first '=': values may contain '='
        Assert.False((bool)results[1]!["written"]!);
        Assert.Contains("Bad", (string)results[1]!["error"]!, StringComparison.Ordinal);
        Assert.False((bool)results[2]!["written"]!);                // 300 does not fit a Byte
        Assert.DoesNotContain("FormatException", (string)results[2]!["error"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_yes_a_script_is_refused_and_nothing_is_written()
    {
        var before = await ReadAsync("/Objects/Custom/DataTypes/UInt16");

        var (exit, output, error) = await RunAsync("write", server.EndpointUrl, "/Objects/Custom/DataTypes/UInt16", "1");

        Assert.Equal(1, exit);
        Assert.Empty(output);
        Assert.Contains("add --yes", error, StringComparison.Ordinal);
        Assert.Equal(before!.ToJsonString(), (await ReadAsync("/Objects/Custom/DataTypes/UInt16"))!.ToJsonString());
    }

    [Fact]
    public async Task In_a_terminal_it_asks_first_and_writes_only_on_yes()
    {
        var console = new TestConsole().Width(140).Interactive();
        console.Input.PushTextWithEnter("n");
        var (exit, _, error) = await RunAsync(console, "write", server.EndpointUrl, "/Objects/Custom/DataTypes/Int32", "5");
        Assert.Equal(1, exit);
        Assert.Contains("Write this value to", console.Output, StringComparison.Ordinal);
        Assert.Contains("nothing written", error, StringComparison.Ordinal);

        console = new TestConsole().Width(140).Interactive();
        console.Input.PushTextWithEnter("y");
        (exit, _, _) = await RunAsync(console, "write", server.EndpointUrl, "/Objects/Custom/DataTypes/Int32", "5");
        Assert.Equal(0, exit);
        Assert.Contains("│ written │", console.Output, StringComparison.Ordinal);
        Assert.Equal(5, (int)(await ReadAsync("/Objects/Custom/DataTypes/Int32"))!);
    }

    [Fact]
    public async Task Mqtt_write_shows_the_value_that_came_back_through_the_broker()
    {
        var (exit, output, _) = await RunAsync("write", broker.EndpointUrl, "t:bulk/sensor/0042", "77.25", "--yes", "-f", "json");

        Assert.Equal(0, exit);
        Assert.Equal(77.25, (double)JsonNode.Parse(output)![0]!["after"]!);
    }

    [Theory]
    [InlineData(null, null, new[] { "ns=2;s=X=5" }, "ns=2;s=X", "5")]
    [InlineData("/Objects/A", "1, 2", new string[0], "/Objects/A", "1, 2")]
    [InlineData(null, null, new[] { "/Objects/Name=x=y" }, "/Objects/Name", "x=y")]
    public void Requests_come_from_arguments_or_set(string? node, string? value, string[] sets, string expectedNode, string expectedValue)
    {
        var request = Assert.Single(Commands.WriteRequests(node, value, sets));
        Assert.Equal((expectedNode, expectedValue), request);
    }

    [Fact]
    public void Missing_value_or_nothing_to_write_is_explained()
    {
        Assert.Contains("Give the new value", Assert.Throws<CliException>(() => Commands.WriteRequests("/Objects/A", null, [])).Message, StringComparison.Ordinal);
        Assert.Contains("Nothing to write", Assert.Throws<CliException>(() => Commands.WriteRequests(null, null, [])).Message, StringComparison.Ordinal);
        Assert.Contains("is not node=value", Assert.Throws<CliException>(() => Commands.WriteRequests(null, null, ["oops"])).Message, StringComparison.Ordinal);
    }
}
