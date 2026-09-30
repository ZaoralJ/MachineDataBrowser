using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    [AvaloniaFact]
    public void Double_clicking_a_header_edge_fits_the_column_to_the_whole_value()
    {
        var vm = new MainWindowViewModel();
        var item = new WatchItemViewModel(new NodeId(1u, 2), "A rather long display name for a watched variable");
        item.Apply(new ValueUpdate(item.NodeId, new string('x', 120), StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow));
        vm.WatchItems.Add(item);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        var name = grid.Columns.Single(c => Equals(c.Header, "Name"));
        var header = window.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(h => Equals(h.Content, "Name") && h.FindAncestorOfType<DataGrid>() == grid);

        var edge = header.TranslatePoint(new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(edge, Avalonia.Input.MouseButton.Left);
            window.MouseUp(edge, Avalonia.Input.MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();

        var text = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == item.DisplayName);
        var probe = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily };
        probe.Measure(Size.Infinity);
        Assert.True(text.Bounds.Width >= probe.DesiredSize.Width - 0.5, $"name still clipped: {text.Bounds.Width:0} of {probe.DesiredSize.Width:0}px (column {name.ActualWidth:0})");

        ColumnFit.Fit(grid, grid.Columns.Single(c => Equals(c.Header, "Value")));
        Assert.False(grid.Columns.Single(c => Equals(c.Header, "Value")).Width.IsStar);
        Assert.True(grid.Columns.Single(c => Equals(c.Header, "Value")).Width.Value > 500);
        window.Close();
    }

    [AvaloniaFact]
    public void Watch_value_column_follows_status_by_default()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(), Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        var visible = grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).Select(c => c.Header!.ToString()).ToList();
        Assert.Equal(visible.IndexOf("Status") + 1, visible.IndexOf("Value"));
        window.Close();
    }

    [AvaloniaFact]
    public void Fitting_the_name_column_keeps_room_for_the_recording_icons()
    {
        var vm = new MainWindowViewModel();
        var item = new WatchItemViewModel(new NodeId(1u, 2), "speed");
        item.SetRecordings([("R", OpcUaBrowser.Core.RecordingState.Recording)]);
        vm.WatchItems.Add(item);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");

        ColumnFit.Fit(grid, grid.Columns.Single(c => Equals(c.Header, "Name")));
        Dispatcher.UIThread.RunJobs();

        var text = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "speed");
        var probe = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily };
        probe.Measure(Size.Infinity);
        Assert.True(text.Bounds.Width >= probe.DesiredSize.Width - 0.5, $"'speed' squeezed to {text.Bounds.Width:0} of {probe.DesiredSize.Width:0}px next to the icons");
        window.Close();
    }

    [AvaloniaFact]
    public void Horizontal_scrollbar_does_not_cover_the_last_row()
    {
        var vm = new MainWindowViewModel();
        for (var i = 0; i < 60; i++)
        {
            vm.WatchItems.Add(new WatchItemViewModel(new NodeId((uint)i + 1, 2), $"item {i}"));
        }

        var window = new MainWindow { DataContext = vm, Width = 700, Height = 800 }; // narrow and full: both bars
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        var bar = grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Single(b => b.Name == "PART_HorizontalScrollbar");
        var rows = grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().Single();
        Assert.True(bar.IsVisible);
        var rowsBottom = rows.TranslatePoint(new Point(0, rows.Bounds.Height), grid)!.Value.Y;
        var barTop = bar.TranslatePoint(new Point(0, 0), grid)!.Value.Y;
        Assert.True(rowsBottom <= barTop + 0.5, $"rows end at {rowsBottom}, scrollbar starts at {barTop}");
        Assert.True(rows.ClipToBounds, "rows are not clipped, so a partly visible last row is drawn under the scrollbar");
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "watch-scrollbar.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        window.Close();
    }
}
