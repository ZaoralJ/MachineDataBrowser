using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.Tests;

/// <summary>Dialog service for tests: answers are preset, questions are recorded.</summary>
public class TestDialogs : IDialogService
{
    public RecordingViewerViewModel? Viewer { get; set; }

    public void ShowRecordingViewer(RecordingViewerViewModel viewer) => Viewer = viewer;

    public Task<string?> PickSessionToOpenAsync() => Task.FromResult<string?>(null);

    public string? SessionPasswordAnswer { get; set; }

    public (string UserName, string EndpointUrl)? SessionPasswordAsked { get; private set; }

    public int SessionPasswordAskCount { get; private set; }

    public Task<string?> AskSessionPasswordAsync(string userName, string endpointUrl)
    {
        SessionPasswordAsked = (userName, endpointUrl);
        SessionPasswordAskCount++;
        return Task.FromResult(SessionPasswordAnswer);
    }

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

    public string? WriteAnswer { get; set; }

    public Task<string?> AskWriteValueAsync(string target, string currentValue) => Task.FromResult(WriteAnswer);

    public string? SnapshotNameAnswer { get; set; }

    public (string Suggested, int Items)? SnapshotNameAsked { get; private set; }

    public Task<string?> AskSnapshotNameAsync(string suggestedName, int itemCount)
    {
        SnapshotNameAsked = (suggestedName, itemCount);
        return Task.FromResult(SnapshotNameAnswer);
    }

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

    public string? SnapshotExportAnswer { get; set; }

    public Task<string?> PickSnapshotExportTargetAsync(string suggestedName) => Task.FromResult(SnapshotExportAnswer);

    public string? SnapshotImportAnswer { get; set; }

    public Task<string?> PickSnapshotFileAsync() => Task.FromResult(SnapshotImportAnswer);

    public string? TagMapAnswer { get; set; }

    public Task<string?> PickTagMapFileAsync() => Task.FromResult(TagMapAnswer);

    public DiagnosticsViewModel? Diagnostics { get; private set; }

    public void ShowDiagnostics(DiagnosticsViewModel diagnostics) => Diagnostics = diagnostics;

    public MonitoringOptions? MonitoringAnswer { get; set; }

    public Task<MonitoringOptions?> EditMonitoringAsync(MonitoringOptions current, string target, int? refreshMs) => Task.FromResult(MonitoringAnswer);

    public ValueDisplay? DisplayAnswer { get; set; }

    public Task<ValueDisplay?> EditDisplayAsync(ValueDisplay current, string target, WatchItemViewModel sample) => Task.FromResult(DisplayAnswer);

    public Task<ActiveRecordingsChoice> AskActiveRecordingsAsync(int count)
    {
        RecordingsAsked++;
        return Task.FromResult(RecordingsAnswer);
    }
}
