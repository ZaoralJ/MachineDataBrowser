using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Opc.Ua;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class GridCopyTests
{
    [AvaloniaFact]
    public void Selected_rows_copy_as_a_tab_separated_table_in_visible_column_order()
    {
        var vm = new MainWindowViewModel();
        for (var i = 0; i < 3; i++)
        {
            var item = new WatchItemViewModel(new NodeId((uint)i + 1, 2), $"item{i}") { RefreshMs = 500 };
            item.Apply(new ValueUpdate(item.NodeId, $"v\t{i}", StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow));
            vm.WatchItems.Add(item);
        }

        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        grid.SelectedItems.Add(vm.WatchItems[2]);
        grid.SelectedItems.Add(vm.WatchItems[0]);

        var lines = GridCopy.ToTable(grid).TrimEnd('\n').Split('\n');

        var header = lines[0].Split('\t');
        Assert.Equal(grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).Select(c => c.Header!.ToString()), header);
        Assert.Equal(3, lines.Length); // header + two rows, in list order (not selection order)
        var row = lines[1].Split('\t');
        Assert.Equal("item0", row[Array.IndexOf(header, "Name")]);
        Assert.Equal("500 ms", row[Array.IndexOf(header, "Refresh")]);
        Assert.Equal("v 0", row[Array.IndexOf(header, "Value")]); // tabs inside values are flattened
        Assert.StartsWith("item2\t", lines[2], StringComparison.Ordinal);

        Assert.Contains(grid.ContextMenu!.Items.OfType<MenuItem>(), m => Equals(m.Header, GridCopy.MenuHeader));
        foreach (var other in window.GetVisualDescendants().OfType<DataGrid>())
        {
            Assert.Contains(other.ContextMenu!.Items.OfType<MenuItem>(), m => Equals(m.Header, GridCopy.MenuHeader));
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Focusing_a_table_with_one_row_selects_it()
    {
        var vm = new MainWindowViewModel();
        vm.WatchItems.Add(new WatchItemViewModel(new NodeId(1u, 2), "only"));
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        Assert.Empty(grid.SelectedItems);

        grid.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(vm.WatchItems[0], grid.SelectedItem);
        Assert.Equal([vm.WatchItems[0]], vm.SelectedWatchItems);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_pane_with_one_row_selects_it()
    {
        var vm = new MainWindowViewModel();
        var only = new WatchItemViewModel(new NodeId(1u, 2), "only");
        vm.WatchItems.Add(only);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Click the empty space of the Watch toolbar, not the row.
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Remove all from watch");
        var at = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width + 60, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([only], vm.SelectedWatchItems);
        window.Close();
    }

    [Fact]
    public void Timestamps_use_one_short_format_with_the_date_in_the_tooltip()
    {
        var at = new DateTimeOffset(2026, 9, 30, 20, 5, 54, 569, TimeSpan.Zero);
        var row = new HistoryRow(at, "T", "ns=1;s=T", "81", "Good", at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(row.TimeText, row.SourceTimeText);
        Assert.Equal(Timestamps.Format(at), row.SourceTimeText);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3}$", row.SourceTimeText); // e.g. 22:05:54.569
        Assert.Contains("2026", row.SourceTimeToolTip, StringComparison.Ordinal); // the date is in the tooltip
    }
}
