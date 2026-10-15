using System.ComponentModel;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using MachineDataBrowser.Driver.OpcUa;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// Changes to machines, only with <c>--allow-writes</c>: writing a value, writing a snapshot back and calling a method.
/// Every change is put to the user for confirmation through MCP elicitation first (unless
/// <c>--skip-write-confirmation</c>), checked against the <see cref="WritePolicy"/> before that, and recorded in the
/// <see cref="AuditLog"/>; endpoints named under an allow pattern and sessions marked read-only are never changed.
/// </summary>
internal sealed class WriteTools(EndpointPool pool, bool skipConfirmation, WritePolicy policy, AuditLog audit, SnapshotStore snapshots)
{
    public const int MaxRestoreItems = 200;

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
        var log = (string outcome, JsonNode? before, JsonNode? after, string? detail) =>
            audit.Add("write", target.Url, node, variable.DisplayId, value, outcome, before, after, detail);
        Checked(() => policy.Check(Names(node, variable), value), detail => log("refused", null, null, detail));

        var before = (await client.ReadValuesAsync([variable.Id], cancellationToken).ConfigureAwait(false))[0];
        await Confirmed(() => ConfirmAsync(server, $"Write {value} to {variable.Name} ({variable.DisplayId}) on {target.Url}? It is {Text(before)} now.", cancellationToken),
            () => log("declined", ValueJson.ToJson(before), null, null)).ConfigureAwait(false);

