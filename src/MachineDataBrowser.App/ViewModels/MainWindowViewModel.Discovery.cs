using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>MQTT: discovery (subscribing to the whole topic filter) can be paused on busy brokers.</summary>
    public bool SupportsDiscoveryPause => _client is IPausableDiscovery;

    public bool IsDiscoveryPaused => _client is IPausableDiscovery { IsDiscoveryPaused: true };

    public bool IsMqttEndpoint => DeviceClient.IsMqtt(EndpointUrl ?? string.Empty);

    /// <summary>Pause discovery this many seconds after connecting (0 = never). Saved with the session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OptionsSummary))]
    public partial int AutoPauseDiscoverySeconds { get; set; }

    partial void OnAutoPauseDiscoverySecondsChanged(int value) => MarkDirty();

    /// <summary>Client thread: the pause can come from the automatic timer, not only from the command.</summary>
    private void OnDiscoveryPausedChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sender, _client))
            {
                return;
            }

            OnPropertyChanged(nameof(IsDiscoveryPaused));
            OnPropertyChanged(nameof(DiscoveryToolTip));
            if (IsDiscoveryPaused && !_discoveryChangedByUser)
            {
                StatusMessage = $"Discovery paused automatically after {AutoPauseDiscoverySeconds} s: only monitored topics are received (⇧⌘P resumes)";
            }

            _discoveryChangedByUser = false;
        });

    /// <summary>Set by the command, so its own change isn't reported as the automatic pause.</summary>
    private bool _discoveryChangedByUser;

    public string DiscoveryToolTip => IsDiscoveryPaused
        ? "Resume discovery: receive the whole topic filter again, so new topics appear"
        : "Pause discovery: receive only the monitored topics instead of the whole topic filter. Saves traffic on busy "
            + "brokers; new topics don't appear while paused.";

    private bool CanToggleDiscovery() => IsConnected && SupportsDiscoveryPause;

    [RelayCommand(CanExecute = nameof(CanToggleDiscovery))]
    private async Task ToggleDiscoveryAsync()
    {
        if (_client is not IPausableDiscovery discovery)
        {
            return;
        }

        var pause = !discovery.IsDiscoveryPaused;
        _discoveryChangedByUser = true;
        try
        {
            await Task.Run(() => discovery.SetDiscoveryPausedAsync(pause));
            StatusMessage = pause
                ? "Discovery paused: only monitored topics are received, new topics don't appear"
                : "Discovery resumed: receiving the whole topic filter again";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            ReportError(ex);
        }

        OnPropertyChanged(nameof(IsDiscoveryPaused));
        OnPropertyChanged(nameof(DiscoveryToolTip));
    }
}
