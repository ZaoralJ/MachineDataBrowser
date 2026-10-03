using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

public sealed record DiagnosticsRow(string Name, string Value);

public sealed record SubscriptionRow(SubscriptionDiagnostics Subscription)
{
    public string Name => Subscription.Name;

    public string Interval => Subscription.PublishingIntervalMs.ToString("0", CultureInfo.InvariantCulture) + " ms";

    public uint Items => Subscription.MonitoredItems;

    public long Notifications => Subscription.Notifications;

    public string LastNotification => Subscription.LastNotification is { } t ? Timestamps.Format(t) : "never";

    public string KeepAlive => $"{Subscription.KeepAliveCount} / lifetime {Subscription.LifetimeCount}";

    public string Publishing => Subscription.PublishingEnabled ? "On" : "Off";
}

/// <summary>Live connection details: refreshed every second while the window is open.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly Func<IDeviceClient> _client;
    private readonly Func<string> _endpoint;
    private readonly IReadOnlyCollection<WatchItemViewModel> _watch;
    private readonly DispatcherTimer _timer;
    private long _lastUpdates = -1;
    private DateTime _lastSample;
    private Task? _refresh;

    public DiagnosticsViewModel(Func<IDeviceClient> client, Func<string> endpoint, IReadOnlyCollection<WatchItemViewModel> watch)
    {
        _client = client;
        _endpoint = endpoint;
        _watch = watch;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await RefreshAsync());
        _timer.Start();
    }

    public ObservableCollection<DiagnosticsRow> Session { get; } = [];

    public ObservableCollection<DiagnosticsRow> App { get; } = [];

    public ObservableCollection<SubscriptionRow> Subscriptions { get; } = [];

    [ObservableProperty]
    public partial bool HasSubscriptions { get; private set; }

    [ObservableProperty]
    public partial string UpdatedText { get; private set; } = string.Empty;

    /// <summary>Refreshes once; a call while a refresh is running waits for that one.</summary>
    public Task RefreshAsync() => _refresh is { IsCompleted: false } running ? running : _refresh = RefreshCoreAsync();

    private async Task RefreshCoreAsync()
    {
        {
            var client = _client();
            ConnectionDiagnostics? details = null;
            string? error = null;
            if (client.State == ConnectionState.Connected && client is IConnectionDiagnosticsSource source)
            {
                try
                {
                    details = await Task.Run(() => source.GetDiagnosticsAsync());
                }
                catch (Exception ex) when (AppErrors.IsRecoverable(ex))
                {
                    error = AppErrors.Describe(ex);
                }
            }

            Replace(Session, details?.Session.Select(p => new DiagnosticsRow(p.Name, p.Value))
                ?? [
                    new DiagnosticsRow("Endpoint", _endpoint()),
                    new DiagnosticsRow("State", client.State.ToString()),
                    new DiagnosticsRow("Server", client.ServerUri ?? string.Empty),
                    new DiagnosticsRow("Details", error ?? "Connect to see the connection details."),
                ]);
            Replace(Subscriptions, details?.Subscriptions.Select(s => new SubscriptionRow(s)) ?? []);
            HasSubscriptions = Subscriptions.Count > 0;
            Replace(App, AppRows(client));
            UpdatedText = $"Updated {Timestamps.Format(DateTimeOffset.Now)} · refreshes every second";
        }
    }

    private IEnumerable<DiagnosticsRow> AppRows(IDeviceClient client)
    {
        var items = _watch.ToList();
        var updates = items.Sum(w => (long)w.UpdateCount);
        var now = DateTime.UtcNow;
        string rate;
        if (_lastUpdates < 0)
        {
            rate = "measuring…";
        }
        else
        {
            var perSecond = Math.Max(0, updates - _lastUpdates) / Math.Max(0.001, (now - _lastSample).TotalSeconds);
            rate = perSecond.ToString("0.#", CultureInfo.InvariantCulture) + " / s";
        }

        _lastUpdates = updates;
        _lastSample = now;
        yield return new DiagnosticsRow("Watched items", $"{items.Count} ({items.Count(w => w.Monitor is not null)} monitored)");
        yield return new DiagnosticsRow("Value updates", $"{rate} · {updates:N0} since added");
        yield return new DiagnosticsRow("Stale / bad", $"{items.Count(w => w.IsStale)} stale · {items.Count(w => w.IsBad)} bad · {items.Count(w => w.IsUncertain)} uncertain");
        yield return new DiagnosticsRow("Protocol", client.GetType().Name.Replace("DeviceClient", string.Empty, StringComparison.Ordinal).Replace("Client", string.Empty, StringComparison.Ordinal));
    }

    /// <summary>Updates in place, so the grids don't flicker or lose their scroll position every second.</summary>
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        var list = rows.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (i < target.Count)
            {
                if (!Equals(target[i], list[i]))
                {
                    target[i] = list[i];
                }
            }
            else
            {
                target.Add(list[i]);
            }
        }

        while (target.Count > list.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    public void Dispose() => _timer.Stop();
}
