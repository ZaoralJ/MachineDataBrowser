using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Opc.Ua;

namespace OpcUaBrowser.Core;

/// <summary>A browsed subtree with the current value of each variable.</summary>
public sealed class NodeTree(NodeId nodeId, string displayName, NodeClass nodeClass)
{
    public NodeId NodeId { get; } = nodeId;

    public string DisplayName { get; } = displayName;

    public NodeClass NodeClass { get; } = nodeClass;

    public object? Value { get; set; }

    public List<NodeTree> Children { get; } = [];
}

/// <summary>Turns a <see cref="NodeTree"/> into a JSON document or a C# class skeleton.</summary>
public static class NodeExport
{
    /// <summary>
    /// Objects become JSON objects keyed by child name; variables become their value. A variable with
    /// child variables (e.g. ServerStatus) is an object of its children, since those expose the components.
    /// </summary>
    public static JsonNode? ToJson(NodeTree node)
    {
        if (node.NodeClass == NodeClass.Variable && node.Children.Count == 0)
        {
            return ValueJson.ToJson(node.Value);
        }

        var result = new JsonObject();
        foreach (var (key, child) in UniqueNames(node.Children, n => n.DisplayName))
        {
            result[key] = ToJson(child);
        }

        return result;
    }

    /// <summary>
    /// Combines cherry-picked nodes into the trees to export. Nodes are grouped under their parent, so picking
    /// a few variables of an object yields that object (the class/record name) with only those properties.
    /// A picked node that has picked descendants is kept as a container of just those descendants (and the
    /// nodes on the way to them). A single picked object/structure is exported on its own, as before.
    /// </summary>
    /// <param name="selections">
    /// Each picked node with its ancestors (root first, parent last; children ignored). <c>Tree</c> is the full
    /// subtree for nodes without picked descendants; for the others only its identity is used.
    /// </param>
    public static List<NodeTree> ComposeSelection(IReadOnlyList<(IReadOnlyList<NodeTree> Ancestors, NodeTree Tree)> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var picked = selections.Select(s => s.Tree.NodeId).ToHashSet();
        var containers = selections.SelectMany(s => s.Ancestors).Select(a => a.NodeId).Where(picked.Contains).ToHashSet();

        var roots = new List<(IReadOnlyList<NodeTree> Ancestors, NodeTree Root)>();
        var rootsById = new Dictionary<NodeId, NodeTree>();
        foreach (var (ancestors, tree) in selections)
        {
            if (!ancestors.Any(a => picked.Contains(a.NodeId)) && !rootsById.ContainsKey(tree.NodeId))
            {
                var root = containers.Contains(tree.NodeId) ? Shell(tree) : tree;
                rootsById[tree.NodeId] = root;
                roots.Add((ancestors, root));
            }
        }

        foreach (var (ancestors, tree) in selections)
        {
            var outer = ancestors.Select((a, i) => (a, i)).FirstOrDefault(x => picked.Contains(x.a.NodeId));
            if (outer.a is null)
            {
                continue;
            }

            var current = rootsById[outer.a.NodeId];
            foreach (var step in ancestors.Skip(outer.i + 1))
            {
                current = GetOrAdd(current, step);
            }

            if (current.Children.All(c => c.NodeId != tree.NodeId))
            {
                current.Children.Add(containers.Contains(tree.NodeId) ? Shell(tree) : tree);
            }
        }

        // Groups whose parent lies inside another group's tree are nested there (picks in ServerStatus and in
        // ServerStatus/BuildInfo give one ServerStatus with a BuildInfo property), so process shallow groups first.
        var result = new List<NodeTree>();
        var placed = new List<(NodeTree Tree, IReadOnlyList<NodeTree> Ancestors)>();
        foreach (var group in roots.GroupBy(r => r.Ancestors.Count > 0 ? r.Ancestors[^1].NodeId : NodeId.Null)
                     .OrderBy(g => g.First().Ancestors.Count))
        {
            var items = group.ToList();
            var ancestors = items[0].Ancestors;
            var parent = ancestors.Count > 0 ? ancestors[^1] : null;

            var host = placed
                .Select(p => (p.Tree, Index: IndexOf(ancestors, p.Tree.NodeId)))
                .Where(p => p.Index >= 0)
                .OrderByDescending(p => p.Index)
                .FirstOrDefault();
            if (host.Tree is not null)
            {
                var current = host.Tree;
                foreach (var step in ancestors.Skip(host.Index + 1))
                {
                    current = GetOrAdd(current, step);
                }

                current.Children.AddRange(items.Select(i => i.Root).Where(r => current.Children.All(c => c.NodeId != r.NodeId)));
                continue;
            }

            if (parent is null || (items.Count == 1 && !IsLeaf(items[0].Root)))
            {
                foreach (var item in items)
                {
                    result.Add(item.Root);
                    placed.Add((item.Root, item.Ancestors));
                }

                continue;
            }

            var shell = Shell(parent);
            shell.Children.AddRange(items.Select(i => i.Root));
            result.Add(shell);
            placed.Add((shell, ancestors.Take(ancestors.Count - 1).ToList()));
        }

        return result;

        static int IndexOf(IReadOnlyList<NodeTree> path, NodeId id)
        {
            for (var i = 0; i < path.Count; i++)
            {
                if (path[i].NodeId == id)
                {
                    return i;
                }
            }

            return -1;
        }

        static NodeTree Shell(NodeTree n) => new(n.NodeId, n.DisplayName, n.NodeClass) { Value = n.Value };

        static NodeTree GetOrAdd(NodeTree parent, NodeTree step)
        {
            var existing = parent.Children.FirstOrDefault(c => c.NodeId == step.NodeId);
            if (existing is null)
            {
                existing = Shell(step);
                parent.Children.Add(existing);
            }

            return existing;
        }
    }

