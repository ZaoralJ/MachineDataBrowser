using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

[assembly: AssemblyFixture(typeof(MachineDataBrowser.Core.Tests.RedisServerFixture))]

namespace MachineDataBrowser.Core.Tests;

/// <summary>Starts a disposable Valkey server used to exercise the Redis-protocol driver.</summary>
public sealed class RedisServerFixture : IAsyncLifetime
{
    private const int RedisPort = 6379;
    private IContainer? _container;

    public string EndpointUrl => $"redis://localhost:{_container!.GetMappedPublicPort(RedisPort)}/2";

    public string Host => _container!.Hostname;

    public int Port => _container!.GetMappedPublicPort(RedisPort);

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder("valkey/valkey:8-alpine")
            .WithPortBinding(RedisPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
