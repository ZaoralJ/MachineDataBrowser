using System.Collections.ObjectModel;
using Avalonia.Controls;

namespace MachineDataBrowser.App.Views;

internal static class SelectionSync
{
    public static void Apply<T>(ObservableCollection<T> target, SelectionChangedEventArgs e)
    {
        foreach (var removed in e.RemovedItems.OfType<T>())
        {
            target.Remove(removed);
        }

        foreach (var added in e.AddedItems.OfType<T>())
        {
            if (!target.Contains(added))
            {
                target.Add(added);
            }
        }
    }
}

internal static class RefreshPrompt
{
    public static async Task<int?> AskAsync(Avalonia.Controls.Control anchor, int current)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(anchor) is not Avalonia.Controls.Window owner)
        {
            return null;
        }

        var input = new Avalonia.Controls.NumericUpDown { Value = current, Minimum = 0, Maximum = 3_600_000, Increment = 50, FormatString = "0" };
        var dialog = new Avalonia.Controls.Window
        {
            Title = "Refresh time",
            Width = 320,
            SizeToContent = Avalonia.Controls.SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
        };
        var ok = new Avalonia.Controls.Button { Content = "Apply", IsDefault = true, MinWidth = 80 };
        ok.Classes.Add("accent");
        var cancel = new Avalonia.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => dialog.Close((int?)(int)(input.Value ?? current));
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new Avalonia.Controls.TextBlock { Text = "Refresh time in milliseconds (0 = all updates: as fast as the server allows, every MQTT message)", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                input,
                new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        return await dialog.ShowDialog<int?>(owner);
    }
}
