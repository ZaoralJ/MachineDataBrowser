using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace OpcUaBrowser.App.ViewModels;

/// <summary>
/// ObservableCollection with <see cref="AddRange"/> and <see cref="RemoveRange"/> that raise one Reset instead of one
/// event per item: adding thousands of rows one by one made the grid re-layout (and every listener run) per row.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    public void AddRange(IEnumerable<T> items) => Batch(() =>
    {
        foreach (var item in items)
        {
            Items.Add(item);
        }
    });

    public void RemoveRange(IEnumerable<T> items) => Batch(() =>
    {
        foreach (var item in items.ToList())
        {
            Items.Remove(item);
        }
    });

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppress)
        {
            base.OnPropertyChanged(e);
        }
    }

    private void Batch(Action change)
    {
        CheckReentrancy();
        _suppress = true;
        try
        {
            change();
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
