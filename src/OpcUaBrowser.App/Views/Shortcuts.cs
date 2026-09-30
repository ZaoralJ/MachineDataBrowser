using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace OpcUaBrowser.App.Views;

/// <summary>One view-scoped shortcut. <c>Cmd</c> in <see cref="Gesture"/> means ⌘ on macOS and Ctrl elsewhere.</summary>
public sealed record Shortcut(string Gesture, ICommand Command, object? Parameter = null, string? Description = null, ICommand? ShowOn = null)
{
    public Shortcut(KeyGesture gesture, ICommand command, object? parameter, string? description)
        : this(gesture.ToString(), command, parameter, description)
    {
    }

    public KeyGesture KeyGesture { get; } = Shortcuts.Parse(Gesture);
}

/// <summary>
/// Registers shortcuts on a view (active while focus is inside it) and shows them where the action already appears:
/// as <see cref="MenuItem.InputGesture"/> in context menus and in the tooltips of toolbar buttons.
/// </summary>
public static class Shortcuts
{
    /// <summary>Every registered shortcut by view, for the Help ▸ Keyboard Shortcuts overview.</summary>
    public static Dictionary<string, IReadOnlyList<Shortcut>> Registry { get; } = [];

    private static readonly HashSet<Button> Annotated = new(ReferenceEqualityComparer.Instance);

    /// <summary>Plain-text overview of all shortcuts, grouped by view (Help ▸ Keyboard Shortcuts).</summary>
    public static string Overview()
    {
        var text = new System.Text.StringBuilder();
        foreach (var (view, shortcuts) in Registry.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            text.AppendLine(view);
            foreach (var group in shortcuts.GroupBy(s => s.Description ?? string.Empty))
            {
                text.Append("   ").Append(string.Join(" / ", group.Select(Display)).PadRight(18)).Append("  ").AppendLine(group.Key);
            }

            text.AppendLine();
        }

        text.AppendLine("Everywhere");
        text.Append("   ").Append(Display(new Shortcut("Cmd+C", NoCommand.Instance)).PadRight(18)).AppendLine("  Copy selected table rows as a table");
        text.Append("   ").Append("Esc".PadRight(18)).AppendLine("  Close the dialog / window");
        return text.ToString().TrimEnd();
    }

    private sealed class NoCommand : ICommand
    {
        public static readonly NoCommand Instance = new();

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => false;

        public void Execute(object? parameter)
        {
        }
    }

    public static KeyGesture Parse(string gesture) =>
        KeyGesture.Parse(gesture.Replace("Cmd", OperatingSystem.IsMacOS() ? "Meta" : "Ctrl", StringComparison.Ordinal));

    public static string Display(Shortcut shortcut) => shortcut.KeyGesture.ToString("p", null);

    public static void Apply(string view, InputElement scope, params Shortcut[] shortcuts) => Register(view, scope, bind: true, shortcuts);

    /// <summary>Lists shortcuts that are bound elsewhere (the app menu) and shows them in <paramref name="scope"/>'s tooltips.</summary>
    public static void Describe(string view, InputElement scope, params Shortcut[] shortcuts) => Register(view, scope, bind: false, shortcuts);

