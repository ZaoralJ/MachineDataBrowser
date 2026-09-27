using System.Reflection;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.Views;

public sealed partial class MainWindow : IDialogService
{
    private static readonly FilePickerFileType SessionFileType = new("OPC UA Browser session")
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
            "The endpoint, connection options or watch list changed since the session was last saved. Your changes will be lost if you don't save them.",
            DialogIcon.Warning,
            new DialogButton<UnsavedChangesChoice>("Don't Save", UnsavedChangesChoice.Discard, DialogButtonRole.Destructive),
            new DialogButton<UnsavedChangesChoice>("Cancel", UnsavedChangesChoice.Cancel, DialogButtonRole.Cancel),
            new DialogButton<UnsavedChangesChoice>("Save", UnsavedChangesChoice.Save, DialogButtonRole.Default));

    public Task<AppSettings?> EditSettingsAsync(AppSettings current) =>
        new SettingsWindow { DataContext = new SettingsViewModel(current) }.ShowDialog<AppSettings?>(this);

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
            "OPC UA Browser",
            $"Version {version}\nOPC Foundation UA-.NETStandard {sdk}\n.NET {Environment.Version} · Avalonia {typeof(Window).Assembly.GetName().Version}",
            DialogIcon.Info,
            new DialogButton<bool>("OK", true, DialogButtonRole.Default));
    }

    public void RevealInFileManager(string path)
    {
        Directory.CreateDirectory(path);
        _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not MainWindowViewModel { IsDirty: true } vm)
        {
            return;
        }

        e.Cancel = true;
        if (await vm.ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
