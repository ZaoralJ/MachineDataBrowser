using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class NewRecordingWindow : Window
{
    public NewRecordingWindow() => InitializeComponent();

    private void OnStart(object? sender, RoutedEventArgs e) =>
        Close((DataContext as NewRecordingViewModel)?.ToOptions(DateTimeOffset.UtcNow));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NewRecordingViewModel vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Record to file",
            SuggestedFileName = vm.Name,
            DefaultExtension = "csv",
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            vm.LiveFilePath = path;
        }
    }
}
