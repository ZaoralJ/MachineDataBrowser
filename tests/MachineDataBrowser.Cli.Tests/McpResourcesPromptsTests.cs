using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>generate_code, resources and prompts through a real <c>mdbrowser mcp</c> process.</summary>
public sealed class McpResourcesPromptsTests(CustomTypesServerFixture custom) : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-mcp-d").FullName;
    private McpClient _client = null!;

    private string Session => Path.Combine(_dir, "line1.mdbsession");

    public async ValueTask InitializeAsync()
    {
        await File.WriteAllTextAsync(Session, $$"""{"endpointUrl":"{{custom.EndpointUrl}}","watch":[{"nodeId":"ns=2;i=50","displayName":"Int32"}]}""", TestContext.Current.CancellationToken);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mdbrowser",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "mdbrowser.dll"), "mcp", "--session", Session],
            EnvironmentVariables = new Dictionary<string, string?> { ["MACHINEDATABROWSER_DATA_DIR"] = _dir },
        });
        _client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<(bool Error, string Text)> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        return (result.IsError ?? false, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Generate_code_mirrors_the_structure_as_csharp_or_json()
    {
        var (error, record) = await CallAsync("generate_code", new() { ["node"] = "/Objects/Custom/Structures" });
        Assert.False(error, record);
        Assert.Contains("public sealed record Structures", record, StringComparison.Ordinal);
        Assert.Contains("{ get; init; }", record, StringComparison.Ordinal);

        (error, var json) = await CallAsync("generate_code", new() { ["node"] = "/Objects/Custom/DataTypes", ["format"] = "json", ["depth"] = 1 });
        Assert.False(error, json);
        Assert.Equal(JsonValueKind.Object, JsonNode.Parse(json)!.GetValueKind());

        (error, var bad) = await CallAsync("generate_code", new() { ["node"] = "/Objects/Custom", ["format"] = "python" });
        Assert.True(error);
        Assert.Contains("format must be", bad, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resources_describe_the_machines_and_the_sessions()
    {
        var resources = await _client.ListResourcesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(resources, r => r.Uri == "mdbrowser://endpoints");
        Assert.DoesNotContain(resources, r => r.Uri == "mdbrowser://recordings");   // none configured

        var endpoints = JsonNode.Parse(Text(await _client.ReadResourceAsync("mdbrowser://endpoints", cancellationToken: TestContext.Current.CancellationToken)))!;
        Assert.Equal(custom.EndpointUrl, (string)endpoints["endpoints"]![0]!["endpoint"]!);
        Assert.Equal("mdbrowser://sessions/line1.mdbsession", (string)endpoints["sessions"]![0]!);

        var session = JsonNode.Parse(Text(await _client.ReadResourceAsync("mdbrowser://sessions/line1.mdbsession", cancellationToken: TestContext.Current.CancellationToken)))!;
        Assert.Equal("Int32", (string)session["watch"]![0]!["displayName"]!);
    }

    [Fact]
    public async Task Prompts_offer_workflows_for_machines()
    {
        var prompts = await _client.ListPromptsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["check_alarms", "diagnose_machine"], prompts.Select(p => p.Name).Order());   // no recordings configured

        var diagnose = await _client.GetPromptAsync("diagnose_machine", new Dictionary<string, object?> { ["area"] = "/Objects/Line1" }, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(diagnose.Messages).Content).Text;
        Assert.Contains("the area /Objects/Line1", text, StringComparison.Ordinal);
        Assert.Contains("Don't change anything", text, StringComparison.Ordinal);
    }

    private static string Text(ReadResourceResult result) => Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
}
