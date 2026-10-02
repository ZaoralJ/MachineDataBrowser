using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class ZoomTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zoom").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [AvaloniaFact]
    public async Task Zoom_steps_scale_the_window_content_and_persist()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        await using (var vm = new MainWindowViewModel(store))
        {
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var host = window.GetVisualDescendants().OfType<LayoutTransformControl>().Single(c => c.Name == "ZoomHost");

            var mod = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.Add, mod, PhysicalKey.NumPadAdd, "+");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.1, vm.UiScale, 3);
            Assert.Equal(1.1, ((ScaleTransform)host.LayoutTransform!).ScaleX, 3);

            vm.ZoomInCommand.Execute(null);
            vm.ZoomOutCommand.Execute(null);
            vm.ZoomOutCommand.Execute(null);
            Assert.Equal(1.0, vm.UiScale, 3);
            vm.ZoomOutCommand.Execute(null);
            Assert.Equal(0.9, vm.UiScale, 3);
            window.Close();
        }

        Assert.Equal(0.9, store.Load().UiScale, 3);
        await using var again = new MainWindowViewModel(store);
        again.ZoomResetCommand.Execute(null);
        Assert.Equal(1.0, again.UiScale, 3);
    }

    [AvaloniaFact]
    public async Task Dialogs_and_secondary_windows_follow_the_zoom()
    {
        await using var vm = new MainWindowViewModel(new SettingsStore(Path.Combine(_dir, "settings.json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.ZoomInCommand.Execute(null);
        vm.ZoomInCommand.Execute(null);
        Assert.Equal(1.25, vm.UiScale, 3);

        var text = new TextBlock { Text = "Dialog" };
        var dialog = new Window { Width = 400, MinHeight = 100, Content = text };
        dialog.Show(window);
        Dispatcher.UIThread.RunJobs();

        var host = Assert.IsType<LayoutTransformControl>(dialog.Content);
        Assert.Same(text, host.Child);
        Assert.Equal(1.25, ((ScaleTransform)host.LayoutTransform!).ScaleX, 3);
        Assert.Equal(500, dialog.Width, 3);
        Assert.Equal(125, dialog.MinHeight, 3);

        // Zooming while the dialog is open rescales it from its original size.
        vm.ZoomResetCommand.Execute(null);
        Assert.Null(host.LayoutTransform);
        Assert.Equal(400, dialog.Width, 3);

        dialog.Close();
        window.Close();
    }
}
