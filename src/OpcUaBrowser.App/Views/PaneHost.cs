using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

/// <summary>
/// Wraps a pane's content. When the pane is shown outside the main window (floating), a bar with a
/// "Dock back" button appears, because dragging a floating window back onto a drop target is unreliable.
/// </summary>
public sealed class PaneHost : DockPanel
{
    public static readonly StyledProperty<string?> PaneIdProperty = AvaloniaProperty.Register<PaneHost, string?>(nameof(PaneId));

    private readonly Border _bar;

    public PaneHost()
    {
        var button = new Button { Content = "Dock back to main window", Padding = new Thickness(10, 3) };
        button.Classes.Add("accent");
        button.Click += (_, _) =>
        {
            if (Pane?.DataContext is MainWindowViewModel vm && PaneId is { } id)
            {
                vm.ShowPaneCommand.Execute(id);
            }
        };

        _bar = new Border
        {
            Padding = new Thickness(8, 4),
            IsVisible = false,
            Child = new DockPanel
            {
                Children =
                {
                    Right(button),
                    new TextBlock { Text = "Floating pane", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 },
                },
            },
        };
        _bar.Bind(Border.BackgroundProperty, _bar.GetResourceObservable("SystemControlBackgroundChromeMediumLowBrush"));
        SetDock(_bar, Avalonia.Controls.Dock.Top);
        Children.Add(_bar);
    }

    public string? PaneId
    {
        get => GetValue(PaneIdProperty);
        set => SetValue(PaneIdProperty, value);
    }

    public Control? Pane
    {
        get => Children.Count > 1 ? Children[1] : null;
        set
        {
            while (Children.Count > 1)
            {
                Children.RemoveAt(1);
            }

            if (value is not null)
            {
                Children.Add(value);
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _bar.IsVisible = TopLevel.GetTopLevel(this) is not MainWindow;
    }

    private static Control Right(Control c)
    {
        SetDock(c, Avalonia.Controls.Dock.Right);
        return c;
    }
}
