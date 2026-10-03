using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

[assembly: AssemblyFixture(typeof(MachineDataBrowser.Core.Tests.MqttSimulatorFixture))]

namespace MachineDataBrowser.Core.Tests;

/// <summary>Builds and starts the broker + publisher from <c>simulators/mqtt</c> (the same image <c>just mqtt</c> runs).</summary>
public sealed class MqttSimulatorFixture : IAsyncLifetime
{
    private const int MqttPort = 1883;

    private readonly IFutureDockerImage _image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/mqtt")
        // One tag per test project: the projects run in parallel, and Testcontainers stages each build in a temp file named
        // after the image, so a shared name lets one build delete another's file.
        .WithName($"machinedatabrowser-mqtt-simulator:test-{typeof(MqttSimulatorFixture).Assembly.GetName().Name!.ToLowerInvariant()}")
        .WithDeleteIfExists(false)
        .WithCleanUp(false)
        .Build();

    private IContainer? _container;

    public string EndpointUrl => $"mqtt://localhost:{_container!.GetMappedPublicPort(MqttPort)}";

    public IContainer Container => _container!;

    public async ValueTask InitializeAsync()
    {
        await _image.CreateAsync();
        _container = new ContainerBuilder(_image)
            .WithPortBinding(MqttPort, true)
            .WithEnvironment("MQTT_SIM_BULK", "50")
            .WithEnvironment("MQTT_SIM_DEATH_PERIOD_S", "6")
            .WithEnvironment("MQTT_SIM_REBIRTH_S", "2")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("MQTT simulator ready"))
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
