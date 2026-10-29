using System.IO;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MachineDataBrowser.Driver.Redis;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class ValkeyAuthenticationTests
{
    [Fact]
    public async Task Configured_acl_credentials_are_required_and_validated()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = CommonDirectoryPath.GetGitDirectory().DirectoryPath;
        var script = Path.Combine(repository, "simulators", "valkey", "entrypoint.sh");
        await using var container = new ContainerBuilder("valkey/valkey:8-alpine")
            .WithPortBinding(6379, true)
            .WithBindMount(script, "/sim/entrypoint.sh")
            .WithEntrypoint("/bin/sh", "/sim/entrypoint.sh")
            .WithCommand("valkey-server", "--save", string.Empty, "--appendonly", "no")
            .WithEnvironment("VALKEY_SIM_USERNAME", "test-user")
            .WithEnvironment("VALKEY_SIM_PASSWORD", "test-password")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        await container.StartAsync(ct);

        var endpoint = $"redis://localhost:{container.GetMappedPublicPort(6379)}/0";
        await using (var authenticated = new RedisClient())
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
            await using var rejected = new RedisClient();
            await Assert.ThrowsAsync<DeviceClientException>(() => rejected.ConnectAsync(new ConnectOptions
            {
                EndpointUrl = endpoint,
                UserName = username,
                Password = password,
            }, ct));
            Assert.Equal(ConnectionState.Disconnected, rejected.State);
        }
    }
}
