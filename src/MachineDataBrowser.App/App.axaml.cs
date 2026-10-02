using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;

namespace MachineDataBrowser.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        FitColumnHeaders();
        CloseSecondaryWindowsOnEscape();
        Views.GridCopy.Install();
        Views.ColumnFit.Install();
        Views.UiZoom.Install();
    }

    /// <summary>
    /// Esc closes secondary windows (recording viewer, info dialogs). Dialogs with a Cancel button already map Esc
    /// to it and return "cancel", so they are left alone; the main window never closes on Esc.
    /// </summary>
    private static void CloseSecondaryWindowsOnEscape() =>
        Avalonia.Input.InputElement.KeyDownEvent.AddClassHandler<Avalonia.Controls.Window>((window, e) =>
        {
            if (e.Handled || window is Views.MainWindow || e.KeyModifiers != Avalonia.Input.KeyModifiers.None)
            {
                return;
            }

            // Enter confirms a dialog through its default button (Avalonia handles that itself); dialogs without one,
            // such as Keyboard Shortcuts, simply close.
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                if (!Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Any(b => b.IsDefault && b.IsEffectivelyVisible)
                    && window.FocusManager?.GetFocusedElement() is not Avalonia.Controls.TextBox { AcceptsReturn: true })
                {
                    e.Handled = true;
                    window.Close();
                }

                return;
            }

            if (e.Key != Avalonia.Input.Key.Escape)
            {
                return;
            }

            if (Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().Any(b => b.IsCancel && b.IsEffectivelyVisible))
            {
                return;
            }

            e.Handled = true;
            window.Close();
        });

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
        AppErrors.Install();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(new SettingsStore(), new LayoutStore());
            var window = new MainWindow
            {
                CreateConnection = () => new MainWindowViewModel(new SettingsStore(), new LayoutStore()),
                DataContext = viewModel,
            };
            desktop.MainWindow = window;
            window.Closed += (_, _) => (window.DataContext as MainWindowViewModel)?.SaveLayout();
            // No cleanup on ShutdownRequested: it fires before the "unsaved changes" prompt, so disposing here
            // disconnected and stopped recordings while the user was still deciding. MainWindow.OnClosing shuts
            // down gently once quitting is confirmed; if the prompt is cancelled everything keeps running.

            var sessionToOpen = desktop.Args?.FirstOrDefault(SessionDocument.IsSessionFile)
                ?? (viewModel.Settings.ReopenLastSession && viewModel.RecentSessions.Count > 0 ? viewModel.RecentSessions[0] : null);
            if (sessionToOpen is not null && File.Exists(sessionToOpen))
            {
                window.Opened += async (_, _) => await viewModel.LoadSessionAsync(sessionToOpen);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
