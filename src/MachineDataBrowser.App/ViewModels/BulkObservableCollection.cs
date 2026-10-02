using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MachineDataBrowser.App.ViewModels;

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

    public void ReplaceAll(IEnumerable<T> items) => Batch(() =>
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
    });

    /// <summary>Inserts <paramref name="items"/> at <paramref name="index"/> with one ranged Add event.</summary>
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        if (Items is List<T> list)
        {
            list.InsertRange(index, items);
        }
        else
        {
            for (var i = 0; i < items.Count; i++)
            {
                Items.Insert(index + i, items[i]);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items.ToList(), index));
    }

    /// <summary>Removes <paramref name="count"/> items from <paramref name="index"/> with one ranged Remove event.</summary>
    public void RemoveRangeAt(int index, int count)
    {
        if (count == 0)
        {
            return;
        }

        CheckReentrancy();
        var removed = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            removed.Add(Items[index + i]);
        }

        if (Items is List<T> list)
        {
            list.RemoveRange(index, count);
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                Items.RemoveAt(index);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

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
