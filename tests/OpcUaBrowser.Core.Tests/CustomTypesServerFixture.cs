using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

[assembly: AssemblyFixture(typeof(OpcUaBrowser.Core.Tests.CustomTypesServerFixture))]

namespace OpcUaBrowser.Core.Tests;

/// <summary>
/// Builds and starts the asyncua server from <c>simulators/opcua-custom</c> (custom structures, enums, unions,
/// large address space). The address space is shrunk to keep startup fast, except for the Flat folder which
/// stays large enough to need browse continuation points.
/// </summary>
public sealed class CustomTypesServerFixture : IAsyncLifetime
{
    private const int OpcPort = 4841;

    private readonly IFutureDockerImage _image = new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/opcua-custom")
        .WithName("opcuabrowser-opcua-custom:test")
        .WithDeleteIfExists(false)
        .WithCleanUp(false)
        .Build();

    private IContainer? _container;

    public string EndpointUrl => $"opc.tcp://localhost:{_container!.GetMappedPublicPort(OpcPort)}/";

    public async ValueTask InitializeAsync()
    {
        await _image.CreateAsync();
        _container = new ContainerBuilder(_image)
            .WithPortBinding(OpcPort, true)
            .WithEnvironment("OPCUA_CUSTOM_LARGE", "2,2,5")
            .WithEnvironment("OPCUA_CUSTOM_FLAT", "2500")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("OPC UA custom server ready"))
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
