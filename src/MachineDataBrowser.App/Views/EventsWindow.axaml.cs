using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class EventsWindow : Window
{
    public EventsWindow()
    {
        InitializeComponent();

        // Closing the window ends the subscription.
        Closed += async (_, _) =>
        {
            if (DataContext is EventsViewModel vm)
            {
                await vm.DisposeAsync();
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not EventsViewModel vm)
        {
            return;
        }

        KeyBindings.Clear();
        Shortcuts.Apply("Events & Alarms", this,
        [
            new("A", vm.AcknowledgeCommand, Description: "Acknowledge the selected alarm"),
            new("Space", new RelayCommand(() => vm.IsPaused = !vm.IsPaused), Description: "Pause / resume the event list"),
            new("Cmd+K", vm.ClearCommand, Description: "Clear the event list"),
        ]);
    }
}
