using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Opc.Ua;
using OpcUaBrowser.Core;

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
        Func<NodeId, string>? formatId = null)
    {
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
                Children.Add(new NodeViewModel(item, _browse, _onError!, _formatId));
            }
        }
        catch (Exception ex) when (ex is ServiceResultException or InvalidOperationException)
        {
            Children.Clear();
            _loaded = false;
            _onError?.Invoke(ex);
        }
    }
}
