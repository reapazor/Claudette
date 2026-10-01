using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>Settings (DESIGN.md §14): Reset to defaults, favorites, fonts, lists from Claude Code, the overrides dot.</summary>
public class SettingsDefaultsTests
{
    [Fact]
    public async Task Every_category_can_be_reset_to_its_defaults()
    {
        await using var h = new TabTestHarness();
        var s = h.Services.Settings;
        s.Sessions.RestoreUnpinnedTabs = true;
        s.Sessions.MachineName = "DESK";
        s.Sessions.SyncNewTabs = true;
        s.Sessions.LibraryFolder = Path.Combine(h.Root, "library");
        s.ClaudeCode.CheckForUpdates = false;
        s.ClaudeCode.ClaudePath = "/opt/claude";
        s.NewTabs.DefaultModel = "opus";
        s.NewTabs.RecentFolderLimit = 5;
        s.Appearance.Theme = ThemeChoice.Dark;
        s.Appearance.Style = AppStyle.Claude;
        s.Appearance.CodeFont = "Fira Code";
        s.Appearance.ShowContextOnTabs = false;
        s.Appearance.Density = Density.Compact;
        s.DiffTool.Kind = "custom";
        s.DiffTool.CustomCommand = "meld {left} {right}";
        s.Advanced.ExtraArguments = "--verbose";
        s.Advanced.LogProtocol = true;
        var settings = new SettingsViewModel(h.Services, null);

        settings.ResetSessionsCommand.Execute(null);
        settings.ResetClaudeCodeCommand.Execute(null);
        settings.ResetNewTabsCommand.Execute(null);
        settings.ResetAppearanceCommand.Execute(null);
        settings.ResetDiffToolCommand.Execute(null);
        settings.ResetAdvancedCommand.Execute(null);

        Assert.False(s.Sessions.RestoreUnpinnedTabs);
        Assert.Null(s.Sessions.MachineName);
        Assert.False(s.Sessions.SyncNewTabs);
        Assert.False(settings.SyncNewTabs);
        // Where the library lives is its own decision, not a reset.
        Assert.Equal(Path.Combine(h.Root, "library"), s.Sessions.LibraryFolder);
        Assert.True(s.ClaudeCode.CheckForUpdates);
        Assert.Null(s.ClaudeCode.ClaudePath);
        Assert.Null(s.NewTabs.DefaultModel);
        Assert.Equal(20, s.NewTabs.RecentFolderLimit);
        Assert.Equal(ThemeChoice.System, s.Appearance.Theme);
        Assert.Equal(AppStyle.Standard, s.Appearance.Style);
        Assert.Equal("Standard", settings.Style.Label);
        Assert.Null(s.Appearance.CodeFont);
        Assert.True(s.Appearance.ShowContextOnTabs);
        Assert.True(settings.ShowContextOnTabs);
        Assert.Equal(Density.Comfortable, s.Appearance.Density);
        Assert.Equal(Density.Comfortable, settings.Density);
        Assert.Equal("builtIn", s.DiffTool.Kind);
        Assert.Equal("", s.Advanced.ExtraArguments);
        Assert.False(s.Advanced.LogProtocol);
        Assert.Equal("", settings.CodeFont);
    }

    [Fact]
    public async Task The_fallback_model_checkpoints_and_hook_rows_are_settings_with_defaults()
    {
        await using var h = new TabTestHarness();
        using var settings = new SettingsViewModel(h.Services, null);
        Assert.Equal("None", settings.FallbackModel.Label);
        Assert.True(settings.KeepFileCheckpoints);
        Assert.False(settings.ShowAllHookRuns);

        settings.FallbackModel = settings.FallbackModelChoices.Single(c => c.Value == "sonnet");
        settings.KeepFileCheckpoints = false;
        settings.ShowAllHookRuns = true;

        Assert.Equal(("sonnet", false, true), (h.Services.Settings.ClaudeCode.FallbackModel, h.Services.Settings.ClaudeCode.KeepFileCheckpoints, h.Services.Settings.ClaudeCode.ShowAllHookRuns));
        settings.ResetClaudeCodeCommand.Execute(null);
        Assert.Equal((null, true, false), (h.Services.Settings.ClaudeCode.FallbackModel, h.Services.Settings.ClaudeCode.KeepFileCheckpoints, h.Services.Settings.ClaudeCode.ShowAllHookRuns));
    }

    [Fact]
    public async Task Density_applies_at_once()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        Assert.Equal(Density.Comfortable, settings.Density);
        Assert.Equal([Density.Comfortable, Density.Compact], settings.Densities);
        Assert.False(h.Shell.IsCompact);

        settings.Density = Density.Compact;

        Assert.Equal(Density.Compact, h.Services.Settings.Appearance.Density);
        Assert.True(h.Shell.IsCompact);

        settings.ResetAppearanceCommand.Execute(null);

