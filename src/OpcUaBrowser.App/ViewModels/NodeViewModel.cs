using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Opc.Ua;
using OpcUaBrowser.Core;

using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class NodeViewModel : ObservableObject
{
    private readonly NodeViewModel? _placeholder;
    private readonly Func<NodeId, Task<IReadOnlyList<BrowseItem>>>? _browse;
    private readonly Action<Exception>? _onError;
    private readonly Func<NodeId, string>? _formatId;
    private bool _loaded;
    private Task? _loading;

    // One placeholder per node: the flattened tree shows it as a row, and rows must be distinct objects.
    private NodeViewModel(NodeViewModel parent)
    {
        Parent = parent;
        Depth = parent.Depth + 1;
        DisplayName = "Loading…";
        NodeId = NodeId.Null;
        IsPlaceholder = true;
    }

    public NodeViewModel(
        BrowseItem item,
        Func<NodeId, Task<IReadOnlyList<BrowseItem>>> browse,
        Action<Exception> onError,
        Func<NodeId, string>? formatId = null,
        NodeViewModel? parent = null)
    {
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        NodeId = item.NodeId;
        _formatId = formatId;
        NodeIdText = formatId?.Invoke(item.NodeId) ?? item.NodeId.ToString();
        DisplayName = item.DisplayName;
        NodeClass = item.NodeClass;
        _browse = browse;
        _onError = onError;

        // The placeholder makes the expander visible until the first browse replaces it with real children.
        HasChildren = item.HasChildren;
        if (item.HasChildren)
        {
            _placeholder = new NodeViewModel(this);
            Children.Add(_placeholder);
        }
        else
        {
            _loaded = true;
        }

        Children.CollectionChanged += (_, _) => StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when <see cref="IsExpanded"/> or <see cref="Children"/> changes, i.e. the visible rows below this node.</summary>
    public event EventHandler? StructureChanged;

    /// <summary>Nesting level: 0 for the root.</summary>
    public int Depth { get; }

    public bool IsPlaceholder { get; }

    /// <summary>The node this one was browsed from; null for the root.</summary>
    public NodeViewModel? Parent { get; }

    /// <summary>Ancestors from the root down to <see cref="Parent"/>.</summary>
    public IReadOnlyList<NodeViewModel> Ancestors
    {
        get
        {
            var list = new List<NodeViewModel>();
            for (var p = Parent; p is not null; p = p.Parent)
            {
                list.Insert(0, p);
            }

            return list;
        }
    }

    public NodeId NodeId { get; }

    /// <summary>Id as shown to people (tooltip, drag text): <c>ns=…</c> for OPC UA, the tag path for CIP.</summary>
    public string NodeIdText { get; } = string.Empty;

    public string DisplayName { get; }

    public NodeClass NodeClass { get; }

    public string ToolTip => IsVariable
        ? $"{NodeClass}  ·  {NodeIdText}\nDouble-click, Enter or drag to Watch to monitor · right-click for more"
        : HasChildren
            ? $"{NodeClass}  ·  {NodeIdText}\nDrag to Watch or right-click to monitor all variables inside (including subfolders)"
            : $"{NodeClass}  ·  {NodeIdText}";

    public bool IsVariable => NodeClass == NodeClass.Variable;

    public bool HasChildren { get; }

    public BulkObservableCollection<NodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            _loading = LoadChildrenAsync();
        }

        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnsureChildrenLoadedAsync()
    {
        IsExpanded = true;
        if (_loading is { } loading)
        {
            await loading;
        }
    }

    public void CollapseAll()
    {
        foreach (var child in Children)
        {
            child.CollapseAll();
        }

        IsExpanded = false;
    }

    /// <summary>
    /// Expands this node and its descendants breadth-first, browsing as needed, until <paramref name="maxDepth"/>
    /// levels or <paramref name="maxNodes"/> expanded nodes. Returns true if a limit stopped the expansion.
    /// </summary>
    public async Task<bool> ExpandAllAsync(int maxDepth, int maxNodes)
    {
        var level = new List<NodeViewModel> { this };
        var expanded = 0;
        for (var depth = 0; depth < maxDepth && level.Count > 0; depth++)
        {
            // Browse a whole level concurrently instead of one round trip per node.
            var batch = level.Where(n => n.HasChildren).ToList();
            var limited = expanded + batch.Count > maxNodes;
            batch = batch.Take(Math.Max(0, maxNodes - expanded)).ToList();
            expanded += batch.Count;
            foreach (var node in batch)
            {
                node.IsExpanded = true;
            }

            await Task.WhenAll(batch.Select(n => n._loading).OfType<Task>());
            if (limited)
            {
                return true;
            }

            level = batch.SelectMany(n => n.Children.Where(c => c.HasChildren)).ToList();
        }

        return level.Count > 0;
    }

    /// <summary>
    /// Re-browses expanded nodes of a live address space (MQTT topics and Sparkplug metrics appear over time) and
    /// merges the result: existing child view models are kept, so expansion and selection survive.
    /// </summary>
    public async Task RefreshExpandedAsync()
    {
        if (_browse is null || !_loaded || !IsExpanded || !HasChildren)
        {
            return;
        }

        IReadOnlyList<BrowseItem> items;
        try
        {
            items = await _browse(NodeId);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            return; // a refresh is best effort; the next one retries
        }

        var existing = new Dictionary<NodeId, NodeViewModel>();
        foreach (var child in Children)
        {
            if (child != _placeholder)
            {
                existing.TryAdd(child.NodeId, child);
            }
        }

        var wanted = items.Select(item =>
            existing.TryGetValue(item.NodeId, out var keep) && keep.NodeClass == item.NodeClass && keep.HasChildren == item.HasChildren
                ? keep
                : new NodeViewModel(item, _browse, _onError!, _formatId, this)).ToList();

        if (!Children.SequenceEqual(wanted))
        {
            // Hash lookups keep the merge linear; it runs every second for live MQTT trees.
            var wantedSet = new HashSet<NodeViewModel>(wanted, System.Collections.Generic.ReferenceEqualityComparer.Instance);
            for (var i = Children.Count - 1; i >= 0; i--)
            {
                if (!wantedSet.Contains(Children[i]))
                {
                    Children.RemoveAt(i);
                }
            }

            var present = new HashSet<NodeViewModel>(Children, System.Collections.Generic.ReferenceEqualityComparer.Instance);
            for (var i = 0; i < wanted.Count; i++)
            {
                if (i < Children.Count && ReferenceEquals(Children[i], wanted[i]))
                {
                    continue;
                }

                if (present.Contains(wanted[i]))
                {
                    Children.Move(Children.IndexOf(wanted[i]), i);
                }
                else
                {
                    Children.Insert(i, wanted[i]);
                }
            }
        }

        foreach (var child in wanted.Where(c => c.IsExpanded))
        {
            await child.RefreshExpandedAsync();
        }
    }

    private async Task LoadChildrenAsync()
    {
        if (_browse is null)
        {
            return;
        }

        _loaded = true;
        try
        {
            var items = await _browse(NodeId);
            Children.ReplaceAll(items.Select(item => new NodeViewModel(item, _browse, _onError!, _formatId, this)));
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            Children.Clear();
            _loaded = false;
            _onError?.Invoke(ex);
        }
    }
}
