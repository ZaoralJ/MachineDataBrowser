using System.Text.Json;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Cli;

/// <summary>Reads the app's appearance from its settings file, so the full-screen browser looks like the app.</summary>
internal static class AppAppearance
{
    /// <summary>The colour theme chosen in the app and whether it uses the light appearance; the default theme, dark, otherwise.</summary>
    public static (ColorTheme Theme, bool Light) Load(string? settingsPath = null)
    {
        var path = settingsPath ?? Path.Combine(ClientPaths.DataRoot, "settings.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject settings)
            {
                var theme = ColorThemeCatalog.Find((string?)settings["colorTheme"]);

                // "System" can't be known in a terminal; dark suits most of them.
                var light = string.Equals((string?)settings["theme"], "Light", StringComparison.OrdinalIgnoreCase);
                return (theme, light);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // An unreadable settings file is not worth failing the browser for.
        }

        return (ColorThemeCatalog.Find(null), false);
    }
}
