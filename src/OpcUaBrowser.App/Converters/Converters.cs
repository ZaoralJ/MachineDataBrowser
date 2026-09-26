using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Opc.Ua;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.Converters;

public static class NodeClassConverters
{
    private static readonly Geometry Folder = StreamGeometry.Parse("M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z");
    private static readonly Geometry Variable = StreamGeometry.Parse("M5.5 7A1.5 1.5 0 1 1 7 5.5 1.5 1.5 0 0 1 5.5 7m15.9 4.6-9-9C12.1 2.2 11.6 2 11 2H4c-1.1 0-2 .9-2 2v7c0 .6.2 1.1.6 1.4l9 9c.4.4.9.6 1.4.6s1-.2 1.4-.6l7-7c.4-.4.6-.9.6-1.4s-.2-1.1-.6-1.4z");
    private static readonly Geometry Method = StreamGeometry.Parse("M7 2v11h3v9l7-12h-4l4-8z");
    private static readonly Geometry Type = StreamGeometry.Parse("M12 2 2 7l10 5 10-5-10-5zm0 13L2 10v7l10 5 10-5v-7l-10 5z");
    private static readonly Geometry View = StreamGeometry.Parse("M12 4.5C7 4.5 2.7 7.6 1 12c1.7 4.4 6 7.5 11 7.5s9.3-3.1 11-7.5c-1.7-4.4-6-7.5-11-7.5zM12 17a5 5 0 1 1 0-10 5 5 0 0 1 0 10zm0-8a3 3 0 1 0 0 6 3 3 0 0 0 0-6z");

    private static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#D4A017"));
    private static readonly IBrush VariableBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush MethodBrush = new SolidColorBrush(Color.Parse("#A855F7"));
    private static readonly IBrush TypeBrush = new SolidColorBrush(Color.Parse("#14B8A6"));
    private static readonly IBrush OtherBrush = new SolidColorBrush(Color.Parse("#8B8D98"));

    public static readonly IValueConverter ToIcon = new FuncValueConverter<NodeClass, Geometry>(nc => nc switch
    {
        NodeClass.Variable => Variable,
        NodeClass.Method => Method,
        NodeClass.ObjectType or NodeClass.VariableType or NodeClass.DataType or NodeClass.ReferenceType => Type,
        NodeClass.View => View,
        _ => Folder,
    });

    public static readonly IValueConverter ToBrush = new FuncValueConverter<NodeClass, IBrush>(nc => nc switch
    {
        NodeClass.Object => FolderBrush,
        NodeClass.Variable => VariableBrush,
        NodeClass.Method => MethodBrush,
        NodeClass.ObjectType or NodeClass.VariableType or NodeClass.DataType or NodeClass.ReferenceType => TypeBrush,
        _ => OtherBrush,
    });
}

public static class ConnectionStateConverters
{
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#8B8D98"));
    private static readonly IBrush Busy = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush Up = new SolidColorBrush(Color.Parse("#22A06B"));

    public static readonly IValueConverter ToBrush = new FuncValueConverter<ConnectionState, IBrush>(state => state switch
    {
        ConnectionState.Connected => Up,
        ConnectionState.Connecting or ConnectionState.Reconnecting => Busy,
        _ => Idle,
    });

    public static readonly IValueConverter ToText = new FuncValueConverter<ConnectionState, string>(state => state switch
    {
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Reconnecting => "Reconnecting…",
        _ => state.ToString(),
    });
}

public sealed class CountToHeaderConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? $"{parameter} ({count})" : parameter;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
