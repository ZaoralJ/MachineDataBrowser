using Avalonia.Controls;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class SnapshotCompareWindow : Window
{
    public SnapshotCompareWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as SnapshotCompareViewModel)?.Dispose();
    }
}
