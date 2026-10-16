using System.Globalization;
using MachineDataBrowser.Core;
using ModelContextProtocol;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>Checks what the device says about a variable before showing a write confirmation.</summary>
internal static class WritePreflight
{
    public static async Task<string?> CheckAsync(IDeviceClient client, DeviceNodeId nodeId, string text, CancellationToken cancellationToken)
    {
        IReadOnlyList<AttributeValue> attributes;
        try
        {
            attributes = await client.ReadAttributesAsync(nodeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceClientException or NotSupportedException)
        {
            return $"could not read write metadata: {ex.Message}";
        }

        var values = attributes.ToDictionary(a => a.Name, a => a.Value, StringComparer.OrdinalIgnoreCase);
        if (values.TryGetValue("NodeClass", out var nodeClass) && nodeClass != nameof(DeviceNodeKind.Variable))
        {
            return $"node class is {nodeClass}, not Variable";
        }

        // Prefer the user's effective permission; fall back to the server's access level on drivers without it.
        var access = values.GetValueOrDefault("UserAccessLevel") ?? values.GetValueOrDefault("AccessLevel");
        if (access is { Length: > 0 } && access.StartsWith("Bad", StringComparison.OrdinalIgnoreCase))
        {
            return $"device could not read write access metadata ('{access}'); cannot verify the variable is writable";
        }

        if (access is { Length: > 0 }
            && !access.Split(',', StringSplitOptions.TrimEntries).Contains("Write", StringComparer.OrdinalIgnoreCase))
        {
            return $"device metadata says access is '{access}' (not writable)";
        }

        var dataTypeText = values.GetValueOrDefault("DataType");
        if (dataTypeText is null || !TryDataType(dataTypeText, out var dataType))
        {
            var isJson = dataTypeText?.StartsWith("JSON ", StringComparison.OrdinalIgnoreCase) == true
                || values.ContainsKey("PayloadFormat")
                || values.TryGetValue("ContentType", out var contentType) && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
            if (isJson)
            {
                try
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(text);
                    if (string.Equals(dataTypeText, "JSON object", StringComparison.OrdinalIgnoreCase) && json is not System.Text.Json.Nodes.JsonObject
                        || string.Equals(dataTypeText, "JSON array", StringComparison.OrdinalIgnoreCase) && json is not System.Text.Json.Nodes.JsonArray)
                    {
                        return $"'{text}' does not match the device's {dataTypeText} payload type";
                    }

                    return null;
                }
                catch (System.Text.Json.JsonException ex)
                {
                    return $"'{text}' is not valid JSON: {ex.Message}";
                }
            }

            return $"device metadata has no supported writable data type ('{values.GetValueOrDefault("DataType") ?? "unknown"}'); cannot validate the value";
        }

        var isArray = IsArray(values);
        object parsed;
        try
        {
            parsed = ValueParser.Parse(text, dataType, isArray);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException or ArgumentException)
        {
            return $"'{text}' is not a valid {dataType}{(isArray ? "[]" : string.Empty)} value: {ex.Message}";
        }

        if (client is IEngineeringRangeSource rangeSource && dataType is DeviceDataType.SByte or DeviceDataType.Byte
            or DeviceDataType.Int16 or DeviceDataType.UInt16 or DeviceDataType.Int32 or DeviceDataType.UInt32
            or DeviceDataType.Int64 or DeviceDataType.UInt64 or DeviceDataType.Float or DeviceDataType.Double)
        {
            try
            {
                var ranges = await rangeSource.ReadRangesAsync([nodeId], cancellationToken).ConfigureAwait(false);
                if (ranges.Count > 0 && ranges[0] is { } range)
                {
                    foreach (var number in Numbers(parsed))
                    {
                        if (!double.IsFinite(number))
                        {
                            return "non-finite numbers (NaN or Infinity) are not valid engineering values";
                        }

                        if (number < range.Low || number > range.High)
                        {
                            return $"{number.ToString(CultureInfo.InvariantCulture)} is outside the device EURange "
                                + $"{range.Low.ToString(CultureInfo.InvariantCulture)}..{range.High.ToString(CultureInfo.InvariantCulture)}";
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is DeviceClientException or NotSupportedException)
            {
                // Ranges are best-effort metadata. Access and type checks still apply if the optional read is unavailable.
            }
        }

        return null;
    }

    private static IEnumerable<double> Numbers(object parsed)
    {
        if (parsed is Array array)
        {
            foreach (var value in array)
            {
                if (value is not null)
                {
                    yield return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
            }
        }
        else if (parsed is IConvertible)
        {
            yield return Convert.ToDouble(parsed, CultureInfo.InvariantCulture);
        }
    }

    private static bool IsArray(Dictionary<string, string> attributes) =>
        attributes.TryGetValue("ValueRank", out var rank) && rank is not "Scalar" and not "ScalarOrOneDimension" and not "Any"
        || attributes.TryGetValue("ArrayDimensions", out var dimensions) && dimensions.Length > 0;

    private static bool TryDataType(string text, out DeviceDataType type)
    {
        var name = text.Trim().Split([' ', '('], 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        type = name switch
        {
            "BOOL" or "BOOLEAN" => DeviceDataType.Boolean,
            "SINT" or "SBYTE" => DeviceDataType.SByte,
            "USINT" or "BYTE" => DeviceDataType.Byte,
            "INT" or "INT16" => DeviceDataType.Int16,
            "UINT" or "UINT16" => DeviceDataType.UInt16,
            "DINT" or "INT32" => DeviceDataType.Int32,
            "UDINT" or "UINT32" => DeviceDataType.UInt32,
            "LINT" or "INT64" => DeviceDataType.Int64,
            "ULINT" or "UINT64" => DeviceDataType.UInt64,
            "REAL" or "FLOAT" => DeviceDataType.Float,
            "LREAL" or "DOUBLE" or "NUMBER" => DeviceDataType.Double,
            "INTEGER" => DeviceDataType.Int64,
            "STRING" or "STR" => DeviceDataType.String,
            "DATETIME" or "DATE" => DeviceDataType.DateTime,
            "GUID" or "UUID" => DeviceDataType.Guid,
            "LOCALIZEDTEXT" => DeviceDataType.LocalizedText,
            "QUALIFIEDNAME" => DeviceDataType.QualifiedName,
            _ => (DeviceDataType)(-1),
        };
        return Enum.IsDefined(type);
    }
}
