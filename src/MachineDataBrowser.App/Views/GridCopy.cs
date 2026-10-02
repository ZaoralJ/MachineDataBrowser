using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace MachineDataBrowser.App.Views;

/// <summary>
/// "Copy as table" for every <see cref="DataGrid"/>: Cmd/Ctrl+C or the context menu copies the selected rows (all rows
/// when nothing is selected) as tab-separated text with a header line, in the grid's visible column order, so it pastes
/// into Excel, Numbers or a text editor as a table.
/// </summary>
public static class GridCopy
{
    /// <summary>Property of the row item copied for a column; defaults to the column's <c>SortMemberPath</c>.</summary>
    public static readonly AttachedProperty<string?> MemberProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, string?>("Member", typeof(GridCopy));

    public static string? GetMember(DataGridColumn column) => column.GetValue(MemberProperty);

    public static void SetMember(DataGridColumn column, string? value) => column.SetValue(MemberProperty, value);

    public const string MenuHeader = "Copy as table";

    private static readonly KeyGesture CopyGesture = new(Key.C, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    /// <summary>Hooks every DataGrid in the app (keyboard shortcut and context-menu entry).</summary>
    public static void Install()
    {
        InputElement.KeyDownEvent.AddClassHandler<DataGrid>(OnKeyDown, RoutingStrategies.Tunnel);
        Control.LoadedEvent.AddClassHandler<DataGrid>((grid, _) => AddMenuItem(grid));
        InputElement.GotFocusEvent.AddClassHandler<DataGrid>((grid, _) => SelectSingleRow(grid));
        InputElement.GotFocusEvent.AddClassHandler<ListBox>((list, _) => SelectSingleRow(list));
    }

    /// <summary>A focused address tree with a single top-level node (Root) selects it.</summary>
    public static void SelectSingleRow(ListBox tree)
    {
        if (tree.Classes.Contains("address-tree") && tree.SelectedItems is { Count: 0 }
            && tree.ItemsSource?.OfType<ViewModels.NodeViewModel>().Where(n => n.Depth == 0).Take(2).ToList() is [var only])
        {
            tree.SelectedItem = only;
        }
    }

    /// <summary>Selects the only row of the list or tree that makes a pane active.</summary>
    public static void SelectSingleRow(Control list)
    {
        switch (list)
        {
            case DataGrid grid:
                SelectSingleRow(grid);
                break;
            case ListBox tree:
                SelectSingleRow(tree);
                break;
        }
    }

    /// <summary>A focused table with exactly one row selects it, so copy and row commands work without a click.</summary>
    public static void SelectSingleRow(DataGrid grid)
    {
        if (grid.SelectedItems.Count == 0 && grid.ItemsSource?.Cast<object>().Take(2).Count() == 1)
        {
            grid.SelectedIndex = 0;
        }
    }

    public static string ToTable(DataGrid grid)
    {
        var columns = grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).ToList();
        var items = grid.SelectedItems.Count > 0
            ? grid.SelectedItems.Cast<object>().OrderBy(IndexIn(grid)).ToList()
            : grid.ItemsSource?.Cast<object>().ToList() ?? [];

        var text = new StringBuilder();
        text.AppendJoin('\t', columns.Select(c => Clean(c.Header?.ToString()))).Append('\n');
        foreach (var item in items)
        {
            text.AppendJoin('\t', columns.Select(c => Clean(ValueOf(item, c)))).Append('\n');
        }

        return text.ToString();
    }

    public static async Task CopyAsync(DataGrid grid)
    {
        if (TopLevel.GetTopLevel(grid)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(ToTable(grid));
        }
    }

    private static void OnKeyDown(DataGrid grid, KeyEventArgs e)
    {
        if (CopyGesture.Matches(e))
        {
            // Also stops the grid's own Ctrl+C, which copies without headers and only bound columns.
            e.Handled = true;
            _ = CopyAsync(grid);
        }
    }

    private static void AddMenuItem(DataGrid grid)
    {
        var menu = grid.ContextMenu;
        if (menu is null)
        {
            menu = new ContextMenu();
            grid.ContextMenu = menu;
        }
        else if (menu.Items.OfType<MenuItem>().Any(m => Equals(m.Header, MenuHeader)))
        {
            return;
        }
        else
        {
            menu.Items.Add(new Separator());
        }

        var item = new MenuItem
        {
            Header = MenuHeader,
            InputGesture = CopyGesture,
        };
        item.Click += async (_, _) => await CopyAsync(grid);
        menu.Items.Add(item);
    }

    private static Func<object, int> IndexIn(DataGrid grid)
    {
        var order = grid.ItemsSource?.Cast<object>().Select((item, index) => (item, index)).ToDictionary(p => p.item, p => p.index, ReferenceEqualityComparer.Instance)
            ?? [];
        return item => order.TryGetValue(item, out var index) ? index : int.MaxValue;
    }

    /// <summary>The text a column shows for a row (its copy text), or null when the column has no member.</summary>
    public static string? ValueText(object item, DataGridColumn column) => ValueOf(item, column);

    private static string? ValueOf(object item, DataGridColumn column)
    {
        var member = GetMember(column) ?? column.SortMemberPath;
        if (string.IsNullOrEmpty(member))
        {
            return null;
        }

        object? value = item;
        foreach (var name in member.Split('.'))
        {
            value = value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
        }

        return value switch
        {
            null => null,
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.CurrentCulture),
            _ => value.ToString(),
        };
    }

    private static string Clean(string? value) =>
        (value ?? string.Empty).Replace('\t', ' ').Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
}
