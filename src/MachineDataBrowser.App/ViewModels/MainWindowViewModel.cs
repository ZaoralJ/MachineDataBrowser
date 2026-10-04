using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Ua;

namespace MachineDataBrowser.App.ViewModels;

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
        EndpointUrl = DefaultEndpointUrl;
        DefaultRefreshMs = DefaultRefreshFor(EndpointUrl);
        IsDirty = false;
        ApplyTheme(Settings.Theme);
        ApplyColorTheme(Settings.ColorTheme);
        Layout = layoutStore?.TryLoad(DockFactory) ?? DockFactory.CreateLayout();

        _client.StateChanged += OnClientStateChanged;
        AppErrors.Reported += OnAppError;
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushUpdates());
        _treeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) =>
        {
            await RefreshTreeIfChangedAsync();
            UnloadCollapsedNodes();
        });
        _treeTimer.Start();
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
            WriteWatchValueCommand.NotifyCanExecuteChanged();
            EditMonitoringCommand.NotifyCanExecuteChanged();
            EditDisplayCommand.NotifyCanExecuteChanged();
            ShowWatchHistoryCommand.NotifyCanExecuteChanged();
        };
        WatchItems.CollectionChanged += (_, _) =>
        {
            TakeSnapshotCommand.NotifyCanExecuteChanged();
            CopyWatchCliMonitorCommand.NotifyCanExecuteChanged();
            CopyWatchCliReadCommand.NotifyCanExecuteChanged();
        };
        Bookmarks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasBookmarks));
        SelectedNodes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectionSummary));
            NotifySelectionCommands();
        };
    }

    public Func<string, Task>? CopyToClipboard { get; set; }

    private readonly DispatcherTimer _treeTimer;
    private int _addressSpaceDirty;
    private bool _refreshingTree;

    /// <summary>MQTT thread: only flag it; the flush timer refreshes the expanded tree at most every few ticks.</summary>
    private void OnAddressSpaceChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref _addressSpaceDirty, 1);

    private async Task RefreshTreeIfChangedAsync()
    {
        if (_refreshingTree || !IsConnected || RootNodes.Count == 0 || Interlocked.Exchange(ref _addressSpaceDirty, 0) == 0)
        {
            return;
        }

        _refreshingTree = true;
        try
        {
            await RootNodes[0].RefreshExpandedAsync();
        }
        finally
        {
            _refreshingTree = false;
        }
    }

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
        if (old is IDynamicAddressSpace oldDynamic)
        {
            oldDynamic.AddressSpaceChanged -= OnAddressSpaceChanged;
        }

        if (old is IPausableDiscovery oldDiscovery)
        {
            oldDiscovery.DiscoveryPausedChanged -= OnDiscoveryPausedChanged;
        }

        _client = DeviceClient.Create(endpointUrl);
        _client.StateChanged += OnClientStateChanged;
        if (_client is IDynamicAddressSpace dynamic)
        {
            dynamic.AddressSpaceChanged += OnAddressSpaceChanged;
        }

        if (_client is IPausableDiscovery discovery)
        {
            discovery.DiscoveryPausedChanged += OnDiscoveryPausedChanged;
        }
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
    [NotifyPropertyChangedFor(nameof(IsOpcUaEndpoint), nameof(IsMqttEndpoint), nameof(HasCredentials), nameof(OptionsSummary))]
    public partial string EndpointUrl { get; set; } = "opc.tcp://localhost:50000";

    /// <summary>Security, credentials and certificate trust only apply to OPC UA, not to EtherNet/IP (<c>eip://</c>).</summary>
    public bool IsOpcUaEndpoint => !DeviceClient.IsEip(EndpointUrl ?? string.Empty) && !DeviceClient.IsMqtt(EndpointUrl ?? string.Empty);

    /// <summary>User name, password and certificate trust apply to OPC UA and MQTT (mqtts://, wss://), not to EtherNet/IP.</summary>
    public bool HasCredentials => !DeviceClient.IsEip(EndpointUrl ?? string.Empty);

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

    public static string FormatRefresh(int ms) => ms switch
    {
        <= 0 => "all",
        >= 1000 when ms % 1000 == 0 => $"{ms / 1000} s",
        _ => $"{ms} ms",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsDisconnected), nameof(StateText), nameof(SupportsEvents), nameof(SupportsHistory), nameof(SupportsMethods), nameof(SupportsMonitoringSettings), nameof(SupportsDiscoveryPause), nameof(IsDiscoveryPaused), nameof(DiscoveryToolTip))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(AddToWatchCommand), nameof(MonitorFolderCommand), nameof(ExpandAllCommand), nameof(NewRecordingCommand), nameof(RecordAllCommand), nameof(SearchCommand), nameof(WriteAttributeValueCommand), nameof(WriteWatchValueCommand), nameof(ShowEventsCommand), nameof(ShowHistoryCommand), nameof(ShowWatchHistoryCommand), nameof(CallMethodCommand), nameof(EditMonitoringCommand), nameof(ToggleBookmarkCommand), nameof(ToggleDiscoveryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCliBrowseCommand), nameof(CopyCliReadCommand), nameof(CopyCliMonitorCommand), nameof(CopyWatchCliMonitorCommand), nameof(CopyWatchCliReadCommand))]
    public partial ConnectionState State { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToWatchCommand), nameof(CopyNodeIdCommand), nameof(MonitorFolderCommand), nameof(CopyNodeJsonCommand), nameof(CopyNodeClassCommand), nameof(CopyNodeRecordCommand), nameof(WriteAttributeValueCommand), nameof(CallMethodCommand), nameof(ToggleBookmarkCommand))]
    public partial NodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyAttributeValueCommand), nameof(WriteAttributeValueCommand))]
    [NotifyPropertyChangedFor(nameof(IsValueAttributeSelected))]
    public partial AttributeValue? SelectedAttribute { get; set; }

    /// <summary>The Value row is selected; only it offers "Write value…".</summary>
    public bool IsValueAttributeSelected => SelectedAttribute?.Name == "Value";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromWatchCommand), nameof(CopyWatchValueCommand), nameof(CopyWatchNodeIdCommand), nameof(WriteWatchValueCommand), nameof(EditMonitoringCommand), nameof(EditDisplayCommand))]
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
            var summary = OptionsSummaryCore();
            return IsReadOnly ? summary + " · read-only" : summary;
        }
    }

    private string OptionsSummaryCore()
    {
        var security = UseSecurity ? "Secure" : "No security";
        var user = string.IsNullOrWhiteSpace(UserName) ? "Anonymous" : UserName;
        var refresh = FormatRefresh(DefaultRefreshMs);
        if (DeviceClient.IsEip(EndpointUrl ?? string.Empty))
        {
            return $"EtherNet/IP · {refresh}";
        }

        if (DeviceClient.IsMqtt(EndpointUrl ?? string.Empty))
        {
            var mqtt = $"MQTT · {user} · {refresh}";
            if (AutoPauseDiscoverySeconds > 0)
            {
                mqtt += $" · pause after {AutoPauseDiscoverySeconds} s";
            }

            return AutoAcceptCertificates ? mqtt + " · auto-trust" : mqtt;
        }

        return AutoAcceptCertificates ? $"{security} · {user} · {refresh} · auto-trust" : $"{security} · {user} · {refresh}";
    }

    public ObservableCollection<NodeViewModel> RootNodes { get; } = [];

    private FlatTree? _treeRows;

    /// <summary>The visible tree rows (flattened for the virtualized address-space list).</summary>
    public BulkObservableCollection<NodeViewModel> TreeRows => (_treeRows ??= CreateFlatTree()).Rows;

    private FlatTree CreateFlatTree()
    {
        var tree = new FlatTree(RootNodes);
        tree.Collapsing += OnTreeCollapsing;
        tree.Collapsed += OnTreeCollapsed;
        return tree;
    }

    public ObservableCollection<NodeViewModel> SelectedNodes { get; } = [];

    public string SelectionSummary => SelectedNodes.Count > 1 ? $"{SelectedNodes.Count} nodes selected" : string.Empty;

    public ObservableCollection<AttributeValue> Attributes { get; } = [];

    public BulkObservableCollection<WatchItemViewModel> WatchItems { get; } = [];

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
                AutoPauseDiscoverySeconds = IsMqttEndpoint ? AutoPauseDiscoverySeconds : 0,
                UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName,
                Password = Password,
            };

            // The SDKs do synchronous work while connecting (endpoint discovery, certificates, type system,
            // libplctag tag creation); run it on the pool so the window stays responsive.
            var client = _client;
            try
            {
                await Task.Run(() => client.ConnectAsync(options));
            }
            catch (Exception ex) when (AppErrors.IsRecoverable(ex)
                && client is IServerCertificateTrust { LastUntrustedCertificate: { } certificate } trust
                && Dialogs is not null)
            {
                // An untrusted server certificate: show it and let the user decide instead of failing.
                var choice = await Dialogs.AskTrustCertificateAsync(certificate, EndpointUrl.Trim());
                if (choice == CertificateTrustChoice.Cancel)
                {
                    throw;
                }

                if (choice == CertificateTrustChoice.Always)
                {
                    trust.TrustPermanently(certificate);
                }

                StatusMessage = $"Connecting to {EndpointUrl}…";
                var trusted = options with { AcceptedCertificateThumbprints = [certificate.Thumbprint] };
                await Task.Run(() => client.ConnectAsync(trusted));
            }

            ClearSearch();
            RootNodes.Clear();
            var root = new NodeViewModel(
                _client.Root,
                Browse,
                ReportError,
                _client.ToDisplayId,
                probe: ProbeHasChildren);
            RootNodes.Add(root);
            root.IsExpanded = true;

            State = _client.State;
            StatusMessage = $"Connected to {_client.ServerUri ?? EndpointUrl}";
            UpdateSettings(Settings.WithRecentEndpoint(EndpointUrl.Trim()));
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            State = _client.State;
            StatusMessage = string.Empty;
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
            await CloseEventViewersAsync();
            var client = _client;
            await Task.Run(client.DisconnectAsync);
            State = _client.State;
            RootNodes.Clear();
            Attributes.Clear();
            StatusMessage = string.Empty;
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            // The session is unusable either way; show why and leave the UI disconnected.
            ReportError(ex);
            State = ConnectionState.Disconnected;
            RootNodes.Clear();
            Attributes.Clear();
            StatusMessage = string.Empty;
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
        AddWatchItemsAsync(SelectionOrCurrent().Where(n => n.IsVariable).Select(n => (n.NodeId, n.DisplayName, n.ParentPath)).ToList());

    [RelayCommand(CanExecute = nameof(CanAddToWatch))]
    private Task MonitorSelectedWithRefreshAsync(int refreshMs) =>
        AddWatchItemsAsync(SelectionOrCurrent().Where(n => n.IsVariable).Select(n => (n.NodeId, n.DisplayName, n.ParentPath)).ToList(), refreshMs);

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

        if (await ChangeRefreshAsync(items, refreshMs))
        {
            StatusMessage = $"Refresh time {FormatRefresh(refreshMs)} for {items.Count} item(s)";
        }
    }

    /// <summary>Moves the items to the subscription publishing every <paramref name="refreshMs"/>; false on error.</summary>
    private async Task<bool> ChangeRefreshAsync(List<WatchItemViewModel> items, int refreshMs)
    {
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

            // Re-created monitors start with default settings: apply the items' own again.
            await ReapplyMonitoringAsync([.. items.Where(i => !i.Monitoring.IsDefault && i.Monitor is not null)]);
            MarkDirty();
            return true;
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
            return false;
        }
    }

    [RelayCommand]
    private Task MonitorNodeAsync(NodeViewModel? node) =>
        node is { IsVariable: true } && IsConnected ? AddWatchItemsAsync([(node.NodeId, node.DisplayName, node.ParentPath)]) : Task.CompletedTask;

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
        await AddWatchItemsAsync([.. nodes.Where(n => n.IsVariable).Select(n => (n.NodeId, n.DisplayName, n.ParentPath))]);
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
            // Browsing thousands of nodes happens off the UI thread; only the result comes back.
            var client = _client;
            var roots = parents.Select(p => (p.NodeId, p.Path)).ToList();
            var variables = await Task.Run(async () =>
            {
                var found = new List<(NodeId NodeId, string DisplayName, string Path)>();
                foreach (var root in roots)
                {
                    var collected = await client.CollectVariablesWithPathsAsync(root.NodeId, maxDepth, maxCount - found.Count, descendIntoVariables);
                    found.AddRange(collected.Select(v => (v.Item.NodeId, v.Item.DisplayName, JoinPath(root.Path, v.Path))));
                    if (found.Count >= maxCount)
                    {
                        break;
                    }
                }

                return found;
            });

            await AddWatchItemsAsync(variables);
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

    private static string JoinPath(string parent, string child) =>
        parent.Length == 0 ? child : child.Length == 0 ? parent : $"{parent}/{child}";

    private Task AddWatchItemsAsync(IReadOnlyList<(NodeId NodeId, string DisplayName, string Path)> nodes, int? refreshMs = null) =>
        AddWatchItemsAsync([.. nodes.Select(n => (n.NodeId, n.DisplayName, n.Path, refreshMs ?? DefaultRefreshMs))]);

    private async Task AddWatchItemsAsync(IReadOnlyList<(NodeId NodeId, string DisplayName, string Path, int RefreshMs)> nodes)
    {
        foreach (var group in nodes.GroupBy(n => n.RefreshMs))
        {
            await AddWatchGroupAsync([.. group.Select(n => (n.NodeId, n.DisplayName, n.Path))], group.Key);
        }
    }

    private async Task AddWatchGroupAsync(List<(NodeId NodeId, string DisplayName, string Path)> nodes, int refreshMs)
    {
        var watched = WatchItems.Select(w => w.NodeId).ToHashSet();
        var items = nodes
            .Where(n => watched.Add(n.NodeId))
            .Select(n => new WatchItemViewModel(n.NodeId, n.DisplayName) { Path = n.Path, PortableId = _client.ToPortableId(n.NodeId), NodeIdText = _client.ToDisplayId(n.NodeId), RefreshMs = refreshMs })
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        // One Reset for the whole batch: per-item adds re-laid out the grid and re-ran every listener per row.
        WatchItems.AddRange(items);
        NotifySelectionCommands();

        var byNodeId = items.ToDictionary(i => i.NodeId);
        try
        {
            var client = _client;
            var ids = items.Select(i => i.NodeId).ToList();
            var results = await Task.Run(() => client.MonitorManyAsync(
                ids,
                update =>
                {
                    if (byNodeId.TryGetValue(update.NodeId, out var item))
                    {
                        _pendingUpdates[item] = update;
                    }
                },
                refreshMs));

            var rejected = results.Where(r => r.Handle is null).ToList();
            foreach (var result in results)
            {
                if (result.Handle is { } handle)
                {
                    byNodeId[result.NodeId].Monitor = handle;
                }
            }

            WatchItems.RemoveRange(rejected.Select(r => byNodeId[r.NodeId]));
            _ = LoadUnitsAsync([.. items.Where(i => i.Monitor is not null)]);

            if (rejected.Count > 0)
            {
                ErrorMessage = $"{rejected.Count} item(s) could not be monitored, e.g. {byNodeId[rejected[0].NodeId].DisplayName}: {rejected[0].Error.StatusCode.SymbolicId}";
            }
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            WatchItems.RemoveRange(items);
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
        CopyCliBrowseCommand.NotifyCanExecuteChanged();
        CopyCliReadCommand.NotifyCanExecuteChanged();
        CopyCliMonitorCommand.NotifyCanExecuteChanged();
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

        WatchItems.RemoveRange(items);
        foreach (var item in items)
        {
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

        StatusMessage = items.Count == 1 ? "Removed 1 item from watch" : $"Removed {items.Count} items from watch";
        if (WatchItems.Count == 0)
        {
            await AskAboutActiveRecordingsAsync();
        }
    }

    /// <summary>
    /// Recordings monitor their items independently of the watch list, so emptying the list leaves them running.
    /// Ask whether to stop (keep history) or close (discard) them.
    /// </summary>
    private async Task AskAboutActiveRecordingsAsync()
    {
        var active = Recordings.Where(r => r.Recording.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Scheduled).ToList();
        if (active.Count == 0 || Dialogs is null)
        {
            return;
        }

        try
        {
            switch (await Dialogs.AskActiveRecordingsAsync(active.Count))
            {
                case ActiveRecordingsChoice.Stop:
                    foreach (var recording in active)
                    {
                        var r = recording.Recording;
                        await Task.Run(() => r.StopAsync());
                        recording.Refresh();
                    }

                    StatusMessage = active.Count == 1 ? "Watch list cleared; recording stopped" : $"Watch list cleared; {active.Count} recordings stopped";
                    break;
                case ActiveRecordingsChoice.Close:
                    foreach (var recording in active)
                    {
                        Recordings.Remove(recording);
                        await recording.DisposeAsync();
                    }

                    SelectedRecording = Recordings.LastOrDefault();
                    NotifyRecordingCommands();
                    StatusMessage = active.Count == 1 ? "Watch list cleared; recording closed" : $"Watch list cleared; {active.Count} recordings closed";
                    break;
                default:
                    StatusMessage = "Watch list cleared; recordings keep running";
                    break;
            }
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
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
        var selection = SelectedNodes.ToList();
        var current = SelectedNode;
        var deferral = _treeRows?.DeferUpdates();
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
            // One rebuild instead of a row update per expanded folder; it resets the list, so restore the selection.
            deferral?.Dispose();
            if (deferral is not null && current is not null)
            {
                SelectedNodes.Clear();
                foreach (var node in selection)
                {
                    SelectedNodes.Add(node);
                }

                SelectedNode = current;
                RevealRequested?.Invoke(this, current);
            }

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

            await RevealPathAsync(path, target.DisplayName);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
    }

    /// <summary>Expands the tree along <paramref name="path"/> (Root first) and selects the last node.</summary>
    private async Task RevealPathAsync(IReadOnlyList<NodeId> path, string displayName)
    {
        var target = (DisplayName: displayName, NodeId: path[^1]);
        {
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

    private bool CanWriteAttributeValue() =>
        IsConnected && !IsReadOnly && IsValueAttributeSelected && SelectedNode is { IsVariable: true };

    [RelayCommand(CanExecute = nameof(CanWriteAttributeValue))]
    private async Task WriteAttributeValueAsync()
    {
        if (SelectedNode is not { } node || SelectedAttribute is not { } attribute)
        {
            return;
        }

        if (await WriteValuesAsync([(node.NodeId, node.DisplayName)], attribute.Value) && ReferenceEquals(SelectedNode, node))
        {
            await LoadAttributesAsync(node);
        }
    }

    private bool CanWriteWatchValue() => IsConnected && !IsReadOnly && HasSelectedWatchItem();

    [RelayCommand(CanExecute = nameof(CanWriteWatchValue))]
    private async Task WriteWatchValueAsync()
    {
        var items = WatchSelectionOrCurrent();
        if (items.Count > 0)
        {
            await WriteValuesAsync([.. items.Select(i => (i.NodeId, i.DisplayName))], items[0].Value);
        }
    }

    /// <summary>Asks for a new value and writes it to every target; returns true when all writes succeeded.</summary>
    private async Task<bool> WriteValuesAsync(IReadOnlyList<(NodeId NodeId, string Name)> targets, string current)
    {
        if (Dialogs is null)
        {
            return false;
        }

        // The commands are disabled in read-only mode; this guards every other way in (shortcuts, future callers).
        if (IsReadOnly)
        {
            StatusMessage = "Read-only mode: writing is turned off (Connection ▸ Read-Only)";
            return false;
        }

        var label = targets.Count == 1 ? targets[0].Name : $"{targets.Count} items";
        if (await Dialogs.AskWriteValueAsync(label, current) is not { } text)
        {
            return false;
        }

        var failures = new List<string>();
        foreach (var (nodeId, name) in targets)
        {
            try
            {
                await _client.WriteValueAsync(nodeId, text);
            }
            catch (Exception ex) when (AppErrors.IsRecoverable(ex))
            {
                failures.Add($"{name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            ReportError(new InvalidOperationException($"Write failed for {string.Join("; ", failures)}"));
            return false;
        }

        StatusMessage = $"Wrote {text} to {label}";
        return true;
    }

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

    partial void OnSelectedNodeChanged(NodeViewModel? value) => _ = LoadAttributesAsync(value, debounce: true);

    private Task? _shutdown;

    public void ShowShutdownProgress()
    {
        IsBusy = true;
        StatusMessage = Recordings.Count > 0 || IsConnected ? "Quitting: stopping recordings and closing the session…" : "Quitting…";
    }

    /// <summary>Graceful, idempotent shutdown used on quit (and by <see cref="DisposeAsync"/>).</summary>
    public Task ShutdownAsync() => _shutdown ??= DisposeCoreAsync();

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task DisposeCoreAsync()
    {
        _flushTimer.Stop();
        _treeTimer.Stop();
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
            await CloseEventViewersAsync();
            await _client.DisposeAsync();
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            AppErrors.Log(ex, "closing connection");
        }
    }

    /// <summary>A hung server shows an error row with a retry instead of "Loading…" until the SDK gives up.</summary>
    internal static TimeSpan BrowseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private Task<IReadOnlyList<BrowseItem>> Browse(NodeId nodeId)
    {
        var client = _client;
        return Task.Run(() => client.BrowseQuickAsync(nodeId)).WaitAsync(BrowseTimeout);
    }

    private Task<IReadOnlyList<bool>?> ProbeHasChildren(IReadOnlyList<NodeId> nodeIds)
    {
        var client = _client;
        return Task.Run(() => client.ProbeHasChildrenAsync(nodeIds)).WaitAsync(BrowseTimeout);
    }

    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(10);
    private DateTime _lastUnloadSweep = DateTime.UtcNow;

    /// <summary>Frees folders collapsed for a long time (they are browsed again when expanded).</summary>
    private void UnloadCollapsedNodes()
    {
        var now = DateTime.UtcNow;
        if (now - _lastUnloadSweep < TimeSpan.FromMinutes(1) || RootNodes.Count == 0)
        {
            return;
        }

        _lastUnloadSweep = now;
        var keep = new HashSet<NodeViewModel>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var selected in SelectedNodes.Append(SelectedNode).OfType<NodeViewModel>())
        {
            for (var n = selected; n is not null; n = n.Parent)
            {
                keep.Add(n);
            }
        }

        foreach (var root in RootNodes)
        {
            root.UnloadCollapsed(UnloadAfter, now, keep);
        }
    }

    private NodeViewModel? _reselectAfterCollapse;

    /// <summary>Collapsing a folder that hides the selection selects the folder, so the Attributes pane follows.</summary>
    private void OnTreeCollapsing(NodeViewModel owner, IReadOnlyList<NodeViewModel> hidden)
    {
        var set = new HashSet<NodeViewModel>(hidden, System.Collections.Generic.ReferenceEqualityComparer.Instance);
        _reselectAfterCollapse = (SelectedNode is { } s && set.Contains(s)) || SelectedNodes.Any(set.Contains) ? owner : null;
    }

    private void OnTreeCollapsed(NodeViewModel owner)
    {
        if (!ReferenceEquals(Interlocked.Exchange(ref _reselectAfterCollapse, null), owner))
        {
            return;
        }

        SelectedNodes.Clear();
        SelectedNodes.Add(owner);
        SelectedNode = owner;
    }

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
                await Task.Run(() => monitor.DisposeAsync().AsTask());
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
        SetAttribute("StatusCode", StatusText.Of(update.Status));
        if (update.SourceTimestamp != DateTime.MinValue)
        {
            SetAttribute("SourceTimestamp", Timestamps.Format(update.SourceTimestamp));
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
                    // Replacing the row drops the grid selection; keep it so the context menu still acts on it.
                    var wasSelected = SelectedAttribute?.Name == name;
                    Attributes[i] = new AttributeValue(name, value);
                    if (wasSelected)
                    {
                        SelectedAttribute = Attributes[i];
                    }
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

    private CancellationTokenSource? _attributesCts;

    /// <summary>Selection delay before reading attributes, so arrowing through the tree doesn't queue server calls per node.</summary>
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(150);

    private async Task LoadAttributesAsync(NodeViewModel? node, bool debounce = false)
    {
        _attributesCts?.Cancel();
        var cts = _attributesCts = new CancellationTokenSource();
        var token = cts.Token;

        await StopSelectedValueMonitorAsync();
        if (token.IsCancellationRequested)
        {
            return;
        }

        Attributes.Clear();
        if (node is null || !IsConnected)
        {
            return;
        }

        try
        {
            if (debounce)
            {
                await Task.Delay(SelectionDebounce, token);
            }

            var client = _client;
            var refreshMs = DefaultRefreshMs;
            var attributes = await Task.Run(() => client.ReadAttributesAsync(node.NodeId, token), token);
            if (SelectedNode != node || token.IsCancellationRequested)
            {
                return;
            }

            foreach (var attribute in attributes)
            {
                Attributes.Add(attribute);
            }

            if (node.IsVariable)
            {
                var monitor = await Task.Run(() => client.MonitorAsync(node.NodeId, update => ApplySelectedValue(node, update), refreshMs), CancellationToken.None);
                if (SelectedNode == node && !token.IsCancellationRequested && _selectedValueMonitor is null)
                {
                    _selectedValueMonitor = monitor;
                    IsSelectedValueLive = true;
                }
                else
                {
                    _ = Task.Run(() => monitor.DisposeAsync().AsTask());
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // superseded by a newer selection
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }
        finally
        {
            if (ReferenceEquals(_attributesCts, cts))
            {
                _attributesCts = null;
            }

            cts.Dispose();
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

        // Values, status and staleness change: rows may enter or leave the filtered view.
        if (IsWatchFiltered)
        {
            RefreshWatchFilter(force: false);
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
            StatusMessage = string.Empty;
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
