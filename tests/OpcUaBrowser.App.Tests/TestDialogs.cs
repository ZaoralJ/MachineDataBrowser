using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.Tests;

/// <summary>Dialog service for tests: answers are preset, questions are recorded.</summary>
public class TestDialogs : IDialogService
{
    public RecordingViewerViewModel? Viewer { get; set; }

    public void ShowRecordingViewer(RecordingViewerViewModel viewer) => Viewer = viewer;

    public Task<string?> PickSessionToOpenAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickSessionSaveTargetAsync(string suggestedName) => Task.FromResult<string?>(null);

    public Task<string?> PickCsvSaveTargetAsync(string suggestedName) => Task.FromResult<string?>(null);

    public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string documentName) => Task.FromResult(UnsavedChangesChoice.Discard);

    public Task<AppSettings?> EditSettingsAsync(AppSettings current) => Task.FromResult<AppSettings?>(null);

    public Task ShowAboutAsync() => Task.CompletedTask;

    public Task<string?> PickRecordingFileAsync() => Task.FromResult<string?>(null);

    public Task<RecordingOptions?> EditNewRecordingAsync(NewRecordingDraft draft) => Task.FromResult<RecordingOptions?>(null);

    public Task<string?> PickExportTargetAsync(string suggestedName, string extension) => Task.FromResult<string?>(null);

    public void RevealInFileManager(string path)
    {
    }

    public RecordingOptions? SettingsAnswer { get; set; }

    public Task<RecordingOptions?> EditRecordingSettingsAsync(NewRecordingViewModel form) => Task.FromResult(SettingsAnswer);

    public ActiveRecordingsChoice RecordingsAnswer { get; set; } = ActiveRecordingsChoice.Keep;

    public int RecordingsAsked { get; private set; }

    public Task<string?> AskWriteValueAsync(string target, string currentValue) => Task.FromResult<string?>(null);

    public CertificateTrustChoice TrustAnswer { get; set; } = CertificateTrustChoice.Cancel;

    public ServerCertificate? TrustAsked { get; private set; }

    public Task<CertificateTrustChoice> AskTrustCertificateAsync(ServerCertificate certificate, string endpointUrl)
    {
        TrustAsked = certificate;
        return Task.FromResult(TrustAnswer);
    }

    public List<EventsViewModel> EventWindows { get; } = [];

    public void ShowEvents(EventsViewModel events) => EventWindows.Add(events);

    public MethodCallViewModel? MethodCall { get; private set; }

    public void ShowMethodCall(MethodCallViewModel method) => MethodCall = method;

    public SnapshotCompareViewModel? Compare { get; private set; }

    public void ShowSnapshotCompare(SnapshotCompareViewModel compare) => Compare = compare;

    public DiagnosticsViewModel? Diagnostics { get; private set; }

    public void ShowDiagnostics(DiagnosticsViewModel diagnostics) => Diagnostics = diagnostics;

    public MonitoringOptions? MonitoringAnswer { get; set; }

    public Task<MonitoringOptions?> EditMonitoringAsync(MonitoringOptions current, string target, int? refreshMs) => Task.FromResult(MonitoringAnswer);

    public Task<ActiveRecordingsChoice> AskActiveRecordingsAsync(int count)
    {
        RecordingsAsked++;
        return Task.FromResult(RecordingsAnswer);
    }
}
