using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Opc.Ua;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core;
using Xunit;

namespace MachineDataBrowser.App.Tests;

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

    [AvaloniaFact]
    public async Task A_snapshot_of_selected_rows_leaves_the_other_rows_out_of_the_comparison()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(new SettingsStore(Path.Combine(_dir, "settings.json"))) { Dialogs = dialogs };
        var speed = Item(1, "Speed", "1500");
        var mode = Item(2, "Mode", "AUTO");
        vm.WatchItems.AddRange([speed, mode, Item(3, "Temperature", "20.5")]);
        Assert.False(vm.TakeSelectionSnapshotCommand.CanExecute(null));

        vm.SelectedWatchItems.Add(speed);
        vm.SelectedWatchItems.Add(mode);
        Assert.True(vm.TakeSelectionSnapshotCommand.CanExecute(null));
        vm.TakeSelectionSnapshotCommand.Execute(null);

        Set(speed, "1400");
        vm.CompareSnapshotCommand.Execute(null);
        using var compare = dialogs.Compare!;
        var snapshot = Assert.Single(compare.Snapshots);
        Assert.True(snapshot.Partial);
        Assert.Equal(["Mode", "Speed"], snapshot.Items.Select(i => i.Name).Order());
        Assert.EndsWith("2 item(s), selection", snapshot.ToString(), StringComparison.Ordinal);

        // Temperature is in the watch list but not in the snapshot: it is not "only after".
        compare.ChangedOnly = false;
        Assert.Equal(["Mode", "Speed"], compare.Rows.Select(r => r.Name).Order());
        Assert.Contains("1 changed, 1 same", compare.Summary, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Snapshots_are_exported_and_imported_as_json_files()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(new SettingsStore(Path.Combine(_dir, "settings.json"))) { Dialogs = dialogs };
        vm.WatchItems.AddRange([Item(1, "Speed", "1500"), Item(2, "Mode", "AUTO")]);
        vm.TakeSnapshotCommand.Execute(null);
        vm.CompareSnapshotCommand.Execute(null);
        using var compare = dialogs.Compare!;
        var original = compare.Baseline!;

        var exported = Path.Combine(_dir, "line1-recipe.json");
        dialogs.SnapshotExportAnswer = exported;
        await compare.ExportCommand.ExecuteAsync(null);
        Assert.True(File.Exists(exported));
        Assert.StartsWith("Exported", compare.WriteResult, StringComparison.Ordinal);

        // Importing it elsewhere (a fresh store) brings back the same items and selects it.
        compare.DeleteCommand.Execute(null);
        Assert.Empty(compare.Snapshots);
        dialogs.SnapshotImportAnswer = exported;
        await compare.ImportCommand.ExecuteAsync(null);
        var imported = Assert.Single(compare.Snapshots);
        Assert.Same(imported, compare.Baseline);
        Assert.Equal(original.Name, imported.Name);
        Assert.Equal(original.Items, imported.Items);

        // Importing the same file again replaces it instead of adding a copy.
        await compare.ImportCommand.ExecuteAsync(null);
        Assert.Single(compare.Snapshots);

        // Anything else is refused with a message.
        var notSnapshot = Path.Combine(_dir, "settings-copy.json");
        await File.WriteAllTextAsync(notSnapshot, """{"theme":"dark"}""", TestContext.Current.CancellationToken);
        dialogs.SnapshotImportAnswer = notSnapshot;
        await compare.ImportCommand.ExecuteAsync(null);
        Assert.StartsWith("Import failed", compare.WriteResult, StringComparison.Ordinal);
        Assert.Single(compare.Snapshots);
    }

    [AvaloniaFact]
    public async Task Snapshots_are_renamed_in_place_and_keep_the_comparison()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(new SettingsStore(Path.Combine(_dir, "settings.json"))) { Dialogs = dialogs };
        vm.WatchItems.AddRange([Item(1, "Speed", "1500")]);
        vm.TakeSnapshotCommand.Execute(null);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        vm.TakeSnapshotCommand.Execute(null);
        vm.CompareSnapshotCommand.Execute(null);
        using var compare = dialogs.Compare!;
        compare.Baseline = compare.Snapshots[1];
        compare.Target = compare.Snapshots[0];
        var file = compare.Baseline.Path!;

        Assert.False(compare.IsRenaming);
        compare.StartRenameCommand.Execute(null);
        Assert.Equal(compare.Baseline.Name, compare.NewName);
        compare.NewName = "  ";
        Assert.False(compare.ApplyRenameCommand.CanExecute(null));
        compare.NewName = "  Line 1 – product A ";
        compare.ApplyRenameCommand.Execute(null);

        Assert.False(compare.IsRenaming);
        Assert.Equal("Line 1 – product A", compare.Baseline!.Name);
        Assert.Equal(file, compare.Baseline.Path);
        Assert.Same(compare.Snapshots[0], compare.Target);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dir, "snapshots"), "*.json").Length);
        Assert.Contains(new SnapshotStore(Path.Combine(_dir, "snapshots")).List(), s => s.Name == "Line 1 – product A");

        // In the window: Enter renames, Esc cancels.
        var window = new SnapshotCompareWindow { DataContext = compare };
        window.Show();
        compare.StartRenameCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        compare.NewName = "Product B";
        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Product B", compare.Baseline!.Name);

        compare.StartRenameCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        compare.NewName = "discarded";
        window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(compare.IsRenaming);
        Assert.Equal("Product B", compare.Baseline!.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Take_snapshot_as_asks_for_a_name_first()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(new SettingsStore(Path.Combine(_dir, "settings.json"))) { Dialogs = dialogs };
        Assert.False(vm.TakeNamedSnapshotCommand.CanExecute(null));
        vm.WatchItems.AddRange([Item(1, "Speed", "1500"), Item(2, "Mode", "AUTO")]);
        Assert.True(vm.TakeNamedSnapshotCommand.CanExecute(null));

        // Cancelled: nothing saved; the suggestion is the usual date-time name.
        await vm.TakeNamedSnapshotCommand.ExecuteAsync(null);
        Assert.StartsWith("Snapshot ", dialogs.SnapshotNameAsked!.Value.Suggested, StringComparison.Ordinal);
        Assert.Equal(2, dialogs.SnapshotNameAsked.Value.Items);
        Assert.False(Directory.Exists(Path.Combine(_dir, "snapshots")));

        dialogs.SnapshotNameAnswer = " Line 1 – product A ";
        await vm.TakeNamedSnapshotCommand.ExecuteAsync(null);
        var snapshot = Assert.Single(new SnapshotStore(Path.Combine(_dir, "snapshots")).List());
        Assert.Equal("Line 1 – product A", snapshot.Name);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.False(snapshot.Partial);
    }
}
