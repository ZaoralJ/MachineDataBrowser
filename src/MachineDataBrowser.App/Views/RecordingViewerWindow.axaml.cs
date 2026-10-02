using Avalonia.Controls;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class RecordingViewerWindow : Window
{
    private RecordingViewerViewModel? _vm;

    public RecordingViewerWindow()
    {
        InitializeComponent();

        // One check box per column; the last visible column cannot be hidden.
        foreach (var column in RowsGrid.Columns)
        {
            var toggle = new CheckBox { Content = column.Header, IsChecked = column.IsVisible };
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (toggle.IsChecked != true && RowsGrid.Columns.Count(c => c.IsVisible) == 1)
                {
                    toggle.IsChecked = true;
                    return;
                }

                column.IsVisible = toggle.IsChecked == true;
            };
            ColumnToggles.Children.Add(toggle);
        }
        Closed += (_, _) => _vm?.Dispose();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.RowsAppended -= OnRowsAppended;
        }

        _vm = DataContext as RecordingViewerViewModel;
        if (_vm is not null)
        {
            var vm = _vm;
            KeyBindings.Clear();
            Shortcuts.Apply("Recording viewer", this,
            [
                new("F", new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.Follow = !vm.Follow), Description: "Follow latest on/off"),
                new("G", new CommunityToolkit.Mvvm.Input.RelayCommand(() => vm.ShowChart = !vm.ShowChart), Description: "Show / hide chart"),
                new("C", new CommunityToolkit.Mvvm.Input.RelayCommand(() => ColumnsButton.Flyout?.ShowAt(ColumnsButton)), Description: "Show / hide columns"),
            ]);
            _vm.RowsAppended += OnRowsAppended;
            _vm.PropertyChanged += (_, p) =>
            {
                if (p.PropertyName == nameof(RecordingViewerViewModel.Follow) && _vm.Follow)
                {
                    ScrollToEnd();
                }
                else if (p.PropertyName == nameof(RecordingViewerViewModel.SelectedRow) && _vm.SelectedRow is { } row)
                {
                    RowsGrid.ScrollIntoView(row, null);
                }
            };
        }
    }

    private void OnRowsAppended(object? sender, EventArgs e)
    {
        if (_vm?.Follow == true)
        {
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
        }
    }

    private void ScrollToEnd()
    {
        if (_vm is { Rows.Count: > 0 } vm)
        {
            RowsGrid.ScrollIntoView(vm.Rows[^1], null);
        }
    }
}
