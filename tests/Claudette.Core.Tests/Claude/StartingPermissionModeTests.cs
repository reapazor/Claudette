using Claudette.Core.Claude;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Claude;

/// <summary>The mode a tab starts in when Claudette doesn't choose one (DESIGN.md §7, "Starting mode").</summary>
public sealed class StartingPermissionModeTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private StartingPermissionMode Read() =>
        StartingPermissionMode.Read(StartingPermissionMode.SettingsFiles(_temp.Combine("managed"), _temp.Combine("config"), _temp.Combine("project")));

    private void Set(string file, string mode) => _temp.Write(file, $$"""{ "permissions": { "defaultMode": "{{mode}}" } }""");

    [Fact]
    public void Without_settings_a_tab_starts_in_auto_mode_as_a_terminal_session_would()
    {
        var starting = Read();

        Assert.Null(starting.DefaultMode);
        Assert.False(starting.AutoModeDisabled);
        Assert.Equal("auto", starting.LaunchMode);
        Assert.Equal("auto", starting.Expected);
    }

    [Fact]
    public void A_default_mode_in_the_users_settings_is_left_to_Claude_Code()
    {
        Set("config/settings.json", "acceptEdits");

        var starting = Read();

        Assert.Null(starting.LaunchMode);
        Assert.Equal("acceptEdits", starting.Expected);
    }

    [Fact]
    public void A_settings_file_with_a_repeated_key_is_read_as_Claude_Code_reads_it()
    {
        // Claude Code's JSON.parse keeps the last value; .NET's JsonNode would throw on the first read.
        _temp.Write("config/settings.json", """{ "permissions": { "defaultMode": "plan" }, "permissions": { "defaultMode": "acceptEdits", "defaultMode": "auto" } }""");

        var starting = Read();

        Assert.Equal("auto", starting.Expected);
        Assert.Equal("auto", starting.LaunchMode);
    }

    [Fact]
    public void Manual_is_the_default_modes_other_name()
    {
        Set("config/settings.json", "manual");

        Assert.Equal("default", Read().Expected);
        Assert.Null(Read().LaunchMode);
    }

    [Fact]
    public void Auto_from_the_users_settings_is_passed_on()
    {
        Set("config/settings.json", "auto");

        Assert.Equal("auto", Read().LaunchMode);
    }

    [Fact]
    public void The_file_that_wins_is_managed_then_local_then_project_then_user()
    {
        Set("config/settings.json", "acceptEdits");
        Set("project/.claude/settings.json", "plan");
        Assert.Equal("plan", Read().Expected);

        Set("project/.claude/settings.local.json", "dontAsk");
        Assert.Equal("dontAsk", Read().Expected);

        Set("managed/managed-settings.json", "default");
        Assert.Equal("default", Read().Expected);
    }

    [Fact]
    public void Managed_drop_ins_apply_after_managed_settings_in_name_order()
    {
        Set("managed/managed-settings.json", "default");
        Set("managed/managed-settings.d/10-first.json", "acceptEdits");
        Set("managed/managed-settings.d/20-second.json", "plan");
        Set("managed/managed-settings.d/.hidden.json", "dontAsk");
        _temp.Write("managed/managed-settings.d/30-notes.txt", """{ "permissions": { "defaultMode": "dontAsk" } }""");

        Assert.Equal("plan", Read().Expected);
    }

    [Fact]
    public void Auto_in_a_projects_settings_doesnt_take_effect_and_the_built_in_default_applies()
    {
        // Claude Code doesn't take auto from a project's files, nor fall back to the user's setting.
        Set("project/.claude/settings.json", "auto");
        Set("config/settings.json", "acceptEdits");

        var starting = Read();

        Assert.Null(starting.DefaultMode);
        Assert.Equal("auto", starting.LaunchMode);
    }

    [Fact]
    public void Bypass_in_a_projects_settings_starts_in_manual()
    {
        Set("project/.claude/settings.local.json", "bypassPermissions");

        var starting = Read();

        Assert.Null(starting.LaunchMode);
        Assert.Equal("default", starting.Expected);
    }

    [Fact]
    public void Disable_auto_mode_in_any_file_keeps_tabs_out_of_auto_mode()
    {
        _temp.Write("managed/managed-settings.json", """{ "disableAutoMode": "disable" }""");

        var starting = Read();

        Assert.True(starting.AutoModeDisabled);
        Assert.Null(starting.LaunchMode);
        Assert.Equal("default", starting.Expected);

        // Under permissions too, and a default mode other than auto still applies.
        _temp.Write("managed/managed-settings.json", "{}");
        _temp.Write("config/settings.json", """{ "permissions": { "defaultMode": "acceptEdits", "disableAutoMode": "disable" } }""");
        Assert.True(Read().AutoModeDisabled);
        Assert.Equal("acceptEdits", Read().Expected);
    }

    [Fact]
    public void Settings_Claudette_cant_read_are_skipped()
    {
        _temp.Write("project/.claude/settings.local.json", "{ not json");
        _temp.Write("project/.claude/settings.json", """{ "permissions": { "defaultMode": 3, "disableAutoMode": true } }""");
        _temp.Write("config/settings.json", """
            {
              // Mine
              "permissions": { "defaultMode": "plan", },
            }
            """);

        var starting = Read();

        Assert.Equal("plan", starting.Expected);
        Assert.False(starting.AutoModeDisabled);
    }

    [Fact]
    public void Without_folders_only_the_files_given_are_read()
    {
        Assert.Empty(StartingPermissionMode.SettingsFiles(null, null, null));
        Assert.Equal(
            [new ClaudeSettingsFile(Path.Combine("c", "settings.json"), SettingsScope.User)],
            StartingPermissionMode.SettingsFiles(null, "c", null));
    }

    public void Dispose() => _temp.Dispose();
}
