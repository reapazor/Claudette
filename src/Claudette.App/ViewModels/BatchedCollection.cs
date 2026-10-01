using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can add many items and drop many from the start with one change
/// notification each, for logs that arrive in batches and are kept to a limit. One at a time, a batch of 500 lines meant
/// 500 notifications, and trimming the start meant shifting the whole list once per line. It can also hold notifications
/// back while many changes are made one by one (<see cref="DeferNotifications"/>).
/// </summary>
public sealed class BatchedCollection<T> : ObservableCollection<T>
{
    private int _deferrals;
    private bool _changedWhileDeferred;

    /// <summary>
    /// Holds change notifications back until the returned scope ends, then says once that the whole list changed: a
    /// restored conversation's thousands of items are laid out once, not once per item.
    /// </summary>
    public IDisposable DeferNotifications()
    {
        _deferrals++;
        return new Deferral(this);
    }

    protected override void OnCollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_deferrals > 0)
        {
            _changedWhileDeferred = true;
            return;
        }
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_deferrals == 0)
        {
            base.OnPropertyChanged(e);
        }
    }

    private void EndDeferral()
    {
        if (--_deferrals > 0 || !_changedWhileDeferred)
        {
            return;
        }
        _changedWhileDeferred = false;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private sealed class Deferral(BatchedCollection<T> collection) : IDisposable
    {
        private BatchedCollection<T>? _collection = collection;

        public void Dispose()
        {
            _collection?.EndDeferral();
            _collection = null;
        }
    }

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
