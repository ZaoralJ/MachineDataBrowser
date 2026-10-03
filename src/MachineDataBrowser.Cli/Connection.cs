using MachineDataBrowser.Core;
using Opc.Ua;

namespace MachineDataBrowser.Cli;

/// <summary>An error with a message meant for the user; printed without a stack trace.</summary>
internal sealed class CliException(string message) : Exception(message);

internal sealed record ConnectionArgs(string Url, string? User, string? Password, bool Secure, bool TrustAll);

/// <summary>A resolved node: its id and the name to show for it.</summary>
internal sealed record Node(NodeId Id, string Name, string DisplayId);

internal static class Connection
{
    /// <summary>MQTT topics appear as messages arrive, so path lookups wait this long for them.</summary>
    private static readonly TimeSpan DynamicLookupTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SettleQuiet = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan SettleMax = TimeSpan.FromSeconds(10);

    public static async Task<IDeviceClient> ConnectAsync(ConnectionArgs args, CancellationToken cancellationToken)
    {
        var client = DeviceClient.Create(args.Url);
        try
        {
            await client.ConnectAsync(
                new ConnectOptions
                {
                    EndpointUrl = args.Url,
                    UseSecurity = args.Secure,
                    AutoAcceptUntrustedCertificates = args.TrustAll,
                    UserName = args.User,
                    Password = args.Password,
                },
                cancellationToken).ConfigureAwait(false);
            if (client is IDynamicAddressSpace dynamic)
            {
                await SettleAsync(dynamic, cancellationToken).ConfigureAwait(false);
            }

            return client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var untrusted = (client as IServerCertificateTrust)?.LastUntrustedCertificate;
            await client.DisposeAsync().ConfigureAwait(false);
            if (untrusted is not null)
            {
                throw new CliException(
                    $"The server certificate is not trusted: {untrusted.Subject} (thumbprint {untrusted.Thumbprint}). " +
                    "Trust it once in the app (it shares the certificate store), or use --trust-all on lab networks.");
            }

            throw;
        }
    }

    /// <summary>
    /// MQTT topics (and Sparkplug births) arrive after connecting: wait until no new ones appeared for a moment, so a
    /// browse or path lookup sees the broker's tree, not an empty one.
    /// </summary>
    private static async Task SettleAsync(IDynamicAddressSpace dynamic, CancellationToken cancellationToken)
    {
        var lastChange = DateTime.UtcNow;
        void OnChanged(object? sender, EventArgs e) => lastChange = DateTime.UtcNow;
        dynamic.AddressSpaceChanged += OnChanged;
        try
        {
            var deadline = DateTime.UtcNow + SettleMax;
            while (DateTime.UtcNow < deadline && DateTime.UtcNow - lastChange < SettleQuiet)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            dynamic.AddressSpaceChanged -= OnChanged;
        }
    }

    /// <summary>
    /// A node from the command line: <c>/Objects/Line1/Speed</c> is a path of display names from the root, anything
    /// else an id as the app shows or saves it (<c>nsu=…;s=…</c>, <c>ns=3;s=StepUp</c>, a Logix tag, an MQTT id).
    /// </summary>
    public static async Task<Node> ResolveAsync(IDeviceClient client, string text, CancellationToken cancellationToken)
    {
        if (!text.StartsWith('/'))
        {
            NodeId id;
            try
            {
                id = client.ParsePortableId(text);
            }
            catch (Exception ex) when (ex is ServiceResultException or FormatException or ArgumentException)
            {
                throw new CliException($"'{text}' is not a node id. Use an id like ns=3;s=Name, or a path like /Objects/Name.");
            }

            return new Node(id, text, client.ToDisplayId(id));
        }

        var item = client.Root;
        foreach (var name in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            item = await FindChildAsync(client, item, name, cancellationToken).ConfigureAwait(false)
                ?? throw new CliException($"'{name}' not found under '{item.DisplayName}' (path {text}).");
        }

        return new Node(item.NodeId, item.DisplayName, client.ToDisplayId(item.NodeId));
    }

    private static async Task<BrowseItem?> FindChildAsync(IDeviceClient client, BrowseItem parent, string name, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + (client is IDynamicAddressSpace ? DynamicLookupTimeout : TimeSpan.Zero);
        while (true)
        {
            var children = await client.BrowseAsync(parent.NodeId, cancellationToken).ConfigureAwait(false);
            var match = children.FirstOrDefault(c => c.DisplayName == name) ?? children.FirstOrDefault(c => c.BrowseName == name);
            if (match is not null || DateTime.UtcNow >= deadline)
            {
                return match;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }
}
