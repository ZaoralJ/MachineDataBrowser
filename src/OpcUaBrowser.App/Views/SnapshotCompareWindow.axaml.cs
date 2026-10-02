using Avalonia.Controls;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class SnapshotCompareWindow : Window
{
    public SnapshotCompareWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as SnapshotCompareViewModel)?.Dispose();
    }
}
