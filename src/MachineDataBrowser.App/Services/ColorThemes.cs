using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.Services;

/// <summary>
/// The colour themes and how they are applied: the app's own brushes (AppStyles.axaml) and the Fluent palette are
/// recoloured in place, so everything bound to them, including Dock and DataGrid, follows without a restart.
/// </summary>
public static class ColorThemes
{
    public const string DefaultName = ColorThemeCatalog.DefaultName;

    public static IReadOnlyList<ColorTheme> All => ColorThemeCatalog.All;

    public static ColorTheme Find(string? name) => ColorThemeCatalog.Find(name);

    /// <summary>Recolours the running app; safe to call repeatedly (e.g. while previewing in Settings).</summary>
    public static void Apply(Application app, ColorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(theme);
        ApplyBrushes(app, ThemeVariant.Light, theme.Light, dark: false);
        ApplyBrushes(app, ThemeVariant.Dark, theme.Dark, dark: true);
        ReplaceFluentTheme(app, theme);
    }

    private static ColorTheme? _fluentTheme;

    /// <summary>
    /// Swaps in a new FluentTheme built with the theme's palettes. Fluent turns its palettes into brushes once and keeps
    /// them, so neither changing nor replacing the palettes reaches dialogs and controls; a new theme instance does
    /// (controls resolve its brushes through dynamic resources).
    /// </summary>
    private static void ReplaceFluentTheme(Application app, ColorTheme theme)
    {
        var index = app.Styles.IndexOf(app.Styles.OfType<FluentTheme>().FirstOrDefault()!);
        // App.axaml's palettes are the default theme's: nothing to swap until another theme is chosen.
        _fluentTheme ??= All[0];
        if (index < 0 || ReferenceEquals(_fluentTheme, theme))
        {
            return;
        }

        var old = (FluentTheme)app.Styles[index];
        var fluent = new FluentTheme { DensityStyle = old.DensityStyle };
        fluent.Palettes[ThemeVariant.Light] = Palette(theme.Light, dark: false);
        fluent.Palettes[ThemeVariant.Dark] = Palette(theme.Dark, dark: true);
        app.Styles[index] = fluent;
        _fluentTheme = theme;
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

    private static ColorPaletteResources Palette(ThemeColors c, bool dark)
    {
        var palette = new ColorPaletteResources();
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
        return palette;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * amount)),
        (byte)Math.Round(a.G + ((b.G - a.G) * amount)),
        (byte)Math.Round(a.B + ((b.B - a.B) * amount)));
}
