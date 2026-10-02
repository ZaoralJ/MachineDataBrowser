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

public sealed class FormLayoutTests
{
    [AvaloniaFact]
    public void Form_controls_share_one_height()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var main = new MainWindow { DataContext = new MainWindowViewModel(), Width = 1280, Height = 800 };
        main.Show();
        var form = new NewRecordingWindow { DataContext = new NewRecordingViewModel(new NewRecordingDraft("Recording 1", 2, 250)) };
        _ = form.ShowDialog<object?>(main);
        var settings = new SettingsWindow { DataContext = new SettingsViewModel(new Services.AppSettings()) };
        _ = settings.ShowDialog<object?>(main);
        Dispatcher.UIThread.RunJobs();

        foreach (var window in new Window[] { form, settings })
        {
            var heights = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c is NumericUpDown or ComboBox || (c is TextBox && c.FindAncestorOfType<NumericUpDown>() is null))
                .Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0)
                .Select(c => Math.Round(c.Bounds.Height))
                .Distinct()
                .ToList();
            Assert.True(heights.Count == 1, $"{window.GetType().Name}: heights {string.Join(", ", heights)}");

            window.CaptureRenderedFrame()?.Dispose();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", $"form-{window.GetType().Name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            window.Close();
        }

        main.Close();
    }
}
