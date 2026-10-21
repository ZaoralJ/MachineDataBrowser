using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

[assembly: AssemblyFixture(typeof(MachineDataBrowser.Core.Tests.ModbusSimulatorFixture))]

namespace MachineDataBrowser.Core.Tests;

/// <summary>Builds and starts the Modbus simulator from <c>simulators/modbus</c> (the same image <c>just modbus</c> runs).</summary>
public sealed class ModbusSimulatorFixture : IAsyncLifetime
{
    private const int ModbusPort = 502;

    private readonly IFutureDockerImage _image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/modbus")
        // One tag per test project: the projects run in parallel, and Testcontainers stages each build in a temp file named
        // after the image, so a shared name lets one build delete another's file.
        .WithName($"machinedatabrowser-modbus-simulator:test-{typeof(ModbusSimulatorFixture).Assembly.GetName().Name!.ToLowerInvariant()}")
        .WithDeleteIfExists(false)
        .WithCleanUp(false)
        .Build();

    private IContainer? _container;

    public string EndpointUrl => $"modbus://localhost:{_container!.GetMappedPublicPort(ModbusPort)}/1";

    public async ValueTask InitializeAsync()
    {
        await _image.CreateAsync();
        _container = new ContainerBuilder(_image)
            .WithPortBinding(ModbusPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Modbus TCP server ready"))
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
