using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<RecordingViewModel> Recordings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(PauseRecordingCommand), nameof(StopRecordingCommand),
        nameof(ResetRecordingCommand), nameof(CloseRecordingCommand), nameof(ViewRecordingCommand), nameof(ExportRecordingCsvCommand), nameof(ExportRecordingJsonCommand))]
    public partial RecordingViewModel? SelectedRecording { get; set; }

    private bool CanCreateRecording() => IsConnected && WatchItems.Any(w => w.Monitor is not null);

    [RelayCommand(CanExecute = nameof(CanCreateRecording))]
    private Task NewRecordingAsync() => NewRecordingFromAsync(RecordingSource());

    [RelayCommand(CanExecute = nameof(CanCreateRecording))]
    private Task RecordAllAsync() => NewRecordingFromAsync([.. WatchItems.Where(w => w.Monitor is not null)]);

    private async Task NewRecordingFromAsync(List<WatchItemViewModel> source)
    {
        if (source.Count == 0)
        {
            return;
        }

        var draft = new NewRecordingDraft($"Recording {Recordings.Count + 1}", source.Count, DefaultRefreshMs);
        if (Dialogs is null || await Dialogs.EditNewRecordingAsync(draft) is not { } options)
        {
            return;
        }

        await CreateRecordingAsync(options, [.. source.Select(w => new RecordedItem(w.NodeId, w.DisplayName, w.PortableId))]);
    }

    /// <summary>Selected watch rows (one or many); all watched items only when nothing is selected.</summary>
    public List<WatchItemViewModel> RecordingSource() =>
        [.. (SelectedWatchItems.Count > 0 ? SelectedWatchItems : WatchItems).Where(w => w.Monitor is not null)];

    public async Task<RecordingViewModel?> CreateRecordingAsync(RecordingOptions options, IReadOnlyList<RecordedItem> items)
    {
        var vm = new RecordingViewModel(new Recording(_client, options, items));
        Recordings.Add(vm);
        SelectedRecording = vm;
        try
        {
            await vm.Recording.StartAsync();
            StatusMessage = vm.Recording.State == RecordingState.Scheduled ? $"'{options.Name}' scheduled" : $"Recording '{options.Name}'";
        }
        catch (Exception ex) when (ex is ServiceResultException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ReportError(ex);
        }

        vm.Refresh();
        NotifyRecordingCommands();
        return vm;
    }

    /// <summary>Marks watch items that open recordings contain (icon in the Name column).</summary>
    private void UpdateRecordingFlags()
    {
        foreach (var item in WatchItems)
        {
            item.SetRecordings([.. Recordings
                .Where(r => r.Recording.Items.Any(i => i.NodeId == item.NodeId))
                .Select(r => (r.Name, r.Recording.State))]);
        }
    }

    /// <summary>The newest open recording that contains the item.</summary>
    private RecordingViewModel? RecordingFor(WatchItemViewModel item) =>
        Recordings.LastOrDefault(r => r.Recording.Items.Any(i => i.NodeId == item.NodeId));

    /// <summary>Opens the recorded values of one watch item, with a trend chart for numeric values.</summary>
    [RelayCommand]
    private void ViewItemRecording(WatchItemViewModel? item)
    {
        item ??= SelectedWatchItem;
        if (item is null || RecordingFor(item) is not { } recording)
        {
            StatusMessage = "No recording contains this item";
            return;
        }

        var recorded = recording.Recording.Items.First(i => i.NodeId == item.NodeId);
        Dialogs?.ShowRecordingViewer(RecordingViewerViewModel.ForRecording(recording.Recording, recorded.DisplayName));
    }

    private bool HasRecording() => SelectedRecording is not null;

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private void ViewRecording()
    {
        if (SelectedRecording is { } vm)
        {
            Dialogs?.ShowRecordingViewer(RecordingViewerViewModel.ForRecording(vm.Recording));
        }
    }

    [RelayCommand]
    private async Task OpenRecordingFileAsync()
    {
        if (Dialogs is not null && await Dialogs.PickRecordingFileAsync() is { } path)
        {
            Dialogs.ShowRecordingViewer(RecordingViewerViewModel.ForFile(path));
            StatusMessage = $"Opened recording {Path.GetFileName(path)} (live — updates while the file grows)";
        }
    }

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private Task StartRecordingAsync() => RunRecording(async r =>
    {
        if (r.State == RecordingState.Paused)
        {
            r.Resume();
        }
        else if (r.State is RecordingState.Created or RecordingState.Stopped)
        {
            await r.StartAsync();
        }
    });

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private Task PauseRecordingAsync() => RunRecording(r =>
    {
        if (r.State == RecordingState.Recording)
        {
            r.Pause();
        }

        return Task.CompletedTask;
    });

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private Task StopRecordingAsync() => RunRecording(r => r.StopAsync());

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private Task ResetRecordingAsync() => RunRecording(r =>
    {
        r.Reset();
        return Task.CompletedTask;
    });

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private async Task CloseRecordingAsync()
    {
        if (SelectedRecording is not { } vm)
        {
            return;
        }

        Recordings.Remove(vm);
        SelectedRecording = Recordings.LastOrDefault();
        await vm.DisposeAsync();
        NotifyRecordingCommands();
    }

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private Task ExportRecordingCsvAsync() => ExportRecordingAsync("csv", (r, p) => r.ExportCsvAsync(p));

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private Task ExportRecordingJsonAsync() => ExportRecordingAsync("json", (r, p) => r.ExportJsonAsync(p));

    private async Task ExportRecordingAsync(string extension, Func<Recording, string, Task> export)
    {
        if (SelectedRecording is not { } vm || Dialogs is null || await Dialogs.PickExportTargetAsync(vm.Name, extension) is not { } path)
        {
            return;
        }

        try
        {
            await export(vm.Recording, path);
            StatusMessage = $"Exported '{vm.Name}' to {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            ErrorMessage = $"Export failed: {ex.Message}";
        }
    }

    private async Task RunRecording(Func<Recording, Task> action)
    {
        if (SelectedRecording is not { } vm)
        {
            return;
        }

        try
        {
            await action(vm.Recording);
        }
        catch (Exception ex) when (ex is ServiceResultException or InvalidOperationException or IOException)
        {
            ReportError(ex);
        }

        vm.Refresh();
    }

    private void NotifyRecordingCommands()
    {
        NewRecordingCommand.NotifyCanExecuteChanged();
        RecordAllCommand.NotifyCanExecuteChanged();
    }

    private async Task StopAllRecordingsAsync()
    {
        foreach (var vm in Recordings.ToList())
        {
            await vm.Recording.StopAsync();
            vm.Refresh();
        }
    }
}

public sealed record NewRecordingDraft(string Name, int ItemCount, int RefreshMs);
