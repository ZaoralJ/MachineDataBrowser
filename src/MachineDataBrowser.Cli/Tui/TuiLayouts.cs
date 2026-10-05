using System.Text.Json;
using System.Text.Json.Serialization;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Cli.Tui;

/// <summary>Pane sizes dragged with the mouse; null is the default share.</summary>
internal sealed record PaneLayout(int? TreeWidth = null, int? AttributesHeight = null, int? ChartHeight = null, int? InfoHeight = null)
{
    public bool IsDefault => this == new PaneLayout();
}

/// <summary>
/// The TUI's pane sizes per endpoint URL, in <c>tui-layouts.json</c> in the data folder: a layout belongs to the
/// machine, not to a session file. Best effort: a missing or broken file is the default layout.
/// </summary>
internal sealed class TuiLayouts(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(ClientPaths.DataRoot, "tui-layouts.json");

    public PaneLayout Get(string url) =>
        Load().TryGetValue(Key(url), out var layout) ? layout : new PaneLayout();

    public void Set(string url, PaneLayout layout)
    {
        var all = Load();
        if (layout.IsDefault)
        {
            all.Remove(Key(url));
        }
        else
        {
            all[Key(url)] = layout;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, TuiLayoutsJsonContext.Default.DictionaryStringPaneLayout));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Saving the TUI layout to {_path} failed: {ex.Message}");
        }
    }

    // The same endpoint typed with another case or a trailing slash is the same machine.
    private static string Key(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    private Dictionary<string, PaneLayout> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), TuiLayoutsJsonContext.Default.DictionaryStringPaneLayout) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, PaneLayout>))]
internal sealed partial class TuiLayoutsJsonContext : JsonSerializerContext;
