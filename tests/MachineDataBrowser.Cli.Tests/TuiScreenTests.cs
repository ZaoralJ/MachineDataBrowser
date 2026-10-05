using MachineDataBrowser.Cli.Tui;
using MachineDataBrowser.Core.Tests;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.Views;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

/// <summary>The full-screen browser drawn headlessly (ANSI driver, virtual time) against the custom simulator.</summary>
public sealed class TuiScreenTests(CustomTypesServerFixture custom) : IAsyncLifetime
{
    private readonly VirtualTimeProvider _time = new();
    private IDeviceClientHolder? _session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record IDeviceClientHolder(MachineDataBrowser.Core.IDeviceClient Client, BrowserModel Model, IApplication App, TuiApp Tui);

    public async ValueTask InitializeAsync()
    {
        var args = new ConnectionArgs(custom.EndpointUrl, null, null, false, true);
        var client = await Connection.ConnectAsync(args, Ct);
        var model = new BrowserModel(client, args, 100);
        var app = Application.Create(_time).Init(DriverRegistry.Names.ANSI);
        var tui = new TuiApp(app, model);
        tui.Start();
        app.Begin(tui.Window);
        _session = new IDeviceClientHolder(client, model, app, tui);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } s)
        {
            s.Tui.Dispose();
            s.App.Dispose();
            await s.Model.DisposeAsync();
            await s.Client.DisposeAsync();
        }
    }

    private IApplication App => _session!.App;

    private string Screen => App.Driver!.ToString() ?? string.Empty;

    /// <summary>Runs timers and the callbacks device work posted back, then draws, until the screen shows what's expected.</summary>
    private async Task<string> UntilScreen(Func<string, bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            App.TimedEvents!.RunTimers();
            App.LayoutAndDraw(true);
            var screen = Screen;
            if (condition(screen))
            {
                return screen;
            }

            Assert.True(DateTime.UtcNow < deadline, $"screen never matched:\n{screen}");
            await Task.Delay(50, Ct);
        }
    }

    private void Press(Key key)
    {
        App.InjectKey(key);
        App.LayoutAndDraw(true);
    }

    [Fact]
    public async Task Small_terminal_keeps_every_pane_and_the_tree_loads()
    {
        App.Driver!.SetScreenSize(80, 25);
        var screen = await UntilScreen(s => s.Contains("Objects", StringComparison.Ordinal));

        foreach (var pane in new[] { "¹Address Space", "²Attributes", "³Monitored Items", "⁴Trend", "⁵Info" })
        {
            Assert.Contains(pane, screen, StringComparison.Ordinal);
        }

        Assert.Contains("mdbrowser", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("loading…", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_title_names_the_endpoint()
    {
        await UntilScreen(s => s.Contains("Objects", StringComparison.Ordinal));

        Assert.Equal($"mdbrowser {custom.EndpointUrl}", _session!.Tui.TerminalTitle);
    }

    private void Mouse(MouseFlags flags, int x, int y)
    {
        App.InjectMouse(new Mouse { Flags = flags, ScreenPosition = new System.Drawing.Point(x, y) });
        App.LayoutAndDraw(true);
    }

    private void Drag(int fromX, int fromY, int toX, int toY)
    {
        Mouse(MouseFlags.LeftButtonPressed, fromX, fromY);
        Mouse(MouseFlags.LeftButtonPressed | MouseFlags.PositionReport, toX, toY);
        Mouse(MouseFlags.LeftButtonReleased, toX, toY);
    }

    [Fact]
    public async Task Dragging_pane_borders_resizes_the_panes()
    {
        App.Driver!.SetScreenSize(100, 30);
        await UntilScreen(s => s.Contains("Objects", StringComparison.Ordinal));
        var tree = _session!.Tui.Window.SubViews.OfType<FrameView>().Single(f => f.Title.Contains("Address Space", StringComparison.Ordinal));
        var info = _session.Tui.Window.SubViews.OfType<FrameView>().Single(f => f.Title.Contains("Info", StringComparison.Ordinal));

        Drag(tree.Frame.Right - 1, 5, 59, 5);
        Assert.Equal(60, tree.Frame.Width);

        var infoHeight = info.Frame.Height;
        Drag(10, info.Frame.Y, 10, info.Frame.Y - 4);
        Assert.Equal(infoHeight + 4, info.Frame.Height);
        Assert.Equal(29, info.Frame.Bottom);
    }

    [Fact]
    public async Task Names_with_underscores_show_as_they_are_in_titles()
    {
        App.Driver!.SetScreenSize(150, 45);
        var model = _session!.Model;
        var counter = (await model.SearchAsync("Every10ms", cancellationToken: Ct)).Hits.Single(h => h.Item.DisplayName == "Every10ms");
        var entry = await model.RevealAsync(counter.Path, Ct);
        var id = (await model.LoadChildrenAsync(entry!, cancellationToken: Ct)).Single(c => c.Item.DisplayName == "Counter").Item.NodeId;
        await model.MonitorNodesAsync([(new Node(id, "Bulk_0014", "ns=2;i=76"), 100)], Ct);

        // Terminal.Gui would read '_' as a hotkey marker and show "Bulk0014".
        var screen = await UntilScreen(s => s.Contains("⁴Trend ┤ Bulk", StringComparison.Ordinal));
        Assert.Contains("⁴Trend ┤ Bulk_0014 ├", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitor_hide_panes_pause_sort_and_refresh_from_the_keyboard()
    {
        App.Driver!.SetScreenSize(150, 45);
        var model = _session!.Model;
        var hit = (await model.SearchAsync("Every10ms", cancellationToken: Ct)).Hits.Single(h => h.Item.DisplayName == "Every10ms");
        _session.Tui.Reveal(hit.Path);
        await UntilScreen(s => s.Contains("Every10ms", StringComparison.Ordinal) && s.Contains("NodeId", StringComparison.Ordinal));

        // m on a folder monitors the variables below it; values, status and the trend appear.
        Press(Key.M);
        var screen = await UntilScreen(s => s.Contains("Counter", StringComparison.Ordinal) && s.Contains("Good", StringComparison.Ordinal) && s.Contains("⁴Trend ┤", StringComparison.Ordinal));
        Assert.Contains("Monitored Items (5)", screen, StringComparison.Ordinal);
        Assert.Contains("Monitoring 5 items: Counter (ns=2;i=76)", screen, StringComparison.Ordinal);

        // i shows the NodeId column (hidden at first; the id is then in the table as well as in the log).
        int Ids(string s) => s.Split("ns=2;i=77").Length - 1;
        var before = Ids(screen);
        Press(Key.I);
        screen = await UntilScreen(s => Ids(s) > before);
        Press(Key.I);
        await UntilScreen(s => Ids(s) == before);

        // 4 hides the trend, and the monitored items take its room; 4 again brings it back.
        Press(new Key('4'));
        screen = await UntilScreen(s => !s.Contains("⁴Trend", StringComparison.Ordinal));
        Press(new Key('4'));
        await UntilScreen(s => s.Contains("⁴Trend", StringComparison.Ordinal));

        // Shift+3 (# in a terminal): monitored items alone on the screen; again restores every pane.
        Press(new Key('#'));
        screen = await UntilScreen(s => !s.Contains("¹Address Space", StringComparison.Ordinal));
        Assert.Contains("³Monitored Items", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("⁵Info", screen, StringComparison.Ordinal);
        Press(new Key('#'));
        screen = await UntilScreen(s => s.Contains("¹Address Space", StringComparison.Ordinal));
        Assert.Contains("⁵Info", screen, StringComparison.Ordinal);

        Press(Key.O);
        Press(Key.P);
        screen = await UntilScreen(s => s.Contains("PAUSED", StringComparison.Ordinal));
        Assert.Contains("sort: Name ↑", screen, StringComparison.Ordinal);

        // + steps the refresh time of every monitored item: 100 ms → 250 ms.
        Press(new Key('+'));
        await UntilScreen(s => s.Contains("- 250 ms +", StringComparison.Ordinal));
        Assert.All(model.Watch, r => Assert.Equal(250, r.RefreshMs));

        // The trend shows the last 30 samples at first; ] shows more; x resets it.
        Press(Key.P);
        await UntilScreen(s => s.Contains("of 30 ·", StringComparison.Ordinal) || s.Contains("window 30", StringComparison.Ordinal));
        Press(new Key(']'));
        await UntilScreen(s => s.Contains("of 60 ·", StringComparison.Ordinal) || s.Contains("window 60", StringComparison.Ordinal));
        Press(Key.X);
        await UntilScreen(s => s.Contains("reset.", StringComparison.Ordinal));
    }
}
