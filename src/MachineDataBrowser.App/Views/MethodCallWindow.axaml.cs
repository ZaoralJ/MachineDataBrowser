using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MachineDataBrowser.App.Views;

public sealed partial class MethodCallWindow : Window
{
    public MethodCallWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
