using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.Core;
using OpcUaBrowser.Core.Ua;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private IDeviceClient _client = new OpcUaClient();
    private readonly LayoutStore? _layoutStore;
    private DockFactory? _dockFactory;

    // Notifications arrive on SDK threads at any rate; keep only the latest per item and flush on a UI timer.
    private readonly ConcurrentDictionary<WatchItemViewModel, ValueUpdate> _pendingUpdates = new();
    private readonly DispatcherTimer _flushTimer;

    public MainWindowViewModel()
        : this(settingsStore: null)
    {
    }

    public MainWindowViewModel(SettingsStore? settingsStore, LayoutStore? layoutStore = null)
    {
        _settingsStore = settingsStore;
        _layoutStore = layoutStore;
        Settings = settingsStore?.Load() ?? new AppSettings();
        DefaultRefreshMs = Settings.SamplingIntervalMs;
        EndpointUrl = DefaultEndpointUrl;
        IsDirty = false;
        ApplyTheme(Settings.Theme);
        Layout = layoutStore?.TryLoad(DockFactory) ?? DockFactory.CreateLayout();

        _client.StateChanged += OnClientStateChanged;
        AppErrors.Reported += OnAppError;
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushUpdates());
        _flushTimer.Start();

        Attributes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttributes));
        WatchItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWatchItems));
            OnPropertyChanged(nameof(WatchCount));
            ClearWatchCommand.NotifyCanExecuteChanged();
            NewRecordingCommand.NotifyCanExecuteChanged();
            RecordAllCommand.NotifyCanExecuteChanged();
            ExportWatchCsvCommand.NotifyCanExecuteChanged();
            UpdateRecordingFlags();
            MarkDirty();
        };
        Recordings.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<RecordingViewModel>() ?? [])
            {
                added.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName == nameof(RecordingViewModel.State))
                    {
                        UpdateRecordingFlags();
                    }
                    else if (p.PropertyName == nameof(RecordingViewModel.Samples))
                    {
                        UpdateRecordedCounts();
                    }
                };
            }

            UpdateRecordingFlags();
        };
        WatchColumns.Changed += (_, _) => MarkDirty();
        SelectedWatchItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WatchSelectionLabel));
            RemoveFromWatchCommand.NotifyCanExecuteChanged();
            SetRefreshCommand.NotifyCanExecuteChanged();
            CopyWatchValueCommand.NotifyCanExecuteChanged();
            CopyWatchNodeIdCommand.NotifyCanExecuteChanged();
            CopyWatchJsonCommand.NotifyCanExecuteChanged();
            CopyWatchValuesJsonCommand.NotifyCanExecuteChanged();
        };
        SelectedNodes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectionSummary));
            NotifySelectionCommands();
        };
    }

    public Func<string, Task>? CopyToClipboard { get; set; }

    private void OnClientStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, _client))
            {
                HandleClientState(state);
            }
        });

    /// <summary>The protocol follows the endpoint URL (<c>opc.tcp://</c> or <c>eip://</c>); swap clients when it changes.</summary>
    private async Task EnsureClientForAsync(string endpointUrl)
    {
        if (DeviceClient.Supports(_client, endpointUrl))
        {
            return;
        }

        var old = _client;
        old.StateChanged -= OnClientStateChanged;
        _client = DeviceClient.Create(endpointUrl);
        _client.StateChanged += OnClientStateChanged;
        await Task.Run(() => old.DisposeAsync().AsTask());
    }

    public DockFactory DockFactory => _dockFactory ??= new DockFactory(this);

    [ObservableProperty]
    public partial Dock.Model.Controls.IRootDock? Layout { get; private set; }

    [RelayCommand]
    private void ShowPane(string? id)
    {
        if (Layout is null || id is null || !DockFactory.HomeDocks.ContainsKey(id))
        {
            return;
        }

        try
        {
            DockFactory.ShowPane(Layout, id);
        }
        catch (InvalidOperationException ex)
        {
            ReportError(ex);
        }
    }

    [RelayCommand]
    private void FloatPane(string? id)
    {
        if (Layout is not null && id is not null && DockFactory.HomeDocks.ContainsKey(id))
        {
            DockFactory.FloatPane(Layout, id);
        }
    }

    [RelayCommand]
    private void DockAllPanes()
    {
        if (Layout is null)
        {
            return;
        }

        try
        {
            DockFactory.DockAllPanes(Layout);
            StatusMessage = "All panes docked in the main window";
        }
        catch (InvalidOperationException ex)
        {
            ReportError(ex);
        }
    }

    [RelayCommand]
    private void ResetLayout()
    {
        _layoutStore?.Delete();
        Layout = DockFactory.CreateLayout();
    }

    public void SaveLayout()
    {
        if (Layout is { } layout)
        {
            try
            {
                _layoutStore?.Save(layout);
            }
            catch (Exception ex) when (AppErrors.IsRecoverable(ex))
            {
                ReportError(ex);
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpcUaEndpoint), nameof(OptionsSummary))]
    public partial string EndpointUrl { get; set; } = "opc.tcp://localhost:50000";

    /// <summary>Security, credentials and certificate trust only apply to OPC UA, not to EtherNet/IP (<c>eip://</c>).</summary>
    public bool IsOpcUaEndpoint => !DeviceClient.IsEip(EndpointUrl ?? string.Empty);

    /// <summary>The last connected endpoint, or localhost when there is no history.</summary>
    private string DefaultEndpointUrl => Settings.RecentEndpoints.Count > 0 ? Settings.RecentEndpoints[0] : "opc.tcp://localhost:50000";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial bool UseSecurity { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial bool AutoAcceptCertificates { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial string UserName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial int DefaultRefreshMs { get; set; } = 250;

    public static IReadOnlyList<int> RefreshPresets { get; } = [100, 250, 500, 1000, 2000, 5000, 10000];

    public static string FormatRefresh(int ms) => ms >= 1000 && ms % 1000 == 0 ? $"{ms / 1000} s" : $"{ms} ms";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsDisconnected), nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(AddToWatchCommand), nameof(MonitorFolderCommand), nameof(ExpandAllCommand), nameof(NewRecordingCommand), nameof(RecordAllCommand))]
    public partial ConnectionState State { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = "Not connected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToWatchCommand), nameof(CopyNodeIdCommand), nameof(MonitorFolderCommand), nameof(CopyNodeJsonCommand), nameof(CopyNodeClassCommand), nameof(CopyNodeRecordCommand))]
    public partial NodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyAttributeValueCommand))]
    public partial AttributeValue? SelectedAttribute { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromWatchCommand), nameof(CopyWatchValueCommand), nameof(CopyWatchNodeIdCommand))]
    public partial WatchItemViewModel? SelectedWatchItem { get; set; }

    partial void OnSelectedWatchItemChanged(WatchItemViewModel? value) => OnPropertyChanged(nameof(WatchSelectionLabel));

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsDisconnected => State == ConnectionState.Disconnected;

    public string StateText => State.ToString();

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasAttributes => Attributes.Count > 0;

    public bool HasWatchItems => WatchItems.Count > 0;

    public int WatchCount => WatchItems.Count;

    public string OptionsSummary
    {
        get
        {
            var security = UseSecurity ? "Secure" : "No security";
            var user = string.IsNullOrWhiteSpace(UserName) ? "Anonymous" : UserName;
            var refresh = FormatRefresh(DefaultRefreshMs);
            if (!IsOpcUaEndpoint)
            {
                return $"EtherNet/IP · {refresh}";
            }

            return AutoAcceptCertificates ? $"{security} · {user} · {refresh} · auto-trust" : $"{security} · {user} · {refresh}";
        }
    }

    public ObservableCollection<NodeViewModel> RootNodes { get; } = [];

    public ObservableCollection<NodeViewModel> SelectedNodes { get; } = [];

    public string SelectionSummary => SelectedNodes.Count > 1 ? $"{SelectedNodes.Count} nodes selected" : string.Empty;

    public ObservableCollection<AttributeValue> Attributes { get; } = [];

    public ObservableCollection<WatchItemViewModel> WatchItems { get; } = [];

    [RelayCommand(CanExecute = nameof(IsDisconnected))]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        StatusMessage = $"Connecting to {EndpointUrl}…";
        try
        {
            await EnsureClientForAsync(EndpointUrl);
            var options = new ConnectOptions
            {
                EndpointUrl = EndpointUrl.Trim(),
                UseSecurity = UseSecurity,
                AutoAcceptUntrustedCertificates = AutoAcceptCertificates,
                UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName,
                Password = Password,
            };

            // The SDKs do synchronous work while connecting (endpoint discovery, certificates, type system,
            // libplctag tag creation); run it on the pool so the window stays responsive.
            var client = _client;
            await Task.Run(() => client.ConnectAsync(options));

            RootNodes.Clear();
            var root = new NodeViewModel(
                _client.Root,
                Browse,
                ReportError,
                _client.ToDisplayId);
            RootNodes.Add(root);
            root.IsExpanded = true;

            State = _client.State;
            StatusMessage = $"Connected to {_client.ServerUri ?? EndpointUrl}";
            UpdateSettings(Settings.WithRecentEndpoint(EndpointUrl.Trim()));
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            State = _client.State;
            StatusMessage = "Not connected";
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        _disconnecting = true;
        StatusMessage = "Disconnecting…";
        try
        {
            await StopAllMonitorsAsync();
            var client = _client;
            await Task.Run(client.DisconnectAsync);
            State = _client.State;
            RootNodes.Clear();
            Attributes.Clear();
            StatusMessage = "Disconnected";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            // The session is unusable either way; show why and leave the UI disconnected.
            ReportError(ex);
            State = ConnectionState.Disconnected;
            RootNodes.Clear();
            Attributes.Clear();
            StatusMessage = "Disconnected";
        }
        finally
        {
            _disconnecting = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private List<NodeViewModel> SelectionOrCurrent() =>
        SelectedNodes.Count > 0 ? [.. SelectedNodes] : SelectedNode is { } node ? [node] : [];

    private bool CanAddToWatch() =>
        IsConnected && SelectionOrCurrent().Any(n => n.IsVariable && WatchItems.All(w => w.NodeId != n.NodeId));

    [RelayCommand(CanExecute = nameof(CanAddToWatch))]
    private Task AddToWatchAsync() =>
        AddWatchItemsAsync(SelectionOrCurrent().Where(n => n.IsVariable).Select(n => (n.NodeId, n.DisplayName)).ToList());

    [RelayCommand(CanExecute = nameof(CanAddToWatch))]
    private Task MonitorSelectedWithRefreshAsync(int refreshMs) =>
        AddWatchItemsAsync(SelectionOrCurrent().Where(n => n.IsVariable).Select(n => (n.NodeId, n.DisplayName)).ToList(), refreshMs);

    /// <summary>Raised with the items the Watch grid should select (the grid owns the multi-selection).</summary>
    public event EventHandler<IReadOnlyList<WatchItemViewModel>>? WatchSelectionRequested;

    /// <summary>Selects watch items without a recent update (see <see cref="WatchItemViewModel.IsStale"/>).</summary>
    [RelayCommand]
    private void SelectStale() => SelectWatchItems(i => i.IsStale, "stale");

    /// <summary>Selects watch items whose latest status is Bad.</summary>
    [RelayCommand]
    private void SelectBad() => SelectWatchItems(i => i.IsBad, "bad");

    /// <summary>Selects watch items that are stale or Bad.</summary>
    [RelayCommand]
    private void SelectStaleOrBad() => SelectWatchItems(i => i.IsStale || i.IsBad, "stale or bad");

    /// <summary>Removes stale watch items (double-click on the stale toolbar button).</summary>
    [RelayCommand]
    private Task RemoveStaleAsync() => RemoveMatchingAsync(i => i.IsStale, "stale");

    /// <summary>Removes watch items with Bad status (double-click on the bad toolbar button).</summary>
    [RelayCommand]
    private Task RemoveBadAsync() => RemoveMatchingAsync(i => i.IsBad, "bad");

    private async Task RemoveMatchingAsync(Func<WatchItemViewModel, bool> predicate, string kind)
    {
        var items = WatchItems.Where(predicate).ToList();
        await RemoveWatchItemsAsync(items);
        StatusMessage = items.Count switch
        {
            0 => $"No {kind} values",
            1 => $"Removed 1 {kind} value from watch",
            _ => $"Removed {items.Count} {kind} values from watch",
        };
    }

    private void SelectWatchItems(Func<WatchItemViewModel, bool> predicate, string kind)
    {
        var items = WatchItems.Where(predicate).ToList();
        StatusMessage = items.Count switch
        {
            0 => $"No {kind} values",
            1 => $"1 {kind} value selected",
            _ => $"{items.Count} {kind} values selected",
        };
        WatchSelectionRequested?.Invoke(this, items);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedWatchItem))]
    private async Task SetRefreshAsync(int refreshMs)
    {
        var items = WatchSelectionOrCurrent().Where(i => i.RefreshMs != refreshMs && i.Monitor is not null).ToList();
        if (items.Count == 0 || !IsConnected)
        {
            return;
        }

        var byNodeId = items.ToDictionary(i => i.NodeId);
        try
        {
            var results = await _client.ChangeRefreshAsync(
                [.. items.Select(i => i.Monitor!)],
                update =>
                {
                    if (byNodeId.TryGetValue(update.NodeId, out var item))
                    {
                        _pendingUpdates[item] = update;
                    }
                },
                refreshMs);

            foreach (var result in results)
            {
                var item = byNodeId[result.NodeId];
                item.Monitor = result.Handle;
                if (result.Handle is not null)
                {
                    item.RefreshMs = refreshMs;
                }
            }

            MarkDirty();
            StatusMessage = $"Refresh time {FormatRefresh(refreshMs)} for {items.Count} item(s)";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
    }

    [RelayCommand]
    private Task MonitorNodeAsync(NodeViewModel? node) =>
        node is { IsVariable: true } && IsConnected ? AddWatchItemsAsync([(node.NodeId, node.DisplayName)]) : Task.CompletedTask;

    private bool CanMonitorContents() => IsConnected && SelectionOrCurrent().Any(n => n.HasChildren);

    [RelayCommand(CanExecute = nameof(CanMonitorContents))]
    private Task MonitorFolderAsync() => MonitorContentsAsync(maxDepth: MaxRecursiveDepth, maxCount: MaxRecursiveItems);

    private const int MaxRecursiveDepth = 10;

    private int MaxRecursiveItems => Settings.MaxRecursiveItems;

    private Task MonitorContentsAsync(int maxDepth, int maxCount) =>
        MonitorContentsAsync(SelectionOrCurrent().Where(n => n.HasChildren).ToList(), maxDepth, maxCount);

    /// <summary>Drop target for nodes dragged from the address space: variables are watched, folders recursively.</summary>
    public async Task DropNodesAsync(IReadOnlyList<NodeViewModel> nodes)
    {
        if (!IsConnected || nodes.Count == 0)
        {
            return;
        }

        var before = WatchItems.Count;
        await AddWatchItemsAsync([.. nodes.Where(n => n.IsVariable).Select(n => (n.NodeId, n.DisplayName))]);
        var containers = nodes.Where(n => n.HasChildren).ToList();
        if (containers.Count > 0)
        {
            await MonitorContentsAsync(containers, MaxRecursiveDepth, MaxRecursiveItems, descendIntoVariables: true);
        }

        StatusMessage = $"Dropped {nodes.Count} node(s): {WatchItems.Count - before} variable(s) added to watch";
    }

    private async Task MonitorContentsAsync(List<NodeViewModel> parents, int maxDepth, int maxCount, bool descendIntoVariables = false)
    {
        IsBusy = true;
        StatusMessage = "Collecting variables recursively…";
        try
        {
            var variables = new List<BrowseItem>();
            foreach (var parent in parents)
            {
                variables.AddRange(await _client.CollectVariablesAsync(parent.NodeId, maxDepth, maxCount - variables.Count, descendIntoVariables));
                if (variables.Count >= maxCount)
                {
                    break;
                }
            }

            await AddWatchItemsAsync(variables.Select(v => (v.NodeId, v.DisplayName)).ToList());
            StatusMessage = variables.Count >= maxCount
                ? $"Monitoring limited to the first {maxCount} variables"
                : $"Found {variables.Count} variable(s)";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task AddWatchItemsAsync(IReadOnlyList<(NodeId NodeId, string DisplayName)> nodes, int? refreshMs = null) =>
        AddWatchItemsAsync([.. nodes.Select(n => (n.NodeId, n.DisplayName, refreshMs ?? DefaultRefreshMs))]);

    private async Task AddWatchItemsAsync(IReadOnlyList<(NodeId NodeId, string DisplayName, int RefreshMs)> nodes)
    {
        foreach (var group in nodes.GroupBy(n => n.RefreshMs))
        {
            await AddWatchGroupAsync([.. group.Select(n => (n.NodeId, n.DisplayName))], group.Key);
        }
    }

    private async Task AddWatchGroupAsync(List<(NodeId NodeId, string DisplayName)> nodes, int refreshMs)
    {
        var watched = WatchItems.Select(w => w.NodeId).ToHashSet();
        var items = nodes
            .Where(n => watched.Add(n.NodeId))
            .Select(n => new WatchItemViewModel(n.NodeId, n.DisplayName) { PortableId = _client.ToPortableId(n.NodeId), NodeIdText = _client.ToDisplayId(n.NodeId), RefreshMs = refreshMs })
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            WatchItems.Add(item);
        }

        NotifySelectionCommands();

        var byNodeId = items.ToDictionary(i => i.NodeId);
        try
        {
            var results = await _client.MonitorManyAsync(
                items.Select(i => i.NodeId).ToList(),
                update =>
                {
                    if (byNodeId.TryGetValue(update.NodeId, out var item))
                    {
                        _pendingUpdates[item] = update;
                    }
                },
                refreshMs);

            var rejected = results.Where(r => r.Handle is null).ToList();
            foreach (var result in results)
            {
                if (result.Handle is { } handle)
                {
                    byNodeId[result.NodeId].Monitor = handle;
                }
                else
                {
                    WatchItems.Remove(byNodeId[result.NodeId]);
                }
            }

            if (rejected.Count > 0)
            {
                ErrorMessage = $"{rejected.Count} item(s) could not be monitored, e.g. {byNodeId[rejected[0].NodeId].DisplayName}: {rejected[0].Error.StatusCode.SymbolicId}";
            }
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            foreach (var item in items)
            {
                WatchItems.Remove(item);
            }

            ReportError(ex);
        }

        NotifySelectionCommands();
    }

    private void NotifySelectionCommands()
    {
        AddToWatchCommand.NotifyCanExecuteChanged();
        MonitorSelectedWithRefreshCommand.NotifyCanExecuteChanged();
        MonitorFolderCommand.NotifyCanExecuteChanged();
        CopyNodeIdCommand.NotifyCanExecuteChanged();
        CopyNodeJsonCommand.NotifyCanExecuteChanged();
        CopyNodeClassCommand.NotifyCanExecuteChanged();
        CopyNodeRecordCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<WatchItemViewModel> SelectedWatchItems { get; } = [];

    public WatchColumnsViewModel WatchColumns { get; } = new();

    public void MarkLayoutChanged() => MarkDirty();

    [RelayCommand]
    private void ResetWatchColumns()
    {
        WatchColumns.Reset();
        MarkDirty();
    }

    public string WatchSelectionLabel => WatchSelectionOrCurrent().Count switch
    {
        0 or 1 => string.Empty,
        var n => $" ({n} items)",
    };

    private List<WatchItemViewModel> WatchSelectionOrCurrent() =>
        SelectedWatchItems.Count > 0 ? [.. SelectedWatchItems] : SelectedWatchItem is { } item ? [item] : [];

    private bool HasSelectedWatchItem() => SelectedWatchItems.Count > 0 || SelectedWatchItem is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedWatchItem))]
    private Task RemoveFromWatchAsync() => RemoveWatchItemsAsync(WatchSelectionOrCurrent());

    private async Task RemoveWatchItemsAsync(List<WatchItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            WatchItems.Remove(item);
            _pendingUpdates.TryRemove(item, out _);
        }

        SelectedWatchItems.Clear();
        NotifySelectionCommands();

        try
        {
            await DeviceClient.StopMonitoringAsync(items.Select(i => i.Monitor).OfType<IAsyncDisposable>());
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }

        if (items.Count > 1)
        {
            StatusMessage = $"Removed {items.Count} items from watch";
        }
    }

    private const int ExpandAllMaxDepth = 5;
    private const int ExpandAllMaxNodes = 2000;

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ExpandAllAsync()
    {
        var targets = SelectionOrCurrent().Where(n => n.HasChildren).ToList();
        if (targets.Count == 0)
        {
            targets = [.. RootNodes];
        }

        IsBusy = true;
        try
        {
            var limited = false;
            foreach (var node in targets)
            {
                limited |= await node.ExpandAllAsync(ExpandAllMaxDepth, ExpandAllMaxNodes);
            }

            StatusMessage = limited
                ? $"Expanded {ExpandAllMaxDepth} levels (limit reached — expand deeper nodes individually)"
                : "Expanded all";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var root in RootNodes)
        {
            root.CollapseAll();
        }

        foreach (var root in RootNodes)
        {
            root.IsExpanded = true;
        }
    }

    public event EventHandler<NodeViewModel>? RevealRequested;

    [RelayCommand]
    private async Task RevealInTreeAsync(WatchItemViewModel? item)
    {
        if ((item ?? SelectedWatchItem) is not { } target || !IsConnected || RootNodes.Count == 0)
        {
            return;
        }

        try
        {
            var path = await _client.GetPathFromRootAsync(target.NodeId);
            if (path.Count == 0)
            {
                StatusMessage = $"'{target.DisplayName}' is not reachable from Root in the address space";
                return;
            }

            var node = RootNodes[0];
            foreach (var id in path.Skip(1))
            {
                await node.EnsureChildrenLoadedAsync();
                var next = node.Children.FirstOrDefault(c => c.NodeId == id);
                if (next is null)
                {
                    StatusMessage = $"Could not find '{target.DisplayName}' under {node.DisplayName}";
                    return;
                }

                node = next;
            }

            SelectedNodes.Clear();
            SelectedNodes.Add(node);
            SelectedNode = node;
            if (Layout is not null)
            {
                DockFactory.ShowPane(Layout, "AddressSpace");
            }

            RevealRequested?.Invoke(this, node);
            StatusMessage = $"Selected {target.DisplayName} in address space";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
    }

    private bool HasSelectedNode() => SelectedNodes.Count > 0 || SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedNode))]
    private Task CopyNodeIdAsync() => CopyAsync(string.Join(Environment.NewLine, SelectionOrCurrent().Select(n => n.NodeIdText)));

    private bool CanCopyNodeTree() => IsConnected && HasSelectedNode();

    [RelayCommand(CanExecute = nameof(CanCopyNodeTree))]
    private Task CopyNodeJsonAsync() => CopyNodeTreeAsync(trees => trees.Count == 1
        ? NodeExport.ToJsonString(trees[0])
        : new System.Text.Json.Nodes.JsonObject(trees.Select(t => System.Collections.Generic.KeyValuePair.Create(t.DisplayName, NodeExport.ToJson(t))))
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    [RelayCommand(CanExecute = nameof(CanCopyNodeTree))]
    private Task CopyNodeClassAsync() => CopyNodeTreeAsync(trees => string.Join(Environment.NewLine, trees.Select(t => NodeExport.ToCSharp(t, formatId: _client.ToDisplayId))));

    [RelayCommand(CanExecute = nameof(CanCopyNodeTree))]
    private Task CopyNodeRecordAsync() => CopyNodeTreeAsync(trees => string.Join(Environment.NewLine, trees.Select(t => NodeExport.ToCSharp(t, asRecord: true, formatId: _client.ToDisplayId))));

    private async Task CopyNodeTreeAsync(Func<List<NodeTree>, string> format)
    {
        var nodes = SelectionOrCurrent().ToList();
        if (nodes.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Only nodes without picked descendants need their subtree read; the others just hold the picks.
            var picked = nodes.Select(n => n.NodeId).ToHashSet();
            var containers = nodes.SelectMany(n => n.Ancestors).Where(a => picked.Contains(a.NodeId)).Select(a => a.NodeId).ToHashSet();
            var selections = new List<(IReadOnlyList<NodeTree> Ancestors, NodeTree Tree)>();
            foreach (var node in nodes)
            {
                var tree = containers.Contains(node.NodeId)
                    ? new NodeTree(node.NodeId, node.DisplayName, node.NodeClass)
                    : await _client.ReadTreeAsync(node.NodeId, node.DisplayName, node.NodeClass, MaxRecursiveDepth, MaxRecursiveItems);
                selections.Add(([.. node.Ancestors.Select(a => new NodeTree(a.NodeId, a.DisplayName, a.NodeClass))], tree));
            }

            var trees = NodeExport.ComposeSelection(selections);
            await CopyAsync(format(trees));
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasSelectedAttribute() => SelectedAttribute is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedAttribute))]
    private Task CopyAttributeValueAsync() => CopyAsync(SelectedAttribute?.Value);

    [RelayCommand(CanExecute = nameof(HasSelectedWatchItem))]
    private Task CopyWatchValueAsync() => CopyAsync(string.Join(Environment.NewLine, WatchSelectionOrCurrent().Select(w => w.Value)));

    [RelayCommand(CanExecute = nameof(HasSelectedWatchItem))]
    private Task CopyWatchJsonAsync()
    {
        var items = WatchSelectionOrCurrent().Select(w => new WatchSnapshot(
            w.DisplayName, w.PortableId, ValueJson.ToJson(w.RawValue), ValueJson.TypeName(w.RawValue), w.Status, w.RefreshMs, w.LastUpdate,
            string.IsNullOrEmpty(w.SourceTimestamp) ? null : w.SourceTimestamp)).ToList();
        var json = items.Count == 1
            ? System.Text.Json.JsonSerializer.Serialize(items[0], AppJsonContext.Default.WatchSnapshot)
            : System.Text.Json.JsonSerializer.Serialize(items, AppJsonContext.Default.ListWatchSnapshot);
        return CopyAsync(json);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedWatchItem))]
    private Task CopyWatchValuesJsonAsync()
    {
        var items = WatchSelectionOrCurrent();
        var json = items.Count == 1
            ? ValueJson.ToJson(items[0].RawValue)
            : new System.Text.Json.Nodes.JsonObject(items
                .GroupBy(w => w.DisplayName)
                .SelectMany(g => g.Count() == 1 ? g.Select(w => (Key: w.DisplayName, Item: w)) : g.Select(w => (Key: w.PortableId, Item: w)))
                .Select(p => System.Collections.Generic.KeyValuePair.Create(p.Key, ValueJson.ToJson(p.Item.RawValue))));
        return CopyAsync(json?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null");
    }

    [RelayCommand(CanExecute = nameof(HasSelectedWatchItem))]
    private Task CopyWatchNodeIdAsync() => CopyAsync(string.Join(Environment.NewLine, WatchSelectionOrCurrent().Select(w => w.NodeIdText)));

    partial void OnSelectedNodeChanged(NodeViewModel? value) => _ = LoadAttributesAsync(value);

    public async ValueTask DisposeAsync()
    {
        _flushTimer.Stop();
        AppErrors.Reported -= OnAppError;

        // Shutdown must not fail because a server is gone or a recording file is locked.
        foreach (var recording in Recordings.ToList())
        {
            try
            {
                await recording.DisposeAsync();
            }
            catch (Exception ex) when (AppErrors.IsRecoverable(ex))
            {
                AppErrors.Log(ex, "closing recording");
            }
        }

        try
        {
            await _client.DisposeAsync();
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            AppErrors.Log(ex, "closing connection");
        }
    }

    private Task<IReadOnlyList<BrowseItem>> Browse(NodeId nodeId) => _client.BrowseAsync(nodeId);

    private async Task CopyAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text) && CopyToClipboard is { } copy)
        {
            await copy(text);
            StatusMessage = "Copied to clipboard";
        }
    }

    private IAsyncDisposable? _selectedValueMonitor;

    [ObservableProperty]
    public partial bool IsSelectedValueLive { get; private set; }

    private async Task StopSelectedValueMonitorAsync()
    {
        IsSelectedValueLive = false;
        if (Interlocked.Exchange(ref _selectedValueMonitor, null) is { } monitor)
        {
            try
            {
                await monitor.DisposeAsync();
            }
            catch (Exception ex) when (AppErrors.IsRecoverable(ex))
            {
                ReportError(ex);
            }
        }
    }

    private void ApplySelectedValue(NodeViewModel node, ValueUpdate update) => Dispatcher.UIThread.Post(() =>
    {
        if (SelectedNode != node)
        {
            return;
        }

        SetAttribute("Value", update.Value);
        SetAttribute("StatusCode", update.Status.SymbolicId ?? update.Status.ToString());
        if (update.SourceTimestamp != DateTime.MinValue)
        {
            SetAttribute("SourceTimestamp", update.SourceTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
        }
    });

    private void SetAttribute(string name, string value)
    {
        for (var i = 0; i < Attributes.Count; i++)
        {
            if (Attributes[i].Name == name)
            {
                if (Attributes[i].Value != value)
                {
                    Attributes[i] = new AttributeValue(name, value);
                }

                return;
            }
        }

        var after = Attributes.ToList().FindIndex(a => a.Name == "Value");
        Attributes.Insert(after < 0 ? Attributes.Count : after + 1, new AttributeValue(name, value));
    }

    partial void OnDefaultRefreshMsChanged(int value)
    {
        MarkDirty();
        if (SelectedNode is { IsVariable: true } node && IsConnected)
        {
            _ = LoadAttributesAsync(node);
        }
    }

    private async Task LoadAttributesAsync(NodeViewModel? node)
    {
        await StopSelectedValueMonitorAsync();
        Attributes.Clear();
        if (node is null || !IsConnected)
        {
            return;
        }

        try
        {
            var attributes = await _client.ReadAttributesAsync(node.NodeId);
            if (SelectedNode != node)
            {
                return;
            }

            foreach (var attribute in attributes)
            {
                Attributes.Add(attribute);
            }

            if (node.IsVariable)
            {
                var monitor = await _client.MonitorAsync(node.NodeId, update => ApplySelectedValue(node, update), DefaultRefreshMs);
                if (SelectedNode == node && _selectedValueMonitor is null)
                {
                    _selectedValueMonitor = monitor;
                    IsSelectedValueLive = true;
                }
                else
                {
                    await monitor.DisposeAsync();
                }
            }
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
    }

    private DateTimeOffset _lastAgeRefresh;

    private void FlushUpdates()
    {
        var now = DateTimeOffset.Now;
        if (now - _lastAgeRefresh >= TimeSpan.FromSeconds(1))
        {
            _lastAgeRefresh = now;
            foreach (var watched in WatchItems)
            {
                watched.RefreshAge(now);
            }
        }

        foreach (var item in _pendingUpdates.Keys)
        {
            if (_pendingUpdates.TryRemove(item, out var update))
            {
                item.Apply(update);
            }
        }
    }

    private async Task StopAllMonitorsAsync()
    {
        await StopSelectedValueMonitorAsync();
        await StopAllRecordingsAsync();
        var items = WatchItems.ToList();
        WatchItems.Clear();
        SelectedWatchItems.Clear();
        _pendingUpdates.Clear();
        try
        {
            var monitors = items.Select(i => i.Monitor).OfType<IAsyncDisposable>().ToList();
            await Task.Run(() => DeviceClient.StopMonitoringAsync(monitors));
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
    }

    private const string ConnectionLostMessage = "Connection lost. Reconnecting…";

    /// <summary>Set while the user disconnects, so the resulting Disconnected state is not reported as a failure.</summary>
    private bool _disconnecting;

    private void HandleClientState(ConnectionState state)
    {
        var previous = State;
        State = state;
        if (state == ConnectionState.Reconnecting)
        {
            StatusMessage = "Connection lost, reconnecting…";
            ErrorMessage = ConnectionLostMessage;
        }
        else if (state == ConnectionState.Connected && previous == ConnectionState.Reconnecting)
        {
            StatusMessage = "Reconnected";
            if (ErrorMessage == ConnectionLostMessage)
            {
                ErrorMessage = null;
            }
        }
        else if (state == ConnectionState.Disconnected && previous is ConnectionState.Connected or ConnectionState.Reconnecting && !_disconnecting)
        {
            StatusMessage = "Disconnected";
            ErrorMessage = "Connection lost and could not be restored. Connect again to continue.";
        }
    }

    private void ReportError(Exception ex)
    {
        AppErrors.Log(ex);
        var message = AppErrors.Describe(ex);
        Dispatcher.UIThread.Post(() => ErrorMessage = message);
    }

    /// <summary>Errors caught by the global handlers (<see cref="AppErrors"/>) land in the same error bar.</summary>
    private void OnAppError(object? sender, string message) => ErrorMessage = message;
}
