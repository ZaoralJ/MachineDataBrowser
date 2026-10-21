using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

[assembly: AssemblyFixture(typeof(MachineDataBrowser.Core.Tests.PcccSimulatorFixture))]

namespace MachineDataBrowser.Core.Tests;

/// <summary>Builds and starts the PCCC simulator from <c>simulators/pccc</c> (the same image <c>just pccc</c> runs).</summary>
public sealed class PcccSimulatorFixture : IAsyncLifetime
{
    private const int EipPort = 44818;

    private readonly IFutureDockerImage _image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/pccc")
        // One tag per test project: the projects run in parallel, and Testcontainers stages each build in a temp file named
        // after the image, so a shared name lets one build delete another's file.
        .WithName($"machinedatabrowser-pccc-simulator:test-{typeof(PcccSimulatorFixture).Assembly.GetName().Name!.ToLowerInvariant()}")
        .WithDeleteIfExists(false)
        .WithCleanUp(false)
        .Build();

    private IContainer? _container;

    public string Url(string plc) => $"pccc://localhost:{_container!.GetMappedPublicPort(EipPort)}?plc={plc}&files=N9,ST10,L11";

    public async ValueTask InitializeAsync()
    {
        await _image.CreateAsync();
        _container = new ContainerBuilder(_image)
            .WithPortBinding(EipPort, true)
            .WithEnvironment("PCCC_SIM_SEED", "1")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("PCCC server ready"))
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
