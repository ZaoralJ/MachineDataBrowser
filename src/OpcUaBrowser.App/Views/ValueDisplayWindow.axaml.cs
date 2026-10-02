using Avalonia.Controls;
using Avalonia.Interactivity;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.Views;

public sealed partial class ValueDisplayWindow : Window
{
    public ValueDisplayWindow() => InitializeComponent();

    private void OnApply(object? sender, RoutedEventArgs e) => Close((DataContext as ValueDisplayViewModel)?.ToDisplay());

    private void OnDefault(object? sender, RoutedEventArgs e) => Close(ValueDisplay.Default);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
