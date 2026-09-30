using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core.Tests;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class SessionAndSettingsTests(OpcPlcFixture plc) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opcuabrowser-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [AvaloniaFact]
    public async Task Session_round_trip_restores_endpoint_and_watch_list_with_namespace_uris()
    {
        var path = Path.Combine(_dir, "line1.opcsession");

        await using (var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl })
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            var basic = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
            vm.SelectedNode = basic;
            await vm.MonitorFolderCommand.ExecuteAsync(null);
            Assert.Equal(4, vm.WatchItems.Count);
            Assert.True(vm.IsDirty);

            await vm.WriteSessionAsync(path);
            Assert.False(vm.IsDirty);
            Assert.Equal("line1 — OPC UA Browser", vm.WindowTitle);
        }

        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("nsu=", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        await using var reopened = new MainWindowViewModel();
        await reopened.LoadSessionAsync(path);

        Assert.True(reopened.IsConnected);
        Assert.Equal(plc.EndpointUrl, reopened.EndpointUrl);
        Assert.Equal(
            ["AlternatingBoolean", "RandomSignedInt32", "RandomUnsignedInt32", "StepUp"],
            reopened.WatchItems.Select(w => w.DisplayName).Order());
        Assert.False(reopened.IsDirty);
        await WaitUntil(() => reopened.WatchItems.All(w => w.Status == "Good"));
    }

    [AvaloniaFact]
    public async Task Opening_a_session_in_the_window_does_not_mark_it_dirty()
    {
        var path = Path.Combine(_dir, "clean.opcsession");
        await new SessionDocument
        {
            EndpointUrl = plc.EndpointUrl,
            AutoAcceptCertificates = true,
            Watch = [new WatchEntry("i=2258", "CurrentTime")],
        }.SaveAsync(path, TestContext.Current.CancellationToken);

        await using var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        await vm.LoadSessionAsync(path);
        await WaitUntil(() => vm.WatchItems.Count == 1 && vm.WatchItems[0].Status == "Good");
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.False(vm.IsDirty, "Freshly opened session must not be dirty.");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Monitor_folder_includes_variables_from_subfolders_but_not_variable_properties()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        await vm.ConnectCommand.ExecuteAsync(null);
        var telemetry = await Navigate(vm, "Objects", "OpcPlc", "Telemetry");
        var basic = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        basic.IsExpanded = true;
        await WaitUntil(() => basic.Children.Count == 4 && basic.Children.All(c => c.IsVariable));

        vm.SelectedNode = telemetry;
        await vm.MonitorFolderCommand.ExecuteAsync(null);

        var names = vm.WatchItems.Select(w => w.DisplayName).ToHashSet();
        Assert.True(names.Count > 4, $"Expected variables from several subfolders, got {names.Count}.");
        Assert.Subset(names, basic.Children.Select(c => c.DisplayName).ToHashSet());
        Assert.DoesNotContain("EURange", names);
        Assert.DoesNotContain("EngineeringUnits", names);
    }

    [AvaloniaFact]
    public async Task Refresh_time_per_item_and_default_round_trip_through_session()
    {
        var path = Path.Combine(_dir, "refresh.opcsession");
        await using (var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl, DefaultRefreshMs = 500 })
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
            await vm.MonitorFolderCommand.ExecuteAsync(null);
            Assert.All(vm.WatchItems, w => Assert.Equal(500, w.RefreshMs));

            var step = vm.WatchItems.Single(w => w.DisplayName == "StepUp");
            vm.SelectedWatchItems.Add(step);
            await vm.SetRefreshCommand.ExecuteAsync(2000);
            Assert.Equal(2000, step.RefreshMs);
            Assert.NotNull(step.Monitor);
            var before = step.Value;
            await WaitUntil(() => step.Value != before);

            await vm.WriteSessionAsync(path);
        }

        await using var reopened = new MainWindowViewModel();
        await reopened.LoadSessionAsync(path);
        Assert.Equal(500, reopened.DefaultRefreshMs);
        Assert.Equal(2000, reopened.WatchItems.Single(w => w.DisplayName == "StepUp").RefreshMs);
        Assert.All(reopened.WatchItems.Where(w => w.DisplayName != "StepUp"), w => Assert.Equal(500, w.RefreshMs));
    }

    [AvaloniaFact]
    public async Task Recording_captures_history_pause_stop_reset_export_close()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        await vm.MonitorFolderCommand.ExecuteAsync(null);
        Assert.True(vm.NewRecordingCommand.CanExecute(null));

        var items = vm.WatchItems.Select(w => new OpcUaBrowser.Core.RecordedItem(w.NodeId, w.DisplayName, w.PortableId)).ToList();
        var rec = await vm.CreateRecordingAsync(new OpcUaBrowser.Core.RecordingOptions { Name = "Before fix", SamplingIntervalMs = 100 }, items);
        Assert.NotNull(rec);
        await WaitUntil(() => rec!.Recording.TotalSamples > 5);

        var viewer = OpcUaBrowser.App.ViewModels.RecordingViewerViewModel.ForRecording(rec!.Recording);
        var seen = viewer.Rows.Count;
        Assert.True(seen > 0);
        await WaitUntil(() => { viewer.Poll(); return viewer.Rows.Count > seen; });
        Assert.Equal(viewer.Rows.Count, viewer.Rows.Distinct().Count());
        viewer.SelectedItem = "StepUp";
        Assert.All(viewer.Rows, r => Assert.Equal("StepUp", r.Name));
        viewer.Dispose();

        var live = Path.Combine(_dir, "live.csv");
        var fileRec = await vm.CreateRecordingAsync(new OpcUaBrowser.Core.RecordingOptions { Name = "file", SamplingIntervalMs = 100, LiveFilePath = live }, items);
        await WaitUntil(() => File.Exists(live) && new FileInfo(live).Length > 200);
        var fileViewer = OpcUaBrowser.App.ViewModels.RecordingViewerViewModel.ForFile(live);
        var n = fileViewer.Rows.Count;
        await WaitUntil(() => { fileViewer.Poll(); return fileViewer.Rows.Count > n; });
        fileViewer.Dispose();
        vm.SelectedRecording = fileRec;
        await vm.CloseRecordingCommand.ExecuteAsync(null);
        vm.SelectedRecording = rec;

        await vm.PauseRecordingCommand.ExecuteAsync(null);
        Assert.Equal(OpcUaBrowser.Core.RecordingState.Paused, rec!.State);
        await vm.StartRecordingCommand.ExecuteAsync(null);
        Assert.Equal(OpcUaBrowser.Core.RecordingState.Recording, rec.State);

        await vm.StopRecordingCommand.ExecuteAsync(null);
        var kept = rec.Recording.TotalSamples;
        Assert.True(kept > 0);
        Assert.Equal(4, vm.WatchItems.Count(w => w.Monitor is not null));

        var csv = Path.Combine(_dir, "h.csv");
        await rec.Recording.ExportCsvAsync(csv, TestContext.Current.CancellationToken);
        Assert.True((await File.ReadAllLinesAsync(csv, TestContext.Current.CancellationToken)).Length > 1);

        await vm.ResetRecordingCommand.ExecuteAsync(null);
        Assert.Equal(0, rec.Recording.TotalSamples);

        await vm.CloseRecordingCommand.ExecuteAsync(null);
        Assert.Empty(vm.Recordings);
        Assert.Equal(OpcUaBrowser.Core.RecordingState.Closed, rec.Recording.State);
    }

    [AvaloniaFact]
    public async Task Watch_columns_visibility_order_and_width_are_saved_with_session()
    {
        var path = Path.Combine(_dir, "columns.opcsession");
        await using (var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl })
        {
            // Wide enough for every column at its saved width; a narrow grid squeezes columns to their minimum.
            var window = new MainWindow { DataContext = vm, Width = 2000, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = window.GetVisualDescendants().OfType<Avalonia.Controls.DataGrid>().Single(g => g.Name == "WatchGrid");
            Assert.False(grid.Columns.Single(c => (string)c.Header! == "Source time").IsVisible);

            vm.WatchColumns["NodeId"]!.IsVisible = false;
            vm.WatchColumns["Source time"]!.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(grid.Columns.Single(c => (string)c.Header! == "NodeId").IsVisible);
            Assert.True(vm.IsDirty);

            grid.Columns.Single(c => (string)c.Header! == "Status").Width = new Avalonia.Controls.DataGridLength(222);
            Dispatcher.UIThread.RunJobs();
            await vm.WriteSessionAsync(path);
            window.Close();
        }

        await using var reopened = new MainWindowViewModel();
        var w2 = new MainWindow { DataContext = reopened, Width = 2000, Height = 900 };
        w2.Show();
        await reopened.LoadSessionAsync(path);
        Dispatcher.UIThread.RunJobs();
        var g2 = w2.GetVisualDescendants().OfType<Avalonia.Controls.DataGrid>().Single(g => g.Name == "WatchGrid");
        Assert.False(g2.Columns.Single(c => (string)c.Header! == "NodeId").IsVisible);
        Assert.True(g2.Columns.Single(c => (string)c.Header! == "Source time").IsVisible);
        Assert.Equal(222, g2.Columns.Single(c => (string)c.Header! == "Status").Width.Value, 1);
        Assert.False(reopened.IsDirty);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Dropping_variables_and_folders_onto_watch_monitors_them()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        await vm.ConnectCommand.ExecuteAsync(null);
        var basic = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        basic.IsExpanded = true;
        await WaitUntil(() => basic.Children.Count == 4 && basic.Children.All(c => c.IsVariable));

        await vm.DropNodesAsync([basic.Children[0], basic.Children[1]]);
        Assert.Equal(2, vm.WatchItems.Count);

        var telemetry = await Navigate(vm, "Objects", "OpcPlc", "Telemetry");
        await vm.DropNodesAsync([telemetry]);
        Assert.True(vm.WatchItems.Count > 4, "folder drop must add all variables below it");
        Assert.Contains("added to watch", vm.StatusMessage, StringComparison.Ordinal);

        var status = await Navigate(vm, "Objects", "Server", "ServerStatus");
        await vm.DropNodesAsync([status]);
        Assert.Contains(vm.WatchItems, w => w.DisplayName == "ServerStatus");
        Assert.Contains(vm.WatchItems, w => w.DisplayName == "CurrentTime");
        Assert.Contains(vm.WatchItems, w => w.DisplayName == "ProductName");
        Assert.Equal(vm.WatchItems.Count, vm.WatchItems.Select(w => w.NodeId).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task Copy_as_json_gives_object_for_one_and_array_for_many()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        string? copied = null;
        vm.CopyToClipboard = t => { copied = t; return Task.CompletedTask; };
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        await vm.MonitorFolderCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.WatchItems.All(w => w.Status == "Good"));

        vm.SelectedWatchItem = vm.WatchItems[0];
        await vm.CopyWatchJsonCommand.ExecuteAsync(null);
        using (var one = System.Text.Json.JsonDocument.Parse(copied!))
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Object, one.RootElement.ValueKind);
            Assert.StartsWith("nsu=", one.RootElement.GetProperty("nodeId").GetString(), StringComparison.Ordinal);
            Assert.Equal("Good", one.RootElement.GetProperty("status").GetString());
            Assert.NotEqual(System.Text.Json.JsonValueKind.String, one.RootElement.GetProperty("value").ValueKind);
        }

        vm.SelectedWatchItems.Add(vm.WatchItems[1]);
        vm.SelectedWatchItems.Add(vm.WatchItems[2]);
        await vm.CopyWatchJsonCommand.ExecuteAsync(null);
        using var many = System.Text.Json.JsonDocument.Parse(copied!);
        Assert.Equal(2, many.RootElement.GetArrayLength());

        vm.SelectedWatchItems.Clear();
        foreach (var w in vm.WatchItems) { vm.SelectedWatchItems.Add(w); }
        await vm.CopyWatchValuesJsonCommand.ExecuteAsync(null);
        using var values = System.Text.Json.JsonDocument.Parse(copied!);
        var flag = values.RootElement.GetProperty("AlternatingBoolean").ValueKind;
        Assert.True(flag is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, values.RootElement.GetProperty("StepUp").ValueKind);
    }

    [AvaloniaFact]
    public async Task Mouse_drag_selects_rows_and_copy_as_json_includes_all_of_them()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        string? copied = null;
        vm.CopyToClipboard = t => { copied = t; return Task.CompletedTask; };
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        await vm.MonitorFolderCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var grid = window.GetVisualDescendants().OfType<Avalonia.Controls.DataGrid>().Single(g => g.Name == "WatchGrid");
        await WaitUntil(() => grid.GetVisualDescendants().OfType<Avalonia.Controls.DataGridRow>().Count() >= 4);
        Point RowCenter(int i)
        {
            var row = grid.GetVisualDescendants().OfType<Avalonia.Controls.DataGridRow>().Single(r => r.DataContext == vm.WatchItems[i]);
            return row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window)!.Value;
        }

        window.MouseDown(RowCenter(0), Avalonia.Input.MouseButton.Left);
        window.MouseMove(RowCenter(1), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        window.MouseMove(RowCenter(2), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        window.MouseMove(RowCenter(3), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        window.MouseUp(RowCenter(3), Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, vm.SelectedWatchItems.Count);
        Assert.Equal(" (4 items)", vm.WatchSelectionLabel);

        window.MouseDown(RowCenter(2), Avalonia.Input.MouseButton.Right, Avalonia.Input.RawInputModifiers.RightMouseButton);
        window.MouseUp(RowCenter(2), Avalonia.Input.MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, vm.SelectedWatchItems.Count);

        await vm.CopyWatchJsonCommand.ExecuteAsync(null);
        using var doc = System.Text.Json.JsonDocument.Parse(copied!);
        Assert.Equal(4, doc.RootElement.GetArrayLength());

        grid.ContextMenu?.Close();
        Dispatcher.UIThread.RunJobs();
        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(vm.WatchItems[0]);
        grid.SelectedItems.Add(vm.WatchItems[1]);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(RowCenter(3), Avalonia.Input.MouseButton.Right, Avalonia.Input.RawInputModifiers.RightMouseButton);
        window.MouseUp(RowCenter(3), Avalonia.Input.MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(vm.WatchItems[3], Assert.Single(vm.SelectedWatchItems));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Recording_source_is_the_watch_selection_one_or_many_else_all()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        await vm.MonitorFolderCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.RecordingSource().Count);

        vm.SelectedWatchItems.Add(vm.WatchItems[2]);
        Assert.Same(vm.WatchItems[2], Assert.Single(vm.RecordingSource()));

        vm.SelectedWatchItems.Add(vm.WatchItems[0]);
        Assert.Equal(2, vm.RecordingSource().Count);
    }

    [AvaloniaFact]
    public async Task Reveal_in_tree_expands_path_and_selects_the_watched_node()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        await vm.MonitorFolderCommand.ExecuteAsync(null);
        vm.CollapseAllCommand.Execute(null);
        vm.SelectedNode = null;

        var step = vm.WatchItems.Single(w => w.DisplayName == "StepUp");
        await vm.RevealInTreeCommand.ExecuteAsync(step);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("StepUp", vm.SelectedNode?.DisplayName);
        Assert.Equal(step.NodeId, vm.SelectedNode!.NodeId);
        var tree = window.GetVisualDescendants().OfType<Avalonia.Controls.TreeView>().Single();
        Assert.Same(vm.SelectedNode, tree.SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Selected_variable_value_updates_live_at_default_refresh()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl, DefaultRefreshMs = 200 };
        await vm.ConnectCommand.ExecuteAsync(null);
        var basic = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        basic.IsExpanded = true;
        await WaitUntil(() => basic.Children.Any(c => c.DisplayName == "StepUp"));

        vm.SelectedNode = basic.Children.Single(c => c.DisplayName == "StepUp");
        await WaitUntil(() => vm.IsSelectedValueLive && vm.Attributes.Any(a => a.Name == "Value"));
        var first = vm.Attributes.Single(a => a.Name == "Value").Value;
        await WaitUntil(() => vm.Attributes.Single(a => a.Name == "Value").Value != first);
        Assert.Contains(vm.Attributes, a => a.Name == "StatusCode" && a.Value == "Good");

        vm.SelectedNode = basic;
        await WaitUntil(() => !vm.IsSelectedValueLive);
        Assert.Empty(vm.WatchItems);
    }

    [AvaloniaFact]
    public async Task Removing_multiple_watch_items_at_once()
    {
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        await vm.MonitorFolderCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.WatchItems.Count);

        vm.SelectedWatchItems.Add(vm.WatchItems[0]);
        vm.SelectedWatchItems.Add(vm.WatchItems[2]);
        vm.SelectedWatchItems.Add(vm.WatchItems[3]);
        var kept = vm.WatchItems[1];
        Assert.True(vm.RemoveFromWatchCommand.CanExecute(null));

        await vm.RemoveFromWatchCommand.ExecuteAsync(null);

        Assert.Same(kept, Assert.Single(vm.WatchItems));
        Assert.Empty(vm.SelectedWatchItems);
        Assert.Equal("Removed 3 items from watch", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Multi_selection_monitors_only_variables_and_recursive_respects_limit()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { MaxRecursiveItems = 7 });
        await using var vm = new MainWindowViewModel(store) { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);

        var basic = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        basic.IsExpanded = true;
        await WaitUntil(() => basic.Children.Count == 4 && basic.Children.All(c => c.NodeId is not null && !c.NodeId.IsNullNodeId));

        vm.SelectedNodes.Add(basic);
        vm.SelectedNodes.Add(basic.Children[0]);
        vm.SelectedNodes.Add(basic.Children[1]);
        await vm.AddToWatchCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.WatchItems.Count);

        vm.SelectedNodes.Clear();
        var telemetry = await Navigate(vm, "Objects", "OpcPlc", "Telemetry");
        vm.SelectedNode = telemetry;
        await vm.MonitorFolderCommand.ExecuteAsync(null);
        Assert.InRange(vm.WatchItems.Count, 7, 9);
        Assert.Contains("limited", vm.StatusMessage, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Endpoint_history_records_connections_and_supports_pick_remove_clear()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { RecentEndpoints = ["opc.tcp://old-plc:4840", "opc.tcp://line2:4840"] });

        await using var vm = new MainWindowViewModel(store) { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "EndpointHistoryButton").IsVisible);

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal([plc.EndpointUrl, "opc.tcp://old-plc:4840", "opc.tcp://line2:4840"], vm.RecentEndpoints);
        await vm.DisconnectCommand.ExecuteAsync(null);

        vm.UseRecentEndpointCommand.Execute("opc.tcp://line2:4840");
        Assert.Equal("opc.tcp://line2:4840", vm.EndpointUrl);

        vm.RemoveRecentEndpointCommand.Execute("opc.tcp://old-plc:4840");
        Assert.Equal([plc.EndpointUrl, "opc.tcp://line2:4840"], store.Load().RecentEndpoints);

        vm.ClearRecentEndpointsCommand.Execute(null);
        Assert.False(vm.HasRecentEndpoints);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "EndpointHistoryButton").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Opc_ua_only_options_are_hidden_for_eip_endpoints()
    {
        var vm = new MainWindowViewModel { EndpointUrl = "opc.tcp://plc:4840", UserName = "op" };
        Assert.True(vm.IsOpcUaEndpoint);
        Assert.Contains("op", vm.OptionsSummary, StringComparison.Ordinal);

        vm.EndpointUrl = "eip://192.168.1.10/1,0";
        Assert.False(vm.IsOpcUaEndpoint);
        Assert.StartsWith("EtherNet/IP", vm.OptionsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("op", vm.OptionsSummary.Replace("EtherNet/IP", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Last_used_endpoint_is_the_default()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { RecentEndpoints = ["opc.tcp://line2:4840", "opc.tcp://old-plc:4840"] });

        var vm = new MainWindowViewModel(store);

        Assert.Equal("opc.tcp://line2:4840", vm.EndpointUrl);
        Assert.False(vm.IsDirty);
    }

    [AvaloniaFact]
    public void Clicking_a_history_entry_fills_the_endpoint_and_closes_the_flyout()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { RecentEndpoints = ["opc.tcp://old-plc:4840", "opc.tcp://line2:4840"] });
        var vm = new MainWindowViewModel(store) { EndpointUrl = "opc.tcp://typed:4840" };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var historyButton = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "EndpointHistoryButton");
        var flyout = Assert.IsAssignableFrom<Avalonia.Controls.Primitives.FlyoutBase>(historyButton.Flyout);
        flyout.ShowAt(historyButton);
        Dispatcher.UIThread.RunJobs();

        var entry = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(((Avalonia.Controls.Flyout)flyout).Content as Avalonia.Visual ?? throw new InvalidOperationException())
            .OfType<Avalonia.Controls.Button>().First(b => b.Classes.Contains("history-entry") && Equals(b.CommandParameter, "opc.tcp://line2:4840"));
        typeof(Avalonia.Controls.Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(entry, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("opc.tcp://line2:4840", vm.EndpointUrl);
        Assert.False(flyout.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Theme_setting_is_applied_and_persisted()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        await using (var vm = new MainWindowViewModel(store))
        {
            vm.SetThemeCommand.Execute(ThemePreference.Dark);
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        }

        Assert.Equal(ThemePreference.Dark, store.Load().Theme);

        await using (var vm = new MainWindowViewModel(store))
        {
            Assert.Equal(ThemePreference.Dark, vm.Theme);
            vm.SetThemeCommand.Execute(ThemePreference.System);
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
        }
    }

    private static async Task<NodeViewModel> Navigate(MainWindowViewModel vm, params string[] path)
    {
        var node = vm.RootNodes[0];
        foreach (var name in path)
        {
            node.IsExpanded = true;
            NodeViewModel? next = null;
            var parent = node;
            await WaitUntil(() => (next = parent.Children.FirstOrDefault(c => c.DisplayName == name)) is not null);
            node = next!;
        }

        return node;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
