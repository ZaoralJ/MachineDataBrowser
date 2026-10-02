using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

/// <summary>A saved place in the address space; <see cref="NodeId"/> is portable (survives server restarts).</summary>
public sealed record Bookmark(string Name, string NodeId, string? Path = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ToolTip => string.IsNullOrEmpty(Path) ? NodeId : $"{Path}\n{NodeId}";
}

public sealed partial class MainWindowViewModel
{
    /// <summary>Bookmarks of this session (node ids are per server, so they live in the session file).</summary>
    public ObservableCollection<Bookmark> Bookmarks { get; } = [];

    public bool HasBookmarks => Bookmarks.Count > 0;

    private bool CanBookmark() => IsConnected && SelectedNode is { IsPlaceholder: false } node && node.Depth > 0;

    /// <summary>Bookmarks the selected node, or removes its bookmark if it has one.</summary>
    [RelayCommand(CanExecute = nameof(CanBookmark))]
    private void ToggleBookmark()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        var id = _client.ToPortableId(node.NodeId);
        if (Bookmarks.FirstOrDefault(b => b.NodeId == id) is { } existing)
        {
            Bookmarks.Remove(existing);
            StatusMessage = $"Bookmark removed: {node.DisplayName}";
        }
        else
        {
            var path = string.Join(" › ", node.Ancestors.Skip(1).Select(a => a.DisplayName));
            Bookmarks.Add(new Bookmark(node.DisplayName, id, path));
            StatusMessage = $"Bookmarked {node.DisplayName}";
        }

        MarkDirty();
    }

    [RelayCommand]
    private void RemoveBookmark(Bookmark? bookmark)
    {
        if (bookmark is not null && Bookmarks.Remove(bookmark))
        {
            MarkDirty();
        }
    }

    /// <summary>Opens the tree at the bookmarked node.</summary>
    [RelayCommand]
    private async Task GoToBookmarkAsync(Bookmark? bookmark)
    {
        if (bookmark is null || !IsConnected || RootNodes.Count == 0)
        {
            return;
        }

        try
        {
            var nodeId = _client.ParsePortableId(bookmark.NodeId);
            var client = _client;
            var path = await Task.Run(() => client.GetPathFromRootAsync(nodeId));
            if (path.Count == 0)
            {
                StatusMessage = $"'{bookmark.Name}' is not in this server's address space";
                return;
            }

            await RevealPathAsync(path, bookmark.Name);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
    }
}