    public static string ToJsonString(NodeTree node) =>
        ToJson(node)?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null";

    /// <summary>C# classes mirroring the subtree: one class per object/structured variable, one property per child.</summary>
    /// <param name="asRecord">Emit <c>sealed record</c> types with <c>init</c> accessors instead of mutable classes.</param>
    /// <param name="formatId">Id text for the <c>summary</c> comments; defaults to <see cref="NodeId.ToString()"/>.</param>
    public static string ToCSharp(NodeTree node, bool asRecord = false, Func<NodeId, string>? formatId = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        formatId ??= id => id.ToString();
        var sb = new StringBuilder();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        WriteClass(sb, node, UniqueClassName(Identifier(node.DisplayName), emitted), emitted, asRecord, formatId);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void WriteClass(StringBuilder sb, NodeTree node, string className, HashSet<string> emitted, bool asRecord, Func<NodeId, string> formatId)
    {
        var nested = new List<(NodeTree Node, string ClassName)>();
        var body = new StringBuilder();
        var names = new HashSet<string>(StringComparer.Ordinal) { className };
        foreach (var child in node.Children.Where(c => !IsEmptyType(c)))
        {
            var property = UniqueClassName(Identifier(child.DisplayName), names);
            string type;
            if (child.NodeClass == NodeClass.Variable && child.Children.Count == 0)
            {
                type = ClrType(child.Value);
            }
            else
            {
                type = UniqueClassName(Identifier(child.DisplayName), emitted);
                nested.Add((child, type));
            }

            body.Append(CultureInfo.InvariantCulture, $"    /// <summary>{Escape(formatId(child.NodeId))}</summary>{Environment.NewLine}");
            body.Append(CultureInfo.InvariantCulture, $"    public {type} {property} {{ get; {(asRecord ? "init" : "set")}; }}{Default(type)}{Environment.NewLine}");
            body.AppendLine();
        }

        sb.Append(CultureInfo.InvariantCulture, $"/// <summary>{Escape(formatId(node.NodeId))}</summary>{Environment.NewLine}");
        sb.Append(CultureInfo.InvariantCulture, $"public sealed {(asRecord ? "record" : "class")} {className}{Environment.NewLine}{{{Environment.NewLine}");
        sb.Append(body.ToString().TrimEnd());
        sb.AppendLine();
        sb.AppendLine("}");
        sb.AppendLine();

        foreach (var (child, childClass) in nested)
        {
            WriteClass(sb, child, childClass, emitted, asRecord, formatId);
        }
    }

    private static bool IsLeaf(NodeTree node) => node.NodeClass == NodeClass.Variable && node.Children.Count == 0;

    /// <summary>A node that would become a class/record without any properties (folders, methods-only objects, ...).</summary>
    private static bool IsEmptyType(NodeTree node) => !IsLeaf(node) && node.Children.All(IsEmptyType);

    private static string UniqueClassName(string baseName, HashSet<string> taken)
    {
        var name = baseName;
        for (var i = 2; !taken.Add(name); i++)
        {
            name = baseName + i.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    private static string Default(string type) =>
        type == "string" ? " = string.Empty;" : type.EndsWith("[]", StringComparison.Ordinal) ? " = [];" : type.EndsWith('?') || IsValueType(type) ? string.Empty : " = new();";

    private static bool IsValueType(string type) =>
        type is "bool" or "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong"
            or "float" or "double" or "decimal" or "DateTime" or "Guid";

    public static string ClrType(object? value) => value switch
    {
        null => "object?",
        bool => "bool",
        sbyte => "sbyte",
        byte => "byte",
        short => "short",
        ushort => "ushort",
        int => "int",
        uint => "uint",
        long => "long",
        ulong => "ulong",
        float => "float",
        double => "double",
        decimal => "decimal",
        string => "string",
        DateTime => "DateTime",
        Uuid or Guid => "Guid",
        byte[] => "byte[]",
        LocalizedText or QualifiedName or NodeId or ExpandedNodeId or StatusCode or System.Xml.XmlElement => "string",
        Matrix m => ElementType(m.Elements) + "[" + new string(',', m.Dimensions.Length - 1) + "]",
        Array a => ElementType(a) + "[]",
        ExtensionObject { Body: IEncodeable e } => e.GetType().Name,
        ExtensionObject => "object",
        IEncodeable e => e.GetType().Name,
        Enum e => e.GetType().Name,
        _ => "object",
    };

    private static string ElementType(Array array)
    {
        var elementType = array.GetType().GetElementType();
        object? sample = array.Length > 0 ? array.GetValue(0) : null;
        if (sample is null && elementType is not null && elementType.IsValueType)
        {
            sample = Activator.CreateInstance(elementType);
        }

        var name = ClrType(sample);
        return name == "object?" ? "object" : name;
    }

    private static IEnumerable<(string Key, T Item)> UniqueNames<T>(IEnumerable<T> items, Func<T, string> name)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = name(item);
            var candidate = key;
            for (var i = 2; !used.Add(candidate); i++)
            {
                candidate = $"{key} ({i})";
            }

            yield return (candidate, item);
        }
    }

    /// <summary>A PascalCase C# identifier from an OPC UA display name.</summary>
    public static string Identifier(string name)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        if (sb.Length == 0)
        {
            return "Node";
        }

        if (char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
