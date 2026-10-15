using System.Globalization;
using System.Text.Json;

namespace MachineDataBrowser.App.Services;

/// <summary>The watch values at one moment, saved to compare later (e.g. before and after a machine change).</summary>
public sealed record Snapshot
{
    public required string Name { get; init; }

    public required DateTimeOffset TakenAt { get; init; }

    public string EndpointUrl { get; init; } = string.Empty;

    public IReadOnlyList<SnapshotItem> Items { get; init; } = [];

    /// <summary>
    /// Taken of selected rows only (a parameter set): other items in what it is compared with are left out instead of
    /// showing as "only after".
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>File the snapshot was loaded from or saved to; not stored in the file.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Path { get; init; }

    public override string ToString() => $"{Name} · {Items.Count} item(s){(Partial ? ", selection" : string.Empty)}";
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
            _memory.RemoveAll(s => s.TakenAt == snapshot.TakenAt);
            _memory.Add(snapshot);
            return snapshot;
        }

        Directory.CreateDirectory(Folder);
        var path = System.IO.Path.Combine(Folder, snapshot.TakenAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json");
        WriteFile(snapshot, path);
        return snapshot with { Path = path };
    }

    /// <summary>Writes a snapshot to a file of the user's choice, e.g. to share a parameter set.</summary>
    public static void Export(Snapshot snapshot, string path) => WriteFile(snapshot, path);

    /// <summary>
    /// Reads a snapshot file (exported here or elsewhere) and adds it to the store. Importing the same file again
    /// replaces the earlier copy, since snapshots are stored by the time they were taken.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a snapshot.</exception>
    public Snapshot Import(string path)
    {
        Snapshot? snapshot;
        try
        {
            using var stream = File.OpenRead(path);
            snapshot = JsonSerializer.Deserialize(stream, AppJsonContext.Default.Snapshot);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a snapshot: {ex.Message}", ex);
        }

        if (snapshot is null || snapshot.Items.Any(i => string.IsNullOrEmpty(i.NodeId)))
        {
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a snapshot (no items with NodeIds).");
        }

        return Save(snapshot);
    }

    private static void WriteFile(Snapshot snapshot, string path)
    {
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, snapshot, AppJsonContext.Default.Snapshot);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Gives a snapshot a meaningful name (e.g. "Line 1 – product A"); the file keeps its path, which comes from the
    /// time the snapshot was taken.
    /// </summary>
    public Snapshot Rename(Snapshot snapshot, string name)
    {
        var renamed = snapshot with { Name = name.Trim() };
        if (Folder is null)
        {
            _memory.RemoveAll(s => s.TakenAt == snapshot.TakenAt);
            _memory.Add(renamed);
            return renamed;
        }

        return snapshot.Path is { } path ? WriteAt(renamed, path) : Save(renamed);
    }

    private static Snapshot WriteAt(Snapshot snapshot, string path)
    {
        WriteFile(snapshot, path);
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
