using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace MachineDataBrowser.Cli.Tui;

/// <summary>
/// The key bar at the bottom, like btop: one word per action with its key letter highlighted ("Monitor" with M bright),
/// or the key in front when it isn't in the word ("/Search"). Only draws; the keys are handled by the app.
/// </summary>
internal sealed class KeyBar : View
{
    private readonly IReadOnlyList<(char Key, string Word)> _items;

    public KeyBar(IReadOnlyList<(char Key, string Word)> items)
    {
        _items = items;
        CanFocus = false;
        Height = 1;
    }

    /// <summary>The bar as plain text, for tests and widths.</summary>
    public string PlainText => string.Join("  ", _items.Select(i => Label(i.Key, i.Word).Text));

    /// <summary>The word to show and where its key letter is in it.</summary>
    public static (string Text, int KeyAt) Label(char key, string word)
    {
        var at = word.IndexOf(char.ToUpperInvariant(key), StringComparison.Ordinal);
        at = at < 0 ? word.IndexOf(key, StringComparison.Ordinal) : at;
        return at >= 0 ? (word, at) : ($"{key}{word}", 0);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = Theme.Text(Theme.Muted);
        var hot = Theme.Text(Theme.Blue, TextStyle.Bold | TextStyle.Underline);
        var x = 1;
        foreach (var (key, word) in _items)
        {
            var (text, keyAt) = Label(key, word);
            for (var i = 0; i < text.Length && x < Viewport.Width; i++, x++)
            {
                SetAttribute(i == keyAt ? hot : normal);
                AddRune(x, 0, new System.Text.Rune(text[i]));
            }

            x += 2;
        }

        SetAttribute(normal);
        for (; x < Viewport.Width; x++)
        {
            AddRune(x, 0, new System.Text.Rune(' '));
        }

        return true;
    }
}
