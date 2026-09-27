using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpcUaBrowser.App.Services;

public sealed record SessionDocument
{
    public const string FileExtension = "opcsession";

    public int Version { get; init; } = 1;

    public required string EndpointUrl { get; init; }

    public bool UseSecurity { get; init; }

    public bool AutoAcceptCertificates { get; init; }

    public string? UserName { get; init; }

    public int? DefaultRefreshMs { get; init; }

    public IReadOnlyList<WatchEntry> Watch { get; init; } = [];

    public IReadOnlyList<ColumnState>? WatchColumns { get; init; }

    public string? WatchSortColumn { get; init; }

    public bool WatchSortDescending { get; init; }

    public static async Task<SessionDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.SessionDocument, cancellationToken)
            ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a valid session file.");
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, this, AppJsonContext.Default.SessionDocument, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }
}

public sealed record WatchSnapshot(
    string Name,
    string NodeId,
    System.Text.Json.Nodes.JsonNode? Value,
    string DataType,
    string Status,
    int RefreshMs,
    DateTimeOffset? LastUpdate,
    string? SourceTime);

public sealed record ColumnState(string Header, bool Visible, double? Width, int Order);

public sealed record WatchEntry(string NodeId, string DisplayName, int? RefreshMs = null);

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record AppSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public int SamplingIntervalMs { get; init; } = 250;

    public int MaxRecursiveItems { get; init; } = 500;

    public bool ReopenLastSession { get; init; }

    public double UiScale { get; init; } = 1.0;

    public IReadOnlyList<string> RecentSessions { get; init; } = [];

    public IReadOnlyList<string> RecentEndpoints { get; init; } = [];

    public const int MaxRecent = 10;

    public AppSettings WithRecentSession(string path) =>
        this with { RecentSessions = PushRecent(RecentSessions, path) };

    public AppSettings WithRecentEndpoint(string url) =>
        this with { RecentEndpoints = PushRecent(RecentEndpoints, url) };

    private static string[] PushRecent(IEnumerable<string> list, string value) =>
        [value, .. list.Where(v => !string.Equals(v, value, StringComparison.Ordinal)).Take(MaxRecent - 1)];
}

public sealed class SettingsStore(string? path = null)
{
    public static string DefaultPath { get; } = Path.Combine(Core.ClientPaths.DataRoot, "settings.json");

    private readonly string _path = path ?? DefaultPath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, settings, AppJsonContext.Default.AppSettings);
        }

        File.Move(temp, _path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionDocument))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(LayoutNode))]
[JsonSerializable(typeof(WatchSnapshot))]
[JsonSerializable(typeof(List<WatchSnapshot>))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
