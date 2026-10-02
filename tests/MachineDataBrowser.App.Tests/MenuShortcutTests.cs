using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class MenuShortcutTests
{
    [AvaloniaFact]
    public void Every_menu_command_has_a_shortcut_and_header_buttons_show_it()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(), Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Dynamic lists (recent sessions / endpoints) are the only commands without a fixed shortcut.
        var dynamic = new[] { "Clear Recent" };
        var missing = Leaves(NativeMenu.GetMenu(window)!)
            .Where(i => i.Command is not null && i.Gesture is null && i.CommandParameter is not string && !dynamic.Contains(i.Header))
            .Select(i => i.Header)
            .ToList();
        Assert.Empty(missing);

        var disconnect = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Disconnect");
        Assert.EndsWith(")", ToolTip.GetTip(disconnect) as string, StringComparison.Ordinal);
        Assert.Contains("Disconnect", Shortcuts.Overview(), StringComparison.Ordinal);
        window.Close();
    }

    private static IEnumerable<NativeMenuItem> Leaves(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().SelectMany(i => i.Menu is { } sub ? Leaves(sub) : [i]);
}
