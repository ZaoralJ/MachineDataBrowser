using Avalonia;

namespace MachineDataBrowser.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DockBehavior.Configure();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
