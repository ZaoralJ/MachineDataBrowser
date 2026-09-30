using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

[assembly: AssemblyFixture(typeof(OpcUaBrowser.Core.Tests.LogixSimulatorFixture))]

namespace OpcUaBrowser.Core.Tests;

/// <summary>Builds and starts the Logix simulator from <c>simulators/cip</c> (the same image <c>just cip</c> runs).</summary>
public sealed class LogixSimulatorFixture : IAsyncLifetime
{
    private const int EipPort = 44818;

    private readonly IFutureDockerImage _image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/cip")
        .WithName("opcuabrowser-cip-simulator:test")
        .WithDeleteIfExists(false)
        .WithCleanUp(false)
        .Build();

    private IContainer? _container;

    public string EndpointUrl => $"eip://localhost:{_container!.GetMappedPublicPort(EipPort)}/1,0";

    public async ValueTask InitializeAsync()
    {
        await _image.CreateAsync();
        _container = new ContainerBuilder(_image)
            .WithPortBinding(EipPort, true)
            .WithEnvironment("CIP_SIM_SEED", "1")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("EtherNet/IP server ready"))
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
