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
        s.Appearance.FullWidthConversation = true;
        s.DiffTool.Kind = "custom";
        s.DiffTool.CustomCommand = "meld {left} {right}";
        s.Advanced.ExtraArguments = "--verbose";
        s.Advanced.LogProtocol = true;
        var settings = new SettingsViewModel(h.Services, null);

        settings.Sessions.ResetCommand.Execute(null);
        settings.ClaudeCode.ResetCommand.Execute(null);
        settings.NewTabs.ResetCommand.Execute(null);
        settings.Appearance.ResetCommand.Execute(null);
        settings.DiffTool.ResetCommand.Execute(null);
        settings.Advanced.ResetCommand.Execute(null);

        Assert.False(s.Sessions.RestoreUnpinnedTabs);
        Assert.Null(s.Sessions.MachineName);
        Assert.False(s.Sessions.SyncNewTabs);
        Assert.False(settings.Sessions.SyncNewTabs);
        // Where the library lives is its own decision, not a reset.
        Assert.Equal(Path.Combine(h.Root, "library"), s.Sessions.LibraryFolder);
        Assert.True(s.ClaudeCode.CheckForUpdates);
        Assert.Null(s.ClaudeCode.ClaudePath);
        Assert.Null(s.NewTabs.DefaultModel);
        Assert.Equal(20, s.NewTabs.RecentFolderLimit);
        Assert.Equal(ThemeChoice.System, s.Appearance.Theme);
        Assert.Equal(AppStyle.Standard, s.Appearance.Style);
        Assert.Equal("Standard", settings.Appearance.Style.Label);
        Assert.Null(s.Appearance.CodeFont);
        Assert.True(s.Appearance.ShowContextOnTabs);
        Assert.True(settings.Appearance.ShowContextOnTabs);
        Assert.Equal(Density.Comfortable, s.Appearance.Density);
        Assert.Equal(Density.Comfortable, settings.Appearance.Density);
        Assert.False(s.Appearance.FullWidthConversation);
        Assert.Equal("builtIn", s.DiffTool.Kind);
        Assert.Equal("", s.Advanced.ExtraArguments);
        Assert.False(s.Advanced.LogProtocol);
        Assert.Equal("", settings.Appearance.CodeFont);
    }

    [Fact]
    public async Task The_fallback_model_checkpoints_and_hook_rows_are_settings_with_defaults()
    {
        await using var h = new TabTestHarness();
        using var settings = new SettingsViewModel(h.Services, null);
        Assert.Equal("None", settings.ClaudeCode.FallbackModel.Label);
        Assert.True(settings.ClaudeCode.KeepFileCheckpoints);
        Assert.False(settings.ClaudeCode.ShowAllHookRuns);

        settings.ClaudeCode.FallbackModel = settings.ClaudeCode.FallbackModelChoices.Single(c => c.Value == "sonnet");
        settings.ClaudeCode.KeepFileCheckpoints = false;
        settings.ClaudeCode.ShowAllHookRuns = true;

        Assert.Equal(("sonnet", false, true), (h.Services.Settings.ClaudeCode.FallbackModel, h.Services.Settings.ClaudeCode.KeepFileCheckpoints, h.Services.Settings.ClaudeCode.ShowAllHookRuns));
        settings.ClaudeCode.ResetCommand.Execute(null);
        Assert.Equal((null, true, false), (h.Services.Settings.ClaudeCode.FallbackModel, h.Services.Settings.ClaudeCode.KeepFileCheckpoints, h.Services.Settings.ClaudeCode.ShowAllHookRuns));
    }

    [Fact]
    public async Task Density_applies_at_once()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        Assert.Equal(Density.Comfortable, settings.Appearance.Density);
        Assert.Equal([Density.Comfortable, Density.Compact], settings.Appearance.Densities);
        Assert.False(h.Shell.IsCompact);

        settings.Appearance.Density = Density.Compact;

        Assert.Equal(Density.Compact, h.Services.Settings.Appearance.Density);
        Assert.True(h.Shell.IsCompact);

        settings.Appearance.ResetCommand.Execute(null);

        Assert.False(h.Shell.IsCompact);
    }

    [Fact]
    public async Task Full_width_conversation_is_off_by_default_and_applies_at_once()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null) { SearchText = "full-width" };
        Assert.False(settings.Appearance.FullWidthConversation);
        Assert.False(h.Shell.IsFullWidth);
        Assert.Contains(settings.SearchResults, r => r is { Category: "Appearance", Label: "Full-width conversation" });

        settings.Appearance.FullWidthConversation = true;

        Assert.True(h.Services.Settings.Appearance.FullWidthConversation);
        // The shell takes the "fullwidth" class, whose styles lift the conversation's and the composer's widest.
        Assert.True(h.Shell.IsFullWidth);

        settings.Appearance.ResetCommand.Execute(null);

        Assert.False(h.Shell.IsFullWidth);
    }

    [Fact]
    public async Task Favorites_can_be_added_reordered_and_removed()
    {
        await using var h = new TabTestHarness();
        var a = Directory.CreateDirectory(Path.Combine(h.Root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(h.Root, "b")).FullName;
        var settings = new SettingsViewModel(h.Services, null);
        Assert.False(settings.NewTabs.HasFavorites);

        h.Platform.FolderToPick = a;
        await settings.NewTabs.AddFavoriteCommand.ExecuteAsync(null);
        h.Platform.FolderToPick = b;
        await settings.NewTabs.AddFavoriteCommand.ExecuteAsync(null);
        Assert.Equal(["a", "b"], settings.NewTabs.Favorites.Select(f => f.Name));

        settings.NewTabs.MoveFavoriteUpCommand.Execute(settings.NewTabs.Favorites[1]);
        Assert.Equal(["b", "a"], settings.NewTabs.Favorites.Select(f => f.Name));
        Assert.Equal(["b", "a"], h.Services.State.FavoriteFolders.Select(Path.GetFileName));
        // The ends don't move further.
        settings.NewTabs.MoveFavoriteUpCommand.Execute(settings.NewTabs.Favorites[0]);
        Assert.Equal(["b", "a"], settings.NewTabs.Favorites.Select(f => f.Name));

        settings.NewTabs.RemoveFavoriteCommand.Execute(settings.NewTabs.Favorites[0]);
        Assert.Equal(["a"], settings.NewTabs.Favorites.Select(f => f.Name));
        Assert.False(FolderHistory.IsFavorite(h.Services.State, b));
    }

    [Fact]
    public async Task Fonts_are_saved_and_empty_means_the_default()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        settings.Appearance.ConversationFont = "  Source Serif 4 ";
        settings.Appearance.CodeFont = "JetBrains Mono";

        Assert.Equal("Source Serif 4", h.Services.Settings.Appearance.ConversationFont);
        Assert.Equal("JetBrains Mono", h.Services.Settings.Appearance.CodeFont);
        settings.Appearance.ConversationFont = "";
        Assert.Null(h.Services.Settings.Appearance.ConversationFont);
        Assert.Single(settings.SearchResultsFor("code font"), r => r.Label == "Code font");
    }

    [Fact]
    public async Task Model_and_effort_lists_come_from_what_Claude_Code_offered()
    {
        await using var h = new TabTestHarness();
        var before = new SettingsViewModel(h.Services, null);
        // Nothing offered yet on this machine: the built-in list.
        Assert.Contains(before.NewTabs.ModelChoices, c => c.Value == "sonnet");

        var tab = await h.OpenTabAsync();
        var settings = new SettingsViewModel(h.Services, null);

        Assert.Equal([null, "opus", "haiku"], settings.NewTabs.ModelChoices.Select(c => c.Value));
        Assert.Equal([null, "low", "high"], settings.NewTabs.EffortChoices.Select(c => c.Value));
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

        Assert.Equal(["Standard", "Claude"], settings.Appearance.StyleOptions.Select(o => o.ToString()));
        Assert.Equal(AppStyle.Standard, settings.Appearance.Style.Style);
        Assert.False(h.Shell.IsClaudeStyle);
        Assert.Contains(settings.SearchResults, r => r is { Category: "Appearance", Label: "Style" });

        settings.Appearance.Style = settings.Appearance.StyleOptions[1];

        Assert.Equal(AppStyle.Claude, h.Services.Settings.Appearance.Style);
        Assert.Equal("Claude", settings.Appearance.Style.Label);
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
