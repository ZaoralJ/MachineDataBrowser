using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

/// <summary>
/// Reloads the open session file when someone else changes it (another window or instance, <c>mdbrowser session</c>,
/// the TUI, the MCP server, an editor), or asks first when reloading would lose unsaved changes or recordings.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private SessionFileWatcher? _sessionWatcher;

    /// <summary>Delay after the last file event before reacting; saves arrive as several events (write, rename, …).</summary>
    internal static TimeSpan SessionChangeDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    [ObservableProperty]
    public partial bool IsSessionChangedOnDisk { get; private set; }

    partial void OnCurrentSessionPathChanged(string? value)
    {
        StopWatchingSessionFile();
        IsSessionChangedOnDisk = false;
        if (value is not null && File.Exists(value))
        {
            _sessionWatcher = new SessionFileWatcher(value, SessionChangeDelay);
            _sessionWatcher.Changed += () => Dispatcher.UIThread.Post(() => _ = OnSessionFileChangedAsync());
        }
    }

    private void StopWatchingSessionFile()
    {
        _sessionWatcher?.Dispose();
        _sessionWatcher = null;
    }

    /// <summary>The file holds what this window loaded or saved; it is not reported as changed.</summary>
    private void AcceptSessionFile() => _sessionWatcher?.Accept();

    private async Task OnSessionFileChangedAsync()
    {
        if (CurrentSessionPath is null)
        {
            return;
        }

        if (IsDirty || Recordings.Count > 0)
        {
            IsSessionChangedOnDisk = true;
            return;
        }

        await ReloadSessionFromDiskAsync();
    }

    [RelayCommand]
    private async Task ReloadSessionFromDiskAsync()
    {
        if (CurrentSessionPath is not { } path)
        {
            return;
        }

        IsSessionChangedOnDisk = false;
        await LoadSessionAsync(path);
        if (CurrentSessionPath == path && !HasError)
        {
            StatusMessage = $"Reloaded {DocumentName}: it was changed outside this window";
        }
    }

    /// <summary>Keeps this window as it is; it counts as unsaved, so saving overwrites the other change.</summary>
    [RelayCommand]
    private void KeepSessionChanges()
    {
        IsSessionChangedOnDisk = false;
        MarkDirty();
    }
}
