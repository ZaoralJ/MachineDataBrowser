using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>Waiting for a value: lets an agent follow a process ("tell me when the line runs") instead of polling.</summary>
internal sealed partial class MachineDataTools
{
    public const int MaxWaitSeconds = 600;

    [McpServerTool(Name = "wait_for", ReadOnly = true, OpenWorld = false)]
    [Description("Waits until a value meets a condition, or until the timeout, and returns whether it was met, the value and when. "
        + "Conditions: '== Run', '!= 0', '> 80', '>= 80', '< 10', '<= 10' (numbers compared as numbers), 'contains Error', or 'changes'. "
        + "If the condition already holds it returns at once.")]
    public Task<string> WaitForAsync(
        [Description("Variable: a path like /Objects/Line1/State or an id")] string node,
        [Description("Condition, e.g. '> 80', '== Run', 'changes'")] string condition,
        [Description("Give up after this many seconds, 1-600")] int timeoutSeconds = 60,
        [Description("Refresh time in ms (default 250; MQTT 0 = every message)")] int? refreshMs = null,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var test = WaitCondition.Parse(condition);
        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var target = await Connection.ResolveAsync(client, node, cancellationToken).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        var met = new TaskCompletionSource<ValueUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueUpdate? first = null;
        ValueUpdate? last = null;
        var gate = new Lock();
        void OnUpdate(ValueUpdate update)
        {
            lock (gate)
            {
                first ??= update;
                last = update;
                if (test.IsMet(update, first))
                {
                    met.TrySetResult(update);
                }
            }
        }

        var interval = refreshMs ?? (DeviceClient.IsMqtt(pool.Find(endpoint).Url) ? 0 : 250);
        var handle = await client.MonitorAsync(target.Id, OnUpdate, interval, cancellationToken).ConfigureAwait(false);
        ValueUpdate? hit = null;
        await using (handle.ConfigureAwait(false))
        {
            try
            {
                hit = await met.Task.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxWaitSeconds)), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Not met in time: reported below with the last value seen.
            }
        }

        ValueUpdate? seen;
        lock (gate)
        {
            seen = hit ?? last;
        }

        return new JsonObject
        {
            ["met"] = hit is not null,
            ["node"] = target.Name,
            ["condition"] = condition.Trim(),
            ["value"] = seen is null ? null : seen.Raw is null ? JsonValue.Create(seen.Value) : ValueJson.ToJson(seen.Raw),
            ["status"] = seen is null ? null : Output.Status(seen.Status),
            ["time"] = seen is null ? null : Iso(seen.SourceTimestamp == DateTime.MinValue ? DateTime.UtcNow : seen.SourceTimestamp),
            ["waitedSeconds"] = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
        };
    });
}

/// <summary>A condition on a value, as the agent writes it: an operator and an operand, or <c>changes</c>.</summary>
internal sealed record WaitCondition(string Operator, string Operand)
{
    private static readonly string[] Operators = [">=", "<=", "==", "!=", ">", "<"];

    public static WaitCondition Parse(string? text)
    {
        var condition = (text ?? string.Empty).Trim();
        if (condition.Equals("changes", StringComparison.OrdinalIgnoreCase))
        {
            return new WaitCondition("changes", string.Empty);
        }

        if (condition.StartsWith("contains ", StringComparison.OrdinalIgnoreCase))
        {
            return new WaitCondition("contains", condition[9..].Trim());
        }

        foreach (var op in Operators)
        {
            if (condition.StartsWith(op, StringComparison.Ordinal))
            {
                var operand = condition[op.Length..].Trim().Trim('\'', '"');
                return operand.Length > 0 ? new WaitCondition(op, operand) : throw Invalid(text);
            }
        }

        throw Invalid(text);
    }

    public bool IsMet(ValueUpdate update, ValueUpdate first)
    {
        var text = update.Value;
        switch (Operator)
        {
            case "changes":
                return !ReferenceEquals(update, first) && text != first.Value;
            case "contains":
                return text.Contains(Operand, StringComparison.OrdinalIgnoreCase);
        }

        var number = update.Numeric;
        if (number is { } value && double.TryParse(Operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit))
        {
            return Operator switch
            {
                "==" => value == limit,
                "!=" => value != limit,
                ">" => value > limit,
                ">=" => value >= limit,
                "<" => value < limit,
                _ => value <= limit,
            };
        }

        var compared = string.Compare(text, Operand, StringComparison.OrdinalIgnoreCase);
        return Operator switch
        {
            "==" => compared == 0,
            "!=" => compared != 0,
            ">" => compared > 0,
            ">=" => compared >= 0,
            "<" => compared < 0,
            _ => compared <= 0,
        };
    }

    private static McpException Invalid(string? text) =>
        new($"'{text}' is not a condition. Use '== Run', '!= 0', '> 80', '>= 80', '< 10', '<= 10', 'contains Error' or 'changes'.");
}
