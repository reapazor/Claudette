using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Composer;
using Claudette.Core.Development;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// What's typed outlives quitting, and can be put aside for any tab (DESIGN.md §5, "Drafts and the stash").
/// </summary>
public class DraftsAndStashTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static readonly string BigPaste = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"log line {i:D5} with something in it"));

    [Fact]
    public async Task A_tabs_draft_is_written_once_typing_pauses()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        tab.ComposerText = "half a thought";
        tab.AddSuffixCommand.Execute(h.Services.Suffix("clarify"));
        Assert.True(tab.AddImage(Png, "shot.png"));
        tab.AddPastedText(BigPaste);
        await h.Services.Drafts.FlushAsync();
        Assert.Null(h.Services.Drafts.Load(tab.Id));

        h.Time.Advance(TabViewModel.DraftSaveDelay);
        await h.Services.Drafts.FlushAsync();

        var saved = h.Services.Drafts.Load(tab.Id)!;
        Assert.Equal("half a thought", saved.Text);
        Assert.Equal(["clarify"], saved.SuffixIds);
        Assert.Equal(Png, Assert.Single(saved.Images!).Data);
        Assert.Equal([BigPaste], saved.PastedTexts);

        // Sending empties it, and the file goes.
        await tab.SendCommand.ExecuteAsync(null);
        h.Time.Advance(TabViewModel.DraftSaveDelay);
        await h.Services.Drafts.FlushAsync();
        Assert.Null(h.Services.Drafts.Load(tab.Id));
    }

    [Fact]
    public async Task Quitting_keeps_the_draft_and_closing_the_tab_deletes_it()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "typed just before quitting";

        // Quitting closes each tab without waiting for the pause.
        await tab.CloseAsync(killProcesses: true);
        await h.Services.Drafts.FlushAsync();
        Assert.Equal("typed just before quitting", h.Services.Drafts.Load(tab.Id)!.Text);

        var other = await h.OpenTabAsync();
        other.ComposerText = "not wanted";
        await h.Shell.CloseTabCommand.ExecuteAsync(other);
        await h.Services.Drafts.FlushAsync();
        Assert.Null(h.Services.Drafts.Load(other.Id));
    }

    [Fact]
    public async Task A_restored_tab_has_its_draft_back_and_the_drafts_of_tabs_not_restored_go()
    {
        await using var h = new TabTestHarness();
        var pinned = new TabState { Folder = h.WorkFolder, IsPinned = true };
        var unpinned = new TabState { Folder = h.WorkFolder };
        h.Services.State.Tabs = [pinned, unpinned];
        await h.Services.Drafts.SaveAsync(pinned.Id, new TabDraft("where I left off", ["plan"], null, [BigPaste]));
        await h.Services.Drafts.SaveAsync(unpinned.Id, new TabDraft("gone with its tab", []));

        h.Shell.Restore(null);

        var tab = Assert.Single(h.Shell.AllTabs);
        Assert.Equal("where I left off", tab.ComposerText);
        Assert.Equal(["plan"], tab.Chips.Select(c => c.Suffix.Id));
        Assert.Equal(BigPaste, Assert.Single(tab.PastedTexts).Text);
        await h.Services.Drafts.FlushAsync();
        Assert.Null(h.Services.Drafts.Load(unpinned.Id));
        Assert.NotNull(h.Services.Drafts.Load(pinned.Id));
    }

    [Fact]
    public async Task Stash_puts_the_whole_message_aside_for_any_tab()
    {
        await using var h = new TabTestHarness();
        h.Factory.ProcessPerSession = true;
        var first = await h.OpenTabAsync();
        var second = await h.OpenTabAsync();
        Assert.False(second.HasStash);

        first.ComposerText = "this belongs in the other tab\nwith a second line";
        first.AddSuffixCommand.Execute(h.Services.Suffix("test"));
        Assert.True(first.AddImage(Png, "shot.png"));
        first.StashCommand.Execute(null);

        // Out of the composer, and in the stash every tab shares.
        Assert.Equal("", first.ComposerText);
        Assert.Empty(first.Chips);
        Assert.Empty(first.Attachments);
        Assert.True(second.HasStash);
        Assert.Equal("Stashed (1) ▾", second.StashButtonText);
        var entry = Assert.Single(second.StashEntries);
        Assert.Equal("this belongs in the other tab", entry.Title);
        Assert.Equal($"{first.FolderName} · just now", entry.Detail);
        await h.Services.Drafts.FlushAsync();
        Assert.Single(new DraftStore(h.Services.Paths.DraftsDirectory).LoadStash());

        // Picked in the other tab: ahead of what's typed there, as a message taken back is.
        second.ComposerText = "typed here";
        second.UnstashCommand.Execute(entry);

        Assert.Equal("this belongs in the other tab\nwith a second line\n\ntyped here", second.ComposerText);
        Assert.Equal(["test"], second.Chips.Select(c => c.Suffix.Id));
        Assert.Single(second.Attachments);
        Assert.False(first.HasStash);
        await h.Services.Drafts.FlushAsync();
        Assert.Empty(new DraftStore(h.Services.Paths.DraftsDirectory).LoadStash());
    }

    [Fact]
    public async Task Nothing_typed_stashes_nothing_and_an_entry_can_be_deleted()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        tab.StashCommand.Execute(null);
        Assert.False(tab.HasStash);

        Assert.True(tab.AddImage(Png, "shot.png"));
        tab.StashCommand.Execute(null);
        var entry = Assert.Single(tab.StashEntries);
        Assert.Equal("An image", entry.Title);

        h.Time.Advance(TimeSpan.FromMinutes(5));
        tab.RefreshStash();
        Assert.EndsWith("5 min ago", Assert.Single(tab.StashEntries).Detail, StringComparison.Ordinal);

        tab.RemoveStashedCommand.Execute(entry);
        Assert.False(tab.HasStash);
        Assert.Empty(tab.Attachments);
    }

    [Fact]
    public async Task The_palette_offers_Stash_while_something_is_typed()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.DoesNotContain(h.Shell.PaletteEntries(), e => e.Label == "Stash the message");

        tab.ComposerText = "later";

        Assert.Contains(h.Shell.PaletteEntries(), e => e.Label == "Stash the message");
    }
}
