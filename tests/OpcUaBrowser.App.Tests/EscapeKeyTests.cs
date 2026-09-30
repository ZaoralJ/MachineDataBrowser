using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class EscapeKeyTests
{
    [AvaloniaFact]
    public void Escape_closes_secondary_windows_but_not_the_main_window()
    {
        var main = new MainWindow { DataContext = new MainWindowViewModel(), Width = 1280, Height = 800 };
        main.Show();
        var viewer = new RecordingViewerWindow { DataContext = RecordingViewerViewModel.ForFile(Path.GetTempFileName()) };
        var closed = false;
        viewer.Closed += (_, _) => closed = true;
        viewer.Show(main);
        Dispatcher.UIThread.RunJobs();

        viewer.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed);

        main.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(main.IsVisible);
        main.Close();
    }

    [AvaloniaFact]
    public async Task Escape_on_a_dialog_with_cancel_returns_cancel()
    {
        var main = new MainWindow { DataContext = new MainWindowViewModel(), Width = 1280, Height = 800 };
        main.Show();
        var result = MessageDialog.ShowAsync(main, "Unsaved", "Save?", "Body", DialogIcon.Warning,
            new DialogButton<string>("Save", "save", DialogButtonRole.Default),
            new DialogButton<string>("Cancel", "cancel", DialogButtonRole.Cancel));
        Dispatcher.UIThread.RunJobs();

        var dialog = main.OwnedWindows.Single();
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("cancel", await result);
        main.Close();
    }
}
