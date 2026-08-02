using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DirectoryMonitor.Presentation.Collections;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public IReadOnlyList<T> AppendRangeAndTrimStart(IReadOnlyList<T> items, int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        if (items.Count == 0 && Items.Count <= maximumCount) return [];

        CheckReentrancy();
        foreach (var item in items) Items.Add(item);

        var removed = new List<T>(Math.Max(0, Items.Count - maximumCount));
        while (Items.Count > maximumCount)
        {
            removed.Add(Items[0]);
            Items.RemoveAt(0);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return removed;
    }
}
