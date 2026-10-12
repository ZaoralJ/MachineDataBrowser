using MachineDataBrowser.Core;
using Xunit;
using StatusCodes = MachineDataBrowser.Core.DeviceStatusCodes;

namespace MachineDataBrowser.Core.Tests;

public sealed class StatusTextTests
{
    [Fact]
    public void Codes_built_from_a_number_still_get_their_name()
    {
        Assert.Equal("Good", StatusText.Of(StatusCodes.Good));
        Assert.Equal("BadCommunicationError", StatusText.Of(StatusCodes.BadCommunicationError));
        Assert.Equal("BadNotWritable", StatusText.Of(StatusCodes.BadNotWritable));
    }
}
