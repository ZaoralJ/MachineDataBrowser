using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MachineDataBrowser.App.Views;

/// <summary>
/// Sizes a grid column to its content, like spreadsheets: double-click the right edge of a column header, or use the
/// header's context menu ("Fit column to content" / "Fit all columns"). The width comes from the header and every
/// row's copy text (<see cref="GridCopy"/>), not only the rows currently on screen.
/// </summary>
public static class ColumnFit
{
    private const double EdgeWidth = 8;
    private const double CellPadding = 32;
    private const int MaxRowsMeasured = 5000;
    private const double MaxWidth = 900;

    public static void Install()
    {
        // PointerPressed with ClickCount 2, not DoubleTapped: the header's resize grip captures the pointer at the edge,
        // so no Tapped/DoubleTapped gesture is raised there.
        InputElement.PointerPressedEvent.AddClassHandler<DataGridColumnHeader>(OnHeaderPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Control.LoadedEvent.AddClassHandler<DataGridColumnHeader>((header, _) => AddMenu(header));
    }

    /// <summary>Width that shows the header and the whole value of every row.</summary>
    public static double ContentWidth(DataGrid grid, DataGridColumn column)
    {
        var header = Measure(column.Header?.ToString(), grid, FontWeight.SemiBold, 11) + CellPadding + 16;
        var member = GridCopy.GetMember(column) ?? column.SortMemberPath;
        var widest = 0.0;
        if (!string.IsNullOrEmpty(member) && grid.ItemsSource is { } items)
        {
            // Cells use the UI font or the monospaced one; measuring both keeps the whole value visible either way.
            foreach (var text in items.Cast<object>().Take(MaxRowsMeasured).Select(item => GridCopy.ValueText(item, column)).Where(t => !string.IsNullOrEmpty(t)).Distinct().Take(500))
            {
                widest = Math.Max(widest, Math.Max(Measure(text, grid, FontWeight.Normal, 13), Measure(text, grid, FontWeight.Normal, 13, mono: true)));
            }
        }

        // Cells can hold more than text (recording dot, chart button, status badge): measure the realized ones too.
        foreach (var item in grid.ItemsSource?.Cast<object>().Take(MaxRowsMeasured) ?? [])
        {
            if (column.GetCellContent(item) is { } content)
            {
                content.Measure(Size.Infinity);
                widest = Math.Max(widest, content.DesiredSize.Width + content.Margin.Left + content.Margin.Right);
            }
        }

        return Math.Clamp(Math.Max(header, widest + CellPadding), column.MinWidth, MaxWidth);
    }

    /// <summary>Also for the stretching (star) column: it becomes a fixed width, and the grid scrolls horizontally.</summary>
    public static void Fit(DataGrid grid, DataGridColumn column) => column.Width = new DataGridLength(ContentWidth(grid, column));

    public static void FitAll(DataGrid grid)
    {
        foreach (var column in grid.Columns.Where(c => c.IsVisible))
        {
            Fit(grid, column);
        }
    }

    private static void OnHeaderPressed(DataGridColumnHeader header, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed
            || header.FindAncestorOfType<DataGrid>() is not { } grid || ColumnOf(grid, header) is not { } column)
        {
            return;
        }

        var x = e.GetPosition(header).X;
        var ordered = grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).ToList();
        var target = x >= header.Bounds.Width - EdgeWidth ? column
            : x <= EdgeWidth && ordered.IndexOf(column) > 0 ? ordered[ordered.IndexOf(column) - 1]
            : null;
        if (target is not null)
        {
            Fit(grid, target);
            e.Handled = true;
        }
    }

    private static void AddMenu(DataGridColumnHeader header)
    {
        if (header.ContextMenu is not null || header.FindAncestorOfType<DataGrid>() is not { } grid || ColumnOf(grid, header) is not { } column)
        {
            return;
        }

        var fit = new MenuItem { Header = "Fit column to content" };
        fit.Click += (_, _) => Fit(grid, column);
        var fitAll = new MenuItem { Header = "Fit all columns" };
        fitAll.Click += (_, _) => FitAll(grid);
        header.ContextMenu = new ContextMenu { Items = { fit, fitAll } };
    }

    private static DataGridColumn? ColumnOf(DataGrid grid, DataGridColumnHeader header) =>
        grid.Columns.FirstOrDefault(c => Equals(c.Header, header.Content));

    private static double Measure(string? text, Control scope, FontWeight weight, double size, bool mono = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var family = mono && scope.TryFindResource("MonoFont", out var font) && font is FontFamily monoFamily ? monoFamily : FontFamily.Default;
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, Brushes.Black);
        return formatted.WidthIncludingTrailingWhitespace;
    }
}
