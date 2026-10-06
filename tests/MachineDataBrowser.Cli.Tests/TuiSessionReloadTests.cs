using MachineDataBrowser.Cli.Tui;
using MachineDataBrowser.Core.Tests;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Time;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>The TUI follows its session file when someone else changes it.</summary>
public sealed class TuiSessionReloadTests(CustomTypesServerFixture custom) : IAsyncLifetime
{
    private readonly VirtualTimeProvider _time = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-tui-reload").FullName;
    private readonly List<string> _opened = [];
    private MachineDataBrowser.Core.IDeviceClient? _client;
    private ConnectionArgs? _args;
    private IApplication? _app;
    private TuiApp? _tui;
    private BrowserModel? _model;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SessionPath => Path.Combine(_dir, "line.mdbsession");

    public async ValueTask InitializeAsync()
    {
        await File.WriteAllTextAsync(SessionPath, $"{{\"endpointUrl\":\"{custom.EndpointUrl}\",\"watch\":[]}}", Ct);
        _args = new ConnectionArgs(custom.EndpointUrl, null, null, false, true);
        _client = await Connection.ConnectAsync(_args, Ct);
        _model = new BrowserModel(_client, _args, 100, SessionPath);
        _model.MarkSessionSaved();
        _app = Application.Create(_time).Init(DriverRegistry.Names.ANSI);
        _tui = new TuiApp(_app, _model, openSession: path =>
        {
            lock (_opened)
            {
                _opened.Add(path);
            }

            var reopened = new BrowserModel(_client, _args, 100, path);
            reopened.MarkSessionSaved();
            return Task.FromResult(reopened);
        }, layouts: new TuiLayouts(Path.Combine(_dir, "layouts.json")));
        _tui.Start();
        _app.Begin(_tui.Window);
    }

    public async ValueTask DisposeAsync()
    {
        _tui?.Dispose();
        _app?.Dispose();
        if (_model is not null)
        {
            await _model.DisposeAsync();
        }

        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        Directory.Delete(_dir, recursive: true);
    }

    private int Opened
    {
        get
        {
            lock (_opened)
            {
                return _opened.Count;
            }
        }
    }

    private async Task Pump(Func<bool> until, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!until() && DateTime.UtcNow < deadline)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            _app!.TimedEvents!.RunTimers();
            _app.LayoutAndDraw(true);
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task A_session_changed_elsewhere_is_reopened()
    {
        await File.WriteAllTextAsync(SessionPath, $"{{\"endpointUrl\":\"{custom.EndpointUrl}\",\"watch\":[],\"defaultRefreshMs\":500}}", Ct);

        await Pump(() => Opened > 0);

        Assert.Equal([SessionPath], _opened);
    }

    [Fact]
    public async Task Its_own_save_does_not_reopen_it()
    {
        await _model!.SaveSessionAsync(SessionPath, Ct);

        await Pump(() => Opened > 0, seconds: 2);

        Assert.Empty(_opened);
    }
}
