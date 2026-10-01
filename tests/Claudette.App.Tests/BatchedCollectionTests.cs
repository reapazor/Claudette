using System.Collections.Specialized;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>A project run's log: lines in batches, kept to a limit, one change per batch.</summary>
public sealed class BatchedCollectionTests
{
    [Fact]
    public void A_batch_is_one_add_and_trimming_the_start_is_one_reset()
    {
        var lines = new BatchedCollection<string>();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        lines.CollectionChanged += (_, e) => changes.Add(e);

        Assert.Equal(0, lines.AddAndTrim(["1", "2", "3"], max: 5));
        var add = Assert.Single(changes);
        Assert.Equal((NotifyCollectionChangedAction.Add, 0, 3), (add.Action, add.NewStartingIndex, add.NewItems!.Count));

        Assert.Equal(3, lines.AddAndTrim(["4", "5", "6"], max: 3));
        Assert.Equal(NotifyCollectionChangedAction.Reset, changes[1].Action);
        Assert.Equal(["4", "5", "6"], lines);
        Assert.Equal(2, changes.Count);

        Assert.Equal(0, lines.AddAndTrim([], max: 3));
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void Deferred_changes_are_heard_of_once_as_a_whole_when_the_last_deferral_ends()
    {
        var list = new BatchedCollection<int> { 1 };
        var changes = new List<NotifyCollectionChangedAction>();
        var counts = 0;
        list.CollectionChanged += (_, e) => changes.Add(e.Action);
        ((System.ComponentModel.INotifyPropertyChanged)list).PropertyChanged += (_, e) => counts += e.PropertyName == nameof(list.Count) ? 1 : 0;

        using (list.DeferNotifications())
        {
            using (list.DeferNotifications())
            {
                list.Add(2);
                list.Add(3);
            }
            list.RemoveAt(0);
            Assert.Empty(changes);
        }

        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal(1, counts);
        Assert.Equal([2, 3], list);

        // Nothing changed: nothing to hear.
        using (list.DeferNotifications())
        {
        }
        Assert.Single(changes);
        list.Add(4);
        Assert.Equal(NotifyCollectionChangedAction.Add, changes[^1]);
    }
}
