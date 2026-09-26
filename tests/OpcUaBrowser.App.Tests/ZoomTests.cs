using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using Xunit;

namespace OpcUaBrowser.App.Tests;

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
}
