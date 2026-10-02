using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.Views;

public sealed partial class MonitoringSettingsWindow : Window
{
    public MonitoringSettingsWindow() => InitializeComponent();

    private void OnApply(object? sender, RoutedEventArgs e) => Close((DataContext as MonitoringSettingsViewModel)?.ToOptions());

    private void OnDefaults(object? sender, RoutedEventArgs e) => Close(MonitoringOptions.Default);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
