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

    [Fact]
    public async Task Probe_drops_the_expander_of_leaves_after_a_quick_browse()
    {
        var probed = new TaskCompletionSource<IReadOnlyList<bool>?>();
        var root = new NodeViewModel(Folder(1, "Root"), Browse, _ => { }, probe: _ => probed.Task);
        await root.EnsureChildrenLoadedAsync();
        Assert.All(root.Children, c => Assert.True(c.HasChildren)); // provisional until the probe answers

        probed.SetResult([false, true]);
        await Task.Yield();
        Assert.False(root.Children[0].HasChildren);
        Assert.Empty(root.Children[0].Children);
        Assert.True(root.Children[1].HasChildren);
    }

    [Fact]
    public async Task Failed_browse_shows_an_error_row_that_retries()
    {
        var fail = true;
        var root = new NodeViewModel(Folder(1, "Root"), id => fail ? Task.FromException<IReadOnlyList<BrowseItem>>(new TimeoutException("slow server")) : Browse(id), _ => { });
        var tree = new FlatTree(new ObservableCollection<NodeViewModel> { root });

        await root.EnsureChildrenLoadedAsync();
        var error = Assert.Single(root.Children);
        Assert.True(error.IsError);
        Assert.Contains("slow server", error.ToolTip, StringComparison.Ordinal);
        Assert.Equal(2, tree.Rows.Count);

        fail = false;
        root.RetryLoad();
        await root.EnsureChildrenLoadedAsync();
        Assert.Equal("Root,A,B", Names(tree));
    }

    [Fact]
    public async Task Long_collapsed_folders_are_unloaded_unless_they_hold_the_selection()
    {
        var root = new NodeViewModel(Folder(1, "Root"), Browse, _ => { });
        await root.EnsureChildrenLoadedAsync();
        var (a, b) = (root.Children[0], root.Children[1]);
        await a.EnsureChildrenLoadedAsync();
        await b.EnsureChildrenLoadedAsync();
        a.IsExpanded = false;
        b.IsExpanded = false;

        var later = DateTime.UtcNow.AddHours(1);
        var keep = new HashSet<NodeViewModel> { root, b, b.Children[0] };
        Assert.Equal(1, root.UnloadCollapsed(TimeSpan.FromMinutes(10), later, keep));
        Assert.False(a.IsLoaded);
        Assert.True(Assert.Single(a.Children).IsPlaceholder);
        Assert.True(b.IsLoaded);

        await a.EnsureChildrenLoadedAsync();
        Assert.Equal(["A1", "A2"], a.Children.Select(c => c.DisplayName));
    }

    [Fact]
    public async Task Collapsing_reports_the_hidden_rows_and_deferral_rebuilds_once()
    {
        var root = new NodeViewModel(Folder(1, "Root"), Browse, _ => { });
        var tree = new FlatTree(new ObservableCollection<NodeViewModel> { root });
        await root.EnsureChildrenLoadedAsync();

        IReadOnlyList<NodeViewModel>? hidden = null;
        tree.Collapsing += (owner, rows) => hidden = owner == root ? rows : null;
        root.IsExpanded = false;
        Assert.Equal(["A", "B"], hidden!.Select(r => r.DisplayName));

        using (tree.DeferUpdates())
        {
            root.IsExpanded = true;
            await root.Children[0].EnsureChildrenLoadedAsync();
            Assert.Equal("Root", Names(tree));
        }

        Assert.Equal("Root,A,A1,A2,B", Names(tree));
    }
}
