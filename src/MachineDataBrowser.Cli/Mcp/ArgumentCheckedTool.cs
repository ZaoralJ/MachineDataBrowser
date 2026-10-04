using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// Checks argument names against the tool's schema before the SDK binds them: its own binding errors reach the agent
/// only as "An error occurred invoking …", which gives it nothing to correct.
/// </summary>
internal sealed class ArgumentCheckedTool(McpServerTool inner) : DelegatingMcpServerTool(inner)
{
    private readonly string[] _known = [.. Names(inner.ProtocolTool.InputSchema, "properties")];
    private readonly string[] _required = [.. Names(inner.ProtocolTool.InputSchema, "required")];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var given = request.Params?.Arguments?.Keys.ToList() ?? [];
        var problems = new List<string>();
        var missing = _required.Where(r => !given.Contains(r, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            problems.Add($"missing required {Plural(missing)}");
        }

        var unknown = given.Where(g => !_known.Contains(g, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            problems.Add($"unknown {Plural(unknown)}");
        }

        if (problems.Count > 0)
        {
            throw new McpException($"{ProtocolTool.Name}: {string.Join("; ", problems)}. Arguments: {string.Join(", ", _known.Select(k => _required.Contains(k) ? $"{k} (required)" : k))}.");
        }

        return base.InvokeAsync(request, cancellationToken);
    }

    private static string Plural(List<string> names) =>
        $"argument{(names.Count == 1 ? string.Empty : "s")} {string.Join(", ", names.Select(n => $"'{n}'"))}";

    private static IEnumerable<string> Names(JsonElement schema, string property)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty(property, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name),
            JsonValueKind.Array => value.EnumerateArray().Select(e => e.GetString()).OfType<string>(),
            _ => [],
        };
    }
}
