using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

/// <summary>
/// Several connections in one window. The header and menu follow the active connection (the window's DataContext);
/// each connection keeps its own dock with its panes, created once and kept while other tabs are shown, so pane
/// state (tree expansion, selection, scrolling) and pane shortcuts stay with their connection.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<MainWindowViewModel, DockControl> _docks = new(ReferenceEqualityComparer.Instance);

    public ConnectionTabs Tabs { get; } = new();

    /// <summary>Creates the view model of a new tab; the app sets one that uses its settings and layout stores.</summary>
    public Func<MainWindowViewModel> CreateConnection { get; set; } = () => new MainWindowViewModel();

    private void InitializeTabs() => TabStrip.DataContext = Tabs;

    /// <summary>Shows <paramref name="connection"/>: header, menu and its dock.</summary>
    private void ShowConnection(MainWindowViewModel connection)
    {
        var tab = Tabs.Find(connection) ?? Tabs.Add(connection);
        Tabs.Activate(tab);
        if (!_docks.TryGetValue(connection, out var dock))
        {
            dock = new DockControl { DataContext = connection, InitializeFactory = true, InitializeLayout = true };
            dock.Bind(DockControl.FactoryProperty, new Binding(nameof(MainWindowViewModel.DockFactory)));
            dock.Bind(DockControl.LayoutProperty, new Binding(nameof(MainWindowViewModel.Layout)));
            _docks[connection] = dock;
        }

        DockHost.Content = dock;
        connection.RefreshSettings();
    }

    public void NewConnectionTab()
    {
        var connection = CreateConnection();
        Tabs.Add(connection);
        DataContext = connection;
    }

    /// <summary>Closes a connection's tab after its unsaved changes are settled; the last tab stays.</summary>
    public async Task CloseConnectionTabAsync(MainWindowViewModel connection)
    {
        if (Tabs.Tabs.Count < 2 || Tabs.Find(connection) is not { } tab)
        {
            return;
        }

        if (!ReferenceEquals(DataContext, connection))
        {
            DataContext = connection; // show it while asking about its unsaved changes
        }

        if (!await connection.ConfirmDiscardAsync())
        {
            return;
        }

        if (Tabs.Remove(tab) is { } next)
        {
            DataContext = next.Connection;
        }

        _docks.Remove(connection);
        try
        {
            await connection.ShutdownAsync().WaitAsync(ShutdownTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException || Services.AppErrors.IsRecoverable(ex))
        {
            Services.AppErrors.Log(ex, "closing a connection tab");
        }
    }

    public void ShowNextTab(int step)
    {
        if (Tabs.Next(step) is { } tab)
        {
            DataContext = tab.Connection;
        }
    }

    private bool CanCloseTab() => Tabs.HasMany;

    private void OnNewTab(object? sender, RoutedEventArgs e) => NewConnectionTab();

    private void OnTabClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ConnectionTab tab)
        {
            DataContext = tab.Connection;
        }
    }

    private async void OnTabCloseClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ConnectionTab tab)
        {
            await CloseConnectionTabAsync(tab.Connection);
        }
    }
}
