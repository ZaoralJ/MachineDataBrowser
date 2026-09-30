using Avalonia.Controls;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class AttributesView : UserControl
{
    private bool _shortcutsApplied;

    public AttributesView() => InitializeComponent();

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
