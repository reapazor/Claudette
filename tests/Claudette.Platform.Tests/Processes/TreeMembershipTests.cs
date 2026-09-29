using Claudette.Platform.Processes;

namespace Claudette.Platform.Tests.Processes;

/// <summary>Walking parent links, and keeping processes that detach (DESIGN.md §4, Process monitor).</summary>
public sealed class TreeMembershipTests
{
    private const int Root = 100;

    private readonly TreeMembership _membership = new(Root);
    private readonly Dictionary<int, (int Parent, long Start)> _system = new()
    {
        [1] = (0, 0),
        [50] = (1, 5),
    };

    [Fact]
    public void Finds_children_and_grandchildren_root_first()
    {
        Spawn(Root, parent: 50, start: 10);
        Spawn(200, parent: Root, start: 11);
        Spawn(201, parent: Root, start: 12);
        Spawn(300, parent: 200, start: 13);
        Spawn(999, parent: 50, start: 14);

        var members = Update();

        Assert.Equal([Root, 200, 201, 300], members.Select(m => m.Pid));
        Assert.True(members[0].IsRoot);
        Assert.All(members.Skip(1), m => Assert.False(m.IsRoot));
        Assert.All(members, m => Assert.False(m.IsDetached));
        Assert.Equal(200, members.Single(m => m.Pid == 300).ParentPid);
    }

    [Fact]
    public void A_re_parented_process_stays_listed_as_detached_until_it_exits()
    {
        Spawn(Root, parent: 50, start: 10);
        Spawn(200, parent: Root, start: 11);
        Spawn(300, parent: 200, start: 12);
        Update();

        // 200 exits; 300 daemonizes and is re-parented to init.
        Exit(200);
        _system[300] = (1, 12);
        var members = Update();

        Assert.Equal([Root, 300], members.Select(m => m.Pid));
        Assert.True(members.Single(m => m.Pid == 300).IsDetached);

        Exit(300);
        Assert.Equal([Root], Update().Select(m => m.Pid));
    }

    [Fact]
    public void Children_of_a_detached_process_are_found_and_detached_too()
    {
        Spawn(Root, parent: 50, start: 10);
        Spawn(200, parent: Root, start: 11);
        Update();
        _system[200] = (1, 11);
        Spawn(400, parent: 200, start: 20);

        var members = Update();

        Assert.Equal([Root, 200, 400], members.Select(m => m.Pid));
        Assert.True(members.Single(m => m.Pid == 400).IsDetached);
    }

    [Fact]
    public void A_process_never_seen_before_it_detached_is_not_found()
    {
        Spawn(Root, parent: 50, start: 10);
        Update();
        Spawn(200, parent: 1, start: 11);

        Assert.Equal([Root], Update().Select(m => m.Pid));
    }

    [Fact]
    public void When_the_root_exits_its_children_are_detached()
    {
        Spawn(Root, parent: 50, start: 10);
        Spawn(200, parent: Root, start: 11);
        Update();

        Exit(Root);
        _system[200] = (1, 11);
        var members = Update();

        var only = Assert.Single(members);
        Assert.Equal(200, only.Pid);
        Assert.True(only.IsDetached);
        Assert.False(only.IsRoot);
    }

    [Fact]
    public void A_reused_pid_is_not_mistaken_for_a_process_seen_before()
    {
        Spawn(Root, parent: 50, start: 10);
        Spawn(200, parent: Root, start: 11);
        Update();

        // 200 exits and an unrelated process gets its PID, with a child of its own.
        Exit(200);
        Spawn(200, parent: 50, start: 30);
        Spawn(201, parent: 200, start: 31);

        Assert.Equal([Root], Update().Select(m => m.Pid));
    }

    [Fact]
    public void A_reused_root_pid_is_not_the_root()
    {
        Spawn(Root, parent: 50, start: 10);
        Update();

        Exit(Root);
        Spawn(Root, parent: 50, start: 40);
        Spawn(200, parent: Root, start: 41);

        Assert.Empty(Update());
    }

    [Fact]
    public void A_child_that_started_before_its_parent_belongs_to_an_older_process_with_that_pid()
    {
        // Windows keeps a child's parent PID after the parent exits; 150 is a leftover of an earlier process 100.
        Spawn(150, parent: Root, start: 5);
        Spawn(Root, parent: 50, start: 10);

        Assert.Equal([Root], Update().Select(m => m.Pid));
    }

    [Fact]
    public void Orphans_that_keep_a_gone_parents_pid_are_found_as_detached()
    {
        // Windows: a child keeps its parent's PID after the parent exits, and isn't re-parented.
        Spawn(Root, parent: 50, start: 10);
        Spawn(200, parent: Root, start: 11);
        Update();

        Spawn(300, parent: 200, start: 12);
        Exit(200);
        var members = Update();

        Assert.Equal([Root, 300], members.Select(m => m.Pid));
        Assert.True(members.Single(m => m.Pid == 300).IsDetached);
    }

    [Fact]
    public void Orphans_of_a_gone_parent_are_told_apart_from_children_of_a_process_that_reused_its_pid()
    {
        Spawn(Root, parent: 50, start: 10);
        Spawn(200, parent: Root, start: 11);
        Update();

        Spawn(300, parent: 200, start: 12);
        Exit(200);
        Spawn(200, parent: 50, start: 20);
        Spawn(301, parent: 200, start: 21);
        var members = Update();

        Assert.Equal([Root, 300], members.Select(m => m.Pid));
    }

    [Fact]
    public void Nothing_is_found_if_the_root_was_never_seen()
    {
        Assert.Empty(Update());
        Assert.Null(_membership.RootStartKey);
    }

    [Fact]
    public void A_root_start_key_given_up_front_is_used()
    {
        var membership = new TreeMembership(Root, rootStartKey: 10);
        Spawn(Root, parent: 50, start: 99);

        Assert.Empty(membership.Update(_system.ToDictionary(p => p.Key, p => p.Value.Parent), pid => _system[pid].Start));
    }

    [Fact]
    public void Unreadable_processes_are_skipped()
    {
        Spawn(Root, parent: 50, start: 10);
        Spawn(200, parent: Root, start: 11);
        Spawn(300, parent: 200, start: 12);

        var members = _membership.Update(
            _system.ToDictionary(p => p.Key, p => p.Value.Parent),
            pid => pid == 200 ? null : _system[pid].Start);

        Assert.Equal([Root], members.Select(m => m.Pid));
    }

    [Fact]
    public void Job_members_are_attached_when_they_descend_from_the_root_through_live_parents()
    {
        var attached = TreeMembership.AttachedToRoot(
            [
                (Root, 50, 10),
                (200, Root, 11),
                (300, 200, 12),
                (400, 250, 13), // Its parent 250 has exited.
                (500, 400, 14),
                (600, Root, 5), // Started before the root: its parent was an older process 100.
            ],
            Root);

        Assert.Equal([Root, 200, 300], attached.Order());
    }

    [Fact]
    public void Without_the_root_every_job_member_is_detached()
    {
        Assert.Empty(TreeMembership.AttachedToRoot([(200, Root, 11)], Root));
    }

    private void Spawn(int pid, int parent, long start) => _system[pid] = (parent, start);

    private void Exit(int pid) => _system.Remove(pid);

    private IReadOnlyList<TreeMember> Update() =>
        _membership.Update(_system.ToDictionary(p => p.Key, p => p.Value.Parent), pid => _system[pid].Start);
}
