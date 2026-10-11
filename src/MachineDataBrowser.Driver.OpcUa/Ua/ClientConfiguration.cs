using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;

namespace MachineDataBrowser.Core.Ua;

internal static class ClientConfiguration
{
    public const string ApplicationName = "MachineDataBrowser";

    public static string PkiRoot { get; } = Path.Combine(ClientPaths.DataRoot, "pki");

    public static string TrustedStorePath { get; } = Path.Combine(PkiRoot, "trusted");

    /// <summary>Writes <paramref name="rawData"/> (DER) where the trusted-peer directory store reads it.</summary>
    public static void AddTrusted(byte[] rawData, string thumbprint)
    {
        var certs = Path.Combine(TrustedStorePath, "certs");
        Directory.CreateDirectory(certs);
        File.WriteAllBytes(Path.Combine(certs, $"{thumbprint}.der"), rawData);
    }

    /// <summary>Whether the user chose to always trust this certificate (<see cref="AddTrusted"/>).</summary>
    public static bool IsTrusted(string thumbprint) =>
        File.Exists(Path.Combine(TrustedStorePath, "certs", $"{thumbprint}.der"));

    public static async Task<(ApplicationConfiguration Configuration, ApplicationInstance Instance)> CreateAsync(
        ITelemetryContext telemetry,
        Func<Certificate, ServiceResult, bool> acceptUntrusted,
        CancellationToken cancellationToken)
    {
        var config = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = ApplicationName,
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:{ApplicationName}",
            ApplicationType = ApplicationType.Client,
            TransportQuotas = new TransportQuotas { OperationTimeout = 30_000 },
            ClientConfiguration = new Opc.Ua.ClientConfiguration { DefaultSessionTimeout = 60_000 },
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(PkiRoot, "own"),
                    SubjectName = $"CN={ApplicationName}, O=MachineDataBrowser, DC={System.Net.Dns.GetHostName()}",
                },
                TrustedPeerCertificates = Store("trusted"),
                TrustedIssuerCertificates = Store("issuer"),
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(PkiRoot, "rejected"),
                },
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = true,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048,
            },
        };

        await config.ValidateAsync(ApplicationType.Client, cancellationToken).ConfigureAwait(false);

        var instance = new ApplicationInstance(config, telemetry)
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Client,
        };
        try
        {
            await instance.CheckApplicationInstanceCertificatesAsync(false, null, cancellationToken).ConfigureAwait(false);
            config.CertificateManager.AcceptError = (certificate, error) =>
                (error.StatusCode == StatusCodes.BadCertificateUntrusted
                    || error.StatusCode == StatusCodes.BadCertificatePolicyCheckFailed)
                && acceptUntrusted(certificate, error);
            return (config, instance);
        }
        catch
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        static CertificateTrustList Store(string name) => new()
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = Path.Combine(PkiRoot, name),
        };
    }
}
