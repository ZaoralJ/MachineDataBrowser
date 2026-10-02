using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class ValueDisplayAppTests(CustomTypesServerFixture server) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("display").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static async Task WatchAsync(MainWindowViewModel vm, params string[] path)
    {
        var node = vm.RootNodes[0];
        foreach (var name in path)
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        vm.SelectedNode = node;
        await vm.AddToWatchCommand.ExecuteAsync(null);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task Units_come_from_the_server_and_formats_change_only_what_is_shown()
    {
        var dialogs = new TestDialogs();
        var session = Path.Combine(_dir, "display.mdbsession");
        await using (var vm = new MainWindowViewModel { EndpointUrl = server.EndpointUrl, Dialogs = dialogs })
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            await WatchAsync(vm, "Objects", "Custom", "History", "Temperature");
            await WatchAsync(vm, "Objects", "Custom", "DataTypes", "UInt16");
            var temperature = vm.WatchItems.Single(w => w.DisplayName == "Temperature");
            var word = vm.WatchItems.Single(w => w.DisplayName == "UInt16");

            await Until(() => temperature.ServerUnit == "°C" && temperature.Value != "…" && word.Value == "60000");
            Assert.EndsWith(" °C", temperature.DisplayValue, StringComparison.Ordinal);
            Assert.Null(word.ServerUnit);

            dialogs.DisplayAnswer = new ValueDisplay { Format = ValueFormat.Hex };
            vm.SelectedWatchItem = word;
            await vm.EditDisplayCommand.ExecuteAsync(null);
            Assert.Equal("0xEA60", word.DisplayValue);
            Assert.Equal("60000", word.Value);                 // the device's value is unchanged
            vm.WatchFilter = "60000";                          // and the filter still finds it
            Assert.Contains(word, vm.WatchView.Cast<WatchItemViewModel>());
            vm.WatchFilter = string.Empty;

            dialogs.DisplayAnswer = new ValueDisplay { Format = ValueFormat.Decimals, Decimals = 1, Unit = "K", Offset = 273.15 };
            vm.SelectedWatchItem = temperature;
            await vm.EditDisplayCommand.ExecuteAsync(null);
            Assert.Matches(@"^\d+\.\d K$", temperature.DisplayValue);

            var window = new ValueDisplayWindow { DataContext = new ValueDisplayViewModel(new ValueDisplay { Format = ValueFormat.Bits }, "UInt16", word) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();
            Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame())
            {
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
                frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "value-display.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
            await vm.WriteSessionAsync(session);
        }

        await using var reopened = new MainWindowViewModel { Dialogs = dialogs };
        await reopened.LoadSessionAsync(session);
        var restored = reopened.WatchItems.Single(w => w.DisplayName == "UInt16");
        Assert.Equal(ValueFormat.Hex, restored.Display.Format);
        await Until(() => restored.Value == "60000");
        Assert.Equal("0xEA60", restored.DisplayValue);
    }
}
