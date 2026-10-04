using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Cli.Tui;

/// <summary>
/// The look of the full-screen browser: a soft dark palette (One Dark), muted borders with the focused pane in blue,
/// a quiet selection bar, and colour only where it means something (status, node class, the trend).
/// </summary>
internal static class Theme
{
    // The palette of the current theme; Apply sets it. The defaults are the app's default theme, dark.
    public static Color Background { get; private set; }

    public static Color Surface { get; private set; }

    public static Color Selection { get; private set; }

    public static Color Border { get; private set; }

    public static Color Dim { get; private set; }

    public static Color Muted { get; private set; }

    public static Color Foreground { get; private set; }

    public static Color Bright { get; private set; }

    /// <summary>The theme's accent: focused pane, keys, selection tint.</summary>
    public static Color Blue { get; private set; }

    public static Color Green { get; private set; }

    public static Color Yellow { get; private set; }

    public static Color Orange { get; private set; }

    public static Color Red { get; private set; }

    public static Color Magenta { get; private set; }

    /// <summary>The theme shown and whether its light appearance is used.</summary>
    public static ColorTheme Current { get; private set; } = ColorThemeCatalog.Find(null);

    public static bool Light { get; private set; }

    static Theme() => Apply(Current, light: false);

    /// <summary>
    /// Takes a colour theme of the app: its window, surface, border, text and accent colours; status colours stay
    /// green / amber / red, darker on a light background so they stay readable.
    /// </summary>
    /// <summary>
    /// Status and chart colours per theme, (green, yellow, orange, red, magenta) for dark and light: the palette's own where
    /// it has them (Nord's aurora, Dracula, Solarized), tones that sit with the theme otherwise. Light ones are darker so
    /// they read on a light background.
    /// </summary>
    private static readonly Dictionary<string, (string[] Dark, string[] Light)> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Indigo"] = (["#7EE787", "#E3B341", "#FFA657", "#FF7B72", "#D2A8FF"], ["#1A7F37", "#9A6700", "#BC4C00", "#CF222E", "#8250DF"]),
        ["Graphite"] = (["#86EFAC", "#FCD34D", "#FDBA74", "#F87171", "#C4B5FD"], ["#15803D", "#A16207", "#C2410C", "#DC2626", "#7C3AED"]),
        ["Ocean"] = (["#9AE6B4", "#F6E05E", "#FBB97C", "#FC8181", "#B794F4"], ["#2F855A", "#975A16", "#C05621", "#C53030", "#6B46C1"]),
        ["Forest"] = (["#BEF264", "#FDE68A", "#FDBA74", "#FCA5A5", "#F0ABFC"], ["#3F6212", "#854D0E", "#9A3412", "#B91C1C", "#86198F"]),
        ["Amber"] = (["#B5CC7A", "#F2C14E", "#F28C38", "#F87171", "#E8A0BF"], ["#4D7C0F", "#A16207", "#C2410C", "#B91C1C", "#9D174D"]),
        ["Nord"] = (["#A3BE8C", "#EBCB8B", "#D08770", "#BF616A", "#B48EAD"], ["#5E8A47", "#A97F23", "#B9603C", "#B4434F", "#8B5E89"]),
        ["Solarized"] = (["#859900", "#B58900", "#CB4B16", "#DC322F", "#D33682"], ["#859900", "#B58900", "#CB4B16", "#DC322F", "#D33682"]),
        ["Dracula"] = (["#50FA7B", "#F1FA8C", "#FFB86C", "#FF5555", "#FF79C6"], ["#1F9D47", "#8A7A00", "#C66A1C", "#D93D63", "#B0368A"]),
    };

    /// <summary>The header and key bar: a band a step off the background.</summary>
    public static Color Band { get; private set; }

    /// <summary>Pane borders without the focus: quiet, so the focused pane stands out.</summary>
    public static Color QuietBorder { get; private set; }

    /// <summary>
    /// Takes a colour theme of the app: its window, surface, border, text and accent colours, plus status and chart
    /// colours that belong to the theme.
    /// </summary>
    public static void Apply(ColorTheme theme, bool light)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Current = theme;
        Light = light;
        var c = light ? theme.Light : theme.Dark;
        Background = new Color(c.Window);
        Surface = new Color(c.Surface);
        Band = light ? new Color(c.Chip) : new Color(c.SurfaceAlt);
        Border = new Color(c.BorderStrong);
        // Title and line share one colour in Terminal.Gui: soft for the line, still readable as a title.
        QuietBorder = Mix(new Color(c.BorderStrong), new Color(c.Muted), light ? 0.75 : 0.55);
        Dim = new Color(c.MutedLow);
        Muted = new Color(c.Muted);
        Foreground = new Color(c.TextMid);
        Bright = new Color(c.Text);
        Blue = new Color(c.Accent);
        Selection = Mix(Surface, Blue, light ? 0.16 : 0.26);
        var accents = Accents.TryGetValue(theme.Name, out var set) ? (light ? set.Light : set.Dark) : (light ? Accents["Indigo"].Light : Accents["Indigo"].Dark);
        Green = new Color(accents[0]);
        Yellow = new Color(accents[1]);
        Orange = new Color(accents[2]);
        Red = new Color(accents[3]);
        Magenta = new Color(accents[4]);
        Base = new Scheme(Text(Foreground))
        {
            Focus = new Attribute(Bright, Selection, TextStyle.Bold),
            Active = Text(Bright),
            HotNormal = Text(Blue, TextStyle.Bold),
            HotFocus = new Attribute(Blue, Selection, TextStyle.Bold),
            HotActive = Text(Blue, TextStyle.Bold),
            Editable = new Attribute(Bright, Surface),
            Disabled = Text(Dim),
        };
        Quiet = new Scheme(Base) { Active = Text(Foreground) };
    }

    private static Color Mix(Color a, Color b, double amount) => new(
        (int)Math.Round(a.R + ((b.R - a.R) * amount)),
        (int)Math.Round(a.G + ((b.G - a.G) * amount)),
        (int)Math.Round(a.B + ((b.B - a.B) * amount)));

    public static Attribute Text(Color foreground, TextStyle style = TextStyle.None) => new(foreground, Background, style);

    /// <summary>Text on the background; the focused selection is an accent-tinted bar, an unfocused one only brighter text.</summary>
    public static Scheme Base { get; private set; } = null!;

    /// <summary>A pane's border and title: blue when it has the focus, muted otherwise.</summary>
    public static Scheme Frame(bool focused) => new(Base)
    {
        Normal = focused ? Text(Blue, TextStyle.Bold) : Text(QuietBorder),
        Focus = focused ? Text(Blue, TextStyle.Bold) : Text(Muted),
        Active = focused ? Text(Blue, TextStyle.Bold) : Text(Muted),
    };

    /// <summary>Same selection, another text colour (tree nodes, status cells, columns).</summary>
    public static Scheme Colored(Color foreground, TextStyle style = TextStyle.None) => new(Base) { Normal = Text(foreground, style) };

    /// <summary>A list that shows its selection only when focused (the info log).</summary>
    public static Scheme Quiet { get; private set; } = null!;

    /// <summary>
    /// Titles show node names, which often contain '_' (Bulk_0014, Motor_1): Terminal.Gui would take it for a hotkey
    /// marker, drop it and underline the next letter. No character is a marker in titles.
    /// </summary>
    public static void PlainTitle(View view) => view.HotKeySpecifier = new System.Text.Rune(0xFFFF);

    public static void Pane(FrameView frame, View content, Scheme? scheme = null)
    {
        PlainTitle(frame);
        frame.BorderStyle = LineStyle.Rounded;
        frame.SetScheme(Frame(focused: false));
        content.SetScheme(scheme ?? Base);
        content.HasFocusChanged += (_, e) => frame.SetScheme(Frame(e.NewValue));
    }

    public static void Dialog(Dialog dialog)
    {
        PlainTitle(dialog);
        dialog.BorderStyle = LineStyle.Rounded;
        dialog.ShadowStyle = ShadowStyles.None;
        dialog.SetScheme(new Scheme(Base) { Normal = new Attribute(Foreground, Surface) });
        foreach (var button in dialog.Buttons)
        {
            Button(button);
        }
    }

    /// <summary>A flat button: the word with its hotkey letter, muted; filled with the accent when focused.</summary>
    public static void Button(Button button)
    {
        button.NoDecorations = true;
        button.ShadowStyle = ShadowStyles.None;
        button.SetScheme(new Scheme(Base)
        {
            Normal = new Attribute(Foreground, Surface),
            HotNormal = new Attribute(Blue, Surface, TextStyle.Bold | TextStyle.Underline),
            Focus = new Attribute(Background, Blue, TextStyle.Bold),
            HotFocus = new Attribute(Background, Blue, TextStyle.Bold | TextStyle.Underline),
            Active = new Attribute(Foreground, Surface),
            HotActive = new Attribute(Blue, Surface, TextStyle.Bold | TextStyle.Underline),
        });
    }

    /// <summary>A table without box lines: a quiet bold header and the rows.</summary>
    public static void Table(TableView table)
    {
        table.Style.ShowVerticalCellLines = false;
        table.Style.ShowVerticalHeaderLines = false;
        table.Style.ShowHorizontalHeaderOverline = false;
        table.Style.ShowHorizontalHeaderUnderline = false;
        table.Style.ShowHorizontalBottomLine = false;
        table.Style.ExpandLastColumn = true;

        // The header never takes selection colours.
        var header = Text(Muted, TextStyle.Bold);
        table.Style.HeaderScheme = new Scheme(header) { Focus = header, Active = header, HotNormal = header, HotFocus = header, HotActive = header };
    }

    /// <summary>Good green, Uncertain yellow, Bad red.</summary>
    public static Color Status(Opc.Ua.StatusCode code) =>
        Opc.Ua.StatusCode.IsGood(code) ? Green : Opc.Ua.StatusCode.IsUncertain(code) ? Yellow : Red;

    /// <summary>The trend's colour by height: green at the bottom, yellow, orange, red at the top.</summary>
    public static Color Gradient(double fromTop) => fromTop switch
    {
        < 0.25 => Red,
        < 0.5 => Orange,
        < 0.75 => Yellow,
        _ => Green,
    };
}
