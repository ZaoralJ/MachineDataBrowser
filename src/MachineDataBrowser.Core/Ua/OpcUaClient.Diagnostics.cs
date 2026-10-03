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
            ("Session", $"{session.SessionName} · timeout {DiagnosticsFormat.Duration(session.SessionTimeout)}"),
            ("Connected since", DiagnosticsFormat.Since(_connectedAt)),
            ("Keep-alive", $"every {DiagnosticsFormat.Duration(session.KeepAliveInterval)} · last {DiagnosticsFormat.Ago(session.LastKeepAliveTime)}"),
            ("Reconnects", DiagnosticsFormat.Reconnects(_reconnects, _lastReconnect)),
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
                info.Add(("Server clock", $"{DiagnosticsFormat.Local(serverTime)} · {(Math.Abs(skew.TotalSeconds) < 1 ? "in sync" : $"{skew.TotalSeconds:+0.0;-0.0} s from this computer")}"));
            }

            if (values[2] is DateTime started)
            {
                info.Add(("Server started", $"{DiagnosticsFormat.Local(started)} ({DiagnosticsFormat.Ago(started)})"));
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
}
