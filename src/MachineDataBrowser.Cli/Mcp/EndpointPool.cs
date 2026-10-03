using MachineDataBrowser.Core;
using ModelContextProtocol;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// The endpoints the MCP server may use (the allowlist given on the command line) and their connections, opened on
/// first use and kept for later tool calls. A connection that dropped for good is opened again on the next call.
/// </summary>
internal sealed class EndpointPool(IReadOnlyList<ConnectionArgs> endpoints) : IAsyncDisposable
{
    private readonly Dictionary<string, IDeviceClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<ConnectionArgs> Endpoints => endpoints;

    public ConnectionState StateOf(ConnectionArgs endpoint) =>
        _clients.TryGetValue(endpoint.Url, out var client) ? client.State : ConnectionState.Disconnected;

    /// <summary>The configured endpoint <paramref name="url"/>; may be omitted when only one is configured.</summary>
    public ConnectionArgs Find(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return endpoints.Count == 1
                ? endpoints[0]
                : throw new McpException($"Several endpoints are configured; pass one of: {string.Join(", ", endpoints.Select(e => e.Url))}.");
        }

        return endpoints.FirstOrDefault(e => Normalize(e.Url) == Normalize(url))
            ?? throw new McpException($"'{url}' is not one of the configured endpoints: {string.Join(", ", endpoints.Select(e => e.Url))}.");
    }

    public async Task<IDeviceClient> ConnectAsync(string? url, CancellationToken cancellationToken)
    {
        var endpoint = Find(url);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(endpoint.Url, out var client))
            {
                if (client.State != ConnectionState.Disconnected)
                {
                    return client;
                }

                _clients.Remove(endpoint.Url);
                await client.DisposeAsync().ConfigureAwait(false);
            }

            client = await Connection.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            _clients[endpoint.Url] = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();
        _gate.Dispose();
    }

    private static string Normalize(string url) => url.Trim().TrimEnd('/').ToUpperInvariant();
}
