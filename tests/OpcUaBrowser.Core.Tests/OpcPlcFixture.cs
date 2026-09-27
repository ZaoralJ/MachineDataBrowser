using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

[assembly: AssemblyFixture(typeof(OpcUaBrowser.Core.Tests.OpcPlcFixture))]

namespace OpcUaBrowser.Core.Tests;

public sealed class OpcPlcFixture : IAsyncLifetime
{
    private const int OpcPort = 50000;

    private readonly IContainer _container = new ContainerBuilder("mcr.microsoft.com/iotedge/opc-plc:latest")
        .WithPortBinding(OpcPort, true)
        .WithCommand($"--pn={OpcPort}", "--autoaccept", "--unsecuretransport", "--ph=localhost")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("OPC UA Server started"))
        .Build();

    public string EndpointUrl => $"opc.tcp://localhost:{_container.GetMappedPublicPort(OpcPort)}";

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
