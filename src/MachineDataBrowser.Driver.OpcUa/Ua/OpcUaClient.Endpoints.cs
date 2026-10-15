using Opc.Ua;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.OpcUa;

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
        var (config, instance) = await ClientConfiguration.CreateAsync(Telemetry, (_, _) => false, cancellationToken).ConfigureAwait(false);
        try
        {
            using var discovery = await DiscoveryClient.CreateAsync(config, new Uri(endpointUrl), DiagnosticsMasks.None, cancellationToken).ConfigureAwait(false);
            ArrayOf<EndpointDescription> endpoints;
            try
            {
                endpoints = await discovery.GetEndpointsAsync(default, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await discovery.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return [.. endpoints.ToList()
                .OrderByDescending(e => e.SecurityLevel)
                .Select(e => new EndpointInfo(
                    e.EndpointUrl ?? string.Empty,
                    e.SecurityMode.ToString(),
                    SecurityPolicies.Default.GetDisplayName(e.SecurityPolicyUri ?? string.Empty) ?? e.SecurityPolicyUri ?? string.Empty,
                    e.SecurityLevel,
                    [.. e.UserIdentityTokens.ToList().Select(t => t.TokenType.ToString()).Distinct()],
                    !e.ServerCertificate.IsNull && !e.ServerCertificate.IsEmpty ? Thumbprint(e.ServerCertificate.Memory.ToArray()) : null))];
        }
        finally
        {
            await instance.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Thumbprint(byte[] certificate)
    {
        using var x509 = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(certificate);
        return x509.Thumbprint;
    }
}
