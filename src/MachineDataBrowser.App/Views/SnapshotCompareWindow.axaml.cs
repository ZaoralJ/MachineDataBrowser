using Avalonia.Controls;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class SnapshotCompareWindow : Window
{
    private bool _reselecting;

    public SnapshotCompareWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SnapshotCompareViewModel vm)
            {
                vm.RowsRefreshed += (_, _) => Reselect(vm);
            }
        };
        Closed += (_, _) => (DataContext as SnapshotCompareViewModel)?.Dispose();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_reselecting || sender is not DataGrid grid || DataContext is not SnapshotCompareViewModel { IsRefreshing: false } vm)
        {
            return;
        }

        vm.SelectedIds.Clear();
        vm.SelectedIds.UnionWith(grid.SelectedItems.OfType<SnapshotDiffRow>().Select(r => r.NodeId));
    }

    // The live comparison replaces every row each second; keep what the user selected.
    private void Reselect(SnapshotCompareViewModel vm)
    {
        if (vm.SelectedIds.Count == 0)
        {
            return;
        }

        _reselecting = true;
        try
        {
            Differences.SelectedItems.Clear();
            foreach (var row in vm.Rows.Where(r => vm.SelectedIds.Contains(r.NodeId)))
            {
                Differences.SelectedItems.Add(row);
            }
        }
        finally
        {
            _reselecting = false;
        }
    }
}
