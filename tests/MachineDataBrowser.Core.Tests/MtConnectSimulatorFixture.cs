using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

[assembly: AssemblyFixture(typeof(MachineDataBrowser.Core.Tests.MtConnectSimulatorFixture))]

namespace MachineDataBrowser.Core.Tests;

/// <summary>Builds and starts the MTConnect agent simulator from <c>simulators/mtconnect</c> (the image <c>just mtconnect</c> runs).</summary>
public sealed class MtConnectSimulatorFixture : IAsyncLifetime
{
    private const int HttpPort = 5000;

    private readonly IFutureDockerImage _image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/mtconnect")
        // One tag per test project: see LogixSimulatorFixture.
        .WithName($"machinedatabrowser-mtconnect-simulator:test-{typeof(MtConnectSimulatorFixture).Assembly.GetName().Name!.ToLowerInvariant()}")
        .WithDeleteIfExists(false)
        .WithCleanUp(false)
        .Build();

    private IContainer? _container;

    public string EndpointUrl => $"mtconnect://localhost:{_container!.GetMappedPublicPort(HttpPort)}";

    /// <summary>The agent's plain HTTP address, the other form of endpoint URL.</summary>
    public string HttpUrl => $"http://localhost:{_container!.GetMappedPublicPort(HttpPort)}";

    public async ValueTask InitializeAsync()
    {
        await _image.CreateAsync();
        _container = new ContainerBuilder(_image)
            .WithPortBinding(HttpPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("MTConnect agent on"))
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
