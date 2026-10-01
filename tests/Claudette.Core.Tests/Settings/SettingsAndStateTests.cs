using Claudette.Core.Development;
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
    public void The_first_launch_that_knows_about_trust_trusts_the_folders_already_used()
    {
        var state = new AppState
        {
            Tabs = [new TabState { Folder = "/work/api" }, new TabState { Folder = "" }],
            RecentFolders = [new RecentFolder { Path = "/work/web" }, new RecentFolder { Path = "/work/api" }],
            FavoriteFolders = ["/work/docs"],
        };

        state.TrustFoldersAlreadyUsed();
        Assert.Equal(["/work/api", "/work/web", "/work/docs"], state.TrustedFolders);

        // Once set, it's the user's list: a later launch doesn't add to it.
        state.FavoriteFolders.Add("/work/new");
        state.TrustFoldersAlreadyUsed();
        Assert.DoesNotContain("/work/new", state.TrustedFolders!);
    }

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
    public async Task A_file_that_cant_be_read_is_left_alone_and_not_saved_over()
    {
        // An antivirus scanner or backup holding it, say: not damaged, so it comes back at the next launch.
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{ "appearance": { "theme": "dark" } }""");
        var store = new JsonFileStore<AppSettings>(path);
        using (Unreadable(path))
        {
            var settings = store.Load();
            Assert.True(store.CouldNotRead);
            Assert.Equal(ThemeChoice.System, settings.Appearance.Theme);

            settings.Appearance.Theme = ThemeChoice.Light;
            await store.SaveAsync(settings, TestContext.Current.CancellationToken);
        }

        // Nothing set aside and no temporary file left. ("settings.json.*" would match the file itself: a Windows pattern.)
        Assert.Equal(["settings.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
        Assert.Equal(ThemeChoice.Dark, store.Load().Appearance.Theme);
        Assert.False(store.CouldNotRead);
    }

    [Fact]
    public async Task Saves_leave_no_temporary_files()
    {
        var store = new JsonFileStore<AppState>(Path.Combine(_root, "state.json"));

        await store.SaveAsync(new AppState(), TestContext.Current.CancellationToken);
        await store.SaveAsync(new AppState(), TestContext.Current.CancellationToken);

        Assert.Equal(["state.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    /// <summary>Makes a file unreadable until disposed: held open without sharing on Windows, no permissions elsewhere.</summary>
    private static IDisposable Unreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        var restore = new Restore(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, mode);
            }
        });
        try
        {
            File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException)
        {
            return restore;
        }
        restore.Dispose();
        Assert.Skip("Running as root, which can read any file.");
        return restore;
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public async Task Tabs_round_trip_with_their_token_totals()
    {
        var store = new JsonFileStore<AppState>(Path.Combine(_root, "state.json"));
        var tab = new TabState { Folder = "/work/api", SessionId = "s1", UserName = "Refactor", IsPinned = true, SyncToLibrary = true };
        tab.Tokens.Models["claude-opus-5-5"] = new ModelTokenTotals { Input = 100, Output = 20 };
        tab.Overrides.Effort = "high";

        await store.SaveAsync(new AppState { Tabs = [tab] }, TestContext.Current.CancellationToken);
        var loaded = Assert.Single(store.Load().Tabs);

        Assert.Equal("Refactor", loaded.UserName);
        Assert.True(loaded.IsPinned);
        Assert.True(loaded.SyncToLibrary);
        Assert.Equal("high", loaded.Overrides.Effort);
        Assert.Equal(120, loaded.Tokens.Total);
    }

    [Fact]
    public async Task Machine_state_round_trips_the_window_models_and_update_choices()
    {
        var store = new JsonFileStore<AppState>(Path.Combine(_root, "state.json"));
        var started = DateTimeOffset.Parse("2026-09-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        await store.SaveAsync(new AppState
        {
            Tabs = [new TabState { Folder = "/work/api", SessionStartedAt = started }],
            Window = new WindowPlacement(40, 60, 1200.5, 800, true),
            KnownModels = [new ModelInfo("opus", "claude-opus-5-5", "Opus", "Most capable", true, ["low", "high"])],
            DismissedClaudeUpdate = "2.1.290",
            NotifiedClaudeUpdate = "2.1.290",
        }, TestContext.Current.CancellationToken);

        var loaded = store.Load();

        Assert.Equal(new WindowPlacement(40, 60, 1200.5, 800, true), loaded.Window);
        var model = Assert.Single(loaded.KnownModels);
        Assert.Equal(("opus", "Opus", true), (model.Value, model.DisplayName, model.SupportsEffort));
        Assert.Equal(["low", "high"], model.SupportedEffortLevels);
        Assert.Equal("2.1.290", loaded.DismissedClaudeUpdate);
        Assert.Equal(started, loaded.Tabs.Single().SessionStartedAt);
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
