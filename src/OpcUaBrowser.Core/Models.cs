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
