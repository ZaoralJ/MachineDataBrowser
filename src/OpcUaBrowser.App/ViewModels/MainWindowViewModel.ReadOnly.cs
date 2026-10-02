using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Read-only session: nothing is sent to the device that changes it (values, method calls, alarm
    /// acknowledgements). Browsing, monitoring, history and recordings work as usual. Saved with the session.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    [NotifyCanExecuteChangedFor(nameof(WriteAttributeValueCommand), nameof(WriteWatchValueCommand))]
    public partial bool IsReadOnly { get; set; }

    partial void OnIsReadOnlyChanged(bool value)
    {
        MarkDirty();
        foreach (var events in _eventViewers)
        {
            events.IsReadOnly = value;
        }

        foreach (var call in _methodCalls)
        {
            call.IsReadOnly = value;
        }

        StatusMessage = value
            ? "Read-only: writing values, calling methods and acknowledging alarms are turned off"
            : "Read-only off: writing values, calling methods and acknowledging alarms are allowed";
    }

    [RelayCommand]
    private void ToggleReadOnly() => IsReadOnly = !IsReadOnly;
}