    private static void Register(string view, InputElement scope, bool bind, IReadOnlyList<Shortcut> shortcuts)
    {
        Registry[view] = shortcuts;
        if (bind && scope is Control target && target.FindLogicalAncestorOfType<UserControl>() is { } pane)
        {
            // Bind on the whole pane (grid, toolbar, …), and make the pane active when it is clicked anywhere, so its
            // shortcuts work after using a toolbar button or clicking empty space, not only with focus in the list.
            BindKeys(pane, shortcuts);
            Panes.Add((pane, shortcuts));
            pane.AttachedToVisualTree += (_, _) => RouteToHoveredPane(TopLevel.GetTopLevel(pane));
            RouteToHoveredPane(TopLevel.GetTopLevel(pane));
            pane.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
            {
                if (TopLevel.GetTopLevel(pane)?.FocusManager?.GetFocusedElement() is not Visual focused || !pane.IsVisualAncestorOf(focused))
                {
                    target.Focus();
                }

                GridCopy.SelectSingleRow(target);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        else if (bind)
        {
            BindKeys(scope, shortcuts);
        }

        if (scope is Control control)
        {
            if (control.IsLoaded)
            {
                Annotate(control, shortcuts);
            }
            else
            {
                control.Loaded += (_, _) => Annotate(control, shortcuts);
            }
        }
    }

    private static void Annotate(Control scope, IReadOnlyList<Shortcut> shortcuts)
    {
        var root = scope as Window ?? scope.FindAncestorOfType<UserControl>() ?? (Control?)scope.FindAncestorOfType<Window>() ?? scope;
        foreach (var menu in root.GetLogicalDescendants().OfType<Control>().Prepend(root).Select(c => c.ContextMenu).OfType<ContextMenu>().Distinct())
        {
            // Menu item commands are bindings that resolve only once the menu is attached, so label them on open.
            AnnotateMenu(menu, shortcuts);
            menu.Opened += (_, _) => AnnotateMenu(menu, shortcuts);
        }

        foreach (var button in root.GetVisualDescendants().OfType<Button>())
        {
            if (Find(shortcuts, button.Command, button.CommandParameter) is { } match && ToolTip.GetTip(button) is string tip
                && Annotated.Add(button))
            {
                ToolTip.SetTip(button, $"{tip} ({Display(match)})");
            }
        }
    }

    private static readonly List<(UserControl Pane, IReadOnlyList<Shortcut> Shortcuts)> Panes = [];
    private static readonly HashSet<TopLevel> Routed = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A pane shortcut that nothing handled (focus is in another pane, or nowhere) goes to the pane under the mouse,
    /// so pointing at Watch and pressing S works without clicking into Watch first.
    /// </summary>
    private static void RouteToHoveredPane(TopLevel? top)
    {
        if (top is null || !Routed.Add(top))
        {
            return;
        }

        Point? pointer = null;
        top.AddHandler(InputElement.PointerMovedEvent, (_, e) => pointer = e.GetPosition(top), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(InputElement.PointerExitedEvent, (_, _) => pointer = null, Avalonia.Interactivity.RoutingStrategies.Direct, handledEventsToo: true);
        top.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Handled || pointer is not { } at || (IsTyping(top) && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) == 0))
            {
                return;
            }

            foreach (var (pane, shortcuts) in Panes)
            {
                if (!pane.IsEffectivelyVisible || TopLevel.GetTopLevel(pane) != top
                    || pane.TranslatePoint(default, top) is not { } origin || !new Rect(origin, pane.Bounds.Size).Contains(at))
                {
                    continue;
                }

                if (shortcuts.FirstOrDefault(s => s.KeyGesture.Matches(e)) is { } match && match.Command.CanExecute(match.Parameter))
                {
                    match.Command.Execute(match.Parameter);
                    e.Handled = true;
                }

                return;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    private static void BindKeys(InputElement target, IReadOnlyList<Shortcut> shortcuts)
    {
        foreach (var shortcut in shortcuts)
        {
            var command = IsPlainKey(shortcut.KeyGesture) ? new NotWhileTyping(shortcut.Command, target) : shortcut.Command;
            target.KeyBindings.Add(new KeyBinding { Gesture = shortcut.KeyGesture, Command = command, CommandParameter = shortcut.Parameter! });
        }
    }

    /// <summary>Keys that type text: no ⌘/Ctrl/Alt (Shift alone still types capitals and symbols).</summary>
    private static bool IsPlainKey(KeyGesture gesture) =>
        (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) == 0;

    /// <summary>True while focus is in a text field (search box, number field, …), where plain keys are typing.</summary>
    public static bool IsTyping(Visual? scope) =>
        (scope is null ? null : TopLevel.GetTopLevel(scope)?.FocusManager?.GetFocusedElement()) is TextBox;

    /// <summary>
    /// A single-key shortcut (S, E, 1–7, Delete, Enter, …) is disabled while the user types in a text field, so the key
    /// reaches the field instead of running a command.
    /// </summary>
    private sealed class NotWhileTyping(ICommand inner, InputElement scope) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => inner.CanExecuteChanged += value;
            remove => inner.CanExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter) => !IsTyping(scope as Visual) && inner.CanExecute(parameter);

        public void Execute(object? parameter)
        {
            if (!IsTyping(scope as Visual))
            {
                inner.Execute(parameter);
            }
        }
    }

    private static void AnnotateMenu(ContextMenu menu, IReadOnlyList<Shortcut> shortcuts)
    {
        foreach (var item in menu.GetLogicalDescendants().OfType<MenuItem>())
        {
            if (item.InputGesture is null && Find(shortcuts, item.Command, item.CommandParameter) is { } match)
            {
                item.InputGesture = match.KeyGesture;
            }
        }
    }

    private static Shortcut? Find(IReadOnlyList<Shortcut> shortcuts, ICommand? command, object? parameter) =>
        command is null ? null : shortcuts.FirstOrDefault(s => (ReferenceEquals(s.Command, command) || ReferenceEquals(s.ShowOn, command)) && Equals(s.Parameter, parameter));
}
