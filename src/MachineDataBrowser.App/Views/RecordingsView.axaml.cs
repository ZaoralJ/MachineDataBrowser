using Avalonia.Controls;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class RecordingsView : UserControl
{
    private bool _shortcutsApplied;

    public RecordingsView()
    {
        InitializeComponent();

        // Right-click acts on the clicked recording, so the context menu's commands target that row.
        RecordingsGrid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(RecordingsGrid).Properties.IsRightButtonPressed
                && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<DataGridRow>(e.Source as Avalonia.Visual, includeSelf: true) is { DataContext: RecordingViewModel row })
            {
                RecordingsGrid.SelectedItem = row;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm || _shortcutsApplied)
        {
            return;
        }

        _shortcutsApplied = true;
        Shortcuts.Apply("Recordings", RecordingsGrid,
        [
            new("N", vm.NewRecordingCommand, Description: "New recording from Watch selection…"),
            new("E", vm.EditRecordingSettingsCommand, Description: "Settings…"),
            new("A", vm.RecordAllCommand, Description: "Record all monitored…"),
            new("Enter", vm.ViewRecordingCommand, Description: "View live"),
            new("O", vm.OpenRecordingFileCommand, Description: "Open recording file…"),
            new("S", vm.StartRecordingCommand, Description: "Start / resume"),
            new("P", vm.PauseRecordingCommand, Description: "Pause"),
            new("X", vm.StopRecordingCommand, Description: "Stop"),
            new("Shift+Back", vm.ResetRecordingCommand, Description: "Reset history"),
            new("C", vm.ExportRecordingCsvCommand, Description: "Export CSV…"),
            new("J", vm.ExportRecordingJsonCommand, Description: "Export JSON…"),
            new("Delete", vm.CloseRecordingCommand, Description: "Close recording"),
        ]);
    }

    private void OnRecordingDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.ViewRecordingCommand.CanExecute(null))
        {
            vm.ViewRecordingCommand.Execute(null);
        }
    }
}
