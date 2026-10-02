using Claudette.Core.ScratchPads;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ScratchPads;

/// <summary>
/// Scratch pads kept on two machines that share a session library folder (DESIGN.md §18, "Scratch pad"), as a sync
/// client would leave it: changes passing between them, both changing it at once, and the copies sync clients make.
/// </summary>
public sealed class ScratchPadStoreTests : IDisposable
{
    private const string Id = "0123456789abcdef";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");

    private readonly TempFolder _root = new("claudette-pads");
    private readonly ScratchPadStore _desktop;
    private readonly ScratchPadStore _laptop;

    public ScratchPadStoreTests()
    {
        _desktop = new ScratchPadStore(_root.Combine("desktop"), () => _root.Combine("library"));
        _laptop = new ScratchPadStore(_root.Combine("laptop"), () => _root.Combine("library"));
    }

    public void Dispose() => _root.Dispose();

    /// <summary>What a pad does when its text is typed: a new revision, saved here, then synced.</summary>
    private static ScratchPadSyncResult Type(ScratchPadStore machine, string text)
    {
        var local = (machine.ReadLocal(Id, Now) ?? new ScratchPadFile()) with { Text = text, Revision = ScratchPadSync.NewRevision() };
        machine.WriteLocal(Id, local);
        return Sync(machine);
    }

    /// <summary>A sync, saving this machine's copy afterwards as the pad does.</summary>
    private static ScratchPadSyncResult Sync(ScratchPadStore machine)
    {
        var result = machine.Sync(Id, machine.ReadLocal(Id, Now) ?? new ScratchPadFile());
        machine.WriteLocal(Id, result.Local);
        return result;
    }

    [Fact]
    public void What_one_machine_types_reaches_the_other()
    {
        Assert.Equal(ScratchPadSyncKind.Pushed, Type(_desktop, "Remember the retry limit").Kind);

        var result = Sync(_laptop);

        Assert.Equal(ScratchPadSyncKind.Pulled, result.Kind);
        Assert.Equal("Remember the retry limit", result.Local.Text);
        Assert.Equal(ScratchPadSyncKind.InSync, Sync(_laptop).Kind);
        Assert.Equal(ScratchPadSyncKind.Pushed, Type(_laptop, "Remember the retry limit\nAnd the timeout").Kind);
        Assert.Equal("Remember the retry limit\nAnd the timeout", Sync(_desktop).Local.Text);
    }

    [Fact]
    public void Both_machines_typing_before_seeing_each_other_asks_and_keeping_both_reaches_both()
    {
        Type(_desktop, "shared\n");
        Sync(_laptop);
        Type(_laptop, "shared\nfrom the laptop\n");
        // The desktop typed too before the laptop's change arrived.
        var desktop = _desktop.ReadLocal(Id, Now)! with { Text = "shared\nfrom the desktop\n", Revision = ScratchPadSync.NewRevision() };
        _desktop.WriteLocal(Id, desktop);

        var result = Sync(_desktop);

        Assert.Equal(ScratchPadSyncKind.Conflict, result.Kind);
        Assert.Equal("shared\nfrom the laptop\n", result.Conflict!.Theirs.Text);
        var text = ScratchPadText.KeepBoth(desktop.Text, result.Conflict.Theirs.Text);
        var resolved = _desktop.Resolve(Id, result.Local, result.Conflict, text, "DESKTOP-01", Now);
        Assert.Equal(ScratchPadSyncKind.Pushed, resolved.Kind);
        _desktop.WriteLocal(Id, resolved.Local);

        var laptop = Sync(_laptop);
        Assert.Equal(ScratchPadSyncKind.Pulled, laptop.Kind);
        Assert.Equal("shared\nfrom the desktop\nfrom the laptop\n", laptop.Local.Text);
        Assert.Equal(ScratchPadSyncKind.InSync, Sync(_desktop).Kind);
    }

    [Fact]
    public void A_sync_client_keeping_the_other_machines_file_over_ours_asks_rather_than_lose_ours()
    {
        Type(_desktop, "start");
        Sync(_laptop);
        var start = File.ReadAllText(_desktop.LibraryPath(Id));
        Assert.Equal(ScratchPadSyncKind.Pushed, Type(_desktop, "desktop's").Kind);
        // The laptop writes before the desktop's file reaches it, and the sync client keeps the laptop's.
        File.WriteAllText(_desktop.LibraryPath(Id), start);
        Assert.Equal(ScratchPadSyncKind.Pushed, Type(_laptop, "laptop's").Kind);

        var result = Sync(_desktop);

        Assert.Equal(ScratchPadSyncKind.Conflict, result.Kind);
        Assert.Equal(("desktop's", "laptop's"), (result.Local.Text, result.Conflict!.Theirs.Text));
    }

