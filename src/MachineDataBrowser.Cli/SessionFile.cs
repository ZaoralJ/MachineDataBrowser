using System.Text.Json;
using System.Text.Json.Serialization;

namespace MachineDataBrowser.Cli;

/// <summary>
/// The parts of an app session file (<c>.mdbsession</c>, <c>.opcsession</c>) the CLI uses. The app owns the format;
/// unknown fields are ignored, so newer files still load.
/// </summary>
internal sealed record SessionFile
{
    public required string EndpointUrl { get; init; }

    public bool UseSecurity { get; init; }

    public bool AutoAcceptCertificates { get; init; }

    /// <summary>The session never writes or calls methods (Connection ▸ Read-Only in the app).</summary>
    public bool ReadOnly { get; init; }

    public string? UserName { get; init; }

    public int? DefaultRefreshMs { get; init; }

    public IReadOnlyList<SessionWatchEntry>? Watch { get; init; }

    public static async Task<SessionFile> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"Session file '{path}' not found.");
        }

        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync(stream, SessionJsonContext.Default.SessionFile, cancellationToken).ConfigureAwait(false)
                ?? throw new CliException($"'{path}' is not a session file.");
        }
        catch (JsonException ex)
        {
            throw new CliException($"'{path}' is not a session file: {ex.Message}");
        }
    }
}

internal sealed record SessionWatchEntry(string NodeId, string DisplayName, int? RefreshMs = null, string? Path = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SessionFile))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
