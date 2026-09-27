using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class ScreenshotTests(OpcUaBrowser.Core.Tests.OpcPlcFixture plc)
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
