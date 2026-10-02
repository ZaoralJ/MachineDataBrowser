using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class ColorThemeTests(OpcUaBrowser.Core.Tests.OpcPlcFixture plc) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("themes").FullName;

    public void Dispose()
    {
        // Other tests expect the default look.
        MainWindowViewModel.ApplyColorTheme(ColorThemes.DefaultName);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        Directory.Delete(_dir, true);
    }

    private static Color Accent(ThemeVariant variant) =>
        Application.Current!.TryGetResource("AppAccentBrush", variant, out var brush) ? ((SolidColorBrush)brush!).Color : default;

    [AvaloniaFact]
    public async Task Every_colour_theme_recolours_light_and_dark_and_is_saved()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        await using var vm = new MainWindowViewModel(store) { EndpointUrl = plc.EndpointUrl };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.ConnectCommand.ExecuteAsync(null);
        var node = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "OpcPlc", "Telemetry", "Basic" })
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        await node.EnsureChildrenLoadedAsync();
        foreach (var name in new[] { "StepUp", "AlternatingBoolean" })
        {
            vm.SelectedNode = node.Children.Single(c => c.DisplayName == name);
            await vm.AddToWatchCommand.ExecuteAsync(null);
        }

        var output = Path.Combine(AppContext.BaseDirectory, "screenshots", "themes");
        Directory.CreateDirectory(output);
        foreach (var theme in ColorThemes.All)
        {
            vm.SetColorThemeCommand.Execute(theme.Name);
            Assert.Equal(theme.Name, vm.ColorTheme);
            Assert.Equal(Color.Parse(theme.Light.Accent), Accent(ThemeVariant.Light));
            Assert.Equal(Color.Parse(theme.Dark.Accent), Accent(ThemeVariant.Dark));

            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Dispose();
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, $"{theme.Name}-{variant}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }

        vm.SetColorThemeCommand.Execute("Nord");
        Assert.Equal("Nord", store.Load().ColorTheme);
        vm.SetColorThemeCommand.Execute("does not exist");
        Assert.Equal(ColorThemes.DefaultName, vm.ColorTheme);
        window.Close();
    }
}