        try
        {
            await client.WriteValueAsync(variable.Id, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceClientException or FormatException or NotSupportedException or ArgumentException)
        {
            log("failed", ValueJson.ToJson(before), null, ex.Message);
            throw;
        }

        var after = (await client.ReadValuesAsync([variable.Id], cancellationToken).ConfigureAwait(false))[0];
        log("written", ValueJson.ToJson(before), ValueJson.ToJson(after), null);
        return new JsonObject
        {
            ["node"] = variable.Name,
            ["id"] = variable.DisplayId,
            ["before"] = ValueJson.ToJson(before),
            ["after"] = ValueJson.ToJson(after),
            ["written"] = true,
        };
    });

    [McpServerTool(Name = "restore_snapshot", Destructive = true, ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Writes a saved snapshot's values back to the machine (a parameter set), after the user confirms the list of "
        + "changes. Only items whose value differs now and was Good in the snapshot are written; returns per item before, "
        + "written and after (read back). Use compare_snapshot first to see what would change.")]
    public Task<string> RestoreSnapshotAsync(
        McpServer server,
        [Description("Snapshot name (from list_snapshots), or a unique start of it")] string snapshot,
        [Description("Only these items: names or paths as in compare_snapshot; default every changed item")] string[]? items = null,
        [Description("Endpoint URL from list_endpoints; default the snapshot's endpoint when configured, else the only one")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var saved = SnapshotTools.Find(snapshots, snapshot);
        endpoint ??= SnapshotTools.EndpointFor(pool, saved);
        var target = Writable(endpoint);
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var now = await SnapshotTools.ReadLiveAsync(client, saved.Items, cancellationToken).ConfigureAwait(false);
        var rows = SnapshotDiff.Compare(saved with { Partial = true }, now).Where(r => r.CanRestore).ToList();
        if (items is { Length: > 0 })
        {
            var wanted = items.Select(i => i.Trim().TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows = [.. rows.Where(r => wanted.Contains(r.Name) || saved.Items.First(i => i.NodeId == r.NodeId).Path is { } p && wanted.Contains(p))];
        }

        if (rows.Count == 0)
        {
            return new JsonObject { ["snapshot"] = saved.Name, ["written"] = 0, ["note"] = "Nothing to write: every item already has its snapshot value, or wasn't Good in the snapshot." };
        }

        if (rows.Count > MaxRestoreItems)
        {
            throw new McpException($"{rows.Count} items would change; at most {MaxRestoreItems} per call. Pass 'items' to restore part of the snapshot.");
        }

        var paths = saved.Items.ToDictionary(i => i.NodeId, i => i.Path, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var names = new[] { paths[row.NodeId] ?? string.Empty, row.NodeId, client.ToDisplayId(client.ParsePortableId(row.NodeId)) };
            Checked(() => policy.Check(names, row.Before), detail => audit.Add("restore_snapshot", target.Url, paths[row.NodeId] ?? row.Name, row.NodeId, row.Before, "refused", JsonValue.Create(row.After), null, detail));
        }

        var elsewhere = saved.EndpointUrl.Length > 0 && !string.Equals(saved.EndpointUrl.TrimEnd('/'), target.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? $" The snapshot was taken on {saved.EndpointUrl}." : string.Empty;
        var list = string.Join("; ", rows.Take(20).Select(r => $"{r.Name}: {r.After} → {r.Before}")) + (rows.Count > 20 ? $"; … and {rows.Count - 20} more" : string.Empty);
        await Confirmed(() => ConfirmAsync(server, $"Write {rows.Count} value(s) from snapshot '{saved.Name}' to {target.Url}?{elsewhere} {list}", cancellationToken),
            () =>
            {
                foreach (var row in rows)
                {
                    audit.Add("restore_snapshot", target.Url, paths[row.NodeId] ?? row.Name, row.NodeId, row.Before, "declined", JsonValue.Create(row.After));
                }
            }).ConfigureAwait(false);

        var results = new JsonArray();
        var failed = 0;
        foreach (var row in rows)
        {
            var id = client.ParsePortableId(row.NodeId);
            string? error = null;
            try
            {
                await client.WriteValueAsync(id, row.Before, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DeviceClientException or FormatException or NotSupportedException or ArgumentException)
            {
                error = ex.Message;
                failed++;
            }

            var after = error is null ? (await client.ReadValuesAsync([id], cancellationToken).ConfigureAwait(false))[0] : null;
            audit.Add("restore_snapshot", target.Url, paths[row.NodeId] ?? row.Name, row.NodeId, row.Before, error is null ? "written" : "failed",
                JsonValue.Create(row.After), error is null ? ValueJson.ToJson(after) : null, error);
            results.Add(new JsonObject
            {
                ["name"] = row.Name,
                ["before"] = row.After,
                ["written"] = row.Before,
                ["after"] = error is null ? ValueJson.ToJson(after) : null,
                ["error"] = error,
            });
        }

        return new JsonObject { ["snapshot"] = saved.Name, ["written"] = rows.Count - failed, ["failed"] = failed, ["items"] = results };
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
        var arguments = inputs ?? [];
        var described = string.Join(", ", arguments);
        Checked(() => policy.CheckMethod(Names(method, methodNode)),
            detail => audit.Add("call_method", target.Url, method, methodNode.DisplayId, described, "refused", detail: detail));
        var owner = @object ?? (method.StartsWith('/') && method.TrimEnd('/').LastIndexOf('/') > 0 ? method.TrimEnd('/')[..method.TrimEnd('/').LastIndexOf('/')] : null)
            ?? throw new McpException("Pass 'object' (the object the method belongs to) when the method is given by id.");
        var objectNode = await Connection.ResolveAsync(client, owner, cancellationToken).ConfigureAwait(false);
        var signature = await caller.GetMethodSignatureAsync(methodNode.Id, cancellationToken).ConfigureAwait(false);
        if (arguments.Length != signature.Inputs.Count)
        {
            throw new McpException($"{methodNode.Name} takes {signature.Inputs.Count} input(s): "
                + string.Join(", ", signature.Inputs.Select(i => $"{i.Name} ({i.DataType}{(i.IsArray && !i.DataType.EndsWith("[]", StringComparison.Ordinal) ? "[]" : string.Empty)})")) + ".");
        }

        described = string.Join(", ", signature.Inputs.Select((input, i) => $"{input.Name} = {arguments[i]}"));
        await Confirmed(() => ConfirmAsync(server, $"Call {methodNode.Name} on {objectNode.Name} on {target.Url}{(described.Length > 0 ? $" with {described}" : string.Empty)}?", cancellationToken),
            () => audit.Add("call_method", target.Url, method, methodNode.DisplayId, described, "declined")).ConfigureAwait(false);

        IReadOnlyList<string> outputs;
        try
        {
            outputs = await caller.CallMethodAsync(objectNode.Id, methodNode.Id, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceClientException or FormatException or NotSupportedException or ArgumentException)
        {
            audit.Add("call_method", target.Url, method, methodNode.DisplayId, described, "failed", detail: ex.Message);
            throw;
        }

        var result = new JsonObject([.. signature.Outputs.Select((output, i) =>
            System.Collections.Generic.KeyValuePair.Create(output.Name, (JsonNode?)(i < outputs.Count ? outputs[i] : null)))]);
        audit.Add("call_method", target.Url, method, methodNode.DisplayId, described, "called", after: result.DeepClone());
        return new JsonObject
        {
            ["method"] = methodNode.Name,
            ["object"] = objectNode.Name,
            ["outputs"] = result,
        };
    });

    /// <summary>What a node may be matched by: its path from the root when it was given as one, and its id.</summary>
    private static string[] Names(string text, Node node) => text.StartsWith('/') ? [text, node.DisplayId] : [node.DisplayId, text];

    private static void Checked(Action check, Action<string> refused)
    {
        try
        {
            check();
        }
        catch (McpException ex)
        {
            refused(ex.Message);
            throw;
        }
    }

    private static async Task Confirmed(Func<Task> confirm, Action declined)
    {
        try
        {
            await confirm().ConfigureAwait(false);
        }
        catch (McpException)
        {
            declined();
            throw;
        }
    }

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

    private static string Text(object? value) => value is null ? "unreadable" : ValueFormatter.Format(value);

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
        catch (DeviceClientException ex)
        {
            throw new McpException($"{ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            throw new McpException(ex.Message);
        }
    }
}
