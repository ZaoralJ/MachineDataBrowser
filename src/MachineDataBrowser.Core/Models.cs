using System.Globalization;

namespace MachineDataBrowser.Core;

public enum DeviceStatusSeverity
{
    Good,
    Uncertain,
    Bad,
}

/// <summary>A protocol-neutral status category and display code.</summary>
public readonly record struct DeviceStatusCode(string Name, DeviceStatusSeverity Severity, uint Code = 0)
{
    public bool IsGood => Severity == DeviceStatusSeverity.Good;

    public bool IsBad => Severity == DeviceStatusSeverity.Bad;

    public bool IsUncertain => Severity == DeviceStatusSeverity.Uncertain;

    public static bool IsGoodCode(DeviceStatusCode status) => status.IsGood;

    public static bool IsBadCode(DeviceStatusCode status) => status.IsBad;

    public static bool IsUncertainCode(DeviceStatusCode status) => status.IsUncertain;

    public static DeviceStatusCode FromName(string name) => new(name,
        name.StartsWith("Good", StringComparison.Ordinal) ? DeviceStatusSeverity.Good
        : name.StartsWith("Uncertain", StringComparison.Ordinal) ? DeviceStatusSeverity.Uncertain
        : DeviceStatusSeverity.Bad);

    public override string ToString() => Name;
}

public static class DeviceStatusCodes
{
    public static DeviceStatusCode Good => Code("Good");
    public static DeviceStatusCode UncertainLastUsableValue => Code("UncertainLastUsableValue");
    public static DeviceStatusCode Bad => Code("Bad");
    public static DeviceStatusCode BadNoData => Code("BadNoData");
    public static DeviceStatusCode BadNoCommunication => Code("BadNoCommunication");
    public static DeviceStatusCode BadNotWritable => Code("BadNotWritable");
    public static DeviceStatusCode BadNodeIdUnknown => Code("BadNodeIdUnknown");
    public static DeviceStatusCode BadTcpEndpointUrlInvalid => Code("BadTcpEndpointUrlInvalid");
    public static DeviceStatusCode BadCommunicationError => Code("BadCommunicationError");
    public static DeviceStatusCode BadNotConnected => Code("BadNotConnected");
    public static DeviceStatusCode BadNodeIdInvalid => Code("BadNodeIdInvalid");
    public static DeviceStatusCode BadAttributeIdInvalid => Code("BadAttributeIdInvalid");
    public static DeviceStatusCode BadDecodingError => Code("BadDecodingError");
    public static DeviceStatusCode BadNotReadable => Code("BadNotReadable");
    public static DeviceStatusCode BadTimeout => Code("BadTimeout");
    public static DeviceStatusCode BadUnexpectedError => Code("BadUnexpectedError");
    public static DeviceStatusCode BadOutOfRange => Code("BadOutOfRange");
    public static DeviceStatusCode BadSensorFailure => Code("BadSensorFailure");
    public static DeviceStatusCode BadConfigurationError => Code("BadConfigurationError");

    private static DeviceStatusCode Code(string name) =>
        DeviceStatusCode.FromName(name) with
        {
            Code = name switch
            {
                "Good" => 0x00000000,
                "UncertainLastUsableValue" => 0x40900000,
                "Bad" => 0x80000000,
                "BadUnexpectedError" => 0x80010000,
                "BadCommunicationError" => 0x80050000,
                "BadDecodingError" => 0x80070000,
                "BadTimeout" => 0x800A0000,
                "BadNoCommunication" => 0x80310000,
                "BadNodeIdInvalid" => 0x80330000,
                "BadNodeIdUnknown" => 0x80340000,
                "BadAttributeIdInvalid" => 0x80350000,
                "BadNotReadable" => 0x803A0000,
                "BadNotWritable" => 0x803B0000,
                "BadOutOfRange" => 0x803C0000,
                "BadSensorFailure" => 0x80560000,
                "BadTcpEndpointUrlInvalid" => 0x80830000,
                "BadConfigurationError" => 0x80890000,
                "BadNotConnected" => 0x808A0000,
                "BadNoData" => 0x809B0000,
                _ => 0,
            },
        };
}

