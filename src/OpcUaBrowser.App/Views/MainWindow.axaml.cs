using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        EndpointHistoryList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button button && button.Classes.Contains("history-entry"))
            {
                EndpointHistoryButton.Flyout?.Hide();
            }
        });
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CopyToClipboard = async text =>
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            };
            vm.Dialogs = this;
            AttachMenu(vm);
            // Extra zoom shortcuts not expressible as a single menu gesture (Cmd+= without Shift, numpad).
            var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            foreach (var (key, command) in new (Key, System.Windows.Input.ICommand)[]
            {
                (Key.Add, vm.ZoomInCommand), (Key.Subtract, vm.ZoomOutCommand), (Key.NumPad0, vm.ZoomResetCommand),
            })
            {
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, cmd), Command = command });
            }

            if (OperatingSystem.IsMacOS())
            {
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemPlus, cmd | KeyModifiers.Shift), Command = vm.ZoomInCommand });
            }
        }
    }

    private void OnEndpointKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && EndpointHistoryButton.IsVisible)
        {
            EndpointHistoryButton.Flyout?.ShowAt(EndpointBox);
            e.Handled = true;
        }
    }
}
