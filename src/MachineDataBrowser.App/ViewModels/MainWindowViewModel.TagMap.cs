using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool IsModbusEndpoint => DeviceClient.IsModbus(EndpointUrl ?? string.Empty);

    /// <summary>Modbus and S7 take a tag map: the device has no names of its own.</summary>
    public bool SupportsTagMap => DeviceClient.UsesTagMap(EndpointUrl ?? string.Empty);

    /// <summary>"Register map" (Modbus) or "Tag list" (S7), for the connection options.</summary>
    public string TagMapLabel => Capitalize(DeviceClient.TagMapName(EndpointUrl ?? string.Empty));

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Modbus: register map (CSV) naming the device's addresses; S7: tag list (TIA Portal / STEP 7 symbol table). Empty
    /// for none. Saved with the session.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary), nameof(HasTagMap), nameof(TagMapName))]
    [NotifyCanExecuteChangedFor(nameof(RemoveTagMapCommand))]
    public partial string TagMapPath { get; set; } = string.Empty;

    public bool HasTagMap => !string.IsNullOrWhiteSpace(TagMapPath);

    public string TagMapName => HasTagMap ? Path.GetFileName(TagMapPath) : "None";

    partial void OnTagMapPathChanged(string value) => MarkDirty();

    /// <summary>The map passed to the client: only for Modbus and S7 endpoints.</summary>
    private string? TagMapForConnect => SupportsTagMap && HasTagMap ? TagMapPath.Trim() : null;

    /// <summary>Picks a register map or tag list; a connected session reconnects so the tree shows its names.</summary>
    [RelayCommand(CanExecute = nameof(SupportsTagMap))]
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
            StatusMessage = HasTagMap ? $"{TagMapLabel} {TagMapName}" : $"{TagMapLabel} removed";
        }
    }
}
