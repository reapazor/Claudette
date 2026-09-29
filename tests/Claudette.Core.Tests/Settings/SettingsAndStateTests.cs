using Claudette.Core.Git;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.Core.Tests.Settings;

public sealed class SettingsAndStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-settings-{Guid.NewGuid():N}");

    public SettingsAndStateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_missing_file_loads_defaults()
    {
        var settings = new JsonFileStore<AppSettings>(Path.Combine(_root, "settings.json")).Load();

        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.Equal(5, settings.QuickSuffixes.Count);
        Assert.Equal(15, settings.CheckIns.RunTimeMinutes);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        var store = new JsonFileStore<AppSettings>(Path.Combine(_root, "settings.json"));
        var settings = new AppSettings();
        settings.Appearance.Theme = ThemeChoice.Dark;
        settings.NewTabs.DefaultModel = "sonnet";
        settings.QuickSuffixes.Add(new QuickSuffix { Label = "Mine", Text = "Do it my way." });

        await store.SaveAsync(settings, TestContext.Current.CancellationToken);
        var loaded = store.Load();

        Assert.Equal(ThemeChoice.Dark, loaded.Appearance.Theme);
        Assert.Equal("sonnet", loaded.NewTabs.DefaultModel);
        Assert.Contains(loaded.QuickSuffixes, s => s.Label == "Mine");
        Assert.Contains("\"theme\": \"dark\"", await File.ReadAllTextAsync(store.Path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_file_is_kept_aside_and_defaults_load()
    {
        var path = Path.Combine(_root, "state.json");
        File.WriteAllText(path, "{ this is not json");

        var state = new JsonFileStore<AppState>(path).Load();

        Assert.Empty(state.Tabs);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_root, "state.json.*.bad"));
    }

    [Fact]
    public async Task Tabs_round_trip_with_their_token_totals()
    {
        var store = new JsonFileStore<AppState>(Path.Combine(_root, "state.json"));
        var tab = new TabState { Folder = "/work/api", SessionId = "s1", UserName = "Refactor", IsPinned = true };
        tab.Tokens.Models["claude-opus-5-5"] = new ModelTokenTotals { Input = 100, Output = 20 };
        tab.Overrides.Effort = "high";

        await store.SaveAsync(new AppState { Tabs = [tab] }, TestContext.Current.CancellationToken);
        var loaded = Assert.Single(store.Load().Tabs);

        Assert.Equal("Refactor", loaded.UserName);
        Assert.True(loaded.IsPinned);
        Assert.Equal("high", loaded.Overrides.Effort);
        Assert.Equal(120, loaded.Tokens.Total);
    }

    [Fact]
    public void Recent_folders_move_to_the_top_and_are_trimmed_but_favorites_stay()
    {
        var state = new AppState();
        var now = DateTimeOffset.UnixEpoch;
        FolderHistory.Touch(state, Path.Combine(_root, "a"), now, limit: 2);
        FolderHistory.Touch(state, Path.Combine(_root, "b"), now, limit: 2);
        FolderHistory.SetFavorite(state, Path.Combine(_root, "a"), true);
        FolderHistory.Touch(state, Path.Combine(_root, "c"), now, limit: 2);
        FolderHistory.Touch(state, Path.Combine(_root, "d"), now, limit: 2);

        Assert.Equal(["d", "c", "a"], state.RecentFolders.Select(r => Path.GetFileName(r.Path)));

        FolderHistory.Touch(state, Path.Combine(_root, "c") + Path.DirectorySeparatorChar, now, limit: 2);
        Assert.Equal("c", Path.GetFileName(state.RecentFolders[0].Path));
        Assert.Equal(3, state.RecentFolders.Count);
    }

    [Fact]
    public void Token_totals_add_up_per_model()
    {
        var totals = new TokenTotals();
        MessageParser.TryParse("""
            {"type":"result","subtype":"success","is_error":false,"modelUsage":{
              "claude-opus-5-5":{"inputTokens":10,"outputTokens":20,"cacheReadInputTokens":1000,"cacheCreationInputTokens":300,"costUSD":0.5},
              "claude-haiku-4-5":{"inputTokens":5,"outputTokens":5,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,"costUSD":0.01}}}
            """, out var message, out _);

        totals.Add((ResultMessage)message!);
        totals.Add((ResultMessage)message!);

        Assert.Equal(2, totals.Turns);
        Assert.Equal(2680, totals.Total);
        Assert.Equal(1.02, totals.EstimatedCostUsd, 5);
        Assert.Equal("2.7k tok", TokenTotals.Short(totals.Total));
    }

    [Fact]
    public void Reads_the_git_branch_including_from_a_subfolder_and_a_worktree()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "src", "deep"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/feature/auth\n");
        var worktreeGit = Path.Combine(_root, "wt-git");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "HEAD"), "0123456789abcdef\n");
        var worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}\n");

        Assert.Equal("feature/auth", GitInfo.TryGetBranch(Path.Combine(repo, "src", "deep")));
        Assert.Equal("0123456", GitInfo.TryGetBranch(worktree));
    }

    [Fact]
    public void The_recent_folder_shortlist_puts_favorites_first_and_tells_same_names_apart()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\src" : "/src";
        string P(params string[] parts) => Path.Combine([root, .. parts]);
        var state = new AppState
        {
            FavoriteFolders = [P("personal", "api")],
            RecentFolders = [new() { Path = P("work", "api") }, new() { Path = P("gone") }, new() { Path = P("docs") }, new() { Path = P("personal", "api") }],
        };

        var shortlist = FolderHistory.Shortlist(state, max: 10, exists: path => !path.EndsWith("gone", StringComparison.Ordinal));

        Assert.Equal([("personal/api", P("personal", "api")), ("work/api", P("work", "api")), ("docs", P("docs"))], shortlist);
        Assert.Single(FolderHistory.Shortlist(state, max: 1, exists: _ => true));
    }
}
