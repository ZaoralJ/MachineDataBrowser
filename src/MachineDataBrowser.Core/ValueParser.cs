using System.Globalization;

namespace MachineDataBrowser.Core;

/// <summary>Parses text typed by the user into values for writing.</summary>
public static class ValueParser
{
    /// <summary>Converts user input to a value of <paramref name="type"/>; arrays are comma-separated, optionally in brackets.</summary>
    public static object Parse(string text, DeviceDataType type, bool isArray)
    {
        switch (type)
        {
            case DeviceDataType.Null or DeviceDataType.Variant or DeviceDataType.ExtensionObject or DeviceDataType.DataValue
                or DeviceDataType.DiagnosticInfo or DeviceDataType.XmlElement or DeviceDataType.NodeId
                or DeviceDataType.ExpandedNodeId or DeviceDataType.StatusCode or DeviceDataType.ByteString:
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
        var elements = Array.CreateInstance(SystemType(type), parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            elements.SetValue(ParseScalar(parts[i], type), i);
        }

        return elements;
    }

    private static object ParseScalar(string text, DeviceDataType type)
    {
        if (type is DeviceDataType.String or DeviceDataType.LocalizedText or DeviceDataType.QualifiedName
            && text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
        {
            text = text[1..^1];
        }

        try
        {
            return type switch
            {
                DeviceDataType.Boolean => text.ToLowerInvariant() switch
                {
                    "true" or "1" or "on" or "yes" => true,
                    "false" or "0" or "off" or "no" => false,
                    _ => throw new FormatException("Expected true or false."),
                },
                DeviceDataType.String or DeviceDataType.LocalizedText or DeviceDataType.QualifiedName => text,
                DeviceDataType.DateTime => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal),
                DeviceDataType.Guid => Guid.Parse(text),
                DeviceDataType.Float => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                DeviceDataType.Double => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                _ => System.Convert.ChangeType(text, SystemType(type), CultureInfo.InvariantCulture),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            throw new FormatException($"'{text}' is not a valid {type}: {ex.Message}", ex);
        }
    }

    private static Type SystemType(DeviceDataType type) => type switch
    {
        DeviceDataType.Boolean => typeof(bool),
        DeviceDataType.SByte => typeof(sbyte),
        DeviceDataType.Byte => typeof(byte),
        DeviceDataType.Int16 => typeof(short),
        DeviceDataType.UInt16 => typeof(ushort),
        DeviceDataType.Int32 => typeof(int),
        DeviceDataType.UInt32 => typeof(uint),
        DeviceDataType.Int64 => typeof(long),
        DeviceDataType.UInt64 => typeof(ulong),
        DeviceDataType.Float => typeof(float),
        DeviceDataType.Double => typeof(double),
        DeviceDataType.String or DeviceDataType.LocalizedText or DeviceDataType.QualifiedName => typeof(string),
        DeviceDataType.DateTime => typeof(DateTime),
        DeviceDataType.Guid => typeof(Guid),
        _ => throw new NotSupportedException($"Writing values of type {type} is not supported."),
    };
}
