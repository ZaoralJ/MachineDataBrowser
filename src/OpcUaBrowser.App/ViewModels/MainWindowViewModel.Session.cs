using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string AppTitle = "OPC UA Browser";

    private readonly SettingsStore? _settingsStore;
    private bool _suppressDirty;

    public IDialogService? Dialogs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecentSessions), nameof(RecentEndpoints), nameof(HasRecentEndpoints), nameof(Theme), nameof(UiScale))]
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

    public IReadOnlyList<string> RecentEndpoints => Settings.RecentEndpoints;

    public bool HasRecentEndpoints => Settings.RecentEndpoints.Count > 0;

    public ThemePreference Theme => Settings.Theme;

    partial void OnEndpointUrlChanged(string value) => MarkDirty();

    partial void OnUseSecurityChanged(bool value) => MarkDirty();

    partial void OnAutoAcceptCertificatesChanged(bool value) => MarkDirty();

    partial void OnUserNameChanged(string value) => MarkDirty();


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
        AutoAcceptCertificates = false;
        UserName = string.Empty;
        DefaultRefreshMs = Settings.SamplingIntervalMs;
        WatchColumns.Reset();
        Password = string.Empty;
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
    }

    public async Task LoadSessionAsync(string path)
    {
        SessionDocument document;
        try
        {
            document = await SessionDocument.LoadAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            ErrorMessage = $"Could not open session: {ex.Message}";
            return;
        }

        await ResetAsync();
        _suppressDirty = true;
        EndpointUrl = document.EndpointUrl;
        UseSecurity = document.UseSecurity;
        AutoAcceptCertificates = document.AutoAcceptCertificates;
        UserName = document.UserName ?? string.Empty;
        DefaultRefreshMs = document.DefaultRefreshMs ?? Settings.SamplingIntervalMs;
        WatchColumns.SetSort(document.WatchSortColumn, document.WatchSortDescending);
        WatchColumns.Apply(document.WatchColumns);
        Password = string.Empty;
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

        var resolved = new List<(NodeId, string, int)>();
        var unresolved = new List<string>();
        foreach (var entry in document.Watch)
        {
            try
            {
                resolved.Add((_client.ParsePortableId(entry.NodeId), entry.DisplayName, entry.RefreshMs ?? DefaultRefreshMs));
            }
            catch (ServiceResultException)
            {
                unresolved.Add(entry.DisplayName);
            }
        }

        _suppressDirty = true;
        await AddWatchItemsAsync(resolved);
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
            EndpointUrl = EndpointUrl.Trim(),
            UseSecurity = UseSecurity,
            AutoAcceptCertificates = AutoAcceptCertificates,
            UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName,
            DefaultRefreshMs = DefaultRefreshMs,
            WatchColumns = WatchColumns.Capture(),
            WatchSortColumn = WatchColumns.SortColumn,
            WatchSortDescending = WatchColumns.SortDescending,
            Watch = [.. WatchItems.Select(w => new WatchEntry(w.PortableId, w.DisplayName, w.RefreshMs == DefaultRefreshMs ? null : w.RefreshMs))],
        };

        try
        {
            await document.SaveAsync(path);
            CurrentSessionPath = path;
            IsDirty = false;
            UpdateSettings(Settings.WithRecentSession(path));
            StatusMessage = $"Saved {DocumentName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Export failed: {ex.Message}";
        }

        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    [RelayCommand(CanExecute = nameof(HasWatchItemsToExport))]
    private async Task ClearWatchAsync()
    {
        var count = WatchItems.Count;
        await StopAllMonitorsAsync();
        StatusMessage = count == 1 ? "Removed 1 item from watch" : $"Removed {count} items from watch";
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
    private async Task OpenSettingsAsync()
    {
        if (Dialogs is not null && await Dialogs.EditSettingsAsync(Settings) is { } updated)
        {
            UpdateSettings(updated);
            ApplyTheme(updated.Theme);
            StatusMessage = "Settings saved";
        }
        else
        {
            // The dialog previews themes live; cancelling returns to the saved one.
            ApplyTheme(Settings.Theme);
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
