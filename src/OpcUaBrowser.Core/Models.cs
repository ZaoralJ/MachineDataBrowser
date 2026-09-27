using Opc.Ua;

namespace OpcUaBrowser.Core;

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
