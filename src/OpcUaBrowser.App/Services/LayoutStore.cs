using System.Text.Json;
using System.Text.Json.Serialization;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Services;

public sealed record LayoutNode
{
    public required string Kind { get; init; }

    public string? Id { get; init; }

    public Orientation Orientation { get; init; }

    public double? Proportion { get; init; }

    public string? ActiveTool { get; init; }

    public IReadOnlyList<string> Tools { get; init; } = [];

    public IReadOnlyList<LayoutNode> Children { get; init; } = [];
}

/// <summary>
/// Persists the docked arrangement (split orientation, proportions, which tool sits in which tab group).
/// Tools are identified by id and re-created on load; floating windows are re-docked into the default layout.
/// </summary>
public sealed class LayoutStore(string? path = null)
{
    public static string DefaultPath { get; } = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "layout.json");

    private readonly string _path = path ?? DefaultPath;

    public void Save(IRootDock layout)
    {
        if (layout.VisibleDockables is not [var content, ..])
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Capture(content), AppJsonContext.Default.LayoutNode));
        File.Move(temp, _path, overwrite: true);
    }

    public IRootDock? TryLoad(DockFactory factory)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var node = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.LayoutNode);
            return node is null ? null : factory.CreateLayout(node);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static LayoutNode Capture(IDockable dockable) => dockable switch
    {
        IToolDock tools => new LayoutNode
        {
            Kind = "tools",
            Id = tools.Id,
            Proportion = Finite(tools.Proportion),
            ActiveTool = tools.ActiveDockable?.Id,
            Tools = [.. (tools.VisibleDockables ?? []).OfType<PaneTool>().Select(t => t.Id)],
        },
        IProportionalDock split => new LayoutNode
        {
            Kind = "split",
            Id = split.Id,
            Orientation = split.Orientation,
            Proportion = Finite(split.Proportion),
            Children = [.. (split.VisibleDockables ?? []).Select(Capture)],
        },
        IProportionalDockSplitter => new LayoutNode { Kind = "splitter" },
        _ => new LayoutNode { Kind = "unknown" },
    };

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
