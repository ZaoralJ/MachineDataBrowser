using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core;
using Opc.Ua;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class WatchItemToolTipTests
{
    [Fact]
    public void Complex_values_show_pretty_json_in_the_value_tooltip()
    {
        var item = new WatchItemViewModel(new DeviceNodeId("BoilerStatus", 2), "BoilerStatus");
        var value = new ExtensionObject(new BuildInfo { ProductName = "PLC", BuildNumber = "7" });
        item.Apply(new ValueUpdate(item.NodeId, value.ToString()!, DeviceStatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow, Raw: value));

        Assert.Contains("\n", item.ValueToolTip, StringComparison.Ordinal);
        Assert.Contains("  \"ProductName\": \"PLC\"", item.ValueToolTip, StringComparison.Ordinal);
        Assert.Contains("  \"BuildNumber\": \"7\"", item.ValueToolTip, StringComparison.Ordinal);
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
}
