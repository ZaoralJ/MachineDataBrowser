using System.Text.Json;

namespace MachineDataBrowser.Core;

/// <summary>Formats JSON text for display without changing the value kept by a device client.</summary>
public static class JsonValueFormatting
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>Returns indented JSON when <paramref name="value"/> is valid JSON; otherwise returns false.</summary>
    public static bool TryFormat(string? value, out string formatted)
    {
        formatted = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            formatted = JsonSerializer.Serialize(document.RootElement, IndentedOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
