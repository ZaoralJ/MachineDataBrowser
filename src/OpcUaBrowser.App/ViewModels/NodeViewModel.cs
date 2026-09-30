using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Opc.Ua;
using OpcUaBrowser.Core;

using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class NodeViewModel : ObservableObject
{
    private static readonly NodeViewModel Placeholder = new();

    private readonly Func<NodeId, Task<IReadOnlyList<BrowseItem>>>? _browse;
    private readonly Action<Exception>? _onError;
    private readonly Func<NodeId, string>? _formatId;
    private bool _loaded;
    private Task? _loading;

    private NodeViewModel()
    {
        DisplayName = "Loading…";
        NodeId = NodeId.Null;
    }

    public NodeViewModel(
        BrowseItem item,
        Func<NodeId, Task<IReadOnlyList<BrowseItem>>> browse,
        Action<Exception> onError,
        Func<NodeId, string>? formatId = null,
        NodeViewModel? parent = null)
    {
        Parent = parent;
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
            Children.Add(Placeholder);
        }
        else
        {
            _loaded = true;
        }
    }

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

    public ObservableCollection<NodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            _loading = LoadChildrenAsync();
        }
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
            var next = new List<NodeViewModel>();
            foreach (var node in level.Where(n => n.HasChildren))
            {
                if (expanded++ >= maxNodes)
                {
                    return true;
                }

                node.IsExpanded = true;
                if (node._loading is { } loading)
                {
                    await loading;
                }

                next.AddRange(node.Children.Where(c => c.HasChildren));
            }

            level = next;
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

        var existing = Children.Where(c => c != Placeholder).ToDictionary(c => c.NodeId);
        var wanted = items.Select(item =>
            existing.TryGetValue(item.NodeId, out var keep) && keep.NodeClass == item.NodeClass && keep.HasChildren == item.HasChildren
                ? keep
                : new NodeViewModel(item, _browse, _onError!, _formatId, this)).ToList();

        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Children[i]))
            {
                Children.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Children.Count && ReferenceEquals(Children[i], wanted[i]))
            {
                continue;
            }

            var at = Children.IndexOf(wanted[i]);
            if (at >= 0)
            {
                Children.Move(at, i);
            }
            else
            {
                Children.Insert(i, wanted[i]);
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
            Children.Clear();
            foreach (var item in items)
            {
                Children.Add(new NodeViewModel(item, _browse, _onError!, _formatId, this));
            }
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            Children.Clear();
            _loaded = false;
            _onError?.Invoke(ex);
        }
    }
}
