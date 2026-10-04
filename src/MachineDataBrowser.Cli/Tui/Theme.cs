using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace MachineDataBrowser.Cli.Tui;

/// <summary>
/// A dark theme in the spirit of btop: black background, grey text, rounded pane borders that light up when the pane
/// has the focus, tables without inner grid lines, and colour only where it means something.
/// </summary>
internal static class Theme
{
    public static readonly Color Background = new(ColorName16.Black);

    public static Attribute Text(ColorName16 foreground, TextStyle style = TextStyle.None) => new(new Color(foreground), Background, style);

    /// <summary>Grey text; selection cyan when focused, dark grey otherwise.</summary>
    public static Scheme Base { get; } = new(Text(ColorName16.Gray))
    {
        Focus = new Attribute(new Color(ColorName16.Black), new Color(ColorName16.BrightCyan)),
        // Selection in a pane without the focus: just brighter text, so only the focused pane shows a bar.
        Active = Text(ColorName16.BrightYellow, TextStyle.Bold),
        HotNormal = Text(ColorName16.BrightCyan, TextStyle.Bold),
        HotFocus = new Attribute(new Color(ColorName16.Black), new Color(ColorName16.BrightCyan), TextStyle.Bold),
        HotActive = Text(ColorName16.BrightCyan, TextStyle.Bold),
        Editable = Text(ColorName16.White),
    };

    /// <summary>The scheme of a pane frame: its border and title; the content keeps <see cref="Base"/>.</summary>
    public static Scheme Frame(bool focused) => new(Base)
    {
        Normal = Text(focused ? ColorName16.BrightCyan : ColorName16.Gray, focused ? TextStyle.Bold : TextStyle.None),
        Focus = Text(focused ? ColorName16.BrightCyan : ColorName16.Gray, TextStyle.Bold),
        Active = Text(focused ? ColorName16.BrightCyan : ColorName16.Gray),
    };

    /// <summary>Same selection colours, another text colour (tree nodes, status cells, columns).</summary>
    public static Scheme Colored(ColorName16 foreground, TextStyle style = TextStyle.None) => new(Base) { Normal = Text(foreground, style) };

    /// <summary>A list that shows its selection only when focused (the info log).</summary>
    public static Scheme Quiet { get; } = new(Base) { Active = Text(ColorName16.Gray) };

    public static void Pane(FrameView frame, View content, Scheme? scheme = null)
    {
        frame.BorderStyle = LineStyle.Rounded;
        frame.SetScheme(Frame(focused: false));
        content.SetScheme(scheme ?? Base);
        content.HasFocusChanged += (_, e) => frame.SetScheme(Frame(e.NewValue));
    }

    public static void Dialog(Dialog dialog)
    {
        dialog.BorderStyle = LineStyle.Rounded;
        dialog.SetScheme(new Scheme(Base) { Normal = Text(ColorName16.White) });
    }

    /// <summary>A table without box lines: a cyan header and the rows.</summary>
    public static void Table(TableView table)
    {
        table.Style.ShowVerticalCellLines = false;
        table.Style.ShowVerticalHeaderLines = false;
        table.Style.ShowHorizontalHeaderOverline = false;
        table.Style.ShowHorizontalHeaderUnderline = false;
        table.Style.ShowHorizontalBottomLine = false;
        table.Style.ExpandLastColumn = true;
        // The header never takes selection colours.
        var header = Text(ColorName16.BrightCyan, TextStyle.Bold);
        table.Style.HeaderScheme = new Scheme(header) { Focus = header, Active = header, HotNormal = header, HotFocus = header, HotActive = header };
    }
}
