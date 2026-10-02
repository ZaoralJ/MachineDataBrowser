using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace OpcUaBrowser.App.Services;

/// <summary>The colours of one appearance (light or dark) of a <see cref="ColorTheme"/>.</summary>
public sealed record ThemeColors(
    string Accent,
    string Window,
    string Surface,
    string SurfaceAlt,
    string Input,
    string Border,
    string BorderStrong,
    string Text,
    string TextMid,
    string Muted,
    string MutedLow,
    string Chip,
    string Error,
    string? Region = null,
    string? AccentText = null,
    string? FluentAccent = null);

/// <summary>A named colour theme with a light and a dark appearance; light/dark/system still picks between them.</summary>
public sealed record ColorTheme(string Name, string Description, ThemeColors Light, ThemeColors Dark)
{
    public override string ToString() => Name;
}

/// <summary>
/// The colour themes and how they are applied: the app's own brushes (AppStyles.axaml) and the Fluent palette are
/// recoloured in place, so everything bound to them, including Dock and DataGrid, follows without a restart.
/// </summary>
public static class ColorThemes
{
    public const string DefaultName = "Indigo";

    public static IReadOnlyList<ColorTheme> All { get; } =
    [
        new("Indigo", "Slate neutrals with an indigo accent (the original look)",
            new("#5B5BD6", "#F4F5F8", "#FFFFFF", "#F7F8FA", "#FFFFFF", "#E3E5EB", "#D3D7DF", "#1B1D24", "#3A3F4B", "#6B7180", "#9097A6", "#EEF0F4", "#D93D42",
                AccentText: "#3E3EB8"),
            new("#8A87FF", "#0B0C10", "#14161C", "#191C23", "#1B1E25", "#242832", "#323744", "#ECEDF1", "#C5C8D2", "#8D93A3", "#666C7B", "#1D2029", "#F2555A",
                Region: "#0F1116", AccentText: "#C9C8FF", FluentAccent: "#6E6AF0")),
        new("Graphite", "Neutral greys with a blue accent",
            new("#2563EB", "#F3F4F6", "#FFFFFF", "#F9FAFB", "#FFFFFF", "#E5E7EB", "#D1D5DB", "#111827", "#374151", "#6B7280", "#9CA3AF", "#EEF0F2", "#DC2626",
                AccentText: "#1D4ED8"),
            new("#60A5FA", "#0A0A0B", "#141416", "#19191C", "#1C1C1F", "#27272A", "#3F3F46", "#F4F4F5", "#D4D4D8", "#A1A1AA", "#71717A", "#202023", "#F87171",
                AccentText: "#BFDBFE", FluentAccent: "#3B82F6")),
        new("Ocean", "Cool blue-grey with a teal accent",
            new("#0F8B8D", "#EEF4F6", "#FFFFFF", "#F5F9FA", "#FFFFFF", "#D8E4E8", "#C3D3D9", "#10232B", "#2E4752", "#5A7480", "#8AA1AB", "#E4EEF1", "#D64545",
                AccentText: "#0B6E70"),
            new("#2DD4BF", "#071317", "#0D1C22", "#122329", "#13252C", "#1C3139", "#29444E", "#E3F1F4", "#B9D2D9", "#7F9DA7", "#5B7882", "#15272E", "#F87171",
                AccentText: "#99F6E4", FluentAccent: "#14B8A6")),
        new("Forest", "Soft green-grey with a green accent",
            new("#2F855A", "#F1F5F2", "#FFFFFF", "#F7FAF8", "#FFFFFF", "#DCE5DF", "#C6D3CA", "#14211A", "#33473B", "#5F7367", "#8EA196", "#E7EFEA", "#C53030",
                AccentText: "#22663F"),
            new("#4ADE80", "#0A110D", "#111A14", "#16201A", "#18231C", "#223027", "#304236", "#E6F0E9", "#BFD3C5", "#86A08F", "#62796A", "#18241D", "#F87171",
                AccentText: "#BBF7D0", FluentAccent: "#22C55E")),
        new("Amber", "Warm sand with an amber accent",
            new("#B45309", "#F7F3EC", "#FFFDF9", "#FAF7F1", "#FFFDF9", "#E8DFD1", "#D8CBB7", "#231A0F", "#4A3C2A", "#7A6A55", "#A6967F", "#F0E9DD", "#C2410C",
                AccentText: "#92400E"),
            new("#FBBF24", "#100D08", "#19150E", "#1F1A12", "#221C13", "#2F271B", "#433827", "#F4EDE1", "#D8CBB6", "#A3937C", "#7A6C58", "#221D14", "#F97316",
                AccentText: "#FDE68A", FluentAccent: "#F59E0B")),
        new("Nord", "The Nord palette: snow storm and polar night with frost blue",
            new("#5E81AC", "#ECEFF4", "#FFFFFF", "#F4F6F9", "#FFFFFF", "#D8DEE9", "#C5CDDB", "#2E3440", "#3B4252", "#4C566A", "#7B879D", "#E5E9F0", "#BF616A",
                AccentText: "#4C6A92"),
            new("#88C0D0", "#242933", "#2E3440", "#323845", "#3B4252", "#3B4252", "#4C566A", "#ECEFF4", "#D8DEE9", "#A3ACBC", "#7B879D", "#353C4A", "#BF616A",
                AccentText: "#B6DCE6", FluentAccent: "#81A1C1")),
    ];

