namespace MachineDataBrowser.Core;

/// <summary>
/// Reads snapshot items from a device once, the way the app (for items not in the watch list) and <c>mdbrowser mcp</c>
/// compare and restore snapshots: values formatted with <see cref="ValueFormatter"/>, a failed read kept as Bad so it is
/// never written back.
/// </summary>
public static class SnapshotReader
{
    public const string ReadFailed = "Bad (read failed)";

    public static SnapshotItem FromValue(string name, string portableId, object? value, string? path = null) =>
        (value is null ? SnapshotItem.Of(name, portableId, string.Empty, ReadFailed) : SnapshotItem.Of(name, portableId, ValueFormatter.Format(value), "Good"))
            with { Path = path };

    /// <summary>The current values of <paramref name="items"/>; an id this connection doesn't know reads as Bad.</summary>
    public static async Task<Snapshot> ReadAsync(IDeviceClient client, IReadOnlyList<SnapshotItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(items);
        var ids = new List<(SnapshotItem Item, DeviceNodeId Id)>();
        var unknown = new List<SnapshotItem>();
        foreach (var item in items)
        {
            try
            {
                ids.Add((item, client.ParsePortableId(item.NodeId)));
            }
            catch (Exception ex) when (ex is DeviceClientException or FormatException or ArgumentException)
            {
                unknown.Add(item);
            }
        }

        var values = ids.Count == 0 ? [] : await client.ReadValuesAsync([.. ids.Select(i => i.Id)], cancellationToken).ConfigureAwait(false);
        return new Snapshot
        {
            Name = "Now",
            TakenAt = DateTimeOffset.Now,
            Items =
            [
                .. ids.Select((entry, i) => FromValue(entry.Item.Name, entry.Item.NodeId, values[i], entry.Item.Path)),
                .. unknown.Select(item => SnapshotItem.Of(item.Name, item.NodeId, string.Empty, "Bad (unknown node)") with { Path = item.Path }),
            ],
        };
    }
}
