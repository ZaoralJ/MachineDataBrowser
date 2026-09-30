using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Opc.Ua;
using OpcUaBrowser.App.ViewModels;
using OpcUaBrowser.App.Views;
using OpcUaBrowser.Core;
using Xunit;

namespace OpcUaBrowser.App.Tests;

public sealed class ShortcutTests
{
    [AvaloniaFact]
    public void Watch_shortcuts_run_their_commands_and_show_in_the_context_menu()
    {
        var vm = new MainWindowViewModel();
        var good = new WatchItemViewModel(new NodeId(1u, 2), "good");
        good.Apply(new ValueUpdate(good.NodeId, "1", StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow));
        var bad = new WatchItemViewModel(new NodeId(2u, 2), "bad");
        bad.Apply(new ValueUpdate(bad.NodeId, "1", StatusCodes.BadNodeIdUnknown, DateTime.UtcNow, DateTime.UtcNow));
        vm.WatchItems.Add(good);
        vm.WatchItems.Add(bad);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid");
        grid.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([bad], vm.SelectedWatchItems);

        // A second press removes them, like double-clicking the toolbar button.
        window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([good], vm.WatchItems);

        grid.ContextMenu!.Open(grid);
        Dispatcher.UIThread.RunJobs();
        var menuItems = grid.ContextMenu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(new KeyGesture(Key.B), menuItems.Single(m => m.Header is "Select").Items.OfType<MenuItem>().Single(m => m.Header is "Bad values").InputGesture);
        Assert.Contains(Shortcuts.Registry.Keys, k => k == "Watch");
        Assert.Contains("Select bad values", Shortcuts.Overview(), StringComparison.Ordinal);
        grid.ContextMenu.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Pane_shortcuts_work_after_clicking_anywhere_in_the_pane()
    {
        var vm = new MainWindowViewModel();
        var stale = new WatchItemViewModel(new NodeId(1u, 2), "stale");
        stale.Apply(new ValueUpdate(stale.NodeId, "1", StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow), DateTimeOffset.Now.AddMinutes(-5));
        stale.RefreshAge(DateTimeOffset.Now);
        vm.WatchItems.Add(stale);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Focus elsewhere (the address space), then click the Watch pane's toolbar area and press S.
        window.GetVisualDescendants().OfType<TreeView>().First().Focus();
        var toolbarButton = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Remove all from watch"
            && b.FindAncestorOfType<WatchView>() is not null);
        var at = toolbarButton.TranslatePoint(new Avalonia.Point(toolbarButton.Bounds.Width + 60, toolbarButton.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var focused = window.FocusManager!.GetFocusedElement();
        var pane = window.GetVisualDescendants().OfType<WatchView>().Single();
        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.SelectedWatchItems.Count == 1, $"focused={focused?.GetType().Name} paneBindings={pane.KeyBindings.Count} gridBindings={window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WatchGrid").KeyBindings.Count} hit={window.InputHitTest(at)?.GetType().Name}");
        window.Close();
    }

    [AvaloniaFact]
    public void Pane_shortcuts_go_to_the_pane_under_the_mouse_when_focus_is_elsewhere()
    {
        var vm = new MainWindowViewModel();
        var stale = new WatchItemViewModel(new NodeId(1u, 2), "stale");
        stale.Apply(new ValueUpdate(stale.NodeId, "1", StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow), DateTimeOffset.Now.AddMinutes(-5));
        stale.RefreshAge(DateTimeOffset.Now);
        vm.WatchItems.Add(stale);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<TreeView>().First().Focus();
        var staleButton = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Select stale values");
        window.MouseMove(staleButton.TranslatePoint(new Point(5, 5), window)!.Value); // hovering the button, no click
        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([stale], vm.SelectedWatchItems);
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_in_the_search_box_never_triggers_single_key_shortcuts()
    {
        var vm = new MainWindowViewModel();
        var stale = new WatchItemViewModel(new NodeId(1u, 2), "stale");
        stale.Apply(new ValueUpdate(stale.NodeId, "1", StatusCodes.Good, DateTime.UtcNow, DateTime.UtcNow), DateTimeOffset.Now.AddMinutes(-5));
        stale.RefreshAge(DateTimeOffset.Now);
        vm.WatchItems.Add(stale);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var staleButton = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Select stale values");
        window.MouseMove(staleButton.TranslatePoint(new Point(5, 5), window)!.Value); // pointing at Watch
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox");
        search.Focus();
        Dispatcher.UIThread.RunJobs();

        foreach (var (key, text) in new[] { (PhysicalKey.S, "s"), (PhysicalKey.B, "b"), (PhysicalKey.E, "e"), (PhysicalKey.Digit1, "1") })
        {
            window.KeyPressQwerty(key, RawInputModifiers.None);
            window.KeyTextInput(text);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal("sbe1", vm.SearchText);
        Assert.Empty(vm.SelectedWatchItems); // S/B were typed, not "select stale/bad"
        window.Close();
    }
}
