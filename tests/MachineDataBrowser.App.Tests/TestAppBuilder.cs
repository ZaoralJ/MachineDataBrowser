using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(MachineDataBrowser.App.Tests.TestAppBuilder))]

namespace MachineDataBrowser.App.Tests;

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
