using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Credentials;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string AppTitle = "Machine Data Browser";

    private readonly SettingsStore? _settingsStore;
    private bool _suppressDirty;
    private IReadOnlyList<RecentSessionEntry>? _recentSessionEntries;

    public IDialogService? Dialogs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecentSessions), nameof(RecentEndpoints), nameof(HasRecentSessions), nameof(HasRecentEndpoints), nameof(HasRecentHistory), nameof(HasRecentSessionsAndEndpoints), nameof(Theme), nameof(ColorTheme), nameof(UiScale))]
    public partial AppSettings Settings { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(DocumentName))]
    public partial string? CurrentSessionPath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial bool IsDirty { get; private set; }

    public string DocumentName => CurrentSessionPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(CurrentSessionPath);

    public string WindowTitle => $"{DocumentName}{(IsDirty ? " •" : string.Empty)} — {AppTitle}";

    public IReadOnlyList<string> RecentSessions => Settings.RecentSessions;

    public IReadOnlyList<RecentSessionEntry> RecentSessionEntries => _recentSessionEntries ??= Settings.RecentSessions
        .Select(path => new RecentSessionEntry(path, Path.GetFileNameWithoutExtension(path), SessionDocument.ReadEndpoint(path)))
        .ToArray();

    public IReadOnlyList<string> RecentEndpoints => Settings.RecentEndpoints;

    public bool HasRecentSessions => Settings.RecentSessions.Count > 0;

    public bool HasRecentEndpoints => Settings.RecentEndpoints.Count > 0;

    public bool HasRecentHistory => HasRecentSessions || HasRecentEndpoints;

    public bool HasRecentSessionsAndEndpoints => HasRecentSessions && HasRecentEndpoints;

    partial void OnSettingsChanged(AppSettings value)
    {
        _recentSessionEntries = null;
        OnPropertyChanged(nameof(RecentSessionEntries));
    }

    public ThemePreference Theme => Settings.Theme;

    public string ColorTheme => ColorThemes.Find(Settings.ColorTheme).Name;

    private bool IsReadOnlyDriver => IsConnected && _client.IsReadOnly
        || (_clientFactory.Supports(_client, EndpointUrl ?? string.Empty)
            ? _client.IsReadOnly
            : MachineDataBrowser.Core.DeviceClient.IsMtConnect(EndpointUrl ?? string.Empty));

    /// <summary>Whether the protocol supports changing the read-only session setting.</summary>
    public bool CanToggleReadOnly => !IsReadOnlyDriver;

    partial void OnEndpointUrlChanged(string oldValue, string newValue)
    {
        UserName = string.Empty;
        Password = string.Empty;
        RememberPassword = false;
        OnPropertyChanged(nameof(CanToggleReadOnly));
        if (_writeAccess.Count > 0)
        {
            _writeAccess.Clear();
            WriteAttributeValueCommand.NotifyCanExecuteChanged();
            WriteWatchValueCommand.NotifyCanExecuteChanged();
        }

        // MTConnect is read-only; new connections start in the same safe mode as saved sessions.
        if (IsReadOnlyDriver && !IsReadOnly)
        {
            IsReadOnly = true;
        }

        // MQTT defaults to every message (0); the other protocols to the configured default. Only a refresh time that
        // is still the other protocol's default is switched, so an explicit choice survives editing the endpoint.
        var wasMqtt = MachineDataBrowser.Core.DeviceClient.IsMqtt(oldValue ?? string.Empty);
        var isMqtt = MachineDataBrowser.Core.DeviceClient.IsMqtt(newValue ?? string.Empty);
        if (isMqtt && !wasMqtt && DefaultRefreshMs == Settings.SamplingIntervalMs)
        {
            DefaultRefreshMs = 0;
        }
        else if (!isMqtt && wasMqtt && DefaultRefreshMs == 0)
        {
            DefaultRefreshMs = Settings.SamplingIntervalMs;
        }

        MarkDirty();
    }

    /// <summary>Default refresh time for new watch items and recordings: every message for MQTT.</summary>
    private int DefaultRefreshFor(string endpointUrl) => MachineDataBrowser.Core.DeviceClient.IsMqtt(endpointUrl) ? 0 : Settings.SamplingIntervalMs;

    partial void OnUseSecurityChanged(bool value) => MarkDirty();

    partial void OnAutoAcceptCertificatesChanged(bool value) => MarkDirty();

    partial void OnUserNameChanged(string value)
    {
        RememberPassword = false;
        MarkDirty();
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        await ResetAsync();
        _suppressDirty = true;
        EndpointUrl = DefaultEndpointUrl;
        UseSecurity = false;
        Bookmarks.Clear();
        IsReadOnly = false;
        AutoAcceptCertificates = false;
        UserName = string.Empty;
        DefaultRefreshMs = DefaultRefreshFor(EndpointUrl);
        AutoPauseDiscoverySeconds = 0;
        TagMapPath = string.Empty;
        GroupWatchByPath = false;
        WatchColumns.Reset();
        Password = string.Empty;
        RememberPassword = false;
        _suppressDirty = false;
        CurrentSessionPath = null;
        IsDirty = false;
        StatusMessage = "New session";
    }

    [RelayCommand]
    private async Task OpenSessionAsync()
    {
        if (Dialogs is null || !await ConfirmDiscardAsync())
        {
            return;
        }

        if (await Dialogs.PickSessionToOpenAsync() is { } path)
        {
            await LoadSessionAsync(path);
        }
    }

    [RelayCommand]
    private async Task OpenRecentSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !await ConfirmDiscardAsync())
        {
            return;
        }

        if (!File.Exists(path))
        {
            ErrorMessage = $"Session file not found: {path}";
            UpdateSettings(Settings with { RecentSessions = [.. Settings.RecentSessions.Where(p => p != path)] });
            return;
        }

        await LoadSessionAsync(path);
        // A session without watch items returns from LoadSessionAsync before connecting. Choosing it from history
        // should still restore its endpoint as a live connection.
        if (CurrentSessionPath == path && WatchItems.Count == 0 && IsDisconnected)
        {
            await ConnectAsync();
        }
    }

    public async Task LoadSessionAsync(string path)
    {
        SessionDocument document;
        try
        {
            document = await SessionDocument.LoadAsync(path);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ErrorMessage = $"Could not open session: {ex.Message}";
            return;
        }

        var sameConnection = string.Equals(EndpointUrl.Trim(), document.EndpointUrl.Trim(), StringComparison.Ordinal)
            && string.Equals(UserName, document.UserName, StringComparison.Ordinal);
        string? sessionPassword = sameConnection && !string.IsNullOrEmpty(Password) ? Password : null;
        var rememberPassword = sameConnection && RememberPassword;
        if (sessionPassword is null && (!string.IsNullOrWhiteSpace(document.UserName) || document.CredentialsRequired))
        {
            try
            {
                sessionPassword = await _credentialStore.GetAsync(EndpointCredentialKey.Create(document.EndpointUrl, document.UserName));
                rememberPassword = sessionPassword is not null;
            }
            catch (Exception ex) when (AppErrors.IsRecoverable(ex))
            {
                // Continue with the password prompt if the OS credential store is unavailable.
            }
        }

        if (document.Watch.Count > 0 && (!string.IsNullOrWhiteSpace(document.UserName) || document.CredentialsRequired)
            && sessionPassword is null && Dialogs is not null)
        {
            var answer = await Dialogs.AskSessionPasswordAsync(document.UserName ?? "default", document.EndpointUrl);
            if (answer is null)
            {
                return;
            }

            sessionPassword = answer.Password;
            rememberPassword = answer.Remember;
        }

        AcceptSessionFile();
        await ResetAsync();
        _suppressDirty = true;
        EndpointUrl = document.EndpointUrl;
        UseSecurity = document.UseSecurity;
        IsReadOnly = document.ReadOnly || MachineDataBrowser.Core.DeviceClient.IsMtConnect(document.EndpointUrl);
        Bookmarks.Clear();
        foreach (var bookmark in document.Bookmarks)
        {
            Bookmarks.Add(bookmark);
        }

        AutoAcceptCertificates = document.AutoAcceptCertificates;
        UserName = document.UserName ?? string.Empty;
        RememberPassword = rememberPassword;
        DefaultRefreshMs = document.DefaultRefreshMs ?? DefaultRefreshFor(document.EndpointUrl);
        AutoPauseDiscoverySeconds = document.AutoPauseDiscoverySeconds ?? 0;
        TagMapPath = string.IsNullOrWhiteSpace(document.TagMap) ? string.Empty : MachineDataBrowser.Core.TagMapReference.Resolve(document.TagMap, path);
        WatchColumns.SetSort(document.WatchSortColumn, document.WatchSortDescending);
        WatchColumns.Apply(document.WatchColumns);
        GroupWatchByPath = document.GroupWatchByPath;
        Password = sessionPassword ?? string.Empty;
        _suppressDirty = false;

        CurrentSessionPath = path;
        UpdateSettings(Settings.WithRecentSession(path));

        if (document.Watch.Count == 0)
        {
            IsDirty = false;
            StatusMessage = $"Opened {DocumentName}";
            return;
        }

        await ConnectAsync();
        if (!IsConnected)
        {
            IsDirty = false;
            return;
        }

        var resolved = new List<(NodeId, string, string, int)>();
        var unresolved = new List<string>();
        var monitoring = new Dictionary<NodeId, MachineDataBrowser.Core.MonitoringOptions>();
        var displays = new Dictionary<NodeId, MachineDataBrowser.Core.ValueDisplay>();
        foreach (var entry in document.Watch)
        {
            try
            {
                var nodeId = _client.ParsePortableId(entry.NodeId);
                resolved.Add((nodeId, entry.DisplayName, entry.Path ?? string.Empty, entry.RefreshMs ?? DefaultRefreshMs));
                if (entry.Monitoring is { IsDefault: false } options)
                {
                    monitoring[nodeId] = options;
                }

                if (entry.Display is { IsDefault: false } display)
                {
                    displays[nodeId] = display;
                }
            }
            catch (Exception parseError) when (AppErrors.IsRecoverable(parseError))
            {
                unresolved.Add(entry.DisplayName);
            }
        }

        _suppressDirty = true;
        await AddWatchItemsAsync(resolved);
        foreach (var item in WatchItems.Where(w => monitoring.ContainsKey(w.NodeId)))
        {
            item.Monitoring = monitoring[item.NodeId];
        }

        foreach (var item in WatchItems.Where(w => displays.ContainsKey(w.NodeId)))
        {
            item.Display = displays[item.NodeId];
        }

        await ReapplyMonitoringAsync([.. WatchItems.Where(w => !w.Monitoring.IsDefault)]);
        _suppressDirty = false;
        IsDirty = false;

        if (unresolved.Count > 0)
        {
            ErrorMessage = $"{unresolved.Count} watch item(s) could not be resolved on this server: {string.Join(", ", unresolved.Take(5))}";
        }

        StatusMessage = $"Opened {DocumentName} — {WatchItems.Count} item(s) monitored";
    }

    [RelayCommand]
    private async Task SaveSessionAsync()
    {
        if (CurrentSessionPath is null)
        {
            await SaveSessionAsAsync();
            return;
        }

        await WriteSessionAsync(CurrentSessionPath);
    }

    [RelayCommand]
    private async Task SaveSessionAsAsync()
    {
        if (Dialogs is not null && await Dialogs.PickSessionSaveTargetAsync(DocumentName) is { } path)
        {
            await WriteSessionAsync(path);
        }
    }

    public async Task WriteSessionAsync(string path)
    {
        var document = new SessionDocument
        {
            EndpointUrl = EndpointUrlTools.WithoutPassword(EndpointUrl),
            UseSecurity = UseSecurity,
            Bookmarks = [.. Bookmarks],
            ReadOnly = IsReadOnly,
            AutoAcceptCertificates = AutoAcceptCertificates,
            UserName = string.IsNullOrWhiteSpace(UserName) ? EndpointUrlTools.UserName(EndpointUrl) : UserName,
            CredentialsRequired = !string.IsNullOrEmpty(Password) || EndpointUrlTools.HasPassword(EndpointUrl),
            DefaultRefreshMs = DefaultRefreshMs,
            AutoPauseDiscoverySeconds = AutoPauseDiscoverySeconds > 0 ? AutoPauseDiscoverySeconds : null,
            TagMap = HasTagMap ? MachineDataBrowser.Core.TagMapReference.ToStored(TagMapPath.Trim(), path) : null,
            WatchColumns = WatchColumns.Capture(),
            WatchSortColumn = WatchColumns.SortColumn,
            WatchSortDescending = WatchColumns.SortDescending,
            GroupWatchByPath = GroupWatchByPath,
            Watch = [.. WatchItems.Select(w => new WatchEntry(w.PortableId, w.DisplayName, w.RefreshMs == DefaultRefreshMs ? null : w.RefreshMs, w.Monitoring.IsDefault ? null : w.Monitoring, w.Display.IsDefault ? null : w.Display, w.Path.Length == 0 ? null : w.Path))],
        };

        try
        {
            await document.SaveAsync(path);
            AcceptSessionFile();
            CurrentSessionPath = path;
            IsSessionChangedOnDisk = false;
            IsDirty = false;
            UpdateSettings(Settings.WithRecentSession(path));
            StatusMessage = $"Saved {DocumentName}";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ErrorMessage = $"Could not save session: {ex.Message}";
        }
    }

    private bool HasWatchItemsToExport() => WatchItems.Count > 0;

    [RelayCommand(CanExecute = nameof(HasWatchItemsToExport))]
    private async Task ExportWatchCsvAsync()
    {
        if (Dialogs is null || await Dialogs.PickCsvSaveTargetAsync($"{DocumentName}-watch") is not { } path)
        {
            return;
        }

        var csv = new StringBuilder("Name,NodeId,Value,Status,SourceTime\n");
        foreach (var item in WatchItems)
        {
            csv.AppendJoin(',', Csv(item.DisplayName), Csv(item.PortableId), Csv(item.Value), Csv(item.Status), Csv(item.SourceTimestamp)).Append('\n');
        }

        try
        {
            await File.WriteAllTextAsync(path, csv.ToString());
            StatusMessage = $"Exported {WatchItems.Count} item(s) to {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ErrorMessage = $"Export failed: {ex.Message}";
        }

        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    [RelayCommand(CanExecute = nameof(HasWatchItemsToExport))]
    private async Task ClearWatchAsync()
    {
        await RemoveWatchItemsAsync([.. WatchItems]);
    }

    public static readonly double[] ZoomSteps = [0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    public double UiScale => Math.Clamp(Settings.UiScale, ZoomSteps[0], ZoomSteps[^1]);

    [RelayCommand]
    private void ZoomIn() => SetZoom(ZoomSteps.FirstOrDefault(s => s > UiScale + 0.001, ZoomSteps[^1]));

    [RelayCommand]
    private void ZoomOut() => SetZoom(ZoomSteps.LastOrDefault(s => s < UiScale - 0.001, ZoomSteps[0]));

    [RelayCommand]
    private void ZoomReset() => SetZoom(1.0);

    private void SetZoom(double scale)
    {
        UpdateSettings(Settings with { UiScale = scale });
        OnPropertyChanged(nameof(UiScale));
        StatusMessage = $"Zoom {scale:P0}";
    }

    [RelayCommand]
    private void SetTheme(ThemePreference theme)
    {
        UpdateSettings(Settings with { Theme = theme });
        ApplyTheme(theme);
    }

    [RelayCommand]
    private void SetColorTheme(string? name)
    {
        var theme = ColorThemes.Find(name);
        UpdateSettings(Settings with { ColorTheme = theme.Name });
        ApplyColorTheme(theme.Name);
        StatusMessage = $"Colour theme {theme.Name}";
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (Dialogs is not null && await Dialogs.EditSettingsAsync(Settings) is { } updated)
        {
            UpdateSettings(updated);
            ApplyTheme(updated.Theme);
            ApplyColorTheme(updated.ColorTheme);
            StatusMessage = "Settings saved";
        }
        else
        {
            // The dialog previews themes live; cancelling returns to the saved one.
            ApplyTheme(Settings.Theme);
            ApplyColorTheme(Settings.ColorTheme);
        }
    }

    [RelayCommand]
    private void UseRecentEndpoint(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (!IsDisconnected)
        {
            StatusMessage = "Disconnect first to switch to another endpoint";
            return;
        }

        EndpointUrl = url;
    }

    [RelayCommand]
    private void RemoveRecentEndpoint(string? url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            UpdateSettings(Settings with { RecentEndpoints = [.. Settings.RecentEndpoints.Where(e => e != url)] });
        }
    }

    [RelayCommand]
    private void RemoveRecentSession(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            UpdateSettings(Settings with { RecentSessions = [.. Settings.RecentSessions.Where(p => p != path)] });
        }
    }

    [RelayCommand]
    private void ClearRecentSessions() => UpdateSettings(Settings with { RecentSessions = [] });

    [RelayCommand]
    private void ClearRecentEndpoints() => UpdateSettings(Settings with { RecentEndpoints = [] });

    [RelayCommand]
    private void ClearRecent() =>
        UpdateSettings(Settings with { RecentSessions = [], RecentEndpoints = [] });

    [RelayCommand]
    private Task ShowAboutAsync() => Dialogs?.ShowAboutAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void RevealCertificates() => Dialogs?.RevealInFileManager(Core.ClientPaths.PkiRoot);

    [RelayCommand]
    private void RevealSettings() => Dialogs?.RevealInFileManager(Path.GetDirectoryName(SettingsStore.DefaultPath)!);

    /// <summary>Reloads the shared settings (another connection tab may have changed recent lists or defaults).</summary>
    public void RefreshSettings()
    {
        if (_settingsStore is not null)
        {
            Settings = _settingsStore.Load();
        }
    }

    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty || Dialogs is null)
        {
            return true;
        }

        switch (await Dialogs.AskUnsavedChangesAsync(DocumentName))
        {
            case UnsavedChangesChoice.Save:
                await SaveSessionAsync();
                return !IsDirty;
            case UnsavedChangesChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    private async Task ResetAsync()
    {
        _suppressDirty = true;
        try
        {
            if (IsConnected)
            {
                await DisconnectAsync();
            }
            else
            {
                await StopAllMonitorsAsync();
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        ErrorMessage = null;
    }

    private void MarkDirty()
    {
        if (!_suppressDirty)
        {
            IsDirty = true;
        }
    }

    private void UpdateSettings(AppSettings settings)
    {
        Settings = settings;
        _settingsStore?.Save(settings);
    }

    internal static void ApplyColorTheme(string? name)
    {
        if (Application.Current is { } app)
        {
            ColorThemes.Apply(app, ColorThemes.Find(name));
        }
    }

    internal static void ApplyTheme(ThemePreference theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}

public sealed record RecentSessionEntry(string Path, string FileName, string? EndpointUrl)
{
    public string DisplayText => string.IsNullOrEmpty(EndpointUrl) ? FileName : $"{FileName} · {EndpointUrl}";
}
