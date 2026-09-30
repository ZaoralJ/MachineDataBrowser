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
        if (!_closeConfirmed && vm.IsDirty)
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

                    await ShutDownGentlyAsync(vm);
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
                if (!await vm.ConfirmDiscardAsync() || _closeConfirmed)
                {
                    return;
                }
            }
            finally
            {
                _closePromptOpenedAt = null;
            }

            _closeConfirmed = true;
        }

        await ShutDownGentlyAsync(vm);
    }

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private bool _shutDown;
    private bool _shuttingDown;

    /// <summary>
    /// Stops recordings (flushing live files), unsubscribes and closes the session before the window goes away, so a
    /// quit never leaves half-written files or dangling server sessions. Bounded by a timeout for unreachable servers.
    /// </summary>
    private async Task ShutDownGentlyAsync(MainWindowViewModel vm)
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        IsEnabled = false;
        vm.ShowShutdownProgress();
        try
        {
            await vm.ShutdownAsync().WaitAsync(ShutdownTimeout);
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
