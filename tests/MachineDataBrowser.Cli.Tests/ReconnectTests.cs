using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>
/// Stops and restarts its own device containers, so the shared ones used by the other tests are left alone. Host ports
/// are fixed: a restarted container would otherwise get a different random port and the client could never come back.
/// </summary>
public sealed class ReconnectTests
{
    [Fact]
    public Task Opc_ua_monitor_shows_bad_values_while_disconnected_and_reloads_them_after_reconnecting() =>
        MonitorAcrossRestartAsync(port => new ContainerBuilder("mcr.microsoft.com/iotedge/opc-plc:latest")
                .WithPortBinding(port, port)
                .WithCommand($"--pn={port}", "--autoaccept", "--unsecuretransport", "--ph=localhost")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("OPC UA Server started"))
                .Build(),
            port => $"opc.tcp://localhost:{port}", "/Objects/OpcPlc/Telemetry/Basic/StepUp", "--trust-all");

    [Fact]
    public async Task Logix_monitor_shows_bad_values_while_disconnected_and_reloads_them_after_reconnecting()
    {
        var image = await ImageAsync("simulators/cip", "cip");
        await MonitorAcrossRestartAsync(port => new ContainerBuilder(image)
                .WithPortBinding(port, 44818)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("EtherNet/IP server ready"))
                .Build(),
            port => $"eip://localhost:{port}/1,0", "HighSpeed.Axes[0].Encoder");
    }

    [Fact]
    public async Task Mqtt_monitor_shows_bad_values_while_disconnected_and_reloads_them_after_reconnecting()
    {
        var image = await ImageAsync("simulators/mqtt", "mqtt");
        await MonitorAcrossRestartAsync(port => new ContainerBuilder(image)
                .WithPortBinding(port, 1883)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("MQTT simulator ready"))
                .Build(),
            port => $"mqtt://localhost:{port}", "/Topics/machines/m1/status/speed");
    }

    private static async Task<IFutureDockerImage> ImageAsync(string directory, string name)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), directory)
            // Own tag: Testcontainers stages each build in a temp file named after the image.
            .WithName($"machinedatabrowser-{name}-simulator:test-reconnect")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(TestContext.Current.CancellationToken);
        return image;
    }

    private static async Task MonitorAcrossRestartAsync(Func<int, IContainer> create, Func<int, string> url, string node, params string[] extra)
    {
        var port = FreePort();
        await using var container = create(port);

        using var stdout = new SyncWriter();
        using var stderr = new SyncWriter();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // Started while the device is still off: it waits until it can connect.
        var monitor = Commands.Build(stdout, stderr).Parse(["monitor", url(port), node, "-r", "100", "-f", "json", .. extra])
            .InvokeAsync(cancellationToken: stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(monitor.IsCompleted, stderr.ToString());

        await container.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => Statuses(stdout).Contains("Good"), stdout, stderr);
        await container.StopAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => Statuses(stdout).Contains("BadNotConnected"), stdout, stderr);
        var beforeRestart = Statuses(stdout).Count;

        // Off for a while: reconnect attempts fail and are retried.
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await container.StartAsync(TestContext.Current.CancellationToken);
        // The reloaded value, then live updates again.
        await WaitForAsync(() => Statuses(stdout).Skip(beforeRestart).Count(s => s == "Good") >= 3, stdout, stderr);
        await stop.CancelAsync();

        Assert.True(await monitor == 0, stderr.ToString() + stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static List<string> Statuses(SyncWriter output)
    {
        // The last line may still be half written.
        var text = output.ToString();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return [.. lines.Take(text.EndsWith('\n') ? lines.Length : lines.Length - 1).Select(l => (string)JsonNode.Parse(l)!["status"]!)];
    }

    private static async Task WaitForAsync(Func<bool> condition, SyncWriter stdout, SyncWriter stderr)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for the stream.\n{stderr}{stdout}");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The stream writes from another thread while the test reads.</summary>
    private sealed class SyncWriter : StringWriter
    {
        private readonly Lock _lock = new();

        public override void Write(char value)
        {
            lock (_lock)
            {
                base.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                base.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_lock)
            {
                base.WriteLine(value);
            }
        }

        public override Task WriteAsync(string? value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return base.ToString();
            }
        }
    }
}
