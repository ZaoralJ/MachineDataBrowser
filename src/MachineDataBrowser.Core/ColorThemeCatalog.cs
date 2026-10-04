namespace MachineDataBrowser.Core;

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
/// The colour themes, shared by the app (which applies them to its brushes) and the full-screen command line browser
/// (which maps them onto terminal colours). Only colours here: no UI types.
/// </summary>
public static class ColorThemeCatalog
{
    public const string DefaultName = "Indigo";

    public static IReadOnlyList<ColorTheme> All { get; } =
    [
        new("Indigo", "Slate neutrals with an indigo accent (the original look)",
            new("#5B5BD6", "#F4F5F8", "#FFFFFF", "#F7F8FA", "#FFFFFF", "#E3E5EB", "#D3D7DF", "#1B1D24", "#3A3F4B", "#6B7180", "#9097A6", "#EEF0F4", "#D93D42",
                AccentText: "#3E3EB8"),
            new("#8A87FF", "#0B0C10", "#14161C", "#191C23", "#1B1E25", "#242832", "#323744", "#ECEDF1", "#C5C8D2", "#8D93A3", "#666C7B", "#1D2029", "#F2555A",
                Region: "#0F1116", AccentText: "#C9C8FF", FluentAccent: "#6E6AF0")),
        new("Graphite", "Pure neutral greys with a blue accent",
            new("#2563EB", "#E6E7EA", "#F7F7F8", "#EFEFF1", "#FFFFFF", "#DCDDE1", "#C4C6CC", "#111827", "#374151", "#6B7280", "#9CA3AF", "#E4E5E8", "#DC2626",
                AccentText: "#1D4ED8"),
            new("#60A5FA", "#0A0A0B", "#161618", "#1C1C1F", "#202023", "#2A2A2E", "#3F3F46", "#F4F4F5", "#D4D4D8", "#A1A1AA", "#71717A", "#232326", "#F87171",
                AccentText: "#BFDBFE", FluentAccent: "#3B82F6")),
        new("Ocean", "Blue-green water: sea-glass light, deep navy dark, teal accent",
            new("#0E7C86", "#D2E5EC", "#EDF6F9", "#E2EFF4", "#F8FCFD", "#B7D1DA", "#98BAC6", "#0B2530", "#284753", "#507482", "#86A3AE", "#C9DFE7", "#D64545",
                AccentText: "#0A5F67"),
            new("#2DD4BF", "#04131B", "#0A2130", "#0E2938", "#0F2D3E", "#17394C", "#23526B", "#E1F2F7", "#B5D3DD", "#7FA3B1", "#577A88", "#0E293A", "#F87171",
                Region: "#061923", AccentText: "#A7F3EA", FluentAccent: "#14B8A6")),
        new("Forest", "Moss and pine: sage light, deep green dark, green accent",
            new("#2F7D46", "#D7E5D5", "#EFF6EC", "#E3EEE0", "#F9FCF8", "#BCD2B9", "#9FBC9B", "#13261A", "#304A38", "#5A7462", "#8CA393", "#CDDFCA", "#C53030",
                AccentText: "#22603A"),
            new("#4ADE80", "#06110A", "#0D2014", "#112819", "#132D1C", "#1C3D27", "#2A5638", "#E4F2E8", "#BBD6C2", "#83A58D", "#5F7D68", "#112819", "#F87171",
                Region: "#081710", AccentText: "#BBF7D0", FluentAccent: "#22C55E")),
        new("Amber", "Paper and leather: warm sand light, dark brown dark, amber accent",
            new("#B45309", "#EBDCC2", "#FBF3E4", "#F3E8D3", "#FFFBF3", "#DAC39D", "#C8AA79", "#2A1E0E", "#4D3B22", "#7C6649", "#A89271", "#E5D3B3", "#C2410C",
                AccentText: "#8A3F07"),
            new("#FBBF24", "#120C04", "#20170B", "#281D0F", "#2C2011", "#3D2E19", "#584325", "#F6EEDF", "#DACAAE", "#A69174", "#7D6A50", "#281E10", "#F97316",
                Region: "#181008", AccentText: "#FDE68A", FluentAccent: "#F59E0B")),
        new("Nord", "The Nord palette: snow storm and polar night with frost blue",
            new("#5E81AC", "#DCE1EA", "#EEF1F6", "#E5E9F0", "#FAFBFD", "#CBD2DE", "#B1BBCC", "#2E3440", "#3B4252", "#4C566A", "#7B879D", "#D8DEE9", "#BF616A",
                AccentText: "#4C6A92"),
            new("#88C0D0", "#242933", "#2E3440", "#333A47", "#3B4252", "#3B4252", "#4C566A", "#ECEFF4", "#D8DEE9", "#A3ACBC", "#7B879D", "#353C4A", "#BF616A",
                AccentText: "#B6DCE6", FluentAccent: "#81A1C1")),
        new("Solarized", "Ethan Schoonover's Solarized: cream light, dark teal dark, blue accent",
            new("#268BD2", "#EEE8D5", "#FDF6E3", "#F5EFDC", "#FFFBF0", "#DDD6C1", "#CBC2A6", "#073642", "#3D5A63", "#657B83", "#93A1A1", "#E6DFC8", "#DC322F",
                AccentText: "#1B6FA8"),
            new("#2AA198", "#002B36", "#073642", "#0A3D4A", "#0B4250", "#164B57", "#24606D", "#EEE8D5", "#C2C9C3", "#93A1A1", "#657B83", "#0A3C48", "#DC322F",
                AccentText: "#9EE3DC", FluentAccent: "#2AA198")),
        new("Dracula", "Dracula: purple-grey dark (and a lavender light), purple accent",
            new("#7C4DDB", "#E3DEF2", "#F7F5FC", "#EDE9F8", "#FFFFFF", "#D3CCE8", "#BCB2DB", "#1F1D2E", "#3E3A56", "#6A6488", "#9993B5", "#DCD5EF", "#D93D63",
                AccentText: "#5E33B8"),
            new("#BD93F9", "#1E1F29", "#282A36", "#2E3040", "#343746", "#3B3E51", "#51547A", "#F8F8F2", "#E0DFF0", "#9EA3C8", "#6272A4", "#313342", "#FF5555",
                Region: "#21222C", AccentText: "#E2CCFF", FluentAccent: "#9F6EF0")),
    ];

    public static ColorTheme Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
