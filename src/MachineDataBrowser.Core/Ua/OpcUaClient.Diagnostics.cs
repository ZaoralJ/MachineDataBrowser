using System.Globalization;
using Opc.Ua;
using Opc.Ua.Client;

namespace MachineDataBrowser.Core.Ua;

public sealed partial class OpcUaClient : IConnectionDiagnosticsSource
{
    private int _reconnects;
    private DateTime? _connectedAt;
    private DateTime? _lastReconnect;

    public async Task<ConnectionDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var endpoint = session.Endpoint;
        var info = new List<(string, string)>
        {
            ("Endpoint", endpoint.EndpointUrl),
            ("Server", ServerUri ?? string.Empty),
            ("Security", $"{endpoint.SecurityMode} · {SecurityPolicies.GetDisplayName(endpoint.SecurityPolicyUri) ?? endpoint.SecurityPolicyUri}"),
            ("User", session.Identity?.DisplayName ?? "Anonymous"),
            ("Session", $"{session.SessionName} · timeout {Seconds(session.SessionTimeout)}"),
            ("Connected since", _connectedAt is { } since ? $"{Local(since)} ({Ago(since)})" : string.Empty),
            ("Keep-alive", $"every {Seconds(session.KeepAliveInterval)} · last {Ago(session.LastKeepAliveTime)}"),
            ("Reconnects", _reconnects == 0 ? "0" : $"{_reconnects} (last {Local(_lastReconnect!.Value)})"),
            ("Requests", $"{session.OutstandingRequestCount} outstanding · {session.DefunctRequestCount} defunct · {session.GoodPublishRequestCount} publish requests"),
        };

        // One Read for the server's own view: state, clock (and the skew to ours) and build.
        try
        {
            var read = await session.ReadAsync(null, 0, TimestampsToReturn.Neither,
                new ReadValueIdCollection
                {
                    new ReadValueId { NodeId = VariableIds.Server_ServerStatus_State, AttributeId = Attributes.Value },
                    new ReadValueId { NodeId = VariableIds.Server_ServerStatus_CurrentTime, AttributeId = Attributes.Value },
                    new ReadValueId { NodeId = VariableIds.Server_ServerStatus_StartTime, AttributeId = Attributes.Value },
                    new ReadValueId { NodeId = VariableIds.Server_ServerStatus_BuildInfo_ProductName, AttributeId = Attributes.Value },
                    new ReadValueId { NodeId = VariableIds.Server_ServerStatus_BuildInfo_SoftwareVersion, AttributeId = Attributes.Value },
                },
                cancellationToken).ConfigureAwait(false);
            var values = read.Results.Select(r => StatusCode.IsGood(r.StatusCode) ? r.Value : null).ToList();
            if (values[0] is int state)
            {
                info.Add(("Server state", ((ServerState)state).ToString()));
            }

            if (values[1] is DateTime serverTime)
            {
                var skew = serverTime - DateTime.UtcNow;
                info.Add(("Server clock", $"{Local(serverTime)} · {(Math.Abs(skew.TotalSeconds) < 1 ? "in sync" : $"{skew.TotalSeconds:+0.0;-0.0} s from this computer")}"));
            }

            if (values[2] is DateTime started)
            {
                info.Add(("Server started", $"{Local(started)} ({Ago(started)})"));
            }

            if (values[3] is string product)
            {
                info.Add(("Server product", values[4] is string version ? $"{product} {version}" : product));
            }
        }
        catch (ServiceResultException ex)
        {
            info.Add(("Server status", $"not readable: {ex.StatusCode}"));
        }

        var subscriptions = session.Subscriptions
            .Select(s => new SubscriptionDiagnostics(
                s.DisplayName ?? string.Empty,
                s.Id,
                s.CurrentPublishingInterval,
                s.MonitoredItemCount,
                s.NotificationCount,
                s.LastNotificationTime == DateTime.MinValue ? null : s.LastNotificationTime,
                s.CurrentKeepAliveCount,
                s.CurrentLifetimeCount,
                s.CurrentPublishingEnabled))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        return new ConnectionDiagnostics(info, subscriptions);
    }

    private static string Seconds(double ms) => ms >= 1000
        ? (ms / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s"
        : ms.ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Ago(DateTime utc)
    {
        if (utc == DateTime.MinValue)
        {
            return "never";
        }

        var age = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return age.TotalSeconds switch
        {
            < 1 => "just now",
            < 60 => $"{(int)age.TotalSeconds} s ago",
            < 3600 => $"{(int)age.TotalMinutes} min ago",
            < 86400 => $"{(int)age.TotalHours} h {age.Minutes} min ago",
            _ => $"{(int)age.TotalDays} days ago",
        };
    }
}
