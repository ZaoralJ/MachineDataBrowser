using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class WatchColumnOption(string header) : ObservableObject
{
    public string Header { get; } = header;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    public double? Width { get; set; }

    public int Order { get; set; }
}

public sealed class WatchColumnsViewModel
{
    public static IReadOnlyList<string> AllHeaders { get; } =
        ["Name", "NodeId", "Status", "Value", "Refresh", "Last update", "Since", "Source time", "Recorded"];

    public static IReadOnlySet<string> DefaultHidden { get; } = new HashSet<string> { "Source time", "NodeId" };

    public WatchColumnsViewModel()
    {
        for (var i = 0; i < AllHeaders.Count; i++)
        {
            var option = new WatchColumnOption(AllHeaders[i]) { Order = i, IsVisible = !DefaultHidden.Contains(AllHeaders[i]) };
            option.PropertyChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            Columns.Add(option);
        }
    }

    public event EventHandler? Changed;

    /// <summary>Raised when state was replaced (session load / reset) and the view must re-apply it.</summary>
    public event EventHandler? Reapplied;

    public ObservableCollection<WatchColumnOption> Columns { get; } = [];

    public string? SortColumn { get; private set; }

    public bool SortDescending { get; private set; }

    /// <summary>Records a header click; returns the direction the grid will now sort in.</summary>
    public bool ToggleSort(string header)
    {
        SortDescending = SortColumn == header && !SortDescending;
        SortColumn = header;
        Changed?.Invoke(this, EventArgs.Empty);
        return SortDescending;
    }

    public void SetSort(string? column, bool descending)
    {
        SortColumn = column;
        SortDescending = descending;
    }

    public WatchColumnOption? this[string header] => Columns.FirstOrDefault(c => c.Header == header);

    public IReadOnlyList<ColumnState> Capture() =>
        [.. Columns.Select(c => new ColumnState(c.Header, c.IsVisible, c.Width, c.Order))];

    public void Apply(IReadOnlyList<ColumnState>? states)
    {
        foreach (var (column, index) in Columns.Select((c, i) => (c, i)))
        {
            var saved = states?.FirstOrDefault(s => s.Header == column.Header);
            column.IsVisible = saved?.Visible ?? !DefaultHidden.Contains(column.Header);
            column.Width = saved?.Width;
            column.Order = saved?.Order ?? index;
        }

        if (Columns.All(c => !c.IsVisible))
        {
            Columns[0].IsVisible = true;
        }

        Reapplied?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        SetSort(null, false);
        Apply(null);
    }
}
