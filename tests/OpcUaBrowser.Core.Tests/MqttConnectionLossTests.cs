using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Opc.Ua;
using OpcUaBrowser.Core.Mqtt;
using Xunit;

namespace OpcUaBrowser.Core.Tests;

/// <summary>A broker that goes away shows as Reconnecting; when it is back, values flow again (resubscribed).</summary>
public sealed class MqttConnectionLossTests
{
    [Fact]
    public async Task Broker_restart_reconnects_and_values_flow_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), "simulators/mqtt")
            .WithName("opcuabrowser-mqtt-simulator:test")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(ct);
        var port = 20000 + Random.Shared.Next(10000); // fixed host port, so the restarted broker is at the same address
        await using var container = new ContainerBuilder(image)
            .WithPortBinding(port, 1883)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("MQTT simulator ready"))
            .Build();
        await container.StartAsync(ct);

        await using var client = new MqttDeviceClient();
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = $"mqtt://localhost:{port}" }, ct);
        var updates = new ConcurrentQueue<ValueUpdate>();
        await using var monitor = await client.MonitorAsync(new NodeId("t:fast/10ms/counter", 1), updates.Enqueue, 0, ct);
        await Until(() => !updates.IsEmpty, ct);

        await container.StopAsync(ct);
        await Until(() => client.State == ConnectionState.Reconnecting, ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(ConnectionState.Reconnecting, client.State); // never "Connected" while the broker is down

        await container.StartAsync(ct);
        await Until(() => client.State == ConnectionState.Connected, ct, seconds: 30);
        updates.Clear();
        await Until(() => !updates.IsEmpty, ct); // resubscribed: new messages arrive
    }

    private static async Task Until(Func<bool> condition, CancellationToken ct, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            await Task.Delay(100, ct);
        }
    }
}
