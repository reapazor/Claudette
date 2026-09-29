using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Settings;

public sealed class SettingsSyncTests : IDisposable
{
    private static readonly string[] Synced = ["appearance", "checkIns", "quickSuffixes", "newTabs.defaultModel", "newTabs.defaultEffort"];
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _root = new("claudette-settings-sync");

    public void Dispose() => _root.Dispose();

    private static JsonObject ToJson(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings, JsonFileStore<AppSettings>.Options)!.AsObject();

    private static Dictionary<string, JsonNode?> Flat(AppSettings settings) => SettingsSync.Flatten(ToJson(settings), Synced);

    private static SyncedSettingsFile RemoteWith(string path, JsonNode? value, DateTimeOffset changedAt, string machine = "LAPTOP") =>
        new() { Values = { [path] = new SyncedSetting { Value = value, ChangedAt = changedAt, Machine = machine } } };

    [Fact]
    public void Flatten_keeps_only_synced_leaves_with_arrays_as_one_value()
    {
        var flat = Flat(new AppSettings());

        Assert.Equal("system", flat["appearance.theme"]!.GetValue<string>());
        Assert.Equal(15, flat["checkIns.runTimeMinutes"]!.GetValue<int>());
        Assert.Equal(5, Assert.IsType<JsonArray>(flat["quickSuffixes"]).Count);
        Assert.True(flat.ContainsKey("newTabs.defaultModel"));
        Assert.Null(flat["newTabs.defaultModel"]);
        Assert.DoesNotContain("newTabs.recentFolderLimit", flat.Keys);
        Assert.DoesNotContain(flat.Keys, k => k.StartsWith("claudeCode", StringComparison.Ordinal) || k.StartsWith("sessions", StringComparison.Ordinal));
        Assert.DoesNotContain(flat.Keys, k => k.StartsWith("quickSuffixes.", StringComparison.Ordinal));
    }

