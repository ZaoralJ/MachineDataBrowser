using CommunityToolkit.Mvvm.ComponentModel;
using Opc.Ua;
using OpcUaBrowser.Core;

using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class NodeViewModel : ObservableObject
{
    private readonly Func<NodeId, Task<IReadOnlyList<BrowseItem>>>? _browse;
    private readonly Func<IReadOnlyList<NodeId>, Task<IReadOnlyList<bool>?>>? _probe;
    private readonly Action<Exception>? _onError;
    private readonly Func<NodeId, string>? _formatId;
    private NodeViewModel? _placeholder;
    private bool _loaded;
    private Task? _loading;
    private DateTime _collapsedAt = DateTime.MaxValue;

    // Placeholder ("Loading…") and error rows. One per node: the flattened tree shows it as a row, and rows must be distinct objects.
    private NodeViewModel(NodeViewModel parent, string text = "Loading…", string? error = null)
    {
        Parent = parent;
        Depth = parent.Depth + 1;
        DisplayName = text;
        NodeId = NodeId.Null;
        IsPlaceholder = true;
        Error = error;
    }

    /// <param name="probe">
    /// Resolves which children really have children when <paramref name="browse"/> only reports it provisionally
    /// (it returns null when the browse was exact). The tree shows children after one round trip and drops the
    /// expanders of leaves when the probe answers.
    /// </param>
    public NodeViewModel(
        BrowseItem item,
        Func<NodeId, Task<IReadOnlyList<BrowseItem>>> browse,
        Action<Exception> onError,
        Func<NodeId, string>? formatId = null,
        NodeViewModel? parent = null,
        Func<IReadOnlyList<NodeId>, Task<IReadOnlyList<bool>?>>? probe = null)
    {
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        NodeId = item.NodeId;
        _formatId = formatId;
        NodeIdText = formatId?.Invoke(item.NodeId) ?? item.NodeId.ToString();
        DisplayName = item.DisplayName;
        NodeClass = item.NodeClass;
        _browse = browse;
        _probe = probe;
        _onError = onError;

        // The placeholder makes the expander visible until the first browse replaces it with real children.
        HasChildren = item.HasChildren;
        if (item.HasChildren)
        {
            Children.Add(Placeholder);
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

    /// <summary>A "Loading…" or error row, not a server node.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>Why browsing the parent failed; set on the error row that offers a retry.</summary>
    public string? Error { get; }

    public bool IsError => Error is not null;

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

    public string ToolTip => IsError
        ? $"{Error}\nClick to retry"
        : IsPlaceholder
            ? DisplayName
            : IsVariable
                ? $"{NodeClass}  ·  {NodeIdText}\nDouble-click, Enter or drag to Watch to monitor · right-click for more"
                : HasChildren
                    ? $"{NodeClass}  ·  {NodeIdText}\nDrag to Watch or right-click to monitor all variables inside (including subfolders)"
                    : $"{NodeClass}  ·  {NodeIdText}";

    public bool IsVariable => NodeClass == NodeClass.Variable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial bool HasChildren { get; private set; }

    public BulkObservableCollection<NodeViewModel> Children { get; } = [];

    /// <summary>Children were browsed and are held in memory.</summary>
    public bool IsLoaded => _loaded && HasChildren;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    private NodeViewModel Placeholder => _placeholder ??= new NodeViewModel(this);

    partial void OnIsExpandedChanged(bool value)
    {
        _collapsedAt = value ? DateTime.MaxValue : DateTime.UtcNow;
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

    /// <summary>Browses again after a failed load (the error row's action).</summary>
    public void RetryLoad()
    {
        if (_loaded || _loading is { IsCompleted: false })
        {
            return;
        }

        if (IsExpanded)
        {
            _loading = LoadChildrenAsync();
        }
        else
        {
            IsExpanded = true;
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
    /// Drops the children of descendants that have been collapsed for longer than <paramref name="age"/>, so a long
    /// session (or a growing MQTT tree) doesn't hold every folder ever opened. Nodes in <paramref name="keep"/>
    /// (ancestors of the selection) are left alone. They are browsed again when expanded. Returns the number unloaded.
    /// </summary>
    public int UnloadCollapsed(TimeSpan age, DateTime now, IReadOnlySet<NodeViewModel> keep)
    {
        var count = 0;
        foreach (var child in Children)
        {
            if (child.IsExpanded)
            {
                count += child.UnloadCollapsed(age, now, keep);
            }
            else if (child.IsLoaded && child._browse is not null && now - child._collapsedAt > age && !keep.Contains(child))
            {
                child._loaded = false;
                child._loading = null;
                child.Children.ReplaceAll([child.Placeholder]);
                count++;
            }
        }

        return count;
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
            if (!child.IsPlaceholder)
            {
                existing.TryAdd(child.NodeId, child);
            }
        }

        // A provisional HasChildren (probe pending) must not replace a node the probe already resolved.
        var added = new List<NodeViewModel>();
        var wanted = items.Select(item =>
        {
            if (existing.TryGetValue(item.NodeId, out var keep) && keep.NodeClass == item.NodeClass
                && (_probe is not null || keep.HasChildren == item.HasChildren))
            {
                return keep;
            }

            var node = Create(item);
            added.Add(node);
            return node;
        }).ToList();

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

        _ = ProbeAsync(added);
        foreach (var child in wanted.Where(c => c.IsExpanded))
        {
            await child.RefreshExpandedAsync();
        }
    }

    private NodeViewModel Create(BrowseItem item) => new(item, _browse!, _onError!, _formatId, this, _probe);

    private async Task LoadChildrenAsync()
    {
        if (_browse is null)
        {
            return;
        }

        _loaded = true;
        if (Children is not [{ IsPlaceholder: true, IsError: false }])
        {
            Children.ReplaceAll([Placeholder]);
        }

        try
        {
            var items = await _browse(NodeId);
            var children = items.Select(Create).ToList();
            Children.ReplaceAll(children);
            if (children.Count == 0)
            {
                HasChildren = false;
            }

            _ = ProbeAsync(children);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            _loaded = false;
            Children.ReplaceAll([new NodeViewModel(this, "Couldn't load — click to retry", AppErrors.Describe(ex))]);
            _onError?.Invoke(ex);
        }
    }

    /// <summary>Drops the expander of children the server says have no children of their own (best effort).</summary>
    private async Task ProbeAsync(IReadOnlyList<NodeViewModel> nodes)
    {
        var candidates = nodes.Where(n => n.HasChildren && !n._loaded).ToList();
        if (_probe is null || candidates.Count == 0)
        {
            return;
        }

        IReadOnlyList<bool>? result;
        try
        {
            result = await _probe([.. candidates.Select(n => n.NodeId)]);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            return; // unresolved children keep their expander; expanding them browses as usual
        }

        for (var i = 0; result is not null && i < candidates.Count && i < result.Count; i++)
        {
            var node = candidates[i];
            if (!result[i] && !node._loaded)
            {
                node._loaded = true;
                node.HasChildren = false;
                node.Children.Clear();
            }
        }
    }
}
