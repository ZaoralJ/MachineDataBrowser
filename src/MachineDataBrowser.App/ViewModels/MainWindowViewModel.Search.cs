using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

/// <summary>Address space search: breadth-first below the selected node (or the whole tree), results reveal their node.</summary>
public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial SearchHit? SelectedSearchHit { get; set; }

    public ObservableCollection<SearchHit> SearchResults { get; } = [];

    public bool HasSearchPanel => IsSearching || SearchResults.Count > 0 || SearchStatus.Length > 0;

    /// <summary>Raised to move keyboard focus into the search box (⌘F).</summary>
    public event EventHandler? SearchFocusRequested;

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(HasSearchPanel));

    partial void OnSearchStatusChanged(string value) => OnPropertyChanged(nameof(HasSearchPanel));

    private bool CanSearch() => IsConnected && !string.IsNullOrWhiteSpace(SearchText);

    [RelayCommand]
    private void FocusSearch()
    {
        if (Layout is not null)
        {
            DockFactory.ShowPane(Layout, "AddressSpace");
        }

        SearchFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        CancelSearch();
        if (RootNodes.Count == 0)
        {
            return;
        }

        // Search below the selected node when it has children (narrow and fast), otherwise the whole tree.
        var start = SelectedNode is { HasChildren: true } node ? node : RootNodes[0];
        var prefix = start.Ancestors.Select(a => a.NodeId).Append(start.NodeId).ToList();
        var query = SearchText.Trim();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchPanel));
        IsSearching = true;
        SearchStatus = $"Searching '{query}' in {start.DisplayName}…";
        var progress = new Progress<int>(n => SearchStatus = $"Searching '{query}' in {start.DisplayName}… {n:N0} nodes");
        try
        {
            var client = _client;
            var item = new BrowseItem(start.NodeId, start.DisplayName, start.DisplayName, start.NodeClass, start.HasChildren);
            var result = await Task.Run(() => client.SearchAsync(item, query, progress: progress, cancellationToken: cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var hit in result.Hits)
            {
                // Full path from Root, so a result can be revealed even when the search started lower down.
                SearchResults.Add(hit with { Path = [.. prefix.Take(prefix.Count - 1), .. hit.Path] });
            }

            OnPropertyChanged(nameof(HasSearchPanel));
            SearchStatus = (result.Hits.Count, result.Truncated) switch
            {
                (0, false) => $"No match for '{query}' in {start.DisplayName} ({result.NodesVisited:N0} nodes)",
                (0, true) => $"No match for '{query}' in the first {result.NodesVisited:N0} nodes of {start.DisplayName}; select a folder to search deeper",
                (var n, false) => $"{n:N0} match{(n == 1 ? string.Empty : "es")} in {start.DisplayName} ({result.NodesVisited:N0} nodes)",
                (var n, true) => $"{n:N0} matches (search limit reached) in {start.DisplayName}; select a folder to narrow it",
            };
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            SearchStatus = "Search failed";
            ReportError(ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
                _searchCts = null;
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        CancelSearch();
        SearchText = string.Empty;
        SearchResults.Clear();
        SearchStatus = string.Empty;
        OnPropertyChanged(nameof(HasSearchPanel));
    }

    [RelayCommand]
    private async Task RevealSearchHitAsync(SearchHit? hit)
    {
        if ((hit ?? SelectedSearchHit) is not { } target || RootNodes.Count == 0)
        {
            return;
        }

        try
        {
            await RevealPathAsync(target.Path, target.Item.DisplayName);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        IsSearching = false;
    }
}
