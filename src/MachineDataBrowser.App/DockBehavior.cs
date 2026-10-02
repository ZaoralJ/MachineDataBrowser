using Dock.Settings;

namespace MachineDataBrowser.App;

internal static class DockBehavior
{
    public static void Configure()
    {
        // A tab needs a deliberate drag before it detaches; small twitches while clicking tabs used to float panes.
        DockSettings.MinimumHorizontalDragDistance = 16;
        DockSettings.MinimumVerticalDragDistance = 16;

        // Drop indicators in their own overlay window, so they stay visible above grids/trees during a drag.
        DockSettings.UseFloatingDockAdorner = true;
        DockSettings.ShowDockablePreviewOnDrag = true;

        // Floating panes belong to the main window: they stay above it, move with it and close with it.
        DockSettings.UseOwnerForFloatingWindows = true;
        DockSettings.FloatingWindowOwnerPolicy = DockFloatingWindowOwnerPolicy.AlwaysOwned;
        DockSettings.CloseFloatingWindowsOnMainWindowClose = true;
        DockSettings.BringWindowsToFrontOnDrag = true;
    }
}
