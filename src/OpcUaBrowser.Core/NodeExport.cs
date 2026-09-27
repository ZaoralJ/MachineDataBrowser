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

    public static string ToJsonString(NodeTree node) =>
        ToJson(node)?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null";

    /// <summary>C# classes mirroring the subtree: one class per object/structured variable, one property per child.</summary>
    /// <param name="asRecord">Emit <c>sealed record</c> types with <c>init</c> accessors instead of mutable classes.</param>
    public static string ToCSharp(NodeTree node, bool asRecord = false)
    {
        var sb = new StringBuilder();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        WriteClass(sb, node, UniqueClassName(Identifier(node.DisplayName), emitted), emitted, asRecord);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void WriteClass(StringBuilder sb, NodeTree node, string className, HashSet<string> emitted, bool asRecord)
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

            body.Append(CultureInfo.InvariantCulture, $"    /// <summary>{Escape(child.NodeId.ToString())}</summary>{Environment.NewLine}");
            body.Append(CultureInfo.InvariantCulture, $"    public {type} {property} {{ get; {(asRecord ? "init" : "set")}; }}{Default(type)}{Environment.NewLine}");
            body.AppendLine();
        }

        sb.Append(CultureInfo.InvariantCulture, $"/// <summary>{Escape(node.NodeId.ToString())}</summary>{Environment.NewLine}");
        sb.Append(CultureInfo.InvariantCulture, $"public sealed {(asRecord ? "record" : "class")} {className}{Environment.NewLine}{{{Environment.NewLine}");
        sb.Append(body.ToString().TrimEnd());
        sb.AppendLine();
        sb.AppendLine("}");
        sb.AppendLine();

        foreach (var (child, childClass) in nested)
        {
            WriteClass(sb, child, childClass, emitted, asRecord);
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
