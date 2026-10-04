using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace MachineDataBrowser.Cli.Tui;

/// <summary>The top line: coloured segments on the left (name, endpoint, state, …), others aligned right.</summary>
internal sealed class HeaderBar : View
{
    private IReadOnlyList<(string Text, Attribute Look)> _left = [];
    private IReadOnlyList<(string Text, Attribute Look)> _right = [];

    public HeaderBar()
    {
        CanFocus = false;
        Height = 1;
    }

    /// <summary>The line as plain text, for tests.</summary>
    public string PlainText => string.Concat(_left.Select(s => s.Text)) + " " + string.Concat(_right.Select(s => s.Text));

    public void Set(IReadOnlyList<(string, Attribute)> left, IReadOnlyList<(string, Attribute)> right)
    {
        _left = left;
        _right = right;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        SetAttribute(new Attribute(Theme.Foreground, Theme.Band));
        for (var x = 0; x < width; x++)
        {
            AddRune(x, 0, new System.Text.Rune(' '));
        }

        var position = 1;
        foreach (var (text, look) in _left)
        {
            SetAttribute(new Attribute(look.Foreground, Theme.Band, look.Style));
            AddStr(position, 0, text);
            position += text.Length;
        }

        var right = width - 1 - _right.Sum(s => s.Text.Length);
        foreach (var (text, look) in _right)
        {
            SetAttribute(new Attribute(look.Foreground, Theme.Band, look.Style));
            AddStr(Math.Max(position + 1, right), 0, text);
            right += text.Length;
        }

        return true;
    }
}
