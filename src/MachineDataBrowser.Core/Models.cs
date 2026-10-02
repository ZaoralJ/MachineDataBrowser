using Opc.Ua;

namespace MachineDataBrowser.Core;

public sealed record ConnectOptions
{
    public required string EndpointUrl { get; init; }

    /// <summary>Pick the most secure endpoint the server offers; otherwise SecurityMode None.</summary>
    public bool UseSecurity { get; init; }

    /// <summary>Trust unknown server certificates without asking. Intended for lab/dev servers.</summary>
    public bool AutoAcceptUntrustedCertificates { get; init; }

    public string? UserName { get; init; }

    public string? Password { get; init; }

    public uint SessionTimeoutMs { get; init; } = 60_000;

    /// <summary>Server certificates (SHA-1 thumbprints) the user trusted for this connection only.</summary>
    public IReadOnlyCollection<string> AcceptedCertificateThumbprints { get; init; } = [];
}

/// <summary>A server certificate the client did not trust, as shown to the user before trusting it.</summary>
public sealed record ServerCertificate(
    string Subject,
    string Issuer,
    string Thumbprint,
    DateTime NotBefore,
    DateTime NotAfter,
    string Problem,
    byte[] RawData)
{
    public bool IsExpired => DateTime.UtcNow > NotAfter.ToUniversalTime() || DateTime.UtcNow < NotBefore.ToUniversalTime();
}

/// <summary>Clients that validate server certificates and can be told to trust one.</summary>
public interface IServerCertificateTrust
{
    /// <summary>The certificate that made the last connect fail because it is not trusted; null otherwise.</summary>
    ServerCertificate? LastUntrustedCertificate { get; }

    /// <summary>Adds the certificate to the trusted store, so later connections accept it without asking.</summary>
    void TrustPermanently(ServerCertificate certificate);
}

public sealed record BrowseItem(NodeId NodeId, string DisplayName, string BrowseName, NodeClass NodeClass, bool HasChildren = true);

public sealed record AttributeValue(string Name, string Value);

public sealed record ValueUpdate(NodeId NodeId, string Value, StatusCode Status, DateTime SourceTimestamp, DateTime ServerTimestamp, double? Numeric = null, object? Raw = null);

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

public sealed record MonitorResult(NodeId NodeId, IAsyncDisposable? Handle, ServiceResult Error);

/// <summary>
/// One event or alarm notification. Condition fields (<see cref="ConditionId"/>, <see cref="IsActive"/>,
/// <see cref="IsAcked"/>, <see cref="Retain"/>) are set for alarms and conditions only.
/// </summary>
public sealed record EventNotification(
    DateTime Time,
    ushort Severity,
    string SourceName,
    string Message,
    string EventType,
    byte[]? EventId,
    NodeId? ConditionId = null,
    string? ConditionName = null,
    bool? IsActive = null,
    bool? IsAcked = null,
    bool Retain = false)
{
    public bool IsCondition => ConditionId is not null && !NodeId.IsNull(ConditionId);
}

/// <summary>Clients that can read a variable's stored history (OPC UA HistoryRead).</summary>
public interface IHistorySource
{
    /// <summary>
    /// Raw stored values of <paramref name="nodeId"/> between <paramref name="startTime"/> and <paramref name="endTime"/>, oldest
    /// first, at most <paramref name="maxValues"/> (the rest is reported by <see cref="HistoryResult.Truncated"/>).
    /// </summary>
    Task<HistoryResult> ReadHistoryAsync(NodeId nodeId, DateTime startTime, DateTime endTime, int maxValues, CancellationToken cancellationToken = default);
}

public sealed record HistoryResult(IReadOnlyList<ValueUpdate> Values, bool Truncated);

/// <summary>One input or output argument of a method.</summary>
public sealed record MethodArgument(string Name, string DataType, BuiltInType BuiltInType, bool IsArray, string Description);

public sealed record MethodSignature(IReadOnlyList<MethodArgument> Inputs, IReadOnlyList<MethodArgument> Outputs);

