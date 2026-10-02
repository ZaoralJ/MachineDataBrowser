namespace OpcUaBrowser.App.Services;

public interface IDialogService
{
    Task<string?> PickSessionToOpenAsync();

    Task<string?> PickSessionSaveTargetAsync(string suggestedName);

    Task<string?> PickCsvSaveTargetAsync(string suggestedName);

    Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string documentName);

    Task<AppSettings?> EditSettingsAsync(AppSettings current);

    Task ShowAboutAsync();

    void ShowRecordingViewer(ViewModels.RecordingViewerViewModel viewer);

    Task<string?> PickRecordingFileAsync();

    Task<OpcUaBrowser.Core.RecordingOptions?> EditNewRecordingAsync(ViewModels.NewRecordingDraft draft);

    /// <summary>Shows the recording form for an existing recording; returns the changed options or null.</summary>
    Task<OpcUaBrowser.Core.RecordingOptions?> EditRecordingSettingsAsync(ViewModels.NewRecordingViewModel form);

    Task<string?> PickExportTargetAsync(string suggestedName, string extension);

    void RevealInFileManager(string path);

    /// <summary>Asked when the watch list became empty while recordings still capture values.</summary>
    Task<ActiveRecordingsChoice> AskActiveRecordingsAsync(int count);

    /// <summary>Asks for a value to write to <paramref name="target"/>; null when cancelled.</summary>
    Task<string?> AskWriteValueAsync(string target, string currentValue);
}

public enum ActiveRecordingsChoice
{
    Keep,
    Stop,
    Close,
}

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}
