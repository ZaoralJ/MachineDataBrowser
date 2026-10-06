using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class SessionFileWatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("machinedatabrowser-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Session(string endpoint) => $"{{\"endpointUrl\":\"{endpoint}\",\"watch\":[],\"bookmarks\":[]}}";

    [AvaloniaFact]
    public async Task A_session_changed_elsewhere_reloads_when_there_is_nothing_to_lose()
    {
        var path = Path.Combine(_dir, "line.mdbsession");
        await File.WriteAllTextAsync(path, Session("opc.tcp://first:4840"), TestContext.Current.CancellationToken);
        await using var vm = new MainWindowViewModel();
        await vm.LoadSessionAsync(path);

        await File.WriteAllTextAsync(path, Session("opc.tcp://second:4840"), TestContext.Current.CancellationToken);

        await WaitFor(() => vm.EndpointUrl == "opc.tcp://second:4840");
        Assert.False(vm.IsDirty);
        Assert.False(vm.IsSessionChangedOnDisk);
        Assert.Contains("Reloaded", vm.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Unsaved_changes_are_kept_until_the_user_chooses()
    {
        var path = Path.Combine(_dir, "line.mdbsession");
        await File.WriteAllTextAsync(path, Session("opc.tcp://first:4840"), TestContext.Current.CancellationToken);
        await using var vm = new MainWindowViewModel();
        await vm.LoadSessionAsync(path);
        vm.EndpointUrl = "opc.tcp://mine:4840";
        Assert.True(vm.IsDirty);

        await File.WriteAllTextAsync(path, Session("opc.tcp://theirs:4840"), TestContext.Current.CancellationToken);

        await WaitFor(() => vm.IsSessionChangedOnDisk);
        Assert.Equal("opc.tcp://mine:4840", vm.EndpointUrl);

        vm.KeepSessionChangesCommand.Execute(null);
        Assert.False(vm.IsSessionChangedOnDisk);
        Assert.True(vm.IsDirty);

        await File.WriteAllTextAsync(path, Session("opc.tcp://again:4840"), TestContext.Current.CancellationToken);
        await WaitFor(() => vm.IsSessionChangedOnDisk);
        await vm.ReloadSessionFromDiskCommand.ExecuteAsync(null);
        Assert.Equal("opc.tcp://again:4840", vm.EndpointUrl);
        Assert.False(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task Own_saves_do_not_trigger_a_reload()
    {
        var path = Path.Combine(_dir, "line.mdbsession");
        await File.WriteAllTextAsync(path, Session("opc.tcp://first:4840"), TestContext.Current.CancellationToken);
        await using var vm = new MainWindowViewModel();
        await vm.LoadSessionAsync(path);
        vm.EndpointUrl = "opc.tcp://mine:4840";
        await vm.WriteSessionAsync(path);
        vm.EndpointUrl = "opc.tcp://unsaved:4840";

        await Pump(TimeSpan.FromSeconds(1));

        Assert.False(vm.IsSessionChangedOnDisk);
        Assert.Equal("opc.tcp://unsaved:4840", vm.EndpointUrl);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Pump(TimeSpan.FromMilliseconds(50));
        }

        Assert.True(condition());
    }

    private static async Task Pump(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
