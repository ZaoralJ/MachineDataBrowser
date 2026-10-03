using System.ComponentModel;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Opc.Ua;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// Changes to machines, only with <c>--allow-writes</c>: writing a value and calling a method. Every change is put to the
/// user for confirmation through MCP elicitation first (unless <c>--skip-write-confirmation</c>); endpoints named under
/// an allow pattern and sessions marked read-only are never changed.
/// </summary>
internal sealed class WriteTools(EndpointPool pool, bool skipConfirmation)
{
    [McpServerTool(Name = "write", Destructive = true, ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Writes a value to a variable on a machine, after the user confirms it. The value is text converted to the variable's "
        + "data type (numbers, true/false, text; arrays as [1, 2, 3]). Returns the value before and after. Only on configured endpoints.")]
    public Task<string> WriteAsync(
        McpServer server,
        [Description("Variable: a path like /Objects/Line1/Setpoint or an id")] string node,
        [Description("New value as text, e.g. 42.5, true, AUTO, [1, 2, 3]")] string value,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var target = Writable(endpoint);
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var variable = await Connection.ResolveAsync(client, node, cancellationToken).ConfigureAwait(false);
        var before = (await client.ReadValuesAsync([variable.Id], cancellationToken).ConfigureAwait(false))[0];
        await ConfirmAsync(server, $"Write {value} to {variable.Name} ({variable.DisplayId}) on {target.Url}? It is {Text(before)} now.", cancellationToken).ConfigureAwait(false);

        await client.WriteValueAsync(variable.Id, value, cancellationToken).ConfigureAwait(false);
        var after = (await client.ReadValuesAsync([variable.Id], cancellationToken).ConfigureAwait(false))[0];
        return new JsonObject
        {
            ["node"] = variable.Name,
            ["id"] = variable.DisplayId,
            ["before"] = ValueJson.ToJson(before),
            ["after"] = ValueJson.ToJson(after),
            ["written"] = true,
        };
    });

    [McpServerTool(Name = "call_method", Destructive = true, ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Calls an OPC UA method on the object it belongs to, after the user confirms it. Inputs are text in the order of the "
        + "method's input arguments (see attributes or browse). Returns the outputs. Only on configured endpoints.")]
    public Task<string> CallMethodAsync(
        McpServer server,
        [Description("The method: a path like /Objects/Line1/Reset or an id; it is called on its parent object")] string method,
        [Description("Input arguments as text, in order")] string[]? inputs = null,
        [Description("Object to call it on (path or id); default the method's parent in the path")] string? @object = null,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var target = Writable(endpoint);
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (client is not IMethodCaller caller)
        {
            throw new McpException("Methods can be called on OPC UA servers only.");
        }

        var methodNode = await Connection.ResolveAsync(client, method, cancellationToken).ConfigureAwait(false);
        var owner = @object ?? (method.StartsWith('/') && method.TrimEnd('/').LastIndexOf('/') > 0 ? method.TrimEnd('/')[..method.TrimEnd('/').LastIndexOf('/')] : null)
            ?? throw new McpException("Pass 'object' (the object the method belongs to) when the method is given by id.");
        var objectNode = await Connection.ResolveAsync(client, owner, cancellationToken).ConfigureAwait(false);
        var signature = await caller.GetMethodSignatureAsync(methodNode.Id, cancellationToken).ConfigureAwait(false);
        var arguments = inputs ?? [];
        if (arguments.Length != signature.Inputs.Count)
        {
            throw new McpException($"{methodNode.Name} takes {signature.Inputs.Count} input(s): "
                + string.Join(", ", signature.Inputs.Select(i => $"{i.Name} ({i.DataType}{(i.IsArray && !i.DataType.EndsWith("[]", StringComparison.Ordinal) ? "[]" : string.Empty)})")) + ".");
        }

        var described = string.Join(", ", signature.Inputs.Select((input, i) => $"{input.Name} = {arguments[i]}"));
        await ConfirmAsync(server, $"Call {methodNode.Name} on {objectNode.Name} on {target.Url}{(described.Length > 0 ? $" with {described}" : string.Empty)}?", cancellationToken).ConfigureAwait(false);

        var outputs = await caller.CallMethodAsync(objectNode.Id, methodNode.Id, arguments, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["method"] = methodNode.Name,
            ["object"] = objectNode.Name,
            ["outputs"] = new JsonObject([.. signature.Outputs.Select((output, i) =>
                System.Collections.Generic.KeyValuePair.Create(output.Name, (JsonNode?)(i < outputs.Count ? outputs[i] : null)))]),
        };
    });

    /// <summary>Only endpoints configured with --endpoint/--session that aren't read-only may be changed.</summary>
    private ConnectionArgs Writable(string? endpoint)
    {
        var target = pool.Find(endpoint);
        if (target.Named)
        {
            throw new McpException($"{target.Url} was named under an allow pattern; changes are only allowed on endpoints configured with --endpoint or --session.");
        }

        return target.ReadOnly
            ? throw new McpException($"The session for {target.Url} is read-only; nothing is written or called there.")
            : target;
    }

    /// <summary>Asks the user (MCP elicitation) and throws unless they accept.</summary>
    private async Task ConfirmAsync(McpServer server, string question, CancellationToken cancellationToken)
    {
        if (server.ClientCapabilities?.Elicitation is null)
        {
            if (skipConfirmation)
            {
                return;
            }

            throw new McpException("This client can't ask the user to confirm changes (MCP elicitation), so nothing was changed. "
                + "Start the server with --skip-write-confirmation to rely on the client's own tool approval instead.");
        }

        var answer = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = question,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["confirm"] = new ElicitRequestParams.BooleanSchema { Description = "Make this change", Default = false },
                },
                Required = ["confirm"],
            },
        }, cancellationToken).ConfigureAwait(false);

        var confirmed = answer.IsAccepted && answer.Content?.TryGetValue("confirm", out var value) == true && value.ValueKind == System.Text.Json.JsonValueKind.True;
        if (!confirmed)
        {
            throw new McpException("The user did not confirm; nothing was changed.");
        }
    }

    private static string Text(object? value) => value is null ? "unreadable" : ValueFormatter.Format(new Variant(value));

    private static async Task<string> Guard(Func<Task<JsonNode>> tool)
    {
        try
        {
            return (await tool().ConfigureAwait(false)).ToJsonString();
        }
        catch (CliException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (ServiceResultException ex)
        {
            throw new McpException($"{StatusText.Of(ex.Result.StatusCode)}: {ex.Message}");
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            throw new McpException(ex.Message);
        }
    }
}
