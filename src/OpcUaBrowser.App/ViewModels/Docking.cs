using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public abstract class PaneTool : Tool
{
    protected PaneTool(MainWindowViewModel main, string id, string title)
    {
        Main = main;
        Id = id;
        Title = title;
        CanClose = false;
        CanFloat = false;
        CanPin = true;
    }

    public MainWindowViewModel Main { get; }
}

public sealed class AddressSpaceTool(MainWindowViewModel main) : PaneTool(main, "AddressSpace", "Address Space");

public sealed class AttributesTool(MainWindowViewModel main) : PaneTool(main, "Attributes", "Attributes");

public sealed class WatchTool : PaneTool
{
    public WatchTool(MainWindowViewModel main)
        : base(main, "Watch", "Watch")
    {
        main.WatchItems.CollectionChanged += (_, _) =>
            Title = main.WatchItems.Count == 0 ? "Watch" : $"Watch ({main.WatchItems.Count})";
    }
}

public sealed class RecordingsTool(MainWindowViewModel main) : PaneTool(main, "Recordings", "Recordings");

public sealed class DockFactory(MainWindowViewModel main) : Factory
{
    private static readonly string[] ToolIds = ["AddressSpace", "Attributes", "Watch", "Recordings"];

    private PaneTool CreateTool(string id) => id switch
    {
        "AddressSpace" => new AddressSpaceTool(main),
        "Attributes" => new AttributesTool(main),
        "Watch" => new WatchTool(main),
        "Recordings" => new RecordingsTool(main),
        _ => throw new InvalidDataException($"Unknown tool '{id}'."),
    };

    public IRootDock CreateLayout(LayoutNode saved)
    {
        var used = new HashSet<string>();
        var content = Build(saved, used);
        if (!used.SetEquals(ToolIds))
        {
            throw new InvalidDataException("Saved layout does not contain every tool exactly once.");
        }

        return Root(content);
    }

    private IDockable Build(LayoutNode node, HashSet<string> used)
    {
        switch (node.Kind)
        {
            case "tools":
                var tools = node.Tools.Select(id => used.Add(id) ? CreateTool(id) : throw new InvalidDataException($"Duplicate tool '{id}'.")).ToList();
                if (tools.Count == 0)
                {
                    throw new InvalidDataException("Empty tool group.");
                }

                return new ToolDock
                {
                    Id = node.Id ?? Guid.NewGuid().ToString("N"),
                    Proportion = node.Proportion ?? double.NaN,
                    VisibleDockables = CreateList<IDockable>([.. tools]),
                    ActiveDockable = tools.FirstOrDefault(t => t.Id == node.ActiveTool) ?? tools[0],
                };
            case "split":
                var children = node.Children.Select(c => Build(c, used)).ToList();
                NormalizeProportions(children);
                return new ProportionalDock
                {
                    Id = node.Id ?? Guid.NewGuid().ToString("N"),
                    Orientation = node.Orientation,
                    Proportion = node.Proportion ?? double.NaN,
                    VisibleDockables = CreateList<IDockable>([.. children]),
                };
            case "splitter":
                return new ProportionalDockSplitter();
            default:
                throw new InvalidDataException($"Unknown layout node '{node.Kind}'.");
        }
    }

    private const double MinPaneProportion = 0.1;

    /// <summary>
    /// A saved split where one side collapsed to ~0 (e.g. after a pane was dragged away) would restore a
    /// layout that shows only one pane. Any sibling below the minimum share resets the split to equal parts.
    /// </summary>
    private static void NormalizeProportions(List<IDockable> children)
    {
        var panes = children.Where(c => c is not IProportionalDockSplitter).ToList();
        if (panes.Count < 2)
        {
            return;
        }

        var shares = panes.Select(p => double.IsFinite(p.Proportion) ? p.Proportion : double.NaN).ToList();
        var known = shares.Where(double.IsFinite).ToList();
        var total = known.Sum();
        var broken = shares.Any(x => double.IsFinite(x) && x < MinPaneProportion) || (known.Count == panes.Count && Math.Abs(total - 1) > 0.05);
        if (!broken)
        {
            return;
        }

        foreach (var pane in panes)
        {
            pane.Proportion = 1.0 / panes.Count;
        }
    }

    private IRootDock Root(IDockable content)
    {
        var root = CreateRootDock();
        root.Id = "Root";
        root.VisibleDockables = CreateList<IDockable>(content);
        root.ActiveDockable = content;
        root.DefaultDockable = content;
        return root;
    }

    public static readonly IReadOnlyDictionary<string, string> HomeDocks = new Dictionary<string, string>
    {
        ["AddressSpace"] = "LeftDock",
        ["Attributes"] = "AttributesDock",
        ["Watch"] = "WatchDock",
        ["Recordings"] = "WatchDock",
    };

