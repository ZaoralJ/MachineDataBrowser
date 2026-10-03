using MachineDataBrowser.Core;
using Opc.Ua;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class StatusTextTests
{
    [Fact]
    public void Codes_built_from_a_number_still_get_their_name()
    {
        Assert.Equal("Good", StatusText.Of(new StatusCode(0u)));
        Assert.Equal("BadCommunicationError", StatusText.Of(new StatusCode(StatusCodes.BadCommunicationError)));
        Assert.Equal("BadNotWritable", StatusText.Of(StatusCodes.BadNotWritable));
    }
}
