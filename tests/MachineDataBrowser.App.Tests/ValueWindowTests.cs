using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using MachineDataBrowser.App.Views;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class ValueWindowTests
{
    [AvaloniaFact]
    public void Valid_json_can_be_formatted_and_restored()
    {
        const string compact = "{\"enterprise\":\"ACME Toys\",\"site\":\"Billund\"}";
        const string pretty = "{\n  \"enterprise\": \"ACME Toys\",\n  \"site\": \"Billund\"\n}";
        var window = new ValueWindow("Value — _meta", compact);
        var text = window.FindControl<TextBox>("ValueText")!;
        var format = window.FindControl<Button>("FormatJsonButton")!;

        Assert.True(format.IsVisible);
        Assert.Equal(compact, text.Text);
        format.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(pretty, text.Text);
        Assert.Equal("Show original", format.Content);
        format.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(compact, text.Text);
        Assert.Equal("Format JSON", format.Content);
    }

    [AvaloniaFact]
    public void Non_json_values_do_not_offer_formatting()
    {
        var window = new ValueWindow("Value — text", "ordinary text");
        Assert.False(window.FindControl<Button>("FormatJsonButton")!.IsVisible);
    }
}