public sealed record DeviceError(DeviceStatusCode StatusCode, string LocalizedText)
{
    public static DeviceError Good { get; } = new(DeviceStatusCodes.Good, string.Empty);

    public override string ToString() => StatusCode.IsGood ? StatusCode.ToString() : $"{StatusCode}: {LocalizedText}";
}

public sealed class DeviceClientException : Exception
{
    public DeviceClientException(DeviceError error)
        : base(error.LocalizedText)
    {
        Error = error;
    }

    public DeviceClientException(DeviceStatusCode statusCode, string message, Exception? innerException = null)
        : this(new DeviceError(statusCode, message), innerException)
    {
    }

    private DeviceClientException(DeviceError error, Exception? innerException)
        : base(error.LocalizedText, innerException)
    {
        Error = error;
    }

    public DeviceError Error { get; }

    public DeviceStatusCode StatusCode => Error.StatusCode;
}

/// <summary>Opaque identity of a node in a connected device's address space.</summary>
public readonly record struct DeviceNodeId
{
    private readonly string? _identifier;

    public DeviceNodeId(string identifier, ushort namespaceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        _identifier = identifier;
        NamespaceIndex = namespaceIndex;
        IsNumeric = false;
    }

    public DeviceNodeId(uint identifier, ushort namespaceIndex = 0)
    {
        _identifier = identifier.ToString(CultureInfo.InvariantCulture);
        NamespaceIndex = namespaceIndex;
        IsNumeric = true;
    }

    private DeviceNodeId(string identifier, bool opaque)
    {
        _identifier = identifier;
        IsOpaque = opaque;
    }

    public static DeviceNodeId Null => default;

    public string Identifier => _identifier ?? string.Empty;

    public ushort NamespaceIndex { get; }

    public bool IsNumeric { get; }

    public bool IsOpaque { get; }

    public bool IsNull => string.IsNullOrEmpty(Identifier);

    public string IdentifierAsString => Identifier;

    public static DeviceNodeId FromOpaque(string identity) =>
        new(identity ?? throw new ArgumentNullException(nameof(identity)), opaque: true);

    public bool TryGetValue(out string value)
    {
        value = Identifier;
        return !IsNull;
    }

    public bool Equals(DeviceNodeId other) => StringComparer.Ordinal.Equals(ToString(), other.ToString());

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    public override string ToString() => IsOpaque
        ? Identifier
        : $"{(NamespaceIndex == 0 ? string.Empty : $"ns={NamespaceIndex};")}{(IsNumeric ? "i=" : "s=")}{Identifier}";
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Object is a protocol-neutral address-space node kind.")]
public enum DeviceNodeKind
{
    Unspecified,
    Object,
    Variable,
    Method,
    ObjectType,
    VariableType,
    DataType,
    ReferenceType,
    View,
}

/// <summary>Protocol-neutral scalar value types used for parsing and display.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These names describe protocol-neutral scalar types.")]
public enum DeviceDataType
{
    Null,
    Boolean,
    SByte,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    String,
    DateTime,
    Guid,
    ByteString,
    XmlElement,
    NodeId,
    ExpandedNodeId,
    StatusCode,
    QualifiedName,
    LocalizedText,
    ExtensionObject,
    DataValue,
    Variant,
    DiagnosticInfo,
}

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

    /// <summary>
    /// <see cref="IPausableDiscovery"/> clients (MQTT): pause discovery this many seconds after connecting, so a busy
    /// broker is browsed briefly and then only monitored topics are received. 0 keeps discovering.
    /// </summary>
    public int AutoPauseDiscoverySeconds { get; init; }

    /// <summary>
    /// Clients without names in the device (Modbus, S7): a file naming its addresses (register map, symbol table), shown
    /// as a tag tree. Null: none.
    /// </summary>
    public string? TagMapPath { get; init; }
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

public sealed record BrowseItem(DeviceNodeId NodeId, string DisplayName, string BrowseName, DeviceNodeKind NodeClass, bool HasChildren = true);

public sealed record AttributeValue(string Name, string Value);

/// <summary>A device-reported numeric engineering range for a variable, when its protocol exposes one.</summary>
public sealed record EngineeringRange(double Low, double High);

/// <summary>Optional richer metadata used to check a write before asking the user to confirm it.</summary>
public interface IEngineeringRangeSource
{
    /// <summary>The range of each variable; null where no valid EURange is exposed.</summary>
    Task<IReadOnlyList<EngineeringRange?>> ReadRangesAsync(IReadOnlyList<DeviceNodeId> nodeIds, CancellationToken cancellationToken = default);
}

public sealed record ValueUpdate(DeviceNodeId NodeId, string Value, DeviceStatusCode Status, DateTime SourceTimestamp, DateTime ServerTimestamp, double? Numeric = null, object? Raw = null);

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

public sealed record MonitorResult(DeviceNodeId NodeId, IAsyncDisposable? Handle, DeviceError Error);

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
    DeviceNodeId? ConditionId = null,
    string? ConditionName = null,
    bool? IsActive = null,
    bool? IsAcked = null,
    bool Retain = false)
{
    public bool IsCondition => ConditionId is { IsNull: false };
}

