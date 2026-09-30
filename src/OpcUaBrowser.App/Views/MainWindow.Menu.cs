using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class MainWindow
{
    private readonly NativeMenu _recentSessionsMenu = new();
    private readonly NativeMenu _recentEndpointsMenu = new();
    private readonly Dictionary<ThemePreference, NativeMenuItem> _themeItems = [];
    private MainWindowViewModel? _menuViewModel;
    private readonly List<Shortcut> _menuShortcuts = [];

    // The macOS native menu exporter cannot swap the window's NativeMenu instance once set
    // ("The menu being updated does not match"), so the menu is built once per view model and
    // only the dynamic submenus / check marks are mutated afterwards.
    private void AttachMenu(MainWindowViewModel vm)
    {
        if (ReferenceEquals(_menuViewModel, vm))
        {
            return;
        }

        if (_menuViewModel is not null)
        {
            _menuViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _menuViewModel = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        if (NativeMenu.GetMenu(this) is { } existing)
        {
            existing.Items.Clear();
            foreach (var item in BuildTopLevelItems(vm))
            {
                existing.Items.Add(item);
            }
        }
        else
        {
            var menu = new NativeMenu();
            foreach (var item in BuildTopLevelItems(vm))
            {
                menu.Items.Add(item);
            }

            NativeMenu.SetMenu(this, menu);
        }

        RefreshDynamicMenuItems();

        // Menu shortcuts in Help ▸ Keyboard Shortcuts and in the tooltips of the header/toolbar buttons.
        Shortcuts.Describe("Menu (anywhere)", this, [.. _menuShortcuts]);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Settings))
        {
            RefreshDynamicMenuItems();
        }
    }

    private List<NativeMenuItemBase> BuildTopLevelItems(MainWindowViewModel vm)
    {
        var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyBindings.Clear();
        _themeItems.Clear();
        _menuShortcuts.Clear();

        return
        [
            Submenu("_File",
                Item("_New Session", vm.NewSessionCommand, new KeyGesture(Key.N, cmd)),
                Item("_Open Session…", vm.OpenSessionCommand, new KeyGesture(Key.O, cmd)),
                new NativeMenuItem(Label("Open _Recent")) { Menu = _recentSessionsMenu },
                new NativeMenuItemSeparator(),
                Item("_Save Session", vm.SaveSessionCommand, new KeyGesture(Key.S, cmd)),
                Item("Save Session _As…", vm.SaveSessionAsCommand, new KeyGesture(Key.S, cmd | KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("_Export Watch List as CSV…", vm.ExportWatchCsvCommand, new KeyGesture(Key.E, cmd)),
                new NativeMenuItemSeparator(),
                Item("Se_ttings…", vm.OpenSettingsCommand, new KeyGesture(Key.OemComma, cmd)),
                OperatingSystem.IsMacOS() ? null : new NativeMenuItemSeparator(),
                OperatingSystem.IsMacOS() ? null : Item("E_xit", new RelayCommandAdapter(Close), new KeyGesture(Key.Q, cmd))),
            Submenu("_Connection",
                Item("_Connect", vm.ConnectCommand, new KeyGesture(Key.Enter, cmd)),
                Item("_Disconnect", vm.DisconnectCommand, new KeyGesture(Key.D, cmd | KeyModifiers.Shift)),
                new NativeMenuItem(Label("Recent _Endpoints")) { Menu = _recentEndpointsMenu },
                new NativeMenuItemSeparator(),
                Item("Show _Certificate Folder", vm.RevealCertificatesCommand, new KeyGesture(Key.K, cmd | KeyModifiers.Alt | KeyModifiers.Shift))),
            Submenu("_View",
                Item("_Find in Address Space…", vm.FocusSearchCommand, new KeyGesture(Key.F, cmd)),
                Item("_Expand All Below Selection", vm.ExpandAllCommand, new KeyGesture(Key.Right, cmd | KeyModifiers.Alt)),
                Item("_Collapse All", vm.CollapseAllCommand, new KeyGesture(Key.Left, cmd | KeyModifiers.Alt)),
                new NativeMenuItemSeparator(),
                Submenu("_Panes",
                    Item("_Address Space", vm.ShowPaneCommand, new KeyGesture(Key.D1, cmd), "AddressSpace"),
                    Item("A_ttributes", vm.ShowPaneCommand, new KeyGesture(Key.D2, cmd), "Attributes"),
                    Item("_Watch", vm.ShowPaneCommand, new KeyGesture(Key.D3, cmd), "Watch"),
                    Item("_Recordings", vm.ShowPaneCommand, new KeyGesture(Key.D4, cmd), "Recordings"),
                    new NativeMenuItemSeparator(),
                    Item("Pop Out Address Space", vm.FloatPaneCommand, new KeyGesture(Key.D1, cmd | KeyModifiers.Alt), "AddressSpace"),
                    Item("Pop Out Attributes", vm.FloatPaneCommand, new KeyGesture(Key.D2, cmd | KeyModifiers.Alt), "Attributes"),
                    Item("Pop Out Watch", vm.FloatPaneCommand, new KeyGesture(Key.D3, cmd | KeyModifiers.Alt), "Watch"),
                    Item("Pop Out Recordings", vm.FloatPaneCommand, new KeyGesture(Key.D4, cmd | KeyModifiers.Alt), "Recordings")),
                Item("_Dock All Floating Panes", vm.DockAllPanesCommand, new KeyGesture(Key.D, cmd | KeyModifiers.Alt)),
                Item("_Reset Layout", vm.ResetLayoutCommand, new KeyGesture(Key.D0, cmd | KeyModifiers.Alt)),
                new NativeMenuItemSeparator(),
                Item("Zoom _In", vm.ZoomInCommand, new KeyGesture(Key.OemPlus, cmd)),
                Item("Zoom _Out", vm.ZoomOutCommand, new KeyGesture(Key.OemMinus, cmd)),
                Item("_Actual Size", vm.ZoomResetCommand, new KeyGesture(Key.D0, cmd)),
                new NativeMenuItemSeparator(),
                Submenu("_Theme",
                    ThemeItem(vm, "Follow _System", ThemePreference.System, new KeyGesture(Key.D7, cmd | KeyModifiers.Alt)),
                    ThemeItem(vm, "_Light", ThemePreference.Light, new KeyGesture(Key.D8, cmd | KeyModifiers.Alt)),
                    ThemeItem(vm, "_Dark", ThemePreference.Dark, new KeyGesture(Key.D9, cmd | KeyModifiers.Alt)))),
            Submenu("_Watch",
                Item("_Monitor Selected Variables", vm.AddToWatchCommand, new KeyGesture(Key.M, cmd | KeyModifiers.Shift)),
                Item("Monitor All Variables in _Folder", vm.MonitorFolderCommand, new KeyGesture(Key.M, cmd | KeyModifiers.Alt | KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("Show Recorded _Values", vm.ViewItemRecordingCommand, new KeyGesture(Key.Y, cmd)),
                Item("_Remove Selected", vm.RemoveFromWatchCommand, new KeyGesture(Key.Back, cmd)),
                Item("_Clear Watch List", vm.ClearWatchCommand, new KeyGesture(Key.Back, cmd | KeyModifiers.Shift))),
            Submenu("_Recording",
                Item("_New Recording (Selected)…", vm.NewRecordingCommand, new KeyGesture(Key.R, cmd)),
                Item("Record _All Monitored…", vm.RecordAllCommand, new KeyGesture(Key.R, cmd | KeyModifiers.Shift)),
                Item("_View Live…", vm.ViewRecordingCommand, new KeyGesture(Key.L, cmd)),
                Item("Recording Se_ttings…", vm.EditRecordingSettingsCommand, new KeyGesture(Key.I, cmd | KeyModifiers.Alt)),
                Item("_Open Recording File…", vm.OpenRecordingFileCommand, new KeyGesture(Key.O, cmd | KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("_Start / Resume", vm.StartRecordingCommand, new KeyGesture(Key.R, cmd | KeyModifiers.Alt)),
                Item("_Pause", vm.PauseRecordingCommand, new KeyGesture(Key.P, cmd | KeyModifiers.Alt)),
                Item("S_top", vm.StopRecordingCommand, new KeyGesture(Key.OemPeriod, cmd | KeyModifiers.Alt)),
                Item("_Reset History", vm.ResetRecordingCommand, new KeyGesture(Key.Back, cmd | KeyModifiers.Alt)),
                new NativeMenuItemSeparator(),
                Item("Export as _CSV…", vm.ExportRecordingCsvCommand, new KeyGesture(Key.E, cmd | KeyModifiers.Alt)),
                Item("Export as _JSON…", vm.ExportRecordingJsonCommand, new KeyGesture(Key.E, cmd | KeyModifiers.Alt | KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("C_lose Recording", vm.CloseRecordingCommand, new KeyGesture(Key.W, cmd | KeyModifiers.Alt | KeyModifiers.Shift))),
            Submenu("_Help",
                Item("_Keyboard Shortcuts", new RelayCommandAdapter(ShowShortcuts), new KeyGesture(Key.OemQuestion, cmd)),
                Item("Show Settings _Folder", vm.RevealSettingsCommand, new KeyGesture(Key.OemComma, cmd | KeyModifiers.Alt | KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("_About Machine Data Browser", vm.ShowAboutCommand, new KeyGesture(Key.I, cmd | KeyModifiers.Alt | KeyModifiers.Shift))),
        ];
    }

    private void RefreshDynamicMenuItems()
    {
        if (_menuViewModel is not { } vm)
        {
            return;
        }

        _recentSessionsMenu.Items.Clear();
        foreach (var path in vm.RecentSessions)
        {
            _recentSessionsMenu.Items.Add(Item(Path.GetFileNameWithoutExtension(path), vm.OpenRecentSessionCommand, parameter: path));
        }

        if (vm.RecentSessions.Count == 0)
        {
            _recentSessionsMenu.Items.Add(new NativeMenuItem("No Recent Sessions") { IsEnabled = false });
        }
        else
        {
            _recentSessionsMenu.Items.Add(new NativeMenuItemSeparator());
            _recentSessionsMenu.Items.Add(Item("Clear Recent", vm.ClearRecentCommand));
        }

        _recentEndpointsMenu.Items.Clear();
        foreach (var url in vm.RecentEndpoints)
        {
            _recentEndpointsMenu.Items.Add(new NativeMenuItem(url) { Command = vm.UseRecentEndpointCommand, CommandParameter = url });
        }

        if (vm.RecentEndpoints.Count == 0)
        {
            _recentEndpointsMenu.Items.Add(new NativeMenuItem("No Recent Endpoints") { IsEnabled = false });
        }

        foreach (var (theme, item) in _themeItems)
        {
            item.IsChecked = vm.Theme == theme;
        }
    }

    private NativeMenuItem ThemeItem(MainWindowViewModel vm, string header, ThemePreference theme, KeyGesture gesture)
    {
        var item = Item(header, vm.SetThemeCommand, gesture, theme);
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = vm.Theme == theme;
        _themeItems[theme] = item;
        return item;
    }

    private static string Label(string header) =>
        OperatingSystem.IsMacOS() ? header.Replace("_", string.Empty, StringComparison.Ordinal) : header;

    private NativeMenuItem Item(string header, ICommand command, KeyGesture? gesture = null, object? parameter = null)
    {
        if (gesture is not null)
        {
            _menuShortcuts.Add(new Shortcut(gesture, command, parameter, header.Replace("_", string.Empty, StringComparison.Ordinal)));
        }

        if (gesture is not null && !OperatingSystem.IsMacOS())
        {
            var binding = new KeyBinding { Gesture = gesture, Command = command };
            if (parameter is not null)
            {
                binding.CommandParameter = parameter;
            }

            KeyBindings.Add(binding);
        }

        return new NativeMenuItem(Label(header)) { Command = command, CommandParameter = parameter, Gesture = gesture };
    }

    private static NativeMenuItem Submenu(string header, params NativeMenuItemBase?[] items)
    {
        var submenu = new NativeMenu();
        foreach (var item in items.OfType<NativeMenuItemBase>())
        {
            submenu.Items.Add(item);
        }

        return new NativeMenuItem(Label(header)) { Menu = submenu };
    }

    private sealed class RelayCommandAdapter(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
