using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// Snapshots the app saves (Watch ▸ Take Snapshot, folder snapshots, imported parameter sets), for the agent: which exist,
/// and how the machine differs from one now or how two differ. Read-only; taking snapshots is
/// <see cref="SnapshotTakeTools"/> and writing them back is <see cref="WriteTools.RestoreSnapshotAsync"/>.
/// </summary>
internal sealed class SnapshotTools(EndpointPool pool, SnapshotStore snapshots)
{
    public const int MaxCompareRows = 500;

    [McpServerTool(Name = "list_snapshots", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Saved snapshots of machine values (taken in the app or with take_snapshot), newest first: name, time, endpoint, "
        + "number of items, and whether it covers only part of a watch list (a folder, a selection or an imported parameter set).")]
    public string ListSnapshots() => new JsonObject
    {
        ["folder"] = snapshots.Folder,
        ["snapshots"] = new JsonArray([.. snapshots.List().Select(s => (JsonNode)new JsonObject
        {
            ["name"] = s.Name,
            ["takenAt"] = s.TakenAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["endpoint"] = s.EndpointUrl,
            ["items"] = s.Items.Count,
            ["partial"] = s.Partial,
        })]),
    }.ToJsonString();

    [McpServerTool(Name = "compare_snapshot", ReadOnly = true, OpenWorld = false)]
    [Description("Compares a saved snapshot with the machine now (read from the device) or with another snapshot: per item the "
        + "value before and after, the change (numeric difference, 'changed', 'only before', 'only after') and statuses. "
        + "Answers 'what changed since …' and shows what restore_snapshot would write.")]
    public Task<string> CompareSnapshotAsync(
        [Description("Snapshot name (from list_snapshots), or a unique start of it")] string snapshot,
        [Description("Another snapshot to compare with instead of the machine now")] string? with = null,
        [Description("Only items that differ")] bool changedOnly = true,
        [Description("Endpoint URL to read now from; default the snapshot's endpoint when configured, else the only one")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var before = Find(snapshots, snapshot);
        Snapshot after;
        if (with is not null)
        {
            after = Find(snapshots, with);
        }
        else
        {
            var client = await pool.ConnectAsync(endpoint ?? EndpointFor(pool, before), cancellationToken).ConfigureAwait(false);
            after = await ReadLiveAsync(client, before.Items, cancellationToken).ConfigureAwait(false);
        }

        // Against the machine, only the snapshot's items are read; nothing else counts as "only after".
        var all = SnapshotDiff.Compare(with is null ? before with { Partial = true } : before, after);
        var rows = changedOnly ? all.Where(r => r.IsChanged).ToList() : [.. all];
        var paths = before.Items.Concat(after.Items).GroupBy(i => i.NodeId).ToDictionary(g => g.Key, g => g.Select(i => i.Path).FirstOrDefault(p => p is not null), StringComparer.Ordinal);
        return new JsonObject
        {
            ["before"] = before.Name,
            ["after"] = with is null ? "now" : after.Name,
            ["changed"] = all.Count(r => r.Change == SnapshotChange.Changed),
            ["same"] = all.Count(r => r.Change == SnapshotChange.Same),
            ["onlyBefore"] = all.Count(r => r.Change == SnapshotChange.Removed),
            ["onlyAfter"] = all.Count(r => r.Change == SnapshotChange.Added),
            ["restorable"] = with is null ? all.Count(r => r.CanRestore) : null,
            ["items"] = new JsonArray([.. rows.Take(MaxCompareRows).Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name,
                ["path"] = paths.GetValueOrDefault(r.NodeId),
                ["id"] = r.NodeId,
                ["before"] = r.Before,
                ["after"] = r.After,
                ["change"] = r.Change == SnapshotChange.Same ? "same" : r.ChangeText,
                ["beforeStatus"] = r.BeforeStatus,
                ["afterStatus"] = r.AfterStatus,
            })]),
            ["truncated"] = rows.Count > MaxCompareRows,
        };
    });

    /// <summary>A snapshot by exact name (newest when several share it) or a unique start of a name.</summary>
    public static Snapshot Find(SnapshotStore store, string name)
    {
        var all = store.List();
        if (all.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return exact;
        }

        var starting = all.Where(s => s.Name.StartsWith(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return starting.Count switch
        {
            1 => starting[0],
            0 => throw new McpException(all.Count == 0
                ? "There are no snapshots yet (Watch ▸ Take Snapshot in the app, or take_snapshot)."
                : $"No snapshot named '{name}'. Use list_snapshots."),
            _ => throw new McpException($"'{name}' matches {starting.Count} snapshots ({string.Join(", ", starting.Take(5).Select(s => s.Name))}); use the full name."),
        };
    }

    /// <summary>The snapshot's own endpoint when it is configured; otherwise null, so the only configured one is used.</summary>
    public static string? EndpointFor(EndpointPool pool, Snapshot snapshot) =>
        pool.Endpoints.FirstOrDefault(e => string.Equals(e.Url.TrimEnd('/'), snapshot.EndpointUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.Url;

    public static Task<Snapshot> ReadLiveAsync(IDeviceClient client, IReadOnlyList<SnapshotItem> items, CancellationToken cancellationToken)
    {
        if (items.Count > MachineDataTools.MaxSnapshotItems)
        {
            throw new McpException($"The snapshot has {items.Count} items; at most {MachineDataTools.MaxSnapshotItems} are read from a machine at once.");
        }

        return SnapshotReader.ReadAsync(client, items, cancellationToken);
    }

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
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
    }
}

/// <summary>
/// take_snapshot, only with <c>--allow-snapshots</c>: it saves a file into the snapshot folder (shared with the app), so
/// it is not offered by the otherwise read-only server unless asked for. Nothing on the machine changes.
/// </summary>
internal sealed class SnapshotTakeTools(EndpointPool pool, SnapshotStore snapshots)
{
    [McpServerTool(Name = "take_snapshot", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Reads variables once and saves their values as a named snapshot (also shown in the app), e.g. before a change on "
        + "the machine, to compare with later (compare_snapshot) or to write back (restore_snapshot). Folders and structures "
        + "expand to the variables below them.")]
    public Task<string> TakeSnapshotAsync(
        [Description("Name for the snapshot, e.g. 'Line 1 before retooling'")] string name,
        [Description("Nodes: paths like /Objects/Line1/Settings (folders expand) or variables")] string[] nodes,
        [Description("Levels below each folder, 1-10")] int depth = 10,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new McpException("Give the snapshot a name.");
        }

        var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var warnings = new JsonArray();
        var resolved = await MachineDataTools.ResolveAsync(client, nodes, recursive: true, depth, MachineDataTools.MaxSnapshotItems, warnings, cancellationToken).ConfigureAwait(false);
        var values = await client.ReadValuesAsync([.. resolved.Select(n => n.Id)], cancellationToken).ConfigureAwait(false);
        var items = resolved.Select((n, i) => SnapshotReader.FromValue(n.Name, client.ToPortableId(n.Id), values[i], FullPath(n))).ToList();
        var saved = snapshots.Save(new Snapshot
        {
            Name = name.Trim(),
            TakenAt = DateTimeOffset.Now,
            EndpointUrl = pool.Find(endpoint).Url,
            Items = items,
            Partial = true,
        });
        return new JsonObject
        {
            ["snapshot"] = saved.Name,
            ["items"] = items.Count,
            ["notGood"] = items.Count(i => !i.Status.StartsWith("Good", StringComparison.Ordinal)),
            ["warnings"] = warnings,
        };
    });

    private static string? FullPath(Node node) => node.ParentPath is null ? null
        : $"/{(node.ParentPath.Length == 0 ? node.DisplayName ?? node.Name : $"{node.ParentPath}/{node.DisplayName ?? node.Name}")}";

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException(ex.Message);
        }
    }
}
