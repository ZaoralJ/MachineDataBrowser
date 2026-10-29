using MachineDataBrowser.Core;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class ConnectionTests
{
    [Fact]
    public void Authentication_failure_is_not_retried_as_an_unreachable_endpoint()
    {
        var error = new DeviceClientException(DeviceStatusCodes.BadUserAccessDenied, "The credentials were rejected.");

        Assert.False(Connection.IsUnreachable(error));
    }
}
