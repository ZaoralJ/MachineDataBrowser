using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

/// <summary>Writing a snapshot's values back to the device (a simple recipe), against the custom-types server.</summary>
public sealed class SnapshotRestoreTests(CustomTypesServerFixture server) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("snapshot-restore").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [AvaloniaFact]
    public async Task Snapshot_values_are_written_back_to_the_selected_rows_after_confirming()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(new SettingsStore(Path.Combine(_dir, "settings.json")))
        {
            EndpointUrl = server.EndpointUrl,
            Dialogs = dialogs,
        };
        await vm.ConnectCommand.ExecuteAsync(null);

        // UInt32 and Int64 are static and no other test uses them, so writing them doesn't disturb parallel tests.
        var folder = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "Custom", "DataTypes" })
        {
            await folder.EnsureChildrenLoadedAsync();
            folder = folder.Children.Single(c => c.DisplayName == name);
        }

        await folder.EnsureChildrenLoadedAsync();
        foreach (var name in new[] { "UInt32", "Int64" })
        {
            vm.SelectedNode = folder.Children.Single(c => c.DisplayName == name);
            await vm.AddToWatchCommand.ExecuteAsync(null);
        }

        var uint32 = vm.WatchItems.Single(w => w.DisplayName == "UInt32");
        var int64 = vm.WatchItems.Single(w => w.DisplayName == "Int64");
        await WaitUntil(() => uint32.Status.StartsWith("Good", StringComparison.Ordinal) && int64.Status.StartsWith("Good", StringComparison.Ordinal));
        var (uint32Before, int64Before) = (uint32.Value, int64.Value);

        try
        {
            vm.TakeSnapshotCommand.Execute(null);
            await Write(vm, dialogs, uint32, "123");
            await Write(vm, dialogs, int64, "5");
            await WaitUntil(() => uint32.Value == "123" && int64.Value == "5");

            vm.CompareSnapshotCommand.Execute(null);
            using var compare = dialogs.Compare!;
            compare.Compare();
            Assert.All(compare.Rows, r => Assert.True(r.CanRestore));

            // Only the selected row is written, and only after confirming.
            compare.SelectedIds.Add(uint32.PortableId);
            Assert.True(compare.RestoreCommand.CanExecute(null));
            compare.RestoreCommand.Execute(null);
            Assert.True(compare.IsConfirming);
            Assert.Contains($"UInt32: 123 → {uint32Before}", compare.PendingWrite, StringComparison.Ordinal);
            Assert.DoesNotContain("Int64", compare.PendingWrite, StringComparison.Ordinal);
            Assert.False(compare.RestoreCommand.CanExecute(null));

            compare.CancelRestoreCommand.Execute(null);
            Assert.False(compare.IsConfirming);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("123", uint32.Value);

            compare.RestoreCommand.Execute(null);
            await compare.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.StartsWith("Wrote 1 value(s)", compare.WriteResult, StringComparison.Ordinal);
            await WaitUntil(() => uint32.Value == uint32Before);
            Assert.Equal("5", int64.Value);

            // Read-only sessions can't write; without a selection every changed row is written.
            vm.IsReadOnly = true;
            compare.Compare();
            Assert.False(compare.RestoreCommand.CanExecute(null));
            vm.IsReadOnly = false;
            compare.SelectedIds.Clear();
            compare.Compare();
            compare.RestoreCommand.Execute(null);
            await compare.ConfirmRestoreCommand.ExecuteAsync(null);
            await WaitUntil(() => int64.Value == int64Before);
        }
        finally
        {
            vm.IsReadOnly = false;
            await Write(vm, dialogs, uint32, uint32Before);
            await Write(vm, dialogs, int64, int64Before);
        }
    }

    [AvaloniaFact]
    public async Task A_folder_snapshot_is_compared_and_written_back_without_watch_rows()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel(new SettingsStore(Path.Combine(_dir, "settings.json")))
        {
            EndpointUrl = server.EndpointUrl,
            Dialogs = dialogs,
        };
        await vm.ConnectCommand.ExecuteAsync(null);
        var folder = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "Custom", "DataTypes" })
        {
            await folder.EnsureChildrenLoadedAsync();
            folder = folder.Children.Single(c => c.DisplayName == name);
        }

        vm.SelectedNode = folder;
        Assert.True(vm.TakeFolderSnapshotCommand.CanExecute(null));

        // Cancelling the name saves nothing; the suggestion names the folder.
        await vm.TakeFolderSnapshotCommand.ExecuteAsync(null);
        Assert.StartsWith("DataTypes ", dialogs.SnapshotNameAsked!.Value.Suggested, StringComparison.Ordinal);
        dialogs.SnapshotNameAnswer = "DataTypes defaults";
        await vm.TakeFolderSnapshotCommand.ExecuteAsync(null);
        Assert.Empty(vm.WatchItems);

        vm.CompareSnapshotCommand.Execute(null);
        using var compare = dialogs.Compare!;
        var defaults = compare.Baseline!;
        Assert.True(defaults.Partial);
        var uint32 = defaults.Items.Single(i => i.Name == "UInt32");
        Assert.Equal("Good", uint32.Status);
        Assert.Contains(defaults.Items, i => i.Name.StartsWith("Arrays/", StringComparison.Ordinal));

        // Nothing is watched: the live side is read from the device, so UInt32 (no other test writes it) is the same.
        await compare.ReadMissingAsync();
        compare.ChangedOnly = false;
        Assert.Equal(SnapshotChange.Same, compare.Rows.Single(r => r.Name == "UInt32").Change);

        // A parameter set edited in a spreadsheet: only UInt32, with a new value.
        var csv = Path.Combine(_dir, "edited.csv");
        await File.WriteAllTextAsync(csv, $"Name,NodeId,Value\nUInt32,\"{uint32.NodeId}\",123\n", TestContext.Current.CancellationToken);
        dialogs.SnapshotImportAnswer = csv;
        await compare.ImportCommand.ExecuteAsync(null);
        Assert.Equal("edited", compare.Baseline!.Name);
        try
        {
            await compare.ReadMissingAsync();
            var row = Assert.Single(compare.Rows);
            Assert.Equal((uint32.Value, "123"), (row.After, row.Before));

            compare.RestoreCommand.Execute(null);
            await compare.ConfirmRestoreCommand.ExecuteAsync(null);
            Assert.StartsWith("Wrote 1 value(s)", compare.WriteResult, StringComparison.Ordinal);
            await compare.ReadMissingAsync();
            Assert.Equal(SnapshotChange.Same, compare.Rows.Single().Change);
        }
        finally
        {
            // Write the folder snapshot back: only UInt32 differs from it.
            compare.Baseline = compare.Snapshots.Single(s => s.Name == "DataTypes defaults");
            await compare.ReadMissingAsync();
            compare.SelectedIds.Add(uint32.NodeId);
            compare.RestoreCommand.Execute(null);
            await compare.ConfirmRestoreCommand.ExecuteAsync(null);
            await compare.ReadMissingAsync();
            Assert.Equal(SnapshotChange.Same, compare.Rows.Single(r => r.Name == "UInt32").Change);
        }
    }

    private static async Task Write(MainWindowViewModel vm, TestDialogs dialogs, WatchItemViewModel item, string value)
    {
        vm.SelectedWatchItem = item;
        dialogs.WriteAnswer = value;
        await vm.WriteWatchValueCommand.ExecuteAsync(null);
        dialogs.WriteAnswer = null;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
