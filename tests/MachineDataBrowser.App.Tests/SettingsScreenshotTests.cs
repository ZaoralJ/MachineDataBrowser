using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class SettingsScreenshotTests
{
    [AvaloniaFact]
    public void Picking_a_theme_applies_it_immediately()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var vm = new SettingsViewModel(new AppSettings { Theme = ThemePreference.Light });

        vm.Theme = ThemePreference.Dark;

        Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
        Application.Current.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaFact]
    public void Dialog_button_labels_are_vertically_centred()
    {
        var window = new SettingsWindow { DataContext = new SettingsViewModel(new AppSettings()) };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        foreach (var button in window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Where(b => b.Content is string))
        {
            var label = button.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First();
            var top = label.TranslatePoint(new Avalonia.Point(0, 0), button)!.Value.Y;
            var gap = (top - (button.Bounds.Height - top - label.Bounds.Height)) / 2;
            Assert.True(Math.Abs(gap) <= 1, $"'{button.Content}' label off-centre by {gap:0.#}px");
        }

        window.Close();
    }

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
