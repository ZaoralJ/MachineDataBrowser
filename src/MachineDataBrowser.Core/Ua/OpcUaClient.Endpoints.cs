using Opc.Ua;

namespace MachineDataBrowser.Core.Ua;

/// <summary>One endpoint a server offers (GetEndpoints).</summary>
public sealed record EndpointInfo(
    string Url,
    string SecurityMode,
    string SecurityPolicy,
    byte SecurityLevel,
    IReadOnlyList<string> UserTokens,
    string? ServerCertificateThumbprint);

public sealed partial class OpcUaClient
{
    /// <summary>The endpoints <paramref name="endpointUrl"/> offers, without creating a session or trusting anything.</summary>
    public static async Task<IReadOnlyList<EndpointInfo>> GetEndpointsAsync(string endpointUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        var config = await ClientConfiguration.CreateAsync(Telemetry, _ => false, cancellationToken).ConfigureAwait(false);
        using var discovery = await DiscoveryClient.CreateAsync(config, new Uri(endpointUrl), DiagnosticsMasks.None, cancellationToken).ConfigureAwait(false);
        EndpointDescriptionCollection endpoints;
        try
        {
            endpoints = await discovery.GetEndpointsAsync(null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await discovery.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return [.. endpoints
            .OrderByDescending(e => e.SecurityLevel)
            .Select(e => new EndpointInfo(
                e.EndpointUrl,
                e.SecurityMode.ToString(),
                SecurityPolicies.GetDisplayName(e.SecurityPolicyUri) ?? e.SecurityPolicyUri,
                e.SecurityLevel,
                [.. (e.UserIdentityTokens ?? []).Select(t => t.TokenType.ToString()).Distinct()],
                e.ServerCertificate is { Length: > 0 } raw ? Thumbprint(raw) : null))];
    }

    private static string Thumbprint(byte[] certificate)
    {
        using var x509 = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(certificate);
        return x509.Thumbprint;
    }
}
