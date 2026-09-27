using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace OpcUaBrowser.App.Views;

internal enum DialogIcon
{
    None,
    Warning,
    Info,
}

internal sealed record DialogButton<T>(string Label, T Result, DialogButtonRole Role = DialogButtonRole.Normal);

internal enum DialogButtonRole
{
    Normal,
    Default,
    Cancel,
    Destructive,
}

internal static class MessageDialog
{
    public static Task<T?> ShowAsync<T>(Window owner, string title, string heading, string body, DialogIcon icon, params DialogButton<T>[] buttons)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            CanMinimize = false,
            CanMaximize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var buttonBar = new DockPanel { LastChildFill = false };
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        DockPanel.SetDock(trailing, Avalonia.Controls.Dock.Right);

        foreach (var spec in buttons)
        {
            var button = new Button
            {
                Content = spec.Label,
                MinWidth = 96,
                Padding = new Thickness(14, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsDefault = spec.Role == DialogButtonRole.Default,
                IsCancel = spec.Role == DialogButtonRole.Cancel,
            };
            if (spec.Role == DialogButtonRole.Default)
            {
                button.Classes.Add("accent");
            }

            button.Click += (_, _) => dialog.Close(spec.Result);

            if (spec.Role == DialogButtonRole.Destructive)
            {
                DockPanel.SetDock(button, Avalonia.Controls.Dock.Left);
                buttonBar.Children.Add(button);
            }
            else
            {
                trailing.Children.Add(button);
            }
        }

        buttonBar.Children.Add(trailing);

        var text = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseMediumHighBrush"),
                },
            },
        };

        var header = new DockPanel();
        if (icon != DialogIcon.None && owner.TryFindResource(icon == DialogIcon.Warning ? "IconWarning" : "IconInfo", out var geometry) && geometry is Geometry data)
        {
            var glyph = new PathIcon
            {
                Data = data,
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 2, 16, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (owner.TryFindResource(icon == DialogIcon.Warning ? "StatusUncertainBrush" : "SystemAccentColor", out var brush))
            {
                glyph.Foreground = brush as IBrush ?? (brush is Color c ? new SolidColorBrush(c) : null);
            }

            DockPanel.SetDock(glyph, Avalonia.Controls.Dock.Left);
            header.Children.Add(glyph);
        }

        header.Children.Add(text);

        dialog.Content = new DockPanel
        {
            Children =
            {
                Docked(new Border
                {
                    Padding = new Thickness(20, 12),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
                    [!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush"),
                    Child = buttonBar,
                }, Avalonia.Controls.Dock.Bottom),
                new Border { Padding = new Thickness(20, 20, 20, 16), Child = header },
            },
        };

        return dialog.ShowDialog<T?>(owner);
    }

    private static Control Docked(Control control, Avalonia.Controls.Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
