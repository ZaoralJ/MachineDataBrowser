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

public sealed class EnterKeyTests
{
    private static MainWindow CreateMain()
    {
        var main = new MainWindow { DataContext = new MainWindowViewModel(), Width = 1280, Height = 800 };
        main.Show();
        Dispatcher.UIThread.RunJobs();
        return main;
    }

    [AvaloniaFact]
    public async Task Enter_confirms_a_message_dialog()
    {
        var main = CreateMain();
        var result = MessageDialog.ShowAsync(main, "t", "h", "b", DialogIcon.Info,
            new DialogButton<string>("Cancel", "cancel", DialogButtonRole.Cancel),
            new DialogButton<string>("OK", "ok", DialogButtonRole.Default));
        Dispatcher.UIThread.RunJobs();
        main.OwnedWindows[0].KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ok", await result);
        main.Close();
    }

    [AvaloniaFact]
    public async Task Enter_in_a_number_field_applies_the_typed_value()
    {
        var main = CreateMain();
        var result = RefreshPrompt.AskAsync(main, 250);
        Dispatcher.UIThread.RunJobs();
        var dialog = main.OwnedWindows[0];
        var box = dialog.GetVisualDescendants().OfType<TextBox>().First();
        box.Focus();
        box.Text = "1234";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1234, await result);
        main.Close();
    }

    [AvaloniaFact]
    public async Task Enter_in_a_text_field_starts_a_new_recording()
    {
        var main = CreateMain();
        var window = new NewRecordingWindow { DataContext = new NewRecordingViewModel(new NewRecordingDraft("R", 1, 250)) };
        var result = window.ShowDialog<RecordingOptions?>(main);
        Dispatcher.UIThread.RunJobs();
        var name = window.GetVisualDescendants().OfType<TextBox>().First();
        name.Focus();
        name.Text = "Typed";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Typed", (await result)?.Name);
        main.Close();
    }

    [AvaloniaFact]
    public void Enter_closes_an_information_window_without_buttons()
    {
        var main = CreateMain();
        var info = new Window { Content = new TextBlock { Text = "shortcuts" } };
        info.Show(main);
        Dispatcher.UIThread.RunJobs();
        info.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(info.IsVisible);
        main.Close();
    }
}

public sealed class MessageDialogLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1.0, "message-dialog.png")]
    [InlineData(1.5, "message-dialog-zoom150.png")]
    public void Buttons_fit_inside_the_dialog(double zoom, string screenshot)
    {
        // A private settings file: zooming persists the scale.
        var vm = new MainWindowViewModel(new MachineDataBrowser.App.Services.SettingsStore(Path.Combine(Directory.CreateTempSubdirectory("zoom").FullName, "settings.json")));
        var main = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        main.Show();
        while (vm.UiScale < zoom - 0.001)
        {
            vm.ZoomInCommand.Execute(null);
        }

        _ = MessageDialog.ShowAsync(main, "Recordings still running", "Stop the running recordings too?",
            "The watch list is empty, but recordings keep capturing their items until they are stopped. Stop keeps the recorded history; Close discards it.",
            DialogIcon.Warning,
            new DialogButton<int>("Close Recordings Now Please", 0, DialogButtonRole.Destructive),
            new DialogButton<int>("Keep Recording Everything", 1, DialogButtonRole.Cancel),
            new DialogButton<int>("Stop Recordings Right Away", 2, DialogButtonRole.Default));
        Dispatcher.UIThread.RunJobs();
        var dialog = main.OwnedWindows[0];
        foreach (var button in dialog.GetVisualDescendants().OfType<Button>())
        {
            var right = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width, 0), dialog)!.Value.X;
            Assert.True(right <= dialog.ClientSize.Width, $"'{button.Content}' ends at {right} beyond {dialog.ClientSize.Width}");
        }

        var body = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        var heading = dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Stop the running recordings too?");
        Assert.Equal(heading.TranslatePoint(default, dialog)!.Value.X, body.TranslatePoint(default, dialog)!.Value.X, 1);
        dialog.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = dialog.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", screenshot), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        dialog.Close();
        vm.ZoomResetCommand.Execute(null);
        main.Close();
    }
}
