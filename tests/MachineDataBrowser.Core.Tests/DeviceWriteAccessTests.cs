using MachineDataBrowser.Core;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class DeviceWriteAccessTests
{
    [Fact]
    public void AccessLevel_without_write_is_read_only()
    {
        Assert.False(DeviceWriteAccess.IsWritable([new AttributeValue("AccessLevel", "Read")]));
    }

    [Fact]
    public void User_access_level_takes_precedence_over_server_access_level()
    {
        Assert.False(DeviceWriteAccess.IsWritable([
            new AttributeValue("AccessLevel", "Read, Write"),
            new AttributeValue("UserAccessLevel", "Read"),
        ]));
    }

    [Fact]
    public void Missing_access_metadata_does_not_assume_read_only()
    {
        Assert.Null(DeviceWriteAccess.IsWritable([new AttributeValue("NodeClass", "Variable")]));
    }
}
