using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MachineDataBrowser.App.Views;

/// <summary>
/// Applies View ▸ Zoom to every window, not only the main one: each window's content is wrapped in a scaling
/// <see cref="LayoutTransformControl"/> when its template is applied (before the first layout), and explicit window
/// sizes are scaled with it, so dialogs built in code or XAML follow the zoom without opting in.
/// </summary>
internal static class UiZoom
{
    private sealed record Hosted(LayoutTransformControl Host, double Width, double Height, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

    private static readonly ConditionalWeakTable<Window, Hosted> Windows = [];

    public static double Scale { get; private set; } = 1.0;

    // Show() sets IsVisible before it reads Width/Height for the initial size and runs the first layout: the right
    // moment to wrap the content and scale the size (the template is applied later, during that layout).
    public static void Install() =>
        Visual.IsVisibleProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            if (e.NewValue is true)
            {
                Attach(window);
            }
        });

    public static void SetScale(double scale)
    {
        if (Math.Abs(scale - Scale) < 0.0001)
        {
            return;
        }

        Scale = scale;
        foreach (var (window, hosted) in Windows)
        {
            Apply(window, hosted);
        }
    }

    private static void Attach(Window window)
    {
        // The main window zooms its own content (bound to the view model); windows without content have nothing to scale.
        if (window is MainWindow || Windows.TryGetValue(window, out _) || window.Content is not Control content)
        {
            return;
        }

        window.Content = null;
        var host = new LayoutTransformControl { Name = "ZoomHost", Child = content };
        window.Content = host;
        var hosted = new Hosted(host, window.Width, window.Height, window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight);
        Windows.Add(window, hosted);
        Apply(window, hosted);
    }

    private static void Apply(Window window, Hosted hosted)
    {
        hosted.Host.LayoutTransform = Math.Abs(Scale - 1.0) < 0.0001 ? null : new ScaleTransform(Scale, Scale);
        window.Width = Scaled(hosted.Width);
        window.Height = Scaled(hosted.Height);
        window.MinWidth = Scaled(hosted.MinWidth);
        window.MinHeight = Scaled(hosted.MinHeight);
        window.MaxWidth = Scaled(hosted.MaxWidth);
        window.MaxHeight = Scaled(hosted.MaxHeight);
    }

    // NaN (auto) and infinity (no limit) stay as they are.
    private static double Scaled(double value) => double.IsFinite(value) && value > 0 ? value * Scale : value;
}
