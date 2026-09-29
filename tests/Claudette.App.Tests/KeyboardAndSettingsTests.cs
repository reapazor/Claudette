using Avalonia.Input;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// Settings → Keyboard, per-suffix shortcuts and the suffix menu's numbers, the Settings search box, and dragging tabs
/// (DESIGN.md §4, §5, §14).
/// </summary>
public class KeyboardAndSettingsTests
{
    private static KeyModifiers Primary => Shortcuts.IsMac ? KeyModifiers.Meta : KeyModifiers.Control;

    private static KeyChord Chord(string text) => KeyChord.TryParse(text, out var chord) ? chord : throw new ArgumentException(text);

    // ---- Matching key presses -------------------------------------------------------------------------------------

    [Fact]
    public void Key_presses_match_the_bound_shortcuts()
    {
        var keyboard = new KeyboardSettings();

        Assert.True(Shortcuts.Matches(keyboard, KeyboardShortcuts.NewTab, Key.T, Primary));
        Assert.False(Shortcuts.Matches(keyboard, KeyboardShortcuts.NewTab, Key.T, Primary | KeyModifiers.Shift));
        Assert.True(Shortcuts.Matches(keyboard, KeyboardShortcuts.History, Key.H, Primary | KeyModifiers.Shift));
        Assert.True(Shortcuts.Matches(keyboard, KeyboardShortcuts.NextTab, Key.Tab, KeyModifiers.Control));
        Assert.True(Shortcuts.Matches(keyboard, KeyboardShortcuts.GoToTab, Key.D7, Primary));
        Assert.True(Shortcuts.Matches(keyboard, KeyboardShortcuts.GoToTab, Key.NumPad3, Primary));
        Assert.False(Shortcuts.Matches(keyboard, KeyboardShortcuts.GoToTab, Key.D0, Primary));
        Assert.True(Shortcuts.Matches(keyboard, KeyboardShortcuts.AllowPrompt, Key.Enter, Primary));
        Assert.True(Shortcuts.Matches(keyboard, KeyboardShortcuts.Stop, Key.Escape, KeyModifiers.None));

        keyboard.Bindings[KeyboardShortcuts.NewTab] = "Primary+Shift+N";
        Assert.False(Shortcuts.Matches(keyboard, KeyboardShortcuts.NewTab, Key.T, Primary));
        Assert.True(Shortcuts.Matches(keyboard, KeyboardShortcuts.NewTab, Key.N, Primary | KeyModifiers.Shift));
    }

    [Fact]
    public void Key_presses_become_chords_and_modifiers_alone_do_not()
    {
        Assert.Null(Shortcuts.FromKeyPress(Key.LeftShift, KeyModifiers.Shift));
        Assert.Equal(Chord("Primary+Alt+Enter"), Shortcuts.FromKeyPress(Key.Return, Primary | KeyModifiers.Alt));
        Assert.Equal(Chord("Primary+D4"), Shortcuts.FromKeyPress(Key.NumPad4, Primary));
    }

    // ---- Settings → Keyboard --------------------------------------------------------------------------------------

    [Fact]
    public async Task Recording_a_shortcut_rebinds_the_command_and_its_tooltip()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        var newTab = settings.ShortcutRows.Single(r => r.Command.Id == KeyboardShortcuts.NewTab);

        settings.StartRecordingCommand.Execute(newTab);
        Assert.True(settings.IsRecordingShortcut);
        Assert.Equal("Press keys…", newTab.ShortcutText);

        settings.RecordShortcut(Chord("Primary+Shift+N"));

        Assert.False(settings.IsRecordingShortcut);
        Assert.Equal("Primary+Shift+N", h.Services.Settings.Keyboard.Bindings[KeyboardShortcuts.NewTab]);
        Assert.True(newTab.IsCustomized);
        Assert.Equal($"New tab ({Chord("Primary+Shift+N").Display(Shortcuts.IsMac)})", h.Services.Tips.NewTab);

