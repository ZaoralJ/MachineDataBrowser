using System.Globalization;
using System.Text.Json;

namespace OpcUaBrowser.App.Services;

/// <summary>The watch values at one moment, saved to compare later (e.g. before and after a machine change).</summary>
public sealed record Snapshot
{
    public required string Name { get; init; }

    public required DateTimeOffset TakenAt { get; init; }

    public string EndpointUrl { get; init; } = string.Empty;

    public IReadOnlyList<SnapshotItem> Items { get; init; } = [];

    /// <summary>File the snapshot was loaded from or saved to; not stored in the file.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Path { get; init; }

    public override string ToString() => $"{Name} · {Items.Count} item(s)";
}

/// <summary>One watched value in a <see cref="Snapshot"/>; items are matched by their portable <see cref="NodeId"/>.</summary>
public sealed record SnapshotItem(string Name, string NodeId, string Value, string Status, double? Numeric);

/// <summary>
/// Snapshots as JSON files in a folder (the data folder's <c>snapshots</c>), newest first. Without a folder they are
/// kept in memory only (tests, or no settings store).
/// </summary>
public sealed class SnapshotStore(string? folder)
{
    private readonly List<Snapshot> _memory = [];

    public string? Folder { get; } = folder;

    public IReadOnlyList<Snapshot> List()
    {
        if (Folder is null)
        {
            return [.. _memory.OrderByDescending(s => s.TakenAt)];
        }

        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var snapshots = new List<Snapshot>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                if (JsonSerializer.Deserialize(stream, AppJsonContext.Default.Snapshot) is { } snapshot)
                {
                    snapshots.Add(snapshot with { Path = file });
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                AppErrors.Log(ex, $"reading snapshot {file}");
            }
        }

        return [.. snapshots.OrderByDescending(s => s.TakenAt)];
    }

    public Snapshot Save(Snapshot snapshot)
    {
        if (Folder is null)
        {
            _memory.Add(snapshot);
            return snapshot;
        }

        Directory.CreateDirectory(Folder);
        var path = System.IO.Path.Combine(Folder, snapshot.TakenAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json");
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, snapshot, AppJsonContext.Default.Snapshot);
        }

        File.Move(temp, path, overwrite: true);
        return snapshot with { Path = path };
    }

    public void Delete(Snapshot snapshot)
    {
        if (Folder is null)
        {
            _memory.RemoveAll(s => s.TakenAt == snapshot.TakenAt && s.Name == snapshot.Name);
        }
        else if (snapshot.Path is { } path && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
