using Opc.Ua;

namespace MachineDataBrowser.Core.Ua;

internal static class UaValueParser
{
    public static DeviceDataType ToDeviceDataType(BuiltInType type) => type switch
    {
        BuiltInType.Boolean => DeviceDataType.Boolean,
        BuiltInType.SByte => DeviceDataType.SByte,
        BuiltInType.Byte => DeviceDataType.Byte,
        BuiltInType.Int16 => DeviceDataType.Int16,
        BuiltInType.UInt16 => DeviceDataType.UInt16,
        BuiltInType.Int32 => DeviceDataType.Int32,
        BuiltInType.UInt32 => DeviceDataType.UInt32,
        BuiltInType.Int64 => DeviceDataType.Int64,
        BuiltInType.UInt64 => DeviceDataType.UInt64,
        BuiltInType.Float => DeviceDataType.Float,
        BuiltInType.Double => DeviceDataType.Double,
        BuiltInType.String => DeviceDataType.String,
        BuiltInType.DateTime => DeviceDataType.DateTime,
        BuiltInType.Guid => DeviceDataType.Guid,
        BuiltInType.ByteString => DeviceDataType.ByteString,
        BuiltInType.XmlElement => DeviceDataType.XmlElement,
        BuiltInType.NodeId => DeviceDataType.NodeId,
        BuiltInType.ExpandedNodeId => DeviceDataType.ExpandedNodeId,
        BuiltInType.StatusCode => DeviceDataType.StatusCode,
        BuiltInType.QualifiedName => DeviceDataType.QualifiedName,
        BuiltInType.LocalizedText => DeviceDataType.LocalizedText,
        BuiltInType.ExtensionObject => DeviceDataType.ExtensionObject,
        BuiltInType.DataValue => DeviceDataType.DataValue,
        BuiltInType.Variant => DeviceDataType.Variant,
        BuiltInType.DiagnosticInfo => DeviceDataType.DiagnosticInfo,
        _ => DeviceDataType.Null,
    };

    public static object Parse(string text, BuiltInType type, bool isArray)
        => Parse(text, ToDeviceDataType(type), isArray);

    public static object Parse(string text, DeviceDataType type, bool isArray)
    {
        var parsed = ValueParser.Parse(text, type, isArray);
        if (type is DeviceDataType.LocalizedText or DeviceDataType.QualifiedName)
        {
            object ConvertValue(object value) => type == DeviceDataType.LocalizedText
                ? new LocalizedText((string)value)
                : new QualifiedName((string)value);

            if (parsed is Array array)
            {
                var converted = Array.CreateInstance(type == DeviceDataType.LocalizedText ? typeof(LocalizedText) : typeof(QualifiedName), array.Length);
                for (var i = 0; i < array.Length; i++)
                {
                    converted.SetValue(ConvertValue(array.GetValue(i)!), i);
                }

                return converted;
            }

            return ConvertValue(parsed);
        }

        return parsed;
    }
}