/// <summary>Clients that can call methods (OPC UA Call).</summary>
public interface IMethodCaller
{
    /// <summary>The input and output arguments of <paramref name="methodId"/>.</summary>
    Task<MethodSignature> GetMethodSignatureAsync(NodeId methodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="methodId"/> on <paramref name="objectId"/> with <paramref name="inputs"/> as text, each parsed
    /// into its argument's type like written values. Returns the outputs as display text.
    /// </summary>
    Task<IReadOnlyList<string>> CallMethodAsync(NodeId objectId, NodeId methodId, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}

public enum DeadbandKind
{
    None,
    Absolute,
    Percent,
}

/// <summary>
/// How the server samples and queues one monitored value. The defaults are what Watch has always used: sampling at the
/// refresh time, a queue of one, no deadband.
/// </summary>
public sealed record MonitoringOptions
{
    public static MonitoringOptions Default { get; } = new();

    /// <summary>Sampling interval; null = the item's refresh time (which is also the publishing interval).</summary>
    public double? SamplingIntervalMs { get; init; }

    /// <summary>Values the server keeps between publishes; more than 1 delivers every sample, not only the last.</summary>
    public uint QueueSize { get; init; } = 1;

    public bool DiscardOldest { get; init; } = true;

    public DeadbandKind Deadband { get; init; }

    /// <summary>Absolute change in the value's units, or percent of its EURange (servers need an EURange for percent).</summary>
    public double DeadbandValue { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => this == Default;

    public string Describe()
    {
        var parts = new List<string>(3);
        if (SamplingIntervalMs is { } sampling)
        {
            parts.Add($"sampling {sampling.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} ms");
        }

        if (QueueSize > 1)
        {
            parts.Add($"queue {QueueSize}{(DiscardOldest ? string.Empty : ", keep oldest")}");
        }

        if (Deadband != DeadbandKind.None)
        {
            parts.Add($"deadband {DeadbandValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{(Deadband == DeadbandKind.Percent ? " %" : string.Empty)}");
        }

        return parts.Count == 0 ? "default" : string.Join(" · ", parts);
    }
}

/// <summary>Clients whose monitored items can be tuned (OPC UA: sampling, queue, deadband).</summary>
public interface IMonitoringSettings
{
    /// <summary>
    /// Applies <paramref name="options"/> to existing monitors in place. Returns one result per monitor; a rejected one
    /// keeps its previous settings. Handles not created by this client are ignored (reported as Good).
    /// </summary>
    Task<IReadOnlyList<ServiceResult>> ApplyMonitoringOptionsAsync(IReadOnlyList<IAsyncDisposable> monitors, MonitoringOptions options, CancellationToken cancellationToken = default);
}

/// <summary>A point-in-time view of the connection, for Connection ▸ Diagnostics.</summary>
public sealed record ConnectionDiagnostics(
    IReadOnlyList<(string Name, string Value)> Session,
    IReadOnlyList<SubscriptionDiagnostics> Subscriptions);

public sealed record SubscriptionDiagnostics(
    string Name,
    uint Id,
    double PublishingIntervalMs,
    uint MonitoredItems,
    long Notifications,
    DateTime? LastNotification,
    uint KeepAliveCount,
    uint LifetimeCount,
    bool PublishingEnabled);

/// <summary>Clients that can describe their connection in detail (OPC UA).</summary>
public interface IConnectionDiagnosticsSource
{
    Task<ConnectionDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Clients that deliver events and alarms (OPC UA Alarms &amp; Conditions).</summary>
public interface IEventSource
{
    /// <summary>
    /// Subscribes to events of <paramref name="notifier"/> (the Server object for all of them) and asks the server to
    /// resend the current state of its conditions. Dispose the handle to unsubscribe.
    /// </summary>
    Task<IAsyncDisposable> SubscribeEventsAsync(NodeId notifier, Action<EventNotification> onEvent, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges an alarm (the <paramref name="eventId"/> of its latest notification).</summary>
    Task AcknowledgeAsync(NodeId conditionId, byte[] eventId, string comment, CancellationToken cancellationToken = default);
}
