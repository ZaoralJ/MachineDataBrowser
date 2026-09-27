using Avalonia.Controls;

namespace OpcUaBrowser.App.Views;

public sealed partial class RecordingsView : UserControl
{
    public RecordingsView() => InitializeComponent();

    private void OnRecordingDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm && vm.ViewRecordingCommand.CanExecute(null))
        {
            vm.ViewRecordingCommand.Execute(null);
        }
    }
}
