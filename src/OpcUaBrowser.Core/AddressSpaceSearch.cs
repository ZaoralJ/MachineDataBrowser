using System.Text.RegularExpressions;
using Opc.Ua;

namespace OpcUaBrowser.Core;

/// <summary>One match of <see cref="AddressSpaceSearch.SearchAsync"/>: the node and the browse path to it.</summary>
public sealed record SearchHit(BrowseItem Item, IReadOnlyList<NodeId> Path, IReadOnlyList<string> PathNames)
{
    /// <summary>"Objects › Boilers › Boiler #1" (without the start node).</summary>
    public string PathText => string.Join(" › ", PathNames);
}

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int NodesVisited, bool Truncated);

/// <summary>
/// Finds nodes by name or id below a start node by browsing breadth-first, so it works the same for every protocol
/// (OPC UA browses the server, EtherNet/IP and MQTT browse their cached models). Limits keep large servers responsive.
/// </summary>
public static class AddressSpaceSearch
{
    /// <summary>
    /// Case-insensitive match on the display name or the display id. <c>*</c> and <c>?</c> are wildcards
    /// (<c>Temp*</c>, <c>press?</c>); without them the text may occur anywhere.
    /// </summary>
    public static Func<string, bool> Matcher(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Trim();
        if (text.Contains('*', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal))
        {
            var pattern = "^" + Regex.Escape(text).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            return value => regex.IsMatch(value);
        }

        return value => value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<SearchResult> SearchAsync(
        this IDeviceClient client,
        BrowseItem start,
        string query,
        int maxDepth = 12,
        int maxResults = 1000,
        int maxNodes = 50_000,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(start);
        var matches = Matcher(query);
        var hits = new List<SearchHit>();
        var visited = new HashSet<NodeId> { start.NodeId };
        var level = new List<(BrowseItem Item, List<NodeId> Path, List<string> Names)> { (start, [start.NodeId], []) };
        var count = 0;
        for (var depth = 0; depth < maxDepth && level.Count > 0; depth++)
        {
            var next = new List<(BrowseItem, List<NodeId>, List<string>)>();
            foreach (var (item, path, names) in level)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<BrowseItem> children;
                try
                {
                    children = await client.BrowseAsync(item.NodeId, cancellationToken).ConfigureAwait(false);
                }
                catch (ServiceResultException)
                {
                    continue; // a node that cannot be browsed (access, removed) must not stop the search
                }

                foreach (var child in children)
                {
                    if (!visited.Add(child.NodeId))
                    {
                        continue;
                    }

                    var childPath = new List<NodeId>(path) { child.NodeId };
                    var childNames = new List<string>(names) { child.DisplayName };
                    if (matches(child.DisplayName) || matches(client.ToDisplayId(child.NodeId)))
                    {
                        hits.Add(new SearchHit(child, childPath, childNames));
                        if (hits.Count >= maxResults)
                        {
                            return new SearchResult(hits, count, Truncated: true);
                        }
                    }

                    if (++count >= maxNodes)
                    {
                        return new SearchResult(hits, count, Truncated: true);
                    }

                    if (child.HasChildren)
                    {
                        next.Add((child, childPath, childNames));
                    }
                }

                progress?.Report(count);
            }

            level = next;
        }

        return new SearchResult(hits, count, Truncated: level.Count > 0);
    }
}