    public static ColorTheme Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>Recolours the running app; safe to call repeatedly (e.g. while previewing in Settings).</summary>
    public static void Apply(Application app, ColorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(theme);
        ApplyBrushes(app, ThemeVariant.Light, theme.Light, dark: false);
        ApplyBrushes(app, ThemeVariant.Dark, theme.Dark, dark: true);
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            ApplyPalette(fluent, ThemeVariant.Light, theme.Light, dark: false);
            ApplyPalette(fluent, ThemeVariant.Dark, theme.Dark, dark: true);
        }
    }

    private static void ApplyBrushes(Application app, ThemeVariant variant, ThemeColors c, bool dark)
    {
        var accent = Color.Parse(c.Accent);
        Set("AppWindowBrush", Color.Parse(c.Window));
        Set("AppSurfaceBrush", Color.Parse(c.Surface));
        Set("AppSurfaceAltBrush", Color.Parse(c.SurfaceAlt));
        Set("AppInputBrush", Color.Parse(c.Input));
        Set("AppBorderBrush", Color.Parse(c.Border));
        Set("AppBorderStrongBrush", Color.Parse(c.BorderStrong));
        Set("AppMutedTextBrush", Color.Parse(c.Muted));
        Set("AppChipBrush", Color.Parse(c.Chip));
        Set("AppAccentBrush", accent);
        Set("AppSelectionTextBrush", Color.Parse(c.AccentText ?? c.Accent));
        Set("AppHoverBrush", dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : WithAlpha(Color.Parse(c.Text), 0x0A));
        Set("AppSelectionBrush", WithAlpha(accent, dark ? (byte)0x33 : (byte)0x1F));
        Set("AppRowSelectionBrush", WithAlpha(accent, dark ? (byte)0x1A : (byte)0x10));

        // The brushes are shared instances (DynamicResource and the Dock aliases hold them): change their colour.
        void Set(string key, Color color)
        {
            if (app.TryGetResource(key, variant, out var value) && value is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }
    }

    private static void ApplyPalette(FluentTheme fluent, ThemeVariant variant, ThemeColors c, bool dark)
    {
        if (!fluent.Palettes.TryGetValue(variant, out var palette))
        {
            return;
        }

        var window = Color.Parse(c.Window);
        var region = Color.Parse(c.Region ?? c.Window);
        var surface = Color.Parse(c.Surface);
        var text = Color.Parse(c.Text);
        var accent = Color.Parse(c.FluentAccent ?? c.Accent);
        palette.Accent = accent;
        palette.RegionColor = region;
        palette.AltHigh = dark ? region : surface;
        palette.AltMediumHigh = surface;
        palette.AltMedium = surface;
        palette.AltLow = surface;
        palette.BaseHigh = text;
        palette.BaseMediumHigh = Color.Parse(c.TextMid);
        palette.BaseMedium = Color.Parse(c.Muted);
        palette.BaseMediumLow = Color.Parse(c.MutedLow);
        palette.BaseLow = Color.Parse(c.Border);
        palette.ChromeLow = dark ? region : window;
        palette.ChromeMediumLow = surface;
        palette.ChromeMedium = Color.Parse(dark ? c.Input : c.Chip);
        palette.ChromeHigh = Color.Parse(c.BorderStrong);
        palette.ChromeAltLow = text;
        palette.ChromeDisabledLow = Color.Parse(c.MutedLow);
        palette.ChromeDisabledHigh = Color.Parse(c.Border);
        palette.ListLow = Mix(surface, text, dark ? 0.06 : 0.04);
        palette.ListMedium = Mix(surface, accent, dark ? 0.18 : 0.10);
        palette.ErrorText = Color.Parse(c.Error);
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * amount)),
        (byte)Math.Round(a.G + ((b.G - a.G) * amount)),
        (byte)Math.Round(a.B + ((b.B - a.B) * amount)));
}