/// <summary>Clients that can read a variable's stored history (OPC UA HistoryRead).</summary>
public interface IHistorySource
{
    /// <summary>
    /// Raw stored values of <paramref name="nodeId"/> between <paramref name="startTime"/> and <paramref name="endTime"/>, oldest
    /// first, at most <paramref name="maxValues"/> (the rest is reported by <see cref="HistoryResult.Truncated"/>).
    /// </summary>
    Task<HistoryResult> ReadHistoryAsync(DeviceNodeId nodeId, DateTime startTime, DateTime endTime, int maxValues, CancellationToken cancellationToken = default);
}

public sealed record HistoryResult(IReadOnlyList<ValueUpdate> Values, bool Truncated);

/// <summary>One input or output argument of a method.</summary>
public sealed record MethodArgument(string Name, string DataType, DeviceDataType ValueType, bool IsArray, string Description);

public sealed record MethodSignature(IReadOnlyList<MethodArgument> Inputs, IReadOnlyList<MethodArgument> Outputs);

/// <summary>Clients that can call methods (OPC UA Call).</summary>
public interface IMethodCaller
{
    /// <summary>The input and output arguments of <paramref name="methodId"/>.</summary>
    Task<MethodSignature> GetMethodSignatureAsync(DeviceNodeId methodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="methodId"/> on <paramref name="objectId"/> with <paramref name="inputs"/> as text, each parsed
    /// into its argument's type like written values. Returns the outputs as display text.
    /// </summary>
    Task<IReadOnlyList<string>> CallMethodAsync(DeviceNodeId objectId, DeviceNodeId methodId, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
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
    Task<IReadOnlyList<DeviceError>> ApplyMonitoringOptionsAsync(IReadOnlyList<IAsyncDisposable> monitors, MonitoringOptions options, CancellationToken cancellationToken = default);
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

/// <summary>Clients that can describe their connection in detail; subscriptions are OPC UA only.</summary>
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
    Task<IAsyncDisposable> SubscribeEventsAsync(DeviceNodeId notifier, Action<EventNotification> onEvent, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges an alarm (the <paramref name="eventId"/> of its latest notification).</summary>
    Task AcknowledgeAsync(DeviceNodeId conditionId, byte[] eventId, string comment, CancellationToken cancellationToken = default);
}
