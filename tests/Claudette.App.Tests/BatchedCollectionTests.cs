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
    public void At_the_limit_the_oldest_go_a_chunk_at_a_time_as_one_remove_before_the_add()
    {
        var lines = new BatchedCollection<int>();
        lines.AddAndTrim([.. Enumerable.Range(1, 10)], max: 10, chunk: 4);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        var counts = new List<int>();
        lines.CollectionChanged += (_, e) =>
        {
            changes.Add(e);
            counts.Add(lines.Count);
        };

        Assert.Equal(4, lines.AddAndTrim([11], max: 10, chunk: 4));

        Assert.Equal([5, 6, 7, 8, 9, 10, 11], lines);
        Assert.Equal((NotifyCollectionChangedAction.Remove, 0, 4), (changes[0].Action, changes[0].OldStartingIndex, changes[0].OldItems!.Count));
        Assert.Equal((NotifyCollectionChangedAction.Add, 6, 1), (changes[1].Action, changes[1].NewStartingIndex, changes[1].NewItems!.Count));
        // Each change is heard with the list as it left it.
        Assert.Equal([6, 7], counts);

        // The next three lines fit without dropping any.
        Assert.Equal(0, lines.AddAndTrim([12, 13, 14], max: 10, chunk: 4));
        Assert.Equal(3, changes.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(50)]
    public void How_many_go_doesnt_depend_on_how_the_lines_were_batched(int batch)
    {
        var lines = new BatchedCollection<int>();
        var dropped = 0;
        foreach (var chunk in Enumerable.Range(1, 47).Chunk(batch))
        {
            dropped += lines.AddAndTrim(chunk, max: 20, chunk: 5);
        }

        Assert.Equal(30, dropped);
        Assert.Equal(Enumerable.Range(31, 17), lines);
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
