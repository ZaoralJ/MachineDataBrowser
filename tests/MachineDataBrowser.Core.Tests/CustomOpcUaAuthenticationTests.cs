using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using MachineDataBrowser.Driver.OpcUa;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class CustomOpcUaAuthenticationTests
{
    [Fact]
    public async Task Configured_credentials_are_required_and_validated()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/opcua-custom")
            .WithName("machinedatabrowser-opcua-custom:test-authentication")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(ct);

        await using var container = new ContainerBuilder(image)
            .WithPortBinding(4841, true)
            .WithEnvironment("OPCUA_CUSTOM_USERNAME", "test-user")
            .WithEnvironment("OPCUA_CUSTOM_PASSWORD", "test-password")
            .WithEnvironment("OPCUA_CUSTOM_LARGE", "1,1,1")
            .WithEnvironment("OPCUA_CUSTOM_FLAT", "1")
            .WithEnvironment("OPCUA_CUSTOM_FAST_MS", "0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("OPC UA custom server ready"))
            .Build();
        await container.StartAsync(ct);

        var endpoint = $"opc.tcp://localhost:{container.GetMappedPublicPort(4841)}/";
        await using (var authenticated = new OpcUaClient())
        {
            await authenticated.ConnectAsync(new ConnectOptions
            {
                EndpointUrl = endpoint,
                UserName = "test-user",
                Password = "test-password",
                UseSecurity = true,
                AutoAcceptUntrustedCertificates = true,
            }, ct);

            Assert.Equal(ConnectionState.Connected, authenticated.State);
        }

        await using (var wrongPassword = new OpcUaClient())
        {
            var error = await Assert.ThrowsAsync<DeviceClientException>(() => wrongPassword.ConnectAsync(new ConnectOptions
            {
                EndpointUrl = endpoint,
                UserName = "test-user",
                Password = "wrong-password",
                UseSecurity = true,
                AutoAcceptUntrustedCertificates = true,
            }, ct));

            Assert.Contains("BadUserAccessDenied", error.StatusCode.Name, StringComparison.Ordinal);
        }

        await using var anonymous = new OpcUaClient();
        await Assert.ThrowsAsync<DeviceClientException>(() => anonymous.ConnectAsync(new ConnectOptions
        {
            EndpointUrl = endpoint,
            UseSecurity = true,
            AutoAcceptUntrustedCertificates = true,
        }, ct));
    }
}
