using Claudette.App.Diffs;
using Claudette.App.Tests.Support;

namespace Claudette.App.Tests;

/// <summary>Reverting Claude's changes from the diff view (DESIGN.md §8, "Reverting").</summary>
public sealed class DiffRevertTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"claudette-revert-{Guid.NewGuid():N}");

    private const string Before = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\neleven\ntwelve\n";

    private async Task<DiffWindowViewModel> OpenAsync(string path, string? before, bool beforeKnown = true, bool fromGit = false)
    {
        var source = new DiffSource(path, Path.GetFileName(path), before, "before Claude's first change", null,
            () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, beforeKnown, FromGit: fromGit);
        var view = new DiffWindowViewModel(source, dark: false);
        await TabTestHarness.Eventually(() => !view.IsLoading, "the diff");
        return view;
    }

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Reverting_a_hunk_undoes_just_that_change()
    {
        var path = Write("a.txt", Before.Replace("two\n", "TWO\n", StringComparison.Ordinal).Replace("eleven\n", "eleven\nadded\n", StringComparison.Ordinal));
        var view = await OpenAsync(path, Before);
        var hunks = view.InlineRows.Where(r => r.CanRevert).Select(r => r.Hunk).ToArray();
        Assert.Equal(2, hunks.Length);

        await view.RevertHunkCommand.ExecuteAsync(hunks[1]);

        Assert.Equal(Before.Replace("two\n", "TWO\n", StringComparison.Ordinal), await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(view.InlineRows, r => r.CanRevert);
        Assert.Equal("+1 −1", view.Stats);
    }

    [Fact]
    public async Task Reverting_the_file_asks_first_and_deletes_one_Claude_made()
    {
        var path = Write("new.txt", "made by Claude\n");
        var view = await OpenAsync(path, before: null);
        Assert.True(view.CanRevert);

        view.AskToRevertFileCommand.Execute(null);
        Assert.True(view.IsConfirmingRevert);
        await view.RevertFileCommand.ExecuteAsync(null);

        Assert.False(File.Exists(path));
        Assert.False(view.IsConfirmingRevert);
        Assert.False(view.CanRevert);
    }

    [Fact]
    public async Task From_git_a_file_not_at_HEAD_is_never_deleted()
    {
        // Untracked: maybe the user's own, not Claude's.
        var path = Write("notes.txt", "mine\n");
        var view = await OpenAsync(path, before: null, fromGit: true);

        Assert.False(view.CanRevert);
        Assert.DoesNotContain(view.InlineRows, r => r.CanRevert);
        await view.RevertFileCommand.ExecuteAsync(null);

        Assert.Equal("mine\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task From_git_the_file_goes_back_to_HEAD_in_its_own_line_endings()
    {
        // Checked out with Windows line endings; git's copy has Unix ones.
        var path = Write("a.txt", Before.Replace("two\n", "TWO\n", StringComparison.Ordinal).ReplaceLineEndings("\r\n"));
        var view = await OpenAsync(path, Before, fromGit: true);
        Assert.Equal("Put the whole file back as it is at HEAD", view.RevertFileTip);

        await view.RevertFileCommand.ExecuteAsync(null);

        Assert.Equal(Before.ReplaceLineEndings("\r\n"), await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_file_changed_since_it_was_shown_isnt_reverted()
    {
        var path = Write("a.txt", Before.Replace("two\n", "TWO\n", StringComparison.Ordinal));
        var view = await OpenAsync(path, Before);
        var hunk = view.InlineRows.Single(r => r.CanRevert).Hunk;
        await File.WriteAllTextAsync(path, "Claude kept going\n", TestContext.Current.CancellationToken);

        await view.RevertHunkCommand.ExecuteAsync(hunk);
        await view.RevertFileCommand.ExecuteAsync(null);

        Assert.Equal("Claude kept going\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.StartsWith("The file changed since this was shown", view.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_known_before_there_is_nothing_to_revert_to()
    {
        var path = Write("a.txt", Before);
        var view = await OpenAsync(path, before: null, beforeKnown: false);

        Assert.False(view.CanRevert);
        Assert.DoesNotContain(view.InlineRows, r => r.CanRevert);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
