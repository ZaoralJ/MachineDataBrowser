using System.Collections.Concurrent;
using Opc.Ua;
using MachineDataBrowser.Core.Ua;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class EventTests(OpcPlcFixture plc) : IAsyncLifetime
{
    private readonly OpcUaClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl, AutoAcceptUntrustedCertificates = true }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    // opc-plc runs with --ses (simple events) and --alm (alarms). Its alarm notifications carry an all-zero EventId,
    // so acknowledging cannot be tested against it; the app tests cover acknowledging with a fake event source.
    [Fact]
    public async Task Server_events_and_alarm_conditions_arrive_with_their_fields()
    {
        var events = new ConcurrentQueue<EventNotification>();
        await using (await _client.SubscribeEventsAsync(ObjectIds.Server, events.Enqueue, Ct))
        {
            await Until(() => events.Any(e => !e.IsCondition) && events.Any(e => e.IsCondition && e.IsActive is not null));

            var plain = events.First(e => !e.IsCondition);
            Assert.False(string.IsNullOrEmpty(plain.EventType));
            Assert.False(string.IsNullOrEmpty(plain.Message));
            Assert.NotEqual(default, plain.Time);
            Assert.NotNull(plain.EventId);

            var alarm = events.First(e => e.IsCondition && e.IsActive is not null);
            Assert.False(string.IsNullOrEmpty(alarm.ConditionName));
            Assert.False(string.IsNullOrEmpty(alarm.SourceName));
            Assert.NotNull(alarm.IsAcked);
            Assert.True(alarm.Severity > 0);
        }

        // Unsubscribed: nothing more arrives.
        await Task.Delay(500, Ct);
        var count = events.Count;
        await Task.Delay(2000, Ct);
        Assert.Equal(count, events.Count);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timeout");
            await Task.Delay(100, Ct);
        }
    }
}
