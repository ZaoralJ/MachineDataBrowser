using Avalonia.Controls;
using Avalonia.Interactivity;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e) =>
        Close((DataContext as SettingsViewModel)?.ToSettings());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
