using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

[assembly: AssemblyFixture(typeof(MachineDataBrowser.Core.Tests.S7SimulatorFixture))]

namespace MachineDataBrowser.Core.Tests;

/// <summary>Builds and starts the S7 simulator from <c>simulators/s7</c> (the same image <c>just s7</c> runs).</summary>
public sealed class S7SimulatorFixture : IAsyncLifetime
{
    public const int S7Port = 102;

    private IContainer? _container;

    /// <summary>The simulator's symbol table, <c>simulators/s7/tags.csv</c>.</summary>
    public static string TagFile => Path.Combine(CommonDirectoryPath.GetGitDirectory().DirectoryPath, "simulators", "s7", "tags.csv");

    public string Host => $"localhost:{_container!.GetMappedPublicPort(S7Port)}";

    /// <summary>Without a tag list: only areas and typed-in addresses.</summary>
    public string BareUrl => $"s7://{Host}/0,1?db=1,2,3";

    public string EndpointUrl => $"s7://{Host}/0,1?tags={Uri.EscapeDataString(TagFile)}";

    public static IFutureDockerImage CreateImage(string tag) => new ImageFromDockerfileBuilder()
        .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/s7")
        // One tag per test project: see LogixSimulatorFixture.
        .WithName($"machinedatabrowser-s7-simulator:{tag}")
        .WithDeleteIfExists(false)
        .WithCleanUp(false)
        .Build();

    public static IContainer CreateContainer(IFutureDockerImage image) => new ContainerBuilder(image)
        .WithPortBinding(S7Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S7 server ready"))
        .Build();

    public async ValueTask InitializeAsync()
    {
        var image = CreateImage($"test-{typeof(S7SimulatorFixture).Assembly.GetName().Name!.ToLowerInvariant()}");
        await image.CreateAsync();
        _container = CreateContainer(image);
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
