using Opc.Ua;
using Opc.Ua.Configuration;

namespace OpcUaBrowser.Core;

internal static class ClientConfiguration
{
    public const string ApplicationName = "OpcUaBrowser";

    public static string PkiRoot { get; } = Path.Combine(ClientPaths.DataRoot, "pki");

    public static async Task<ApplicationConfiguration> CreateAsync(
        ITelemetryContext telemetry,
        Func<CertificateValidationEventArgs, bool> acceptUntrusted,
        CancellationToken cancellationToken)
    {
        var validator = new CertificateValidator(telemetry);
        validator.CertificateValidation += (_, e) =>
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted && acceptUntrusted(e))
            {
                e.Accept = true;
            }
        };

        var config = new ApplicationConfiguration
        {
            ApplicationName = ApplicationName,
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:{ApplicationName}",
            ApplicationType = ApplicationType.Client,
            CertificateValidator = validator,
            TransportQuotas = new TransportQuotas { OperationTimeout = 30_000 },
            ClientConfiguration = new Opc.Ua.ClientConfiguration { DefaultSessionTimeout = 60_000 },
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(PkiRoot, "own"),
                    SubjectName = $"CN={ApplicationName}, O=OpcUaBrowser, DC={System.Net.Dns.GetHostName()}",
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
        await instance.CheckApplicationInstanceCertificatesAsync(false, null, cancellationToken).ConfigureAwait(false);

        return config;

        static CertificateTrustList Store(string name) => new()
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = Path.Combine(PkiRoot, name),
        };
    }
}
