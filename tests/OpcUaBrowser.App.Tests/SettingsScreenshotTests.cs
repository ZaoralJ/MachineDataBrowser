using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class SettingsScreenshotTests
{
    [AvaloniaFact]
    public void Renders_settings_dialog()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var window = new SettingsWindow { DataContext = new SettingsViewModel(new AppSettings()) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, "Settings-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
    }
}
