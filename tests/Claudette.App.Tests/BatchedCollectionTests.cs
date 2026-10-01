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
}
