using Claudette.Core.Composer;
using Claudette.Core.Git;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Composer;

/// <summary>The composer's <c>@</c> file autocomplete (DESIGN.md §5, "Composer").</summary>
public sealed class FileMentionTests : IDisposable
{
    private readonly TempFolder _root = new("claudette-mentions");
    private readonly IsolatedGitLauncher _git;

    public FileMentionTests()
    {
        _git = new IsolatedGitLauncher(_root.Path);
    }

    public void Dispose() => _root.Dispose();

    private string Repo => _root.Combine("repo");

    // ---- Finding the mention being typed ----------------------------------------------------------------------

    [Theory]
    [InlineData("@", 1, 0, "")]
    [InlineData("see @src/ma", 11, 4, "src/ma")]
    [InlineData("see @src/ma and more", 11, 4, "src/ma")]
    [InlineData("line one\n@rea", 13, 9, "rea")]
    [InlineData("see @\"my fo", 11, 4, "my fo")]
    public void A_mention_is_found_up_to_the_caret(string text, int caret, int start, string query) =>
        Assert.Equal(new ComposerToken(start, caret - start, query), ComposerTokens.FindMention(text, caret));

    [Theory]
    [InlineData("mail me@example.com", 19)]
    [InlineData("see @src/main.cs now", 20)]
    [InlineData("see @\"my file.txt\" now", 18)]
    [InlineData("no mention", 5)]
    [InlineData("@", 0)]
    public void Otherwise_there_is_none(string text, int caret) => Assert.Null(ComposerTokens.FindMention(text, caret));

    [Fact]
    public void Picking_a_file_inserts_the_path_and_a_space()
    {
        var text = "see @ma please";
        var token = ComposerTokens.FindMention(text, 7)!.Value;

        Assert.Equal(new ComposerEdit("see @src/main.cs please", 17), ComposerTokens.ReplaceMention(text, token, "src/main.cs"));
    }

    [Fact]
    public void Picking_a_folder_leaves_the_mention_open()
    {
        var token = ComposerTokens.FindMention("@sr", 3)!.Value;

        Assert.Equal(new ComposerEdit("@src/", 5), ComposerTokens.ReplaceMention("@sr", token, "src/"));
    }

    [Fact]
    public void Paths_with_spaces_are_quoted()
    {
        Assert.Equal("@\"docs/my notes.md\"", ComposerTokens.Mention("docs/my notes.md"));
        Assert.Equal("@\"my folder/", ComposerTokens.Mention("my folder/", closeQuote: false));
        var text = "@\"my folder/no";
        var token = ComposerTokens.FindMention(text, text.Length)!.Value;
        Assert.Equal("my folder/no", token.Query);
        Assert.Equal("@\"my folder/notes.md\" ", ComposerTokens.ReplaceMention(text, token, "my folder/notes.md").Text);
    }