    [Fact]
    public void Flatten_can_treat_an_object_as_one_value()
    {
        var settings = JsonNode.Parse("""{"keyboard":{"bindings":{"tab.close":"Ctrl+W"},"enabled":true}}""")!.AsObject();

        var flat = SettingsSync.Flatten(settings, ["keyboard"], leaves: ["keyboard.bindings"]);

        Assert.Equal(["keyboard.bindings", "keyboard.enabled"], flat.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Ctrl+W", flat["keyboard.bindings"]!["tab.close"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_sets_leaves_back_and_round_trips()
    {
        var source = new AppSettings();
        source.Appearance.Theme = ThemeChoice.Dark;
        source.CheckIns.RunTimeMinutes = 30;
        source.NewTabs.DefaultModel = "sonnet";
        source.QuickSuffixes.RemoveAt(0);
        var flat = Flat(source);

        var target = ToJson(new AppSettings());
        foreach (var (path, value) in flat)
        {
            SettingsSync.Apply(target, path, value);
        }
        SettingsSync.Apply(target, "brandNew.section.value", JsonValue.Create(3));

        Assert.Equal(flat.OrderBy(p => p.Key).Select(p => p.Value?.ToJsonString()), Flat(Deserialize(target)).OrderBy(p => p.Key).Select(p => p.Value?.ToJsonString()));
        var applied = Deserialize(target);
        Assert.Equal(ThemeChoice.Dark, applied.Appearance.Theme);
        Assert.Equal(30, applied.CheckIns.RunTimeMinutes);
        Assert.Equal("sonnet", applied.NewTabs.DefaultModel);
        Assert.Equal(4, applied.QuickSuffixes.Count);
        Assert.Equal(3, target["brandNew"]!["section"]!["value"]!.GetValue<int>());

        static AppSettings Deserialize(JsonObject json) => json.Deserialize<AppSettings>(JsonFileStore<AppSettings>.Options)!;
    }

    [Fact]
    public void First_merge_takes_synced_values_and_publishes_the_rest()
    {
        var remote = RemoteWith("appearance.theme", JsonValue.Create("dark"), T0.AddDays(-1));

        var result = SettingsSync.Merge(Flat(new AppSettings()), new SettingsSyncState(), remote, T0, "DESKTOP-01");

        Assert.Equal("dark", Assert.Single(result.ToApply).Value!.GetValue<string>());
        Assert.DoesNotContain("appearance.theme", result.Published);
        Assert.Contains("checkIns.runTimeMinutes", result.Published);
        Assert.Equal(T0, result.Remote.Values["checkIns.runTimeMinutes"].ChangedAt);
        Assert.Equal("DESKTOP-01", result.Remote.Values["checkIns.runTimeMinutes"].Machine);
        Assert.Equal("LAPTOP", result.Remote.Values["appearance.theme"].Machine);
        Assert.Equal(T0.AddDays(-1), result.State.Values["appearance.theme"].ChangedAt);
    }

    [Fact]
    public void A_local_edit_is_published_stamped_now()
    {
        var settings = new AppSettings();
        var first = SettingsSync.PublishAll(Flat(settings), T0, "DESKTOP-01");

        settings.Appearance.Theme = ThemeChoice.Light;
        var now = T0.AddHours(1);
        var result = SettingsSync.Merge(Flat(settings), first.State, first.Remote, now, "DESKTOP-01");

        Assert.Equal(["appearance.theme"], result.Published);
        Assert.Empty(result.ToApply);
        Assert.True(result.RemoteChanged);
        Assert.Equal("light", result.Remote.Values["appearance.theme"].Value!.GetValue<string>());
        Assert.Equal(now, result.Remote.Values["appearance.theme"].ChangedAt);
        Assert.Equal(now, result.State.Values["appearance.theme"].ChangedAt);
    }

    [Fact]
    public void A_newer_synced_change_is_applied()
    {
        var settings = new AppSettings();
        var first = SettingsSync.PublishAll(Flat(settings), T0, "DESKTOP-01");
        first.Remote.Values["appearance.theme"] = new SyncedSetting { Value = JsonValue.Create("dark"), ChangedAt = T0.AddMinutes(5), Machine = "LAPTOP" };

        var result = SettingsSync.Merge(Flat(settings), first.State, first.Remote, T0.AddMinutes(10), "DESKTOP-01");

        Assert.Equal("dark", result.ToApply["appearance.theme"]!.GetValue<string>());
        Assert.Empty(result.Published);
        Assert.False(result.RemoteChanged);
        Assert.Equal(T0.AddMinutes(5), result.State.Values["appearance.theme"].ChangedAt);
    }

    [Fact]
    public void When_both_sides_changed_the_newest_change_wins()
    {
        var settings = new AppSettings();
        var first = SettingsSync.PublishAll(Flat(settings), T0, "DESKTOP-01");
        settings.Appearance.Theme = ThemeChoice.Light;

        // The other machine changed it earlier than now: this machine's edit is newer.
        var remote = RemoteWith("appearance.theme", JsonValue.Create("dark"), T0.AddMinutes(5));
        var localWins = SettingsSync.Merge(Flat(settings), first.State, remote, T0.AddMinutes(10), "DESKTOP-01");
        Assert.Contains("appearance.theme", localWins.Published);
        Assert.DoesNotContain("appearance.theme", localWins.ToApply.Keys);
        Assert.Equal("light", localWins.Remote.Values["appearance.theme"].Value!.GetValue<string>());

        // The other machine's change is stamped after now (its clock is ahead): it's newer.
        var later = RemoteWith("appearance.theme", JsonValue.Create("dark"), T0.AddMinutes(15));
        var remoteWins = SettingsSync.Merge(Flat(settings), first.State, later, T0.AddMinutes(10), "DESKTOP-01");
        Assert.Equal("dark", remoteWins.ToApply["appearance.theme"]!.GetValue<string>());
        Assert.DoesNotContain("appearance.theme", remoteWins.Published);
    }

    [Fact]
    public async Task The_same_value_on_both_sides_is_not_a_conflict()
    {
        var settings = new AppSettings();
        var first = SettingsSync.PublishAll(Flat(settings), T0, "DESKTOP-01");
        settings.Appearance.Theme = ThemeChoice.Dark;
        first.Remote.Values["appearance.theme"] = new SyncedSetting { Value = JsonValue.Create("dark"), ChangedAt = T0.AddMinutes(5), Machine = "LAPTOP" };

        var result = SettingsSync.Merge(Flat(settings), first.State, first.Remote, T0.AddMinutes(10), "DESKTOP-01");

        Assert.Empty(result.Published);
        Assert.Empty(result.ToApply);

        // Nor is an unchanged setting whose value went through the file.
        var path = _root.Combine("settings-sync.json");
        await SettingsSync.WriteFileAsync(path, result.Remote, TestContext.Current.CancellationToken);
        var again = SettingsSync.Merge(Flat(settings), result.State, SettingsSync.ReadFile(path), T0.AddMinutes(20), "DESKTOP-01");
        Assert.Empty(again.Published);
        Assert.Empty(again.ToApply);
    }

    [Fact]
    public void A_synced_file_that_lost_a_newer_value_gets_it_back()
    {
        var settings = new AppSettings { Appearance = { Theme = ThemeChoice.Dark } };
        var first = SettingsSync.PublishAll(Flat(settings), T0, "DESKTOP-01");
        var older = RemoteWith("appearance.theme", JsonValue.Create("light"), T0.AddDays(-1));

        var result = SettingsSync.Merge(Flat(settings), first.State, older, T0.AddHours(1), "DESKTOP-01");

        Assert.Empty(result.ToApply);
        Assert.Contains("appearance.theme", result.Published);
        Assert.Equal("dark", result.Remote.Values["appearance.theme"].Value!.GetValue<string>());
        Assert.Equal(T0, result.Remote.Values["appearance.theme"].ChangedAt);
    }

    [Fact]
    public void Synced_settings_this_machine_does_not_have_are_kept_but_not_applied()
    {
        var remote = RemoteWith("notifications.newFeature", JsonValue.Create(true), T0);

        var result = SettingsSync.Merge(Flat(new AppSettings()), new SettingsSyncState(), remote, T0.AddMinutes(1), "DESKTOP-01");

        Assert.DoesNotContain("notifications.newFeature", result.ToApply.Keys);
        Assert.True(result.Remote.Values["notifications.newFeature"].Value!.GetValue<bool>());
        Assert.DoesNotContain("notifications.newFeature", result.State.Values.Keys);
    }

    [Fact]
    public void Publish_all_replaces_the_synced_settings()
    {
        var settings = new AppSettings { Appearance = { Theme = ThemeChoice.Light } };

        var result = SettingsSync.PublishAll(Flat(settings), T0, "DESKTOP-01");

        Assert.Empty(result.ToApply);
        Assert.Equal(Flat(settings).Keys.Order(StringComparer.Ordinal), result.Published.Order(StringComparer.Ordinal));
        Assert.All(result.Remote.Values.Values, v => Assert.Equal(T0, v.ChangedAt));
        Assert.Equal("light", result.Remote.Values["appearance.theme"].Value!.GetValue<string>());
        Assert.Equal(result.Remote.Values.Keys.Order(StringComparer.Ordinal), result.State.Values.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_file_round_trips_and_reads_leniently()
    {
        var path = _root.Combine("settings-sync.json");
        Assert.False(SettingsSync.HasRemoteValues(SettingsSync.ReadFile(path)));

        var published = SettingsSync.PublishAll(Flat(new AppSettings()), T0, "DESKTOP-01").Remote;
        await SettingsSync.WriteFileAsync(path, published, TestContext.Current.CancellationToken);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        var read = SettingsSync.ReadFile(path);

        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.Equal("DESKTOP-01", json["values"]!["checkIns.runTimeMinutes"]!["machine"]!.GetValue<string>());
        Assert.Equal(15, json["values"]!["checkIns.runTimeMinutes"]!["value"]!.GetValue<int>());
        Assert.True(SettingsSync.HasRemoteValues(read));
        Assert.Equal(published.Values.Keys.Order(StringComparer.Ordinal), read.Values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(T0, read.Values["appearance.theme"].ChangedAt);
        Assert.Empty(Directory.GetFiles(_root.Path, "*.tmp"));

        File.WriteAllText(path, """{"version":1,"values":{"a.b":{"value":1,"changedAt":"not a date"},"c":"nonsense","d.e":{"value":[1,2],"changedAt":"2026-01-01T00:00:00Z"}},"extra":true}""");
        Assert.Equal(["d.e"], SettingsSync.ReadFile(path).Values.Keys);

        File.WriteAllText(path, "{ broken");
        Assert.Empty(SettingsSync.ReadFile(path).Values);
    }

    [Fact]
    public async Task Two_machines_converge_through_the_file()
    {
        var path = _root.Combine("settings-sync.json");
        var ct = TestContext.Current.CancellationToken;
        var desktop = new AppSettings();
        var laptop = new AppSettings { Appearance = { Theme = ThemeChoice.Light } };
        var desktopState = new SettingsSyncState();
        var laptopState = new SettingsSyncState();

        async Task Sync(AppSettings settings, SettingsSyncState state, DateTimeOffset now, string machine, Action<SettingsSyncState> saveState)
        {
            var result = SettingsSync.Merge(Flat(settings), state, SettingsSync.ReadFile(path), now, machine);
            var json = ToJson(settings);
            foreach (var (key, value) in result.ToApply)
            {
                SettingsSync.Apply(json, key, value);
            }
            var applied = json.Deserialize<AppSettings>(JsonFileStore<AppSettings>.Options)!;
            settings.Appearance = applied.Appearance;
            settings.CheckIns = applied.CheckIns;
            settings.QuickSuffixes = applied.QuickSuffixes;
            settings.NewTabs = applied.NewTabs;
            if (result.RemoteChanged)
            {
                await SettingsSync.WriteFileAsync(path, result.Remote, ct);
            }
            saveState(result.State);
        }

        // The laptop turns sync on first ("Replace them with this machine's"), then the desktop joins.
        var initial = SettingsSync.PublishAll(Flat(laptop), T0, "LAPTOP");
        await SettingsSync.WriteFileAsync(path, initial.Remote, ct);
        laptopState = initial.State;
        await Sync(desktop, desktopState, T0.AddMinutes(1), "DESKTOP-01", s => desktopState = s);
        Assert.Equal(ThemeChoice.Light, desktop.Appearance.Theme);

        // Each edits a different setting; neither overwrites the other's.
        desktop.CheckIns.RunTimeMinutes = 45;
        laptop.Appearance.ConversationFontSize = 16;
        await Sync(desktop, desktopState, T0.AddMinutes(2), "DESKTOP-01", s => desktopState = s);
        await Sync(laptop, laptopState, T0.AddMinutes(3), "LAPTOP", s => laptopState = s);
        await Sync(desktop, desktopState, T0.AddMinutes(4), "DESKTOP-01", s => desktopState = s);

        Assert.Equal(45, laptop.CheckIns.RunTimeMinutes);
        Assert.Equal(16, desktop.Appearance.ConversationFontSize);
        Assert.Equal(Flat(desktop).Select(p => (p.Key, p.Value?.ToJsonString())), Flat(laptop).Select(p => (p.Key, p.Value?.ToJsonString())));
    }
}
