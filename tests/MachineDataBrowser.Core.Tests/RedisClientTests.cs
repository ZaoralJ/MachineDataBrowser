using System.Collections.Concurrent;
using MachineDataBrowser.Core;
using MachineDataBrowser.Driver.Redis;
using StackExchange.Redis;
using System.Globalization;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class RedisClientTests(RedisServerFixture fixture)
{
    [Fact]
    public async Task Browses_colon_namespaces_and_reads_and_writes_strings_and_hash_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync($"{fixture.Host}:{fixture.Port}");
        var database = multiplexer.GetDatabase(2);
        await database.StringSetAsync("mdb-test:plant:temperature", "42.5", TimeSpan.FromMinutes(2));
        await database.HashSetAsync("mdb-test:plant:state", [new HashEntry("running", "true"), new HashEntry("mode", "auto")]);

        await using var client = new RedisClient();
        await client.ConnectAsync(new ConnectOptions { EndpointUrl = fixture.EndpointUrl }, cancellationToken);

        var root = await client.BrowseAsync(client.Root.NodeId, cancellationToken);
        var testNamespace = Assert.Single(root, item => item.DisplayName == "mdb-test");
        var plant = Assert.Single(await client.BrowseAsync(testNamespace.NodeId, cancellationToken), item => item.DisplayName == "plant");
        var members = await client.BrowseAsync(plant.NodeId, cancellationToken);
        var temperature = Assert.Single(members, item => item.DisplayName == "temperature");
        var state = Assert.Single(members, item => item.DisplayName == "state");
        Assert.Equal(DeviceNodeKind.Variable, temperature.NodeClass);
        Assert.Equal(DeviceNodeKind.Object, state.NodeClass);

        Assert.Equal("42.5", Assert.Single(await client.ReadValuesAsync([temperature.NodeId], cancellationToken)));
        var fields = await client.BrowseAsync(state.NodeId, cancellationToken);
        var running = Assert.Single(fields, item => item.DisplayName == "running");
        Assert.Equal("true", Assert.Single(await client.ReadValuesAsync([running.NodeId], cancellationToken)));
        Assert.Equal("field/", client.ToPortableId(running.NodeId)[..6]);

        var attributes = await client.ReadAttributesAsync(temperature.NodeId, cancellationToken);
        Assert.Contains(attributes, attribute => attribute.Name == "Type" && attribute.Value == "String");
        Assert.Contains(attributes, attribute => attribute.Name == "AccessLevel" && attribute.Value == "Read, Write");

        await client.WriteValueAsync(temperature.NodeId, "43.5", cancellationToken);
        await client.WriteValueAsync(running.NodeId, "false", cancellationToken);
        Assert.Equal("43.5", (await database.StringGetAsync("mdb-test:plant:temperature")).ToString());
        Assert.Equal("false", (await database.HashGetAsync("mdb-test:plant:state", "running")).ToString());
        Assert.NotNull(await database.KeyTimeToLiveAsync("mdb-test:plant:temperature"));

        var updates = new ConcurrentQueue<ValueUpdate>();
        await using (await client.MonitorAsync(temperature.NodeId, updates.Enqueue, 100, cancellationToken))
        {
            await Task.Delay(300, cancellationToken);
            Assert.Single(updates);
            await database.StringSetAsync("mdb-test:plant:temperature", "44.1");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (updates.Count < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken);
            }

            Assert.Equal(2, updates.Count);
            Assert.Equal("44.1", updates.Last().Value);
        }

        await database.HashDeleteAsync("mdb-test:plant:state", "mode");
        var staleField = Assert.Single(fields, item => item.DisplayName == "mode");
        var missingField = await Assert.ThrowsAsync<DeviceClientException>(() => client.WriteValueAsync(staleField.NodeId, "new", cancellationToken));
        Assert.Equal(DeviceStatusCodes.BadNodeIdUnknown, missingField.StatusCode);
        Assert.False(await database.HashExistsAsync("mdb-test:plant:state", "mode"));

        await database.KeyDeleteAsync("mdb-test:plant:temperature");
        var missingKey = await Assert.ThrowsAsync<DeviceClientException>(() => client.WriteValueAsync(temperature.NodeId, "new", cancellationToken));
        Assert.Equal(DeviceStatusCodes.BadNotWritable, missingKey.StatusCode);
        Assert.False(await database.KeyExistsAsync("mdb-test:plant:temperature"));
    }

    [Theory]
    [InlineData("redis://localhost:6379", 0, false)]
    [InlineData("redis://user:pass@localhost:6379/3", 3, false)]
    [InlineData("rediss://localhost/2", 2, true)]
    public void Parse_endpoint_reads_database_and_transport(string endpoint, int expectedDatabase, bool expectedTls)
    {
        var uri = RedisClient.ParseEndpoint(endpoint);

        Assert.Equal(expectedDatabase, uri.AbsolutePath.Trim('/').Length == 0 ? 0 : int.Parse(uri.AbsolutePath.Trim('/'), CultureInfo.InvariantCulture));
        Assert.Equal(expectedTls, uri.Scheme == "rediss");
    }

    [Theory]
    [InlineData("redis://localhost/not-a-database")]
    [InlineData("redis://localhost/1?db=2")]
    [InlineData("valkey://localhost")]
    public void Parse_endpoint_rejects_unsupported_forms(string endpoint)
    {
        Assert.Throws<FormatException>(() => RedisClient.ParseEndpoint(endpoint));
    }

    [Theory]
    [InlineData("rediss://user:secret@redis.example/2", "rediss://user@redis.example/2", "user")]
    [InlineData("redis://:secret@redis.example", "redis://@redis.example", null)]
    public void Endpoint_credentials_are_not_saved_with_passwords(string endpoint, string expected, string? expectedUser)
    {
        Assert.Equal(expected, EndpointUrlTools.WithoutPassword(endpoint));
        Assert.Equal(expectedUser, EndpointUrlTools.UserName(endpoint));
        Assert.True(EndpointUrlTools.HasPassword(endpoint));
    }
}