        settings.ResetShortcutCommand.Execute(newTab);
        Assert.False(newTab.IsCustomized);
        Assert.Empty(h.Services.Settings.Keyboard.Bindings);
    }

    [Fact]
    public async Task A_shortcut_already_in_use_is_refused()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        var newTab = settings.ShortcutRows.Single(r => r.Command.Id == KeyboardShortcuts.NewTab);

        settings.StartRecordingCommand.Execute(newTab);
        settings.RecordShortcut(Chord("Primary+W"));

        Assert.True(settings.IsRecordingShortcut);
        Assert.Equal($"{Chord("Primary+W").Display(Shortcuts.IsMac)} is already used by Close tab.", newTab.Error);
        Assert.Empty(h.Services.Settings.Keyboard.Bindings);
    }

    [Fact]
    public async Task A_shortcut_that_would_stop_typing_is_refused()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        var history = settings.ShortcutRows.Single(r => r.Command.Id == KeyboardShortcuts.History);

        settings.StartRecordingCommand.Execute(history);
        settings.RecordShortcut(Chord("Shift+H"));
        Assert.Equal("Add Ctrl, Alt or Cmd, so typing still works.", history.Error);

        settings.RecordShortcut(Chord("F2"));
        Assert.Equal("F2", h.Services.Settings.Keyboard.Bindings[KeyboardShortcuts.History]);
    }

    [Fact]
    public async Task Go_to_tab_takes_a_number_key_for_all_nine()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        var goTo = settings.ShortcutRows.Single(r => r.Command.Id == KeyboardShortcuts.GoToTab);

        settings.StartRecordingCommand.Execute(goTo);
        settings.RecordShortcut(Chord("Alt+G"));
        Assert.Contains("number key", goTo.Error);

        settings.RecordShortcut(Chord("Alt+D5"));
        Assert.Equal("Alt+D1", h.Services.Settings.Keyboard.Bindings[KeyboardShortcuts.GoToTab]);
        Assert.Equal("Alt+1…9", goTo.ShortcutText.Replace("⌥", "Alt+", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_and_resetting_all_shortcuts()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        var stop = settings.ShortcutRows.Single(r => r.Command.Id == KeyboardShortcuts.Stop);

        settings.ClearShortcutCommand.Execute(stop);
        Assert.Equal("None", stop.ShortcutText);
        Assert.False(Shortcuts.Matches(h.Services.Settings.Keyboard, KeyboardShortcuts.Stop, Key.Escape, KeyModifiers.None));

        settings.ResetKeyboardCommand.Execute(null);
        Assert.True(Shortcuts.Matches(h.Services.Settings.Keyboard, KeyboardShortcuts.Stop, Key.Escape, KeyModifiers.None));
    }

    // ---- Quick suffixes (DESIGN.md §5) ----------------------------------------------------------------------------

    [Fact]
    public async Task A_suffix_gets_its_own_shortcut_unless_a_command_has_it()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        var clarify = settings.Suffixes.First();

        settings.StartRecordingCommand.Execute(clarify);
        settings.RecordShortcut(Chord("Primary+Shift+H"));
        Assert.Contains("History", clarify.Error);

        settings.RecordShortcut(Chord("Primary+Alt+C"));
        Assert.Equal("Primary+Alt+C", clarify.Suffix.Shortcut);
        Assert.True(clarify.HasShortcut);

        settings.ClearShortcutCommand.Execute(clarify);
        Assert.Null(clarify.Suffix.Shortcut);
    }

    [Fact]
    public async Task The_suffix_menu_numbers_the_first_nine_and_picks_by_number()
    {
        await using var h = new TabTestHarness();
        h.Services.Settings.QuickSuffixes[1].Shortcut = "Primary+Alt+P";
        var tab = await h.OpenTabAsync();

        var menu = tab.SuffixMenu;
        Assert.Equal([1, 2, 3, 4, 5], menu.Select(m => m.Number));
        Assert.Equal(Chord("Primary+Alt+P").Display(Shortcuts.IsMac), menu[1].Shortcut);

        Assert.True(tab.PickSuffix(2));
        Assert.False(tab.PickSuffix(9));

        Assert.Equal("Plan only", Assert.Single(tab.Chips).Suffix.Label);
    }

    // ---- Settings search (DESIGN.md §14) -------------------------------------------------------------------------

    [Fact]
    public async Task Searching_settings_finds_them_by_name_and_opens_their_category()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);

        settings.SearchText = "font";

        Assert.True(settings.IsSearching);
        Assert.Equal(["Conversation font", "Conversation font size", "Code font", "Code font size"], settings.SearchResults.Select(r => r.Label));
        Assert.Equal("Appearance", settings.SelectedCategory);

        settings.SearchText = "badge";
        settings.SelectedSearchResult = settings.SearchResults.Single();
        Assert.Equal("Notifications", settings.SelectedCategory);

        settings.SearchText = "usage history";
        Assert.All(settings.SearchResults, r => Assert.Equal("Usage", r.Category));

        settings.SearchText = "sync new tabs";
        Assert.Equal(new SettingsSearchResult("Sessions", "Sync new tabs to the session library"), Assert.Single(settings.SearchResults));

        settings.SearchText = "xyzzy";
        Assert.Empty(settings.SearchResults);

        settings.SearchText = "";
        Assert.False(settings.IsSearching);
    }

    // ---- Dragging tabs and groups (DESIGN.md §4) ------------------------------------------------------------------

    [Fact]
    public async Task Tabs_move_within_their_group_and_pinned_tabs_stay_first()
    {
        await using var h = new TabTestHarness();
        // Restored tabs don't start until selected, so only the first one talks to the scripted Claude Code.
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = true;
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true }, new TabState { Folder = h.WorkFolder }, new TabState { Folder = h.WorkFolder }];
        h.Shell.Restore(null);
        var group = h.Shell.Groups.Single();
        var (a, b, c) = (group.Tabs[0], group.Tabs[1], group.Tabs[2]);

        Assert.True(h.Shell.MoveTabTo(c, 1));
        Assert.Equal([a, c, b], group.Tabs);

        // An unpinned tab can't move ahead of a pinned one, nor a pinned one behind the unpinned.
        h.Shell.MoveTabTo(b, 0);
        Assert.Equal(a, group.Tabs[0]);
        Assert.False(h.Shell.MoveTabTo(a, 2));
        Assert.Equal([a.Id, b.Id, c.Id], h.Services.State.Tabs.Select(t => t.Id));
    }

    [Fact]
    public async Task Groups_move_and_keep_their_order()
    {
        await using var h = new TabTestHarness();
        var other = Path.Combine(h.Root, "other");
        Directory.CreateDirectory(other);
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true }, new TabState { Folder = other, IsPinned = true }];
        h.Shell.Restore(null);
        var (first, second) = (h.Shell.Groups[0], h.Shell.Groups[1]);

        Assert.True(h.Shell.MoveGroupTo(second, 0));

        Assert.Equal([second, first], h.Shell.Groups);
        Assert.Equal(other, h.Services.State.Tabs[0].Folder);
    }

    // ---- Settings sync (DESIGN.md §14) ----------------------------------------------------------------------------

    [Fact]
    public async Task Shortcuts_and_notification_settings_sync_between_machines()
    {
        await using var first = new TabTestHarness();
        await using var second = new TabTestHarness();
        var library = Path.Combine(first.Root, "shared-library");

        // This machine turns sync on with its own shortcut and notification choices: they're published.
        first.Services.Settings.Keyboard.Bindings[KeyboardShortcuts.NewTab] = "Primary+Shift+N";
        first.Services.Settings.Notifications.TurnFinished = false;
        first.Services.Settings.Sessions.LibraryFolder = library;
        first.Services.Settings.Sessions.SyncSettings = true;
        first.Services.Library.OnSettingsChanged();
        var syncFile = Path.Combine(library, "settings-sync.json");
        await TabTestHarness.Eventually(() => File.Exists(syncFile) && File.ReadAllText(syncFile).Contains("keyboard.bindings", StringComparison.Ordinal), "the published settings");

        // Another machine joins the same library and takes them.
        second.Services.Settings.Sessions.LibraryFolder = library;
        second.Services.Settings.Sessions.SyncSettings = true;
        second.Services.Library.OnSettingsChanged();
        await TabTestHarness.Eventually(() => second.Services.Settings.Keyboard.Bindings.ContainsKey(KeyboardShortcuts.NewTab), "the synced shortcut");

        Assert.Equal("Primary+Shift+N", second.Services.Settings.Keyboard.Bindings[KeyboardShortcuts.NewTab]);
        Assert.False(second.Services.Settings.Notifications.TurnFinished);
    }
}
