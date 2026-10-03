using Opc.Ua;
using MachineDataBrowser.Core.Ua;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

/// <summary>HistoryRead against the History folder of <c>simulators/opcua-custom</c> (two hours prefilled, 10 s apart).</summary>
public sealed class HistoryTests(CustomTypesServerFixture server) : IAsyncLifetime
{
    private readonly OpcUaClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = server.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<NodeId> TemperatureAsync()
    {
        var node = ObjectIds.ObjectsFolder;
        foreach (var name in new[] { "Custom", "History", "Temperature" })
        {
            node = (await _client.BrowseAsync(node, Ct)).Single(c => c.DisplayName == name).NodeId;
        }

        return node;
    }

    [Fact]
    public async Task Raw_history_of_the_last_hour_is_read_across_pages_oldest_first()
    {
        var temperature = await TemperatureAsync();
        var now = DateTime.UtcNow;

        var hour = await _client.ReadHistoryAsync(temperature, now.AddHours(-1), now, 10_000, Ct);
        Assert.False(hour.Truncated);
        Assert.InRange(hour.Values.Count, 355, 450); // 360 prefilled (10 s apart) plus the live ones (1 s apart)
        Assert.All(hour.Values, v => Assert.NotNull(v.Numeric));
        Assert.Equal(hour.Values.OrderBy(v => v.SourceTimestamp).Select(v => v.SourceTimestamp), hour.Values.Select(v => v.SourceTimestamp));

        var capped = await _client.ReadHistoryAsync(temperature, now.AddHours(-2), now, 500, Ct);
        Assert.True(capped.Truncated); // 720+ stored values, the oldest 500 returned
        Assert.Equal(500, capped.Values.Count);
    }

    [Fact]
    public async Task Servers_without_continuation_points_are_paged_by_time()
    {
        // The simulator (asyncua) returns a full page and no continuation point; small pages force paging by time.
        var temperature = await TemperatureAsync();
        var now = DateTime.UtcNow;
        var all = await _client.ReadHistoryAsync(temperature, now.AddHours(-1), now, 10_000, Ct);
        var pageSize = OpcUaClient.HistoryPageSize;
        try
        {
            OpcUaClient.HistoryPageSize = 100;
            var paged = await _client.ReadHistoryAsync(temperature, now.AddHours(-1), now, 10_000, Ct);
            Assert.True(paged.Values.Count > 300, $"{paged.Values.Count} values");
            Assert.Equal(all.Values.Count, paged.Values.Count);
            Assert.Equal(all.Values.Select(v => v.SourceTimestamp), paged.Values.Select(v => v.SourceTimestamp));
            Assert.False(paged.Truncated);

            var capped = await _client.ReadHistoryAsync(temperature, now.AddHours(-1), now, 150, Ct);
            Assert.Equal(150, capped.Values.Count);
            Assert.True(capped.Truncated);
        }
        finally
        {
            OpcUaClient.HistoryPageSize = pageSize;
        }
    }

    [Fact]
    public async Task Variable_without_history_returns_nothing_or_an_error()
    {
        // Servers differ: asyncua answers with no values, others with BadHistoryOperationUnsupported.
        try
        {
            var result = await _client.ReadHistoryAsync(VariableIds.Server_ServerStatus_CurrentTime, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, 100, Ct);
            Assert.Empty(result.Values);
        }
        catch (ServiceResultException)
        {
        }
    }
}
