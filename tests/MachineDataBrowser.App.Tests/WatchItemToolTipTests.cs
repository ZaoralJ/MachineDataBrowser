using Avalonia.Controls;
using Avalonia.VisualTree;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core;
using Opc.Ua;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class WatchItemToolTipTests
{
    [Fact]
    public void Complex_value_tooltip_is_a_short_summary_and_full_value_stays_available()
    {
        var item = new WatchItemViewModel(new DeviceNodeId("BoilerStatus", 2), "BoilerStatus");
        var value = new ExtensionObject(new BuildInfo { ProductName = "PLC", BuildNumber = "7" });
        item.Apply(new ValueUpdate(item.NodeId, value.ToString()!, DeviceStatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow, Raw: value));

        Assert.StartsWith("Object · ", item.ValueToolTip, StringComparison.Ordinal);
        Assert.Contains(" properties", item.ValueToolTip, StringComparison.Ordinal);
        Assert.Contains("Double-click to view full value", item.ValueToolTip, StringComparison.Ordinal);
        Assert.Contains("\"ProductName\": \"PLC\"", item.FullValueText, StringComparison.Ordinal);
        Assert.Contains("\"BuildNumber\": \"7\"", item.FullValueText, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_array_tooltip_does_not_expand_all_items()
    {
        var item = new WatchItemViewModel(new DeviceNodeId("Samples", 2), "Samples");
        var values = Enumerable.Range(0, 200).ToArray();
        item.Apply(new ValueUpdate(item.NodeId, "[200 items]", DeviceStatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow, Raw: values));

        Assert.Equal("Array · 200 items\nDouble-click to view full value", item.ValueToolTip);
        Assert.StartsWith("[\n  0,\n  1,\n  2", item.FullValueText, StringComparison.Ordinal);
        Assert.EndsWith("  198,\n  199\n]", item.FullValueText, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_text_tooltip_is_a_summary_and_full_value_is_pretty_printed()
    {
        const string json = "{\"enterprise\":\"ACME Toys\","
            + "\"site\":\"Billund\",\"timezone\":\"Europe/Copenhagen\",\"areas\":[\"moulding\"]}";
        var item = new WatchItemViewModel(new DeviceNodeId("_meta", 2), "_meta");
        item.Apply(new ValueUpdate(
            item.NodeId, json, DeviceStatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow, Raw: json));

        Assert.Equal("Object · 4 properties\nDouble-click to view full value", item.ValueToolTip);
        Assert.Contains("\n  \"enterprise\": \"ACME Toys\",", item.FullValueText, StringComparison.Ordinal);
        Assert.Contains("\n  \"areas\": [\n    \"moulding\"\n  ]", item.FullValueText, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_full_value_stays_unchanged()
    {
        var item = new WatchItemViewModel(new DeviceNodeId("Message", 2), "Message");
        item.Apply(new ValueUpdate(
            item.NodeId, "Pump is running", DeviceStatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow, Raw: "Pump is running"));

        Assert.Equal("Pump is running", item.FullValueText);
    }

    [Fact]
    public void Scalar_tooltip_keeps_the_display_hint()
    {
        var item = new WatchItemViewModel(new DeviceNodeId("Temperature", 2), "Temperature")
        {
            Display = new ValueDisplay { Format = ValueFormat.Hex },
        };
        item.Apply(new ValueUpdate(item.NodeId, "42", DeviceStatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow, Raw: 42));

        Assert.Equal("42 as sent · shown hex", item.ValueToolTip);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Full_value_dialog_wraps_long_values_and_aligns_them_to_the_top()
    {
        var window = new ValueWindow("Value — Samples", string.Join(", ", Enumerable.Range(0, 1000)));
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var value = window.GetVisualDescendants().OfType<TextBox>().Single(textBox => textBox.Name == "ValueText");

        Assert.Equal("Wrap", value.TextWrapping.ToString());
        Assert.Equal("Top", value.VerticalContentAlignment.ToString());
        Assert.Equal("Disabled", value.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty).ToString());
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Full_value_dialog_close_button_closes_the_window()
    {
        var window = new ValueWindow("Value — Samples", "[0, 1, 2]");
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var close = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Content?.ToString() == "Close");
        close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
    }
}
