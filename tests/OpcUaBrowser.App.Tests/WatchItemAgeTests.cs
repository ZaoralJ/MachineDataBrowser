using Opc.Ua;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class WatchItemAgeTests
{
    [Fact]
    public void Last_update_and_since_track_age_and_flag_stale()
    {
        var item = new WatchItemViewModel(new NodeId(1), "x") { RefreshMs = 250 };
        var t0 = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

        item.Apply(new ValueUpdate(item.NodeId, "1", StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow), t0);
        Assert.Equal("now", item.SinceText);
        Assert.Equal(1, item.UpdateCount);
        Assert.False(item.IsStale);

        item.RefreshAge(t0.AddSeconds(12));
        Assert.Equal("12 s ago", item.SinceText);
        Assert.True(item.IsStale);

        item.RefreshAge(t0.AddMinutes(3).AddSeconds(4));
        Assert.Equal("3 min 4 s ago", item.SinceText);
    }
}