        Assert.False(h.Shell.IsCompact);
    }

    [Fact]
    public async Task Favorites_can_be_added_reordered_and_removed()
    {
        await using var h = new TabTestHarness();
        var a = Directory.CreateDirectory(Path.Combine(h.Root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(h.Root, "b")).FullName;
        var settings = new SettingsViewModel(h.Services, null);
        Assert.False(settings.HasFavorites);

        h.Platform.FolderToPick = a;
        await settings.AddFavoriteCommand.ExecuteAsync(null);
        h.Platform.FolderToPick = b;
        await settings.AddFavoriteCommand.ExecuteAsync(null);
        Assert.Equal(["a", "b"], settings.Favorites.Select(f => f.Name));

        settings.MoveFavoriteUpCommand.Execute(settings.Favorites[1]);
        Assert.Equal(["b", "a"], settings.Favorites.Select(f => f.Name));
        Assert.Equal(["b", "a"], h.Services.State.FavoriteFolders.Select(Path.GetFileName));
        // The ends don't move further.
        settings.MoveFavoriteUpCommand.Execute(settings.Favorites[0]);
        Assert.Equal(["b", "a"], settings.Favorites.Select(f => f.Name));

        settings.RemoveFavoriteCommand.Execute(settings.Favorites[0]);
        Assert.Equal(["a"], settings.Favorites.Select(f => f.Name));
        Assert.False(FolderHistory.IsFavorite(h.Services.State, b));
    }

    [Fact]
    public async Task Fonts_are_saved_and_empty_means_the_default()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null)
        {
            ConversationFont = "  Source Serif 4 ",
            CodeFont = "JetBrains Mono",
        };

        Assert.Equal("Source Serif 4", h.Services.Settings.Appearance.ConversationFont);
        Assert.Equal("JetBrains Mono", h.Services.Settings.Appearance.CodeFont);
        settings.ConversationFont = "";
        Assert.Null(h.Services.Settings.Appearance.ConversationFont);
        Assert.Single(settings.SearchResultsFor("code font"), r => r.Label == "Code font");
    }

    [Fact]
    public async Task Model_and_effort_lists_come_from_what_Claude_Code_offered()
    {
        await using var h = new TabTestHarness();
        var before = new SettingsViewModel(h.Services, null);
        // Nothing offered yet on this machine: the built-in list.
        Assert.Contains(before.ModelChoices, c => c.Value == "sonnet");

        var tab = await h.OpenTabAsync();
        var settings = new SettingsViewModel(h.Services, null);

        Assert.Equal([null, "opus", "haiku"], settings.ModelChoices.Select(c => c.Value));
        Assert.Equal([null, "low", "high"], settings.EffortChoices.Select(c => c.Value));
        Assert.Equal(["opus", "haiku"], h.Services.State.KnownModels.Select(m => m.Value));
        var tabSettings = new TabSettingsViewModel(h.Services, tab, () => { });
        Assert.Equal([null, "low", "high"], tabSettings.EffortChoices.Select(c => c.Value));
    }

    [Fact]
    public async Task A_tab_with_its_own_settings_is_marked()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.False(tab.HasOverrides);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await tab.ChooseEffortCommand.ExecuteAsync("high");

        Assert.True(tab.HasOverrides);
        Assert.Contains(nameof(TabViewModel.HasOverrides), changed);
    }

    [Fact]
    public async Task Style_offers_Standard_and_Claude_and_saves_the_choice()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null) { SearchText = "style" };
        var changed = 0;
        h.Services.SettingsChanged += (_, _) => changed++;

        Assert.Equal(["Standard", "Claude"], settings.StyleOptions.Select(o => o.ToString()));
        Assert.Equal(AppStyle.Standard, settings.Style.Style);
        Assert.False(h.Shell.IsClaudeStyle);
        Assert.Contains(settings.SearchResults, r => r is { Category: "Appearance", Label: "Style" });

        settings.Style = settings.StyleOptions[1];

        Assert.Equal(AppStyle.Claude, h.Services.Settings.Appearance.Style);
        Assert.Equal("Claude", settings.Style.Label);
        Assert.True(changed > 0);
        // The shell takes the "claude" class for the Claude apps' shapes.
        Assert.True(h.Shell.IsClaudeStyle);
    }

    [Theory]
    [InlineData(AppStyle.Standard, null, "$Default")]
    [InlineData(AppStyle.Claude, null, "Charter")]
    [InlineData(AppStyle.Claude, "Fira Sans", "Fira Sans")]
    [InlineData(AppStyle.Standard, "Fira Sans", "Fira Sans")]
    public void Replies_are_serif_in_the_Claude_style_unless_a_conversation_font_is_set(AppStyle style, string? font, string first) =>
        Assert.Equal(first, Claudette.App.Themes.AppColors.ReplyFont(style, font).FamilyNames[0]);
}
