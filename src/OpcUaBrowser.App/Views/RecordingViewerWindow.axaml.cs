using Avalonia.Controls;
using Avalonia.Threading;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class RecordingViewerWindow : Window
{
    private RecordingViewerViewModel? _vm;

    public RecordingViewerWindow()
    {
        InitializeComponent();
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
