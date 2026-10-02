using System.Collections.ObjectModel;

namespace OpcUaBrowser.App.ViewModels;

/// <summary>
/// The visible rows of the node tree (each node followed by its children while expanded), for a virtualized list:
/// Avalonia's TreeView builds a container for every expanded node, which froze the UI on large folders.
/// Expanding, collapsing or re-browsing a node replaces only the changed span of rows below it.
/// </summary>
public sealed class FlatTree
{
    private readonly ObservableCollection<NodeViewModel> _roots;
    private readonly HashSet<NodeViewModel> _attached = new(ReferenceEqualityComparer.Instance);

    public FlatTree(ObservableCollection<NodeViewModel> roots)
    {
        _roots = roots;
        roots.CollectionChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public BulkObservableCollection<NodeViewModel> Rows { get; } = [];

    private void Rebuild()
    {
        foreach (var node in _attached)
        {
            node.StructureChanged -= OnStructureChanged;
        }

        _attached.Clear();
        var rows = new List<NodeViewModel>();
        foreach (var root in _roots)
        {
            Collect(root, rows);
        }

        Rows.ReplaceAll(rows);
        Attach(rows);
    }

    private static void Collect(NodeViewModel node, List<NodeViewModel> rows)
    {
        rows.Add(node);
        if (node.IsExpanded)
        {
            foreach (var child in node.Children)
            {
                Collect(child, rows);
            }
        }
    }

    private void OnStructureChanged(object? sender, EventArgs e)
    {
        if (sender is not NodeViewModel node)
        {
            return;
        }

        var index = Rows.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        var start = index + 1;
        var end = start;
        while (end < Rows.Count && Rows[end].Depth > node.Depth)
        {
            end++;
        }

        var desired = new List<NodeViewModel>();
        if (node.IsExpanded)
        {
            foreach (var child in node.Children)
            {
                Collect(child, desired);
            }
        }

        var oldCount = end - start;
        var prefix = 0;
        while (prefix < oldCount && prefix < desired.Count && ReferenceEquals(Rows[start + prefix], desired[prefix]))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldCount - prefix && suffix < desired.Count - prefix
               && ReferenceEquals(Rows[end - 1 - suffix], desired[^(suffix + 1)]))
        {
            suffix++;
        }

        var removeCount = oldCount - prefix - suffix;
        var inserted = desired.GetRange(prefix, desired.Count - prefix - suffix);
        if (removeCount == 0 && inserted.Count == 0)
        {
            return;
        }

        var removed = new List<NodeViewModel>(removeCount);
        for (var i = 0; i < removeCount; i++)
        {
            removed.Add(Rows[start + prefix + i]);
        }

        Rows.RemoveRangeAt(start + prefix, removeCount);
        Rows.InsertRange(start + prefix, inserted);

        var keep = new HashSet<NodeViewModel>(inserted, ReferenceEqualityComparer.Instance);
        foreach (var gone in removed.Where(r => !keep.Contains(r)))
        {
            gone.StructureChanged -= OnStructureChanged;
            _attached.Remove(gone);
        }

        Attach(inserted);
    }

    private void Attach(IEnumerable<NodeViewModel> rows)
    {
        foreach (var row in rows)
        {
            if (_attached.Add(row))
            {
                row.StructureChanged += OnStructureChanged;
            }
        }
    }
}