    /// <summary>
    /// Brings a pane back into the main window whatever state it is in: floating, hidden, pinned,
    /// in a closed floating window (re-created), or already docked (just activated).
    /// </summary>
    public PaneTool ShowPane(IRootDock root, string id)
    {
        var tool = FindAnywhere(root, id);
        if (tool is not null && IsDockedInMain(root, tool))
        {
            SetActiveDockable(tool);
            return tool;
        }

        if (tool is not null)
        {
            if (IsDockablePinned(tool, root))
            {
                UnpinDockable(tool);
                if (IsDockedInMain(root, tool))
                {
                    SetActiveDockable(tool);
                    return tool;
                }
            }

            root.HiddenDockables?.Remove(tool);
            if (tool.Owner is IDock owner && owner.VisibleDockables?.Contains(tool) == true)
            {
                RemoveDockable(tool, true);
            }
        }

        tool ??= CreateTool(id);
        tool.CanFloat = false;
        var target = FindInMain(root, d => d is IToolDock && d.Id == HomeDocks[id]) as IDock
            ?? FindInMain(root, d => d is IToolDock) as IDock;
        if (target is null)
        {
            throw new InvalidOperationException("Main layout has no tool area; use View ▸ Reset Layout.");
        }

        AddDockable(target, tool);
        SetActiveDockable(tool);
        CloseEmptyWindows(root);
        return tool;
    }

    /// <summary>
    /// Explicit "pop out": panes cannot float by dragging (that happened by accident too often),
    /// only through this command. Docking back resets <see cref="IDockable.CanFloat"/>.
    /// </summary>
    public void FloatPane(IRootDock root, string id)
    {
        var tool = FindAnywhere(root, id) ?? ShowPane(root, id);
        if (!IsDockedInMain(root, tool))
        {
            return;
        }

        tool.CanFloat = true;
        FloatDockable(tool);
    }

    public void DockAllPanes(IRootDock root)
    {
        foreach (var id in ToolIds)
        {
            ShowPane(root, id);
        }

        CloseEmptyWindows(root);
    }

    public static bool IsPaneDocked(IRootDock root, string id) => FindAnywhere(root, id) is { } t && IsDockedInMain(root, t);

    private void CloseEmptyWindows(IRootDock root)
    {
        foreach (var window in (root.Windows ?? []).ToList())
        {
            if (window.Layout is null || !Descendants(window.Layout).OfType<PaneTool>().Any())
            {
                RemoveWindow(window);
            }
        }
    }

    private static PaneTool? FindAnywhere(IRootDock root, string id) =>
        Descendants(root)
            .Concat((root.Windows ?? []).Where(w => w.Layout is not null).SelectMany(w => Descendants(w.Layout!)))
            .Concat(root.HiddenDockables ?? [])
            .Concat(root.LeftPinnedDockables ?? []).Concat(root.RightPinnedDockables ?? [])
            .Concat(root.TopPinnedDockables ?? []).Concat(root.BottomPinnedDockables ?? [])
            .OfType<PaneTool>()
            .FirstOrDefault(t => t.Id == id);

    private static bool IsDockedInMain(IRootDock root, IDockable tool) =>
        Descendants(root).Contains(tool) && !(root.HiddenDockables?.Contains(tool) ?? false);

    private static IDockable? FindInMain(IRootDock root, Func<IDockable, bool> predicate) => Descendants(root).FirstOrDefault(predicate);

    private static IEnumerable<IDockable> Descendants(IDockable dockable)
    {
        yield return dockable;
        if (dockable is IDock dock)
        {
            foreach (var child in (dock.VisibleDockables ?? []).SelectMany(Descendants))
            {
                yield return child;
            }
        }
    }

    public override IRootDock CreateLayout()
    {
        var addressSpace = new AddressSpaceTool(main);
        var attributes = new AttributesTool(main);
        var watch = new WatchTool(main);
        var recordings = new RecordingsTool(main);

        var left = new ToolDock
        {
            Id = "LeftDock",
            Proportion = 0.3,
            Alignment = Alignment.Left,
            VisibleDockables = CreateList<IDockable>(addressSpace),
            ActiveDockable = addressSpace,
        };

        var right = new ProportionalDock
        {
            Id = "RightDock",
            Proportion = 0.7,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock
                {
                    Id = "AttributesDock",
                    Proportion = 0.45,
                    VisibleDockables = CreateList<IDockable>(attributes),
                    ActiveDockable = attributes,
                },
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    Id = "WatchDock",
                    Proportion = 0.55,
                    VisibleDockables = CreateList<IDockable>(watch, recordings),
                    ActiveDockable = watch,
                }),
        };

        var mainLayout = new ProportionalDock
        {
            Id = "MainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), right),
        };

        return Root(mainLayout);
    }
}
