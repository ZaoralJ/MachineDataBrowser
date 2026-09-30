using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;

namespace OpcUaBrowser.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        FitColumnHeaders();
    }

    /// <summary>
    /// Fluent's column header template keeps a MinWidth=32 column for the sort arrow even when a column is
    /// unsorted, so narrow columns showed "El" for "Elapsed". Let that column shrink to the arrow instead.
    /// </summary>
    private static void FitColumnHeaders() =>
        Avalonia.Controls.Primitives.TemplatedControl.TemplateAppliedEvent.AddClassHandler<Avalonia.Controls.DataGridColumnHeader>((header, e) =>
        {
            if ((e.NameScope.Find("SortIcon") as Avalonia.Controls.Shapes.Path)?.Parent is Avalonia.Controls.Grid grid)
            {
                foreach (var column in grid.ColumnDefinitions.Skip(1))
                {
                    column.MinWidth = 0;
                    column.Width = Avalonia.Controls.GridLength.Auto;
                }
            }
        });

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
