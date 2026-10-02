using Claudette.Core.Composer;
using Claudette.Core.Development;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Composer;

/// <summary>Unsent messages on disk: each tab's draft and the stash (DESIGN.md §5, "Drafts and the stash").</summary>
public sealed class DraftStoreTests : IDisposable
{
    private readonly TempFolder _root = new("claudette-drafts");

    public void Dispose() => _root.Dispose();

    private DraftStore Store => new(_root.Combine("drafts"));

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T12:00:00Z");

    [Fact]
    public void A_tabs_draft_comes_back_whole()
    {
        var draft = new TabDraft("fix the build", ["clarify"], [new DraftImage("shot.png", AttachmentTests.Png)], ["a long log\nof many lines"]);

        Assert.True(Store.Save("tab1", draft));
        var read = Store.Load("tab1")!;

        Assert.Equal("fix the build", read.Text);
        Assert.Equal(["clarify"], read.SuffixIds);
        Assert.Equal(AttachmentTests.Png, Assert.Single(read.Images!).Data);
        Assert.Equal("shot.png", read.Images![0].Name);
        Assert.Equal(["a long log\nof many lines"], read.PastedTexts);
        Assert.Null(Store.Load("tab2"));
    }

    [Fact]
    public void An_empty_draft_deletes_the_saved_one()
    {
        Store.Save("tab1", new TabDraft("x", []));

        Assert.True(Store.Save("tab1", new TabDraft("  ", [], [], [])));

        Assert.Null(Store.Load("tab1"));
        Assert.Empty(Directory.GetFiles(_root.Combine("drafts")));
        Assert.True(Store.Save("tab1", null));
    }

    [Fact]
    public void Drafts_of_tabs_not_kept_are_deleted()
    {
        Store.Save("keep", new TabDraft("a", []));
        Store.Save("gone", new TabDraft("b", []));
        Store.AddToStash(new StashedDraft("s1", Now, "/work", new TabDraft("stashed", [])));

        Store.KeepOnly(new HashSet<string> { "keep" });

        Assert.NotNull(Store.Load("keep"));
        Assert.Null(Store.Load("gone"));
        // The stash isn't any tab's.
        Assert.Single(Store.LoadStash());
    }

    [Fact]
    public void The_stash_lists_newest_first_and_entries_can_be_taken_out()
    {
        Store.AddToStash(new StashedDraft("older", Now, "/work", new TabDraft("first", [])));
        Store.AddToStash(new StashedDraft("newer", Now.AddMinutes(5), "/api", new TabDraft("second", [], null, ["paste"])));

        Assert.Equal(["second", "first"], Store.LoadStash().Select(s => s.Draft.Text));
        Assert.Equal("/api", Store.LoadStash()[0].Folder);
        Assert.Equal(["paste"], Store.LoadStash()[0].Draft.PastedTexts);

        Assert.True(Store.RemoveFromStash("newer"));
        Assert.Equal(["first"], Store.LoadStash().Select(s => s.Draft.Text));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Ids_that_arent_safe_file_names_are_refused(string id)
    {
        Assert.False(Store.Save(id, new TabDraft("x", [])));
        Assert.Null(Store.Load(id));
        Assert.False(Store.AddToStash(new StashedDraft(id, Now, "/work", new TabDraft("x", []))));
        Assert.False(Directory.Exists(_root.Combine("escape")));
    }

    [Fact]
    public void A_file_that_isnt_a_draft_reads_as_none()
    {
        _root.Write("drafts/tab-bad.json", "{ not json");
        _root.Write("drafts/stash/broken.json", "[]");
        Store.AddToStash(new StashedDraft("good", Now, "/work", new TabDraft("fine", [])));

        Assert.Null(Store.Load("bad"));
        Assert.Equal(["fine"], Store.LoadStash().Select(s => s.Draft.Text));
    }
}
