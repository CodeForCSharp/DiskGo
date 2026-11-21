using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DiskGo;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that supports batched insert, remove,
/// and replace operations with a single <see cref="NotifyCollectionChangedAction.Reset"/>
/// notification, avoiding thousands of incremental layout passes on large folders.
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void InsertRange(int index, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();

        int insertAt = index;
        foreach (T item in items)
            Items.Insert(insertAt++, item);

        if (insertAt != index)
            RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
            return;
        CheckReentrancy();

        for (int i = 0; i < count; i++)
            Items.RemoveAt(index);

        RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();

        Items.Clear();
        foreach (T item in items)
            Items.Add(item);

        RaiseReset();
    }

    public void RefreshAll(Action<IList<T>> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        CheckReentrancy();

        mutate(Items);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
