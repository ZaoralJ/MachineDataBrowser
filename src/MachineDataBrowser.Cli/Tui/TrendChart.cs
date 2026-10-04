using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace MachineDataBrowser.Cli.Tui;

/// <summary>
/// A filled braille area chart, like btop's: every cell holds 2×4 dots, so a column of the pane shows two samples and
/// a row four levels. Colours run from green at the bottom through yellow to red at the top.
/// </summary>
internal sealed class TrendChart : View
{
    // Braille dot bits by (column, row from the top) inside one character cell.
    private static readonly int[,] Dots = { { 0x01, 0x02, 0x04, 0x40 }, { 0x08, 0x10, 0x20, 0x80 } };

    private IReadOnlyList<double> _values = [];

    public TrendChart() => CanFocus = false;

    public void SetValues(IReadOnlyList<double> values)
    {
        _values = values;
        SetNeedsDraw();
    }

    /// <summary>The samples the chart shows for a given width: the newest two per column.</summary>
    public static IReadOnlyList<double> Tail(IReadOnlyList<double> values, int columns) =>
        values.Count <= columns * 2 ? values : [.. values.Skip(values.Count - columns * 2)];

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        var background = GetAttributeForRole(VisualRole.Normal).Background;
        var values = Tail(_values, width);
        if (width <= 0 || height <= 0 || values.Count < 2)
        {
            SetAttribute(new Attribute(ColorName16.DarkGray, background));
            AddStr(0, 0, values.Count == 0 ? "Select a monitored numeric item (Tab to Monitored Items)." : "Waiting for more samples…");
            return true;
        }

        var min = values.Min();
        var max = values.Max();
        var span = max - min;
        var levels = height * 4;

        // Dot height per sample: at least one dot, so a flat line still shows.
        var heights = values.Select(v => span <= 0 ? levels / 2 : 1 + (int)Math.Round((v - min) / span * (levels - 1))).ToList();

        // Fewer samples than dot columns (the first minute): stretch them over the full width, newest at the right.
        var columns = width * 2;
        int Sample(int dotColumn) => heights.Count >= columns
            ? heights.Count - columns + dotColumn
            : (int)((long)dotColumn * heights.Count / columns);
        for (var row = 0; row < height; row++)
        {
            SetAttribute(new Attribute(RowColor(row, height), background));
            for (var col = 0; col < width; col++)
            {
                var bits = 0;
                for (var sub = 0; sub < 2; sub++)
                {
                    var index = Sample(col * 2 + sub);

                    for (var dot = 0; dot < 4; dot++)
                    {
                        // Dot level counted from the bottom of the chart, 1-based.
                        var level = (height - row) * 4 - dot;
                        if (heights[index] >= level)
                        {
                            bits |= Dots[sub, dot];
                        }
                    }
                }

                AddRune(col, row, new System.Text.Rune(0x2800 + bits));
            }
        }

        return true;
    }

    private static Color RowColor(int row, int height)
    {
        var fromTop = height <= 1 ? 1.0 : (double)row / (height - 1);
        return fromTop switch
        {
            < 0.25 => new Color(ColorName16.BrightRed),
            < 0.5 => new Color(ColorName16.BrightYellow),
            < 0.75 => new Color(ColorName16.Yellow),
            _ => new Color(ColorName16.BrightGreen),
        };
    }
}
