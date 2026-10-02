using Avalonia.Controls;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class AttributesView : UserControl
{
    private bool _shortcutsApplied;

    public AttributesView()
    {
        InitializeComponent();
        AttributesGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>"Write value…" only appears for the Value row of a variable.</summary>
    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        WriteValueItem.IsVisible = DataContext is MainWindowViewModel vm && vm.WriteAttributeValueCommand.CanExecute(null);
    }

    /// <summary>Right-click selects the row under the pointer so the context menu acts on it.</summary>
    private void OnGridPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(AttributesGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        for (var v = e.Source as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
        {
            if (v is DataGridRow { DataContext: OpcUaBrowser.Core.AttributeValue attribute })
            {
                AttributesGrid.SelectedItem = attribute;
                return;
            }
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm && !_shortcutsApplied)
        {
            _shortcutsApplied = true;
            Shortcuts.Apply("Attributes", AttributesGrid, [new("Cmd+Alt+C", vm.CopyAttributeValueCommand, Description: "Copy value")]);
        }
    }
}
