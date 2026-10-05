using System.Collections.ObjectModel;
using System.Globalization;
using MachineDataBrowser.Core;
using Opc.Ua;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MachineDataBrowser.Cli.Tui;

/// <summary>Rows for a <see cref="TableView"/>, replaced as a whole on every refresh.</summary>
internal sealed class GridSource(string[] columns) : ITableSource
{
    private IReadOnlyList<string[]> _rows = [];

    public string[] ColumnNames { get; } = columns;

    public int Columns => ColumnNames.Length;

    public int Rows => _rows.Count;

    public object this[int row, int col] => _rows[row][col];

    public void SetRows(IReadOnlyList<string[]> rows) => _rows = rows;
}

/// <summary>
/// The full-screen browser (<c>mdbrowser tui</c>): address space, attributes, monitored items and an info log, like
/// opcua-commander, for every protocol. Everything it does goes through <see cref="BrowserModel"/>; this class only
/// draws and turns keys into calls. Device work runs off the UI thread and reports back through <see cref="Ui"/>.
/// </summary>
internal sealed class TuiApp : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The refresh times - and + step through; 0 (every message) only for MQTT.</summary>
    private static readonly int[] RefreshSteps = [0, 50, 100, 250, 500, 1000, 2000, 5000, 10_000];

    /// <summary>
    /// Info and trend take a share of the height, so monitored items keep room on a small terminal; a dragged border
    /// replaces the share, but never squeezes the other panes away.
    /// </summary>
    private int InfoHeight => Math.Clamp(_infoHeight ?? Math.Clamp(_app.Screen.Height / 6, 3, 8), 3, Math.Max(3, _app.Screen.Height - 8));

    private int ChartHeight => Math.Clamp(_chartHeight ?? Math.Clamp(_app.Screen.Height / 4, 4, 10), 3, Math.Max(3, _app.Screen.Height - 10));

    private enum Splitter
    {
        None,
        Tree,
        Attributes,
        Chart,
        Info,
    }

    /// <summary>Sizes set by dragging a pane border with the mouse; null is the default share.</summary>
    private int? _treeWidth;
    private int? _attributesHeight;
    private int? _chartHeight;
    private int? _infoHeight;
    private Splitter _dragging;
    private readonly TuiLayouts _layouts;

    private static readonly string[] SortColumns = ["Name", "Value", "Status", "Updated", "Updates"];

    private readonly IApplication _app;
    private BrowserModel _model;
    private readonly Func<string, Task<BrowserModel>>? _openSession;
    private readonly Window _window;
    private readonly HeaderBar _header;
    private readonly TreeView<TreeEntry> _tree;
    private readonly GridSource _attributeRows = new(["Attribute", "Value"]);
    private readonly TableView _attributes;
    private readonly GridSource _watchRows = new(["Name", "Value", "Status", "Updated", "Updates", "Id"]);
    private readonly TableView _watch;
    private readonly TrendChart _chart;
    private readonly ObservableCollection<string> _logLines = [];
    private readonly ListView _log;
    private readonly FrameView _treeFrame;
    private readonly FrameView _attributesFrame;
    private readonly FrameView _watchFrame;
    private readonly FrameView _chartFrame;
    private readonly FrameView _logFrame;
    private readonly FrameView[] _panes;
    private readonly KeyBar _status;
    private readonly Dictionary<TreeEntry, TreeEntry> _loadingPlaceholders = [];
    private readonly Dictionary<Key, Action> _extraKeys = [];
    private List<WatchRow> _shown = [];
    private List<RowSnapshot> _shownSnapshots = [];
    private string _filter = string.Empty;
    private int _sortColumn = -1;
    private bool _sortDescending;
    private bool _paused;
    private int _shownLogCount;
    private int _attributeRequest;
    private bool _treeDirty;
    private string? _terminalTitle;

    public TuiApp(IApplication app, BrowserModel model, ColorTheme? theme = null, bool light = false, Func<string, Task<BrowserModel>>? openSession = null, TuiLayouts? layouts = null)
    {
        _app = app;
        _model = model;
        _openSession = openSession;
        _layouts = layouts ?? new TuiLayouts();
        Theme.Apply(theme ?? ColorThemeCatalog.Find(null), light);

        _window = new Window { BorderStyle = global::Terminal.Gui.Drawing.LineStyle.None };
        _header = new HeaderBar { X = 0, Y = 0, Width = Dim.Fill() };

        _treeFrame = new FrameView { Title = "¹Address Space" };
        _tree = new TreeView<TreeEntry>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            TreeBuilder = new DelegateTreeBuilder<TreeEntry>(Children, e => e.Item.HasChildren),
            AspectGetter = Label,
            // Letters are the commands (m, w, q, …), not type-to-find.
            AllowLetterBasedNavigation = false,
        };
        _tree.AddObject(model.Root);
        _tree.SelectionChanged += (_, e) => ShowAttributes(e.NewValue);
        _tree.ColorGetter = TreeColor;
        _tree.Style.ShowBranchLines = false;
        _tree.Style.ExpandableSymbol = new System.Text.Rune('▸');
        _tree.Style.CollapseableSymbol = new System.Text.Rune('▾');
        _treeFrame.Add(_tree);

        _attributesFrame = new FrameView { Title = "²Attributes" };
        _attributes = new TableView(_attributeRows) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true, CollectionNavigator = null };
        Theme.Table(_attributes);
        _attributesFrame.Add(_attributes);

        _watchFrame = new FrameView { Title = "³Monitored Items" };
        _watch = new TableView(_watchRows) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true, CollectionNavigator = null };
        Theme.Table(_watch);
        _watch.Style.RowColorGetter = args => RowScheme(args.RowIndex);
        // Value stands out, status in its colour, time, count and id recede.
        // Long names and JSON payloads are cut, so status, time and count stay on screen. Value stands out, status in its
        // colour, time, count and id recede; the id column shows with i.
        _watch.Style.ColumnStyles[0] = new ColumnStyle { MaxWidth = 28, ColorGetter = args => RowScheme(args.RowIndex) ?? Theme.Colored(Theme.Foreground) };
        _watch.Style.ColumnStyles[1] = new ColumnStyle { MaxWidth = 30, ColorGetter = args => RowScheme(args.RowIndex) ?? Theme.Colored(Theme.Bright, TextStyle.Bold) };
        _watch.Style.ColumnStyles[2] = new ColumnStyle { ColorGetter = args => StatusScheme(args.RowIndex) };
        _watch.Style.ColumnStyles[3] = new ColumnStyle { ColorGetter = _ => Theme.Colored(Theme.Muted) };
        _watch.Style.ColumnStyles[4] = new ColumnStyle { ColorGetter = _ => Theme.Colored(Theme.Muted) };
        _watch.Style.ColumnStyles[5] = new ColumnStyle { Visible = false, ColorGetter = _ => Theme.Colored(Theme.Dim) };
        _watchFrame.Add(_watch);

        _chartFrame = new FrameView { Title = "⁴Trend" };
        _chart = new TrendChart { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _chartFrame.Add(_chart);

        _logFrame = new FrameView { Title = "⁵Info" };
        _log = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _log.SetSource(_logLines);
        _logFrame.Add(_log);
        _panes = [_treeFrame, _attributesFrame, _watchFrame, _chartFrame, _logFrame];
        _window.SetScheme(Theme.Base);
        Theme.Pane(_treeFrame, _tree);
        Theme.Pane(_attributesFrame, _attributes);
        Theme.Pane(_watchFrame, _watch);
        Theme.Pane(_chartFrame, _chart);
        Theme.Pane(_logFrame, _log, Theme.Quiet);

        _status = new KeyBar([]) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
        ConfigureKeys();

        _window.Add(_header, _treeFrame, _attributesFrame, _watchFrame, _chartFrame, _logFrame, _status);
        // The TUI's keys work whichever pane has the focus (and with the kitty keyboard protocol, as in Warp); not while
        // a dialog is open, so typing in a text field stays typing.
        _app.Keyboard.KeyDown += OnAppKeyDown;
        _app.Mouse.MouseEvent += OnAppMouse;

        // Tab / Shift+Tab move between the panes (Terminal.Gui's Tab stays inside one frame).
        foreach (var view in new View[] { _tree, _attributes, _watch, _log })
        {
            view.KeyDown += (_, key) =>
            {
                if (key == Key.Tab || key == Key.Tab.WithShift)
                {
                    FocusNextPane(view, key == Key.Tab ? 1 : -1);
                    key.Handled = true;
                }
            };
        }
        Attach(_model);

        LayoutPanes();
        _app.ScreenChanged += (_, _) => LayoutPanes();
        _app.AddTimeout(RefreshInterval, () =>
        {
            RefreshWatch();
            RefreshChart();
            RefreshLog();
            RefreshDynamicTree();
            RefreshHeader();
            return true;
        });
    }

    public Window Window => _window;

    /// <summary>The title last sent to the terminal (its tab), for tests.</summary>
    internal string? TerminalTitle => _terminalTitle;

    /// <summary>Runs the browser until q or Ctrl+C.</summary>
    /// <summary>
    /// Runs the browser until q or Ctrl+C. Sessions opened with Ctrl+O replace the connection in place, within this one
    /// run: the terminal (and its keyboard mode) stays set up. The models it ends with are disposed.
    /// </summary>
    public static async Task RunAsync(BrowserModel model, Func<string, Task<BrowserModel>> openSession, ColorTheme theme, bool light, CancellationToken cancellationToken)
    {
        using var app = Application.Create().Init();
        var tui = new TuiApp(app, model, theme, light, openSession);
        try
        {
            tui.Start();
            await app.RunAsync(tui.Window, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            tui.Dispose();
            await tui._model.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The main actions in the key bar and every key; history, alarms, events and discovery per protocol.</summary>
    private void ConfigureKeys()
    {
        var opcUa = _model.SupportsHistory || _model.SupportsEvents;
        _status.SetItems(
        [
            ('m', "Monitor"), ('u', "Unmonitor"), ('w', "Write"), ('s', "Search"), ('f', "Filter"), ('p', "Pause"),
            ('o', "Sort"), ('r', "Record"),
            .. opcUa ? new[] { ('a', "Alarms"), ('e', "Events"), ('y', "History") } : [],
            .. _model.SupportsDiscoveryPause ? new[] { ('d', "Discovery") } : [],
            ('t', "Theme"), ('h', "Help"), ('q', "Quit"),
        ]);
        _extraKeys.Clear();
        foreach (var (key, action) in new (Key, Action)[]
        {
            (Key.M, Monitor), (Key.U, Unmonitor), (Key.W, Write), (Key.S, Search), (Key.F, Filter), (Key.P, TogglePause),
            (Key.O, NextSort), (Key.R, ToggleRecording), (Key.H, Help), (Key.T, NextTheme), (Key.L, ToggleLight),
            (Key.Q, Quit), (Key.I, ToggleIds), (Key.G, Diagnostics), (Key.S.WithCtrl, SaveSession),
            (Key.F5, Reload), (Key.C, () => _model.ClearLog()), (Key.O.WithCtrl, OpenSession),
            (Key.X, ResetTrend), (new Key('['), () => StepTrendWindow(-1)), (new Key(']'), () => StepTrendWindow(+1)),
        })
        {
            _extraKeys[key] = action;
        }

        if (_model.SupportsEvents)
        {
            _extraKeys[Key.A] = Alarms;
            _extraKeys[Key.E] = Events;
        }

        if (_model.SupportsHistory)
        {
            _extraKeys[Key.Y] = History;
        }

        if (_model.SupportsDiscoveryPause)
        {
            _extraKeys[Key.D] = () => Run(() => _model.ToggleDiscoveryAsync(), "pausing or resuming discovery");
        }
    }

    private void Attach(BrowserModel model)
    {
        model.Changed += () => _treeDirty = true;
        if (model.Client is IDynamicAddressSpace dynamic)
        {
            dynamic.AddressSpaceChanged += (_, _) => _treeDirty = true;
        }

        var layout = _layouts.Get(model.Args.Url);
        (_treeWidth, _attributesHeight, _chartHeight, _infoHeight) = (layout.TreeWidth, layout.AttributesHeight, layout.ChartHeight, layout.InfoHeight);
    }

    private void SaveLayout() =>
        _layouts.Set(_model.Args.Url, new PaneLayout(_treeWidth, _attributesHeight, _chartHeight, _infoHeight));

    /// <summary>0: every pane back, at its default size; the endpoint's saved sizes are forgotten.</summary>
    private void ResetLayout()
    {
        (_treeWidth, _attributesHeight, _chartHeight, _infoHeight) = (null, null, null, null);
        _beforeZoom = null;
        foreach (var pane in _panes)
        {
            pane.Visible = true;
        }

        SaveLayout();
        LayoutPanes();
    }

    /// <summary>
    /// Shows another connection (Ctrl+O): its tree, monitored items and keys. The previous one is closed, with any
    /// recording it ran.
    /// </summary>
    private void SwitchTo(BrowserModel model)
    {
        var previous = _model;
        _model = model;
        Attach(model);
        _loadingPlaceholders.Clear();
        _tree.ClearObjects();
        _tree.AddObject(model.Root);
        _tree.Expand(model.Root);
        _tree.SelectedObject = model.Root;
        _filter = string.Empty;
        _sortColumn = -1;
        _paused = false;
        _shownLogCount = -1;
        ConfigureKeys();
        LayoutPanes();
        RefreshWatch(force: true);
        RefreshHeader();
        _tree.SetFocus();
        Run(async () => await previous.DisposeAsync().ConfigureAwait(false), $"closing {previous.Args.Url}");
    }

    /// <summary>First expand, focus and attributes; separate so tests can start without running the loop.</summary>
    public void Start()
    {
        _tree.Expand(_model.Root);
        _tree.SelectedObject = _model.Root;
        _tree.SetFocus();
    }

    public void Dispose()
    {
        _app.Keyboard.KeyDown -= OnAppKeyDown;
        _app.Mouse.MouseEvent -= OnAppMouse;
        _window.Dispose();
    }

    // ------------------------------------------------------------------ tree

    private IEnumerable<TreeEntry> Children(TreeEntry entry)
    {
        if (entry.Children is { } children)
        {
            return children;
        }

        if (!entry.Loading)
        {
            Run(async () =>
            {
                await _model.LoadChildrenAsync(entry).ConfigureAwait(false);
                Ui(() => _tree.RefreshObject(entry, true));
            }, $"browsing {entry.Item.DisplayName}");
        }

        if (!_loadingPlaceholders.TryGetValue(entry, out var placeholder))
        {
            placeholder = new TreeEntry(new BrowseItem(Opc.Ua.NodeId.Null, "loading…", string.Empty, NodeClass.Unspecified, false), entry);
            _loadingPlaceholders[entry] = placeholder;
        }

        return [placeholder];
    }

    /// <summary>Variables get a dot, methods an ƒ; objects and folders just their name (the colour tells the class).</summary>
    private static string Label(TreeEntry entry) => entry.Item.NodeClass switch
    {
        NodeClass.Variable => $"• {entry.Item.DisplayName}",
        NodeClass.Method => $"ƒ {entry.Item.DisplayName}",
        _ => entry.Item.DisplayName,
    };

    private TreeEntry? Selected => _tree.SelectedObject is { Item.NodeClass: not NodeClass.Unspecified } entry ? entry : null;

    private void ShowAttributes(TreeEntry? entry)
    {
        var request = ++_attributeRequest;
        if (entry is null || entry.Item.NodeClass == NodeClass.Unspecified)
        {
            _attributeRows.SetRows([]);
            _attributes.Update();
            return;
        }

        Run(async () =>
        {
            var attributes = await _model.ReadAttributesAsync(entry).ConfigureAwait(false);
            Ui(() =>
            {
                // Arrow keys move faster than the device answers: only the latest selection is shown.
                if (request != _attributeRequest)
                {
                    return;
                }

                _attributeRows.SetRows([.. attributes.Select(a => new[] { a.Name, a.Value })]);
                _attributes.Update();
            });
        }, $"reading attributes of {entry.Item.DisplayName}");
    }

    private void Reload()
    {
        if (Selected is not { } entry)
        {
            return;
        }

        Run(async () =>
        {
            await _model.LoadChildrenAsync(entry, reload: true).ConfigureAwait(false);
            Ui(() => _tree.RefreshObject(entry, true));
        }, $"browsing {entry.Item.DisplayName}");
    }

    /// <summary>MQTT topics keep arriving: expanded nodes are browsed again (from the cached model) when it changed.</summary>
    private void RefreshDynamicTree()
    {
        if (!_treeDirty || _model.Client is not IDynamicAddressSpace)
        {
            return;
        }

        _treeDirty = false;
        var expanded = Expanded(_model.Root).ToList();
        Run(async () =>
        {
            foreach (var entry in expanded)
            {
                var before = entry.Children?.Count;
                await _model.LoadChildrenAsync(entry, reload: true).ConfigureAwait(false);
                if (entry.Children?.Count != before)
                {
                    Ui(() => _tree.RefreshObject(entry, true));
                }
            }
        }, "updating the tree");
    }

    private IEnumerable<TreeEntry> Expanded(TreeEntry entry)
    {
        if (entry.Children is null || !_tree.IsExpanded(entry))
        {
            yield break;
        }

        yield return entry;
        foreach (var child in entry.Children.SelectMany(Expanded))
        {
            yield return child;
        }
    }

    // ------------------------------------------------------------------ monitored items

    /// <summary>The monitored row under the cursor, as shown (filtered and sorted).</summary>
    private WatchRow? SelectedRow
    {
        get
        {
            var row = _watch.Value?.SelectedCell.Y ?? -1;
            return row >= 0 && row < _shown.Count ? _shown[row] : null;
        }
    }

    private void Monitor()
    {
        if (Selected is not { } entry)
        {
            return;
        }

        Run(async () =>
        {
            await _model.MonitorAsync(entry).ConfigureAwait(false);
            Ui(() => RefreshWatch());
        }, $"monitoring {entry.Item.DisplayName}");
    }

    private void Unmonitor()
    {
        if (SelectedRow is not { } row)
        {
            _model.Log("Select a monitored item first (Tab to the Monitored Items pane).");
            return;
        }

        Run(async () =>
        {
            await _model.UnmonitorAsync(row).ConfigureAwait(false);
            Ui(() => RefreshWatch());
        }, $"unmonitoring {row.Node.Name}");
    }

    private void RefreshWatch(bool force = false)
    {
        var rows = _model.Watch;
        if (_paused && !force)
        {
            _watchFrame.Title = WatchTitle(rows.Count);
            return;
        }

        var matches = _filter.Length == 0 ? null : AddressSpaceSearch.Matcher(_filter);
        var shown = rows.Select(r => (Row: r, Snap: r.Snapshot()))
            .Where(p => matches is null || matches(p.Row.Node.Name) || matches(p.Row.Node.DisplayId));
        if (_sortColumn >= 0)
        {
            Func<(WatchRow Row, RowSnapshot Snap), IComparable> key = _sortColumn switch
            {
                0 => p => p.Row.Node.Name,
                1 => p => p.Snap.Numeric is { } n ? (IComparable)n : p.Snap.Value,
                2 => p => p.Snap.Status,
                3 => p => p.Snap.Time,
                _ => p => p.Snap.Updates,
            };
            // Numbers and text can't be compared with each other: numbers first, then text.
            var comparer = Comparer<IComparable>.Create((x, y) => x.GetType() == y.GetType()
                ? x.CompareTo(y)
                : (x is double ? -1 : 1));
            shown = _sortDescending ? shown.OrderByDescending(key, comparer) : shown.OrderBy(key, comparer);
        }

        var list = shown.ToList();
        _shown = [.. list.Select(p => p.Row)];
        _shownSnapshots = [.. list.Select(p => p.Snap)];
        _watchRows.SetRows([.. list.Select(p => new[]
        {
            p.Row.Node.Name, p.Snap.Value, p.Snap.Status, p.Snap.Time, p.Snap.Updates.ToString(CultureInfo.InvariantCulture), p.Row.Node.DisplayId,
        })]);
        _watch.Update();
        _watchFrame.Title = WatchTitle(rows.Count);
    }

    private string WatchTitle(int total)
    {
        var parts = new List<string> { _shown.Count == total ? $"³Monitored Items ({total})" : $"³Monitored Items ({_shown.Count} of {total})" };
        if (_filter.Length > 0)
        {
            parts.Add($"filter: {_filter}");
        }

        if (_sortColumn >= 0)
        {
            parts.Add($"sort: {SortColumns[_sortColumn]} {(_sortDescending ? "↓" : "↑")}");
        }

        if (_paused)
        {
            parts.Add("PAUSED");
        }

        return string.Join("├─┤", parts);
    }

    /// <summary>Samples the trend shows: 0 fits the width (two per column), otherwise the last this many.</summary>
    private static readonly int[] TrendWindows = [0, 30, 60, 120, 300, WatchRow.MaxHistory];

    private int _trendWindow = 30;

    /// <summary>The row the trend shows: the selected one when it holds a number, else the first that does.</summary>
    private WatchRow? ChartRow =>
        SelectedRow is { } selected && (selected.History.Count > 0 || selected.Snapshot().Numeric is not null)
            ? selected
            : _shown.FirstOrDefault(r => r.History.Count > 0);

    private void RefreshChart()
    {
        if (!_chartFrame.Visible || _paused)
        {
            return;
        }

        if (ChartRow is not { } row)
        {
            _chartFrame.Title = "⁴Trend";
            _chart.SetValues([]);
            return;
        }

        var values = row.History;
        if (_trendWindow > 0 && values.Count > _trendWindow)
        {
            values = [.. values.Skip(values.Count - _trendWindow)];
        }

        var window = _trendWindow == 0 ? "fit" : _trendWindow.ToString(CultureInfo.InvariantCulture);
        var visible = TrendChart.Tail(values, Math.Max(1, _chart.Viewport.Width));
        _chartFrame.Title = visible.Count == 0
            ? $"⁴Trend ┤ {row.Node.Name} ├ window {window}"
            : string.Create(CultureInfo.InvariantCulture,
                $"⁴Trend ┤ {row.Node.Name} ├ last {visible.Count} of {window} · min {visible.Min():G6} · max {visible.Max():G6} · avg {visible.Average():G6} · now {visible[^1]:G6}");
        _chart.SetValues(values);
    }

    /// <summary>x: the trend starts again from the next sample (e.g. after a change on the machine).</summary>
    private void ResetTrend()
    {
        if (ChartRow is not { } row)
        {
            return;
        }

        row.ClearHistory();
        _model.Log($"Trend of {BrowserModel.Describe(row.Node)} reset.");
        RefreshChart();
    }

    /// <summary>[ / ]: fewer or more samples in the trend.</summary>
    private void StepTrendWindow(int direction)
    {
        var index = Array.IndexOf(TrendWindows, _trendWindow);
        _trendWindow = TrendWindows[Math.Clamp(index + direction, 0, TrendWindows.Length - 1)];
        RefreshChart();
    }

    // ------------------------------------------------------------------ layout, header and panes

    /// <summary>
    /// Places the visible panes: the tree on the left, attributes / monitored items / trend stacked on the right, info at
    /// the bottom. A hidden pane gives its room to the others; monitored items take what's left on the right.
    /// </summary>
    private void LayoutPanes()
    {
        var bottom = 1 + (_logFrame.Visible ? InfoHeight : 0);
        var right = new[] { _attributesFrame, _watchFrame, _chartFrame }.Where(p => p.Visible).ToList();

        // Info alone (zoomed): it takes the whole screen below the header.
        if (_logFrame.Visible && !_treeFrame.Visible && right.Count == 0)
        {
            _logFrame.X = 0;
            _logFrame.Y = 1;
            _logFrame.Width = Dim.Fill();
            _logFrame.Height = Dim.Fill(1);
            _window.SetNeedsLayout();
            return;
        }

        var treeVisible = _treeFrame.Visible || right.Count == 0;
        _treeFrame.Visible = treeVisible;

        _treeFrame.X = 0;
        _treeFrame.Y = 1;
        _treeFrame.Width = right.Count == 0 ? Dim.Fill()
            : _treeWidth is { } width ? Math.Clamp(width, 10, Math.Max(10, _app.Screen.Width - 20))
            : Dim.Percent(35);
        _treeFrame.Height = Dim.Fill(bottom);

        // The pane that takes the remaining height: monitored items, else attributes, else the trend.
        var filler = _watchFrame.Visible ? _watchFrame : right.FirstOrDefault();
        Pos y = 1;
        for (var i = 0; i < right.Count; i++)
        {
            var pane = right[i];
            pane.X = treeVisible ? Pos.Right(_treeFrame) : 0;
            pane.Y = y;
            pane.Width = Dim.Fill();
            if (pane == filler)
            {
                var after = right.Skip(i + 1).Sum(p => p == _chartFrame ? ChartHeight : 0);
                pane.Height = Dim.Fill(bottom + after);
            }
            else
            {
                pane.Height = pane == _chartFrame ? ChartHeight
                    : _attributesHeight is { } height ? Math.Clamp(height, 3, Math.Max(3, _app.Screen.Height - 12))
                    : Dim.Percent(25);
            }

            y = Pos.Bottom(pane);
        }

        _logFrame.X = 0;
        _logFrame.Y = Pos.AnchorEnd(InfoHeight + 1);
        _logFrame.Width = Dim.Fill();
        _logFrame.Height = InfoHeight;
        _window.SetNeedsLayout();
    }

    /// <summary>The panes' main views in Tab order; hidden panes are skipped.</summary>
    private IEnumerable<View> FocusOrder() => PaneViews().Where(p => p.Pane.Visible).Select(p => p.View);

    private (FrameView Pane, View View)[] PaneViews() =>
        [(_treeFrame, _tree), (_attributesFrame, _attributes), (_watchFrame, _watch), (_logFrame, _log)];

    private void FocusNextPane(View current, int direction)
    {
        var order = FocusOrder().ToList();
        if (order.Count == 0)
        {
            return;
        }

        var index = order.IndexOf(current);
        order[((index < 0 ? 0 : index + direction) % order.Count + order.Count) % order.Count].SetFocus();
    }

    /// <summary>Visibility before a zoom; null when not zoomed.</summary>
    private bool[]? _beforeZoom;

    /// <summary>Shift+1-5: the pane alone on the screen; the same again restores the layout before.</summary>
    private void ToggleZoom(int index)
    {
        if (_beforeZoom is { } before && _panes[index].Visible && _panes.Count(p => p.Visible) == 1)
        {
            for (var i = 0; i < _panes.Length; i++)
            {
                _panes[i].Visible = before[i];
            }

            _beforeZoom = null;
        }
        else
        {
            _beforeZoom ??= [.. _panes.Select(p => p.Visible)];
            for (var i = 0; i < _panes.Length; i++)
            {
                _panes[i].Visible = i == index;
            }
        }

        LayoutPanes();
        var pane = _panes[index];
        View? target = pane.Visible ? PaneViews().FirstOrDefault(p => p.Pane == pane).View : null;
        (target ?? FocusOrder().FirstOrDefault())?.SetFocus();
    }

    /// <summary>1-5 show or hide a pane, like btop; a pane that appears gets the focus.</summary>
    private void TogglePane(int index)
    {
        _beforeZoom = null;
        var pane = _panes[index];
        pane.Visible = !pane.Visible;
        LayoutPanes();
        View? target = pane.Visible ? PaneViews().FirstOrDefault(p => p.Pane == pane).View : null;
        (target ?? FocusOrder().FirstOrDefault())?.SetFocus();
    }

    /// <summary>Dragging a border between panes resizes them; a double-click on it restores the default size.</summary>
    private void OnAppMouse(object? sender, Mouse mouse)
    {
        if (mouse.Handled || _app.TopRunnableView != _window)
        {
            return;
        }

        var at = mouse.ScreenPosition;
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased) && _dragging != Splitter.None)
        {
            _dragging = Splitter.None;
            SaveLayout();
            mouse.Handled = true;
            return;
        }

        if (_dragging != Splitter.None && (mouse.Flags.HasFlag(MouseFlags.PositionReport) || mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)))
        {
            Resize(_dragging, at);
            mouse.Handled = true;
            return;
        }

        var splitter = SplitterAt(at);
        if (splitter == Splitter.None)
        {
            return;
        }

        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked))
        {
            _ = splitter switch
            {
                Splitter.Tree => _treeWidth = null,
                Splitter.Attributes => _attributesHeight = null,
                Splitter.Chart => _chartHeight = null,
                _ => _infoHeight = null,
            };
            SaveLayout();
            LayoutPanes();
            mouse.Handled = true;
        }
        else if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
        {
            _dragging = splitter;
            mouse.Handled = true;
        }
        else if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) || mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased))
        {
            // The click that ends a drag shouldn't also select or focus what's under the border.
            mouse.Handled = true;
        }
    }

    /// <summary>The border under <paramref name="at"/> (screen cells): a pane's edge or the one next to it.</summary>
    private Splitter SplitterAt(System.Drawing.Point at)
    {
        static bool Within(int value, int start, int end) => value >= start && value < end;

        var log = _logFrame.Frame;
        var othersVisible = _panes.Any(p => p != _logFrame && p.Visible);
        if (_logFrame.Visible && othersVisible && (at.Y == log.Y || at.Y == log.Y - 1))
        {
            return Splitter.Info;
        }

        var filler = _watchFrame.Visible ? _watchFrame : new[] { _attributesFrame, _chartFrame }.FirstOrDefault(p => p.Visible);
        var chart = _chartFrame.Frame;
        if (_chartFrame.Visible && filler != _chartFrame && Within(at.X, chart.X, chart.Right) && (at.Y == chart.Y || at.Y == chart.Y - 1))
        {
            return Splitter.Chart;
        }

        var attributes = _attributesFrame.Frame;
        if (_attributesFrame.Visible && filler != _attributesFrame && Within(at.X, attributes.X, attributes.Right)
            && (at.Y == attributes.Bottom - 1 || at.Y == attributes.Bottom))
        {
            return Splitter.Attributes;
        }

        var tree = _treeFrame.Frame;
        var rightVisible = _attributesFrame.Visible || _watchFrame.Visible || _chartFrame.Visible;
        return _treeFrame.Visible && rightVisible && Within(at.Y, tree.Y, tree.Bottom) && (at.X == tree.Right - 1 || at.X == tree.Right)
            ? Splitter.Tree
            : Splitter.None;
    }

    private void Resize(Splitter splitter, System.Drawing.Point at)
    {
        switch (splitter)
        {
            case Splitter.Tree:
                _treeWidth = at.X + 1;
                break;
            case Splitter.Attributes:
                _attributesHeight = at.Y - _attributesFrame.Frame.Y + 1;
                break;
            case Splitter.Chart:
                _chartHeight = _chartFrame.Frame.Bottom - at.Y;
                break;
            case Splitter.Info:
                // The key bar is the last line; info ends just above it.
                _infoHeight = _app.Screen.Height - 1 - at.Y;
                break;
        }

        LayoutPanes();
    }

    private void OnAppKeyDown(object? sender, Key key)
    {
        if (key.Handled || _app.TopRunnableView != _window)
        {
            return;
        }

        OnKeyDown(sender, key);
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (_extraKeys.TryGetValue(key, out var action))
        {
            action();
            key.Handled = true;
            return;
        }

        if (key.IsCtrl || key.IsAlt)
        {
            return;
        }

        // Shift+1-5 zoom. Terminals send them as ! @ # $ % (classic), or as the digit key with Shift (kitty keyboard
        // protocol, e.g. Warp), where the key has no single character to match.
        var baseCode = key.NoShift.KeyCode;
        if (key.IsShift && baseCode is >= KeyCode.D1 and <= KeyCode.D5)
        {
            ToggleZoom((int)(baseCode - KeyCode.D1));
            key.Handled = true;
            return;
        }

        switch (key.AsRune.Value)
        {
            case >= '1' and <= '5' when key.IsShift:
                ToggleZoom(key.AsRune.Value - '1');
                key.Handled = true;
                break;
            case '!' or '@' or '#' or '$' or '%':
                ToggleZoom("!@#$%".IndexOf((char)key.AsRune.Value, StringComparison.Ordinal));
                key.Handled = true;
                break;
            case '0':
                ResetLayout();
                key.Handled = true;
                break;
            case >= '1' and <= '5':
                TogglePane(key.AsRune.Value - '1');
                key.Handled = true;
                break;
            case '-':
                StepRefresh(-1);
                key.Handled = true;
                break;
            case '+' or '=':
                StepRefresh(+1);
                key.Handled = true;
                break;
            case 'O':
                _sortDescending = !_sortDescending;
                RefreshWatch(force: true);
                key.Handled = true;
                break;
            default:
                break;
        }
    }

    private void RefreshHeader()
    {
        var state = _model.Client.State;
        var connected = state == ConnectionState.Connected;
        var left = new List<(string, Attribute)>
        {
            ("mdbrowser", Theme.Text(Theme.Magenta, TextStyle.Bold)),
            ("  " + _model.Args.Url, Theme.Text(Theme.Foreground)),
            ("  " + (connected ? "●" : "○") + " " + state, Theme.Text(connected ? Theme.Green : Theme.Red, TextStyle.Bold)),
        };
        if (_model.SessionPath is { } path)
        {
            left.Add(("  " + Path.GetFileName(path), Theme.Text(Theme.Muted)));
        }

        if (_model.Args.ReadOnly)
        {
            left.Add(("  read-only", Theme.Text(Theme.Yellow)));
        }

        if (_model.IsDiscoveryPaused)
        {
            left.Add(("  ⏸ discovery paused", Theme.Text(Theme.Yellow, TextStyle.Bold)));
        }

        if (_model.RecordingPath is { } file)
        {
            left.Add(($"  ⏺ REC {Path.GetFileName(file)} · {_model.RecordedSamples}", Theme.Text(Theme.Red, TextStyle.Bold)));
        }

        var right = new List<(string, Attribute)>
        {
            ("- ", Theme.Text(Theme.Blue, TextStyle.Bold)),
            (BrowserModel.RefreshText(_model.DefaultRefreshMs), Theme.Text(Theme.Bright)),
            (" +", Theme.Text(Theme.Blue, TextStyle.Bold)),
            ("   " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Theme.Text(Theme.Muted)),
        };
        _header.Set(left, right);

        // The terminal's tab title: the driver clears it at startup.
        var title = "mdbrowser " + (_model.SessionPath is { } session ? Path.GetFileName(session) : _model.Args.Url);
        if (title != _terminalTitle && _app.Driver is { } driver)
        {
            driver.SetTerminalTitle(title);
            _terminalTitle = title;
        }
    }

    /// <summary>- / +: the next refresh time for every monitored item (and new ones).</summary>
    private void StepRefresh(int direction)
    {
        var steps = _model.Client is IDynamicAddressSpace ? RefreshSteps : RefreshSteps.Where(s => s > 0).ToArray();
        var current = Array.FindIndex(steps, s => s >= _model.DefaultRefreshMs);
        current = current < 0 ? steps.Length - 1 : current;
        var next = steps[Math.Clamp(current + direction, 0, steps.Length - 1)];
        if (next == _model.DefaultRefreshMs)
        {
            return;
        }

        Run(() => _model.ChangeRefreshAsync(next), $"changing the refresh time to {BrowserModel.RefreshText(next)}");
    }

    // ------------------------------------------------------------------ filter, pause, sort

    private void Filter()
    {
        if (Ask("Filter monitored items", "Name or id (Temp*, Motor?, any text; empty shows all):", _filter) is not { } text)
        {
            return;
        }

        _filter = text;
        RefreshWatch(force: true);
    }

    /// <summary>i: the id column of the monitored items, for names that repeat.</summary>
    private void ToggleIds()
    {
        _watch.Style.ColumnStyles[5].Visible = !_watch.Style.ColumnStyles[5].Visible;
        _watch.Update();
    }

    /// <summary>Freezes the display to read values; monitoring and recording go on.</summary>
    private void TogglePause()
    {
        _paused = !_paused;
        _model.Log(_paused ? "Display paused (p continues); monitoring and recording go on." : "Display continues.");
        RefreshWatch(force: true);
    }

    /// <summary>o: next sort column (then unsorted); O reverses.</summary>
    private void NextSort()
    {
        _sortColumn = _sortColumn + 1 >= SortColumns.Length ? -1 : _sortColumn + 1;
        RefreshWatch(force: true);
    }

    // ------------------------------------------------------------------ colours

    private static Scheme? TreeColor(TreeEntry entry) => entry.Item.NodeClass switch
    {
        NodeClass.Variable => Theme.Colored(Theme.Green),
        NodeClass.Method => Theme.Colored(Theme.Magenta),
        NodeClass.Unspecified => Theme.Colored(Theme.Dim),
        _ => Theme.Colored(Theme.Foreground),
    };

    /// <summary>Stale and waiting rows are dimmed.</summary>
    private Scheme? RowScheme(int row) =>
        row < _shownSnapshots.Count && (_shownSnapshots[row].Stale || _shownSnapshots[row].Waiting) ? Theme.Colored(Theme.Dim) : null;

    /// <summary>Good green, Uncertain yellow, Bad red.</summary>
    private Scheme? StatusScheme(int row)
    {
        if (row >= _shownSnapshots.Count || _shownSnapshots[row].Waiting)
        {
            return null;
        }

        var code = _shownSnapshots[row].Code;
        return Theme.Colored(Theme.Status(code));
    }

    private void RefreshLog()
    {
        var lines = _model.LogLines;
        if (lines.Count == _shownLogCount && (lines.Count == 0 || _logLines.LastOrDefault() == lines[^1]))
        {
            return;
        }

        _logLines.Clear();
        foreach (var line in lines)
        {
            _logLines.Add(line);
        }

        _shownLogCount = lines.Count;
        _log.MoveEnd();
    }

    // ------------------------------------------------------------------ write

    private void Write()
    {
        // The focused pane decides: a monitored row, or the selected node in the tree.
        var (id, name) = _watch.HasFocus && SelectedRow is { } row
            ? (row.Node.Id, row.Node.Name)
            : Selected is { IsVariable: true } entry ? (entry.Item.NodeId, entry.Item.DisplayName) : (null!, null!);
        if (id is null)
        {
            _model.Log("Select a variable to write.");
            return;
        }

        if (_model.WriteBlockedReason is { } reason)
        {
            _model.Log(reason);
            return;
        }

        // Dialogs open from the key press itself (see Load); the current value fills in as it arrives.
        var now = "…";
        if (Ask($"Write {name}", "New value:", string.Empty, async () => now = await _model.ReadValueTextAsync(id).ConfigureAwait(false)) is not { } text)
        {
            return;
        }

        if (!Confirm("Write value", $"Write {text} to {name} on {_model.Args.Url}?\nIt is {now} now."))
        {
            _model.Log("Nothing written.");
            return;
        }

        // A failed write is reported in the info log with its reason.
        Run(() => _model.WriteAsync(id, name, text), $"writing {name}");
    }

    // ------------------------------------------------------------------ search

    private void Search()
    {
        if (Ask("Search", "Name or id (Temp*, Motor?, or any text):", string.Empty) is not { Length: > 0 } text)
        {
            return;
        }

        SearchResult? result = null;
        var choice = Pick($"Search: {text}", ["Name", "Class", "Path"], async () =>
        {
            result = await _model.SearchAsync(text).ConfigureAwait(false);
            _model.Log(result.Hits.Count == 0
                ? $"Nothing matches {text} ({result.NodesVisited} nodes searched)."
                : $"{result.Hits.Count} match(es) for {text}{(result.Truncated ? " (search stopped early)" : string.Empty)}.");
            return (null, [.. result.Hits.Select(h => new[] { h.Item.DisplayName, h.Item.NodeClass.ToString(), h.PathText })]);
        }, goTo: true);
        if (choice is { } index && result is { } found && index < found.Hits.Count)
        {
            Reveal(found.Hits[index].Path);
        }
    }

    internal void Reveal(IReadOnlyList<Opc.Ua.NodeId> path)
    {
        Run(async () =>
        {
            var entry = await _model.RevealAsync(path).ConfigureAwait(false);
            Ui(() =>
            {
                if (entry is null)
                {
                    _model.Log("That node is no longer in the address space.");
                    return;
                }

                // Top-down: a branch can only be expanded once its parent is.
                var ancestors = new List<TreeEntry>();
                for (var parent = entry.Parent; parent is not null; parent = parent.Parent)
                {
                    ancestors.Insert(0, parent);
                }

                foreach (var parent in ancestors)
                {
                    _tree.RefreshObject(parent);
                    _tree.Expand(parent);
                    // Children only become branches the next level can expand once the line map is rebuilt.
                    _ = _tree.GetObjectRow(parent);
                }

                _tree.SelectedObject = entry;
                _tree.EnsureVisible(entry);
                _tree.SetFocus();
            });
        }, "opening the search result");
    }

    // ------------------------------------------------------------------ recording and sessions

    private void ToggleRecording()
    {
        if (_model.RecordingPath is not null)
        {
            Run(_model.StopRecordingAsync, "stopping the recording");
            return;
        }

        var suggestion = $"mdbrowser-{DateTime.Now:yyyyMMdd-HHmmss}.db";
        if (Ask("Record", "File (.db/.sqlite = SQLite, otherwise CSV; Tab completes):", suggestion, path: true) is not { Length: > 0 } path)
        {
            return;
        }

        Run(() => _model.StartRecordingAsync(path), $"recording to {path}");
    }

    private void Quit() => _app.RequestStop(_window);

    /// <summary>Ctrl+O: a session from the app's recent ones or this folder, or any file; the browser reopens with it.</summary>
    private void OpenSession()
    {
        var candidates = AppAppearance.RecentSessions()
            .Concat(Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.mdbsession").Concat(Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.opcsession")))
            .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Take(50).ToList();
        List<string[]> rows = [.. candidates.Select(c => new[] { Path.GetFileNameWithoutExtension(c), Path.GetDirectoryName(c) ?? string.Empty }), ["Other file…", string.Empty]];
        if (Pick("Open session", ["Session", "Folder"], rows, goTo: true, goToTitle: "_Open") is not { } index)
        {
            return;
        }

        var path = index < candidates.Count ? candidates[index] : Ask("Open session", "Session file (.mdbsession; Tab completes):", _model.SessionPath ?? string.Empty, path: true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            Error("Open session", $"{path} doesn't exist.");
            return;
        }

        if (_model.RecordingPath is { } recording && !Confirm("Open session", $"Recording to {recording} stops when the session opens. Open {Path.GetFileName(path)}?"))
        {
            return;
        }

        if (_openSession is not { } open)
        {
            return;
        }

        _model.Log($"Opening {Path.GetFileName(path)}…");
        Run(async () =>
        {
            var model = await open(path).ConfigureAwait(false);
            Ui(() => SwitchTo(model));
        }, $"opening {path}");
    }

    private void SaveSession()
    {
        var suggestion = _model.SessionPath ?? "mdbrowser.mdbsession";
        if (Ask("Save session", "Session file (opens in the app and with mdbrowser run; Tab completes):", suggestion, path: true) is not { Length: > 0 } path)
        {
            return;
        }

        if (File.Exists(path) && path != _model.SessionPath
            && !Confirm("Save session", $"{path} exists. Replace its watch list with the monitored items?\nEverything else in it is kept."))
        {
            return;
        }

        Run(() => _model.SaveSessionAsync(path), $"saving {path}");
    }

    // ------------------------------------------------------------------ history, alarms, events, diagnostics

    private void History()
    {
        if (!_model.SupportsHistory)
        {
            _model.Log("History is available from OPC UA servers only.");
            return;
        }

        if (Selected is not { IsVariable: true } entry)
        {
            _model.Log("Select a variable in the tree for its history.");
            return;
        }

        if (Ask("History", "How far back (minutes):", "60") is not { } text || !int.TryParse(text, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            return;
        }

        Pick($"History of {entry.Item.DisplayName}, last {minutes} min", ["Time", "Status", "Value"], async () =>
        {
            var result = await _model.ReadHistoryAsync(entry.Item.NodeId, TimeSpan.FromMinutes(minutes)).ConfigureAwait(false);
            var numbers = result.Values.Select(v => v.Numeric).OfType<double>().ToList();
            var summary = result.Values.Count == 0
                ? "no values stored"
                : string.Create(CultureInfo.InvariantCulture, $"{result.Values.Count} values{(result.Truncated ? " (truncated)" : string.Empty)}")
                    + (numbers.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" · min {numbers.Min():G6} · max {numbers.Max():G6} · avg {numbers.Average():G6}") : string.Empty);
            List<string[]> rows = [.. result.Values.OrderByDescending(v => v.SourceTimestamp)
                .Select(v => new[] { (Output.Time(v) ?? DateTime.MinValue).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), StatusText.Of(v.Status), v.Value })];
            return ($"History of {entry.Item.DisplayName}, last {minutes} min: {summary}", rows);
        });
    }

    private void Alarms()
    {
        if (!_model.SupportsEvents)
        {
            _model.Log("Alarms are available from OPC UA servers only.");
            return;
        }

        // The subscription makes the server resend the conditions it holds; the table fills in as they arrive.
        Run(() => _model.StartEventsAsync(), "subscribing to alarms");
        Live("Alarms (active or not acknowledged)", ["Severity", "Source", "Alarm", "Active", "Acked", "Message", "Time"], () =>
            [.. _model.Alarms.Select(a => new[]
            {
                a.Severity.ToString(CultureInfo.InvariantCulture), a.SourceName, a.ConditionName ?? a.EventType,
                YesNo(a.IsActive), YesNo(a.IsAcked), a.Message, a.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            })]);
    }

    private void Events()
    {
        if (!_model.SupportsEvents)
        {
            _model.Log("Events are available from OPC UA servers only.");
            return;
        }

        Run(() => _model.StartEventsAsync(), "subscribing to events");
        Live("Events (newest first)", ["Time", "Severity", "Source", "Type", "Message"], () =>
            [.. _model.Events.Select(e => new[]
            {
                e.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), e.Severity.ToString(CultureInfo.InvariantCulture),
                e.SourceName, e.EventType, e.Message,
            })]);
    }

    private void Diagnostics() =>
        Pick("Connection diagnostics", ["Name", "Value"], async () =>
        {
            var lines = await _model.DiagnosticsAsync().ConfigureAwait(false);
            return (null, [.. lines.Select(l => new[] { l.Name, l.Value })]);
        });

    private void Help()
    {
        List<string[]> rows =
        [
            ["Enter / → / ←", "Expand or collapse a node"],
            ["Tab / Shift+Tab", "Next / previous pane"],
            ["1 … 5", "Show or hide a pane: address space, attributes, monitored items, trend, info"],
            ["Shift+1 … 5", "The pane alone on the full screen; again restores the layout"],
            ["0", "Reset the layout: every pane, default sizes (forgets the sizes saved for this endpoint)"],
            ["Mouse drag", "Resize panes by their borders (saved per endpoint); double-click a border: its default"],
            ["m", "Monitor the variable, or every variable below a folder or structure"],
            ["u", "Stop monitoring the selected monitored item"],
            ["w", "Write a value (shows the current one, asks first, reads back)"],
            ["s", "Search the address space by name or id (Temp*, Motor?)"],
            ["f", "Filter the monitored items by name or id"],
            ["o / O", "Sort the monitored items by the next column / reverse"],
            ["i", "Show or hide the NodeId (tag, topic) column of the monitored items"],
            ["p", "Pause the display (monitoring and recording go on)"],
            ["- / +", "Refresh time of all monitored items"],
            ["x", "Reset the trend: start again from the next sample"],
            ["[ / ]", "Fewer / more samples in the trend: fit the width, 30, 60, 120, 300, 600"],
            ["r", "Start or stop recording the monitored items (.db = SQLite, else CSV)"],
            ["Ctrl+S", "Save the monitored items as a session file"],
            ["Ctrl+O", "Open a session: its endpoint, options and watch list (recent ones and those in this folder)"],
        ];
        if (_model.SupportsHistory)
        {
            rows.Add(["y", "History of the selected variable"]);
        }

        if (_model.SupportsEvents)
        {
            rows.Add(["a / e", "Current alarms / live events"]);
        }

        if (_model.SupportsDiscoveryPause)
        {
            rows.Add(["d", "Pause / resume discovery: receive only the monitored topics (busy brokers)"]);
        }

        rows.AddRange(
        [
            ["g", "Connection diagnostics"],
            ["t / l", $"Next colour theme (the app's) / light or dark; now {Theme.Current.Name}, {(Theme.Light ? "light" : "dark")}"],
            ["F5", "Browse the selected node again"],
            ["c", "Clear the info log"],
            ["h", "This help"],
            ["q", "Quit"],
        ]);
        Pick("Keys", ["Key", "Does"], rows);
    }

    // ------------------------------------------------------------------ themes

    private void NextTheme()
    {
        var all = ColorThemeCatalog.All;
        var next = all[(all.ToList().IndexOf(Theme.Current) + 1) % all.Count];
        ApplyTheme(next, Theme.Light);
    }

    private void ToggleLight() => ApplyTheme(Theme.Current, !Theme.Light);

    /// <summary>Recolours every pane at once; tables and the tree take their colours from the theme as they draw.</summary>
    private void ApplyTheme(ColorTheme theme, bool light)
    {
        Theme.Apply(theme, light);
        _window.SetScheme(Theme.Base);
        foreach (var (pane, view) in PaneViews().Append((_chartFrame, _chart)))
        {
            pane.SetScheme(Theme.Frame(view.HasFocus));
            view.SetScheme(view == _log ? Theme.Quiet : Theme.Base);
        }

        Theme.Table(_attributes);
        Theme.Table(_watch);
        _window.SetNeedsDraw();
        _model.Log($"Theme {theme.Name}, {(light ? "light" : "dark")}.");
    }

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "-" };

    // ------------------------------------------------------------------ dialogs

    /// <summary>A themed dialog with its buttons (the last is the default); the caller adds the content first.</summary>
    private static Dialog NewDialog(string title, Dim width, Dim? height, View[] content, params string[] buttons)
    {
        var dialog = new Dialog { Title = title, Width = width };
        if (height is not null)
        {
            dialog.Height = height;
        }

        dialog.Add(content);
        foreach (var button in buttons)
        {
            dialog.AddButton(new Button { Title = $" {button} " });
        }

        Theme.Dialog(dialog);
        return dialog;
    }

    /// <summary>
    /// Runs a dialog with its buttons' letters as keys (C for Close, Y / N, …): Terminal.Gui only takes them with Alt or
    /// when the focused view passes letters on, and not as the kitty keyboard protocol (Warp) sends them. A text field
    /// with the focus keeps its letters.
    /// </summary>
    private void RunDialog(Dialog dialog)
    {
        var letters = dialog.Buttons
            .Select((button, index) => (Index: index, Title: button.Title ?? string.Empty))
            .Select(b => (b.Index, At: b.Title.IndexOf('_', StringComparison.Ordinal)))
            .Where(b => b.At >= 0)
            .ToDictionary(b => char.ToLowerInvariant(dialog.Buttons[b.Index].Title[b.At + 1]), b => b.Index);

        void OnKey(object? sender, Key key)
        {
            if (key.Handled || _app.TopRunnableView != dialog || dialog.MostFocused is TextField || key.IsCtrl || key.IsAlt)
            {
                return;
            }

            var rune = key.NoShift.AsRune.Value;
            if (rune > 0 && rune < 0x10000 && letters.TryGetValue(char.ToLowerInvariant((char)rune), out var index))
            {
                dialog.Result = index;
                dialog.RequestStop();
                key.Handled = true;
            }
        }

        _app.Keyboard.KeyDown += OnKey;
        try
        {
            _app.Run(dialog);
        }
        finally
        {
            _app.Keyboard.KeyDown -= OnKey;
        }
    }

    private static Label Text(string text, Pos? y = null)
    {
        var label = new Label { Text = text, X = 1, Y = y ?? 1 };
        Theme.PlainTitle(label);
        return label;
    }

    /// <summary>
    /// A text prompt; null when cancelled. <paramref name="load"/> fetches the initial text while the prompt is open
    /// (filled in unless something was typed already).
    /// </summary>
    private string? Ask(string title, string label, string initial, Func<Task<string>>? load = null, bool path = false)
    {
        var text = Text(label);
        var field = new TextField { Text = initial, X = 1, Y = Pos.Bottom(text) + 1, Width = Dim.Fill(1) };
        field.SetScheme(new Scheme(Theme.Base) { Normal = new Attribute(Theme.Bright, Theme.Selection), Focus = new Attribute(Theme.Bright, Theme.Selection), Editable = new Attribute(Theme.Bright, Theme.Selection) });
        using var dialog = NewDialog(title, Dim.Percent(70), null, [text, field], "_Cancel", "_OK");
        if (load is not null)
        {
            text.Text = $"{label} (reading the current value…)";
            Run(async () =>
            {
                var value = await load().ConfigureAwait(false);
                Ui(() =>
                {
                    text.Text = $"Now: {value}. {label}";
                    if (string.IsNullOrEmpty(field.Text))
                    {
                        field.Text = value;
                    }
                });
            }, $"reading {title}");
        }

        if (path)
        {
            // Tab completes the path like a shell; several matches are listed under the field.
            var matches = Text(string.Empty, Pos.Bottom(field) + 1);
            matches.SetScheme(new Scheme(Theme.Base) { Normal = new Attribute(Theme.Muted, Theme.Surface) });
            dialog.Add(matches);
            field.KeyDown += (_, key) =>
            {
                if (key != Key.Tab)
                {
                    return;
                }

                var (completed, found) = PathCompletion.Complete(field.Text ?? string.Empty);
                field.Text = completed;
                field.MoveEnd();
                matches.Text = found.Count switch
                {
                    0 => "no match",
                    1 => string.Empty,
                    _ => string.Join("  ", found.Take(12)) + (found.Count > 12 ? $"  … {found.Count - 12} more" : string.Empty),
                };
                key.Handled = true;
            };
        }

        field.SetFocus();
        RunDialog(dialog);
        if (dialog.Result != 1)
        {
            return null;
        }

        var answer = field.Text?.Trim() ?? string.Empty;
        return path ? PathCompletion.Expand(answer) : answer;
    }

    /// <summary>A yes/no question; No is the default, so Enter alone changes nothing.</summary>
    private bool Confirm(string title, string message)
    {
        using var dialog = NewDialog(title, Dim.Auto(minimumContentDim: 50), null, [Text(message)], "_Yes", "_No");
        dialog.Buttons[^1].SetFocus();
        RunDialog(dialog);
        return dialog.Result == 0;
    }

    private void Error(string title, string message)
    {
        using var dialog = NewDialog(title, Dim.Auto(minimumContentDim: 50), null, [Text(message)], "_OK");
        dialog.SetScheme(new Scheme(dialog.GetScheme()) { Normal = new Attribute(Theme.Red, Theme.Surface) });
        RunDialog(dialog);
    }

    /// <summary>
    /// A table dialog that opens at once and fills in when <paramref name="load"/> returns (and may give a new title).
    /// Dialogs are only opened from key presses: one opened from a background callback gets no keys with the kitty
    /// keyboard protocol (Warp).
    /// </summary>
    private int? Pick(string title, string[] columns, Func<Task<(string? Title, List<string[]> Rows)>> load, bool goTo = false)
    {
        var source = new GridSource(columns);
        source.SetRows([[.. columns.Select((_, i) => i == 0 ? "loading…" : string.Empty)]]);
        var table = new TableView(source) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true, CollectionNavigator = null };
        Theme.Table(table);
        table.SetScheme(new Scheme(Theme.Base) { Normal = new Attribute(Theme.Foreground, Theme.Surface) });
        using var dialog = NewDialog(title, Dim.Percent(90), Dim.Percent(80), [table], goTo ? ["_Close", "_Go to"] : ["_Close"]);
        var loaded = 0;
        Run(async () =>
        {
            var (newTitle, rows) = await load().ConfigureAwait(false);
            Ui(() =>
            {
                source.SetRows(rows.Count == 0 ? [[.. columns.Select((_, i) => i == 0 ? "nothing found" : string.Empty)]] : rows);
                loaded = rows.Count;
                if (newTitle is not null)
                {
                    dialog.Title = newTitle;
                }

                table.Update();
            });
        }, title);
        table.SetFocus();
        RunDialog(dialog);
        var row = table.Value?.SelectedCell.Y ?? -1;
        return goTo && dialog.Result == 1 && row >= 0 && row < loaded ? row : null;
    }

    /// <summary>A table in a dialog; with <paramref name="goTo"/>, Enter / Go to picks a row (its index), Esc closes.</summary>
    private int? Pick(string title, string[] columns, List<string[]> rows, bool goTo = false, string goToTitle = "_Go to")
    {
        var source = new GridSource(columns);
        source.SetRows(rows);
        var table = new TableView(source) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true, CollectionNavigator = null };
        Theme.Table(table);
        table.SetScheme(new Scheme(Theme.Base) { Normal = new Attribute(Theme.Foreground, Theme.Surface) });
        using var dialog = NewDialog(title, Dim.Percent(90), Dim.Percent(80), [table], goTo ? ["_Close", goToTitle] : ["_Close"]);
        table.SetFocus();
        RunDialog(dialog);
        var row = table.Value?.SelectedCell.Y ?? -1;
        return goTo && dialog.Result == 1 && row >= 0 && row < rows.Count ? row : null;
    }

    /// <summary>A table that keeps updating while open (alarms, events).</summary>
    private void Live(string title, string[] columns, Func<IReadOnlyList<string[]>> rows)
    {
        var source = new GridSource(columns);
        source.SetRows(rows());
        var table = new TableView(source) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true, CollectionNavigator = null };
        Theme.Table(table);
        table.SetScheme(new Scheme(Theme.Base) { Normal = new Attribute(Theme.Foreground, Theme.Surface) });
        using var dialog = NewDialog(title, Dim.Percent(95), Dim.Percent(80), [table], "_Close");
        var open = true;
        _app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            if (open)
            {
                source.SetRows(rows());
                table.Update();
            }

            return open;
        });
        table.SetFocus();
        RunDialog(dialog);
        open = false;
    }

    // ------------------------------------------------------------------ plumbing

    private void Ui(Action action) => _app.Invoke(action);

    /// <summary>Device work off the UI thread; failures go to the info log instead of ending the program.</summary>
    private void Run(Func<Task> work, string what) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && Errors.IsRecoverable(ex))
            {
                _model.Log($"Failed {what}: {Describe(ex)}");
            }
        });

    private static string Describe(Exception ex) => ex switch
    {
        ServiceResultException sre => $"{StatusText.Of(sre.Result.StatusCode)}: {sre.Message}",
        AggregateException { InnerExceptions.Count: 1 } a => Describe(a.InnerExceptions[0]),
        _ => ex.Message,
    };
}
