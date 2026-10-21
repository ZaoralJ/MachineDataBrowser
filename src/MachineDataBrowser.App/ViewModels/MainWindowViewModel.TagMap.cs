using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool IsModbusEndpoint => DeviceClient.IsModbus(EndpointUrl ?? string.Empty);

    /// <summary>Modbus: register map (CSV) naming the device's addresses; empty for none. Saved with the session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary), nameof(HasTagMap), nameof(TagMapName))]
    [NotifyCanExecuteChangedFor(nameof(RemoveTagMapCommand))]
    public partial string TagMapPath { get; set; } = string.Empty;

    public bool HasTagMap => !string.IsNullOrWhiteSpace(TagMapPath);

    public string TagMapName => HasTagMap ? Path.GetFileName(TagMapPath) : "None";

    partial void OnTagMapPathChanged(string value) => MarkDirty();

    /// <summary>The map passed to the client: only for Modbus endpoints.</summary>
    private string? TagMapForConnect => IsModbusEndpoint && HasTagMap ? TagMapPath.Trim() : null;

    /// <summary>Picks a register map; a connected session reconnects so the tree shows its names.</summary>
    [RelayCommand(CanExecute = nameof(IsModbusEndpoint))]
    private async Task ImportTagMapAsync()
    {
        if (Dialogs is null || await Dialogs.PickTagMapFileAsync() is not { } path)
        {
            return;
        }

        await ApplyTagMapAsync(path);
    }

    [RelayCommand(CanExecute = nameof(HasTagMap))]
    private Task RemoveTagMapAsync() => ApplyTagMapAsync(string.Empty);

    private async Task ApplyTagMapAsync(string path)
    {
        TagMapPath = path;
        if (IsConnected)
        {
            await DisconnectAsync();
            await ConnectAsync();
        }

        if (ErrorMessage is null)
        {
            StatusMessage = HasTagMap ? $"Register map {TagMapName}" : "Register map removed";
        }
    }
}
