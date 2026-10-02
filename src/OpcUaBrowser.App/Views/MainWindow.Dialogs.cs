using System.Reflection;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.Views;

public sealed partial class MainWindow : IDialogService
{
    private static readonly FilePickerFileType SessionFileType = new("Machine Data Browser session")
    {
        Patterns = [$"*.{SessionDocument.FileExtension}"],
        AppleUniformTypeIdentifiers = ["public.json"],
        MimeTypes = ["application/json"],
    };

    private static readonly FilePickerFileType CsvFileType = new("CSV")
    {
        Patterns = ["*.csv"],
        AppleUniformTypeIdentifiers = ["public.comma-separated-values-text"],
        MimeTypes = ["text/csv"],
    };

    private bool _closeConfirmed;

    public async Task<string?> PickSessionToOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Session",
            AllowMultiple = false,
            FileTypeFilter = [SessionFileType, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSessionSaveTargetAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Session",
            SuggestedFileName = suggestedName,
            DefaultExtension = SessionDocument.FileExtension,
            FileTypeChoices = [SessionFileType],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickCsvSaveTargetAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Watch List",
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = [CsvFileType],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string documentName) =>
        await MessageDialog.ShowAsync(
            this,
            "Unsaved changes",
            $"Save changes to \"{documentName}\"?",
            "The endpoint, connection options or watch list changed since the session was last saved. Your changes will be lost if you don't save them."
                + (_closePromptOpenedAt is null ? string.Empty : $"\n\nTip: hold {(OperatingSystem.IsMacOS() ? "⌘Q" : "Ctrl+Q")} to quit without saving."),
            DialogIcon.Warning,
            new DialogButton<UnsavedChangesChoice>("Don't Save", UnsavedChangesChoice.Discard, DialogButtonRole.Destructive),
            new DialogButton<UnsavedChangesChoice>("Cancel", UnsavedChangesChoice.Cancel, DialogButtonRole.Cancel),
            new DialogButton<UnsavedChangesChoice>("Save", UnsavedChangesChoice.Save, DialogButtonRole.Default));

    public async Task<ActiveRecordingsChoice> AskActiveRecordingsAsync(int count) =>
        await MessageDialog.ShowAsync(
            this,
            "Recordings still running",
            count == 1 ? "Stop the running recording too?" : $"Stop the {count} running recordings too?",
            "The watch list is empty, but recordings keep capturing their items until they are stopped. Stop keeps the recorded history; Close discards it.",
            DialogIcon.Warning,
            new DialogButton<ActiveRecordingsChoice>(count == 1 ? "Close" : "Close All", ActiveRecordingsChoice.Close, DialogButtonRole.Destructive),
            new DialogButton<ActiveRecordingsChoice>("Keep Running", ActiveRecordingsChoice.Keep, DialogButtonRole.Cancel),
            new DialogButton<ActiveRecordingsChoice>(count == 1 ? "Stop" : "Stop All", ActiveRecordingsChoice.Stop, DialogButtonRole.Default));

    public async Task<CertificateTrustChoice> AskTrustCertificateAsync(ServerCertificate certificate, string endpointUrl)
    {
        var validity = $"{certificate.NotBefore.ToLocalTime():yyyy-MM-dd} – {certificate.NotAfter.ToLocalTime():yyyy-MM-dd}"
            + (certificate.IsExpired ? "  (not valid now)" : string.Empty);
        var body =
            $"{endpointUrl} presented a certificate this app does not trust yet. Check it with the server's administrator before trusting it.\n\n"
            + $"Subject:      {certificate.Subject}\n"
            + $"Issuer:       {certificate.Issuer}\n"
            + $"Valid:        {validity}\n"
            + $"Thumbprint:   {FormatThumbprint(certificate.Thumbprint)}\n\n"
            + $"Reason: {certificate.Problem}\n\n"
            + "Trust Once connects this time only. Always Trust adds it to the trusted certificates in the settings folder (pki/trusted).";
        return await MessageDialog.ShowAsync(
            this,
            "Untrusted server certificate",
            "Trust this server's certificate?",
            body,
            DialogIcon.Warning,
            new DialogButton<CertificateTrustChoice>("Cancel", CertificateTrustChoice.Cancel, DialogButtonRole.Cancel),
            new DialogButton<CertificateTrustChoice>("Always Trust", CertificateTrustChoice.Always),
            new DialogButton<CertificateTrustChoice>("Trust Once", CertificateTrustChoice.Once, DialogButtonRole.Default));

        static string FormatThumbprint(string hex) => string.Join(':', hex.Chunk(2).Select(c => new string(c)));
    }

    public async Task<string?> AskWriteValueAsync(string target, string currentValue)
    {
        var input = new TextBox { Text = currentValue, MinWidth = 320, AcceptsReturn = false, FontFamily = new Avalonia.Media.FontFamily("Menlo, Consolas, monospace") };
        var dialog = new Window
        {
            Title = "Write value",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = "Write", IsDefault = true, MinWidth = 80 };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => dialog.Close(input.Text ?? string.Empty);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"New value for {target}", FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Converted to the variable's data type. Arrays: comma-separated, e.g. [1, 2, 3].", TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
                input,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return await dialog.ShowDialog<string?>(this);
    }

    public Task<AppSettings?> EditSettingsAsync(AppSettings current) =>
        new SettingsWindow { DataContext = new SettingsViewModel(current) }.ShowDialog<AppSettings?>(this);

    public Task<RecordingOptions?> EditRecordingSettingsAsync(NewRecordingViewModel form) =>
        new NewRecordingWindow { DataContext = form }.ShowDialog<RecordingOptions?>(this);

