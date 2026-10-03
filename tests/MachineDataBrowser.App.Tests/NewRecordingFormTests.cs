using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class NewRecordingFormTests
{
    [Fact]
    public void File_retention_is_offered_for_sqlite_files_only()
    {
        var form = new NewRecordingViewModel(new NewRecordingDraft("R", 1, 250)) { UseLiveFile = true, LiveFilePath = "/tmp/line1.csv", UseFileRetention = true, FileRetentionDays = 7 };
        Assert.False(form.CanUseFileRetention);
        Assert.Null(form.ToOptions(DateTimeOffset.UtcNow).FileRetention);   // CSV: only ever appended to

        form.LiveFilePath = "/tmp/line1.db";
        Assert.True(form.CanUseFileRetention);
        Assert.Equal(TimeSpan.FromDays(7), form.ToOptions(DateTimeOffset.UtcNow).FileRetention);

        form.UseLiveFile = false;
        Assert.False(form.CanUseFileRetention);
        Assert.Null(form.ToOptions(DateTimeOffset.UtcNow).FileRetention);
    }

    [AvaloniaFact]
    public async Task Typed_max_points_is_used_when_clicking_start()
    {
        var main = new MainWindow { DataContext = new MainWindowViewModel(), Width = 1280, Height = 800 };
        main.Show();
        var form = new NewRecordingWindow { DataContext = new NewRecordingViewModel(new NewRecordingDraft("R", 1, 250)) };
        var result = form.ShowDialog<RecordingOptions?>(main);
        Dispatcher.UIThread.RunJobs();

        var maxPoints = form.GetVisualDescendants().OfType<NumericUpDown>().ElementAt(1);
        var box = maxPoints.GetVisualDescendants().OfType<TextBox>().First();
        var p = box.TranslatePoint(new Point(10, box.Bounds.Height / 2), form)!.Value;
        form.MouseDown(p, MouseButton.Left);
        form.MouseUp(p, MouseButton.Left);
        box.SelectAll();
        form.KeyTextInput("25");
        Dispatcher.UIThread.RunJobs();

        var start = form.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Start"));
        var s = start.TranslatePoint(new Point(start.Bounds.Width / 2, start.Bounds.Height / 2), form)!.Value;
        form.MouseDown(s, MouseButton.Left);
        form.MouseUp(s, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(25, (await result)?.MaxPointsPerItem);
        main.Close();
    }
}