    [Fact]
    public void A_sync_clients_conflict_copy_is_asked_about_then_goes()
    {
        Type(_desktop, "start");
        Sync(_laptop);
        var desktopsFile = File.ReadAllText(_desktop.LibraryPath(Id));
        Type(_desktop, "desktop's");
        // Dropbox kept the laptop's file and renamed the desktop's.
        var desktops = File.ReadAllText(_desktop.LibraryPath(Id));
        File.WriteAllText(_desktop.LibraryPath(Id), desktopsFile);
        Type(_laptop, "laptop's");
        var copy = Path.Combine(Path.GetDirectoryName(_desktop.LibraryPath(Id))!, $"{Id} (DESKTOP-01's conflicted copy 2026-10-01).json");
        File.WriteAllText(copy, desktops);

        var laptop = Sync(_laptop);

        Assert.Equal(ScratchPadSyncKind.InSync, laptop.Kind);
        Assert.Equal(("desktop's", copy), (laptop.Conflict!.Theirs.Text, laptop.Conflict.CopyPath));
        var resolved = _laptop.Resolve(Id, laptop.Local, laptop.Conflict, "laptop's\ndesktop's\n", "LAPTOP", Now);
        Assert.Equal(ScratchPadSyncKind.Pushed, resolved.Kind);
        _laptop.WriteLocal(Id, resolved.Local);
        Assert.False(File.Exists(copy));
        // The desktop wrote the copy and nothing since: it takes the choice.
        var desktop = Sync(_desktop);
        Assert.Equal((ScratchPadSyncKind.Pulled, "laptop's\ndesktop's\n"), (desktop.Kind, desktop.Local.Text));
        Assert.Null(desktop.Conflict);
    }

    [Fact]
    public void A_copy_the_library_already_carries_on_from_goes_without_asking()
    {
        Type(_desktop, "first");
        var first = File.ReadAllText(_desktop.LibraryPath(Id));
        Type(_desktop, "first, then more");
        var copy = Path.Combine(Path.GetDirectoryName(_desktop.LibraryPath(Id))!, $"{Id} (1).json");
        File.WriteAllText(copy, first);

        var result = Sync(_desktop);

        Assert.Null(result.Conflict);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void A_library_file_being_written_is_neither_used_nor_written_over()
    {
        Type(_desktop, "notes");
        File.WriteAllText(_desktop.LibraryPath(Id), "{ half written");

        var result = Type(_desktop, "notes, edited");

        Assert.Equal(ScratchPadSyncKind.Unavailable, result.Kind);
        Assert.Equal("{ half written", File.ReadAllText(_desktop.LibraryPath(Id)));
        Assert.Equal("notes, edited", _desktop.ReadLocal(Id, Now)!.Text);
    }

    [Fact]
    public void A_library_that_cant_be_reached_keeps_the_pad_here_until_it_can()
    {
        // The library's folder is under a file, as a drive that isn't there can't be written to either.
        var blocked = _root.Write("blocked", "");
        var library = Path.Combine(blocked, "library");
        var machine = new ScratchPadStore(_root.Combine("machine"), () => library);
        machine.WriteLocal(Id, new ScratchPadFile { Text = "kept", Revision = "l1" });

        var result = machine.Sync(Id, machine.ReadLocal(Id, Now)!);

        Assert.Equal(ScratchPadSyncKind.Unavailable, result.Kind);
        Assert.False(string.IsNullOrEmpty(result.Reason));
        Assert.Equal("kept", machine.ReadLocal(Id, Now)!.Text);
    }

    [Fact]
    public void A_copy_on_this_machine_that_isnt_a_pad_is_kept_aside_and_the_pad_starts_empty()
    {
        var path = _desktop.LocalPath(Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a pad");

        Assert.Null(_desktop.ReadLocal(Id, Now));
        Assert.False(File.Exists(path));
        Assert.Equal("not a pad", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, $"{Id}.20261001120000.bad")));
    }

    [Fact]
    public void An_id_that_isnt_a_pads_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _desktop.LocalPath(@"..\settings"));
    }
}
