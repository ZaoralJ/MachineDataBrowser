using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using MachineDataBrowser.Driver.MtConnect;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class MtConnectAuthenticationTests
{
    [Fact]
    public async Task Configured_credentials_are_required_and_validated()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/mtconnect")
            .WithName("machinedatabrowser-mtconnect-simulator:test-authentication")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(ct);

        await using var container = new ContainerBuilder(image)
            .WithPortBinding(0, 5000)
            .WithEnvironment("MTCONNECT_SIM_USERNAME", "test-user")
            .WithEnvironment("MTCONNECT_SIM_PASSWORD", "test-password")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("MTConnect agent on"))
            .Build();
        await container.StartAsync(ct);

        var endpoint = $"mtconnect://localhost:{container.GetMappedPublicPort(5000)}";
        await using (var authenticated = new MtConnectClient())
        {
            await authenticated.ConnectAsync(new ConnectOptions
            {
                EndpointUrl = endpoint,
                UserName = "test-user",
                Password = "test-password",
            }, ct);

            Assert.Equal(ConnectionState.Connected, authenticated.State);
        }

        foreach (var (username, password) in new (string?, string?)[]
        {
            ("test-user", "wrong-password"),
            (null, null),
        })
        {
            await using var rejected = new MtConnectClient();
            var error = await Assert.ThrowsAsync<DeviceClientException>(() => rejected.ConnectAsync(new ConnectOptions
            {
                EndpointUrl = endpoint,
                UserName = username,
                Password = password,
            }, ct));

            Assert.Contains("HTTP 401", error.Message, StringComparison.Ordinal);
            Assert.Equal(ConnectionState.Disconnected, rejected.State);
        }
    }
}
