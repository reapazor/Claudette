using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>The mode a tab starts in, and Auto in the mode picker (DESIGN.md §7, "Starting mode").</summary>
public class PermissionModeTests
{
    [Fact]
    public async Task A_new_tab_starts_in_auto_mode_as_a_terminal_session_would()
    {
        await using var h = new TabTestHarness();

        await h.OpenTabAsync();

        Assert.Equal("auto", h.Factory.Launches[0].PermissionMode);
    }

    [Fact]
    public async Task A_mode_chosen_in_Settings_or_Tab_settings_is_used_instead()
    {
        await using var h = new TabTestHarness(s => s.NewTabs.DefaultPermissionMode = "acceptEdits");

        await h.OpenTabAsync();

        Assert.Equal("acceptEdits", h.Factory.Launches[0].PermissionMode);
    }

    [Fact]
    public async Task A_default_mode_in_Claude_Codes_own_settings_is_left_to_Claude_Code()
    {
        await using var h = new TabTestHarness();
        UseClaudeSettings(h, """{ "permissions": { "defaultMode": "plan" } }""");

        var tab = await h.OpenTabAsync();

        Assert.Null(h.Factory.Launches[0].PermissionMode);
        Assert.Equal("Default (Plan)", TabSettings(h, tab).ModeChoices[0].Label);
    }

    [Fact]
    public async Task A_resumed_tab_is_switched_to_auto_mode_once_it_has_started()
    {
        await using var h = new TabTestHarness();

        var tab = await RestoreAsync(h);

        // Without the flag, so Claude Code can bring back plan mode; it came back in Manual, so Claudette switches.
        Assert.Equal("s1", h.Factory.Launches[0].Resume);
        Assert.Null(h.Factory.Launches[0].PermissionMode);
        Assert.Equal(["auto"], ModeRequests(h));
        Assert.Equal("Auto", tab.PermissionModeName);
    }

    [Fact]
    public async Task A_resumed_tab_that_comes_back_in_plan_mode_stays_there()
    {
        await using var h = new TabTestHarness();
        var initialize = h.Transport.Answers["initialize"];
        h.Transport.Answers["initialize"] = request =>
        {
            var response = initialize(request)!;
            response["current_permission_mode"] = "plan";
            return response;
        };

        var tab = await RestoreAsync(h);

        Assert.Empty(ModeRequests(h));
        Assert.Equal("Plan", tab.PermissionModeName);
    }

    [Fact]
    public async Task Auto_is_in_the_picker_and_switches_the_session()
    {
        await using var h = new TabTestHarness(s => s.NewTabs.DefaultPermissionMode = "default");
        var tab = await h.OpenTabAsync();
        Assert.Equal("Manual", tab.PermissionModeName);
        Assert.Equal(["Manual", "Accept edits", "Plan", "Auto", "Bypass permissions"], tab.PermissionModeChoices.Select(c => c.Label));

        await tab.ChooseModeCommand.ExecuteAsync(tab.PermissionModeChoices.Single(c => c.IsAuto));

        Assert.Equal(["auto"], ModeRequests(h));
        Assert.Equal("Auto", tab.PermissionModeName);
    }

    [Fact]
    public async Task Auto_isnt_offered_for_a_model_without_it()
    {
        // Haiku: Claude Code's initialize reply leaves supportsAutoMode out.
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.Contains(tab.PermissionModeChoices, c => c.IsAuto);
        var switched = new List<string?>();
        tab.PropertyChanged += (_, e) => switched.Add(e.PropertyName);

        tab.ChooseModelCommand.Execute(tab.Models.Single(m => m.Value == "haiku"));
        await tab.ConfirmModelSwitchCommand.ExecuteAsync(null);

        Assert.DoesNotContain(tab.PermissionModeChoices, c => c.IsAuto);
        Assert.Contains(nameof(TabViewModel.PermissionModeChoices), switched);
    }

    [Fact]
    public async Task Auto_isnt_offered_or_asked_for_when_Claude_Codes_settings_turn_it_off()
    {
        await using var h = new TabTestHarness();
        UseClaudeSettings(h, """{ "disableAutoMode": "disable" }""");

        var tab = await h.OpenTabAsync();

        Assert.Null(h.Factory.Launches[0].PermissionMode);
        Assert.DoesNotContain(tab.PermissionModeChoices, c => c.IsAuto);
        Assert.Equal("Default (Manual)", TabSettings(h, tab).ModeChoices[0].Label);
    }

    [Fact]
    public async Task Tab_settings_and_Settings_name_Claude_Codes_default()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        using var settings = new SettingsViewModel(h.Services, null);

        Assert.Equal("Default (Auto)", TabSettings(h, tab).ModeChoices[0].Label);
        Assert.Equal("Claude Code's default (Auto)", settings.ModeChoices[0].Label);

        h.Services.Settings.NewTabs.DefaultPermissionMode = "acceptEdits";
        Assert.Equal("Default (Accept edits)", TabSettings(h, tab).ModeChoices[0].Label);
    }

    [Fact]
    public async Task Going_back_to_the_default_in_Tab_settings_switches_back_to_auto_mode()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var previous = tab.State.Overrides;
        tab.State.Overrides = new TabOverrides { PermissionMode = "plan" };
        await tab.ApplyOverridesAsync(previous);

        previous = tab.State.Overrides;
        tab.State.Overrides = new TabOverrides();
        await tab.ApplyOverridesAsync(previous);

        Assert.Equal(["plan", "auto"], ModeRequests(h));
        Assert.Equal("Auto", tab.PermissionModeName);
    }

    private static void UseClaudeSettings(TabTestHarness h, string json)
    {
        var config = Path.Combine(h.Root, "claude-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.json"), json);
        h.Services.ClaudeConfigDirectory = config;
    }

    private static async Task<TabViewModel> RestoreAsync(TabTestHarness h)
    {
        h.WriteTranscript("s1", Wire.Entry("user", "2026-09-27T09:30:00Z", new JsonObject { ["role"] = "user", ["content"] = "Plan the parser fix" }));
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "s1" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the restored tab to start");
        return tab;
    }

    private static TabSettingsViewModel TabSettings(TabTestHarness h, TabViewModel tab) => new(h.Services, tab, () => { });

    private static string[] ModeRequests(TabTestHarness h) =>
        h.Transport.Sent
            .Where(m => m["type"]?.GetValue<string>() == "control_request" && m["request"]!["subtype"]!.GetValue<string>() == "set_permission_mode")
            .Select(m => m["request"]!["mode"]!.GetValue<string>())
            .ToArray();
}
