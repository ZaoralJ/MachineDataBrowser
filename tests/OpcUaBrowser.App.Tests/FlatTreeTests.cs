using System.Collections.ObjectModel;
using Opc.Ua;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class FlatTreeTests
{
    private readonly Dictionary<uint, List<BrowseItem>> _children = new()
    {
        [1] = [Folder(2, "A"), Folder(3, "B")],
        [2] = [Leaf(4, "A1"), Leaf(5, "A2")],
        [3] = [Leaf(6, "B1")],
    };

    private static BrowseItem Folder(uint id, string name) => new(new NodeId(id), name, name, NodeClass.Object, true);

    private static BrowseItem Leaf(uint id, string name) => new(new NodeId(id), name, name, NodeClass.Variable, false);

    private Task<IReadOnlyList<BrowseItem>> Browse(NodeId id) =>
        Task.FromResult<IReadOnlyList<BrowseItem>>([.. _children[(uint)id.Identifier]]);

    private static string Names(FlatTree tree) => string.Join(",", tree.Rows.Select(r => r.IsPlaceholder ? "…" : r.DisplayName));

    [Fact]
    public async Task Rows_follow_expand_collapse_and_refresh()
    {
        var root = new NodeViewModel(Folder(1, "Root"), Browse, _ => { });
        var tree = new FlatTree(new ObservableCollection<NodeViewModel> { root });
        Assert.Equal("Root", Names(tree));

        await root.EnsureChildrenLoadedAsync();
        Assert.Equal("Root,A,B", Names(tree));

        var a = root.Children[0];
        a.IsExpanded = true;
        await a.EnsureChildrenLoadedAsync();
        Assert.Equal("Root,A,A1,A2,B", Names(tree));
        Assert.Equal([0, 1, 2, 2, 1], tree.Rows.Select(r => r.Depth));

        // Collapsing the root hides everything below it; expanding brings back the still-expanded A subtree.
        root.IsExpanded = false;
        Assert.Equal("Root", Names(tree));
        root.IsExpanded = true;
        Assert.Equal("Root,A,A1,A2,B", Names(tree));

        _children[2] = [Leaf(5, "A2"), Leaf(7, "A3")];
        await root.RefreshExpandedAsync();
        Assert.Equal("Root,A,A2,A3,B", Names(tree));
        Assert.Equal(tree.Rows.Count, tree.Rows.Distinct().Count());
    }

    [Fact]
    public void Each_unloaded_node_shows_its_own_placeholder_row()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<BrowseItem>>();
        var root = new NodeViewModel(Folder(1, "Root"), _ => pending.Task, _ => { });
        var tree = new FlatTree(new ObservableCollection<NodeViewModel> { root });

        root.IsExpanded = true;
        Assert.Equal("Root,…", Names(tree));
        Assert.Same(root, tree.Rows[1].Parent);
    }
}
