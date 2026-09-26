using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;

namespace OpcUaBrowser.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(new SettingsStore(), new LayoutStore());
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            window.Closed += (_, _) => viewModel.SaveLayout();
            desktop.ShutdownRequested += async (_, _) => await viewModel.DisposeAsync();

            var sessionToOpen = desktop.Args?.FirstOrDefault(a => a.EndsWith($".{SessionDocument.FileExtension}", StringComparison.OrdinalIgnoreCase))
                ?? (viewModel.Settings.ReopenLastSession && viewModel.RecentSessions.Count > 0 ? viewModel.RecentSessions[0] : null);
            if (sessionToOpen is not null && File.Exists(sessionToOpen))
            {
                window.Opened += async (_, _) => await viewModel.LoadSessionAsync(sessionToOpen);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
