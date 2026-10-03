using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

/// <summary>
/// Screenshots for README and the user manual, in the dark theme, written to <c>docs/images</c>.
/// Skipped unless <c>MDB_DOCS_SCREENSHOTS=1</c> (<c>just docs-screenshots</c>): live values differ on every run.
/// </summary>
public sealed class DocsScreenshotTests(OpcPlcFixture plc, CustomTypesServerFixture custom) : IDisposable
{
    private static readonly string OutputDir = Path.Combine(RepoRoot(), "docs", "images");

    public void Dispose()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        MainWindowViewModel.ApplyColorTheme(ColorThemes.DefaultName);
    }

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MDB_DOCS_SCREENSHOTS") == "1", "Set MDB_DOCS_SCREENSHOTS=1 to update docs/images.");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        MainWindowViewModel.ApplyColorTheme(ColorThemes.DefaultName);
        Directory.CreateDirectory(OutputDir);
    }

    [AvaloniaFact]
    public async Task Main_window_and_watch()
    {
        RequireEnabled();
        await using var vm = new MainWindowViewModel(DarkSettings()) { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);

        var basic = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic");
        await vm.DropNodesAsync([basic]);
        foreach (var name in new[] { "CurrentTime", "State" })
        {
            await vm.MonitorNodeCommand.ExecuteAsync(await Navigate(vm, "Objects", "Server", "ServerStatus", name));
        }

        (await Navigate(vm, "Objects", "Server")).IsExpanded = false;

        vm.SelectedNode = await Navigate(vm, "Objects", "OpcPlc", "Telemetry", "Basic", "StepUp");
        await Until(() => vm.HasAttributes && vm.WatchItems.All(w => w.Status.Length > 0));
        Save(window, "main-window.png");

        vm.GroupWatchByPath = true;
        await Until(() => vm.WatchView.Groups?.Count > 1);
        Save(window, "watch.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Recording_viewer_with_history_chart()
    {
        RequireEnabled();
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(DarkSettings()) { EndpointUrl = custom.EndpointUrl, Dialogs = dialogs };
        await vm.ConnectCommand.ExecuteAsync(null);
        foreach (var name in new[] { "Temperature", "Pressure" })
        {
            var node = await Navigate(vm, "Objects", "Custom", "History", name);
            vm.SelectedNodes.Add(node);
            vm.SelectedNode = node;
        }

        await vm.ShowHistoryCommand.ExecuteAsync(60);
        var viewer = Assert.IsType<RecordingViewerViewModel>(dialogs.Viewer);
        var window = new RecordingViewerWindow { DataContext = viewer, Width = 1100, Height = 680 };
        window.Show();
        Save(window, "recording-viewer.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Events_and_alarms()
    {
        RequireEnabled();
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(DarkSettings()) { EndpointUrl = plc.EndpointUrl, Dialogs = dialogs };
        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.ShowEventsCommand.ExecuteAsync(null);
        var events = Assert.Single(dialogs.EventWindows);
        await Until(() =>
        {
            events.Flush();
            return events.Events.Count >= 10 && events.Alarms.Count > 0;
        }, seconds: 30);

        var window = new EventsWindow { DataContext = events, Width = 1100, Height = 620 };
        window.Show();
        Save(window, "events.png");
        window.Close();
    }

    private static SettingsStore DarkSettings()
    {
        var store = new SettingsStore(Path.Combine(Directory.CreateTempSubdirectory("docs-screenshots").FullName, "settings.json"));
        store.Save(new AppSettings { Theme = ThemePreference.Dark });
        return store;
    }

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(OutputDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static async Task<NodeViewModel> Navigate(MainWindowViewModel vm, params string[] path)
    {
        var node = vm.RootNodes[0];
        foreach (var name in path)
        {
            node.IsExpanded = true;
            var parent = node;
            NodeViewModel? next = null;
            await Until(() => (next = parent.Children.FirstOrDefault(c => c.DisplayName == name)) is not null);
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
