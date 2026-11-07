using System.Collections.Specialized;
using System.ComponentModel;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class MainWindow
{
    private readonly Dictionary<MainWindowViewModel, PropertyChangedEventHandler> _sessionCollectionPropertyHandlers = [];
    private string? _sessionCollectionPath;
    private bool _sessionCollectionDirty;
    private bool _suppressSessionCollectionChanges;

    public bool IsSessionCollectionDirty => _sessionCollectionDirty;

    public async Task SaveSessionCollectionAsAsync()
    {
        if (DataContext is not MainWindowViewModel vm || vm.Dialogs is null
            || await vm.Dialogs.PickSessionCollectionSaveTargetAsync("Sessions") is not { } path)
        {
            return;
        }

        await WriteSessionCollectionAsync(path);
    }

    private Task SaveSessionCollectionAsync() => _sessionCollectionPath is { } path
        ? WriteSessionCollectionAsync(path)
        : SaveSessionCollectionAsAsync();

    private async Task OpenSessionFromWindowAsync(MainWindowViewModel connection)
    {
        if (connection.Dialogs is null || await connection.Dialogs.PickSessionToOpenAsync() is not { } path)
        {
            return;
        }

        if (SessionCollectionDocument.IsSessionCollectionFile(path))
        {
            await OpenSessionCollectionAsync(path);
            return;
        }

        if (await connection.ConfirmDiscardAsync())
        {
            await connection.LoadSessionAsync(path);
        }
    }

    public async Task OpenSessionCollectionAsync(string path)
    {
        SessionCollectionDocument collection;
        try
        {
            collection = await SessionCollectionDocument.LoadAsync(path);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            AppErrors.Report(ex, "Could not open session collection");
            return;
        }

        var existing = Tabs.Tabs.Select(tab => tab.Connection).ToArray();
        foreach (var connection in existing.Where(connection => connection.IsDirty).ToArray())
        {
            if (!await connection.ConfirmDiscardAsync())
            {
                return;
            }
        }

        var activeDialogs = (DataContext as MainWindowViewModel)?.Dialogs;
        var staged = new List<MainWindowViewModel>();
        try
        {
            foreach (var session in collection.Sessions)
            {
                var connection = CreateConnection();
                staged.Add(connection);
                connection.Dialogs = activeDialogs;
                if (!await connection.LoadSessionDocumentAsync(session, path, isCollectionEntry: true))
                {
                    await ShutDownSessionConnectionsAsync(staged, "discarding a cancelled collection restore");
                    return;
                }
            }
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            await ShutDownSessionConnectionsAsync(staged, "discarding a failed collection restore");
            AppErrors.Report(ex, "Could not restore session collection");
            return;
        }

        await ShutDownSessionConnectionsAsync(existing, "replacing connections with a session collection");
        _suppressSessionCollectionChanges = true;
        try
        {
            _sessionCollectionPath = path;
            _sessionCollectionDirty = false;
            foreach (var connection in existing)
            {
                if (Tabs.Find(connection) is { } tab)
                {
                    Tabs.Remove(tab);
                }

                _docks.Remove(connection);
            }

            _docks.Clear();
            foreach (var connection in staged)
            {
                Tabs.Add(connection);
            }

            DataContext = staged[0];
        }
        finally
        {
            _suppressSessionCollectionChanges = false;
        }
    }

    public async Task WriteSessionCollectionAsync(string path)
    {
        var connections = Tabs.Tabs.Select(tab => tab.Connection).ToArray();
        if (connections.Length == 0 && DataContext is MainWindowViewModel vm)
        {
            connections = [vm];
        }

        try
        {
            var document = new SessionCollectionDocument
            {
                Sessions = [.. connections.Select(connection => connection.CaptureSessionDocument(path))],
            };
            await document.SaveAsync(path);

            _sessionCollectionPath = path;
            _suppressSessionCollectionChanges = true;
            try
            {
                _sessionCollectionDirty = false;
                foreach (var connection in connections)
                {
                    connection.SaveSessionCollection = SaveSessionCollectionAsync;
                    connection.SaveSessionCollectionAs = SaveSessionCollectionAsAsync;
                    connection.SetSessionCollectionSavedState(path);
                }
            }
            finally
            {
                _suppressSessionCollectionChanges = false;
            }
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            AppErrors.Report(ex, "Could not save session collection");
        }
    }

    private void OnConnectionTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var current = Tabs.Tabs.Select(tab => tab.Connection).ToHashSet();
        foreach (var connection in _sessionCollectionPropertyHandlers.Keys.Where(connection => !current.Contains(connection)).ToArray())
        {
            connection.SessionContentChanged -= OnSessionContentChanged;
            connection.PropertyChanged -= _sessionCollectionPropertyHandlers[connection];
            _sessionCollectionPropertyHandlers.Remove(connection);
            connection.SaveSessionCollection = null;
            connection.SaveSessionCollectionAs = null;
            connection.OpenSessionFromWindow = null;
        }

        foreach (var connection in current.Where(connection => !_sessionCollectionPropertyHandlers.ContainsKey(connection)))
        {
            PropertyChangedEventHandler propertyHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.CurrentSessionPath))
                {
                    MarkSessionCollectionDirty();
                }
                else if (args.PropertyName == nameof(MainWindowViewModel.IsDirty) && _sessionCollectionDirty && !_suppressSessionCollectionChanges)
                {
                    connection.SetSessionCollectionDirty(true);
                }
            };
            _sessionCollectionPropertyHandlers.Add(connection, propertyHandler);
            connection.PropertyChanged += propertyHandler;
            connection.SessionContentChanged += OnSessionContentChanged;
            connection.OpenSessionFromWindow = () => OpenSessionFromWindowAsync(connection);
            if (_sessionCollectionPath is not null)
            {
                connection.SaveSessionCollection = SaveSessionCollectionAsync;
                connection.SaveSessionCollectionAs = SaveSessionCollectionAsAsync;
                connection.SetSessionCollectionSavedState(_sessionCollectionPath);
                if (_sessionCollectionDirty)
                {
                    connection.SetSessionCollectionDirty(true);
                }
            }
        }

        if (!_suppressSessionCollectionChanges && (e.NewItems?.Count > 0 || e.OldItems?.Count > 0))
        {
            MarkSessionCollectionDirty();
        }
    }

    private void OnSessionContentChanged(object? sender, EventArgs e) => MarkSessionCollectionDirty();

    private static async Task ShutDownSessionConnectionsAsync(IEnumerable<MainWindowViewModel> connections, string context)
    {
        try
        {
            await Task.WhenAll(connections.Select(connection => connection.ShutdownAsync())).WaitAsync(ShutdownTimeout);
        }
        catch (TimeoutException ex)
        {
            AppErrors.Log(ex, $"{context} timed out");
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            AppErrors.Log(ex, context);
        }
    }

    private void MarkSessionCollectionDirty()
    {
        if (_sessionCollectionPath is null || _sessionCollectionDirty || _suppressSessionCollectionChanges)
        {
            return;
        }

        _sessionCollectionDirty = true;
        _suppressSessionCollectionChanges = true;
        try
        {
            foreach (var tab in Tabs.Tabs)
            {
                tab.Connection.SetSessionCollectionDirty(true);
            }
        }
        finally
        {
            _suppressSessionCollectionChanges = false;
        }
    }
}
