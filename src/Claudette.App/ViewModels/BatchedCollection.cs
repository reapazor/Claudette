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

    /// <summary>
    /// Adds <paramref name="items"/> to the end, first dropping items from the start so that at most
    /// <paramref name="max"/> are left. They're dropped <paramref name="chunk"/> at a time, so a log at its limit drops
    /// its oldest lines once every <paramref name="chunk"/> lines rather than with every line. The drop is one Remove,
    /// and the batch one Add, so a list showing it keeps the items it has built; only a batch that replaces everything is
    /// a Reset.
    /// </summary>
    /// <returns>How many were dropped, always a multiple of <paramref name="chunk"/>: the same however the items were batched.</returns>
    public int AddAndTrim(IReadOnlyList<T> items, int max, int chunk = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunk, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, chunk);
        if (items.Count == 0)
        {
            return 0;
        }
        CheckReentrancy();
        var list = (List<T>)Items;
        var over = list.Count + items.Count - max;
        var dropped = over <= 0 ? 0 : (over + chunk - 1) / chunk * chunk;
        if (dropped >= list.Count && dropped > 0)
        {
            // Everything that was there goes, and some of the batch with it.
            var had = list.Count;
            list.Clear();
            list.AddRange(items.Skip(dropped - had));
            Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return dropped;
        }
        if (dropped > 0)
        {
            var removed = list.GetRange(0, dropped);
            list.RemoveRange(0, dropped);
            Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, 0));
        }
        var index = list.Count;
        list.AddRange(items);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items.ToList(), index));
        return dropped;
    }

    private void Changed(NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(e);
    }
}
