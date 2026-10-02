using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

/// <summary>One connection in the window's tab strip.</summary>
public sealed partial class ConnectionTab : ObservableObject
{
    public ConnectionTab(MainWindowViewModel connection)
    {
        Connection = connection;
        connection.PropertyChanged += OnConnectionChanged;
    }

    public MainWindowViewModel Connection { get; }

    /// <summary>The session's name once saved, else the endpoint without the scheme (e.g. "plc-7:4840").</summary>
    public string Title => Connection.CurrentSessionPath is not null
        ? Connection.DocumentName
        : Uri.TryCreate(Connection.EndpointUrl?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.IsDefaultPort || uri.Port < 0 ? uri.Host : $"{uri.Host}:{uri.Port}"
            : string.IsNullOrWhiteSpace(Connection.EndpointUrl) ? "New connection" : Connection.EndpointUrl.Trim();

    public string ToolTip => $"{Connection.EndpointUrl}\n{Connection.State}{(Connection.IsDirty ? " · unsaved changes" : string.Empty)}";

    public bool IsConnected => Connection.State == ConnectionState.Connected;

    public bool IsReconnecting => Connection.State is ConnectionState.Reconnecting or ConnectionState.Connecting;

    public bool IsDirty => Connection.IsDirty;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.EndpointUrl) or nameof(MainWindowViewModel.CurrentSessionPath)
            or nameof(MainWindowViewModel.IsDirty) or nameof(MainWindowViewModel.State))
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsReconnecting));
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public void Detach() => Connection.PropertyChanged -= OnConnectionChanged;
}

/// <summary>
/// The connections open in one window. Each is a full <see cref="MainWindowViewModel"/> with its own client, watch
/// list, recordings and session file; the window shows the active one.
/// </summary>
public sealed partial class ConnectionTabs : ObservableObject
{
    public ObservableCollection<ConnectionTab> Tabs { get; } = [];

    [ObservableProperty]
    public partial ConnectionTab? Active { get; private set; }

    /// <summary>The tab strip shows once there is more than one connection.</summary>
    public bool HasMany => Tabs.Count > 1;

    public ConnectionTab Add(MainWindowViewModel connection)
    {
        var tab = new ConnectionTab(connection);
        Tabs.Add(tab);
        OnPropertyChanged(nameof(HasMany));
        return tab;
    }

    public void Activate(ConnectionTab tab)
    {
        foreach (var other in Tabs)
        {
            other.IsActive = ReferenceEquals(other, tab);
        }

        Active = tab;
    }

    /// <summary>Removes a tab and returns the one to show next (its right neighbour, else the left one).</summary>
    public ConnectionTab? Remove(ConnectionTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return Active;
        }

        Tabs.RemoveAt(index);
        tab.Detach();
        OnPropertyChanged(nameof(HasMany));
        return Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public ConnectionTab? Next(int step) =>
        Active is null || Tabs.Count < 2 ? null : Tabs[((Tabs.IndexOf(Active) + step) % Tabs.Count + Tabs.Count) % Tabs.Count];

    public ConnectionTab? Find(MainWindowViewModel connection) => Tabs.FirstOrDefault(t => ReferenceEquals(t.Connection, connection));
}
