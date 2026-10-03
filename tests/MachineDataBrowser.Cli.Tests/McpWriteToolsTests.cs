using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>write and call_method through a real <c>mdbrowser mcp</c> process, with the user's answer simulated by the client.</summary>
public sealed class McpWriteToolsTests(CustomTypesServerFixture custom) : IDisposable
{
    private const string Int32Node = "/Objects/Custom/DataTypes/Int32";
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-mcp-writes").FullName;
    private readonly List<McpClient> _clients = [];

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A client whose user answers confirmations with <paramref name="confirm"/>; null = a client that can't ask.</summary>
    private async Task<McpClient> StartAsync(bool? confirm, params string[] args)
    {
        var options = new McpClientOptions();
        var asked = new List<string>();
        if (confirm is { } answer)
        {
            options.Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() };
            options.Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    Questions.Add(request?.Message ?? string.Empty);
                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(answer) },
                    });
                },
            };
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mdbrowser",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "mdbrowser.dll"), "mcp", .. args],
            EnvironmentVariables = new Dictionary<string, string?> { ["MACHINEDATABROWSER_DATA_DIR"] = _dir },
        });
        var client = await McpClient.CreateAsync(transport, options, cancellationToken: TestContext.Current.CancellationToken);
        _clients.Add(client);
        return client;
    }

    private List<string> Questions { get; } = [];

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        return (result.IsError ?? false, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private async Task<long> ReadInt32Async()
    {
        var client = await StartAsync(null, "--endpoint", custom.EndpointUrl);
        var (_, text) = await CallAsync(client, "read", new() { ["nodes"] = new[] { Int32Node } });
        return (long)JsonNode.Parse(text)!["values"]![0]!["value"]!;
    }

    [Fact]
    public async Task Without_allow_writes_there_are_no_write_tools()
    {
        var client = await StartAsync(true, "--endpoint", custom.EndpointUrl);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tools, t => t.Name is "write" or "call_method");
    }

    [Fact]
    public async Task A_confirmed_write_changes_the_value_and_a_declined_one_does_not()
    {
        var yes = await StartAsync(true, "--endpoint", custom.EndpointUrl, "--allow-writes");
        var tools = await yes.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(tools.Single(t => t.Name == "write").ProtocolTool.Annotations?.DestructiveHint);

        var (error, text) = await CallAsync(yes, "write", new() { ["node"] = Int32Node, ["value"] = "4711" });
        Assert.False(error, text);
        Assert.Equal(4711, (long)JsonNode.Parse(text)!["after"]!);
        Assert.Contains(Questions, q => q.Contains("Write 4711 to Int32", StringComparison.Ordinal));

        var no = await StartAsync(false, "--endpoint", custom.EndpointUrl, "--allow-writes");
        (error, text) = await CallAsync(no, "write", new() { ["node"] = Int32Node, ["value"] = "1" });
        Assert.True(error);
        Assert.Contains("did not confirm", text, StringComparison.Ordinal);
        Assert.Equal(4711, await ReadInt32Async());
    }

    [Fact]
    public async Task Clients_that_cannot_ask_are_refused_unless_confirmation_is_skipped()
    {
        var cannotAsk = await StartAsync(null, "--endpoint", custom.EndpointUrl, "--allow-writes");
        var (error, text) = await CallAsync(cannotAsk, "write", new() { ["node"] = Int32Node, ["value"] = "5" });
        Assert.True(error);
        Assert.Contains("--skip-write-confirmation", text, StringComparison.Ordinal);

        var skipping = await StartAsync(null, "--endpoint", custom.EndpointUrl, "--allow-writes", "--skip-write-confirmation");
        (error, text) = await CallAsync(skipping, "write", new() { ["node"] = Int32Node, ["value"] = "6" });
        Assert.False(error, text);
        Assert.Equal(6, await ReadInt32Async());
    }

    [Fact]
    public async Task Methods_are_called_with_their_arguments_after_confirmation()
    {
        var client = await StartAsync(true, "--endpoint", custom.EndpointUrl, "--allow-writes");

        var (error, text) = await CallAsync(client, "call_method", new() { ["method"] = "/Objects/Custom/Methods/Stats", ["inputs"] = new[] { "[3, 8, 1]" } });
        Assert.False(error, text);
        var outputs = JsonNode.Parse(text)!["outputs"]!;
        Assert.Equal(("1", "8", "4"), ((string)outputs["Min"]!, (string)outputs["Max"]!, (string)outputs["Mean"]!));

        (error, text) = await CallAsync(client, "call_method", new() { ["method"] = "/Objects/Custom/Methods/Stats", ["inputs"] = Array.Empty<string>() });
        Assert.True(error);
        Assert.Contains("takes 1 input(s): Values (Double[])", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_sessions_and_named_endpoints_are_never_changed()
    {
        var session = Path.Combine(_dir, "readonly.mdbsession");
        await File.WriteAllTextAsync(session, $$"""{"endpointUrl":"{{custom.EndpointUrl}}","readOnly":true,"watch":[]}""", TestContext.Current.CancellationToken);
        var readOnly = await StartAsync(true, "--session", session, "--allow-writes");
        var (error, text) = await CallAsync(readOnly, "write", new() { ["node"] = Int32Node, ["value"] = "1" });
        Assert.True(error);
        Assert.Contains("read-only", text, StringComparison.Ordinal);

        var named = await StartAsync(true, "--endpoint", "opc.tcp://localhost:1", "--allow", "opc.tcp://localhost:*", "--allow-writes");
        (error, text) = await CallAsync(named, "write", new() { ["node"] = Int32Node, ["value"] = "1", ["endpoint"] = custom.EndpointUrl });
        Assert.True(error);
        Assert.Contains("allow pattern", text, StringComparison.Ordinal);
        Assert.Empty(Questions);   // refused before anyone was asked
    }
}
