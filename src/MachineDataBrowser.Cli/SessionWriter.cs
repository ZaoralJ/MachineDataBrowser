using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Cli;

/// <summary>
/// Writes app session files (<c>.mdbsession</c>) the app opens like its own: the endpoint and its options (never a
/// password) and watch items as portable ids with name, parent path and refresh time. Adding to an existing file only
/// appends to its watch list; everything else in it (bookmarks, columns, display formats, …) is kept as it is.
/// </summary>
internal static class SessionWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public sealed record Item(Node Node, int? RefreshMs);

    public sealed record Result(int Added, int AlreadyThere, int Total);

    public static async Task<Result> CreateAsync(string path, ConnectionArgs connection, int? defaultRefreshMs, IDeviceClient client, IReadOnlyList<Item> items, bool force, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !force)
        {
            throw new CliException($"'{path}' exists. Use 'mdbrowser session add' to add to it, or --force to replace it.");
        }

        var document = new JsonObject
        {
            ["version"] = 1,
            ["endpointUrl"] = connection.Url,
            ["useSecurity"] = connection.Secure,
            ["readOnly"] = false,
            ["autoAcceptCertificates"] = connection.TrustAll,
            ["userName"] = string.IsNullOrWhiteSpace(connection.User) ? null : connection.User,
            ["defaultRefreshMs"] = defaultRefreshMs,
            ["watch"] = new JsonArray(),
            ["bookmarks"] = new JsonArray(),
            ["groupWatchByPath"] = false,
        };
        var result = await AppendAsync(document, client, items, defaultRefreshMs, cancellationToken).ConfigureAwait(false);
        await SaveAsync(path, document, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static async Task<Result> AddAsync(string path, JsonObject document, IDeviceClient client, IReadOnlyList<Item> items, CancellationToken cancellationToken)
    {
        var defaultRefreshMs = document["defaultRefreshMs"]?.GetValue<int?>();
        var result = await AppendAsync(document, client, items, defaultRefreshMs, cancellationToken).ConfigureAwait(false);
        await SaveAsync(path, document, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Makes the file's watch list exactly <paramref name="items"/>: a new file is created; in an existing one, rows still
    /// watched keep their own settings (display format, monitoring, …), the rest of the file is kept, others are removed.
    /// </summary>
    public static async Task<Result> SaveWatchAsync(string path, ConnectionArgs connection, int? defaultRefreshMs, IDeviceClient client, IReadOnlyList<Item> items, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return await CreateAsync(path, connection, defaultRefreshMs, client, items, force: false, cancellationToken).ConfigureAwait(false);
        }

        var document = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var keep = items.Select(i => client.ToPortableId(i.Node.Id)).ToHashSet(StringComparer.Ordinal);
        if (document["watch"] is JsonArray watch)
        {
            foreach (var entry in watch.Where(e => !keep.Contains((string?)e?["nodeId"] ?? string.Empty)).ToList())
            {
                watch.Remove(entry);
            }
        }

        return await AddAsync(path, document, client, items, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes watch items from the file without connecting: a node matches by its path and name as shown
    /// (<c>/Objects/Line1/Speed</c>) or just its name (<c>Speed</c>; every item with that name), its saved id, or the end of it
    /// (<c>s=Speed</c>). With <paramref name="recursive"/>, a folder removes every item below it. Every node must match
    /// something, else nothing is written; everything else in the file is kept.
    /// </summary>
    public static async Task<(int Removed, int Total)> RemoveAsync(string path, IReadOnlyList<string> nodes, bool recursive, CancellationToken cancellationToken)
    {
        var document = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var watch = document["watch"] as JsonArray ?? [];
        var remove = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            var matches = watch.OfType<JsonObject>().Where(e => Matches(e, node, recursive)).ToList();
            if (matches.Count == 0)
            {
                throw new CliException($"'{node}' is not in the watch list of {path}; nothing was removed." +
                    (recursive ? string.Empty : " A folder needs -R."));
            }

            remove.UnionWith(matches);
        }

        foreach (var entry in remove)
        {
            watch.Remove(entry);
        }

        await SaveAsync(path, document, cancellationToken).ConfigureAwait(false);
        return (remove.Count, watch.Count);
    }

    private static bool Matches(JsonObject entry, string node, bool recursive)
    {
        var id = (string?)entry["nodeId"] ?? string.Empty;
        var name = (string?)entry["displayName"] ?? string.Empty;
        var parent = ((string?)entry["path"] ?? string.Empty).Trim('/');
        var full = parent.Length == 0 ? name : $"{parent}/{name}";
        var wanted = node.Trim('/');
        return id == node
            || id.EndsWith(";" + node, StringComparison.Ordinal)
            || string.Equals(full, wanted, StringComparison.Ordinal)
            || string.Equals(name, node, StringComparison.Ordinal)
            || (recursive && wanted.Length > 0 && (parent == wanted || parent.StartsWith(wanted + "/", StringComparison.Ordinal)));
    }

    /// <summary>An existing session file as JSON, so adding to it keeps every field, including ones the CLI doesn't know.</summary>
    public static async Task<JsonObject> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"Session file '{path}' not found; create it with 'mdbrowser session create'.");
        }

        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new CliException($"'{path}' is not a session file.");
        }
        catch (JsonException ex)
        {
            throw new CliException($"'{path}' is not a session file: {ex.Message}");
        }
    }

    private static async Task<Result> AppendAsync(JsonObject document, IDeviceClient client, IReadOnlyList<Item> items, int? defaultRefreshMs, CancellationToken cancellationToken)
    {
        if (document["watch"] is not JsonArray watch)
        {
            document["watch"] = watch = [];
        }

        var existing = watch.Select(e => (string?)e?["nodeId"]).OfType<string>().ToHashSet(StringComparer.Ordinal);
        int added = 0, already = 0;
        foreach (var (node, refreshMs) in items)
        {
            var id = client.ToPortableId(node.Id);
            if (!existing.Add(id))
            {
                already++;
                continue;
            }

            var entry = new JsonObject
            {
                ["nodeId"] = id,
                ["displayName"] = node.DisplayName ?? await DisplayNameAsync(client, node, cancellationToken).ConfigureAwait(false),
            };
            if (refreshMs is { } ms && ms != defaultRefreshMs)
            {
                entry["refreshMs"] = ms;
            }

            if (!string.IsNullOrEmpty(node.ParentPath))
            {
                entry["path"] = node.ParentPath;
            }

            watch.Add(entry);
            added++;
        }

        return new Result(added, already, watch.Count);
    }

    /// <summary>
    /// A node given by id has no name yet: ask the device, which also checks that the node exists, so a typo is caught
    /// now and not when the session is opened. Falls back to the id when the device doesn't report a name.
    /// </summary>
    private static async Task<string> DisplayNameAsync(IDeviceClient client, Node node, CancellationToken cancellationToken)
    {
        IReadOnlyList<AttributeValue> attributes;
        try
        {
            attributes = await client.ReadAttributesAsync(node.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Opc.Ua.ServiceResultException ex) when (Opc.Ua.StatusCode.IsBad(ex.StatusCode))
        {
            throw new CliException($"'{node.Name}' is not on the device ({StatusText.Of(ex.Result.StatusCode)}); nothing was saved.");
        }

        if (attributes.Count == 0 || attributes.Any(a => a.Name == "NodeClass" && a.Value.StartsWith("Bad", StringComparison.Ordinal)))
        {
            throw new CliException($"'{node.Name}' is not on the device; nothing was saved.");
        }

        return attributes.FirstOrDefault(a => a.Name == "DisplayName")?.Value is { Length: > 0 } name && !name.StartsWith("Bad", StringComparison.Ordinal) ? name : node.Name;
    }

    /// <summary>Through a temporary file, so an existing session is never left half written.</summary>
    private static async Task SaveAsync(string path, JsonObject document, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        await File.WriteAllTextAsync(temp, document.ToJsonString(Indented), cancellationToken).ConfigureAwait(false);
        File.Move(temp, full, overwrite: true);
    }
}
