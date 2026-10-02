using Avalonia.Controls;
using MachineDataBrowser.App.ViewModels;

namespace MachineDataBrowser.App.Views;

public sealed partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as DiagnosticsViewModel)?.Dispose();
    }
}
