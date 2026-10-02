using System.Globalization;
using Opc.Ua;

namespace OpcUaBrowser.Core;

/// <summary>Parses text typed by the user into values for writing.</summary>
public static class ValueParser
{
    /// <summary>Converts user input to a value of <paramref name="type"/>; arrays are comma-separated, optionally in brackets.</summary>
    public static object Parse(string text, BuiltInType type, bool isArray)
    {
        switch (type)
        {
            case BuiltInType.Null or BuiltInType.Variant or BuiltInType.ExtensionObject or BuiltInType.DataValue
                or BuiltInType.DiagnosticInfo or BuiltInType.XmlElement:
                throw new NotSupportedException($"Writing values of type {type} is not supported.");
        }

        if (!isArray)
        {
            return ParseScalar(text.Trim(), type);
        }

        var body = text.Trim();
        if (body.StartsWith('[') && body.EndsWith(']'))
        {
            body = body[1..^1];
        }

        var parts = body.Trim().Length == 0 ? [] : body.Split(',').Select(p => p.Trim()).ToArray();
        var elements = Array.CreateInstance(TypeInfo.GetSystemType(type, ValueRanks.Scalar), parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            elements.SetValue(ParseScalar(parts[i], type), i);
        }

        return elements;
    }

    private static object ParseScalar(string text, BuiltInType type)
    {
        if (type is BuiltInType.String or BuiltInType.LocalizedText or BuiltInType.QualifiedName
            && text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
        {
            text = text[1..^1];
        }

        try
        {
            return type switch
            {
                BuiltInType.Boolean => text.ToLowerInvariant() switch
                {
                    "true" or "1" or "on" or "yes" => true,
                    "false" or "0" or "off" or "no" => false,
                    _ => throw new FormatException("Expected true or false."),
                },
                BuiltInType.String => text,
                BuiltInType.LocalizedText => new LocalizedText(text),
                BuiltInType.DateTime => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal),
                BuiltInType.Float => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                BuiltInType.Double => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                _ => TypeInfo.Cast(text, type) ?? throw new FormatException(),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ServiceResultException)
        {
            throw new FormatException($"'{text}' is not a valid {type}: {ex.Message}", ex);
        }
    }
}
