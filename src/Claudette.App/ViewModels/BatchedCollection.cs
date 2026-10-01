using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can add many items and drop many from the start with one change
/// notification each, for logs that arrive in batches and are kept to a limit. One at a time, a batch of 500 lines meant
/// 500 notifications, and trimming the start meant shifting the whole list once per line.
/// </summary>
public sealed class BatchedCollection<T> : ObservableCollection<T>
{
    /// <summary>Adds <paramref name="items"/> to the end, then drops items from the start until at most <paramref name="max"/> are left.</summary>
    /// <returns>How many were dropped.</returns>
    public int AddAndTrim(IReadOnlyList<T> items, int max)
    {
        if (items.Count == 0)
        {
            return 0;
        }
        CheckReentrancy();
        var list = (List<T>)Items;
        list.AddRange(items);
        var dropped = Math.Max(0, list.Count - max);
        if (dropped > 0)
        {
            list.RemoveRange(0, dropped);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        if (dropped == 0)
        {
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items.ToList(), list.Count - items.Count));
        }
        else
        {
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        return dropped;
    }
}
