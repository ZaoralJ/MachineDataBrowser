using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(OpcUaBrowser.App.Tests.TestAppBuilder))]

namespace OpcUaBrowser.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        DockBehavior.Configure();
        return AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
