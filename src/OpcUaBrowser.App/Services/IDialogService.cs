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

    /// <summary>Shows a server certificate the app does not trust and asks whether to trust it.</summary>
    Task<CertificateTrustChoice> AskTrustCertificateAsync(OpcUaBrowser.Core.ServerCertificate certificate, string endpointUrl);

    /// <summary>Opens a live Events &amp; Alarms window; closing it ends the subscription.</summary>
    void ShowEvents(ViewModels.EventsViewModel events);

    /// <summary>Opens the call form of a method.</summary>
    void ShowMethodCall(ViewModels.MethodCallViewModel method);

    /// <summary>Opens the snapshot comparison.</summary>
    void ShowSnapshotCompare(ViewModels.SnapshotCompareViewModel compare);

    /// <summary>Opens the live connection diagnostics.</summary>
    void ShowDiagnostics(ViewModels.DiagnosticsViewModel diagnostics);
}

public enum CertificateTrustChoice
{
    Cancel,
    Once,
    Always,
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
