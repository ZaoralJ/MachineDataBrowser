using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Opc.Ua;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("snapshots").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static WatchItemViewModel Item(uint id, string name, string value)
    {
        var item = new WatchItemViewModel(new NodeId(id, 2), name) { PortableId = $"nsu=urn:test;i={id}" };
        Set(item, value);
        return item;
    }

    private static void Set(WatchItemViewModel item, string value, StatusCode? status = null) =>
        item.Apply(new ValueUpdate(item.NodeId, value, status ?? StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow));

    [AvaloniaFact]
    public async Task Snapshots_are_saved_and_compared_with_live_values_and_each_other()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(new SettingsStore(Path.Combine(_dir, "settings.json"))) { Dialogs = dialogs };
        Assert.False(vm.TakeSnapshotCommand.CanExecute(null));

        var speed = Item(1, "Speed", "1500");
        var mode = Item(2, "Mode", "AUTO");
        var temp = Item(3, "Temperature", "20.5");
        vm.WatchItems.AddRange([speed, mode, temp]);
        Assert.True(vm.TakeSnapshotCommand.CanExecute(null));
        vm.TakeSnapshotCommand.Execute(null);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "snapshots"), "*.json"));

        // The machine changes: two values move, one item goes away, one is added.
        Set(speed, "1480");
        Set(mode, "MANUAL", StatusCodes.UncertainLastUsableValue);
        vm.WatchItems.Remove(temp);
        vm.WatchItems.Add(Item(4, "Pressure", "4.1"));

        vm.CompareSnapshotCommand.Execute(null);
        using var compare = Assert.IsType<SnapshotCompareViewModel>(dialogs.Compare);
        Assert.True(compare.IsLive);
        var rows = compare.Rows.ToDictionary(r => r.Name);
        Assert.Equal(4, rows.Count); // changed only: Speed, Mode, Temperature (gone), Pressure (new)
        Assert.Equal("-20", rows["Speed"].ChangeText);
        Assert.Equal(("AUTO", "MANUAL"), (rows["Mode"].Before, rows["Mode"].After));
        Assert.Equal(SnapshotChange.Removed, rows["Temperature"].Change);
        Assert.Equal(SnapshotChange.Added, rows["Pressure"].Change);
        Assert.Contains("2 changed, 0 same, 1 only after, 1 only before", compare.Summary, StringComparison.Ordinal);

        // Live: the comparison follows the watch values.
        Set(speed, "1500");
        compare.Compare();
        Assert.DoesNotContain(compare.Rows, r => r.Name == "Speed");
        compare.ChangedOnly = false;
        Assert.Contains(compare.Rows, r => r.Name == "Speed" && r.Change == SnapshotChange.Same);

        var window = new SnapshotCompareWindow { DataContext = compare };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "snapshot-compare.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        window.Close();

        // A second snapshot, compared with the first; then deleting the newest leaves one.
        await Task.Delay(20, TestContext.Current.CancellationToken);
        vm.TakeSnapshotCommand.Execute(null);
        vm.CompareSnapshotCommand.Execute(null);
        using var second = dialogs.Compare!;
        Assert.Equal(2, second.Snapshots.Count);
        second.Baseline = second.Snapshots[1];   // the older one
        second.Target = second.Snapshots[0];     // vs the newer one
        Assert.False(second.IsLive);
        Assert.Contains(second.Rows, r => r.Name == "Mode");
        second.Baseline = second.Snapshots[0];
        second.DeleteCommand.Execute(null);
        Assert.Single(second.Snapshots);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "snapshots"), "*.json"));
    }
}
