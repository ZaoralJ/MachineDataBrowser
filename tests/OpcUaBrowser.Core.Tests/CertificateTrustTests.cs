using OpcUaBrowser.Core.Ua;
using Xunit;

namespace OpcUaBrowser.Core.Tests;

public sealed class CertificateTrustTests(OpcPlcFixture plc)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Untrusted_server_certificate_is_reported_and_can_be_trusted_once_or_permanently()
    {
        await using var client = new OpcUaClient();
        var options = new ConnectOptions { EndpointUrl = plc.EndpointUrl, UseSecurity = true, AutoAcceptUntrustedCertificates = false };

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(options, Ct));
        var certificate = Assert.IsType<ServerCertificate>(client.LastUntrustedCertificate);
        Assert.False(string.IsNullOrEmpty(certificate.Thumbprint));
        Assert.NotEmpty(certificate.RawData);

        await client.ConnectAsync(options with { AcceptedCertificateThumbprints = [certificate.Thumbprint] }, Ct);
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Null(client.LastUntrustedCertificate);
        await client.DisconnectAsync();

        var trusted = Path.Combine(ClientPaths.PkiRoot, "trusted", "certs", $"{certificate.Thumbprint}.der");
        try
        {
            client.TrustPermanently(certificate);
            await client.ConnectAsync(options, Ct);
            Assert.Equal(ConnectionState.Connected, client.State);
        }
        finally
        {
            File.Delete(trusted);
        }
    }
}
