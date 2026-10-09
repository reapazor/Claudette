using Claudette.Core.Threads;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// Threads (DESIGN.md §18): which tabs are threads, which thread each sub-thread belongs to, the menus' and palette's
/// commands, keeping sub-threads under their thread in the sidebar, and the links through restoring and closing. What
/// a thread and a sub-thread do is in <c>TabViewModel.Thread.cs</c>.
/// </summary>
public sealed partial class ShellViewModel
{
    private TabGroupViewModel? GroupOf(TabViewModel tab) => Groups.FirstOrDefault(g => g.Tabs.Contains(tab));

    /// <summary>The threads <paramref name="tab"/> could join: the others in its group. None for a thread, which can't nest.</summary>
    internal IReadOnlyList<TabViewModel> ThreadsFor(TabViewModel tab) =>
        tab.IsThread || GroupOf(tab) is not { } group ? [] : [.. group.Tabs.Where(t => t.IsThread && t != tab && t != tab.ThreadHead)];

    [RelayCommand]
    private void MakeThread(TabViewModel? tab)
    {
        if (tab is not { CanMakeThread: true })
        {
            return;
        }
        tab.SetIsThread(true);
        ThreadsChanged(tab);
        Announce($"{tab.DisplayName} is a thread.");
    }

    /// <summary><b>Stop being a thread</b>: its sub-threads carry on as ordinary tabs.</summary>
    [RelayCommand]
    private void StopBeingThread(TabViewModel? tab)
    {
        if (tab is not { IsThread: true })
        {
            return;
        }
        tab.ForgetSubThreadWork();
        foreach (var subThread in tab.SubThreads.ToArray())
        {
            Detach(subThread, SubThreadOutcome.Detached);
        }
        tab.SetIsThread(false);
        ThreadsChanged(tab);
    }

    /// <summary><b>Assign to thread</b>: <paramref name="tab"/> becomes a sub-thread of <paramref name="head"/>, in the same group.</summary>
    internal void AssignToThread(TabViewModel tab, TabViewModel head)
    {
        if (tab == head || tab.IsThread || !head.IsThread || tab.ThreadHead == head || GroupOf(tab) is not { } group || GroupOf(head) != group)
        {
            return;
        }
        if (tab.ThreadHead is not null)
        {
            Detach(tab, SubThreadOutcome.Detached);
        }
        tab.SetThreadHead(head);
        head.AttachSubThread(tab);
        ThreadsChanged(tab);
    }

    [RelayCommand]
    private void DetachFromThread(TabViewModel? tab)
    {
        if (tab?.ThreadHead is null)
        {
            return;
        }
        Detach(tab, SubThreadOutcome.Detached);
        ThreadsChanged(tab);
    }

    private static void Detach(TabViewModel subThread, SubThreadOutcome outcome)
    {
        subThread.ThreadHead?.DetachSubThread(subThread, outcome);
        subThread.SetThreadHead(null);
    }

    [RelayCommand]
    private void GoToThread(TabViewModel? tab)
    {
        if (tab?.ThreadHead is { } head)
        {
            SelectTab(head.Id);
        }
    }

    /// <summary><b>New sub-thread</b>: a new tab in the thread's folder, assigned to it.</summary>
    [RelayCommand]
    private Task NewSubThreadAsync(TabViewModel? head) =>
        head is { IsThread: true } && GroupOf(head) is { } group ? OpenFolderAsync(group.Folder, tab => AssignToThread(tab, head)) : Task.CompletedTask;

    /// <summary><b>New sub-thread in a worktree</b>: a worktree tab of the thread's checkout, assigned to it.</summary>
    [RelayCommand]
    private Task NewSubThreadInWorktreeAsync(TabViewModel? head) =>
        head is { IsThread: true } && GroupOf(head) is { } group ? OpenWorktreeTabAsync(group.Folder, tab => AssignToThread(tab, head)) : Task.CompletedTask;

    /// <summary>A tab's part in a thread changed: the sidebar's order, the menus and the saved tabs follow.</summary>
    private void ThreadsChanged(TabViewModel tab)
    {
        if (GroupOf(tab) is { } group)
        {
            KeepSubThreadsUnderThreads(group);
            foreach (var other in group.Tabs)
            {
                other.ThreadMenuChanged();
            }
        }
        SaveTabs();
    }

    /// <summary>
    /// Each thread's sub-threads sit right under it, in the order they have among themselves. A sub-thread moved away
    /// comes back; a thread moved takes them along.
    /// </summary>
    private static void KeepSubThreadsUnderThreads(TabGroupViewModel group)
    {
        if (!group.Tabs.Any(t => t.IsSubThread))
        {
            return;
        }
        var order = new List<TabViewModel>(group.Tabs.Count);
        foreach (var tab in group.Tabs)
        {
            if (tab.ThreadHead is { } head && group.Tabs.Contains(head))
            {
                continue;
            }
            order.Add(tab);
            if (tab.IsThread)
            {
                order.AddRange(group.Tabs.Where(t => t.ThreadHead == tab));
            }
        }
        Reorder(group, order);
    }

    /// <summary>Moves the group's tabs into <paramref name="order"/>, one move at a time, so the sidebar keeps its rows.</summary>
    private static void Reorder(TabGroupViewModel group, IReadOnlyList<TabViewModel> order)
    {
        for (var i = 0; i < order.Count; i++)
        {
            var from = group.Tabs.IndexOf(order[i]);
            if (from != i)
            {
                group.Tabs.Move(from, i);
            }
        }
    }

    /// <summary>The group's tabs as moving sees them: each thread with its sub-threads is one block, any other tab one of its own.</summary>
    private static List<List<TabViewModel>> Blocks(TabGroupViewModel group)
    {
        var blocks = new List<List<TabViewModel>>();
        foreach (var tab in group.Tabs)
        {
            if (tab.ThreadHead is { } head && blocks.Count > 0 && blocks[^1][0] == head)
            {
                blocks[^1].Add(tab);
            }
            else
            {
                blocks.Add([tab]);
            }
        }
        return blocks;
    }

    /// <summary>After restoring tabs: each sub-thread joins its thread again, if that came back in its group too.</summary>
    private void LinkThreads()
    {
        foreach (var tab in AllTabs.Where(t => t.State.ThreadId is not null).ToArray())
        {
            var head = AllTabs.FirstOrDefault(t => t.Id == tab.State.ThreadId);
            if (head is { IsThread: true } && head != tab && !tab.IsThread && GroupOf(head) == GroupOf(tab))
            {
                tab.SetThreadHead(head);
                head.AttachSubThread(tab);
            }
            else
            {
                tab.SetThreadHead(null);
            }
        }
        foreach (var group in Groups)
        {
            KeepSubThreadsUnderThreads(group);
            foreach (var tab in group.Tabs)
            {
                tab.ThreadMenuChanged();
            }
        }
    }

    /// <summary>
    /// A tab is closing: a thread's sub-threads carry on as ordinary tabs, and a sub-thread's thread hears it closed if
    /// it was waiting on it.
    /// </summary>
    private void LeaveThreadsOnClose(TabViewModel tab)
    {
        tab.ForgetSubThreadWork();
        foreach (var subThread in tab.SubThreads.ToArray())
        {
            Detach(subThread, SubThreadOutcome.Detached);
        }
        if (tab.ThreadHead is not null)
        {
            Detach(tab, SubThreadOutcome.Closed);
        }
        if (GroupOf(tab) is { } group)
        {
            foreach (var other in group.Tabs)
            {
                other.ThreadMenuChanged();
            }
        }
    }
}
