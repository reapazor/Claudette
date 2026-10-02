using Claudette.Core.ScratchPads;

namespace Claudette.Core.Tests.ScratchPads;

/// <summary>
/// How a scratch pad on this machine and the library's copy come together (DESIGN.md §18, "Scratch pad"): each case of
/// <see cref="ScratchPadSync.Decide"/> and of resolving both machines' changes.
/// </summary>
public sealed class ScratchPadSyncTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

    /// <summary>The library's copy at <paramref name="revision"/>, built on <paramref name="lineage"/>.</summary>
    private static ScratchPadFile Library(string text, string revision, params string[] lineage) =>
        new() { Text = text, Revision = revision, Lineage = lineage, Machine = "DESKTOP-01", ChangedAt = Now };

    /// <summary>This machine's copy: <paramref name="text"/> saved as <paramref name="revision"/>, last synced at <paramref name="synced"/>.</summary>
    private static ScratchPadFile Local(string text, string revision, string? synced) => new() { Text = text, Revision = revision, Synced = synced, Machine = "LAPTOP" };

    [Fact]
    public void A_pad_never_written_with_nothing_in_the_library_has_nothing_to_do()
    {
        Assert.Equal(ScratchPadSyncKind.InSync, ScratchPadSync.Decide(new ScratchPadFile(), null).Kind);
    }

    [Fact]
    public void A_pad_the_library_doesnt_have_yet_goes_there()
    {
        var result = ScratchPadSync.Decide(Local("notes", "l1", synced: null), null);

        Assert.Equal(ScratchPadSyncKind.Pushed, result.Kind);
        Assert.Equal(("notes", "l1"), (result.Library!.Text, result.Library.Revision));
        Assert.Null(result.Library.Synced);
        Assert.Equal("l1", result.Local.Synced);
        Assert.False(result.Local.HasUnsyncedChanges);
    }

    [Fact]
    public void Nothing_changed_on_either_side_is_in_sync()
    {
        Assert.Equal(ScratchPadSyncKind.InSync, ScratchPadSync.Decide(Local("notes", "r1", synced: "r1"), Library("notes", "r1")).Kind);
    }

    [Fact]
    public void Changes_here_go_to_the_library_carrying_on_from_its_copy()
    {
        var result = ScratchPadSync.Decide(Local("notes and more", "l2", synced: "r1"), Library("notes", "r1", "r0"));

        Assert.Equal(ScratchPadSyncKind.Pushed, result.Kind);
        Assert.Equal("notes and more", result.Library!.Text);
        Assert.Equal(["r1", "r0"], result.Library.Lineage);
        Assert.True(result.Library.Includes("r1"));
        Assert.Equal("l2", result.Local.Synced);
    }

    [Fact]
    public void Another_machines_changes_come_here_when_nothing_changed_here()
    {
        var result = ScratchPadSync.Decide(Local("notes", "r1", synced: "r1"), Library("notes, edited elsewhere", "r3", "r2", "r1"));

        Assert.Equal(ScratchPadSyncKind.Pulled, result.Kind);
        Assert.Equal("notes, edited elsewhere", result.Local.Text);
        Assert.Equal(("r3", "r3"), (result.Local.Revision, result.Local.Synced));
        Assert.Empty(result.Local.Lineage);
    }

    [Fact]
    public void A_pad_never_written_here_takes_the_librarys()
    {
        Assert.Equal(ScratchPadSyncKind.Pulled, ScratchPadSync.Decide(new ScratchPadFile(), Library("from the other machine", "r1")).Kind);
    }

    [Fact]
    public void Both_changed_asks()
    {
        var result = ScratchPadSync.Decide(Local("mine", "l2", synced: "r1"), Library("theirs", "r2", "r1"));

        Assert.Equal(ScratchPadSyncKind.Conflict, result.Kind);
        Assert.Equal("theirs", result.Conflict!.Theirs.Text);
        Assert.Null(result.Conflict.CopyPath);
        Assert.Equal("mine", result.Local.Text);
    }

    [Fact]
    public void A_library_copy_that_replaced_this_machines_without_seeing_it_asks_rather_than_lose_it()
    {
        // A sync client kept the other machine's file over ours: it doesn't carry on from what we wrote.
        var result = ScratchPadSync.Decide(Local("mine", "l1", synced: "l1"), Library("theirs", "r2", "r0"));

        Assert.Equal(ScratchPadSyncKind.Conflict, result.Kind);
    }

    [Fact]
    public void A_pad_written_here_before_it_ever_synced_asks_when_the_library_has_another()
    {
        Assert.Equal(ScratchPadSyncKind.Conflict, ScratchPadSync.Decide(Local("mine", "l1", synced: null), Library("theirs", "r1")).Kind);
    }

    [Fact]
    public void The_same_text_on_both_takes_the_librarys_revision_without_asking()
    {
        var result = ScratchPadSync.Decide(Local("same", "l2", synced: "r1"), Library("same", "r2", "r1"));

        Assert.Equal(ScratchPadSyncKind.Adopted, result.Kind);
        Assert.Equal(("r2", "r2"), (result.Local.Revision, result.Local.Synced));
    }

    [Fact]
    public void A_library_copy_in_a_newer_format_is_left_alone()
    {
        var newer = Library("theirs", "r2") with { Version = ScratchPadFile.CurrentVersion + 1 };

        Assert.Equal(ScratchPadSyncKind.NewerFormat, ScratchPadSync.Decide(Local("mine", "l2", synced: "r1"), newer).Kind);
    }

    [Fact]
    public void A_choice_carries_on_from_both_copies_so_the_other_machine_takes_it_without_asking()
    {
        var mine = Local("mine", "l2", synced: "r1");
        var theirs = Library("theirs", "r2", "r1");

        var result = ScratchPadSync.Resolve(mine, theirs, new ScratchPadConflict(theirs, null), "mine\ntheirs\n", "r3", "LAPTOP", Now);

        Assert.Equal(ScratchPadSyncKind.Pushed, result.Kind);
        var written = result.Library!;
        Assert.Equal(("mine\ntheirs\n", "r3", "LAPTOP", Now), (written.Text, written.Revision, written.Machine, written.ChangedAt));
        Assert.True(written.Includes("r2") && written.Includes("l2") && written.Includes("r1"));
        Assert.Equal(("r3", "r3"), (result.Local.Revision, result.Local.Synced));
        // The machine that wrote "theirs" has nothing new since, so it takes the choice.
        var other = ScratchPadSync.Decide(Local("theirs", "r2", synced: "r2"), written);
        Assert.Equal(ScratchPadSyncKind.Pulled, other.Kind);
    }

    [Fact]
    public void Choosing_the_librarys_text_takes_it_without_writing()
    {
        var theirs = Library("theirs", "r2", "r1");

        var result = ScratchPadSync.Resolve(Local("mine", "l2", synced: "r1"), theirs, new ScratchPadConflict(theirs, null), "theirs", "r3", "LAPTOP", Now);

        Assert.Equal(ScratchPadSyncKind.Pulled, result.Kind);
        Assert.Null(result.Library);
        Assert.Equal(("theirs", "r2"), (result.Local.Text, result.Local.Synced));
    }

    [Fact]
    public void A_library_copy_that_moved_on_while_the_user_chose_asks_again_about_that()
    {
        var theirs = Library("theirs", "r2", "r1");
        var later = Library("theirs, and later", "r4", "r2", "r1");

        var result = ScratchPadSync.Resolve(Local("mine", "l2", synced: "r1"), later, new ScratchPadConflict(theirs, null), "mine", "r3", "LAPTOP", Now);

        Assert.Equal(ScratchPadSyncKind.Conflict, result.Kind);
        Assert.Same(later, result.Conflict!.Theirs);
        Assert.Null(result.Library);
    }

    [Fact]
    public void The_file_reads_back_as_written_and_tolerates_what_it_doesnt_know()
    {
        var file = Library("line one\nline \"two\"", "r2", "r1", "r0") with { Synced = "r2", Remote = "github.com/owner/repo", PathInRepo = "web" };

        var read = ScratchPadFile.Parse(file.ToJson())!;

        Assert.Equal((file.Text, file.Revision, file.Synced, file.Machine, file.ChangedAt, file.Remote, file.PathInRepo),
            (read.Text, read.Revision, read.Synced, read.Machine, read.ChangedAt, read.Remote, read.PathInRepo));
        Assert.Equal(file.Lineage, read.Lineage);
        Assert.Equal("only text", ScratchPadFile.Parse("""{"version":1,"text":"only text","later":{"field":true}}""")!.Text);
        Assert.True(ScratchPadFile.Parse("""{"version":2,"text":"x"}""")!.IsNewerFormat);
        Assert.Null(ScratchPadFile.Parse("""{"text":"no version"}"""));
        Assert.Null(ScratchPadFile.Parse("not json"));
    }
}
