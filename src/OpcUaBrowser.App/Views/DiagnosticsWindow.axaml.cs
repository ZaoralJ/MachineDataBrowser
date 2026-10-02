using Avalonia.Controls;
using OpcUaBrowser.App.ViewModels;

namespace OpcUaBrowser.App.Views;

public sealed partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as DiagnosticsViewModel)?.Dispose();
    }
}
