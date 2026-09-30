using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Opc.Ua;
using OpcUaBrowser.Core.Cip;
using Xunit;

namespace OpcUaBrowser.Core.Tests;

/// <summary>A PLC that disappears must surface as Reconnecting and Bad values, never as an exception or a crash.</summary>
public sealed class CipConnectionLossTests
{
    [Fact]
    public async Task Losing_the_plc_reports_reconnecting_and_bad_values()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/cip")
            .WithName("opcuabrowser-cip-simulator:test")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(ct);
        await using var container = new ContainerBuilder(image)
            .WithPortBinding(44818, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("EtherNet/IP server ready"))
            .Build();
        await container.StartAsync(ct);

        await using var client = new CipClient();
        var states = new System.Collections.Concurrent.ConcurrentQueue<ConnectionState>();
        client.StateChanged += (_, s) => states.Enqueue(s);
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = $"eip://localhost:{container.GetMappedPublicPort(44818)}/1,0" }, ct);

        var updates = new System.Collections.Concurrent.ConcurrentQueue<ValueUpdate>();
        await using var monitor = await client.MonitorAsync(new NodeId("Heartbeat", CipClient.TagNamespace), updates.Enqueue, 100, ct);
        await Until(() => updates.Any(u => StatusCode.IsGood(u.Status)));

        await container.StopAsync(ct);
        await Until(() => states.Contains(ConnectionState.Reconnecting), seconds: 30);
        await Until(() => updates.Any(u => StatusCode.IsBad(u.Status)), seconds: 30);

        Assert.Null((await client.ReadValuesAsync([new NodeId("Heartbeat", CipClient.TagNamespace)], ct))[0]);
        await client.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    private static async Task Until(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
