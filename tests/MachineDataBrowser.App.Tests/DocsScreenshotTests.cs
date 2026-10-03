using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

/// <summary>
/// Screenshots for README and the user manual, in the dark theme, written to <c>docs/images</c>. Each scene drives the
/// real app against the test servers and opens dialogs through the app's own commands.
/// Skipped unless <c>MDB_DOCS_SCREENSHOTS=1</c> (<c>just docs-screenshots</c>): live values differ on every run.
/// </summary>
public sealed class DocsScreenshotTests(OpcPlcFixture plc, CustomTypesServerFixture custom, MqttSimulatorFixture broker) : IDisposable
{
    private static readonly string OutputDir = Path.Combine(RepoRoot(), "docs", "images");

    private readonly string _dir = Directory.CreateTempSubdirectory("docs-screenshots").FullName;

    private readonly System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;

    public void Dispose()
    {
        System.Globalization.CultureInfo.CurrentCulture = _culture;
        System.Globalization.CultureInfo.CurrentUICulture = _culture;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        MainWindowViewModel.ApplyColorTheme(ColorThemes.DefaultName);
        Directory.Delete(_dir, recursive: true);
    }

    // ---- Window, Watch ----

    [AvaloniaFact]
    public async Task Main_window_and_watch()
    {
        var (vm, window) = await OpenAsync(plc.EndpointUrl);
        await using var connection = vm;
        await vm.DropNodesAsync([await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic")]);
        await WatchAsync(vm, "Objects", "Server", "ServerStatus", "CurrentTime");
        await WatchAsync(vm, "Objects", "Server", "ServerStatus", "State");
        (await Navigate(vm, "Objects", "Server")).IsExpanded = false;

        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp");
        await Until(() => vm.HasAttributes && vm.WatchItems.All(w => w.Status.Length > 0));
        Save(window, "main-window.png");

        vm.GroupWatchByPath = true;
        await Until(() => vm.WatchView.Groups?.Count > 1);
        Save(window, "watch.png");
        window.Close();
    }

    // ---- Connecting ----

    [AvaloniaFact]
    public async Task Connection_options_certificate_prompt_and_diagnostics()
    {
        var (vm, window) = await OpenAsync(plc.EndpointUrl, connect: false);
        await using var connection = vm;
        var options = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Connection options");
        options.Flyout!.ShowAt(options);
        Save(window, "connection-options.png");
        options.Flyout.Hide();

        var now = DateTime.UtcNow;
        var certificate = new ServerCertificate(
            "CN=OpcPlc, O=Microsoft, DC=plc-01", "CN=OpcPlc, O=Microsoft, DC=plc-01",
            "3F2A9C41D07E5B8812C4AF06E39B2D7745C1A0FE", now.AddDays(-30), now.AddYears(1),
            "The certificate is self-signed and not in the trusted store.", []);
        _ = window.AskTrustCertificateAsync(certificate, "opc.tcp://plc-01:4840");
        SaveDialog(window, "certificate-prompt.png");

        await vm.ConnectCommand.ExecuteAsync(null);
        await WatchAsync(vm, "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp");
        await WatchAsync(vm, "Objects", "OpcPlc", "Telemetry", "Basic", "RandomSignedInt32");
        vm.ShowDiagnosticsCommand.Execute(null);
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        SaveDialog(window, "diagnostics.png", close: true);
        window.Close();
    }

    // ---- Address Space ----

    [AvaloniaFact]
    public async Task Search_and_bookmarks()
    {
        var (vm, window) = await OpenAsync(plc.EndpointUrl);
        await using var connection = vm;
        foreach (var path in new[] { new[] { "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp" }, ["Objects", "OpcPlc", "Telemetry", "Slow"], ["Objects", "Boilers"] })
        {
            vm.SelectedNode = await Navigate(vm, path);
            vm.ToggleBookmarkCommand.Execute(null);
        }

        var bookmarks = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Bookmarks");
        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp");
        bookmarks.Flyout!.ShowAt(bookmarks);
        Save(window, "bookmarks.png");
        bookmarks.Flyout.Hide();

        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc");
        vm.SearchText = "Random*";
        await vm.SearchCommand.ExecuteAsync(null);
        await Until(() => vm.SearchResults.Count > 0 && !vm.IsSearching, seconds: 30);
        Save(window, "search.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Mqtt_topics_and_sparkplug()
    {
        var (vm, window) = await OpenAsync(broker.EndpointUrl);
        await using var connection = vm;
        var root = vm.RootNodes[0];
        await WatchAsync(vm, "Topics", "machines", "m1", "status", "speed");
        await WatchAsync(vm, "Sparkplug B", "Plant1", "Edge1", "Press1", "Temperature");
        vm.SelectedNode = await Navigate(vm, "Topics", "machines", "m1", "status");
        await Until(() => vm.HasAttributes && vm.WatchItems.All(w => w.UpdateCount > 1));
        Save(window, "mqtt.png");
        window.Close();
    }

    // ---- Watch dialogs, snapshots, writing ----

    [AvaloniaFact]
    public async Task Display_format_monitoring_settings_and_write_value()
    {
        var (vm, window) = await OpenAsync(custom.EndpointUrl);
        await using var connection = vm;
        await WatchAsync(vm, "Objects", "Custom", "DataTypes", "UInt16");
        await WatchAsync(vm, "Objects", "Custom", "History", "Temperature");
        var word = vm.WatchItems.Single(w => w.DisplayName == "UInt16");
        var temperature = vm.WatchItems.Single(w => w.DisplayName == "Temperature");
        await Until(() => word.Value != "…" && temperature.ServerUnit is not null);

        Select(vm, word);
        _ = vm.EditDisplayCommand.ExecuteAsync(null);
        var display = await DialogAsync(window);
        ((ValueDisplayViewModel)display.DataContext!).Format = ValueFormat.Bits;
        SaveDialog(window, "display-format.png", close: true);

        Select(vm, temperature);
        _ = vm.EditMonitoringCommand.ExecuteAsync(null);
        var monitoring = await DialogAsync(window);
        var settings = (MonitoringSettingsViewModel)monitoring.DataContext!;
        settings.QueueSize = 10;
        settings.Deadband = DeadbandKind.Absolute;
        settings.DeadbandValue = 0.5m;
        SaveDialog(window, "monitoring-settings.png", close: true);

        _ = vm.WriteWatchValueCommand.ExecuteAsync(null);
        await DialogAsync(window);
        SaveDialog(window, "write-value.png", close: true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Snapshot_compare()
    {
        var (vm, window) = await OpenAsync(plc.EndpointUrl);
        await using var connection = vm;
        await vm.DropNodesAsync([await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic")]);
        await Until(() => vm.WatchItems.All(w => w.Value != "…"));
        vm.TakeSnapshotCommand.Execute(null);
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        vm.CompareSnapshotCommand.Execute(null);
        var compare = (SnapshotCompareViewModel)(await DialogAsync(window)).DataContext!;
        compare.ChangedOnly = false;
        SaveDialog(window, "snapshot-compare.png", close: true);
        window.Close();
    }

    // ---- Methods, history, events ----

    [AvaloniaFact]
    public async Task Method_call_and_history()
    {
        var (vm, window) = await OpenAsync(custom.EndpointUrl);
        await using var connection = vm;
        await vm.CallMethodCommand.ExecuteAsync(await Navigate(vm, "Objects", "Custom", "Methods", "Stats"));
        var call = (MethodCallViewModel)(await DialogAsync(window)).DataContext!;
        call.Inputs[0].Text = "[3, 8, 1, 12.5]";
        await call.CallCommand.ExecuteAsync(null);
        SaveDialog(window, "method-call.png", close: true);

        foreach (var name in new[] { "Temperature", "Pressure" })
        {
            var node = await Navigate(vm, "Objects", "Custom", "History", name);
            vm.SelectedNodes.Add(node);
            vm.SelectedNode = node;
        }

        await vm.ShowHistoryCommand.ExecuteAsync(60);
        var viewer = await DialogAsync(window);
        viewer.Width = 1100;
        viewer.Height = 680;
        SaveDialog(window, "history.png", close: true);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Events_and_alarms()
    {
        var (vm, window) = await OpenAsync(plc.EndpointUrl);
        await using var connection = vm;
        await vm.ShowEventsCommand.ExecuteAsync(null);
        var events = await DialogAsync(window);
        var model = (EventsViewModel)events.DataContext!;
        await Until(() =>
        {
            model.Flush();
            return model.Events.Count >= 10 && model.Alarms.Count > 0;
        }, seconds: 30);
        events.Width = 1100;
        events.Height = 620;
        SaveDialog(window, "events.png", close: true);
        window.Close();
    }

    // ---- Recordings ----

    [AvaloniaFact]
    public async Task Recordings_pane_new_recording_form_and_viewer()
    {
        var (vm, window) = await OpenAsync(plc.EndpointUrl);
        await using var connection = vm;
        await vm.DropNodesAsync([await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic")]);
        var step = vm.WatchItems.Single(w => w.DisplayName == "StepUp");
        var random = vm.WatchItems.Single(w => w.DisplayName == "RandomSignedInt32");
        await Until(() => step.Monitor is not null && random.Monitor is not null);

        var trend = await vm.CreateRecordingAsync(
            new RecordingOptions { Name = "Line 1 trend", SamplingIntervalMs = 100 },
            [new RecordedItem(step.NodeId, step.DisplayName, step.PortableId), new RecordedItem(random.NodeId, random.DisplayName, random.PortableId)]);
        await vm.CreateRecordingAsync(
            new RecordingOptions { Name = "Shift log", SamplingIntervalMs = 1000, MaxPointsPerItem = 10_000 },
            [.. vm.WatchItems.Select(w => new RecordedItem(w.NodeId, w.DisplayName, w.PortableId))]);
        await Task.Delay(4000, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        vm.ShowPaneCommand.Execute("Recordings");
        vm.SelectedRecording = trend;
        Save(window, "recordings.png");

        Select(vm, step);
        _ = vm.NewRecordingCommand.ExecuteAsync(null);
        await DialogAsync(window);
        SaveDialog(window, "new-recording.png", close: true);

        vm.SelectedRecording = trend;
        vm.ViewRecordingCommand.Execute(null);
        var viewer = await DialogAsync(window);
        viewer.Width = 1100;
        viewer.Height = 680;
        SaveDialog(window, "recording-viewer.png", close: true);
        window.Close();
    }

    // ---- Tabs, settings, keyboard ----

    [AvaloniaFact]
    public async Task Connection_tabs_settings_and_shortcuts()
    {
        var (first, window) = await OpenAsync(plc.EndpointUrl);
        await using var connection = first;
        await first.DropNodesAsync([await Navigate(first, "Objects", "OpcPlc", "Telemetry", "Basic")]);

        window.NewConnectionTab();
        var second = (MainWindowViewModel)window.DataContext!;
        second.EndpointUrl = custom.EndpointUrl;
        await second.ConnectCommand.ExecuteAsync(null);
        foreach (var name in new[] { "Temperature", "Pressure", "Running" })
        {
            await WatchAsync(second, "Objects", "Custom", "History", name);
        }

        (await Navigate(second, "Objects", "Custom")).IsExpanded = false;
        await Until(() => second.WatchItems.All(w => w.Value != "…"));
        Save(window, "connection-tabs.png");

        new SettingsWindow { DataContext = new SettingsViewModel(first.Settings) }.Show(window);
        await DialogAsync(window);
        SaveDialog(window, "settings.png", close: true);

        window.ShowShortcuts();
        var shortcuts = await DialogAsync(window);
        shortcuts.Height = 760;
        SaveDialog(window, "keyboard-shortcuts.png", close: true);
        window.Close();
    }

    // ---- helpers ----

    private async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenAsync(string endpoint, bool connect = true)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MDB_DOCS_SCREENSHOTS") == "1", "Set MDB_DOCS_SCREENSHOTS=1 to update docs/images.");
        Directory.CreateDirectory(OutputDir);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

        // The view model applies the saved appearance when it is created, so the dark theme comes from its settings.
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { Theme = ThemePreference.Dark, ColorTheme = ColorThemes.DefaultName });
        var vm = new MainWindowViewModel(store, new LayoutStore(Path.Combine(_dir, "layout.json"))) { EndpointUrl = endpoint };
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        if (connect)
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected);
        }

        return (vm, window);
    }

    private static async Task WatchAsync(MainWindowViewModel vm, params string[] path) =>
        await vm.MonitorNodeCommand.ExecuteAsync(await Navigate(vm, path));

    private static void Select(MainWindowViewModel vm, WatchItemViewModel item)
    {
        vm.SelectedWatchItems.Clear();
        vm.SelectedWatchItems.Add(item);
        vm.SelectedWatchItem = item;
    }

    private static async Task<Window> DialogAsync(Window owner)
    {
        Window? dialog = null;
        await Until(() => (dialog = owner.OwnedWindows.Count > 0 ? owner.OwnedWindows[^1] : null) is not null);
        Dispatcher.UIThread.RunJobs();
        return dialog!;
    }

    private static void SaveDialog(Window owner, string name, bool close = true)
    {
        var dialog = owner.OwnedWindows[^1];
        Save(dialog, name);
        if (close)
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Save(Window window, string name)
    {
        // A new connection tab applies its own (default) appearance; the docs are dark throughout.
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        foreach (var number in window.GetVisualDescendants().OfType<NumericUpDown>())
        {
            number.NumberFormat = System.Globalization.CultureInfo.GetCultureInfo("en-US").NumberFormat;
        }
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.True(MeanLuminance(frame!) < 90, $"{name} is not in dark mode.");
        frame!.Save(Path.Combine(OutputDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static double MeanLuminance(Avalonia.Media.Imaging.Bitmap frame)
    {
        var size = frame.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixels.Length);
        try
        {
            frame.CopyPixels(new PixelRect(size), buffer, pixels.Length, stride);
            System.Runtime.InteropServices.Marshal.Copy(buffer, pixels, 0, pixels.Length);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }

        double sum = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            sum += (0.114 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.299 * pixels[i + 2]);
        }

        return sum / (pixels.Length / 4);
    }

    private static async Task<NodeViewModel> Navigate(MainWindowViewModel vm, params string[] path)
    {
        var node = vm.RootNodes[0];
        foreach (var name in path)
        {
            node.IsExpanded = true;
            var parent = node;
            NodeViewModel? next = null;
            await Until(() => (next = parent.Children.FirstOrDefault(c => c.DisplayName == name)) is not null, seconds: 45);
            node = next!;
        }

        return node;
    }

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MachineDataBrowser.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root (MachineDataBrowser.slnx) not found.");
    }
}