    public Task<RecordingOptions?> EditNewRecordingAsync(NewRecordingDraft draft) =>
        new NewRecordingWindow { DataContext = new NewRecordingViewModel(draft) }.ShowDialog<RecordingOptions?>(this);

    public async Task<string?> PickExportTargetAsync(string suggestedName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export recording",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public void ShowRecordingViewer(RecordingViewerViewModel viewer) =>
        new RecordingViewerWindow { DataContext = viewer }.Show(this);

    public void ShowEvents(EventsViewModel events) =>
        new EventsWindow { DataContext = events }.Show(this);

    public void ShowMethodCall(MethodCallViewModel method) =>
        new MethodCallWindow { DataContext = method }.Show(this);

    public void ShowSnapshotCompare(SnapshotCompareViewModel compare) =>
        new SnapshotCompareWindow { DataContext = compare }.Show(this);

    public Task<MonitoringOptions?> EditMonitoringAsync(MonitoringOptions current, string target, int? refreshMs) =>
        new MonitoringSettingsWindow { DataContext = new MonitoringSettingsViewModel(current, target, refreshMs) }.ShowDialog<MonitoringOptions?>(this);

    public void ShowDiagnostics(DiagnosticsViewModel diagnostics) =>
        new DiagnosticsWindow { DataContext = diagnostics }.Show(this);

    public async Task<string?> PickRecordingFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Recording",
            AllowMultiple = false,
            FileTypeFilter = [CsvFileType, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task ShowAboutAsync()
    {
        var app = Assembly.GetExecutingAssembly();
        var version = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        var sdk = typeof(Opc.Ua.Client.Session).Assembly.GetName().Version;
        await MessageDialog.ShowAsync(
            this,
            "About",
            "Machine Data Browser",
            $"Version {version}\nOPC Foundation UA-.NETStandard {sdk}\n.NET {Environment.Version} · Avalonia {typeof(Window).Assembly.GetName().Version}",
            DialogIcon.Info,
            new DialogButton<bool>("OK", true, DialogButtonRole.Default));
    }

    /// <summary>All view shortcuts (registered by the views through <see cref="Shortcuts"/>) in one window.</summary>
    public void ShowShortcuts()
    {
        var window = new Window
        {
            Title = "Keyboard Shortcuts",
            Width = 560,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new ScrollViewer
            {
                Padding = new Avalonia.Thickness(20),
                Content = new SelectableTextBlock
                {
                    Text = "Shortcuts work while the view has focus. Menu shortcuts are listed in the menus.\n\n" + Shortcuts.Overview(),
                    FontFamily = (Avalonia.Media.FontFamily)this.FindResource("MonoFont")!,
                    FontSize = 12,
                },
            },
        };
        window.Show(this);
    }

    public void RevealInFileManager(string path)
    {
        Directory.CreateDirectory(path);
        _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    private static readonly TimeSpan HoldToQuit = TimeSpan.FromSeconds(1.5);
    private DateTime? _closePromptOpenedAt;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_shutDown || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        e.Cancel = true;
        var connections = Tabs.Tabs.Count > 0 ? [.. Tabs.Tabs.Select(t => t.Connection)] : new List<MainWindowViewModel> { vm };
        if (!_closeConfirmed && connections.Any(c => c.IsDirty))
        {
            if (_closePromptOpenedAt is { } openedAt)
            {
                if (DateTime.UtcNow - openedAt < HoldToQuit)
                {
                    // Quit requested again right away (the shortcut is held): quit without saving, but still gently.
                    _closeConfirmed = true;
                    foreach (var owned in OwnedWindows.ToList())
                    {
                        owned.Close();
                    }

                    await ShutDownGentlyAsync(connections);
                    return;
                }

                // Only one "unsaved changes" prompt: bring the open one to the front.
                if (OwnedWindows.Count > 0)
                {
                    OwnedWindows[0].Activate();
                }

                return;
            }

            _closePromptOpenedAt = DateTime.UtcNow;
            try
            {
                // One prompt per connection with unsaved changes, each shown while it is asked about.
                foreach (var connection in connections.Where(c => c.IsDirty).ToList())
                {
                    if (!ReferenceEquals(DataContext, connection))
                    {
                        DataContext = connection;
                    }

                    if (!await connection.ConfirmDiscardAsync() || _closeConfirmed)
                    {
                        return;
                    }
                }
            }
            finally
            {
                _closePromptOpenedAt = null;
            }

            _closeConfirmed = true;
        }

        await ShutDownGentlyAsync(connections);
    }

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private bool _shutDown;
    private bool _shuttingDown;

    /// <summary>
    /// Stops recordings (flushing live files), unsubscribes and closes the session before the window goes away, so a
    /// quit never leaves half-written files or dangling server sessions. Bounded by a timeout for unreachable servers.
    /// </summary>
    private async Task ShutDownGentlyAsync(IReadOnlyList<MainWindowViewModel> connections)
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        IsEnabled = false;
        (DataContext as MainWindowViewModel)?.ShowShutdownProgress();
        try
        {
            // All connections close in parallel, under one timeout for unreachable servers.
            await Task.WhenAll(connections.Select(c => c.ShutdownAsync())).WaitAsync(ShutdownTimeout);
        }
        catch (TimeoutException ex)
        {
            AppErrors.Log(ex, "shutdown timed out; quitting anyway");
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            AppErrors.Log(ex, "shutdown");
        }

        _shutDown = true;
        Close();
    }
}
