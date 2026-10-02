using Avalonia;
using Avalonia.Controls;
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

/// <summary>Dialogs follow the colour theme, also when it changes while the app runs (or while they are open).</summary>
public sealed class DialogThemeTests : IDisposable
{
    public void Dispose()
    {
        MainWindowViewModel.ApplyColorTheme(ColorThemes.DefaultName);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    private static Color PixelAt(Window window, int x, int y)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame()!;
        var pixel = new byte[4];
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            frame.CopyPixels(new PixelRect(x, y, 1, 1), buffer, 4, 4);
            System.Runtime.InteropServices.Marshal.Copy(buffer, pixel, 0, 4);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }

        return frame.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? Color.FromRgb(pixel[2], pixel[1], pixel[0]) : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Open_and_new_dialogs_take_the_colour_theme(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        MainWindowViewModel.ApplyColorTheme("Indigo");
        var open = new SettingsWindow { DataContext = new SettingsViewModel(new AppSettings()) };
        open.Show();
        var indigo = PixelAt(open, 10, 10);

        MainWindowViewModel.ApplyColorTheme("Nord");
        var nord = ColorThemes.Find("Nord");
        var colors = variant == "Dark" ? nord.Dark : nord.Light;
        var expected = Color.Parse(colors.Region ?? colors.Window); // window backgrounds are the region colour
        Assert.NotEqual(indigo, expected);
        Assert.Equal(expected, PixelAt(open, 10, 10));       // already open (Settings previews live)

        var later = new SettingsWindow { DataContext = new SettingsViewModel(new AppSettings { ColorTheme = "Nord" }) };
        later.Show();
        Assert.Equal(expected, PixelAt(later, 10, 10));      // opened afterwards
        later.Close();
        open.Close();
    }
}
