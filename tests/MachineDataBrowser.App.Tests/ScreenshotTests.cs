using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class ScreenshotTests(MachineDataBrowser.Core.Tests.OpcPlcFixture plc)
{
    private static readonly string OutputDir = Path.Combine(AppContext.BaseDirectory, "screenshots");

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Renders_main_window_with_watch_items(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl };
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();

        Directory.CreateDirectory(OutputDir);
        Save(window, $"{theme}-disconnected.png");

        await vm.ConnectCommand.ExecuteAsync(null);
        var root = vm.RootNodes[0];
        var objects = await Find(root, "Objects");
        var plcNode = await Find(objects, "OpcPlc");
        var telemetry = await Find(plcNode, "Telemetry");
        var basic = await Find(telemetry, "Basic");
        foreach (var name in new[] { "AlternatingBoolean", "RandomSignedInt32", "StepUp" })
        {
            vm.SelectedNode = await Find(basic, name);
            await vm.AddToWatchCommand.ExecuteAsync(null);
        }

        await Until(() => vm.WatchItems.All(w => w.Value != "…") && vm.HasAttributes);
        Save(window, $"{theme}-connected.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Renders_connection_options_flyout()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Connection options");
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        var spinners = window.GetVisualDescendants().OfType<NumericUpDown>().Where(s => s.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(spinners);
        var presenter = spinners[0].GetVisualAncestors().OfType<Avalonia.Controls.FlyoutPresenter>().First();
        Directory.CreateDirectory(OutputDir);
        Save(window, "connection-options.png");
        foreach (var spinner in spinners)
        {
            var right = spinner.TranslatePoint(new Point(spinner.Bounds.Width, 0), presenter)!.Value.X;
            Assert.True(right <= presenter.Bounds.Width - presenter.Padding.Right + 0.5, $"spinner right edge {right} exceeds presenter content {presenter.Bounds.Width - presenter.Padding.Right}");
        }
        foreach (var text in presenter.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
        {
            Assert.True(text.DesiredSize.Width <= text.Bounds.Width + 0.5, $"'{text.Text}' is clipped");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Pane_toolbar_text_buttons_are_not_clipped()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        vm.ShowPaneCommand.Execute("Recordings");
        Dispatcher.UIThread.RunJobs();

        var labels = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("icon-only"))
            .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)).ToList();
        Assert.Contains(labels, t => t.Text == "JSON");
        foreach (var text in labels)
        {
            Assert.True(text.DesiredSize.Width <= text.Bounds.Width + 0.5, $"'{text.Text}' is clipped");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Renders_endpoint_history_flyout_without_overflow()
    {
        var store = new MachineDataBrowser.App.Services.SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"));
        store.Save(new MachineDataBrowser.App.Services.AppSettings { RecentEndpoints = ["opc.tcp://localhost:62541", "eip://183944-010.plant.example.com:44818", "opc.tcp://268007-201.plant.example.com:4840"] });
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var vm = new MainWindowViewModel(store);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "EndpointHistoryButton");
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        Directory.CreateDirectory(OutputDir);
        Save(window, "endpoint-history.png");
        var presenter = window.GetVisualDescendants().OfType<FlyoutPresenter>().Single();
        var scroll = presenter.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"content {scroll.Extent.Width} wider than {scroll.Viewport.Width}");
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaTheory]
    [InlineData("Watch")]
    [InlineData("Recordings")]
    public void Column_headers_are_readable_at_minimum_width(string pane)
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        vm.ShowPaneCommand.Execute(pane);
        Dispatcher.UIThread.RunJobs();
        foreach (var column in window.GetVisualDescendants().OfType<DataGrid>().SelectMany(g => g.Columns))
        {
            column.Width = new DataGridLength(column.MinWidth);
        }

        Dispatcher.UIThread.RunJobs();
        var headers = window.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.Content is string { Length: > 0 } && h.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(headers);
        foreach (var header in headers)
        {
            // The template squeezes the label itself, so compare it with the label's natural width.
            var text = header.GetVisualDescendants().OfType<TextBlock>().First();
            var probe = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontWeight = text.FontWeight, FontFamily = text.FontFamily };
            probe.Measure(Size.Infinity);
            Assert.True(text.Bounds.Width >= probe.DesiredSize.Width - 0.5, $"{pane}: header '{header.Content}' clipped: {text.Bounds.Width:0} of {probe.DesiredSize.Width:0}px");
        }

        window.Close();
    }

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(OutputDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static async Task<NodeViewModel> Find(NodeViewModel parent, string name)
    {
        parent.IsExpanded = true;
        NodeViewModel? found = null;
        await Until(
            () => (found = parent.Children.FirstOrDefault(c => c.DisplayName == name)) is not null,
            () => $"'{name}' not under '{parent.DisplayName}'. Children: {string.Join(", ", parent.Children.Select(c => c.DisplayName))}");
        return found!;
    }

    private static async Task Until(Func<bool> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, describe?.Invoke() ?? "Timed out waiting for condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
