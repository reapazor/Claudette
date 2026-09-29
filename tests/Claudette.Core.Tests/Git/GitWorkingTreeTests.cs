using Claudette.Core.Diffs;
using Claudette.Core.Git;
using Claudette.Core.Processes;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Git;

/// <summary>Runs the real <c>git</c> against temporary repositories, isolated from the user's and system git config.</summary>
public sealed class GitWorkingTreeTests : IDisposable
{
    private static readonly bool GitInstalled = FileProbe.Instance.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") is not null;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-git-{Guid.NewGuid():N}");
    private readonly string _repo;
    private readonly IsolatedGitLauncher _launcher;
    private readonly GitWorkingTree _git;

    public GitWorkingTreeTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        _launcher = new IsolatedGitLauncher(_root);
        _git = new GitWorkingTree(_launcher, TimeProvider.System);
    }

    public void Dispose()
    {
        // Git writes its objects read-only, which stops Directory.Delete on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_git_everything_is_empty()
    {
        var git = new GitWorkingTree(new ProcessLauncher(), TimeProvider.System, $"claudette-no-such-git-{Guid.NewGuid():N}");

        Assert.Null(await git.GetRepositoryRootAsync(_repo, Token));
        Assert.Empty(await git.GetChangesAsync(_repo, Token));
        Assert.Null(await git.GetHeadContentAsync(_repo, Path.Combine(_repo, "a.txt"), Token));
        Assert.Null(await git.HasUncommittedChangesAsync(_repo, Token));
    }

    [Fact]
    public async Task Outside_a_repository_everything_is_empty()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");

        Assert.Null(await _git.GetRepositoryRootAsync(_repo, Token));
        Assert.Empty(await _git.GetChangesAsync(_repo, Token));
        Assert.Null(await _git.GetHeadContentAsync(_repo, Path.Combine(_repo, "a.txt"), Token));
        Assert.Null(await _git.HasUncommittedChangesAsync(_repo, Token));
        Assert.Null(await _git.GetRepositoryRootAsync(Path.Combine(_root, "missing"), Token));
    }

    [Fact]
    public async Task The_root_is_found_from_a_subfolder()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await InitAsync();
        Directory.CreateDirectory(Path.Combine(_repo, "src", "deep"));

        var root = await _git.GetRepositoryRootAsync(Path.Combine(_repo, "src", "deep"), Token);

        Assert.NotNull(root);
        Assert.True(Path.IsPathFullyQualified(root));
        Assert.Equal(Path.GetFileName(_repo), Path.GetFileName(root));
        Assert.True(Directory.Exists(Path.Combine(root, ".git")));
    }

    [Fact]
    public async Task Changes_against_head_have_kinds_and_line_counts()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await InitAsync();
        Write("modified.txt", "one\ntwo\nthree\n");
        Write("deleted.txt", "gone\n");
        Write("renamed-old.txt", string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n")));
        File.WriteAllBytes(Path.Combine(_repo, "image.bin"), [1, 0, 2, 0, 3]);
        Write("sub/stay.txt", "same\n");
        await CommitAsync();

        Write("modified.txt", "one\n2\nthree\nfour\n");
        File.Delete(Path.Combine(_repo, "deleted.txt"));
        await GitAsync("mv", "renamed-old.txt", "renamed-new.txt");
        Write("staged.txt", "s1\ns2\n");
        await GitAsync("add", "staged.txt");
        Write("untracked dir/new file ü.txt", "u1\nu2\nu3");
        File.WriteAllBytes(Path.Combine(_repo, "image.bin"), [9, 0, 9]);

        var root = (await _git.GetRepositoryRootAsync(_repo, Token))!;
        var changes = (await _git.GetChangesAsync(Path.Combine(_repo, "sub"), Token))
            .ToDictionary(c => Path.GetRelativePath(root, c.Path).Replace('\\', '/'));

        Assert.Equal(6, changes.Count);
        Assert.All(changes.Values, c => Assert.True(Path.IsPathFullyQualified(c.Path)));
        Assert.Equal((GitChangeKind.Modified, 2, 1), Summary(changes["modified.txt"]));
        Assert.Equal((GitChangeKind.Deleted, 0, 1), Summary(changes["deleted.txt"]));
        Assert.Equal((GitChangeKind.Renamed, 0, 0), Summary(changes["renamed-new.txt"]));
        Assert.Equal(Path.Combine(root, "renamed-old.txt"), changes["renamed-new.txt"].OldPath);
        Assert.Equal((GitChangeKind.Added, 2, 0), Summary(changes["staged.txt"]));
        Assert.Equal((GitChangeKind.Untracked, 3, 0), Summary(changes["untracked dir/new file ü.txt"]));
        Assert.Equal((GitChangeKind.Modified, null, null), Summary(changes["image.bin"]));
        Assert.Null(changes["modified.txt"].OldPath);
    }

    [Fact]
    public async Task A_repository_without_commits_counts_everything_as_added()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await InitAsync();
        Write("staged.txt", "a\nb\n");
        await GitAsync("add", "staged.txt");
        Write("loose.txt", "c\n");

        var changes = (await _git.GetChangesAsync(_repo, Token)).ToDictionary(c => Path.GetFileName(c.Path));

        Assert.Equal((GitChangeKind.Added, 2, 0), Summary(changes["staged.txt"]));
        Assert.Equal((GitChangeKind.Untracked, 1, 0), Summary(changes["loose.txt"]));
        Assert.Null(await _git.GetHeadContentAsync(_repo, Path.Combine(_repo, "staged.txt"), Token));
        Assert.True(await _git.HasUncommittedChangesAsync(_repo, Token));
    }

    [Fact]
    public async Task Head_content_is_read_by_absolute_path()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await InitAsync();
        Write("src/app.txt", "line 1\r\nline 2\n");
        Write("with space ü.txt", "x\n");
        Write("empty.txt", "");
        await CommitAsync();
        Write("src/app.txt", "changed");
        Write("untracked.txt", "new");
        var root = (await _git.GetRepositoryRootAsync(_repo, Token))!;

        Assert.Equal("line 1\nline 2\n", await _git.GetHeadContentAsync(_repo, Path.Combine(_repo, "src", "app.txt"), Token));
        Assert.Equal("line 1\nline 2\n", await _git.GetHeadContentAsync(Path.Combine(_repo, "src"), Path.Combine(_repo, "src", "app.txt"), Token));
        Assert.Equal("line 1\nline 2\n", await _git.GetHeadContentAsync(_repo, Path.Combine(root, "src", "app.txt"), Token));
        Assert.Equal("x\n", await _git.GetHeadContentAsync(_repo, Path.Combine(_repo, "with space ü.txt"), Token));
        Assert.Equal("", await _git.GetHeadContentAsync(_repo, Path.Combine(_repo, "empty.txt"), Token));
        Assert.Null(await _git.GetHeadContentAsync(_repo, Path.Combine(_repo, "untracked.txt"), Token));
        Assert.Null(await _git.GetHeadContentAsync(_repo, Path.Combine(_root, "outside.txt"), Token));
    }

    [Fact]
    public async Task Head_content_is_found_through_a_symlinked_folder()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await InitAsync();
        Write("top.txt", "top\n");
        Write("sub/inner.txt", "inner\n");
        await CommitAsync();
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, _repo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't create symbolic links.");
        }

        // Git reports the real path as the root, so these are only reachable relative to the folder.
        Assert.Equal("inner\n", await _git.GetHeadContentAsync(Path.Combine(link, "sub"), Path.Combine(link, "sub", "inner.txt"), Token));
        Assert.Equal("top\n", await _git.GetHeadContentAsync(Path.Combine(link, "sub"), Path.Combine(link, "top.txt"), Token));
    }

    [Fact]
    public async Task Uncommitted_changes_include_untracked_files()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await InitAsync();
        Write("a.txt", "a\n");
        await CommitAsync();

        Assert.False(await _git.HasUncommittedChangesAsync(_repo, Token));
        Assert.Empty(await _git.GetChangesAsync(_repo, Token));

        Write("b.txt", "b\n");

        Assert.True(await _git.HasUncommittedChangesAsync(_repo, Token));
    }

    [Fact]
    public async Task Git_runs_with_quotepath_off_and_optional_locks_off_for_status()
    {
        var launcher = new Support.FakeProcessLauncher();
        var git = new GitWorkingTree(launcher, TimeProvider.System);
        var check = git.HasUncommittedChangesAsync(_repo, Token);
        launcher.Processes[0].Exit(0);
        await check;

        var spec = Assert.Single(launcher.Started);
        Assert.Equal("git", spec.FileName);
        Assert.Equal(["-c", "core.quotepath=false", "--no-optional-locks", "status", "--porcelain=v1", "-z"], spec.Arguments.Take(6));
        Assert.Equal(_repo, spec.WorkingDirectory);
    }

    private static (GitChangeKind, int?, int?) Summary(GitChange change) => (change.Kind, change.Added, change.Removed);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task InitAsync()
    {
        await GitAsync("init", "-q");
        await GitAsync("config", "user.name", "Claudette Tests");
        await GitAsync("config", "user.email", "tests@example.invalid");
        await GitAsync("config", "commit.gpgsign", "false");
        await GitAsync("config", "core.autocrlf", "false");
    }

    private async Task CommitAsync()
    {
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", "Commit");
    }

    private async Task GitAsync(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(
            _launcher, new ProcessStartSpec("git", arguments) { WorkingDirectory = _repo }, TimeSpan.FromSeconds(30), TimeProvider.System, Token);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}
