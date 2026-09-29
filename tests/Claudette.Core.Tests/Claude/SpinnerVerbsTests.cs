using Claudette.Core.Claude;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Claude;

/// <summary>The working line's verbs, with Claude Code's <c>spinnerVerbs</c> setting (DESIGN.md §5, "Working line").</summary>
public sealed class SpinnerVerbsTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private string Project => _temp.Combine("project");

    private string Config => _temp.Combine("config");

    private IReadOnlyList<string> Resolve() => SpinnerVerbs.Resolve(SpinnerVerbs.SettingsFiles(Config, Project));

    [Fact]
    public void Without_the_setting_Claudettes_own_verbs_are_used()
    {
        Assert.Same(SpinnerVerbs.BuiltIn, Resolve());
        Assert.Contains("Noodling", SpinnerVerbs.BuiltIn);
        Assert.Equal(SpinnerVerbs.BuiltIn.Count, SpinnerVerbs.BuiltIn.Distinct().Count());
    }

    [Fact]
    public void Append_adds_the_users_verbs()
    {
        _temp.Write("config/settings.json", """{ "spinnerVerbs": { "mode": "append", "verbs": ["Rendering shaders", "Noodling", "Baking lightmaps…"] } }""");

        var verbs = Resolve();

        Assert.Equal([.. SpinnerVerbs.BuiltIn, "Rendering shaders", "Baking lightmaps"], verbs);
    }

    [Fact]
    public void Replace_shows_only_the_users_verbs_and_an_empty_list_keeps_the_built_in_ones()
    {
        _temp.Write("config/settings.json", """{ "spinnerVerbs": { "mode": "replace", "verbs": ["Compiling", "Cooking"] } }""");
        Assert.Equal(["Compiling", "Cooking"], Resolve());

        _temp.Write("config/settings.json", """{ "spinnerVerbs": { "mode": "replace", "verbs": [] } }""");
        Assert.Same(SpinnerVerbs.BuiltIn, Resolve());
    }

    [Fact]
    public void The_most_specific_settings_file_wins()
    {
        _temp.Write("config/settings.json", """{ "spinnerVerbs": { "mode": "replace", "verbs": ["User"] } }""");
        _temp.Write("project/.claude/settings.json", """{ "spinnerVerbs": { "mode": "replace", "verbs": ["Project"] } }""");
        Assert.Equal(["Project"], Resolve());

        _temp.Write("project/.claude/settings.local.json", """{ "spinnerVerbs": { "mode": "replace", "verbs": ["Local"] } }""");
        Assert.Equal(["Local"], Resolve());
    }

    [Fact]
    public void Settings_Claudette_cant_read_are_skipped()
    {
        _temp.Write("config/settings.json", """{ "spinnerVerbs": { "mode": "replace", "verbs": ["User"] } }""");
        _temp.Write("project/.claude/settings.local.json", "{ not json");
        _temp.Write("project/.claude/settings.json", """{ "spinnerVerbs": { "mode": "shuffle", "verbs": ["Project"] } }""");

        Assert.Equal(["User"], Resolve());
    }

    [Fact]
    public void Comments_trailing_commas_and_stray_values_are_tolerated()
    {
        _temp.Write("config/settings.json", """
            {
              // Mine
              "spinnerVerbs": { "verbs": ["Wobbling", 42, "", "  Wobbling  ",], },
            }
            """);

        Assert.Equal([.. SpinnerVerbs.BuiltIn, "Wobbling"], Resolve());
    }

    [Fact]
    public void Without_a_config_folder_only_the_projects_files_are_read() =>
        Assert.Equal(
            [Path.Combine("p", ".claude", "settings.local.json"), Path.Combine("p", ".claude", "settings.json")],
            SpinnerVerbs.SettingsFiles(null, "p"));

    public void Dispose() => _temp.Dispose();
}
