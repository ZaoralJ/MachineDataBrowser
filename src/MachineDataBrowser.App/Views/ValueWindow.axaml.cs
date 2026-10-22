using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace MachineDataBrowser.App.Views;

public sealed partial class ValueWindow : Window
{
    public ValueWindow()
    {
        InitializeComponent();
    }

    public ValueWindow(string title, string value) : this()
    {
        ValueTitle.Text = title;
        ValueText.Text = value;
    }

    private async void OnCopy(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(ValueText.Text ?? string.Empty);
        }
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