    // ---- Matching -----------------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<IndexedPath> Paths = ProjectFileIndex.WithFolders(
    [
        "README.md",
        "src/Claudette.App/ViewModels/TabViewModel.cs",
        "src/Claudette.App/ViewModels/TabViewModel.Composer.cs",
        "src/Claudette.App/Views/TabView.axaml",
        "src/Claudette.Core/Composer/PathMatcher.cs",
        "docs/readme-old.txt",
        "tests/Readers/ReaderTests.cs",
    ], 1000);

    private static string[] Match(string query) => PathMatcher.Match(Paths, query, cancellationToken: TestContext.Current.CancellationToken).Select(p => p.Path).ToArray();

    [Fact]
    public void Folders_are_listed_from_the_files()
    {
        Assert.Contains(Paths, p => p is { Path: "src/Claudette.App/", IsFolder: true, Name: "Claudette.App", Parent: "src/" });
        Assert.Equal(0, Paths.Single(p => p.Path == "src/").Depth);
    }

    [Fact]
    public void With_no_query_the_top_is_listed_folders_first() =>
        Assert.Equal(["docs/", "src/", "tests/", "README.md"], Match(""));

    [Fact]
    public void A_name_that_starts_with_the_query_comes_first()
    {
        var matches = Match("read");

        Assert.Equal(["README.md", "tests/Readers/", "docs/readme-old.txt", "tests/Readers/ReaderTests.cs"], matches.Take(4));
    }

    [Fact]
    public void A_path_prefix_lists_what_is_inside_shallow_first()
    {
        var matches = Match("src/claudette.app/");

        Assert.Equal(["src/Claudette.App/Views/", "src/Claudette.App/ViewModels/"], matches.Take(2));
        Assert.DoesNotContain("src/Claudette.App/", matches);
    }

    [Fact]
    public void Fuzzy_matching_prefers_word_starts()
    {
        var matches = Match("tvmco");

        Assert.Equal("src/Claudette.App/ViewModels/TabViewModel.Composer.cs", matches[0]);
        Assert.Contains("src/Claudette.Core/Composer/PathMatcher.cs", Match("pthmtch"));
        Assert.Empty(Match("zzqq"));
    }

    [Fact]
    public void A_paths_parts_are_worked_out_once()
    {
        var file = new IndexedPath("src/app/Main.cs");
        var folder = new IndexedPath("src/app/");

        Assert.Equal(("Main.cs", "src/app/", 2, false), (file.Name, file.Parent, file.Depth, file.IsFolder));
        Assert.Equal(("app", "src/", 1, true), (folder.Name, folder.Parent, folder.Depth, folder.IsFolder));
        Assert.Equal(new IndexedPath("src/app/Main.cs"), file);
    }

    [Fact]
    public void The_cap_counts_files_not_the_folders_theyre_in()
    {
        var paths = ProjectFileIndex.WithFolders(["a/b/c/one.txt", "a/b/c/two.txt", "three.txt"], maxFiles: 2);

        Assert.Equal(["a/", "a/b/", "a/b/c/", "a/b/c/one.txt", "a/b/c/two.txt"], paths.Select(p => p.Path));
    }

    [Fact]
    public void A_stale_match_stops()
    {
        using var stale = new CancellationTokenSource();
        stale.Cancel();

        Assert.Throws<OperationCanceledException>(() => PathMatcher.Match(Paths, "core", cancellationToken: stale.Token));
    }

    [Fact]
    public void A_letter_the_word_start_jump_would_skip_still_matches()
    {
        var paths = ProjectFileIndex.WithFolders(["ab/c/b.txt"], 10);

        Assert.Contains("ab/c/b.txt", PathMatcher.Match(paths, "bcb", cancellationToken: TestContext.Current.CancellationToken).Select(p => p.Path));
    }

    // ---- Listing the folder ------------------------------------------------------------------------------------

    [Fact]
    public async Task In_a_git_repository_ignored_files_are_left_out()
    {
        Assert.SkipWhen(!IsolatedGitLauncher.GitInstalled, "git isn't on PATH.");
        Directory.CreateDirectory(Repo);
        await _git.RunAsync(Repo, "init", "-q");
        _root.Write("repo/.gitignore", "build/\n*.log\n");
        _root.Write("repo/src/app.cs", "");
        _root.Write("repo/src/notes.log", "");
        _root.Write("repo/build/out.dll", "");
        _root.Write("repo/untracked.txt", "");
        await _git.RunAsync(Repo, "add", ".gitignore", "src/app.cs");

        var index = new ProjectFileIndex(Repo, new GitWorkingTree(_git, TimeProvider.System), TimeProvider.System);
        var paths = (await index.GetAsync()).Select(p => p.Path).ToArray();

        Assert.True(index.FromGit);
        Assert.Equal([".gitignore", "src/", "src/app.cs", "untracked.txt"], paths);
    }

    [Fact]
    public async Task From_a_subfolder_of_a_repository_paths_are_relative_to_it()
    {
        Assert.SkipWhen(!IsolatedGitLauncher.GitInstalled, "git isn't on PATH.");
        Directory.CreateDirectory(Repo);
        await _git.RunAsync(Repo, "init", "-q");
        _root.Write("repo/top.txt", "");
        _root.Write("repo/src/app.cs", "");

        var index = new ProjectFileIndex(Path.Combine(Repo, "src"), new GitWorkingTree(_git, TimeProvider.System), TimeProvider.System);

        Assert.Equal(["app.cs"], (await index.GetAsync()).Select(p => p.Path));
    }

    [Fact]
    public async Task Outside_git_the_walk_skips_heavy_folders()
    {
        _root.Write("plain/src/app.cs", "");
        _root.Write("plain/node_modules/lib/index.js", "");
        _root.Write("plain/bin/Debug/app.dll", "");
        _root.Write("plain/obj/project.assets.json", "");
        _root.Write("plain/.git/HEAD", "");
        _root.Write("plain/.github/workflows/ci.yml", "");
        var git = new GitWorkingTree(new IsolatedGitLauncher(_root.Path), TimeProvider.System, $"claudette-no-such-git-{Guid.NewGuid():N}");

        var index = new ProjectFileIndex(_root.Combine("plain"), git, TimeProvider.System);
        var paths = (await index.GetAsync()).Select(p => p.Path).ToArray();

        Assert.False(index.FromGit);
        Assert.Equal([".github/", ".github/workflows/", ".github/workflows/ci.yml", "src/", "src/app.cs"], paths);
    }

    [Fact]
    public void The_walk_stops_at_its_limits()
    {
        for (var i = 0; i < 30; i++)
        {
            _root.Write($"many/file{i:00}.txt", "");
        }
        _root.Write("many/a/b/c/deep.txt", "");

        Assert.Equal(10, ProjectFileIndex.Walk(_root.Combine("many"), 10, 16).Count);
        Assert.DoesNotContain(ProjectFileIndex.Walk(_root.Combine("many"), 1000, 2), p => p.Path == "a/b/c/deep.txt");
        Assert.Empty(ProjectFileIndex.Walk(_root.Combine("missing"), 10, 16));
    }

    [Fact]
    public async Task The_listing_is_cached_until_it_is_stale()
    {
        _root.Write("cached/one.txt", "");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z"));
        var git = new GitWorkingTree(new IsolatedGitLauncher(_root.Path), time, $"claudette-no-such-git-{Guid.NewGuid():N}");
        var index = new ProjectFileIndex(_root.Combine("cached"), git, time) { MaxAge = TimeSpan.FromSeconds(15) };

        Assert.Single(await index.GetAsync());
        _root.Write("cached/two.txt", "");
        Assert.True(index.IsFresh);
        Assert.Single(await index.GetAsync());

        time.Advance(TimeSpan.FromSeconds(16));
        Assert.False(index.IsFresh);
        Assert.Equal(2, (await index.GetAsync()).Count);

        _root.Write("cached/three.txt", "");
        index.Invalidate();
        Assert.Equal(3, (await index.GetAsync()).Count);
    }
}
