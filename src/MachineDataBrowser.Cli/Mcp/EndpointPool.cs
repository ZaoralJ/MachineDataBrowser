using System.Text.RegularExpressions;
using MachineDataBrowser.Core;
using ModelContextProtocol;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// The endpoints the MCP server may use and their connections, opened on first use and kept for later tool calls (a
/// connection that dropped for good is opened again on the next call). Configured endpoints (<c>--endpoint</c>,
/// <c>--session</c>) carry their login and trust options; endpoints the agent names that only match an
/// <c>--allow</c> pattern (or <c>--allow-any</c>) connect without any credentials or automatic certificate trust.
/// </summary>
internal sealed class EndpointPool(IReadOnlyList<ConnectionArgs> endpoints, IReadOnlyList<string>? patterns = null, bool allowAny = false) : IAsyncDisposable
{
    private readonly Regex[] _patterns = [.. (patterns ?? []).Select(Glob)];
    private readonly Dictionary<string, IDeviceClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConnectionArgs> _named = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Configured endpoints, then those the agent named under a pattern (in the order first used).</summary>
    public IReadOnlyList<ConnectionArgs> Endpoints
    {
        get
        {
            lock (_named)
            {
                return [.. endpoints, .. _named.Values];
            }
        }
    }

    public IReadOnlyList<string> Patterns => allowAny ? ["*"] : [.. (patterns ?? [])];

    /// <summary>Whether any machine can be reached: configured endpoints or patterns for named ones.</summary>
    public bool HasMachines => endpoints.Count > 0 || _patterns.Length > 0 || allowAny;

    public ConnectionState StateOf(ConnectionArgs endpoint) =>
        _clients.TryGetValue(endpoint.Url, out var client) ? client.State : ConnectionState.Disconnected;

    /// <summary>A configured endpoint (may be omitted when only one is configured), or one allowed by a pattern.</summary>
    public ConnectionArgs Find(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return endpoints.Count == 1 && _patterns.Length == 0 && !allowAny
                ? endpoints[0]
                : throw new McpException(endpoints.Count == 0
                    ? "Name the endpoint to use (e.g. opc.tcp://host:4840); it must match the server's allowed patterns."
                    : $"Pass the endpoint: one of {string.Join(", ", endpoints.Select(e => e.Url))}{(_patterns.Length > 0 || allowAny ? ", or one matching the allowed patterns" : string.Empty)}.");
        }

        if (endpoints.FirstOrDefault(e => Normalize(e.Url) == Normalize(url)) is { } configured)
        {
            return configured;
        }

        if (!allowAny && !_patterns.Any(p => p.IsMatch(url.Trim())))
        {
            var configuredText = endpoints.Count > 0 ? $"one of the configured endpoints ({string.Join(", ", endpoints.Select(e => e.Url))})" : null;
            var allowedPatterns = _patterns.Length > 0 ? $"matching the allowed patterns ({string.Join(", ", patterns!)})" : null;
            throw new McpException($"'{url}' is not {string.Join(" or ", new[] { configuredText, allowedPatterns }.OfType<string>())}.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("opc.tcp" or "eip" or "mqtt" or "mqtts" or "ws" or "wss"))
        {
            throw new McpException($"'{url}' is not an endpoint URL (opc.tcp://, eip://, mqtt://, mqtts://, ws://, wss://).");
        }

        lock (_named)
        {
            // Named by the agent: no login, no automatic trust; certificates the app trusts still apply.
            var key = Normalize(url);
            if (!_named.TryGetValue(key, out var named))
            {
                named = new ConnectionArgs(url.Trim(), null, null, false, false, ReadOnly: true, Named: true);
                _named[key] = named;
            }

            return named;
        }
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

    /// <summary><c>*</c> any characters, <c>?</c> one; the whole URL must match, case-insensitively.</summary>
    private static Regex Glob(string pattern) => new(
        "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
