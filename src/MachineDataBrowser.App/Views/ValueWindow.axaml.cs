using Avalonia.Controls;
using Avalonia.Input.Platform;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.Views;

public sealed partial class ValueWindow : Window
{
    private string _originalValue = string.Empty;
    private bool _isFormatted;

    public ValueWindow()
    {
        InitializeComponent();
    }

    public ValueWindow(string title, string value) : this()
    {
        ValueTitle.Text = title;
        _originalValue = value;
        ValueText.Text = _originalValue;
        FormatJsonButton.IsVisible = JsonValueFormatting.TryFormat(value, out _);
    }

    private void OnFormatJson(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isFormatted)
        {
            ValueText.Text = _originalValue;
            FormatJsonButton.Content = "Format JSON";
            _isFormatted = false;
        }
        else if (JsonValueFormatting.TryFormat(_originalValue, out var formatted))
        {
            ValueText.Text = formatted;
            FormatJsonButton.Content = "Show original";
            _isFormatted = true;
        }
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
